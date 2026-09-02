// Checks that the artwork is really in the executable and really being found.
//
// Dimly carries one copy of its icon, as the Win32 resource the compiler embeds for
// /win32icon, and reads it back from there. If that resource were ever dropped from the build,
// nothing would crash: every icon would quietly become the generic Windows application icon.
// This is the check that would notice.
//
// Built by tools/test.ps1 with src/AppInfo.cs, and - importantly - with the same /win32icon
// switch the real build uses.

using System;
using System.Drawing;
using Dimly;

internal static class IconProbe
{
    private static int _failures;

    private static void Main()
    {
        foreach (int size in new int[] { 16, 20, 24, 32, 48, 64, 128, 256 })
        {
            Icon icon = AppInfo.Icon(size);
            Check("icon at " + size + "px is the size asked for", icon.Width == size && icon.Height == size);
        }

        Check("the icon is Dimly's, not the Windows stand-in", !LooksLikeFallback());

        foreach (int size in new int[] { 30, 44, 88 })
        {
            Bitmap mark = AppInfo.Mark(size);
            Check("mark at " + size + "px is drawable", mark != null && mark.Width == size && mark.Height == size);
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : _failures + " CHECK(S) FAILED");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    /// <summary>
    /// AppInfo falls back to the system application icon when the resource cannot be loaded,
    /// so telling the two apart is the whole point of this probe.
    /// </summary>
    private static bool LooksLikeFallback()
    {
        using (Bitmap ours = AppInfo.Icon(32).ToBitmap())
        using (Bitmap theirs = SystemIcons.Application.ToBitmap())
        {
            if (ours.Width != theirs.Width || ours.Height != theirs.Height) return false;
            for (int y = 0; y < ours.Height; y += 4)
                for (int x = 0; x < ours.Width; x += 4)
                    if (ours.GetPixel(x, y) != theirs.GetPixel(x, y)) return false;
            return true;
        }
    }

    private static void Check(string what, bool passed)
    {
        Console.WriteLine((passed ? "  pass  " : "  FAIL  ") + what);
        if (!passed) _failures++;
    }
}
