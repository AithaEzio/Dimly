// A diagnostic recorder for everything that happens around your screens: when Windows switches
// them off, when it says they are back, what each monitor actually reports through it all, and
// what Dimly does about it.
//
// It is a witness, not a participant. It never changes a brightness, never touches Dimly, and
// never produces input - so leaving the machine alone with this running is the same as leaving
// it alone.
//
// The one thing it does do is ask each monitor how bright it is, twice, every few seconds:
//
//   held  - through a handle taken once at the start and never taken again. This is the handle
//           Dimly would have been holding when the screen went dark, and the whole stuck-dim
//           bug turns on the fact that such a handle keeps answering after the monitor has
//           stopped listening. If "held" reports a brightness the screen plainly is not at,
//           that is the fault, caught in the act.
//   fresh - through a handle taken for that reading alone. This is what the monitor will say
//           to somebody who has just arrived, and it is how you tell a monitor that is awake
//           from one that is merely powered.
//
// Built and run by tools/screenlog.ps1.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class ScreenLog
{
    // ------------------------------------------------------------------ win32

    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_WTSSESSION_CHANGE = 0x02B1;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;

    private static readonly Guid ConsoleDisplayState =
        new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");
    private static readonly Guid SessionDisplayStatus =
        new Guid("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
    private static readonly Guid MonitorPowerOn =
        new Guid("02731015-4510-4526-99e6-e5a17ebd1aea");
    private static readonly Guid AwayMode =
        new Guid("98a7f580-01f7-48aa-9c0f-44352c29e5c0");

    private static readonly Guid VideoSubgroup =
        new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoPowerdownTimeout =
        new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static readonly Guid SleepSubgroup =
        new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid StandbyTimeout =
        new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerSetting
    {
        public Guid Setting;
        public int DataLength;
        public byte Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size, Tick; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, Reserved1;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref Rect area, IntPtr data);

    [DllImport("user32.dll")] private static extern IntPtr RegisterPowerSettingNotification(IntPtr window, ref Guid setting, int flags);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(IntPtr window, int flags);

    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, ref uint count);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] found);
    [DllImport("dxva2.dll")] private static extern bool GetMonitorBrightness(IntPtr physical, ref uint minimum, ref uint current, ref uint maximum);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitor(IntPtr physical);

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, IntPtr scheme, ref Guid group, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, IntPtr scheme, ref Guid group, ref Guid setting, out uint value);
    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerReadFriendlyName(IntPtr root, IntPtr scheme, IntPtr group, IntPtr setting, StringBuilder buffer, ref uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    // ------------------------------------------------------------------ state

    private static readonly object Gate = new object();
    private static readonly DateTime Started = DateTime.Now;
    private static StreamWriter _file;
    private static volatile bool _stopping;

    /// <summary>One monitor, with the handle taken at the start kept for the whole run.</summary>
    private sealed class Watched
    {
        public string Name;
        public string Description;
        public Rect Area;
        public IntPtr Held;
        public int LastFresh = NothingYet;
        public int LastHeld = NothingYet;
    }

    /// <summary>No reading has been taken yet, as distinct from a reading that failed.</summary>
    private const int NothingYet = int.MinValue;

    private static readonly List<Watched> Monitors = new List<Watched>();

    // ------------------------------------------------------------------ logging

    private static void Log(string line)
    {
        string stamped = string.Format(CultureInfo.InvariantCulture,
            "{0:HH:mm:ss.fff}  [{1,7:0.0}s]  {2}",
            DateTime.Now, (DateTime.Now - Started).TotalSeconds, line);

        lock (Gate)
        {
            Console.WriteLine(stamped);
            if (_file == null) return;
            _file.WriteLine(stamped);
            _file.Flush();          // the screen may go off mid-run; nothing is left buffered
        }
    }

    private static void Heading(string text)
    {
        lock (Gate)
        {
            Console.WriteLine();
            Console.WriteLine(text);
            if (_file == null) return;
            _file.WriteLine();
            _file.WriteLine(text);
            _file.Flush();
        }
    }

    // ------------------------------------------------------------------ window

    /// <summary>Never shown. It exists only to be told things by Windows.</summary>
    private sealed class Listener : Form
    {
        public Listener()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
            IntPtr handle = Handle;
            GC.KeepAlive(handle);
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        public void Listen()
        {
            Register("CONSOLE_DISPLAY_STATE", ConsoleDisplayState);
            Register("SESSION_DISPLAY_STATUS", SessionDisplayStatus);
            Register("MONITOR_POWER_ON", MonitorPowerOn);
            Register("AWAY_MODE", AwayMode);

            Log(WTSRegisterSessionNotification(Handle, 0)
                ? "listening for lock and unlock"
                : "could NOT listen for lock and unlock");
        }

        private void Register(string name, Guid setting)
        {
            Guid copy = setting;
            Log(RegisterPowerSettingNotification(Handle, ref copy, 0) != IntPtr.Zero
                ? "listening for " + name
                : "could NOT listen for " + name);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST) Power(m);
            else if (m.Msg == WM_DISPLAYCHANGE) Log("WM_DISPLAYCHANGE  - the desktop layout changed");
            else if (m.Msg == WM_WTSSESSION_CHANGE) Session((int)m.WParam);
            base.WndProc(ref m);
        }

        private static void Power(Message m)
        {
            int what = (int)m.WParam;
            if (what == PBT_APMSUSPEND) { Log("*** THE MACHINE IS SUSPENDING (sleep) ***"); return; }
            if (what == PBT_APMRESUMESUSPEND) { Log("*** RESUMED FROM SLEEP ***"); return; }
            if (what == PBT_APMRESUMEAUTOMATIC) { Log("*** RESUMED (automatic) ***"); return; }
            if (what != PBT_POWERSETTINGCHANGE) return;

            PowerSetting setting;
            try
            {
                setting = (PowerSetting)Marshal.PtrToStructure(m.LParam, typeof(PowerSetting));
            }
            catch (Exception) { return; }

            if (setting.Setting == ConsoleDisplayState)
                Log(">>> SCREEN " + DisplayState(setting.Data) + "   (CONSOLE_DISPLAY_STATE = " + setting.Data + ")");
            else if (setting.Setting == SessionDisplayStatus)
                Log("    session display " + DisplayState(setting.Data) + "   (SESSION_DISPLAY_STATUS = " + setting.Data + ")");
            else if (setting.Setting == MonitorPowerOn)
                Log("    monitor power " + (setting.Data == 0 ? "OFF" : "ON") + "   (MONITOR_POWER_ON, legacy)");
            else if (setting.Setting == AwayMode)
                Log("    away mode " + (setting.Data == 0 ? "exited" : "entered"));
        }

        private static string DisplayState(byte data)
        {
            return data == 0 ? "OFF" : data == 1 ? "ON" : "DIMMED BY WINDOWS";
        }

        private static void Session(int reason)
        {
            if (reason == 0x7) Log("    session locked");
            else if (reason == 0x8) Log("    session unlocked");
        }
    }

    // ------------------------------------------------------------------ monitors

    private static List<IntPtr> Screens()
    {
        List<IntPtr> found = new List<IntPtr>();
        MonitorEnum collect = delegate(IntPtr monitor, IntPtr dc, ref Rect area, IntPtr data)
        {
            found.Add(monitor);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Takes a physical monitor handle, or IntPtr.Zero when none can be had. Asked more than
    /// once: a monitor drops the occasional request, and one refusal is not an answer.
    /// </summary>
    private static IntPtr TakeHandle(IntPtr screen, out string description, int attempts)
    {
        description = null;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(80);

            uint count = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(screen, ref count) || count == 0) continue;

            PhysicalMonitor[] physical = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(screen, count, physical)) continue;

            description = physical[0].Description;
            for (int i = 1; i < physical.Length; i++) DestroyPhysicalMonitor(physical[i].Handle);
            return physical[0].Handle;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Brightness through a handle. -1 means the monitor was asked and would not say; -2 means
    /// there was no handle to ask through. Telling those apart matters: the first is a monitor
    /// that has stopped listening, the second is one that was never reached.
    /// </summary>
    private const int Silent = -1;
    private const int NoHandle = -2;

    private static int Ask(IntPtr physical)
    {
        if (physical == IntPtr.Zero) return NoHandle;
        uint low = 0, now = 0, high = 0;
        if (!GetMonitorBrightness(physical, ref low, ref now, ref high) || high <= low) return Silent;
        return (int)Math.Round((now - (double)low) * 100.0 / (high - low));
    }

    private static string Percent(int value)
    {
        if (value == NoHandle) return "no handle";
        if (value == Silent) return "silent";
        return value + "%";
    }

    private static void FindMonitors()
    {
        foreach (IntPtr screen in Screens())
        {
            MonitorInfoEx info = new MonitorInfoEx();
            info.Size = Marshal.SizeOf(typeof(MonitorInfoEx));
            GetMonitorInfo(screen, ref info);

            string description;
            IntPtr held = TakeHandle(screen, out description, 4);

            Watched watched = new Watched();
            watched.Name = info.Device;
            watched.Description = description;
            watched.Area = info.Monitor;
            watched.Held = held;
            Monitors.Add(watched);
        }
    }

    // ------------------------------------------------------------------ sampling

    private static long IdleSeconds()
    {
        LastInput info = new LastInput();
        info.Size = (uint)Marshal.SizeOf(typeof(LastInput));
        if (!GetLastInputInfo(ref info)) return -1;
        return (long)(uint)((uint)Environment.TickCount - info.Tick) / 1000;
    }

    private static void Sample(bool probeMonitors)
    {
        StringBuilder line = new StringBuilder();
        line.Append("sample  idle=").Append(IdleSeconds()).Append("s");

        Process[] dimly = Process.GetProcessesByName("Dimly");
        line.Append("  Dimly=").Append(dimly.Length == 0 ? "not running" : "running");
        if (dimly.Length > 0)
        {
            try { line.Append(" (").Append(dimly[0].WorkingSet64 / 1024).Append(" KB)"); }
            catch (Exception) { }
        }
        foreach (Process process in dimly) process.Dispose();

        int screens = Screens().Count;
        if (screens != Monitors.Count) line.Append("  DISPLAY COUNT NOW ").Append(screens);

        if (!probeMonitors)
        {
            Log(line.ToString());
            return;
        }

        foreach (Watched monitor in Monitors)
        {
            int held = Ask(monitor.Held);

            string ignored;
            IntPtr fresh = IntPtr.Zero;
            foreach (IntPtr screen in Screens())
            {
                MonitorInfoEx info = new MonitorInfoEx();
                info.Size = Marshal.SizeOf(typeof(MonitorInfoEx));
                GetMonitorInfo(screen, ref info);
                if (info.Device != monitor.Name) continue;
                fresh = TakeHandle(screen, out ignored, 2);
                break;
            }

            int now = Ask(fresh);
            if (fresh != IntPtr.Zero) DestroyPhysicalMonitor(fresh);

            line.Append("  |  ").Append(monitor.Name)
                .Append(" held=").Append(Percent(held))
                .Append(" fresh=").Append(Percent(now));

            if (monitor.LastHeld != NothingYet && held != monitor.LastHeld)
                line.Append("  [held changed from ").Append(Percent(monitor.LastHeld)).Append("]");
            if (monitor.LastFresh != NothingYet && now != monitor.LastFresh)
                line.Append("  [CHANGED from ").Append(Percent(monitor.LastFresh)).Append("]");
            if (held >= 0 && now >= 0 && Math.Abs(held - now) > 3)
                line.Append("  [!! held and fresh DISAGREE - the stale handle is lying !!]");

            monitor.LastHeld = held;
            monitor.LastFresh = now;
        }

        Log(line.ToString());
    }

    // ------------------------------------------------------------------ setup report

    private static uint ReadTimeout(IntPtr scheme, Guid group, Guid setting, bool onBattery)
    {
        uint value;
        uint result = onBattery
            ? PowerReadDCValueIndex(IntPtr.Zero, scheme, ref group, ref setting, out value)
            : PowerReadACValueIndex(IntPtr.Zero, scheme, ref group, ref setting, out value);
        return result == 0 ? value : uint.MaxValue;
    }

    private static string Minutes(uint seconds)
    {
        if (seconds == uint.MaxValue) return "unknown";
        if (seconds == 0) return "never";
        return seconds >= 60
            ? (seconds / 60) + " min"
            : seconds + " s";
    }

    /// <summary>
    /// The real Windows version. Environment.OSVersion reports 6.2 to a program without a
    /// manifest saying otherwise, which is no use in a diagnostic.
    /// </summary>
    private static string WindowsVersion()
    {
        try
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (key == null) return Environment.OSVersion.Version.ToString();
                object name = key.GetValue("ProductName");
                object shown = key.GetValue("DisplayVersion");
                object build = key.GetValue("CurrentBuild");
                return (name == null ? "Windows" : name.ToString())
                     + (shown == null ? string.Empty : " " + shown)
                     + (build == null ? string.Empty : " (build " + build + ")");
            }
        }
        catch (Exception) { return Environment.OSVersion.Version.ToString(); }
    }

    private static void ReportSetup()
    {
        Heading("---- this machine ----");
        Log("Windows " + WindowsVersion() + "   " + (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit"));

        PowerStatus power;
        if (GetSystemPowerStatus(out power))
            Log("power: " + (power.ACLineStatus == 1 ? "plugged in" : "on battery"));

        IntPtr scheme;
        if (PowerGetActiveScheme(IntPtr.Zero, out scheme) == 0)
        {
            try
            {
                uint size = 512;
                StringBuilder name = new StringBuilder((int)size);
                if (PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, name, ref size) == 0)
                    Log("power plan: " + name);

                Log("turn off the screen after : " + Minutes(ReadTimeout(scheme, VideoSubgroup, VideoPowerdownTimeout, false))
                    + " plugged in, " + Minutes(ReadTimeout(scheme, VideoSubgroup, VideoPowerdownTimeout, true)) + " on battery");
                Log("put the PC to sleep after : " + Minutes(ReadTimeout(scheme, SleepSubgroup, StandbyTimeout, false))
                    + " plugged in, " + Minutes(ReadTimeout(scheme, SleepSubgroup, StandbyTimeout, true)) + " on battery");
            }
            finally { LocalFree(scheme); }
        }

        Heading("---- monitors ----");
        foreach (Watched monitor in Monitors)
        {
            Log(monitor.Name
                + "   " + (monitor.Area.Right - monitor.Area.Left) + "x" + (monitor.Area.Bottom - monitor.Area.Top)
                + " at " + monitor.Area.Left + "," + monitor.Area.Top
                + "   " + (monitor.Held == IntPtr.Zero
                    ? "would NOT give a DDC/CI handle, even after four tries"
                    : "DDC/CI: " + (string.IsNullOrEmpty(monitor.Description) ? "yes" : monitor.Description.Trim()))
                + "   brightness now " + Percent(Ask(monitor.Held)));
        }

        Heading("---- Dimly ----");
        Process[] running = Process.GetProcessesByName("Dimly");
        if (running.Length == 0) Log("Dimly is NOT running");
        foreach (Process process in running)
        {
            try { Log("Dimly is running: " + process.MainModule.FileName); }
            catch (Exception) { Log("Dimly is running"); }
            process.Dispose();
        }

        string settings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dimly\\settings.ini");
        if (!File.Exists(settings)) { Log("no settings file at " + settings); return; }

        Log("settings (" + settings + "):");
        try
        {
            foreach (string line in File.ReadAllLines(settings)) Log("    " + line);
        }
        catch (IOException error) { Log("could not read them: " + error.Message); }
    }

    // ------------------------------------------------------------------ main

    [STAThread]
    private static void Main(string[] args)
    {
        int minutes = 15;
        int every = 5;
        bool probe = true;
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "dimly-screenlog.txt");

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-minutes" && i + 1 < args.Length) minutes = int.Parse(args[++i]);
            else if (args[i] == "-every" && i + 1 < args.Length) every = int.Parse(args[++i]);
            else if (args[i] == "-noprobe") probe = false;
            else if (args[i] == "-out" && i + 1 < args.Length) path = args[++i];
        }

        try { _file = new StreamWriter(path, false, Encoding.UTF8); }
        catch (Exception error)
        {
            Console.WriteLine("Could not write the log to " + path + ": " + error.Message);
            return;
        }

        Console.Title = "Dimly screen log";
        Log("---- Dimly screen log ----");
        Log("writing to " + path);
        Log("recording for " + minutes + " minutes, sampling every " + every + " s"
            + (probe ? string.Empty : ", without asking the monitors anything"));

        Listener listener = new Listener();
        FindMonitors();
        ReportSetup();

        Heading("---- what to do now ----");
        Log("Leave the machine completely alone. Let Windows switch the screen off on its own,");
        Log("wait several minutes past that, then wake it the way you normally would and watch");
        Log("what the brightness does. This window keeps recording until it says it has stopped.");
        Heading("---- recording ----");

        listener.Listen();

        Thread sampler = new Thread(delegate()
        {
            DateTime until = Started.AddMinutes(minutes);
            while (!_stopping && DateTime.Now < until)
            {
                try { Sample(probe); }
                catch (Exception error) { Log("sampling failed: " + error.Message); }

                for (int waited = 0; waited < every * 1000 && !_stopping; waited += 200)
                    Thread.Sleep(200);
            }

            _stopping = true;
            try { listener.BeginInvoke(new MethodInvoker(Application.ExitThread)); }
            catch (Exception) { }
        });
        sampler.IsBackground = true;
        sampler.Start();

        Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
        {
            _stopping = true;
            e.Cancel = true;
        };

        Application.Run();

        _stopping = true;
        Heading("---- stopped ----");
        Log("The log is at " + path);
        Log("Send that file on - it has everything this recorded.");

        foreach (Watched monitor in Monitors)
            if (monitor.Held != IntPtr.Zero) DestroyPhysicalMonitor(monitor.Held);

        lock (Gate)
        {
            _file.Flush();
            _file.Close();
            _file = null;
        }

        Console.WriteLine();
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }
}
