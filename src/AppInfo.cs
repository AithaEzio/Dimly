using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Dimly
{
    /// <summary>Identity and artwork, both embedded in the single executable.</summary>
    public static class AppInfo
    {
        public const string Name = "Dimly";
        public const string Version = "1.0";
        public const string Tagline = "Dims your screens while you are away.";

        private const string IconResource = "Dimly.dimly.ico";

        private static readonly Dictionary<int, Icon> IconCache = new Dictionary<int, Icon>();
        private static readonly Dictionary<int, Bitmap> MarkCache = new Dictionary<int, Bitmap>();

        public static string ExecutablePath { get { return Application.ExecutablePath; } }

        /// <summary>The application icon at the nearest available frame to <paramref name="size"/>.</summary>
        public static Icon Icon(int size)
        {
            Icon icon;
            if (IconCache.TryGetValue(size, out icon)) return icon;

            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(IconResource))
                icon = stream != null ? new Icon(stream, size, size) : SystemIcons.Application;

            IconCache[size] = icon;
            return icon;
        }

        /// <summary>The icon as a bitmap, for drawing inside the window.</summary>
        public static Bitmap Mark(int size)
        {
            Bitmap mark;
            if (MarkCache.TryGetValue(size, out mark)) return mark;

            using (Bitmap source = Icon(size >= 64 ? 128 : 64).ToBitmap())
            {
                mark = new Bitmap(size, size);
                using (Graphics g = Graphics.FromImage(mark))
                {
                    Ui.Quality(g);
                    g.DrawImage(source, new Rectangle(0, 0, size, size));
                }
            }

            MarkCache[size] = mark;
            return mark;
        }
    }
}
