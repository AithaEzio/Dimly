// Traces a dim-and-restore through a display power cycle using Dimly's real display layer,
// printing every value it acts on. When a screen is left dim after waking, this shows which
// step is at fault: the brightness that was captured, the write, or the read-back that is
// meant to prove the write landed.
//
// Built by tools/restoreprobe.ps1 with src/Native.cs and src/Displays.cs.
// The screen goes black for about ten seconds. Brightness is put back at the end.

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Dimly;

internal static class RestoreProbe
{
    private const int AwayLevel = 30;

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [STAThread]
    private static void Main()
    {
        Form host = new Form();
        IntPtr handle = host.Handle;
        GC.KeepAlive(handle);

        DisplayManager displays = new DisplayManager(host);
        displays.Refresh();
        if (displays.Targets.Count == 0) { Console.WriteLine("No displays."); return; }

        DisplayTarget target = displays.Targets[0];
        Console.WriteLine("display: " + target.Name + "  (" + target.Kind + ")");

        int before;
        bool readable = target.TryRead(out before);
        Console.WriteLine("  TryRead before dimming : ok=" + readable + " value=" + before);

        // Exactly what DimEngine does when it dims.
        target.Captured = readable ? before : 100;
        int goal = Math.Min(target.Captured.Value, AwayLevel);
        Console.WriteLine("  Captured               : " + target.Captured.Value);
        Console.WriteLine("  writing                : " + goal);
        try { target.Write(goal); target.Applied = goal; }
        catch (Exception error) { Console.WriteLine("  write threw: " + error.Message); }

        Thread.Sleep(800);
        int afterDim;
        Console.WriteLine("  TryRead after dimming  : ok=" + target.TryRead(out afterDim) + " value=" + afterDim);

        Console.WriteLine();
        Console.WriteLine("Turning the display off for 10 seconds...");
        SendMessage(new IntPtr(0xFFFF), 0x0112, (IntPtr)0xF170, (IntPtr)2);
        Sleep(10000);
        Wiggle();
        Sleep(3000);
        Wiggle();
        Sleep(2000);

        Console.WriteLine();
        Console.WriteLine("After waking:");
        int afterWake;
        Console.WriteLine("  TryRead                : ok=" + target.TryRead(out afterWake) + " value=" + afterWake);
        Console.WriteLine("  Captured still         : " +
            (target.Captured.HasValue ? target.Captured.Value.ToString(CultureInfo.InvariantCulture) : "null"));

        Console.WriteLine("  TryWriteVerified(" + target.Captured.Value + ")  : "
            + target.TryWriteVerified(target.Captured.Value));

        Thread.Sleep(800);
        int finalValue;
        Console.WriteLine("  TryRead at the end     : ok=" + target.TryRead(out finalValue) + " value=" + finalValue);

        displays.Dispose();
        host.Dispose();
    }

    private static void Wiggle()
    {
        mouse_event(0x0001, 4, 0, 0, IntPtr.Zero);
        Thread.Sleep(80);
        mouse_event(0x0001, unchecked((uint)-4), 0, 0, IntPtr.Zero);
    }

    /// <summary>Sleeps while keeping the message loop alive, as the real app would.</summary>
    private static void Sleep(int milliseconds)
    {
        int until = Environment.TickCount + milliseconds;
        while (Environment.TickCount < until)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
    }
}
