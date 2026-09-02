using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Management;
using System.Threading;
using System.Windows.Forms;

namespace Dimly
{
    /// <summary>How a display's brightness is actually being changed.</summary>
    public enum BrightnessKind
    {
        /// <summary>Panel backlight, via the WMI interface laptops expose.</summary>
        Backlight,
        /// <summary>Monitor backlight, via the DDC/CI channel over the video cable.</summary>
        Ddc,
        /// <summary>A click-through black overlay, for displays that allow neither.</summary>
        Overlay
    }

    public interface IBrightnessBackend : IDisposable
    {
        BrightnessKind Kind { get; }
        bool TryRead(out int percent);

        /// <summary>Applies a 0-100 level. Throws when the hardware refuses.</summary>
        void Write(int percent);

        /// <summary>
        /// Re-establishes whatever handle the display may have invalidated. Powering a monitor
        /// off, or the machine sleeping, quietly kills the handles held across it.
        /// </summary>
        void Reacquire();
    }

    // ---------------------------------------------------------------- backends

    /// <summary>Laptop and all-in-one panels, driven through root\WMI.</summary>
    internal sealed class WmiBacklight : IBrightnessBackend
    {
        private const string Scope = "root\\WMI";

        private readonly string _instanceName;
        private ManagementObject _methods;

        private WmiBacklight(string instanceName)
        {
            _instanceName = instanceName;
            PnpKey = NormalizeInstanceName(instanceName);
        }

        /// <summary>The monitor's Plug-and-Play identity, for matching against a display adapter.</summary>
        public string PnpKey { get; private set; }

        public BrightnessKind Kind { get { return BrightnessKind.Backlight; } }

        public static List<WmiBacklight> Discover()
        {
            List<WmiBacklight> panels = new List<WmiBacklight>();
            try
            {
                using (ManagementObjectSearcher searcher =
                    new ManagementObjectSearcher(Scope, "SELECT InstanceName FROM WmiMonitorBrightnessMethods"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject panel in results)
                        using (panel)
                        {
                            string name = panel["InstanceName"] as string;
                            if (!string.IsNullOrEmpty(name)) panels.Add(new WmiBacklight(name));
                        }
                }
            }
            catch (ManagementException) { }        // class absent: no controllable panel
            catch (UnauthorizedAccessException) { }
            return panels;
        }

        public bool TryRead(out int percent)
        {
            percent = 100;
            try
            {
                string query = "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE InstanceName='"
                             + EscapeWql(_instanceName) + "'";
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(Scope, query))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject panel in results)
                        using (panel)
                        {
                            percent = Clamp(Convert.ToInt32(panel["CurrentBrightness"], CultureInfo.InvariantCulture));
                            return true;
                        }
                }
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        public void Write(int percent)
        {
            if (_methods == null)
            {
                _methods = new ManagementObject(Scope,
                    "WmiMonitorBrightnessMethods.InstanceName='" + EscapeWql(_instanceName) + "'", null);
            }
            // WmiSetBrightness(Timeout seconds, Brightness 0-100)
            _methods.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Clamp(percent) });
        }

        /// <summary>Drops the cached method object; the next write makes a fresh one.</summary>
        public void Reacquire()
        {
            if (_methods == null) return;
            _methods.Dispose();
            _methods = null;
        }

        public void Dispose()
        {
            Reacquire();
        }

        /// <summary>
        /// Turns <c>DISPLAY\LGD05C0\4&amp;1a2b&amp;0&amp;UID8388688_0</c> into
        /// <c>LGD05C0\4&amp;1A2B&amp;0&amp;UID8388688</c>, matching what the display adapter reports.
        /// </summary>
        private static string NormalizeInstanceName(string instanceName)
        {
            string key = instanceName;

            int firstSeparator = key.IndexOf('\\');
            if (firstSeparator >= 0) key = key.Substring(firstSeparator + 1);

            int suffix = key.LastIndexOf('_');
            if (suffix > 0)
            {
                bool digitsOnly = suffix < key.Length - 1;
                for (int i = suffix + 1; i < key.Length && digitsOnly; i++)
                    digitsOnly = char.IsDigit(key[i]);
                if (digitsOnly) key = key.Substring(0, suffix);
            }
            return key.ToUpperInvariant();
        }

        private static string EscapeWql(string value)
        {
            return value.Replace("\\", "\\\\").Replace("'", "\\'");
        }

        internal static int Clamp(int percent)
        {
            return percent < 0 ? 0 : (percent > 100 ? 100 : percent);
        }
    }

    /// <summary>External monitors that answer DDC/CI brightness commands.</summary>
    internal sealed class DdcBackend : IBrightnessBackend
    {
        private readonly IntPtr _screen;
        private uint _minimum;
        private uint _range;
        private IntPtr _handle;

        private DdcBackend(IntPtr screen, IntPtr handle, string description, uint minimum, uint maximum)
        {
            _screen = screen;
            _handle = handle;
            _minimum = minimum;
            _range = maximum - minimum;
            Description = description;
        }

        public string Description { get; private set; }

        public BrightnessKind Kind { get { return BrightnessKind.Ddc; } }

        /// <summary>Probes a monitor and returns a backend only if it truly answers. Any physical
        /// monitor handles that go unused are destroyed here.</summary>
        public static DdcBackend TryCreate(IntPtr hMonitor)
        {
            uint count = 0;
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref count) || count == 0) return null;

            Native.PHYSICAL_MONITOR[] monitors = new Native.PHYSICAL_MONITOR[count];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors)) return null;

            DdcBackend backend = null;
            foreach (Native.PHYSICAL_MONITOR monitor in monitors)
            {
                uint minimum = 0, current = 0, maximum = 0;
                if (backend == null
                    && Native.GetMonitorBrightness(monitor.hPhysicalMonitor, ref minimum, ref current, ref maximum)
                    && maximum > minimum)
                {
                    backend = new DdcBackend(hMonitor, monitor.hPhysicalMonitor,
                        monitor.szPhysicalMonitorDescription, minimum, maximum);
                }
                else
                {
                    Native.DestroyPhysicalMonitor(monitor.hPhysicalMonitor);
                }
            }
            return backend;
        }

        /// <summary>
        /// A monitor answers over a slow serial link and drops the occasional query, so one
        /// refusal is not an answer. Past these attempts it really is not talking.
        /// </summary>
        private const int ReadAttempts = 3;
        private const int ReadPauseMilliseconds = 60;

        public bool TryRead(out int percent)
        {
            percent = 100;
            for (int attempt = 1; ; attempt++)
            {
                uint minimum = 0, current = 0, maximum = 0;
                if (Native.GetMonitorBrightness(_handle, ref minimum, ref current, ref maximum)
                    && maximum > minimum)
                {
                    percent = WmiBacklight.Clamp(
                        (int)Math.Round((current - (double)minimum) * 100.0 / (maximum - minimum)));
                    return true;
                }
                if (attempt >= ReadAttempts) return false;
                Thread.Sleep(ReadPauseMilliseconds);
            }
        }

        public void Write(int percent)
        {
            uint value = _minimum + (uint)Math.Round(_range * WmiBacklight.Clamp(percent) / 100.0);
            if (!Native.SetMonitorBrightness(_handle, value))
                throw new InvalidOperationException("The monitor rejected a DDC/CI brightness command.");
        }

        /// <summary>
        /// Asks the monitor for a new physical handle. The old one survives a display being
        /// powered off in name only: commands sent to it are accepted and ignored.
        /// </summary>
        public void Reacquire()
        {
            IntPtr stale = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (stale != IntPtr.Zero) Native.DestroyPhysicalMonitor(stale);

            uint count = 0;
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(_screen, ref count) || count == 0) return;

            Native.PHYSICAL_MONITOR[] monitors = new Native.PHYSICAL_MONITOR[count];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(_screen, count, monitors)) return;

            _handle = monitors[0].hPhysicalMonitor;
            for (int i = 1; i < monitors.Length; i++)
                Native.DestroyPhysicalMonitor(monitors[i].hPhysicalMonitor);

            uint minimum = 0, current = 0, maximum = 0;
            if (Native.GetMonitorBrightness(_handle, ref minimum, ref current, ref maximum) && maximum > minimum)
            {
                _minimum = minimum;
                _range = maximum - minimum;
            }
        }

        public void Dispose()
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero) Native.DestroyPhysicalMonitor(handle);
        }
    }

    /// <summary>
    /// The universal fallback: a black, click-through, always-on-top window over the display.
    /// It does not save power, but it works on every display Windows can draw on.
    /// </summary>
    internal sealed class OverlayBackend : IBrightnessBackend
    {
        /// <summary>Never go fully black - a hidden screen is indistinguishable from a broken one.</summary>
        private const double MaximumDimming = 0.92;

        private readonly Control _uiThread;
        private readonly Rectangle _bounds;
        private OverlayForm _window;
        private int _percent = 100;

        public OverlayBackend(Control uiThread, Rectangle bounds)
        {
            _uiThread = uiThread;
            _bounds = bounds;
        }

        public BrightnessKind Kind { get { return BrightnessKind.Overlay; } }

        /// <summary>An overlay never darkens anything until we ask it to, so its rest state is 100.</summary>
        public bool TryRead(out int percent)
        {
            percent = 100;
            return true;
        }

        public void Write(int percent)
        {
            _percent = WmiBacklight.Clamp(percent);
            if (_uiThread.InvokeRequired) _uiThread.Invoke(new MethodInvoker(Apply));
            else Apply();
        }

        private void Apply()
        {
            if (_percent >= 100)
            {
                if (_window != null) _window.Visible = false;
                return;
            }

            if (_window == null) _window = new OverlayForm(_bounds);
            _window.Opacity = Math.Min(MaximumDimming, 1.0 - _percent / 100.0);
            if (!_window.Visible) _window.Show();

            // Something else may have taken the top spot since we last showed.
            Native.SetWindowPos(_window.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        /// <summary>An overlay owns no handle that anything else can invalidate.</summary>
        public void Reacquire()
        {
        }

        public void Dispose()
        {
            if (_window == null) return;
            OverlayForm window = _window;
            _window = null;
            if (_uiThread.InvokeRequired) _uiThread.Invoke(new MethodInvoker(window.Dispose));
            else window.Dispose();
        }

        private sealed class OverlayForm : Form
        {
            public OverlayForm(Rectangle bounds)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.Black;
                Opacity = 0;
                Bounds = bounds;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT
                                        | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                    return parameters;
                }
            }
        }
    }

    // ------------------------------------------------------------------ target

    /// <summary>One display, plus the brightness channel that turned out to work for it.</summary>
    public sealed class DisplayTarget : IDisposable
    {
        /// <summary>DDC/CI quantises, so a read rarely returns the exact number written.</summary>
        private const int VerifyTolerance = 3;

        /// <summary>Long enough for a monitor to have acted before its brightness is read back.</summary>
        private const int SettleMilliseconds = 150;

        private readonly Func<IBrightnessBackend> _overlayFactory;
        private IBrightnessBackend _backend;
        private bool _degraded;

        internal DisplayTarget(string key, string name, string model, Rectangle bounds, bool isPrimary,
                               IBrightnessBackend backend, Func<IBrightnessBackend> overlayFactory)
        {
            Key = key;
            Name = name;
            Model = model;
            Bounds = bounds;
            IsPrimary = isPrimary;
            _backend = backend;
            _overlayFactory = overlayFactory;
        }

        /// <summary>Stable across reboots and reconnects, so per-display choices survive.</summary>
        public string Key { get; private set; }
        public string Name { get; private set; }
        public string Model { get; private set; }
        public Rectangle Bounds { get; private set; }
        public bool IsPrimary { get; private set; }

        public BrightnessKind Kind { get { return _backend.Kind; } }

        /// <summary>True once hardware control failed and the overlay took over.</summary>
        public bool Degraded { get { return _degraded; } }

        /// <summary>Brightness recorded the moment we dimmed; null while the user is present.</summary>
        public int? Captured { get; set; }

        /// <summary>The last value written, so an interrupted fade knows where it stopped.</summary>
        public int? Applied { get; set; }

        /// <summary>
        /// The level this display should be sitting at with the user present. Kept after a
        /// restore has been let go of, so that a display switched off by Windows and brought
        /// back dim can be put right without anything left to compare against.
        /// </summary>
        public int? LastAwakeLevel { get; set; }

        public bool TryRead(out int percent)
        {
            try { return _backend.TryRead(out percent); }
            catch (Exception) { percent = 100; return false; }
        }

        public void Write(int percent)
        {
            try
            {
                _backend.Write(percent);
                return;
            }
            catch (Exception) { }

            // A display that has just come back from being powered off hands out new handles,
            // and the old ones fail. Try again with fresh ones before writing the hardware off:
            // giving up here used to strand a perfectly good monitor on the overlay for good.
            try
            {
                _backend.Reacquire();
                _backend.Write(percent);
                return;
            }
            catch (Exception)
            {
                // Monitors that advertise DDC/CI but reject writes are common enough that
                // silently switching to the overlay is better than silently doing nothing.
                if (_degraded || _overlayFactory == null) throw;
            }

            _degraded = true;
            IBrightnessBackend replaced = _backend;
            _backend = _overlayFactory();
            replaced.Dispose();
            _backend.Write(percent);
        }

        /// <summary>Re-establishes the display's handle after a sleep or a power cycle.</summary>
        public void Reacquire()
        {
            try { _backend.Reacquire(); }
            catch (Exception) { }
        }

        /// <summary>
        /// Writes a level and proves it took effect, which matters most when putting brightness
        /// back. A monitor that has just been powered on will accept a command against a stale
        /// handle and do nothing with it, reporting success either way - so a restore that is
        /// merely sent is not a restore. Returns false if the display cannot be made to agree.
        /// </summary>
        public bool TryWriteVerified(int percent)
        {
            // An overlay reports the hardware behind it, never its own dimming, so there is
            // nothing to read back and compare against.
            if (Kind == BrightnessKind.Overlay)
            {
                try { Write(percent); return true; }
                catch (Exception) { return false; }
            }

            if (Confirm(percent)) return true;
            Reacquire();
            return Confirm(percent);
        }

        private bool Confirm(int percent)
        {
            int actual;
            if (TryRead(out actual) && Math.Abs(actual - percent) <= VerifyTolerance) return true;

            try { Write(percent); }
            catch (Exception) { return false; }

            Thread.Sleep(SettleMilliseconds);
            return TryRead(out actual) && Math.Abs(actual - percent) <= VerifyTolerance;
        }

        public void Dispose()
        {
            _backend.Dispose();
        }
    }

    // ----------------------------------------------------------------- manager

    /// <summary>Enumerates displays and picks the best brightness channel for each.</summary>
    public sealed class DisplayManager : IDisposable
    {
        private readonly Control _uiThread;
        private List<DisplayTarget> _targets = new List<DisplayTarget>();

        public DisplayManager(Control uiThread)
        {
            _uiThread = uiThread;
        }

        /// <summary>The current displays. Replaced wholesale by <see cref="Refresh"/>.</summary>
        public IList<DisplayTarget> Targets { get { return _targets; } }

        /// <summary>Rebuilds the display list. Talks to WMI and DDC/CI, so call it off the UI thread.</summary>
        public void Refresh()
        {
            List<WmiBacklight> panels = WmiBacklight.Discover();
            List<Screen> screens = EnumerateScreens();
            List<DisplayTarget> rebuilt = new List<DisplayTarget>();

            foreach (Screen screen in screens)
            {
                string pnpKey, model;
                Native.DescribeMonitor(screen.AdapterName, out pnpKey, out model);

                IBrightnessBackend backend = TakePanel(panels, pnpKey);
                if (backend == null) backend = DdcBackend.TryCreate(screen.Handle);

                DdcBackend ddc = backend as DdcBackend;
                if (ddc != null && !string.IsNullOrEmpty(ddc.Description)) model = ddc.Description;

                Rectangle bounds = screen.Bounds;
                Func<IBrightnessBackend> overlayFactory = MakeOverlayFactory(bounds);
                if (backend == null) { backend = overlayFactory(); overlayFactory = null; }

                rebuilt.Add(new DisplayTarget(
                    string.IsNullOrEmpty(pnpKey) ? screen.AdapterName : pnpKey,
                    DisplayName(screen.AdapterName, rebuilt.Count),
                    Describe(model), bounds, screen.IsPrimary, backend, overlayFactory));
            }

            // A laptop panel whose adapter identity could not be matched is still a laptop panel:
            // if exactly one is unclaimed and exactly one display lacks hardware control, pair them.
            AdoptOrphanPanel(panels, rebuilt);

            rebuilt.Sort(delegate(DisplayTarget a, DisplayTarget b)
            {
                if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
                if (a.Bounds.X != b.Bounds.X) return a.Bounds.X.CompareTo(b.Bounds.X);
                return a.Bounds.Y.CompareTo(b.Bounds.Y);
            });

            List<DisplayTarget> previous = Interlocked.Exchange(ref _targets, rebuilt);
            foreach (DisplayTarget target in previous) target.Dispose();
            foreach (WmiBacklight unused in panels) unused.Dispose();
        }

        private Func<IBrightnessBackend> MakeOverlayFactory(Rectangle bounds)
        {
            Control ui = _uiThread;
            return delegate { return new OverlayBackend(ui, bounds); };
        }

        private static WmiBacklight TakePanel(List<WmiBacklight> panels, string pnpKey)
        {
            if (string.IsNullOrEmpty(pnpKey)) return null;
            for (int i = 0; i < panels.Count; i++)
            {
                if (!string.Equals(panels[i].PnpKey, pnpKey, StringComparison.OrdinalIgnoreCase)) continue;
                WmiBacklight panel = panels[i];
                panels.RemoveAt(i);
                return panel;
            }
            return null;
        }

        private void AdoptOrphanPanel(List<WmiBacklight> panels, List<DisplayTarget> targets)
        {
            if (panels.Count != 1) return;

            DisplayTarget candidate = null;
            foreach (DisplayTarget target in targets)
            {
                if (target.Kind != BrightnessKind.Overlay) continue;
                if (candidate != null) return;     // ambiguous - leave everything alone
                candidate = target;
            }
            if (candidate == null) return;

            int index = targets.IndexOf(candidate);
            targets[index] = new DisplayTarget(candidate.Key, candidate.Name, candidate.Model,
                candidate.Bounds, candidate.IsPrimary, panels[0], MakeOverlayFactory(candidate.Bounds));
            candidate.Dispose();
            panels.Clear();
        }

        private static string DisplayName(string adapterName, int index)
        {
            // \\.\DISPLAY3 -> "Display 3"; anything unexpected falls back to position.
            int digits = adapterName.Length;
            while (digits > 0 && char.IsDigit(adapterName[digits - 1])) digits--;
            string number = adapterName.Substring(digits);
            return "Display " + (number.Length > 0 ? number : (index + 1).ToString(CultureInfo.InvariantCulture));
        }

        private static string Describe(string model)
        {
            if (string.IsNullOrEmpty(model)) return null;
            model = model.Trim();
            // These tell the user nothing they cannot already see.
            if (model == "Generic PnP Monitor" || model == "Generic Non-PnP Monitor"
                || model == "Default Monitor" || model == "Primary Monitor") return null;
            return model;
        }

        private struct Screen
        {
            public IntPtr Handle;
            public string AdapterName;
            public Rectangle Bounds;
            public bool IsPrimary;
        }

        private static List<Screen> EnumerateScreens()
        {
            List<Screen> screens = new List<Screen>();
            Native.MonitorEnumProc callback = delegate(IntPtr handle, IntPtr hdc, ref Native.RECT clip, IntPtr data)
            {
                Native.MONITORINFOEX info = Native.MONITORINFOEX.Create();
                if (Native.GetMonitorInfo(handle, ref info))
                {
                    Screen screen = new Screen();
                    screen.Handle = handle;
                    screen.AdapterName = info.szDevice;
                    screen.Bounds = info.rcMonitor.ToRectangle();
                    screen.IsPrimary = info.IsPrimary;
                    screens.Add(screen);
                }
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            return screens;
        }

        public void Dispose()
        {
            foreach (DisplayTarget target in _targets) target.Dispose();
            _targets = new List<DisplayTarget>();
        }
    }
}
