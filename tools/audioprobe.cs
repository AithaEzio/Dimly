// Development probe: prints what Dimly's MediaWatcher hears, once a second.
// Use it to find out why a particular player is or is not counted as playback.
//
//   csc /target:exe src\MediaWatcher.cs tools\audioprobe.cs
//
// Optional argument: how many seconds to watch (default 15).

using System;
using System.Globalization;
using System.Threading;
using Dimly;

internal static class AudioProbe
{
    private static void Main(string[] args)
    {
        int seconds = 15;
        if (args.Length > 0) int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);

        using (MediaWatcher watcher = new MediaWatcher())
        {
            watcher.Enabled = true;
            Console.WriteLine("second  peak      playing");
            for (int i = 1; i <= seconds; i++)
            {
                Thread.Sleep(1000);
                Console.WriteLine("{0,6}  {1,-8}  {2}",
                    i, watcher.LastPeak.ToString("0.000000", CultureInfo.InvariantCulture), watcher.IsPlaying);
            }
        }
    }
}
