// Checks Native.IsFullscreenAppActive against real windows.
//
// The case that matters is the second one. A maximised window overhangs its monitor by the
// invisible resize border, and when the taskbar auto-hides the work area is the whole monitor,
// so "the window covers the screen" is true for an ordinary maximised browser. Reading that as
// fullscreen made "never dim over a fullscreen app" block dimming permanently.
//
// Built by tools/test.ps1 with src/Native.cs. Flashes a few windows for a moment.

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Dimly;

internal static class FullscreenTest
{
    private static int _failures;

    [STAThread]
    private static void Main()
    {
        Rectangle monitor = Screen.PrimaryScreen.Bounds;

        Check("a borderless window filling the monitor is fullscreen", true, delegate(Form form)
        {
            form.FormBorderStyle = FormBorderStyle.None;
            form.Bounds = monitor;
        });

        Check("a maximised ordinary window is not fullscreen", false, delegate(Form form)
        {
            form.FormBorderStyle = FormBorderStyle.Sizable;
            form.WindowState = FormWindowState.Maximized;
        });

        Check("a normal small window is not fullscreen", false, delegate(Form form)
        {
            form.FormBorderStyle = FormBorderStyle.Sizable;
            form.Bounds = new Rectangle(monitor.X + 120, monitor.Y + 120, 640, 400);
        });

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : _failures + " CHECK(S) FAILED");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static void Check(string what, bool expected, Action<Form> arrange)
    {
        bool actual;
        using (Form form = new Form())
        {
            form.Text = "Dimly fullscreen check";
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.BackColor = Color.FromArgb(18, 20, 28);
            arrange(form);

            form.Show();

            // The rule reads the *foreground* window, so the test window has to actually be
            // in front. Windows can refuse to hand over the foreground - another process may
            // hold it, or be topmost - and a refusal is not a failure of the rule.
            bool inFront = false;
            for (int attempt = 0; attempt < 5 && !inFront; attempt++)
            {
                form.Activate();
                Pump(300);
                inFront = GetForegroundWindow() == form.Handle;
            }

            if (!inFront)
            {
                Console.WriteLine("  skip  " + what + " (could not bring the test window to the front)");
                form.Close();
                Pump(200);
                return;
            }

            actual = Native.IsFullscreenAppActive();

            form.Close();
            Pump(200);
        }

        bool passed = actual == expected;
        Console.WriteLine((passed ? "  pass  " : "  FAIL  ") + what
            + (passed ? string.Empty : "  (expected " + expected + ", got " + actual + ")"));
        if (!passed) _failures++;
    }

    private static void Pump(int milliseconds)
    {
        int until = Environment.TickCount + milliseconds;
        while (Environment.TickCount < until)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }
}
