using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WinForms = System.Windows.Forms;

namespace Nomi
{
    enum PanelMode { Reply, Suggestions, Ask }

    // Nomi's bubble: pick what to answer, tune the tone, read and edit the answer, then send it
    // to Outlook. It sits next to the mascot, or anywhere once moved by its header.
    class AssistantPanel : Window
    {
        readonly Border shell;
        readonly FrameworkElement header, replyArea, suggestionsArea, askArea, suggestionsOff, suggestionsEmpty, grip;
        readonly RadioButton tabReply, tabSuggestions, tabAsk;
        readonly TextBlock engineText, targetKind, targetTitle, targetDetail, statusText, outputHint;
        readonly Ellipse engineDot;
        readonly TextBox instruction, question, output;
        readonly Button generate, insert, copy, regenerate, stop, ask, useSelection, zone, reattach, enableSuggestions;
        readonly WrapPanel tones;
        readonly ListBox suggestionList;
        Settings settings;
        bool busy;
        bool dragging, resizing;
        Point pressCursor;
        Int32Rect pressBounds;
        Size pressSize;

        public event Action CloseRequested, GenerateRequested, RegenerateRequested, InsertRequested, StopRequested,
            UseSelectionRequested, ZoneRequested, AskRequested, EnableSuggestionsRequested, LayoutEdited;
        public event Action<PanelMode> ModeChanged;
        public event Action<Suggestion> SuggestionPicked;

        public Settings Settings { get { return settings; } }

        public AssistantPanel(Settings settings)
        {
            this.settings = settings;
            Title = "Nomi";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            shell = Ui.Load<Border>("Panel.xaml");
            Content = shell;
            header = F<FrameworkElement>("Header");
            replyArea = F<FrameworkElement>("ReplyArea");
            suggestionsArea = F<FrameworkElement>("SuggestionsArea");
            askArea = F<FrameworkElement>("AskArea");
            suggestionsOff = F<FrameworkElement>("SuggestionsOff");
            suggestionsEmpty = F<FrameworkElement>("SuggestionsEmpty");
            grip = F<FrameworkElement>("Grip");
            tabReply = F<RadioButton>("TabReply");
            tabSuggestions = F<RadioButton>("TabSuggestions");
            tabAsk = F<RadioButton>("TabAsk");
            engineText = F<TextBlock>("EngineText");
            engineDot = F<Ellipse>("EngineDot");
            targetKind = F<TextBlock>("TargetKind");
            targetTitle = F<TextBlock>("TargetTitle");
            targetDetail = F<TextBlock>("TargetDetail");
            statusText = F<TextBlock>("StatusText");
            outputHint = F<TextBlock>("OutputHint");
            instruction = F<TextBox>("Instruction");
            question = F<TextBox>("Question");
            output = F<TextBox>("Output");
            generate = F<Button>("GenerateButton");
            insert = F<Button>("InsertButton");
            copy = F<Button>("CopyButton");
            regenerate = F<Button>("RegenerateButton");
            stop = F<Button>("StopButton");
            ask = F<Button>("AskButton");
            useSelection = F<Button>("UseSelectionButton");
            zone = F<Button>("ZoneButton");
            reattach = F<Button>("ReattachButton");
            enableSuggestions = F<Button>("EnableSuggestionsButton");
            tones = F<WrapPanel>("Tones");
            suggestionList = F<ListBox>("SuggestionList");

            foreach (string tone in Prompts.Tones)
            {
                var chip = new RadioButton { Content = Prompts.ToneLabel(tone), Tag = tone, GroupName = "tone" };
                chip.Style = (Style)FindResource("Chip");
                tones.Children.Add(chip);
            }

            F<Button>("CloseButton").Click += delegate { Raise(CloseRequested); };
            generate.Click += delegate { Raise(GenerateRequested); };
            regenerate.Click += delegate { Raise(RegenerateRequested); };
            insert.Click += delegate { Raise(InsertRequested); };
            stop.Click += delegate { Raise(StopRequested); };
            ask.Click += delegate { Raise(AskRequested); };
            useSelection.Click += delegate { Raise(UseSelectionRequested); };
            zone.Click += delegate { Raise(ZoneRequested); };
            enableSuggestions.Click += delegate { Raise(EnableSuggestionsRequested); };
            copy.Click += delegate { CopyOutput(); };
            reattach.Click += delegate
            {
                this.settings.PanelPlacement = "mascot";
                reattach.Visibility = Visibility.Collapsed;
                Raise(LayoutEdited);
            };
            foreach (var tab in new[] { tabReply, tabSuggestions, tabAsk })
                tab.Checked += delegate { ShowMode(Mode); if (ModeChanged != null) ModeChanged(Mode); };
            suggestionList.SelectionChanged += delegate
            {
                var s = suggestionList.SelectedItem as Suggestion;
                if (s != null && SuggestionPicked != null) SuggestionPicked(s);
            };
            output.TextChanged += delegate { outputHint.Visibility = output.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateButtons(); };
            instruction.KeyDown += (s, e) => { if (e.Key == Key.Enter && !busy) { e.Handled = true; Raise(GenerateRequested); } };
            question.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && !busy) { e.Handled = true; Raise(AskRequested); }
            };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; Raise(CloseRequested); } };

            header.MouseLeftButtonDown += OnHeaderDown;
            grip.MouseLeftButtonDown += OnGripDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += delegate { EndGesture(); };
            LostMouseCapture += delegate { EndGesture(); };
            SourceInitialized += delegate { Native.AddExStyle(this, Native.WS_EX_TOOLWINDOW); };

            ApplySettings(settings);
            SetTarget(null, "Sélectionnez un mail dans Outlook, ou surlignez une zone de l'écran.");
            UpdateButtons();
        }

        T F<T>(string name) where T : class { return Ui.Find<T>(shell, name); }

        static void Raise(Action a) { if (a != null) a(); }

        // ---------------------------------------------------------------- state shown

        public void ApplySettings(Settings s)
        {
            settings = s;
            Theme.Apply(Resources, s);
            Width = s.PanelWidth;
            Height = s.PanelHeight;
            reattach.Visibility = s.PanelPlacement == "free" ? Visibility.Visible : Visibility.Collapsed;
            foreach (RadioButton chip in tones.Children)
                if ((string)chip.Tag == s.DefaultTone && Tone == null) chip.IsChecked = true;
            if (Tone == null) ((RadioButton)tones.Children[0]).IsChecked = true;
            suggestionsOff.Visibility = s.OutlookSuggestions ? Visibility.Collapsed : Visibility.Visible;
            suggestionList.Visibility = s.OutlookSuggestions ? Visibility.Visible : Visibility.Collapsed;
        }

        public PanelMode Mode
        {
            get { return tabSuggestions.IsChecked == true ? PanelMode.Suggestions : tabAsk.IsChecked == true ? PanelMode.Ask : PanelMode.Reply; }
            set
            {
                if (value == PanelMode.Suggestions) tabSuggestions.IsChecked = true;
                else if (value == PanelMode.Ask) tabAsk.IsChecked = true;
                else tabReply.IsChecked = true;
                ShowMode(value);
            }
        }

        void ShowMode(PanelMode m)
        {
            replyArea.Visibility = m == PanelMode.Reply ? Visibility.Visible : Visibility.Collapsed;
            suggestionsArea.Visibility = m == PanelMode.Suggestions ? Visibility.Visible : Visibility.Collapsed;
            askArea.Visibility = m == PanelMode.Ask ? Visibility.Visible : Visibility.Collapsed;
            insert.Content = m == PanelMode.Ask ? "Copier la réponse" : "Ouvrir dans Outlook";
            UpdateButtons();
        }

        public string Tone
        {
            get
            {
                foreach (RadioButton chip in tones.Children) if (chip.IsChecked == true) return (string)chip.Tag;
                return null;
            }
        }

        public string Instruction { get { return instruction.Text; } }
        public string Question { get { return question.Text; } }
        public string Output { get { return output.Text; } set { output.Text = value ?? ""; output.ScrollToHome(); } }
        public bool CanInsert;   // set by the controller: is there a mail to reply to?

        public void Append(string chunk)
        {
            output.AppendText(chunk);
            output.ScrollToEnd();
        }

        public void SetTarget(MailItem mail, string emptyMessage)
        {
            targetKind.Text = "MAIL CIBLÉ";
            if (mail == null)
            {
                targetTitle.Text = "Aucun mail";
                targetDetail.Text = emptyMessage ?? "";
                return;
            }
            targetTitle.Text = mail.Subject;
            string when = mail.Received == DateTime.MinValue ? "" : " · " + mail.Received.ToString("ddd d MMM HH:mm");
            targetDetail.Text = (string.IsNullOrEmpty(mail.Sender) ? "" : mail.Sender) + when;
        }

        public void SetZoneTarget(string excerpt, MailItem context)
        {
            targetKind.Text = "ZONE SURLIGNÉE" + (context != null ? " · " + Shorten(context.Subject, 40) : "");
            string flat = (excerpt ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            targetTitle.Text = flat.Length == 0 ? "(aucun texte reconnu)" : "« " + Shorten(flat, 90) + " »";
            targetDetail.Text = flat.Length > 90 ? Shorten(flat.Substring(90), 160) : "";
        }

        static string Shorten(string s, int max) { return s.Length <= max ? s : s.Substring(0, max).TrimEnd() + "…"; }

        public void SetBusy(bool on, string status)
        {
            busy = on;
            stop.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            output.IsReadOnly = on;
            SetStatus(status, false);
            UpdateButtons();
        }

        public bool Busy { get { return busy; } }

        public void SetStatus(string text, bool error)
        {
            statusText.Text = text ?? "";
            statusText.Foreground = (Brush)FindResource(error ? "DangerBrush" : "MutedBrush");
        }

        public void SetEngine(EngineState state, string text)
        {
            engineText.Text = text;
            string key = state == EngineState.Ready ? "AccentBrush" : state == EngineState.Failed ? "DangerBrush" : "MutedBrush";
            engineDot.Fill = (Brush)FindResource(key);
            engineDot.ToolTip = text;
        }

        void UpdateButtons()
        {
            bool hasText = output.Text.Trim().Length > 0;
            generate.IsEnabled = !busy;
            ask.IsEnabled = !busy;
            regenerate.IsEnabled = !busy && Mode != PanelMode.Suggestions;
            regenerate.Visibility = Mode == PanelMode.Ask ? Visibility.Collapsed : Visibility.Visible;
            insert.IsEnabled = !busy && hasText && (Mode == PanelMode.Ask || CanInsert);
            copy.IsEnabled = hasText;
        }

        public void Refresh() { UpdateButtons(); }

        public void SetSuggestions(ObservableCollection<Suggestion> items)
        {
            if (suggestionList.ItemsSource != items) suggestionList.ItemsSource = items;
            suggestionsEmpty.Visibility = settings.OutlookSuggestions && items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            tabSuggestions.Content = items.Count > 0 ? "Suggestions · " + items.Count : "Suggestions";
        }

        public void SelectSuggestion(Suggestion s) { suggestionList.SelectedItem = s; }

        public void FocusInput()
        {
            Activate();
            if (Mode == PanelMode.Ask) question.Focus();
            else if (Mode == PanelMode.Reply) instruction.Focus();
        }

        void CopyOutput()
        {
            string text = Mode == PanelMode.Ask ? output.Text : Prompts.CleanAnswer(output.Text);
            if (CopyToClipboard(text)) SetStatus("Copié dans le presse-papiers.", false);
            else SetStatus("Le presse-papiers est occupé, réessayez.", true);
        }

        public static bool CopyToClipboard(string text)
        {
            for (int i = 0; i < 6; i++)
            {
                try { Clipboard.SetDataObject(text ?? "", true); return true; }
                catch (COMException) { Thread.Sleep(60); }   // another app is holding the clipboard
                catch (ExternalException) { Thread.Sleep(60); }
            }
            return false;
        }

        // ---------------------------------------------------------------- placement

        // Next to the mascot (left of it on the right half of the screen, right of it otherwise),
        // bottoms aligned, always fully inside the work area.
        public void PlaceNear(Int32Rect mascot)
        {
            if (settings.PanelPlacement == "free" && PlaceFree()) return;
            for (int pass = 0; pass < 2; pass++)   // second pass once the target monitor's DPI applies
            {
                double scale = Native.Scale(this);
                int w = (int)(Width * scale), h = (int)(Height * scale), gap = (int)(10 * scale);
                var center = new System.Drawing.Point(mascot.X + mascot.Width / 2, mascot.Y + mascot.Height / 2);
                var screen = WinForms.Screen.FromPoint(center);
                var work = screen.WorkingArea;
                bool leftSide = center.X > work.Left + work.Width / 2;
                int x = leftSide ? mascot.X - w - gap : mascot.X + mascot.Width + gap;
                int y = mascot.Y + mascot.Height - h;
                x = Math.Max(work.Left + gap, Math.Min(work.Right - w - gap, x));
                y = Math.Max(work.Top + gap, Math.Min(work.Bottom - h - gap, y));
                Native.MoveTo(this, x, y);
            }
        }

        bool PlaceFree()
        {
            if (double.IsNaN(settings.PanelX) || double.IsNaN(settings.PanelY)) return false;
            var b = Native.Bounds(this);
            var r = new System.Drawing.Rectangle((int)settings.PanelX, (int)settings.PanelY, Math.Max(1, b.Width), Math.Max(1, b.Height));
            foreach (var screen in WinForms.Screen.AllScreens)
            {
                var visible = System.Drawing.Rectangle.Intersect(screen.WorkingArea, r);
                if (visible.Width >= r.Width / 2 && visible.Height >= 40)
                {
                    Native.MoveTo(this, r.X, r.Y);
                    return true;
                }
            }
            return false;
        }

        void OnHeaderDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject && IsInsideButton((DependencyObject)e.OriginalSource)) return;
            dragging = true;
            pressCursor = Native.Cursor();
            pressBounds = Native.Bounds(this);
            CaptureMouse();
            e.Handled = true;
        }

        static bool IsInsideButton(DependencyObject d)
        {
            for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is Button) return true;
            return false;
        }

        void OnGripDown(object sender, MouseButtonEventArgs e)
        {
            resizing = true;
            pressCursor = Native.Cursor();
            pressSize = new Size(Width, Height);
            CaptureMouse();
            e.Handled = true;
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragging && !resizing) return;
            Point c = Native.Cursor();
            if (dragging)
            {
                Native.MoveTo(this, (int)(pressBounds.X + c.X - pressCursor.X), (int)(pressBounds.Y + c.Y - pressCursor.Y));
            }
            else
            {
                double scale = Native.Scale(this);
                Width = Settings.Clamp(pressSize.Width + (c.X - pressCursor.X) / scale, 300, 900);
                Height = Settings.Clamp(pressSize.Height + (c.Y - pressCursor.Y) / scale, 320, 1000);
            }
        }

        void EndGesture()
        {
            if (!dragging && !resizing) return;
            bool moved = dragging;
            dragging = resizing = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (moved)
            {
                var b = Native.Bounds(this);
                if (b.X == pressBounds.X && b.Y == pressBounds.Y) return;
                settings.PanelPlacement = "free";
                settings.PanelX = b.X;
                settings.PanelY = b.Y;
                reattach.Visibility = Visibility.Visible;
            }
            else
            {
                settings.PanelWidth = Math.Round(Width);
                settings.PanelHeight = Math.Round(Height);
            }
            Raise(LayoutEdited);
        }
    }
}
