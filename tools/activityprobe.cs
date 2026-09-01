// Verifies the shipping ActivityWatcher end to end: that Raw Input registration succeeds, that
// the clock climbs while nothing happens, and that a real mouse movement resets it.
//
// This cannot be inferred from Dimly's behaviour alone. If registration failed, Available would
// be false, the engine would quietly fall back to the system idle clock, and dimming would look
// exactly the same on a healthy machine - while doing nothing at all on the machine the setting
// exists to rescue.
//
// Built by tools/test.ps1 with src/ActivityWatcher.cs.

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Dimly;

internal static class ActivityProbe
{
    private static int _failures;

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    private const uint MOUSEEVENTF_MOVE = 0x0001;

    /// <summary>
    /// Windows' own idle clock, used only to tell the two ways a quiet-window check can fail
    /// apart: the watcher wrongly stamping activity, or somebody genuinely using the machine
    /// while the test runs. The second is not a failure, and must not be reported as one.
    /// </summary>
    private static long SystemIdleMs()
    {
        LASTINPUTINFO info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (!GetLastInputInfo(ref info)) return -1;
        return (long)(uint)((uint)Environment.TickCount - info.dwTime);
    }

    private static int _skipped;

    /// <summary>Asserts only if the machine really was left alone for the window.</summary>
    private static void CheckWhenQuiet(string what, bool passed, int neededMs)
    {
        long system = SystemIdleMs();
        if (system >= 0 && system < neededMs)
        {
            Console.WriteLine("  skip  " + what + " (somebody used the machine during the check)");
            _skipped++;
            return;
        }
        Check(what, passed);
    }

    [STAThread]
    private static void Main()
    {
        using (ActivityWatcher watcher = new ActivityWatcher())
        {
            watcher.Enabled = true;
            Check("Raw Input registration succeeded", watcher.Available);

            if (!watcher.Available)
            {
                Console.WriteLine();
                Console.WriteLine("Nothing further can be checked without it.");
                Environment.Exit(1);
            }

            Pump(2500);
            int quiet = watcher.IdleMilliseconds;
            Console.WriteLine("  idle after 2.5s of nothing: " + quiet + "ms");
            CheckWhenQuiet("the clock climbs while nobody does anything", quiet >= 2000, 2000);

            // A pixel out and back: real movement, but the pointer ends where it started.
            mouse_event(MOUSEEVENTF_MOVE, 1, 0, 0, IntPtr.Zero);
            Pump(120);
            mouse_event(MOUSEEVENTF_MOVE, unchecked((uint)-1), 0, 0, IntPtr.Zero);
            Pump(400);

            int afterMove = watcher.IdleMilliseconds;
            Console.WriteLine("  idle after a real movement: " + afterMove + "ms");
            Check("real mouse movement resets the clock", afterMove < 1000);

            // The jitter filter: a move of no distance must not count as somebody being here.
            Pump(2500);
            int beforeJitter = watcher.IdleMilliseconds;
            mouse_event(MOUSEEVENTF_MOVE, 0, 0, 0, IntPtr.Zero);
            Pump(400);
            int afterJitter = watcher.IdleMilliseconds;
            Console.WriteLine("  idle across a zero-distance move: " + beforeJitter + "ms -> " + afterJitter + "ms");
            CheckWhenQuiet("a zero-distance move is ignored as jitter", afterJitter > beforeJitter, 2000);
        }

        Console.WriteLine();
        if (_failures > 0) Console.WriteLine(_failures + " CHECK(S) FAILED");
        else if (_skipped > 0) Console.WriteLine("ALL CHECKS PASSED (" + _skipped + " skipped - machine in use)");
        else Console.WriteLine("ALL CHECKS PASSED");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    private static void Check(string what, bool passed)
    {
        Console.WriteLine((passed ? "  pass  " : "  FAIL  ") + what);
        if (!passed) _failures++;
    }

    /// <summary>Raw Input arrives as window messages, so the loop has to run.</summary>
    private static void Pump(int milliseconds)
    {
        int until = Environment.TickCount + milliseconds;
        while (Environment.TickCount < until)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }
}
