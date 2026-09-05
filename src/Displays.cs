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
        /// Re-establishes whatever this backend holds. <paramref name="screen"/> is the display
        /// as Windows has just enumerated it, or IntPtr.Zero to keep the one already known.
        /// </summary>
        void Reacquire(IntPtr screen);
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
        public void Reacquire(IntPtr screen)
        {
            if (_methods == null) return;
            _methods.Dispose();
            _methods = null;
        }

        public void Dispose()
        {
            Reacquire(IntPtr.Zero);
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
        private IntPtr _screen;
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
        ///
        /// The screen it is asked through matters as much as the handle itself. An HMONITOR is
        /// only good until the display configuration changes, and switching every monitor off
        /// and on again changes it - Windows sends WM_DISPLAYCHANGE twice on the way down and
        /// twice on the way back. Asking through the one captured before all that fails for
        /// good, which is how a monitor ends up unreachable until the displays are rescanned by
        /// hand. So the caller passes the screen as Windows has just enumerated it.
        /// </summary>
        public void Reacquire(IntPtr screen)
        {
            if (screen != IntPtr.Zero) _screen = screen;

            IntPtr stale = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (stale != IntPtr.Zero) Native.DestroyPhysicalMonitor(stale);

            uint count = 0;
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(_screen, ref count) || count == 0) return;

            Native.PHYSICAL_MONITOR[] monitors = new Native.PHYSICAL_MONITOR[count];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(_screen, count, monitors)) return;

            _handle = monitors[0].hPhysicalMonitor;
            for (int i = 1; i < monitors.Length; i++)
                Native.DestroyPhysicalMonitor(monitors[i].hPhysicalMonitor);

            // The scale this monitor reports is not asked for again. It is the same monitor, so
            // it is the same scale, and asking costs a full round trip over a slow serial link -
            // on a path that runs every time the screen switches off. Whether the new handle is
            // any good is settled by the write that follows, which is read back and retried
            // until the display agrees; a question here would prove nothing that does not.
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
        public void Reacquire(IntPtr screen)
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
            ScreenOn = true;
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

        /// <summary>
        /// Whether Windows currently has the screen switched on. A display being powered down
        /// refuses everything, and refusals from a dark screen are not evidence about it.
        /// </summary>
        public bool ScreenOn { get; set; }

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
                _backend.Reacquire(IntPtr.Zero);
                _backend.Write(percent);
                return;
            }
            catch (Exception)
            {
                // Monitors that advertise DDC/CI but reject writes are common enough that
                // silently switching to the overlay is better than silently doing nothing.
                //
                // But a monitor Windows has just powered down rejects everything, and that says
                // nothing about what it can do awake. Giving up there condemns a working display
                // to a black overlay for the rest of the session - which is exactly what happens
                // around a screen timeout, as Windows dims and then switches off each monitor in
                // turn. Refusals from a dark screen are not evidence.
                if (_degraded || _overlayFactory == null || !ScreenOn) throw;
            }

            _degraded = true;
            IBrightnessBackend replaced = _backend;
            _backend = _overlayFactory();
            replaced.Dispose();
            _backend.Write(percent);
        }

        /// <summary>
        /// Re-establishes the display's handle after a sleep or a power cycle. Pass the screen
        /// as Windows has just enumerated it whenever that is known: the one from before a
        /// power cycle is no longer valid, and asking through it fails permanently.
        /// </summary>
        public void Reacquire(IntPtr screen)
        {
            try { _backend.Reacquire(screen); }
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
            Reacquire(IntPtr.Zero);
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

        /// <summary>
        /// Whether Windows has the screen switched on. Passed down to every display, because a
        /// monitor that is powered down refuses everything and must not be judged on that.
        /// </summary>
        public bool ScreenOn
        {
            get { return _screenOn; }
            set
            {
                _screenOn = value;
                foreach (DisplayTarget target in _targets) target.ScreenOn = value;
            }
        }

        private bool _screenOn = true;

        /// <summary>Runs work on the thread that owns the overlay windows.</summary>
        public void OnUiThread(MethodInvoker work)
        {
            if (_uiThread == null || _uiThread.IsDisposed || !_uiThread.IsHandleCreated) return;
            try { _uiThread.BeginInvoke(work); }
            catch (Exception) { }
        }

        /// <summary>
        /// Re-establishes every display after the screen has been switched off and on. Handles
        /// taken before the power-off are dead in all but name, so each is taken again.
        ///
        /// When the same displays are still attached nothing is torn down: the objects the rest
        /// of the app is holding stay valid, and so does the level each display is waiting to be
        /// put back to. That skips the WMI query, the DDC/CI probe of every monitor and the
        /// rebuilding of the overlay windows - which is worth having on a path that runs every
        /// single time the screen switches off. Anything else falls back to a full rebuild.
        /// </summary>
        /// <returns>True when the list of displays itself changed.</returns>
        public bool Reacquire()
        {
            List<Screen> screens = EnumerateScreens();
            if (!SameDisplays(screens))
            {
                Refresh();
                return true;
            }

            foreach (DisplayTarget target in _targets)
                target.Reacquire(ScreenFor(screens, target));
            return false;
        }

        /// <summary>
        /// Whether the displays now attached are the ones already known - same identities, same
        /// places, same primary. Identity alone is not enough: a monitor that has been moved
        /// needs its overlay rebuilt over the right part of the desktop.
        /// </summary>
        private bool SameDisplays(List<Screen> screens)
        {
            List<DisplayTarget> known = _targets;
            if (screens.Count != known.Count || screens.Count == 0) return false;

            foreach (Screen screen in screens)
            {
                string pnpKey, model;
                Native.DescribeMonitor(screen.AdapterName, out pnpKey, out model);
                string key = string.IsNullOrEmpty(pnpKey) ? screen.AdapterName : pnpKey;

                bool found = false;
                foreach (DisplayTarget target in known)
                {
                    if (!string.Equals(target.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (target.Bounds != screen.Bounds || target.IsPrimary != screen.IsPrimary) return false;
                    found = true;
                    break;
                }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether every display is answering yet. A monitor in power save answers nothing at
        /// all, so this is the difference between the screen being on as Windows reports it and
        /// on as the monitor sees it. Handles are taken again first: one from before the
        /// power-off would answer for the monitor rather than from it.
        ///
        /// A display Dimly covers with an overlay has nothing to ask, and is always ready.
        /// </summary>
        public bool AllAnswering()
        {
            List<Screen> screens = EnumerateScreens();
            foreach (DisplayTarget target in _targets)
            {
                target.Reacquire(ScreenFor(screens, target));

                int level;
                if (!target.TryRead(out level)) return false;
            }
            return true;
        }

        /// <summary>
        /// The display as Windows has it now, or IntPtr.Zero if it is no longer there. Screens
        /// are matched by identity rather than by position in the list: the order Windows
        /// enumerates them in is not promised to survive a power cycle.
        /// </summary>
        private static IntPtr ScreenFor(List<Screen> screens, DisplayTarget target)
        {
            foreach (Screen screen in screens)
            {
                string pnpKey, model;
                Native.DescribeMonitor(screen.AdapterName, out pnpKey, out model);
                string key = string.IsNullOrEmpty(pnpKey) ? screen.AdapterName : pnpKey;

                if (string.Equals(key, target.Key, StringComparison.OrdinalIgnoreCase))
                    return screen.Handle;
            }
            return IntPtr.Zero;
        }

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

            // A rebuild can happen while displays are dimmed - the screen coming back after a
            // power-off is exactly such a moment - and the level each one is waiting to be put
            // back to lives on the target being replaced. Carry it over, or the new target
            // knows nothing and the screen is left dim.
            foreach (DisplayTarget fresh in rebuilt) fresh.ScreenOn = _screenOn;

            List<DisplayTarget> previous = Interlocked.Exchange(ref _targets, rebuilt);
            foreach (DisplayTarget fresh in rebuilt)
                foreach (DisplayTarget old in previous)
                {
                    if (!string.Equals(fresh.Key, old.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    fresh.Captured = old.Captured;
                    fresh.Applied = old.Applied;
                    fresh.LastAwakeLevel = old.LastAwakeLevel;
                    break;
                }

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
