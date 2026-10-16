using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace Nomi
{
    static class ColorUtil
    {
        static readonly Regex Hex = new Regex("^#[0-9A-Fa-f]{6}$");

        public static bool IsHex(string s) { return s != null && Hex.IsMatch(s); }

        public static Color Parse(string hex, Color fallback)
        {
            if (!IsHex(hex)) return fallback;
            int v = int.Parse(hex.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }

        public static string ToHex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        public static Color WithAlpha(Color c, double alpha)
        {
            return Color.FromArgb((byte)Math.Round(255 * Math.Max(0, Math.Min(1, alpha))), c.R, c.G, c.B);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        public static double Luminance(Color c)
        {
            Func<double, double> ch = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); };
            return 0.2126 * ch(c.R) + 0.7152 * ch(c.G) + 0.0722 * ch(c.B);
        }

        // Readable text color on top of the given background.
        public static Color OnColor(Color bg)
        {
            return Luminance(bg) > 0.45 ? Color.FromRgb(0x1C, 0x26, 0x33) : Color.FromRgb(0xFA, 0xF6, 0xEF);
        }

        public static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    // Palette taken from the mascot: cream, night blue, orange. Everything the panels
    // draw comes from these resources, so the settings preview is just a resource swap.
    static class Theme
    {
        public static readonly Color Cream = Color.FromRgb(0xE8, 0xDE, 0xD0);
        public static readonly Color Night = Color.FromRgb(0x26, 0x33, 0x42);
        public static readonly Color Orange = Color.FromRgb(0xF5, 0xA6, 0x23);

        public static void Apply(ResourceDictionary r, Settings s)
        {
            bool dark = s.PanelTheme == "dark";
            Color accent = ColorUtil.Parse(s.Accent, Orange);
            Color bg = ColorUtil.Parse(s.PanelBackground, dark ? Color.FromRgb(0x1B, 0x25, 0x31) : Color.FromRgb(0xF7, 0xF2, 0xEA));
            bool bgDark = ColorUtil.Luminance(bg) < 0.3;
            Color text = bgDark ? Color.FromRgb(0xEE, 0xE8, 0xDE) : Color.FromRgb(0x1E, 0x29, 0x35);
            Color muted = ColorUtil.Mix(text, bg, 0.42);
            Color surface = ColorUtil.Mix(bg, bgDark ? Colors.White : Night, bgDark ? 0.055 : 0.05);
            Color surfaceHover = ColorUtil.Mix(bg, bgDark ? Colors.White : Night, bgDark ? 0.10 : 0.09);
            Color line = ColorUtil.Mix(bg, text, bgDark ? 0.16 : 0.15);

            r["BgBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(bg, s.PanelOpacity));
            r["SurfaceBrush"] = ColorUtil.Brush(surface);
            r["SurfaceHoverBrush"] = ColorUtil.Brush(surfaceHover);
            r["LineBrush"] = ColorUtil.Brush(line);
            r["TextBrush"] = ColorUtil.Brush(text);
            r["MutedBrush"] = ColorUtil.Brush(muted);
            r["AccentBrush"] = ColorUtil.Brush(accent);
            r["AccentSoftBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(accent, bgDark ? 0.18 : 0.20));
            r["OnAccentBrush"] = ColorUtil.Brush(ColorUtil.OnColor(accent));
            r["DangerBrush"] = ColorUtil.Brush(bgDark ? Color.FromRgb(0xF0, 0x8A, 0x6C) : Color.FromRgb(0xB4, 0x3C, 0x1E));
            r["BaseFontSize"] = s.FontSize;
            r["SmallFontSize"] = Math.Max(10, s.FontSize - 1.5);
            r["TitleFontSize"] = s.FontSize + 3;
            r["PanelRadius"] = new CornerRadius(s.Rounded ? 14 : 3);
            r["ControlRadius"] = new CornerRadius(s.Rounded ? 8 : 2);
        }
    }
}
