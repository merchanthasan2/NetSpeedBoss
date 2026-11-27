using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

namespace NetSpeedBoss
{
    public enum SpeedTestPhase { Idle, Ping, Download, Upload, Done, Cancelled, Error }

    public class SpeedTestState
    {
        public SpeedTestPhase Phase = SpeedTestPhase.Idle;
        public int ProgressPct;
        public double PingMs = -1, JitterMs = -1;
        public double DownloadBps, UploadBps;   // bytes per second
        public string Error = "";
        public bool Running { get { return Phase == SpeedTestPhase.Ping || Phase == SpeedTestPhase.Download || Phase == SpeedTestPhase.Upload; } }
    }

    /// <summary>
    /// Multi-stream HTTPS speed test against Cloudflare edge endpoints.
    /// Uses 6 parallel download streams and 4 parallel upload streams so dual-WAN /
    /// load-balanced connections distribute flows across all active internet links.
    /// </summary>
    public class SpeedTester
    {
        const string DownUrl = "https://speed.cloudflare.com/__down?bytes=25000000";
        const string PingUrl = "https://speed.cloudflare.com/__down?bytes=0";
        const string UpUrl = "https://speed.cloudflare.com/__up";

        readonly object gate = new object();
        readonly List<HttpWebRequest> activeReqs = new List<HttpWebRequest>();
        volatile bool cancelled;
        public readonly SpeedTestState State = new SpeedTestState();

        public bool Start(Action<SpeedTestState> onProgress)
        {
            if (State.Running) return false;
            cancelled = false;
            State.Phase = SpeedTestPhase.Ping;
            State.ProgressPct = 0;
            State.PingMs = -1; State.JitterMs = -1;
            State.DownloadBps = 0; State.UploadBps = 0;
            State.Error = "";

            ThreadPool.QueueUserWorkItem(delegate { Run(onProgress); });
            return true;
        }

        public void Cancel()
        {
            cancelled = true;
            lock (gate)
            {
                foreach (HttpWebRequest req in activeReqs)
                    try { req.Abort(); } catch { }
                activeReqs.Clear();
            }
        }

        void Track(HttpWebRequest req) { lock (gate) activeReqs.Add(req); }
        void Untrack(HttpWebRequest req) { lock (gate) activeReqs.Remove(req); }

        void Emit(Action<SpeedTestState> cb)
        {
            if (cb != null) cb(State);
        }

        void Run(Action<SpeedTestState> cb)
        {
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                if (ServicePointManager.DefaultConnectionLimit < 16) ServicePointManager.DefaultConnectionLimit = 16;

                // 1. Ping & jitter (5 samples, discard first TLS handshake)
                Emit(cb);
                List<double> rtts = new List<double>();
                for (int i = 0; i < 6 && !cancelled; i++)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(PingUrl);
                    req.Timeout = 4000; req.ReadWriteTimeout = 4000; req.UserAgent = "NetSpeedBoss";
                    Track(req);
                    try
                    {
                        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                        using (Stream s = resp.GetResponseStream())
                            while (s.ReadByte() >= 0) { }
                        sw.Stop();
                        if (i > 0) rtts.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    finally { Untrack(req); }
                    State.ProgressPct = (i + 1) * 10 / 6;
                    if (rtts.Count > 0)
                    {
                        rtts.Sort();
                        State.PingMs = rtts[rtts.Count / 2];
                    }
                    Emit(cb);
                }
                if (cancelled) { State.Phase = SpeedTestPhase.Cancelled; Emit(cb); return; }
                if (rtts.Count >= 2)
                {
                    double jit = 0;
                    for (int i = 1; i < rtts.Count; i++) jit += Math.Abs(rtts[i] - rtts[i - 1]);
                    State.JitterMs = jit / (rtts.Count - 1);
                }

                // 2. Multi-stream Download (6 streams, 6.5s max, 1.0s warmup)
                State.Phase = SpeedTestPhase.Download;
                State.DownloadBps = MeasureTransfer(true, 6, 6500, 1000, 10, 55, cb);
                if (cancelled) { State.Phase = SpeedTestPhase.Cancelled; Emit(cb); return; }

                // 3. Multi-stream Upload (4 streams, 5.5s max, 0.9s warmup)
                State.Phase = SpeedTestPhase.Upload;
                State.UploadBps = MeasureTransfer(false, 4, 5500, 900, 55, 100, cb);
                if (cancelled) { State.Phase = SpeedTestPhase.Cancelled; Emit(cb); return; }

                State.ProgressPct = 100;
                State.Phase = SpeedTestPhase.Done;
                Emit(cb);
            }
            catch (Exception ex)
            {
                State.Phase = cancelled ? SpeedTestPhase.Cancelled : SpeedTestPhase.Error;
                State.Error = cancelled ? "" : ex.Message;
                Emit(cb);
            }
        }

        double MeasureTransfer(bool download, int streams, int durationMs, int warmupMs, int pctStart, int pctEnd, Action<SpeedTestState> cb)
        {
            long measuredBytes = 0;
            bool warmupDone = false;
            Stopwatch sw = Stopwatch.StartNew();
            ManualResetEvent doneEvt = new ManualResetEvent(false);
            int alive = streams;
            byte[] upPayload = download ? null : new byte[1024 * 1024]; // 1 MB chunk per POST

            for (int i = 0; i < streams; i++)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    byte[] buf = download ? new byte[65536] : null;
                    try
                    {
                        while (!cancelled && sw.ElapsedMilliseconds < durationMs)
                        {
                            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(download ? DownUrl : UpUrl);
                            req.Timeout = 5000; req.ReadWriteTimeout = 5000; req.UserAgent = "NetSpeedBoss";
                            Track(req);
                            try
                            {
                                if (download)
                                {
                                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                                    using (Stream s = resp.GetResponseStream())
                                    {
                                        int n;
                                        while (!cancelled && sw.ElapsedMilliseconds < durationMs && (n = s.Read(buf, 0, buf.Length)) > 0)
                                        {
                                            if (sw.ElapsedMilliseconds >= warmupMs) Interlocked.Add(ref measuredBytes, n);
                                        }
                                    }
                                }
                                else
                                {
                                    req.Method = "POST";
                                    req.ContentType = "application/octet-stream";
                                    req.ContentLength = upPayload.Length;
                                    using (Stream rs = req.GetRequestStream())
                                    {
                                        int off = 0;
                                        while (!cancelled && off < upPayload.Length && sw.ElapsedMilliseconds < durationMs)
                                        {
                                            int chunk = Math.Min(65536, upPayload.Length - off);
                                            rs.Write(upPayload, off, chunk);
                                            off += chunk;
                                            if (sw.ElapsedMilliseconds >= warmupMs) Interlocked.Add(ref measuredBytes, chunk);
                                        }
                                    }
                                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse()) { }
                                }
                            }
                            catch { if (cancelled) break; Thread.Sleep(50); }
                            finally { Untrack(req); }
                        }
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref alive) == 0) doneEvt.Set();
                    }
                });
            }

            while (!doneEvt.WaitOne(180))
            {
                if (cancelled) break;
                long ms = sw.ElapsedMilliseconds;
                if (ms >= warmupMs)
                {
                    warmupDone = true;
                    double sec = Math.Max(0.2, (ms - warmupMs) / 1000.0);
                    double bps = Interlocked.Read(ref measuredBytes) / sec;
                    if (download) State.DownloadBps = bps; else State.UploadBps = bps;
                }
                State.ProgressPct = pctStart + (int)(Math.Min(1.0, ms / (double)durationMs) * (pctEnd - pctStart));
                Emit(cb);
            }

            double finalSec = Math.Max(0.2, (Math.Min(sw.ElapsedMilliseconds, durationMs) - (warmupDone ? warmupMs : 0)) / 1000.0);
            return Interlocked.Read(ref measuredBytes) / finalSec;
        }
    }
}
