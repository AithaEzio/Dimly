// Development probe: runs Dimly's real display enumeration and prints what it found.
// Built by tools/probe.ps1 against src/Native.cs and src/Displays.cs.

using System;
using System.Windows.Forms;
using Dimly;

internal static class Probe
{
    [STAThread]
    private static void Main()
    {
        Form host = new Form();
        IntPtr handle = host.Handle;
        GC.KeepAlive(handle);

        DisplayManager displays = new DisplayManager(host);
        try
        {
            displays.Refresh();
        }
        catch (Exception error)
        {
            Console.WriteLine("REFRESH THREW:");
            Console.WriteLine(error.ToString());
            return;
        }

        Console.WriteLine("targets: " + displays.Targets.Count);
        foreach (DisplayTarget target in displays.Targets)
        {
            int current;
            bool read = target.TryRead(out current);
            Console.WriteLine(string.Format(
                "  {0} | kind={1} | {2}x{3} | primary={4} | model={5} | key={6} | read={7} value={8}",
                target.Name, target.Kind, target.Bounds.Width, target.Bounds.Height,
                target.IsPrimary, target.Model, target.Key, read, current));
        }
    }
}
