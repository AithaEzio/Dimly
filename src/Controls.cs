using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Dimly
{
    /// <summary>
    /// Base for everything Dimly draws itself. Controls are opaque - each one fills its own
    /// rectangle with whatever colour sits behind it - which keeps WinForms out of the business
    /// of transparency and keeps repaints flicker-free.
    /// </summary>
    public abstract class ThemedControl : Control
    {
        private Color _backdrop = Color.Empty;

        protected ThemedControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        protected static Theme T { get { return Theme.Current; } }

        /// <summary>The colour immediately behind this control. Inherited from the parent when unset.</summary>
        public virtual Color Backdrop
        {
            get { return _backdrop.A != 0 ? _backdrop : InheritedBackdrop(); }
            set { _backdrop = value; Invalidate(); }
        }

        /// <summary>The colour children of this control sit on. Containers override it.</summary>
        protected virtual Color ChildBackdrop { get { return Backdrop; } }

        private Color InheritedBackdrop()
        {
            ThemedControl parent = Parent as ThemedControl;
            if (parent != null) return parent.ChildBackdrop;
            return Parent != null ? Parent.BackColor : T.Window;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Backdrop);
        }

        protected void MakeFocusable()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        /// <summary>
        /// True only when a focus ring belongs on screen: the control has focus *and* Windows
        /// says to show focus cues, which it does after keyboard navigation but not after a
        /// click. Rings are always drawn inside the control's own bounds so they cannot be
        /// clipped into stray marks at the corners.
        /// </summary>
        protected bool KeyboardFocus { get { return Focused && ShowFocusCues; } }

        /// <summary>Called after the palette changes so cached geometry can be dropped.</summary>
        public virtual void OnThemeChanged()
        {
            Invalidate();
            foreach (Control child in Controls)
            {
                ThemedControl themed = child as ThemedControl;
                if (themed != null) themed.OnThemeChanged();
                else child.Invalidate();
            }
        }
    }

    // ------------------------------------------------------------------- card

    /// <summary>A rounded surface that groups related settings.</summary>
    public sealed class Card : ThemedControl
    {
        public Card()
        {
            Radius = 14;
        }

        public double Radius { get; set; }
        public string Heading { get; set; }

        protected override Color ChildBackdrop { get { return T.Card; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.Quality(g);

            RectangleF bounds = new RectangleF(0, 0, Width - 1, Height - 1);
            float radius = (float)Radius * Ui.Scale;

            Ui.FillRound(g, T.Card, bounds, radius);
            Ui.DrawRound(g, T.Border, 1f * Ui.Scale, bounds, radius);
            if (T.Glow) Ui.DrawRound(g, Ui.Alpha(T.Accent, 22), 1f * Ui.Scale,
                RectangleF.Inflate(bounds, -1.5f * Ui.Scale, -1.5f * Ui.Scale), radius);

            if (!string.IsNullOrEmpty(Heading))
                TextRenderer.DrawText(g, Heading, Ui.Font(8.5f, FontStyle.Bold),
                    new Point(Ui.Px(20), Ui.Px(18)), T.TextFaint,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            base.OnPaint(e);
        }
    }

    // ----------------------------------------------------------------- caption

    public enum Tone
    {
        Normal,
        Muted,
        Faint
    }

    /// <summary>A run of static text. The tone is resolved when painting, so it follows the theme.</summary>
    public sealed class Caption : ThemedControl
    {
        public Caption(string text, float fontSize, FontStyle style, Tone tone)
        {
            Text = text;
            FontSize = fontSize;
            Style = style;
            Ink = tone;
            Height = Ui.Px(20);
        }

        public float FontSize { get; set; }
        public FontStyle Style { get; set; }
        public Tone Ink { get; set; }

        public bool Wrap { get; set; }

        /// <summary>Aligns to the right edge, for a value that belongs opposite its label.</summary>
        public bool AlignRight { get; set; }

        private Color Colour
        {
            get
            {
                switch (Ink)
                {
                    case Tone.Muted: return T.TextMuted;
                    case Tone.Faint: return T.TextFaint;
                    default: return T.Text;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            TextFormatFlags flags = TextFormatFlags.NoPrefix
                | (Wrap ? TextFormatFlags.WordBreak : TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter)
                | (AlignRight ? TextFormatFlags.Right : TextFormatFlags.Left);

            TextRenderer.DrawText(e.Graphics, Text, Ui.Font(FontSize, Style), ClientRectangle, Colour, flags);
        }
    }

    // ------------------------------------------------------------------ toggle

    public sealed class ToggleSwitch : ThemedControl
    {
        private const int DesignWidth = 44;
        private const int DesignHeight = 24;

        private readonly Timer _animation;
        private bool _checked;
        private float _phase;
        private bool _hover;

        public ToggleSwitch()
        {
            MakeFocusable();
            Size = new Size(Ui.Px(DesignWidth), Ui.Px(DesignHeight));
            Cursor = Cursors.Hand;

            _animation = new Timer();
            _animation.Interval = 15;
            _animation.Tick += Advance;
        }

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                _animation.Start();
                EventHandler handler = CheckedChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        /// <summary>Sets the position without telling anyone - for loading saved settings.</summary>
        public void SetCheckedSilently(bool value)
        {
            _checked = value;
            _phase = value ? 1f : 0f;
            Invalidate();
        }

        private void Advance(object sender, EventArgs e)
        {
            float target = _checked ? 1f : 0f;
            float step = 0.16f;
            if (Math.Abs(target - _phase) <= step) { _phase = target; _animation.Stop(); }
            else _phase += Math.Sign(target - _phase) * step;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { Focus(); Checked = !Checked; }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Checked = !Checked; e.Handled = true; }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.Quality(g);

            RectangleF track = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float radius = track.Height / 2f;
            Color off = _hover ? Ui.Mix(T.Track, T.Text, 0.10) : T.Track;
            Color on = _hover ? Ui.Mix(T.Accent, Color.White, 0.12) : T.Accent;
            Color fill = Ui.Mix(off, on, _phase);

            if (T.Glow && _phase > 0.05f)
                Ui.Halo(g, Ui.Alpha(T.Accent, (int)(200 * _phase)), track, radius, 3);

            Ui.FillRound(g, fill, track, radius);
            if (_phase < 0.95f) Ui.DrawRound(g, T.Border, 1f * Ui.Scale, track, radius);
            if (KeyboardFocus)
                Ui.DrawRound(g, Ui.Alpha(_phase > 0.5f ? T.OnAccent : T.Accent, 170), 1.4f * Ui.Scale,
                    RectangleF.Inflate(track, -2.5f * Ui.Scale, -2.5f * Ui.Scale), radius - 2.5f * Ui.Scale);

            float inset = 3f * Ui.Scale;
            float knob = track.Height - inset * 2f;
            float travel = track.Width - knob - inset * 2f;
            RectangleF knobRect = new RectangleF(track.X + inset + travel * _phase, track.Y + inset, knob, knob);

            Color knobColor = Ui.Mix(T.KnobOff, T.OnAccent, _phase);
            using (SolidBrush brush = new SolidBrush(knobColor))
                g.FillEllipse(brush, knobRect);
        }
    }

    // ------------------------------------------------------------------ slider

    public sealed class Slider : ThemedControl
    {
        private int _minimum = 0;
        private int _maximum = 100;
        private int _value;
        private bool _dragging;
        private bool _hover;

        public Slider()
        {
            MakeFocusable();
            Height = Ui.Px(30);
            Cursor = Cursors.Hand;
        }

        /// <summary>A disabled slider must not look like a slider sitting at zero.</summary>
        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            if (!Enabled) { _hover = false; _dragging = false; }
            Invalidate();
        }

        /// <summary>Fires continuously while dragging - use it for live feedback.</summary>
        public event EventHandler ValueChanged;

        /// <summary>Fires once the user lets go - use it for saving and for hardware writes.</summary>
        public event EventHandler ValueCommitted;

        public int Minimum { get { return _minimum; } set { _minimum = value; Value = _value; } }
        public int Maximum { get { return _maximum; } set { _maximum = value; Value = _value; } }

        public int Value
        {
            get { return _value; }
            set
            {
                int clamped = Math.Max(_minimum, Math.Min(_maximum, value));
                if (clamped == _value) return;
                _value = clamped;
                Invalidate();
                EventHandler handler = ValueChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        public void SetValueSilently(int value)
        {
            _value = Math.Max(_minimum, Math.Min(_maximum, value));
            Invalidate();
        }

        private float KnobRadius { get { return 9f * Ui.Scale; } }

        /// <summary>Room for the knob's hover ring, so it is never clipped at either end.</summary>
        private float Edge { get { return KnobRadius + 3f * Ui.Scale; } }

        private float Fraction
        {
            get { return _maximum == _minimum ? 0f : (_value - _minimum) / (float)(_maximum - _minimum); }
        }

        private void SetFromPointer(int x)
        {
            float edge = Edge;
            float span = Width - edge * 2f;
            if (span <= 0) return;
            float fraction = Math.Max(0f, Math.Min(1f, (x - edge) / span));
            Value = _minimum + (int)Math.Round(fraction * (_maximum - _minimum));
        }

        private void Commit()
        {
            EventHandler handler = ValueCommitted;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            _dragging = true;
            SetFromPointer(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) SetFromPointer(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;
            _dragging = false;
            Commit();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value += Math.Sign(e.Delta);
            Commit();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int step = e.Control ? 10 : 1;
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) { Value -= step; Commit(); e.Handled = true; }
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) { Value += step; Commit(); e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { Value = _minimum; Commit(); e.Handled = true; }
            else if (e.KeyCode == Keys.End) { Value = _maximum; Commit(); e.Handled = true; }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                case Keys.Home: case Keys.End:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.Quality(g);

            float radius = KnobRadius;
            float edge = Edge;
            float thickness = 5f * Ui.Scale;
            float centreY = Height / 2f;
            RectangleF rail = new RectangleF(edge, centreY - thickness / 2f, Width - edge * 2f, thickness);

            Ui.FillRound(g, T.Track, rail, thickness / 2f);

            // Nothing to point at: the value is not known, so no knob claims to be the value.
            if (!Enabled) return;

            float filled = rail.Width * Fraction;
            if (filled > 0.5f)
            {
                RectangleF progress = new RectangleF(rail.X, rail.Y, filled, rail.Height);
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    new RectangleF(rail.X, rail.Y, Math.Max(rail.Width, 1f), rail.Height),
                    T.AccentDeep, T.Accent, LinearGradientMode.Horizontal))
                using (GraphicsPath path = Ui.Round(progress, thickness / 2f))
                {
                    brush.WrapMode = WrapMode.TileFlipXY;
                    g.FillPath(brush, path);
                }
            }

            PointF centre = new PointF(rail.X + filled, centreY);
            RectangleF knob = new RectangleF(centre.X - radius, centre.Y - radius, radius * 2f, radius * 2f);

            if (T.Glow && (_hover || _dragging || KeyboardFocus))
                Ui.Halo(g, T.Accent, knob, radius, 3);

            using (SolidBrush brush = new SolidBrush(T.Accent))
                g.FillEllipse(brush, knob);
            using (SolidBrush brush = new SolidBrush(_dragging ? T.Accent : T.Card))
                g.FillEllipse(brush, RectangleF.Inflate(knob, -3.5f * Ui.Scale, -3.5f * Ui.Scale));
            if (_hover || _dragging || KeyboardFocus)
                using (Pen pen = new Pen(Ui.Alpha(T.Accent, 90), 1.5f * Ui.Scale))
                    g.DrawEllipse(pen, RectangleF.Inflate(knob, 3f * Ui.Scale, 3f * Ui.Scale));
        }
    }

    // --------------------------------------------------------------- segmented

    /// <summary>A row of mutually exclusive choices - the delay presets, the fade speeds.</summary>
    public sealed class Segmented : ThemedControl
    {
        private string[] _items = new string[0];
        private int _selected;
        private int _hover = -1;

        public Segmented()
        {
            MakeFocusable();
            Height = Ui.Px(34);
            Cursor = Cursors.Hand;
        }

        public event EventHandler SelectedIndexChanged;

        public string[] Items
        {
            get { return _items; }
            set { _items = value ?? new string[0]; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (value < 0 || value >= _items.Length || value == _selected) return;
                _selected = value;
                Invalidate();
                EventHandler handler = SelectedIndexChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        /// <summary>Sets the selection, or -1 when the underlying value matches no preset.</summary>
        public void SetSelectedSilently(int index)
        {
            _selected = index >= _items.Length ? -1 : Math.Max(-1, index);
            Invalidate();
        }

        private float CellWidth
        {
            get { return _items.Length == 0 ? 0 : (Width - Inset * 2f) / _items.Length; }
        }

        private float Inset { get { return 3f * Ui.Scale; } }

        private int IndexAt(int x)
        {
            float cell = CellWidth;
            if (cell <= 0) return -1;
            int index = (int)((x - Inset) / cell);
            return index < 0 || index >= _items.Length ? -1 : index;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            int index = IndexAt(e.X);
            if (index >= 0) SelectedIndex = index;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = IndexAt(e.X);
            if (index == _hover) return;
            _hover = index;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) { SelectedIndex = Math.Max(0, _selected - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { SelectedIndex = _selected + 1; e.Handled = true; }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.Quality(g);

            RectangleF bounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float radius = 9f * Ui.Scale;
            Ui.FillRound(g, T.Track, bounds, radius);
            if (KeyboardFocus) Ui.DrawRound(g, Ui.Alpha(T.Accent, 150), 1.4f * Ui.Scale, bounds, radius);

            if (_items.Length == 0) return;

            float cell = CellWidth;
            if (_selected >= 0)
            {
                RectangleF pill = new RectangleF(Inset + cell * _selected, Inset,
                    cell, bounds.Height - Inset * 2f);
                if (T.Glow) Ui.Halo(g, T.Accent, pill, radius - 2f * Ui.Scale, 2);
                Ui.FillRound(g, T.Accent, pill, radius - 2f * Ui.Scale);
            }

            for (int i = 0; i < _items.Length; i++)
            {
                Rectangle cellRect = Rectangle.Round(new RectangleF(Inset + cell * i, Inset,
                    cell, bounds.Height - Inset * 2f));
                Color colour = i == _selected ? T.OnAccent : (i == _hover ? T.Text : T.TextMuted);
                TextRenderer.DrawText(g, _items[i], Ui.Font(8.5f, i == _selected ? FontStyle.Bold : FontStyle.Regular),
                    cellRect, colour,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }

    // ------------------------------------------------------------------ button

    public sealed class PillButton : ThemedControl
    {
        private bool _hover;
        private bool _pressed;

        public PillButton()
        {
            MakeFocusable();
            Height = Ui.Px(36);
            Cursor = Cursors.Hand;
            Primary = true;
        }

        public bool Primary { get; set; }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); _pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { InvokeOnClick(this, EventArgs.Empty); e.Handled = true; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.Quality(g);

            RectangleF bounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float radius = bounds.Height / 2f;

            Color face, ink;
            if (!Enabled)
            {
                face = T.Track;
                ink = T.TextFaint;
                Ui.FillRound(g, face, bounds, radius);
                Ui.DrawRound(g, T.Border, 1f * Ui.Scale, bounds, radius);
                TextRenderer.DrawText(g, Text, Ui.Font(9f, FontStyle.Bold), ClientRectangle, ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            if (Primary)
            {
                face = _pressed ? T.AccentDeep : (_hover ? Ui.Mix(T.Accent, Color.White, 0.14) : T.Accent);
                ink = T.OnAccent;
                if (T.Glow && (_hover || KeyboardFocus)) Ui.Halo(g, T.Accent, bounds, radius, 3);
                Ui.FillRound(g, face, bounds, radius);
            }
            else
            {
                face = _pressed ? T.CardHover : (_hover ? Ui.Mix(T.Card, T.Text, 0.06) : T.Card);
                ink = T.Text;
                Ui.FillRound(g, face, bounds, radius);
                Ui.DrawRound(g, _hover ? T.Accent : T.Border, 1f * Ui.Scale, bounds, radius);
            }

            if (KeyboardFocus)
                Ui.DrawRound(g, Ui.Alpha(Primary ? T.OnAccent : T.Accent, 170), 1.4f * Ui.Scale,
                    RectangleF.Inflate(bounds, -3f * Ui.Scale, -3f * Ui.Scale), radius - 3f * Ui.Scale);

            TextRenderer.DrawText(g, Text, Ui.Font(9f, FontStyle.Bold), ClientRectangle, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // -------------------------------------------------------------- setting row

    /// <summary>A label, an explanation, and one control on the right.</summary>
    public sealed class SettingRow : ThemedControl
    {
        private Control _control;

        public SettingRow()
        {
            Height = Ui.Px(56);
        }

        public string Label { get; set; }
        public string Description { get; set; }

        public Control Field
        {
            get { return _control; }
            set
            {
                if (_control != null) Controls.Remove(_control);
                _control = value;
                if (_control == null) return;
                Controls.Add(_control);
                PerformLayout();
            }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_control == null) return;
            _control.Left = Width - _control.Width;
            _control.Top = (Height - _control.Height) / 2;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int reserved = _control != null ? _control.Width + Ui.Px(16) : 0;
            Rectangle text = new Rectangle(0, 0, Math.Max(Ui.Px(40), Width - reserved), Height);

            bool hasDescription = !string.IsNullOrEmpty(Description);
            int labelTop = hasDescription ? Ui.Px(11) : (Height - Ui.Px(18)) / 2;

            TextRenderer.DrawText(g, Label, Ui.Font(9.5f, FontStyle.Regular),
                new Rectangle(text.X, labelTop, text.Width, Ui.Px(20)), T.Text,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (!hasDescription) return;
            TextRenderer.DrawText(g, Description, Ui.Font(8.25f, FontStyle.Regular),
                new Rectangle(text.X, labelTop + Ui.Px(20), text.Width, Ui.Px(20)), T.TextMuted,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    // ------------------------------------------------------------------- gauge

    /// <summary>The away level as an arc, so the number has a shape as well as a value.</summary>
    public sealed class BrightnessGauge : ThemedControl
    {
        private const float StartAngle = 135f;
        private const float SweepAngle = 270f;

        private int _percent = 20;

        public int Percent
        {
            get { return _percent; }
            set { _percent = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        public string Legend { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.Quality(g);

            float thickness = 9f * Ui.Scale;
            float size = Math.Min(Width, Height) - thickness;
            RectangleF arc = new RectangleF((Width - size) / 2f, (Height - size) / 2f, size, size);

            using (Pen pen = new Pen(T.Track, thickness))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, arc, StartAngle, SweepAngle);
            }

            float sweep = SweepAngle * _percent / 100f;
            if (sweep > 0.5f)
            {
                LinearGradientBrush brush = new LinearGradientBrush(
                    RectangleF.Inflate(arc, thickness, thickness), T.AccentAlt, T.Accent, LinearGradientMode.ForwardDiagonal);
                // The pen copies the brush, so the wrap mode has to be set before it is made:
                // a clamped gradient leaves a stray mark where its rectangle starts.
                brush.WrapMode = WrapMode.TileFlipXY;

                using (brush)
                using (Pen pen = new Pen(brush, thickness))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    if (T.Glow)
                        using (Pen halo = new Pen(Ui.Alpha(T.Accent, 55), thickness + 6f * Ui.Scale))
                        {
                            halo.StartCap = LineCap.Round;
                            halo.EndCap = LineCap.Round;
                            g.DrawArc(halo, arc, StartAngle, sweep);
                        }
                    g.DrawArc(pen, arc, StartAngle, sweep);
                }
            }

            // The reading is placed by hand because the number is large and the unit small.
            // Drawing them as one string is not an option, and butting them together on advance
            // width alone collides for a narrow glyph: "1" leaves no room before the "%".
            string value = _percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Font numberFont = Ui.Font(30f, FontStyle.Regular);
            Font unitFont = Ui.Font(13f, FontStyle.Regular);

            Size number = TextRenderer.MeasureText(g, value, numberFont, Size.Empty, TextFormatFlags.NoPadding);
            Size unit = TextRenderer.MeasureText(g, "%", unitFont, Size.Empty, TextFormatFlags.NoPadding);

            int gap = Ui.Px(4);
            int left = (Width - (number.Width + gap + unit.Width)) / 2;
            int top = (Height - number.Height) / 2 - Ui.Px(8);

            TextRenderer.DrawText(g, value, numberFont, new Point(left, top), T.Text, TextFormatFlags.NoPadding);

            // The unit sits on the number's baseline, lifted a little so it reads as a unit
            // rather than as a second, smaller number.
            TextRenderer.DrawText(g, "%", unitFont,
                new Point(left + number.Width + gap, top + number.Height - unit.Height - Ui.Px(4)),
                T.TextMuted, TextFormatFlags.NoPadding);

            if (string.IsNullOrEmpty(Legend)) return;
            TextRenderer.DrawText(g, Legend, Ui.Font(8.25f, FontStyle.Regular),
                new Rectangle(0, top + number.Height + Ui.Px(2), Width, Ui.Px(20)), T.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // ------------------------------------------------------------- theme picker

    public sealed class ThemeSwatch : ThemedControl
    {
        private bool _hover;

        public ThemeSwatch(Theme theme)
        {
            Swatch = theme;
            Cursor = Cursors.Hand;
            MakeFocusable();
        }

        public Theme Swatch { get; private set; }
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

            RectangleF bounds = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
            float radius = 12f * Ui.Scale;

            // The swatch previews its own palette, not the one currently in use.
            Ui.FillRound(g, Swatch.Window, bounds, radius);

            RectangleF strip = new RectangleF(bounds.X + Ui.Px(14), bounds.Y + Ui.Px(14),
                bounds.Width - Ui.Px(28), Ui.Px(46));
            Ui.FillRound(g, Swatch.Card, strip, 8f * Ui.Scale);
            Ui.DrawRound(g, Swatch.Border, 1f * Ui.Scale, strip, 8f * Ui.Scale);

            float dot = Ui.Px(12);
            float dotY = strip.Y + (strip.Height - dot) / 2f;
            using (SolidBrush brush = new SolidBrush(Swatch.Accent))
                g.FillEllipse(brush, strip.X + Ui.Px(12), dotY, dot, dot);
            using (SolidBrush brush = new SolidBrush(Swatch.AccentAlt))
                g.FillEllipse(brush, strip.X + Ui.Px(30), dotY, dot, dot);

            RectangleF bar = new RectangleF(strip.X + Ui.Px(52), dotY + Ui.Px(3),
                Math.Max(Ui.Px(10), strip.Right - strip.X - Ui.Px(66)), Ui.Px(6));
            Ui.FillRound(g, Swatch.Track, bar, bar.Height / 2f);
            Ui.FillRound(g, Swatch.Accent, new RectangleF(bar.X, bar.Y, bar.Width * 0.55f, bar.Height), bar.Height / 2f);

            TextRenderer.DrawText(g, Swatch.Name, Ui.Font(9.5f, FontStyle.Bold),
                new Rectangle(Ui.Px(15), (int)strip.Bottom + Ui.Px(10), Width - Ui.Px(30), Ui.Px(20)),
                Swatch.Text, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Swatch.Tagline, Ui.Font(8.25f, FontStyle.Regular),
                new Rectangle(Ui.Px(15), (int)strip.Bottom + Ui.Px(28), Width - Ui.Px(30), Ui.Px(20)),
                Swatch.TextMuted, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            Color edge = Selected ? T.Accent : (_hover || KeyboardFocus ? Ui.Alpha(T.Accent, 130) : T.Border);
            float weight = Selected ? 2f : 1f;
            if (Selected && T.Glow) Ui.Halo(g, T.Accent, bounds, radius, 3);
            Ui.DrawRound(g, edge, weight * Ui.Scale, bounds, radius);
        }
    }

    // -------------------------------------------------------------- display row

    public sealed class DisplayRow : ThemedControl
    {
        private readonly ToggleSwitch _toggle;

        public DisplayRow(DisplayTarget target, bool enabled)
        {
            Target = target;

            _toggle = new ToggleSwitch();
            _toggle.SetCheckedSilently(enabled);
            _toggle.CheckedChanged += delegate
            {
                EventHandler handler = IncludedChanged;
                if (handler != null) handler(this, EventArgs.Empty);
                Invalidate();
            };
            Controls.Add(_toggle);
            Height = Ui.Px(62);
        }

        public DisplayTarget Target { get; private set; }
        /// <summary>Whether this display takes part in dimming.</summary>
        public bool Included { get { return _toggle.Checked; } }

        public event EventHandler IncludedChanged;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_toggle == null) return;   // layout can run while the row is still being built
            _toggle.Left = Width - _toggle.Width - Ui.Px(4);
            _toggle.Top = (Height - _toggle.Height) / 2;
        }

        private string Method()
        {
            switch (Target.Kind)
            {
                case BrightnessKind.Backlight: return "Panel backlight";
                case BrightnessKind.Ddc: return Target.Degraded ? "Software overlay (DDC/CI refused)" : "Backlight over DDC/CI";
                default: return "Software overlay";
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_toggle == null) return;

            Graphics g = e.Graphics;
            Ui.Quality(g);

            int textLeft = Ui.Px(48);
            int textWidth = Width - textLeft - _toggle.Width - Ui.Px(20);

            // A small screen glyph, lit or not depending on whether this display participates.
            bool on = _toggle.Checked;
            RectangleF screen = new RectangleF(Ui.Px(6), (Height - Ui.Px(24)) / 2f, Ui.Px(30), Ui.Px(21));
            Ui.FillRound(g, on ? Ui.Alpha(T.Accent, 45) : T.Track, screen, 4f * Ui.Scale);
            Ui.DrawRound(g, on ? T.Accent : T.Border, 1.2f * Ui.Scale, screen, 4f * Ui.Scale);
            using (SolidBrush brush = new SolidBrush(on ? T.Accent : T.TextFaint))
                g.FillRectangle(brush, screen.X + screen.Width / 2f - Ui.Px(5), screen.Bottom + Ui.Px(2), Ui.Px(10), Ui.Px(2));

            string title = Target.Name;
            if (Target.IsPrimary) title += "  ·  Primary";

            TextRenderer.DrawText(g, title, Ui.Font(9.5f, FontStyle.Regular),
                new Rectangle(textLeft, Ui.Px(11), textWidth, Ui.Px(20)),
                on ? T.Text : T.TextMuted, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            string detail = Target.Bounds.Width + " × " + Target.Bounds.Height + "  ·  " + Method();
            if (!string.IsNullOrEmpty(Target.Model)) detail = Target.Model + "  ·  " + detail;

            TextRenderer.DrawText(g, detail, Ui.Font(8.25f, FontStyle.Regular),
                new Rectangle(textLeft, Ui.Px(31), textWidth, Ui.Px(20)),
                T.TextMuted, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
