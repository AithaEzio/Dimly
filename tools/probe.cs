// Development probe: runs Dimly's real display enumeration and prints what it found.
// Built by tools/probe.ps1 against src/Native.cs and src/Displays.cs.

using System;
using System.Windows.Forms;
using Dimly;

internal static class Probe
{
    private const int Samples = 10;
    private const int SamplePauseMilliseconds = 120;

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
            Console.WriteLine(string.Format(
                "  {0} | kind={1} | {2}x{3} | primary={4} | model={5} | key={6}",
                target.Name, target.Kind, target.Bounds.Width, target.Bounds.Height,
                target.IsPrimary, target.Model, target.Key));

            // One reading proves nothing about a monitor on a serial link: it can answer the
            // query that enumerated it and drop the next. Sample it, and report both how often
            // it answers and what it says, so a refused read is never mistaken for a dark screen.
            int answered = 0;
            int lowest = 101, highest = -1;
            for (int sample = 0; sample < Samples; sample++)
            {
                int current;
                if (target.TryRead(out current))
                {
                    answered++;
                    if (current < lowest) lowest = current;
                    if (current > highest) highest = current;
                }
                System.Threading.Thread.Sleep(SamplePauseMilliseconds);
            }

            Console.WriteLine(answered == 0
                ? string.Format("      answered {0} of {1} reads", answered, Samples)
                : string.Format("      answered {0} of {1} reads | {2}",
                    answered, Samples,
                    lowest == highest ? lowest + "%" : lowest + "% to " + highest + "%"));
        }
    }
}
