using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Dimly
{
    internal static class Program
    {
        /// <summary>Broadcast by a second copy of Dimly to bring the first one's window forward.</summary>
        internal static int SecondInstanceMessage;

        private static TrayApp _app;

        [STAThread]
        private static void Main(string[] args)
        {
            bool startHidden = false;
            foreach (string argument in args)
                if (argument == "--tray" || argument == "/tray") startHidden = true;

            SecondInstanceMessage = Native.RegisterWindowMessage("DimlyOpenSettings");

            // One instance per signed-in session: two copies would fight over brightness.
            bool owned;
            using (Mutex instance = new Mutex(true, @"Local\Dimly.SingleInstance", out owned))
            {
                if (!owned)
                {
                    SignalRunningCopy();
                    return;
                }

                Run(startHidden);
                GC.KeepAlive(instance);
            }
        }

        /// <summary>
        /// Asks the copy already running to show its window. Its message window is found by
        /// name and posted to directly: a broadcast reaches only windows Windows chooses to
        /// deliver to, and this one - never shown, and off-screen - is not among them.
        /// </summary>
        private static void SignalRunningCopy()
        {
            if (SecondInstanceMessage == 0) return;

            IntPtr window = Native.FindWindow(null, TrayApp.MessageWindowTitle);
            if (window != IntPtr.Zero)
                Native.PostMessage(window, SecondInstanceMessage, IntPtr.Zero, IntPtr.Zero);
        }

        private static void Run(bool startHidden)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainException;

            Ui.Initialize(DesktopScale(), 880, Screen.PrimaryScreen.WorkingArea.Height);

            AppSettings settings = AppSettings.Load();
            Theme.Current = Theme.Find(settings.ThemeId);

            _app = new TrayApp(settings, startHidden || settings.StartHidden);
            try
            {
                Application.Run(_app);
            }
            finally
            {
                _app.Dispose();
            }
        }

        private static float DesktopScale()
        {
            using (Graphics screen = Graphics.FromHwnd(IntPtr.Zero))
                return screen.DpiX / 96f;
        }

        // A crash must never leave the screens dark: restore before anything else.

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Rescue(e.Exception);
        }

        private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            Rescue(e.ExceptionObject as Exception);
        }

        private static void Rescue(Exception error)
        {
            if (_app != null) _app.PanicRestore();

            string log = WriteCrashLog(error);
            MessageBox.Show(
                AppInfo.Name + " hit an unexpected problem and will close."
                + Environment.NewLine + Environment.NewLine
                + "Your displays have been restored."
                + Environment.NewLine + Environment.NewLine
                + (error != null ? error.Message : "Unknown error.")
                + (log == null ? string.Empty : Environment.NewLine + Environment.NewLine
                    + "Details were written to:" + Environment.NewLine + log),
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);

            Environment.Exit(1);
        }

        /// <summary>
        /// Leaves the stack trace somewhere the user can find it. A tray application has no
        /// console to print to, and "it just vanished" is not a bug report anyone can act on.
        /// </summary>
        private static string WriteCrashLog(Exception error)
        {
            try
            {
                string folder = Path.GetDirectoryName(AppSettings.FilePath);
                string path = Path.Combine(folder, "crash.txt");
                Directory.CreateDirectory(folder);
                File.WriteAllText(path,
                    DateTime.Now.ToString("u") + "  " + AppInfo.Name + " " + AppInfo.Version
                    + Environment.NewLine + Environment.NewLine
                    + (error != null ? error.ToString() : "Unknown error."));
                return path;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
