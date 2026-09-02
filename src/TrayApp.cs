using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Dimly
{
    /// <summary>
    /// The resident application: a tray icon, a hidden window that owns the message loop, and
    /// the engine behind them. The settings window is created lazily and only ever hidden.
    /// </summary>
    public sealed class TrayApp : ApplicationContext
    {
        private readonly AppSettings _settings;
        /// <summary>The name a second copy of Dimly looks for to reach this one.</summary>
        internal static string MessageWindowTitle { get { return MessageWindow.Title; } }

        private readonly MessageWindow _messages;
        private readonly DisplayManager _displays;
        private readonly MediaWatcher _media;
        private readonly ActivityWatcher _activity;
        private readonly DimEngine _engine;
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _pauseItem;
        private readonly ToolStripMenuItem _dimItem;
        private readonly ToolStripMenuItem _statusItem;

        private SettingsWindow _window;
        private bool _exiting;

        public TrayApp(AppSettings settings, bool startHidden)
        {
            _settings = settings;

            _messages = new MessageWindow(this);
            _displays = new DisplayManager(_messages);
            _media = new MediaWatcher();
            _activity = new ActivityWatcher();
            _engine = new DimEngine(settings, _displays, _media, _activity);

            _statusItem = new ToolStripMenuItem(AppInfo.Name);
            _statusItem.Enabled = false;
            _statusItem.Font = new Font(SystemFonts.MenuFont, FontStyle.Bold);

            _dimItem = new ToolStripMenuItem("Dim now", null,
                delegate { _engine.Overridden = !_engine.Overridden; });
            _pauseItem = new ToolStripMenuItem("Pause dimming", null, delegate { _engine.Paused = !_engine.Paused; });

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.Renderer = new TrayMenuRenderer();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Open Dimly", null, delegate { OpenWindow(); }));
            menu.Items.Add(_dimItem);
            menu.Items.Add(_pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { Quit(); }));

            _tray = new NotifyIcon();
            _tray.Icon = AppInfo.Icon(SystemInformation.SmallIconSize.Width);
            _tray.Text = AppInfo.Name;
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { OpenWindow(); };
            _tray.Visible = true;

            _engine.Changed += delegate { RefreshTray(); };

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.SessionEnding += OnSessionEnding;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // Enumerating monitors talks to WMI and DDC/CI, which can take a second on some
            // machines. Do it off the UI thread so the tray icon appears immediately.
            Task.Factory.StartNew(delegate
            {
                _displays.Refresh();
                _messages.BeginInvoke(new MethodInvoker(delegate
                {
                    _engine.Start();
                    RefreshTray();
                    if (_window == null) Native.TrimMemory();
                }));
            });

            if (!startHidden) OpenWindow();
        }

        private void OpenWindow()
        {
            if (_window == null || _window.IsDisposed)
            {
                _window = new SettingsWindow(_settings, _engine, _displays, RefreshTray);
                _window.Hidden += delegate { HintWhereItWent(); };
            }
            _window.Summon();
        }

        /// <summary>
        /// Closing the window only hides it, and the first time that happens the app appears to
        /// have vanished. Say where it went - once, ever, rather than every time.
        /// </summary>
        private void HintWhereItWent()
        {
            if (_settings.TrayHintShown || _exiting) return;

            _settings.TrayHintShown = true;
            _settings.Save();

            _tray.BalloonTipTitle = AppInfo.Name + " is still running";
            _tray.BalloonTipText = "It is down here in the tray. Double-click to open it again.";
            _tray.ShowBalloonTip(5000);
        }

        private void RefreshTray()
        {
            string state;
            if (_engine.Paused) state = "paused";
            else if (_engine.Overridden) state = "dimmed by hand";
            else if (_engine.State == DimState.Dimmed) state = "dimmed";
            else if (_engine.HeldByMedia) state = "holding off, media is playing";
            else state = "watching, dims after " + DelayScale.Humanize(_settings.IdleSeconds);

            _tray.Text = Truncate(AppInfo.Name + " - " + state, 63);
            _statusItem.Text = AppInfo.Name + " - " + state;
            _pauseItem.Text = _engine.Paused ? "Resume dimming" : "Pause dimming";
            _dimItem.Text = _engine.Overridden ? "Restore brightness" : "Dim now";
            _dimItem.Enabled = !_engine.Paused;
        }

        /// <summary>NotifyIcon.Text is limited to 63 characters and throws beyond it.</summary>
        private static string Truncate(string text, int limit)
        {
            return text.Length <= limit ? text : text.Substring(0, limit - 1) + "…";
        }

        // ------------------------------------------------------------- system

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) _engine.SetLocked(true);
            else if (e.Reason == SessionSwitchReason.SessionUnlock) _engine.SetLocked(false);
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            // Never leave a signed-out or shutting-down machine with our brightness applied.
            _engine.ShutdownRestore();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) _engine.OnResume();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            _engine.ReloadDisplays();
        }

        /// <summary>
        /// Windows switched the screen back on after its display timeout. That is not a sleep,
        /// so this is the only warning Dimly gets that every monitor handle it holds is stale.
        /// </summary>
        private void OnDisplayPowerOn()
        {
            _engine.OnDisplayPowerOn();
        }

        /// <summary>Called when another copy of Dimly is launched.</summary>
        internal void OnSecondInstance()
        {
            OpenWindow();
        }

        /// <summary>Last-ditch restore when the process is going down unexpectedly.</summary>
        internal void PanicRestore()
        {
            try { _engine.ShutdownRestore(); }
            catch (Exception) { }
            try { _tray.Visible = false; }
            catch (Exception) { }
        }

        private void Quit()
        {
            if (_exiting) return;
            _exiting = true;

            _tray.Visible = false;
            _engine.ShutdownRestore();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.SessionEnding -= OnSessionEnding;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

                if (_window != null) _window.Dispose();
                _engine.Dispose();
                _media.Dispose();
                _activity.Dispose();
                _displays.Dispose();
                _tray.Dispose();
                _messages.Dispose();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------- message window

        /// <summary>
        /// An invisible window that outlives the settings window. It owns the UI thread for
        /// overlay dimmers and receives the "another instance started" broadcast.
        /// </summary>
        private sealed class MessageWindow : Form
        {
            /// <summary>
            /// What a second copy of Dimly looks for to hand its request over. A broadcast was
            /// tried first and is not delivered here, so the window is addressed by name.
            /// </summary>
            public const string Title = "Dimly.MessageWindow.6F1B";


            private readonly TrayApp _owner;
            private IntPtr _displayNotice;

            private bool _displayWasOn = true;

            public MessageWindow(TrayApp owner)
            {
                _owner = owner;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
                Size = new Size(1, 1);
                Text = Title;

                // Force the handle so background threads can marshal onto this thread at once.
                IntPtr handle = Handle;
                GC.KeepAlive(handle);

                Guid setting = Native.GUID_CONSOLE_DISPLAY_STATE;
                _displayNotice = Native.RegisterPowerSettingNotification(handle, ref setting, 0);
            }

            protected override void SetVisibleCore(bool value)
            {
                base.SetVisibleCore(false);
            }

            protected override void WndProc(ref Message m)
            {
                if (Program.SecondInstanceMessage != 0 && m.Msg == Program.SecondInstanceMessage)
                    _owner.OnSecondInstance();
                else if (m.Msg == Native.WM_POWERBROADCAST && (int)m.WParam == Native.PBT_POWERSETTINGCHANGE)
                    OnPowerSetting(m.LParam);
                base.WndProc(ref m);
            }

            /// <summary>
            /// Windows reports the display state as off, on, or dimmed by its own timeout. Only
            /// the change from off back to on matters, and it is announced on every state
            /// change - including at startup - so the edge is what is acted on, not the value.
            /// </summary>
            private void OnPowerSetting(IntPtr data)
            {
                Native.POWERBROADCAST_SETTING setting;
                try
                {
                    setting = (Native.POWERBROADCAST_SETTING)Marshal.PtrToStructure(
                        data, typeof(Native.POWERBROADCAST_SETTING));
                }
                catch (Exception) { return; }

                if (setting.PowerSetting != Native.GUID_CONSOLE_DISPLAY_STATE) return;

                bool on = setting.Data != 0;
                bool wasOn = _displayWasOn;
                _displayWasOn = on;

                if (on && !wasOn) _owner.OnDisplayPowerOn();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && _displayNotice != IntPtr.Zero)
                {
                    Native.UnregisterPowerSettingNotification(_displayNotice);
                    _displayNotice = IntPtr.Zero;
                }
                base.Dispose(disposing);
            }
        }

        // --------------------------------------------------------- menu styling

        /// <summary>Paints the tray menu in the chosen theme instead of the Windows default.</summary>
        private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
        {
            public TrayMenuRenderer() : base(new TrayColours()) { }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                Theme theme = Theme.Current;
                if (!e.Item.Enabled) e.TextColor = theme.TextFaint;
                else e.TextColor = e.Item.Selected ? theme.OnAccent : theme.Text;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (SolidBrush brush = new SolidBrush(Theme.Current.Card))
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Current.Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }

            private sealed class TrayColours : ProfessionalColorTable
            {
                public override Color MenuItemSelected { get { return Theme.Current.Accent; } }
                public override Color MenuItemSelectedGradientBegin { get { return Theme.Current.Accent; } }
                public override Color MenuItemSelectedGradientEnd { get { return Theme.Current.Accent; } }
                public override Color MenuItemBorder { get { return Theme.Current.Accent; } }
                public override Color MenuBorder { get { return Theme.Current.Border; } }
                public override Color ToolStripDropDownBackground { get { return Theme.Current.Card; } }
                public override Color ImageMarginGradientBegin { get { return Theme.Current.Card; } }
                public override Color ImageMarginGradientMiddle { get { return Theme.Current.Card; } }
                public override Color ImageMarginGradientEnd { get { return Theme.Current.Card; } }
                public override Color SeparatorDark { get { return Theme.Current.Border; } }
                public override Color SeparatorLight { get { return Theme.Current.Border; } }
            }
        }
    }
}
