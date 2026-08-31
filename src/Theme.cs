using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Dimly
{
    /// <summary>A complete colour set. Every painted pixel in Dimly comes from one of these.</summary>
    public sealed class Theme
    {
        private Theme() { }

        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Tagline { get; private set; }

        /// <summary>True when the theme wants luminous edges: accent-tinted borders and knob halos.</summary>
        public bool Glow { get; private set; }

        public Color Window { get; private set; }
        public Color Sidebar { get; private set; }
        public Color Card { get; private set; }
        public Color CardHover { get; private set; }
        public Color Border { get; private set; }
        public Color BorderSoft { get; private set; }
        public Color Text { get; private set; }
        public Color TextMuted { get; private set; }
        public Color TextFaint { get; private set; }
        public Color Accent { get; private set; }
        public Color AccentDeep { get; private set; }
        public Color AccentAlt { get; private set; }
        public Color OnAccent { get; private set; }
        public Color Track { get; private set; }

        /// <summary>The switch knob when a toggle is off - it must read against <see cref="Track"/>.</summary>
        public Color KnobOff { get; private set; }

        public bool IsDark { get; private set; }

        private static readonly Theme[] Catalogue = new Theme[]
        {
            new Theme
            {
                Id = "midnight", Name = "Midnight", Tagline = "Deep slate, calm blue", Glow = false,
                Window = Rgb(0x12141C), Sidebar = Rgb(0x0D0F16), Card = Rgb(0x1A1D28), CardHover = Rgb(0x212532),
                Border = Rgb(0x272C3B), BorderSoft = Rgb(0x1F2331),
                Text = Rgb(0xE8EBF4), TextMuted = Rgb(0x99A1B6), TextFaint = Rgb(0x6A7288),
                Accent = Rgb(0x6E8CFF), AccentDeep = Rgb(0x4A5CC9), AccentAlt = Rgb(0xA78BFA),
                OnAccent = Rgb(0x0A0D18), Track = Rgb(0x2A2F41), KnobOff = Rgb(0x9AA3B8), IsDark = true
            },
            new Theme
            {
                Id = "neon", Name = "Neon", Tagline = "Black glass, electric cyan", Glow = true,
                Window = Rgb(0x07080E), Sidebar = Rgb(0x04050A), Card = Rgb(0x0C1017), CardHover = Rgb(0x121A28),
                Border = Rgb(0x1B2C40), BorderSoft = Rgb(0x142032),
                Text = Rgb(0xE6FBFF), TextMuted = Rgb(0x7E9AB0), TextFaint = Rgb(0x4E6579),
                Accent = Rgb(0x00E5FF), AccentDeep = Rgb(0x00A9C4), AccentAlt = Rgb(0xFF3DD1),
                OnAccent = Rgb(0x00131A), Track = Rgb(0x10202F), KnobOff = Rgb(0x5C7C93), IsDark = true
            },
            new Theme
            {
                Id = "daylight", Name = "Daylight", Tagline = "Clean white, crisp ink", Glow = false,
                Window = Rgb(0xF3F5FA), Sidebar = Rgb(0xFFFFFF), Card = Rgb(0xFFFFFF), CardHover = Rgb(0xF2F5FC),
                Border = Rgb(0xE3E8F2), BorderSoft = Rgb(0xEDF0F7),
                Text = Rgb(0x161923), TextMuted = Rgb(0x5F6880), TextFaint = Rgb(0x8B93A7),
                Accent = Rgb(0x3B5BFF), AccentDeep = Rgb(0x2C48E6), AccentAlt = Rgb(0x7C4DFF),
                OnAccent = Rgb(0xFFFFFF), Track = Rgb(0xDEE4F0), KnobOff = Rgb(0xFFFFFF), IsDark = false
            }
        };

        public static IList<Theme> All { get { return Catalogue; } }

        public static Theme Find(string id)
        {
            foreach (Theme theme in Catalogue)
                if (string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)) return theme;
            return null;
        }

        private static Theme _current = Catalogue[0];

        public static Theme Current
        {
            get { return _current; }
            set
            {
                if (value == null || value == _current) return;
                _current = value;
                EventHandler handler = CurrentChanged;
                if (handler != null) handler(null, EventArgs.Empty);
            }
        }

        public static event EventHandler CurrentChanged;

        private static Color Rgb(int packed)
        {
            return Color.FromArgb(255, (packed >> 16) & 0xFF, (packed >> 8) & 0xFF, packed & 0xFF);
        }
    }

    /// <summary>Shared drawing primitives and the one scale factor the whole UI is built on.</summary>
    public static class Ui
    {
        private static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();
        private static string _family = "Segoe UI";

        /// <summary>Design pixels to device pixels. Everything in the layout is multiplied by this.</summary>
        public static float Scale { get; private set; }

        static Ui()
        {
            Scale = 1f;
        }

        /// <summary>
        /// Fixes the scale for the session. Dimly is system-DPI aware, so this happens once;
        /// the factor is trimmed if the window would otherwise not fit the screen.
        /// </summary>
        public static void Initialize(float dpiScale, int designHeight, int availableHeight)
        {
            float fits = availableHeight / (float)designHeight;
            Scale = Math.Max(0.85f, Math.Min(dpiScale, fits));
        }

        public static int Px(double design)
        {
            return (int)Math.Round(design * Scale);
        }

        public static Font Font(float size, FontStyle style)
        {
            string key = size.ToString("0.##") + "/" + (int)style;
            Font font;
            if (Fonts.TryGetValue(key, out font)) return font;

            font = new Font(_family, size * Scale, style, GraphicsUnit.Point);
            Fonts[key] = font;
            return font;
        }

        public static GraphicsPath Round(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float limit = Math.Min(bounds.Width, bounds.Height) / 2f;
            radius = Math.Max(0f, Math.Min(radius, limit));

            if (radius <= 0.5f)
            {
                path.AddRectangle(bounds);
                return path;
            }

            float diameter = radius * 2f;
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Color color, RectangleF bounds, float radius)
        {
            using (GraphicsPath path = Round(bounds, radius))
            using (SolidBrush brush = new SolidBrush(color))
                g.FillPath(brush, path);
        }

        public static void DrawRound(Graphics g, Color color, float width, RectangleF bounds, float radius)
        {
            RectangleF inset = RectangleF.Inflate(bounds, -width / 2f, -width / 2f);
            using (GraphicsPath path = Round(inset, radius - width / 2f))
            using (Pen pen = new Pen(color, width))
                g.DrawPath(pen, path);
        }

        /// <summary>A soft outward halo, used by the Neon theme for active controls.</summary>
        public static void Halo(Graphics g, Color color, RectangleF bounds, float radius, int rings)
        {
            for (int ring = rings; ring >= 1; ring--)
            {
                RectangleF spread = RectangleF.Inflate(bounds, ring * Scale, ring * Scale);
                DrawRound(g, Alpha(color, 40 / ring), 1.4f * Scale, spread, radius + ring * Scale);
            }
        }

        public static Color Alpha(Color color, int alpha)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), color);
        }

        public static Color Mix(Color from, Color to, double amount)
        {
            amount = Math.Max(0.0, Math.Min(1.0, amount));
            return Color.FromArgb(
                (int)Math.Round(from.R + (to.R - from.R) * amount),
                (int)Math.Round(from.G + (to.G - from.G) * amount),
                (int)Math.Round(from.B + (to.B - from.B) * amount));
        }

        public static void Quality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }
    }
}
