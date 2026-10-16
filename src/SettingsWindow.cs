using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using WinForms = System.Windows.Forms;

namespace Nomi
{
    // Settings with live preview: every change is applied at once to the mascot and the bubble
    // (shown while this window is open); "Annuler" puts everything back, "Enregistrer" keeps it.
    class SettingsWindow : Window
    {
        public Settings Working;
        Settings original;
        readonly Grid root;
        readonly List<Action> refreshers = new List<Action>();
        readonly Dictionary<string, TextBlock> labels = new Dictionary<string, TextBlock>();
        readonly TextBlock dirtyText, engineStatus;
        readonly TextBox modelPath;
        readonly Border highlightSample;
        bool loading;
        bool saved;

        public event Action<Settings> Preview;
        public event Action<Settings> Saved;
        public event Action<string> CornerRequested;   // "BR", "BL", "TR", "TL"

        static readonly string[] AccentColors = { "#F5A623", "#E98969", "#C2553D", "#3E9C8F", "#4C7DD9", "#6E8B3D", "#D4B13F" };
        static readonly string[] BackgroundColors = { "", "#1B2531", "#263342", "#2A2A2A", "#F7F2EA", "#FFFFFF", "#E8DED0" };
        static readonly string[] HighlightColors = { "#F5A623", "#FFE45C", "#7CD992", "#6CC4FF", "#FF8FB1", "#E98969" };

        public SettingsWindow(Settings current, System.Drawing.Icon icon)
        {
            original = current.Clone();
            Working = current.Clone();
            Title = "Nomi · Paramètres";
            Width = 780;
            Height = Math.Min(860, SystemParameters.WorkArea.Height - 40);
            MinWidth = 560;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            if (icon != null)
                Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

            root = Ui.Load<Grid>("Settings.xaml");
            Content = root;
            dirtyText = Ui.Find<TextBlock>(root, "DirtyText");
            engineStatus = Ui.Find<TextBlock>(root, "EngineStatus");
            modelPath = Ui.Find<TextBox>(root, "ModelPathBox");
            highlightSample = Ui.Find<Border>(root, "HighlightSample");
            Ui.Find<TextBlock>(root, "VersionText").Text = "Nomi " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3) +
                " · tout fonctionne sur cet ordinateur, sans connexion · réglages : " + System.IO.Path.Combine(Settings.Folder, "settings.ini");

            Bind(root);
            Ui.Find<Button>(root, "SaveButton").Click += delegate { saved = true; if (Saved != null) Saved(Working.Clone()); original = Working.Clone(); Close(); };
            Ui.Find<Button>(root, "CancelButton").Click += delegate { Close(); };
            Ui.Find<Button>(root, "OpenLogs").Click += delegate
            {
                try { Directory.CreateDirectory(Log.Folder); Process.Start("explorer.exe", "\"" + Log.Folder + "\""); }
                catch (Exception e) { Log.Error("Open logs", e); }
            };
            Ui.Find<Button>(root, "ResetAll").Click += delegate
            {
                var d = new Settings();
                d.MascotX = Working.MascotX; d.MascotY = Working.MascotY;
                d.PanelX = Working.PanelX; d.PanelY = Working.PanelY;
                Working.CopyFrom(d);
                RefreshAll();
                Changed();
            };
            foreach (var corner in new[] { "BR", "BL", "TR", "TL" })
            {
                string c = corner;
                Ui.Find<Button>(root, "Corner" + c).Click += delegate { if (CornerRequested != null) CornerRequested(c); };
            }
            Ui.Find<Button>(root, "BrowseModel").Click += delegate { BrowseModel(); };
            Ui.Find<Button>(root, "DefaultModel").Click += delegate { Working.ModelPath = ""; RefreshAll(); Changed(); };

            SourceInitialized += delegate { DarkTitleBar(); };
            Closing += delegate { if (!saved && !Working.SameAs(original) && Preview != null) Preview(original.Clone()); };

            RefreshAll();
        }

        public bool WasSaved { get { return saved; } }

        public void SetEngineStatus(string text)
        {
            engineStatus.Text = "Moteur llama.cpp intégré · " + text;
        }

        // The user moved or resized the mascot or the bubble by hand: those values are kept
        // whatever happens to the rest of the settings.
        public void SyncPlacement(Settings from)
        {
            foreach (var target in new[] { Working, original })
            {
                target.MascotX = from.MascotX; target.MascotY = from.MascotY; target.MascotSize = from.MascotSize;
                target.PanelX = from.PanelX; target.PanelY = from.PanelY; target.PanelPlacement = from.PanelPlacement;
                target.PanelWidth = from.PanelWidth; target.PanelHeight = from.PanelHeight;
            }
            RefreshAll();
        }

        // ---------------------------------------------------------------- generic binding

        void Bind(DependencyObject node)
        {
            var fe = node as FrameworkElement;
            string tag = fe != null ? fe.Tag as string : null;
            if (!string.IsNullOrEmpty(tag)) BindOne(fe, tag);
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                var d = child as DependencyObject;
                if (d != null) Bind(d);
            }
        }

        static FieldInfo Field(string name)
        {
            var f = typeof(Settings).GetField(name);
            if (f == null) throw new InvalidOperationException("Réglage inconnu : " + name);
            return f;
        }

        void BindOne(FrameworkElement e, string tag)
        {
            if (tag.StartsWith("value:")) { labels[tag.Substring(6)] = (TextBlock)e; return; }
            if (tag.StartsWith("colors:")) { BuildSwatches((WrapPanel)e, Field(tag.Substring(7))); return; }

            var slider = e as Slider;
            if (slider != null)
            {
                var f = Field(tag);
                refreshers.Add(() => slider.Value = (double)f.GetValue(Working));
                slider.ValueChanged += delegate { if (loading) return; f.SetValue(Working, Math.Round(slider.Value, 2)); Changed(); };
                return;
            }
            var radio = e as RadioButton;
            if (radio != null)
            {
                string[] kv = tag.Split('=');
                var f = Field(kv[0]);
                refreshers.Add(() => radio.IsChecked = Convert.ToString(f.GetValue(Working), CultureInfo.InvariantCulture) == kv[1]);
                radio.Checked += delegate { if (loading) return; f.SetValue(Working, kv[1]); Changed(); };
                return;
            }
            var check = e as CheckBox;
            if (check != null)
            {
                var f = Field(tag);
                refreshers.Add(() => check.IsChecked = (bool)f.GetValue(Working));
                RoutedEventHandler h = delegate { if (loading) return; f.SetValue(Working, check.IsChecked == true); Changed(); };
                check.Checked += h;
                check.Unchecked += h;
                return;
            }
            var text = e as TextBox;
            if (text != null)
            {
                var f = Field(tag);
                refreshers.Add(() => text.Text = (string)f.GetValue(Working));
                text.TextChanged += delegate { if (loading) return; f.SetValue(Working, text.Text); Changed(false); };
            }
        }

        void BuildSwatches(WrapPanel panel, FieldInfo field)
        {
            var swatches = new List<Tuple<Border, string>>();
            string[] palette = field.Name == "Accent" ? AccentColors : field.Name == "HighlightColor" ? HighlightColors : BackgroundColors;
            foreach (string hex in palette)
            {
                string value = hex;
                var ring = new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 6, 6),
                    BorderThickness = new Thickness(2), Cursor = Cursors.Hand, Padding = new Thickness(3),
                    ToolTip = value == "" ? "Selon le thème" : value
                };
                UIElement dot;
                if (value == "")
                {
                    // "Auto": half dark, half light.
                    var g = new Grid();
                    g.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0xF7, 0xF2, 0xEA)) });
                    g.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M 10,0 A 10,10 0 0 1 10,20 Z"), Fill = new SolidColorBrush(Color.FromRgb(0x1B, 0x25, 0x31)), Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Right, Width = 10 });
                    dot = g;
                }
                else dot = new Ellipse { Fill = new SolidColorBrush(ColorUtil.Parse(value, Colors.Gray)), Stroke = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), StrokeThickness = 1 };
                ring.Child = dot;
                ring.MouseLeftButtonUp += delegate { field.SetValue(Working, value); RefreshAll(); Changed(); };
                panel.Children.Add(ring);
                swatches.Add(Tuple.Create(ring, value));
            }

            var other = new Button { Content = "Autre…", Margin = new Thickness(4, 0, 6, 6), Padding = new Thickness(10, 4, 10, 4) };
            other.SetResourceReference(StyleProperty, "Btn");
            other.Click += delegate
            {
                using (var dialog = new WinForms.ColorDialog { FullOpen = true, AnyColor = true })
                {
                    Color c = ColorUtil.Parse((string)field.GetValue(Working), Theme.Orange);
                    dialog.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
                    if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;
                    field.SetValue(Working, ColorUtil.ToHex(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B)));
                    RefreshAll();
                    Changed();
                }
            };
            panel.Children.Add(other);

            var hexBox = new TextBox { Width = 92, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.NoWrap, ToolTip = "Code couleur #RRVVBB" };
            hexBox.SetResourceReference(StyleProperty, "Input");
            Action commit = () =>
            {
                string v = hexBox.Text.Trim();
                if (!v.StartsWith("#")) v = "#" + v;
                if (ColorUtil.IsHex(v)) { field.SetValue(Working, v.ToUpperInvariant()); RefreshAll(); Changed(); }
                else if (hexBox.Text.Trim().Length == 0 && field.Name == "PanelBackground") { field.SetValue(Working, ""); RefreshAll(); Changed(); }
                else hexBox.Text = (string)field.GetValue(Working);
            };
            hexBox.LostKeyboardFocus += delegate { commit(); };
            hexBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { commit(); e.Handled = true; } };
            panel.Children.Add(hexBox);

            refreshers.Add(() =>
            {
                string current = (string)field.GetValue(Working);
                foreach (var sw in swatches)
                    sw.Item1.BorderBrush = string.Equals(sw.Item2, current, StringComparison.OrdinalIgnoreCase)
                        ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
                if (!hexBox.IsKeyboardFocused) hexBox.Text = current;
            });
        }

        void BrowseModel()
        {
            using (var dialog = new WinForms.OpenFileDialog { Filter = "Modèle GGUF (*.gguf)|*.gguf", Title = "Choisir un modèle local" })
            {
                if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;
                Working.ModelPath = dialog.FileName;
                RefreshAll();
                Changed();
            }
        }

        // ---------------------------------------------------------------- refresh

        void RefreshAll()
        {
            loading = true;
            try
            {
                Theme.Apply(Resources, Working);
                Resources["BgBrush"] = ColorUtil.Brush(((SolidColorBrush)Resources["BgBrush"]).Color.ChangeAlpha(255));
                foreach (var r in refreshers) r();
                UpdateLabels();
                string model = LlmEngine.ResolveModel(Working);
                modelPath.Text = string.IsNullOrWhiteSpace(Working.ModelPath)
                    ? "Modèle intégré" + (model != null ? " · " + System.IO.Path.GetFileName(model) : " (introuvable)")
                    : Working.ModelPath + (File.Exists(Working.ModelPath) ? "" : "  (introuvable)");
                Color hl = ColorUtil.Parse(Working.HighlightColor, Theme.Orange);
                highlightSample.Background = new SolidColorBrush(ColorUtil.WithAlpha(hl, Working.HighlightOpacity));
                highlightSample.BorderBrush = new SolidColorBrush(hl);
                highlightSample.Width = 300;
                highlightSample.Height = 26;
                dirtyText.Text = Working.SameAs(original) ? "" : "Modifications non enregistrées";
            }
            finally { loading = false; }
        }

        void UpdateLabels()
        {
            var fr = CultureInfo.GetCultureInfo("fr-FR");
            Func<double, string> minutes = v => v <= 0 ? "Jamais" : v.ToString("0", fr) + " min";
            var formats = new Dictionary<string, Func<double, string>>
            {
                { "MascotSize", v => v.ToString("0", fr) + " px" },
                { "MascotOpacity", v => (v * 100).ToString("0", fr) + " %" },
                { "SleepMinutes", minutes },
                { "PanelOpacity", v => (v * 100).ToString("0", fr) + " %" },
                { "PanelWidth", v => v.ToString("0", fr) + " px" },
                { "PanelHeight", v => v.ToString("0", fr) + " px" },
                { "FontSize", v => v.ToString("0.#", fr) + " pt" },
                { "HighlightOpacity", v => (v * 100).ToString("0", fr) + " %" },
                { "SuggestionCount", v => v.ToString("0", fr) + (v > 1 ? " mails" : " mail") },
                { "MaxTokens", v => "≈ " + (v * 0.75).ToString("0", fr) + " mots" },
                { "Temperature", v => v <= 0.15 ? "Sobre" : v <= 0.5 ? "Équilibrée" : "Inventive" },
                { "Threads", v => v <= 0 ? "Auto" : v.ToString("0", fr) },
                { "UnloadAfterMinutes", minutes }
            };
            foreach (var pair in labels)
            {
                Func<double, string> f;
                double v = Convert.ToDouble(Field(pair.Key).GetValue(Working));
                pair.Value.Text = formats.TryGetValue(pair.Key, out f) ? f(v) : v.ToString(fr);
            }
        }

        void Changed(bool refresh = true)
        {
            Working.Normalize();
            if (refresh) RefreshAll();
            else dirtyText.Text = Working.SameAs(original) ? "" : "Modifications non enregistrées";
            if (Preview != null) Preview(Working.Clone());
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void DarkTitleBar()
        {
            try
            {
                int dark = Working.PanelTheme == "dark" ? 1 : 0;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4);
            }
            catch { }
        }
    }

    static class ColorExtensions
    {
        public static Color ChangeAlpha(this Color c, byte a) { return Color.FromArgb(a, c.R, c.G, c.B); }
    }
}
