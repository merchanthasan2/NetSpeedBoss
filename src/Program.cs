using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace NetSpeedBoss
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool restarted = Array.IndexOf(args, "--restarted") >= 0;
            bool created;
            Mutex mutex = new Mutex(true, "NetSpeedBoss_SingleInstance_7F3A", out created);
            if (!created)
            {
                bool acquired = false;
                if (restarted)   // previous (non-elevated) instance is shutting down
                {
                    try { acquired = mutex.WaitOne(10000); }
                    catch (AbandonedMutexException) { acquired = true; }
                }
                if (!acquired)
                {
                    try { EventWaitHandle.OpenExisting("NetSpeedBoss_ShowDashboard").Set(); } catch { }
                    return;
                }
            }

            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log(e.ExceptionObject as Exception); };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp(args));
            GC.KeepAlive(mutex);
        }

        static void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(Path.Combine(Settings.Dir, "error.log"), DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
