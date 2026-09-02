using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Dimly
{
    /// <summary>Identity and artwork, both embedded in the single executable.</summary>
    public static class AppInfo
    {
        public const string Name = "Dimly";
        public const string Tagline = "Dims your screens while you are away.";

        private static string _version;

        /// <summary>
        /// Read from the assembly rather than written twice: what the window shows and what
        /// Explorer shows for the file are then guaranteed to agree. See src/AssemblyInfo.cs.
        /// </summary>
        public static string Version
        {
            get
            {
                if (_version == null)
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    object[] tags = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                    _version = tags.Length > 0
                        ? ((AssemblyInformationalVersionAttribute)tags[0]).InformationalVersion
                        : assembly.GetName().Version.ToString(2);
                }
                return _version;
            }
        }

        /// <summary>
        /// The icon the C# compiler embeds for /win32icon. Reading it from there is what lets
        /// the executable carry one copy of the artwork instead of two: a managed copy of the
        /// same file cost more than a third of the finished size.
        /// </summary>
        private const string IconResourceName = "#32512";

        private static readonly Dictionary<int, Icon> IconCache = new Dictionary<int, Icon>();
        private static readonly Dictionary<int, Bitmap> MarkCache = new Dictionary<int, Bitmap>();

        public static string ExecutablePath { get { return Application.ExecutablePath; } }

        /// <summary>
        /// The application icon, rendered at the requested size. Windows picks the closest frame
        /// in the icon and scales it, so asking for an exact size is better than choosing a frame
        /// and resizing it here.
        /// </summary>
        public static Icon Icon(int size)
        {
            Icon icon;
            if (IconCache.TryGetValue(size, out icon)) return icon;

            icon = LoadFromResource(size) ?? SystemIcons.Application;
            IconCache[size] = icon;
            return icon;
        }

        private static Icon LoadFromResource(int size)
        {
            IntPtr handle = LoadImage(GetModuleHandle(null), IconResourceName, IMAGE_ICON, size, size, LR_DEFAULTCOLOR);
            if (handle == IntPtr.Zero) return null;

            try
            {
                // Clone into a managed icon that owns its own copy, so the handle can go back.
                // Fully qualified because this class has a method called Icon.
                using (Icon borrowed = System.Drawing.Icon.FromHandle(handle))
                    return (Icon)borrowed.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }

        private const uint IMAGE_ICON = 1;
        private const uint LR_DEFAULTCOLOR = 0;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr module, string name, uint type, int cx, int cy, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        /// <summary>The icon as a bitmap, for drawing inside the window.</summary>
        public static Bitmap Mark(int size)
        {
            Bitmap mark;
            if (MarkCache.TryGetValue(size, out mark)) return mark;

            mark = Icon(size).ToBitmap();
            MarkCache[size] = mark;
            return mark;
        }
    }
}
