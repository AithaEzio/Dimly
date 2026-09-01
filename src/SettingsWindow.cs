using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Dimly
{
    /// <summary>What the pages are allowed to ask of the application.</summary>
    public interface IShell
    {
        AppSettings Settings { get; }
        DimEngine Engine { get; }
        DisplayManager Displays { get; }

        /// <summary>Writes settings to disk and re-applies them to any display that is already dimmed.</summary>
        void Persist();

        void UseTheme(Theme theme);
    }

    /// <summary>
    /// The idle delay slider's scale: geometric, so that five seconds and five minutes both get
    /// a usable share of the track instead of everything short bunching up at the left.
    /// </summary>
    internal static class DelayScale
    {
        public const int Positions = 100;

        public static int ToSeconds(int position)
        {
            double ratio = (double)AppSettings.MaxIdleSeconds / AppSettings.MinIdleSeconds;
            double seconds = AppSettings.MinIdleSeconds * Math.Pow(ratio, position / (double)Positions);

            int step = seconds < 60 ? 1 : (seconds < 600 ? 5 : 30);
            int rounded = (int)(Math.Round(seconds / step) * step);
            return Math.Max(AppSettings.MinIdleSeconds, Math.Min(AppSettings.MaxIdleSeconds, rounded));
        }

        public static int ToPosition(int seconds)
        {
            double ratio = (double)AppSettings.MaxIdleSeconds / AppSettings.MinIdleSeconds;
            double position = Math.Log(seconds / (double)AppSettings.MinIdleSeconds) / Math.Log(ratio) * Positions;
            return Math.Max(0, Math.Min(Positions, (int)Math.Round(position)));
        }

        public static string Humanize(int seconds)
        {
            if (seconds < 60) return seconds + " seconds";

            int minutes = seconds / 60;
            int rest = seconds % 60;
            string text = minutes + (minutes == 1 ? " minute" : " minutes");
            if (rest > 0) text += " " + rest + " seconds";
            return text;
        }
    }

    /// <summary>A fixed patch of artwork, drawn from the embedded icon.</summary>
    internal sealed class MarkBox : ThemedControl
    {
        public MarkBox(int size)
        {
            Size = new Size(size, size);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Ui.Quality(e.Graphics);
            e.Graphics.DrawImage(AppInfo.Mark(Width), 0, 0, Width, Height);
        }
    }

    // ------------------------------------------------------------------- shell

    public sealed class SettingsWindow : Form, IShell
    {
        private const int DesignWidth = 900;
        private const int DesignHeight = 840;
        private const int SidebarWidth = 210;
        private const int CaptionHeight = 54;
        private const int PagePadding = 28;

        private readonly AppSettings _settings;
        private readonly DimEngine _engine;
        private readonly DisplayManager _displays;
        private readonly Action _onSettingsSaved;

        private readonly Sidebar _sidebar;
        private readonly CaptionBar _caption;
        private readonly Panel _content;
        private readonly List<Page> _pages = new List<Page>();
        private readonly Timer _statusTimer;

        private Page _active;

        public SettingsWindow(AppSettings settings, DimEngine engine, DisplayManager displays, Action onSettingsSaved)
        {
            _settings = settings;
            _engine = engine;
            _displays = displays;
            _onSettingsSaved = onSettingsSaved;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Text = AppInfo.Name;
            Icon = AppInfo.Icon(32);
            KeyPreview = true;
            ShowInTaskbar = true;
            ClientSize = new Size(Ui.Px(DesignWidth), Ui.Px(DesignHeight));
            BackColor = Theme.Current.Window;

            _sidebar = new Sidebar(this);
            _sidebar.SetBounds(0, 0, Ui.Px(SidebarWidth), ClientSize.Height);
            _sidebar.NavigateTo += delegate(object sender, NavigationEventArgs e) { ShowPage(_pages[e.Index]); };
            _sidebar.DragRequested += delegate { BeginDrag(); };

            _caption = new CaptionBar();
            _caption.SetBounds(Ui.Px(SidebarWidth), 0, ClientSize.Width - Ui.Px(SidebarWidth), Ui.Px(CaptionHeight));
            _caption.MinimizeClicked += delegate { WindowState = FormWindowState.Minimized; };
            _caption.CloseClicked += delegate { HideToTray(); };
            _caption.DragRequested += delegate { BeginDrag(); };

            Size area = new Size(
                ClientSize.Width - Ui.Px(SidebarWidth) - Ui.Px(PagePadding) * 2,
                ClientSize.Height - Ui.Px(CaptionHeight) - Ui.Px(22));

            _content = new Panel();
            _content.SetBounds(Ui.Px(SidebarWidth) + Ui.Px(PagePadding), Ui.Px(CaptionHeight), area.Width, area.Height);
            _content.BackColor = Theme.Current.Window;

            _pages.Add(new BehaviourPage(this, area));
            _pages.Add(new DisplaysPage(this, area));
            _pages.Add(new AppearancePage(this, area));
            foreach (Page page in _pages)
            {
                page.Visible = false;
                _content.Controls.Add(page);
            }

            Controls.Add(_content);
            Controls.Add(_caption);
            Controls.Add(_sidebar);

            ShowPage(_pages[0]);

            _statusTimer = new Timer();
            _statusTimer.Interval = 1000;
            _statusTimer.Tick += delegate { _sidebar.Invalidate(); };

            _engine.Changed += OnEngineChanged;
            Theme.CurrentChanged += OnThemeChanged;
        }

        public AppSettings Settings { get { return _settings; } }
        public DimEngine Engine { get { return _engine; } }
        public DisplayManager Displays { get { return _displays; } }

        public void Persist()
        {
            _settings.Save();
            _engine.Reapply();
            if (_onSettingsSaved != null) _onSettingsSaved();
        }

        public void UseTheme(Theme theme)
        {
            _settings.ThemeId = theme.Id;
            Theme.Current = theme;
            Persist();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= Native.CS_DROPSHADOW;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                _statusTimer.Start();
                if (_active != null) _active.OnWindowShown();
            }
            else _statusTimer.Stop();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) HideToTray();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // The window is only a view onto a resident app; the tray's Exit is what really closes it.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        private void HideToTray()
        {
            Hide();
            Native.TrimMemory();
        }

        private void BeginDrag()
        {
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
        }

        private void ShowPage(Page page)
        {
            if (_active == page) return;
            if (_active != null) _active.Visible = false;
            _active = page;
            _active.Visible = true;
            _active.OnWindowShown();
            _caption.Title = page.Title;
            _caption.Invalidate();
            _sidebar.Select(_pages.IndexOf(page));
        }

        private void OnEngineChanged(object sender, EventArgs e)
        {
            _sidebar.Invalidate();
            foreach (Page page in _pages) page.OnEngineChanged();
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            BackColor = Theme.Current.Window;
            _content.BackColor = Theme.Current.Window;
            _sidebar.OnThemeChanged();
            _caption.OnThemeChanged();
            foreach (Page page in _pages) page.OnThemeChanged();
            Invalidate(true);
        }

        /// <summary>Brings the window to the user rather than waiting for them to find it.</summary>
        public void Summon()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _engine.Changed -= OnEngineChanged;
                Theme.CurrentChanged -= OnThemeChanged;
                _statusTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        // -------------------------------------------------------------- sidebar

        private sealed class NavigationEventArgs : EventArgs
        {
            public NavigationEventArgs(int index) { Index = index; }
            public int Index { get; private set; }
        }

        private enum Glyph { Behaviour, Displays, Appearance }

        private sealed class Sidebar : ThemedControl
        {
            private readonly IShell _shell;
            private readonly NavItem[] _items;
            private readonly PillButton _pause;

            public Sidebar(IShell shell)
            {
                _shell = shell;

                _items = new NavItem[]
                {
                    new NavItem("Away & dimming", Glyph.Behaviour),
                    new NavItem("Displays", Glyph.Displays),
                    new NavItem("Appearance", Glyph.Appearance)
                };

                for (int i = 0; i < _items.Length; i++)
                {
                    int index = i;
                    _items[i].SetBounds(Ui.Px(12), Ui.Px(112 + i * 46), Ui.Px(SidebarWidth - 24), Ui.Px(40));
                    _items[i].Click += delegate
                    {
                        EventHandler<NavigationEventArgs> handler = NavigateTo;
                        if (handler != null) handler(this, new NavigationEventArgs(index));
                    };
                    Controls.Add(_items[i]);
                }
                _items[0].Selected = true;

                _pause = new PillButton();
                _pause.Primary = false;
                _pause.Text = "Pause";
                _pause.Click += delegate
                {
                    _shell.Engine.Paused = !_shell.Engine.Paused;
                    UpdatePauseLabel();
                };
                Controls.Add(_pause);
            }

            public event EventHandler<NavigationEventArgs> NavigateTo;
            public event EventHandler DragRequested;

            public override Color Backdrop { get { return T.Sidebar; } }
            protected override Color ChildBackdrop { get { return T.Sidebar; } }

            public void Select(int index)
            {
                for (int i = 0; i < _items.Length; i++)
                {
                    _items[i].Selected = i == index;
                    _items[i].Invalidate();
                }
            }

            private void UpdatePauseLabel()
            {
                _pause.Text = _shell.Engine.Paused ? "Resume" : "Pause";
                _pause.Invalidate();
                Invalidate();
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                // Adding the nav items lays the sidebar out before the button exists.
                if (_pause == null) return;
                _pause.SetBounds(Ui.Px(16), Height - Ui.Px(60), Width - Ui.Px(32), Ui.Px(36));
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                EventHandler handler = DragRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Ui.Quality(g);

                int mark = Ui.Px(30);
                g.DrawImage(AppInfo.Mark(mark), Ui.Px(20), Ui.Px(30), mark, mark);

                TextRenderer.DrawText(g, AppInfo.Name, Ui.Font(14f, FontStyle.Regular),
                    new Point(Ui.Px(58), Ui.Px(30)), T.Text, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "v" + AppInfo.Version, Ui.Font(8f, FontStyle.Regular),
                    new Point(Ui.Px(60), Ui.Px(53)), T.TextFaint, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

                using (Pen pen = new Pen(T.BorderSoft, 1f))
                    g.DrawLine(pen, Width - 1, 0, Width - 1, Height);

                PaintStatus(g);
            }

            /// <summary>The live answer to "is it about to dim, and why not?"</summary>
            private void PaintStatus(Graphics g)
            {
                DimEngine engine = _shell.Engine;

                string headline;
                string detail;
                Color dot;

                if (engine.Paused)
                {
                    headline = "Paused";
                    detail = "Dimming is switched off";
                    dot = T.TextFaint;
                }
                else if (engine.Overridden)
                {
                    headline = "Dimmed by hand";
                    detail = "Stays down until you restore it";
                    dot = T.AccentAlt;
                }
                else if (engine.State == DimState.Dimmed)
                {
                    headline = "Dimmed";
                    detail = "Waiting for you to come back";
                    dot = T.Accent;
                }
                else if (engine.HeldByMedia)
                {
                    headline = "Media playing";
                    detail = "Countdown starts when it stops";
                    dot = T.AccentAlt;
                }
                else
                {
                    int remaining = Math.Max(0, _shell.Settings.IdleSeconds - engine.CountdownSeconds);
                    headline = "Watching";
                    detail = remaining > 0 ? "Dims in " + remaining + "s" : "Dimming now";
                    dot = T.Accent;
                }

                int top = Height - Ui.Px(124);
                float size = Ui.Px(8);

                if (T.Glow && dot != T.TextFaint)
                    using (SolidBrush brush = new SolidBrush(Ui.Alpha(dot, 55)))
                        g.FillEllipse(brush, Ui.Px(17), top + Ui.Px(3), size + Ui.Px(6), size + Ui.Px(6));
                using (SolidBrush brush = new SolidBrush(dot))
                    g.FillEllipse(brush, Ui.Px(20), top + Ui.Px(6), size, size);

                TextRenderer.DrawText(g, headline, Ui.Font(9.5f, FontStyle.Bold),
                    new Point(Ui.Px(36), top), T.Text, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, detail, Ui.Font(8.25f, FontStyle.Regular),
                    new Rectangle(Ui.Px(20), top + Ui.Px(24), Width - Ui.Px(34), Ui.Px(18)), T.TextMuted,
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }

            public override void OnThemeChanged()
            {
                UpdatePauseLabel();
                base.OnThemeChanged();
            }
        }

        private sealed class NavItem : ThemedControl
        {
            private readonly Glyph _glyph;
            private bool _hover;

            public NavItem(string text, Glyph glyph)
            {
                Text = text;
                _glyph = glyph;
                Cursor = Cursors.Hand;
                MakeFocusable();
            }

            public bool Selected { get; set; }

            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { InvokeOnClick(this, EventArgs.Empty); e.Handled = true; }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Ui.Quality(g);

                RectangleF bounds = new RectangleF(0, 0, Width, Height);
                if (Selected)
                {
                    Ui.FillRound(g, Ui.Alpha(T.Accent, T.IsDark ? 36 : 24), bounds, 10f * Ui.Scale);
                    Ui.FillRound(g, T.Accent,
                        new RectangleF(0, Height * 0.24f, 3f * Ui.Scale, Height * 0.52f), 1.5f * Ui.Scale);
                }
                else if (_hover || KeyboardFocus)
                {
                    Ui.FillRound(g, Ui.Alpha(T.Text, 14), bounds, 10f * Ui.Scale);
                }

                Color ink = Selected ? T.Accent : (_hover || KeyboardFocus ? T.Text : T.TextMuted);
                PaintGlyph(g, new RectangleF(Ui.Px(14), (Height - Ui.Px(16)) / 2f, Ui.Px(16), Ui.Px(16)), ink);

                TextRenderer.DrawText(g, Text, Ui.Font(9.25f, Selected ? FontStyle.Bold : FontStyle.Regular),
                    new Rectangle(Ui.Px(40), 0, Width - Ui.Px(48), Height),
                    Selected ? T.Text : ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }

            private void PaintGlyph(Graphics g, RectangleF box, Color ink)
            {
                using (Pen pen = new Pen(ink, 1.5f * Ui.Scale))
                using (SolidBrush brush = new SolidBrush(ink))
                {
                    switch (_glyph)
                    {
                        case Glyph.Behaviour:
                            // The app mark in miniature: a disc with one lit edge.
                            g.DrawEllipse(pen, box.X, box.Y, box.Width, box.Height);
                            using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
                            {
                                path.AddArc(box.X, box.Y, box.Width, box.Height, 90, 180);
                                path.AddArc(box.X + box.Width * 0.32f, box.Y, box.Width * 0.68f, box.Height, 270, -180);
                                path.CloseFigure();
                                g.FillPath(brush, path);
                            }
                            break;

                        case Glyph.Displays:
                            g.DrawRectangle(pen, box.X, box.Y + box.Height * 0.08f, box.Width, box.Height * 0.64f);
                            g.FillRectangle(brush, box.X + box.Width * 0.30f, box.Y + box.Height * 0.84f,
                                box.Width * 0.40f, box.Height * 0.14f);
                            break;

                        case Glyph.Appearance:
                            g.DrawEllipse(pen, box.X, box.Y + box.Height * 0.12f, box.Width * 0.70f, box.Height * 0.70f);
                            g.FillEllipse(brush, box.X + box.Width * 0.32f, box.Y + box.Height * 0.26f,
                                box.Width * 0.68f, box.Height * 0.68f);
                            break;
                    }
                }
            }
        }

        // ---------------------------------------------------------- caption bar

        private sealed class CaptionBar : ThemedControl
        {
            private int _hoverButton = -1;

            public CaptionBar()
            {
                Title = string.Empty;
            }

            public string Title { get; set; }

            public event EventHandler MinimizeClicked;
            public event EventHandler CloseClicked;
            public event EventHandler DragRequested;

            private Rectangle ButtonRect(int index)
            {
                int width = Ui.Px(46);
                int height = Ui.Px(32);
                int right = Width - Ui.Px(14);
                return new Rectangle(right - width * (2 - index), (Height - height) / 2, width, height);
            }

            private int ButtonAt(Point point)
            {
                for (int i = 0; i < 2; i++)
                    if (ButtonRect(i).Contains(point)) return i;
                return -1;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int hit = ButtonAt(e.Location);
                if (hit == _hoverButton) return;
                _hoverButton = hit;
                Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hoverButton = -1;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;

                int hit = ButtonAt(e.Location);
                if (hit < 0)
                {
                    EventHandler drag = DragRequested;
                    if (drag != null) drag(this, EventArgs.Empty);
                    return;
                }

                EventHandler handler = hit == 0 ? MinimizeClicked : CloseClicked;
                if (handler != null) handler(this, EventArgs.Empty);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Ui.Quality(g);

                TextRenderer.DrawText(g, Title, Ui.Font(13f, FontStyle.Regular),
                    new Rectangle(Ui.Px(PagePadding), 0, Math.Max(Ui.Px(40), Width - Ui.Px(160)), Height), T.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

                for (int i = 0; i < 2; i++)
                {
                    Rectangle button = ButtonRect(i);
                    bool hot = _hoverButton == i;
                    Color ink = T.TextMuted;

                    if (hot)
                    {
                        Color face = i == 1 ? Color.FromArgb(232, 17, 35) : Ui.Alpha(T.Text, 24);
                        Ui.FillRound(g, face, button, 8f * Ui.Scale);
                        ink = i == 1 ? Color.White : T.Text;
                    }

                    float centreX = button.X + button.Width / 2f;
                    float centreY = button.Y + button.Height / 2f;
                    float arm = Ui.Px(5);

                    using (Pen pen = new Pen(ink, 1.3f * Ui.Scale))
                    {
                        if (i == 0) g.DrawLine(pen, centreX - arm, centreY, centreX + arm, centreY);
                        else
                        {
                            g.DrawLine(pen, centreX - arm, centreY - arm, centreX + arm, centreY + arm);
                            g.DrawLine(pen, centreX + arm, centreY - arm, centreX - arm, centreY + arm);
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------- pages

        /// <summary>
        /// A page of cards. The window cannot be resized, so every position is absolute -
        /// design units in, device pixels out, no anchoring to go wrong.
        /// </summary>
        private abstract class Page : ThemedControl
        {
            private int _cursor;

            protected Page(IShell shell, Size area)
            {
                Shell = shell;
                Size = area;
                Location = Point.Empty;
            }

            protected IShell Shell { get; private set; }

            public abstract string Title { get; }

            public virtual void OnWindowShown() { }
            public virtual void OnEngineChanged() { }

            /// <summary>Adds a card below the previous one. Heights are in design units.</summary>
            protected Card AddCard(string heading, int designHeight)
            {
                Card card = new Card();
                card.Heading = heading;
                card.SetBounds(0, Ui.Px(_cursor), Width, Ui.Px(designHeight));
                Controls.Add(card);
                _cursor += designHeight + 16;
                return card;
            }

            /// <summary>Tears the page down so it can be rebuilt against new hardware.</summary>
            protected void ResetCards()
            {
                List<Control> existing = new List<Control>();
                foreach (Control child in Controls) existing.Add(child);

                foreach (Control child in existing)
                {
                    Controls.Remove(child);
                    child.Dispose();
                }
                _cursor = 0;
            }

            /// <summary>A label, an explanation, and a control on the right, inside a card.</summary>
            protected static SettingRow Row(Card card, string label, string description, Control field, int designY)
            {
                SettingRow row = new SettingRow();
                row.Label = label;
                row.Description = description;
                row.Field = field;
                row.SetBounds(Ui.Px(20), Ui.Px(designY), card.Width - Ui.Px(40), Ui.Px(52));
                card.Controls.Add(row);
                return row;
            }

            /// <summary>Places a control inside a card, stretched to the card's inner width.</summary>
            protected static Control Place(Card card, Control control, int designX, int designY, int designRightPad)
            {
                int x = Ui.Px(designX);
                control.SetBounds(x, Ui.Px(designY),
                    Math.Max(Ui.Px(60), card.Width - x - Ui.Px(designRightPad)),
                    control.Height > 0 ? control.Height : Ui.Px(20));
                card.Controls.Add(control);
                return control;
            }
        }

        private sealed class BehaviourPage : Page
        {
            private static readonly int[] DelayPresets = { 5, 10, 30, 60, 120, 300 };
            private static readonly string[] DelayLabels = { "5s", "10s", "30s", "1m", "2m", "5m" };
            private static readonly int[] FadeSpeeds = { 0, 350, 700, 1300 };

            private readonly BrightnessGauge _gauge;
            private readonly Slider _level;
            private readonly PillButton _override;
            private readonly Segmented _presets;
            private readonly Slider _delay;
            private readonly Caption _delaySummary;

            public BehaviourPage(IShell shell, Size area) : base(shell, area)
            {
                AppSettings settings = shell.Settings;

                // --- away brightness ------------------------------------------------
                Card away = AddCard("AWAY BRIGHTNESS", 196);

                _gauge = new BrightnessGauge();
                _gauge.Percent = settings.AwayBrightness;
                _gauge.Legend = "away level";
                _gauge.SetBounds(Ui.Px(24), Ui.Px(44), Ui.Px(136), Ui.Px(136));
                away.Controls.Add(_gauge);

                Place(away, new Caption("Dim screens to", 10.5f, FontStyle.Bold, Tone.Normal), 188, 60, 24);
                Place(away, new Caption("A display already dimmer than this is left alone.",
                    8.5f, FontStyle.Regular, Tone.Muted), 188, 82, 24);

                _level = new Slider();
                _level.Minimum = 0;
                _level.Maximum = 100;
                _level.SetValueSilently(settings.AwayBrightness);
                _level.ValueChanged += delegate { _gauge.Percent = _level.Value; };
                _level.ValueCommitted += delegate
                {
                    Shell.Settings.AwayBrightness = _level.Value;
                    Shell.Persist();
                };
                Place(away, _level, 188, 112, 24);

                _override = new PillButton();
                _override.SetBounds(Ui.Px(188), Ui.Px(148), Ui.Px(168), Ui.Px(34));
                _override.Click += delegate
                {
                    Shell.Engine.Overridden = !Shell.Engine.Overridden;
                };
                away.Controls.Add(_override);
                ShowOverrideState();

                Place(away, new Caption("Dims straight away and stays there.",
                    8.25f, FontStyle.Regular, Tone.Faint), 364, 157, 24);

                // --- timing ---------------------------------------------------------
                Card timing = AddCard("WHEN TO DIM", 200);

                Place(timing, new Caption("Idle delay", 10.5f, FontStyle.Bold, Tone.Normal), 20, 48, 20);
                Place(timing, new Caption("Time without keyboard or mouse before Dimly steps in.",
                    8.5f, FontStyle.Regular, Tone.Muted), 20, 70, 20);

                _presets = new Segmented();
                _presets.Items = DelayLabels;
                _presets.SelectedIndexChanged += delegate
                {
                    if (_presets.SelectedIndex < 0) return;
                    SetDelay(DelayPresets[_presets.SelectedIndex]);
                    _delay.SetValueSilently(DelayScale.ToPosition(Shell.Settings.IdleSeconds));
                };
                Place(timing, _presets, 20, 98, 20);

                _delay = new Slider();
                _delay.Minimum = 0;
                _delay.Maximum = DelayScale.Positions;
                _delay.ValueChanged += delegate { ShowDelay(DelayScale.ToSeconds(_delay.Value)); };
                _delay.ValueCommitted += delegate { SetDelay(DelayScale.ToSeconds(_delay.Value)); };
                Place(timing, _delay, 20, 142, 20);

                _delaySummary = new Caption(string.Empty, 8.5f, FontStyle.Regular, Tone.Muted);
                Place(timing, _delaySummary, 20, 172, 20);

                _delay.SetValueSilently(DelayScale.ToPosition(settings.IdleSeconds));
                SyncDelayControls(settings.IdleSeconds);

                // --- rules ----------------------------------------------------------
                Card rules = AddCard("RULES", 316);

                Segmented fade = new Segmented();
                fade.Items = new string[] { "Off", "Fast", "Smooth", "Slow" };
                fade.Width = Ui.Px(228);
                fade.SetSelectedSilently(FadeIndex(settings));
                fade.SelectedIndexChanged += delegate
                {
                    int speed = FadeSpeeds[fade.SelectedIndex];
                    Shell.Settings.Fade = speed > 0;
                    if (speed > 0) Shell.Settings.FadeMillis = speed;
                    Shell.Persist();
                };
                Row(rules, "Fade the change", "A gradual shift is easier on the eyes than a jump.", fade, 44);

                ToggleSwitch onLock = new ToggleSwitch();
                onLock.SetCheckedSilently(settings.DimOnLock);
                onLock.CheckedChanged += delegate
                {
                    Shell.Settings.DimOnLock = onLock.Checked;
                    Shell.Persist();
                };
                Row(rules, "Dim when Windows locks", "Do not wait out the delay after Win+L.", onLock, 96);

                ToggleSwitch fullscreen = new ToggleSwitch();
                fullscreen.SetCheckedSilently(settings.SkipFullscreen);
                fullscreen.CheckedChanged += delegate
                {
                    Shell.Settings.SkipFullscreen = fullscreen.Checked;
                    Shell.Persist();
                };
                Row(rules, "Never dim over a fullscreen app",
                    "Films and games count as being at the desk.", fullscreen, 148);

                ToggleSwitch audio = new ToggleSwitch();
                audio.SetCheckedSilently(settings.HoldWhileAudioPlays);
                audio.CheckedChanged += delegate
                {
                    Shell.Settings.HoldWhileAudioPlays = audio.Checked;
                    Shell.Persist();
                };
                Row(rules, "Never dim while media is playing",
                    "Sound means someone is still listening, music included.", audio, 200);

                ToggleSwitch devices = new ToggleSwitch();
                devices.SetCheckedSilently(settings.IgnoreNoisyDevices);
                devices.CheckedChanged += delegate
                {
                    Shell.Settings.IgnoreNoisyDevices = devices.Checked;
                    Shell.Persist();
                };
                Row(rules, "Ignore devices that keep the PC awake",
                    "A gamepad with drifting sticks can hold Windows awake by itself.", devices, 252);
            }

            public override string Title { get { return "Away & dimming"; } }

            private static int FadeIndex(AppSettings settings)
            {
                if (!settings.Fade) return 0;
                if (settings.FadeMillis <= 450) return 1;
                return settings.FadeMillis <= 900 ? 2 : 3;
            }

            private void SetDelay(int seconds)
            {
                Shell.Settings.IdleSeconds = Math.Max(AppSettings.MinIdleSeconds,
                    Math.Min(AppSettings.MaxIdleSeconds, seconds));
                SyncDelayControls(Shell.Settings.IdleSeconds);
                Shell.Persist();
            }

            /// <summary>Keeps the preset row and the fine-tune slider telling the same story.</summary>
            private void SyncDelayControls(int seconds)
            {
                _presets.SetSelectedSilently(Array.IndexOf(DelayPresets, seconds));
                ShowDelay(seconds);
            }

            private void ShowDelay(int seconds)
            {
                _delaySummary.Text = "Dims after " + DelayScale.Humanize(seconds) + " without input.";
                _delaySummary.Invalidate();
            }

            private void ShowOverrideState()
            {
                bool on = Shell.Engine.Overridden;
                _override.Text = on ? "Restore brightness" : "Dim now";
                _override.Primary = !on;
                _override.Invalidate();
            }

            public override void OnEngineChanged()
            {
                ShowOverrideState();
            }

            public override void OnWindowShown()
            {
                _gauge.Percent = Shell.Settings.AwayBrightness;
                _level.SetValueSilently(Shell.Settings.AwayBrightness);
                _delay.SetValueSilently(DelayScale.ToPosition(Shell.Settings.IdleSeconds));
                SyncDelayControls(Shell.Settings.IdleSeconds);
                ShowOverrideState();
            }
        }

        private sealed class DisplaysPage : Page
        {
            private int _builtFor = -1;

            public DisplaysPage(IShell shell, Size area) : base(shell, area)
            {
                Build();
            }

            public override string Title { get { return "Displays"; } }

            private void Build()
            {
                ResetCards();

                IList<DisplayTarget> targets = Shell.Displays.Targets;
                _builtFor = targets.Count;

                int rows = Math.Max(1, targets.Count);
                Card card = AddCard("DISPLAYS", 44 + rows * 62 + 76);

                int top = 44;
                foreach (DisplayTarget target in targets)
                {
                    DisplayRow row = new DisplayRow(target, Shell.Settings.IsEnabled(target));
                    row.SetBounds(Ui.Px(20), Ui.Px(top), card.Width - Ui.Px(40), Ui.Px(62));
                    DisplayRow captured = row;
                    row.IncludedChanged += delegate
                    {
                        Shell.Settings.SetEnabled(captured.Target, captured.Included);
                        Shell.Persist();
                    };
                    card.Controls.Add(row);
                    top += 62;
                }

                if (targets.Count == 0)
                {
                    Place(card, new Caption("No displays were detected.", 9.5f, FontStyle.Regular, Tone.Muted),
                        20, top + 18, 20);
                    top += 62;
                }

                Caption note = new Caption(
                    "Dimly uses the strongest channel each display allows: the panel backlight on laptops, "
                    + "DDC/CI over the cable on monitors, and a software overlay when neither is offered.",
                    8.5f, FontStyle.Regular, Tone.Muted);
                note.Wrap = true;
                note.SetBounds(Ui.Px(20), Ui.Px(top + 14), Math.Max(Ui.Px(200), card.Width - Ui.Px(150)), Ui.Px(48));
                card.Controls.Add(note);

                PillButton rescan = new PillButton();
                rescan.Primary = false;
                rescan.Text = "Rescan";
                rescan.SetBounds(card.Width - Ui.Px(116), Ui.Px(top + 20), Ui.Px(96), Ui.Px(34));
                rescan.Click += delegate { Reload(); };
                card.Controls.Add(rescan);
            }

            /// <summary>Re-enumerates the hardware, then redraws the list from the result.</summary>
            private void Reload()
            {
                Shell.Engine.ReloadDisplays();

                // Enumeration happens on the engine's worker; wait for it to land before redrawing.
                Timer settle = new Timer();
                settle.Interval = 900;
                settle.Tick += delegate
                {
                    settle.Stop();
                    settle.Dispose();
                    _builtFor = -1;
                    Build();
                };
                settle.Start();
            }

            public override void OnWindowShown()
            {
                if (_builtFor != Shell.Displays.Targets.Count) Build();
            }
        }

        private sealed class AppearancePage : Page
        {
            private readonly List<ThemeSwatch> _swatches = new List<ThemeSwatch>();

            public AppearancePage(IShell shell, Size area) : base(shell, area)
            {
                Card themes = AddCard("THEME", 208);

                IList<Theme> catalogue = Theme.All;
                int gap = Ui.Px(14);
                int width = (themes.Width - Ui.Px(40) - gap * (catalogue.Count - 1)) / catalogue.Count;

                for (int i = 0; i < catalogue.Count; i++)
                {
                    ThemeSwatch swatch = new ThemeSwatch(catalogue[i]);
                    swatch.Selected = catalogue[i] == Theme.Current;
                    swatch.SetBounds(Ui.Px(20) + i * (width + gap), Ui.Px(44), width, Ui.Px(144));
                    swatch.Click += delegate(object sender, EventArgs e)
                    {
                        ThemeSwatch clicked = (ThemeSwatch)sender;
                        Shell.UseTheme(clicked.Swatch);
                        foreach (ThemeSwatch other in _swatches)
                        {
                            other.Selected = other == clicked;
                            other.Invalidate();
                        }
                    };
                    _swatches.Add(swatch);
                    themes.Controls.Add(swatch);
                }

                Card startup = AddCard("STARTUP", 164);

                ToggleSwitch withWindows = new ToggleSwitch();
                withWindows.SetCheckedSilently(Startup.IsEnabled());
                withWindows.CheckedChanged += delegate { Startup.SetEnabled(withWindows.Checked); };
                Row(startup, "Start with Windows", "Dimly is ready the moment you sign in.", withWindows, 44);

                ToggleSwitch hidden = new ToggleSwitch();
                hidden.SetCheckedSilently(Shell.Settings.StartHidden);
                hidden.CheckedChanged += delegate
                {
                    Shell.Settings.StartHidden = hidden.Checked;
                    Shell.Persist();
                };
                Row(startup, "Start hidden in the tray", "Skip this window and go straight to work.", hidden, 96);

                Card about = AddCard("ABOUT", 146);

                MarkBox mark = new MarkBox(Ui.Px(44));
                mark.Location = new Point(Ui.Px(24), Ui.Px(50));
                about.Controls.Add(mark);

                Caption name = new Caption(AppInfo.Name + " " + AppInfo.Version, 12f, FontStyle.Regular, Tone.Normal);
                name.SetBounds(Ui.Px(82), Ui.Px(46), Ui.Px(320), Ui.Px(24));
                about.Controls.Add(name);

                Caption tagline = new Caption(AppInfo.Tagline, 8.75f, FontStyle.Regular, Tone.Muted);
                tagline.SetBounds(Ui.Px(82), Ui.Px(70), about.Width - Ui.Px(102), Ui.Px(20));
                about.Controls.Add(tagline);

                Caption credits = new Caption("Made by Aitha & AI.", 8.75f, FontStyle.Regular, Tone.Muted);
                credits.SetBounds(Ui.Px(82), Ui.Px(88), about.Width - Ui.Px(102), Ui.Px(20));
                about.Controls.Add(credits);

                Caption where = new Caption("Settings: " + AppSettings.DisplayPath, 8f, FontStyle.Regular, Tone.Faint);
                where.SetBounds(Ui.Px(82), Ui.Px(106), about.Width - Ui.Px(102), Ui.Px(20));
                about.Controls.Add(where);
            }

            public override string Title { get { return "Appearance"; } }

            public override void OnWindowShown()
            {
                foreach (ThemeSwatch swatch in _swatches)
                {
                    swatch.Selected = swatch.Swatch == Theme.Current;
                    swatch.Invalidate();
                }
            }
        }
    }
}
