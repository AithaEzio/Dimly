// Traces what happens to a display's brightness across a monitor power-off - the Windows
// "turn off the screen after N minutes" timeout, not sleep.
//
// It answers two questions that decide how the bug can be fixed at all:
//   1. Does anything tell the app the display went off and came back? SystemEvents and the
//      power-setting notifications are all logged with timestamps.
//   2. Can a brightness write be confirmed while the panel is still dark? A physical monitor
//      handle taken before the power-off can accept commands and echo them back, so a restore
//      looks successful and the screen still comes up dim.
//
// Built by tools/waketrace.ps1 against the shipping src/Native.cs and src/Displays.cs.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Dimly;

internal static class WakeTrace
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int SC_MONITORPOWER = 0xF170;
    private const int MonitorOff = 2;

    private static readonly Guid GuidConsoleDisplayState =
        new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");
    private static readonly Guid GuidMonitorPowerOn =
        new Guid("02731015-4510-4526-99e6-e5a17ebd1aea");

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public int DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

    private static readonly DateTime Started = DateTime.Now;

    /// <summary>Set once the screen has been asked to switch off, so an unasked-for wake shows up.</summary>
    private static bool _switchedOff;
    private static double _cameBackBySelf = -1;

    private static void Log(string what)
    {
        Console.WriteLine("  [{0,6:0.0}s] {1}", (DateTime.Now - Started).TotalSeconds, what);
    }

    /// <summary>A window that writes down every wake-related message Windows sends it.</summary>
    private sealed class Listener : Form
    {
        public Listener()
        {
            // Never shown, but it must exist to receive broadcasts.
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(-4000, -4000);
            Size = new System.Drawing.Size(1, 1);
        }

        public void Listen()
        {
            Guid console = GuidConsoleDisplayState;
            Guid monitor = GuidMonitorPowerOn;
            Log(RegisterPowerSettingNotification(Handle, ref console, 0) != IntPtr.Zero
                ? "registered for GUID_CONSOLE_DISPLAY_STATE"
                : "COULD NOT register for GUID_CONSOLE_DISPLAY_STATE");
            Log(RegisterPowerSettingNotification(Handle, ref monitor, 0) != IntPtr.Zero
                ? "registered for GUID_MONITOR_POWER_ON"
                : "COULD NOT register for GUID_MONITOR_POWER_ON");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE)
            {
                POWERBROADCAST_SETTING setting = (POWERBROADCAST_SETTING)
                    Marshal.PtrToStructure(m.LParam, typeof(POWERBROADCAST_SETTING));
                string name = setting.PowerSetting == GuidConsoleDisplayState ? "CONSOLE_DISPLAY_STATE"
                            : setting.PowerSetting == GuidMonitorPowerOn ? "MONITOR_POWER_ON"
                            : setting.PowerSetting.ToString();
                string state = setting.Data == 0 ? "OFF" : setting.Data == 1 ? "ON" : "DIMMED";
                Log("WM_POWERBROADCAST  " + name + " = " + state);

                // A screen that switches itself back on has not been off for the time asked
                // for, and anything the run concludes about a long power-off would be false.
                if (_switchedOff && _cameBackBySelf < 0
                    && setting.PowerSetting == GuidConsoleDisplayState && setting.Data == 1)
                {
                    _cameBackBySelf = (DateTime.Now - Started).TotalSeconds;
                }
            }
            else if (m.Msg == WM_DISPLAYCHANGE)
            {
                Log("WM_DISPLAYCHANGE");
            }
            base.WndProc(ref m);
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        int offSeconds = args.Length > 0 ? int.Parse(args[0]) : 60;
        int watchSeconds = args.Length > 1 ? int.Parse(args[1]) : 45;

        Listener listener = new Listener();
        IntPtr handle = listener.Handle;
        GC.KeepAlive(handle);
        listener.Listen();

        SystemEvents.DisplaySettingsChanged += delegate { Log("SystemEvents.DisplaySettingsChanged"); };
        SystemEvents.PowerModeChanged += delegate(object s, PowerModeChangedEventArgs e)
        {
            Log("SystemEvents.PowerModeChanged  " + e.Mode);
        };

        DisplayManager displays = new DisplayManager(listener);
        displays.Refresh();

        DisplayTarget target = null;
        foreach (DisplayTarget candidate in displays.Targets)
            if (candidate.Kind == BrightnessKind.Ddc || candidate.Kind == BrightnessKind.Backlight)
            {
                target = candidate;
                break;
            }

        if (target == null)
        {
            Console.WriteLine("No display Dimly can set - nothing to trace.");
            return;
        }

        Console.WriteLine("Tracing " + target.Name + " (" + target.Kind + ")");
        Console.WriteLine("The screen goes off for " + offSeconds + "s, then is woken and watched for "
                          + watchSeconds + "s.");
        Console.WriteLine();

        int original;
        if (!target.TryRead(out original)) original = 75;
        Log("brightness now " + original + "%");

        target.Write(30);
        Pump(1200);
        int dimmed;
        target.TryRead(out dimmed);
        Log("dimmed to " + dimmed + "%  (this is the state the screen switches off in)");

        Log("switching the screen off...");
        _switchedOff = true;
        SendMessage((IntPtr)0xFFFF, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)MonitorOff);

        // Nothing is asked of the display while it is off: a DDC query would wake it.
        Pump(offSeconds * 1000);

        Log("waking it with real input...");
        mouse_event(0x0001, 4, 0, 0, IntPtr.Zero);
        Pump(80);
        mouse_event(0x0001, unchecked((uint)-4), 0, 0, IntPtr.Zero);

        bool wroteYet = false;
        bool claimedSuccess = false;
        for (int second = 0; second < watchSeconds; second++)
        {
            Pump(1000);

            int held;
            bool answered = target.TryRead(out held);

            if (!wroteYet && second >= 1)
            {
                wroteYet = true;
                bool ok = target.TryWriteVerified(original);
                claimedSuccess = ok;
                Log("TryWriteVerified(" + original + ") returned " + ok
                    + "   <- true here means Dimly would stop trying");
                continue;
            }

            Log("held handle: " + (answered ? held + "%" : "no answer"));
        }

        // The decisive reading: a handle taken now, after the display has finished powering on.
        target.Reacquire();
        Pump(400);
        int fresh;
        bool freshAnswered = target.TryRead(out fresh);
        Log("FRESH handle reads " + (freshAnswered ? fresh + "%" : "no answer"));

        Console.WriteLine();
        if (_cameBackBySelf >= 0)
        {
            Console.WriteLine(string.Format(
                "INCONCLUSIVE - the screen switched itself back on {0:0.0}s in, so it was never off",
                _cameBackBySelf));
            Console.WriteLine("for the time asked for. Something on this machine is keeping the");
            Console.WriteLine("display awake; nothing below says anything about a long power-off.");
            Console.WriteLine();
        }

        if (claimedSuccess && freshAnswered && Math.Abs(fresh - original) > 5)
        {
            Console.WriteLine("REPRODUCED - the restore was confirmed while the panel stayed at "
                              + fresh + "%.");
            Console.WriteLine("Dimly would have believed it and stopped trying.");
        }
        else if (freshAnswered && Math.Abs(fresh - original) <= 5)
        {
            Console.WriteLine("Restore held: the display really is back at " + fresh + "%.");
        }
        else
        {
            Console.WriteLine("Inconclusive - the display never answered afterwards.");
        }

        target.Write(original);
        Pump(600);
    }

    private static void Pump(int milliseconds)
    {
        int until = Environment.TickCount + milliseconds;
        while (unchecked(Environment.TickCount - until) < 0)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(40);
        }
    }
}
