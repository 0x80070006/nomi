using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace Nomi
{
    // The floating mascot: always on top, transparent around the drawing (clicks pass through),
    // draggable anywhere, resizable (corner handle or Ctrl + wheel), hides half behind a screen
    // edge when dropped there, falls asleep when left alone.
    class MascotWindow : Window
    {
        const int HotkeyPanel = 0x4E31, HotkeyZone = 0x4E32;
        const double DragThreshold = 5;

        public readonly MascotView View = new MascotView();
        readonly Path grip;
        readonly DispatcherTimer housekeeping;
        Settings settings;

        bool pressed, dragging, resizing;
        Point pressCursor, lastCursor;
        Int32Rect pressBounds;
        double pressSize;
        DateTime lastMove = DateTime.UtcNow;
        DateTime lastInteraction = DateTime.UtcNow;
        bool edgeMode;
        int edgeSide;
        bool hotkeysOn;

        public event Action Clicked;
        public event Action<string> MenuCommand;   // "reply", "zone", "suggestions", "ask", "settings", "hide"
        public event Action PlacementChanged;      // position or size changed by the user
        public event Action HotkeyPanelPressed, HotkeyZonePressed;

        public Settings Settings { get { return settings; } }

        readonly Border badge;
        readonly TextBlock badgeText;

        // Small counter of ready suggestions, on the top right of the mascot.
        public void SetBadge(int count)
        {
            badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            badgeText.Text = count > 9 ? "9+" : count.ToString();
        }

        public void CommitPosition() { UpdateEdge(false); RememberPosition(); }

        public MascotWindow(Settings settings)
        {
            this.settings = settings;
            Title = "Nomi";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            Focusable = false;
            UseLayoutRounding = true;

            grip = new Path
            {
                Data = Geometry.Parse("M 14,2 L 2,14 M 14,7 L 7,14 M 14,12 L 12,14"),
                Stroke = new SolidColorBrush(Color.FromArgb(200, 0x26, 0x33, 0x42)),
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 16, Height = 16
            };
            var gripHost = new Border
            {
                Width = 20, Height = 20,
                Background = new SolidColorBrush(Color.FromArgb(215, 0xF7, 0xF2, 0xEA)),
                CornerRadius = new CornerRadius(5),
                Child = grip,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = Cursors.SizeNWSE,
                Opacity = 0,
                ToolTip = "Glisser pour redimensionner (ou Ctrl + molette)"
            };
            gripHost.MouseLeftButtonDown += OnGripDown;

            badgeText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x26, 0x33, 0x42)),
                FontWeight = FontWeights.Bold, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            badge = new Border
            {
                MinWidth = 20, Height = 20, CornerRadius = new CornerRadius(10), Padding = new Thickness(5, 0, 5, 0),
                Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x33, 0x42)), BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 6, 0), Child = badgeText, Visibility = Visibility.Collapsed,
                ToolTip = "Suggestions de réponse prêtes"
            };

            var root = new Grid();
            root.Children.Add(View);
            root.Children.Add(badge);
            root.Children.Add(gripHost);
            Content = root;

            MouseEnter += delegate { gripHost.Opacity = 1; Touch(); if (View.Sleeping) { View.Sleeping = false; View.Pulse(MascotView.Surprise, 700); } };
            MouseLeave += delegate { if (!resizing) gripHost.Opacity = 0; };
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            LostMouseCapture += delegate { if (pressed || resizing) EndGesture(false); };
            MouseWheel += OnWheel;
            MouseRightButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; ShowMenu(); };

            SourceInitialized += delegate
            {
                Native.AddExStyle(this, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
                HwndSource.FromHwnd(Native.Handle(this)).AddHook(WndProc);
                ApplySettings(this.settings);
            };
            Closed += delegate { SetHotkeys(false); };

            housekeeping = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            housekeeping.Tick += delegate { Housekeeping(); };
            housekeeping.Start();
        }

        public void ApplySettings(Settings s)
        {
            settings = s;
            Width = Height = s.MascotSize;
            Opacity = s.MascotOpacity;
            Topmost = s.AlwaysOnTop;
            View.Animate = s.Animations && SystemParameters.ClientAreaAnimation;
            if (IsLoaded || Native.Handle(this) != IntPtr.Zero)
            {
                SetHotkeys(s.Hotkeys);
                EnsureOnScreen();
            }
        }

        // Places the window at the saved pixel position, or in the bottom-right corner of the
        // main screen when there is none or it is no longer on any screen (monitor unplugged).
        public void PlaceInitially()
        {
            var b = Native.Bounds(this);
            int w = Math.Max(1, b.Width), h = Math.Max(1, b.Height);
            if (!double.IsNaN(settings.MascotX) && !double.IsNaN(settings.MascotY))
            {
                var r = new System.Drawing.Rectangle((int)settings.MascotX, (int)settings.MascotY, w, h);
                foreach (var screen in WinForms.Screen.AllScreens)
                {
                    var visible = System.Drawing.Rectangle.Intersect(screen.WorkingArea, r);
                    if (visible.Width * visible.Height >= w * h / 4)
                    {
                        Native.MoveTo(this, r.X, r.Y);
                        UpdateEdge(false);
                        return;
                    }
                }
            }
            var work = WinForms.Screen.PrimaryScreen.WorkingArea;
            int margin = (int)(24 * Native.Scale(this));
            Native.MoveTo(this, work.Right - w - margin, work.Bottom - h - margin);
        }

        public void ResetPosition()
        {
            settings.MascotX = settings.MascotY = double.NaN;
            PlaceInitially();
            RememberPosition();
        }

        void EnsureOnScreen()
        {
            var b = Native.Bounds(this);
            if (b.IsEmpty) return;
            var r = new System.Drawing.Rectangle(b.X, b.Y, b.Width, b.Height);
            var work = WinForms.Screen.FromRectangle(r).WorkingArea;
            int minVisible = b.Width / 3;
            int x = Math.Max(work.Left - b.Width + minVisible, Math.Min(work.Right - minVisible, b.X));
            int y = Math.Max(work.Top, Math.Min(work.Bottom - b.Height / 2, b.Y));
            if (x != b.X || y != b.Y) Native.MoveTo(this, x, y);
        }

        public void Touch() { lastInteraction = DateTime.UtcNow; }

        // ---------------------------------------------------------------- drag & click

        void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (resizing) return;
            pressed = true;
            dragging = false;
            pressCursor = lastCursor = Native.Cursor();
            pressBounds = Native.Bounds(this);
            CaptureMouse();
            Touch();
            e.Handled = true;
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (resizing) { ResizeMove(); return; }
            if (!pressed) return;
            Point c = Native.Cursor();
            if (!dragging && (Math.Abs(c.X - pressCursor.X) > DragThreshold || Math.Abs(c.Y - pressCursor.Y) > DragThreshold))
            {
                dragging = true;
                if (edgeMode) SetEdge(false, 0);
            }
            if (!dragging) return;

            Native.MoveTo(this, (int)(pressBounds.X + c.X - pressCursor.X), (int)(pressBounds.Y + c.Y - pressCursor.Y));
            double dt = Math.Max(0.016, (DateTime.UtcNow - lastMove).TotalSeconds);
            double vx = (c.X - lastCursor.X) / dt / 900.0, vy = (c.Y - lastCursor.Y) / dt / 900.0;
            View.SetDragging(true, vx, vy);
            lastCursor = c;
            lastMove = DateTime.UtcNow;
            if (PositionMoved != null) PositionMoved();
        }

        public event Action PositionMoved;   // live, while dragging (the panel follows)

        void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (resizing) { EndGesture(true); return; }
            if (!pressed) return;
            bool wasDrag = dragging;
            EndGesture(true);
            if (wasDrag) return;

            if (edgeMode) SlideOut();
            if (Clicked != null) Clicked();
        }

        void EndGesture(bool commit)
        {
            bool wasDrag = dragging, wasResize = resizing;
            pressed = dragging = resizing = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            View.SetDragging(false, 0, 0);
            if (!commit) return;
            if (wasDrag)
            {
                UpdateEdge(true);
                RememberPosition();
            }
            if (wasResize) RememberPosition();
        }

        void RememberPosition()
        {
            var b = Native.Bounds(this);
            if (b.IsEmpty) return;
            settings.MascotX = b.X;
            settings.MascotY = b.Y;
            if (PlacementChanged != null) PlacementChanged();
        }

        // Dropped mostly past the left or right edge: tuck it in, with just the face peeking out.
        void UpdateEdge(bool snap)
        {
            var b = Native.Bounds(this);
            if (b.IsEmpty) return;
            var center = new System.Drawing.Point(b.X + b.Width / 2, b.Y + b.Height / 2);
            var screen = WinForms.Screen.FromPoint(center).Bounds;
            int hiddenRight = b.X + b.Width - screen.Right, hiddenLeft = screen.Left - b.X;
            int side = hiddenRight > b.Width * 0.3 ? 1 : hiddenLeft > b.Width * 0.3 ? -1 : 0;
            if (side == 0) { SetEdge(false, 0); EnsureOnScreen(); return; }
            if (snap)
            {
                int peek = (int)(b.Width * 0.58);
                Native.MoveTo(this, side > 0 ? screen.Right - peek : screen.Left - (b.Width - peek), Math.Max(screen.Top, Math.Min(screen.Bottom - b.Height, b.Y)));
            }
            SetEdge(true, side);
        }

        void SetEdge(bool on, int side)
        {
            edgeMode = on;
            edgeSide = side;
            View.SetEdge(on, side);
        }

        // Back fully on screen (animated), before opening the panel.
        void SlideOut()
        {
            var b = Native.Bounds(this);
            var screen = WinForms.Screen.FromPoint(new System.Drawing.Point(b.X + b.Width / 2, b.Y + b.Height / 2)).WorkingArea;
            int margin = (int)(12 * Native.Scale(this));
            int target = edgeSide > 0 ? screen.Right - b.Width - margin : screen.Left + margin;
            SetEdge(false, 0);
            int start = b.X, y = b.Y, step = 0;
            var anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            anim.Tick += delegate
            {
                step++;
                double u = Math.Min(1, step / 12.0);
                u = 1 - Math.Pow(1 - u, 3);
                Native.MoveTo(this, (int)(start + (target - start) * u), y);
                if (PositionMoved != null) PositionMoved();
                if (step >= 12) { anim.Stop(); RememberPosition(); }
            };
            anim.Start();
        }

        // ---------------------------------------------------------------- resize

        void OnGripDown(object sender, MouseButtonEventArgs e)
        {
            resizing = true;
            pressCursor = Native.Cursor();
            pressSize = settings.MascotSize;
            CaptureMouse();
            e.Handled = true;
        }

        void ResizeMove()
        {
            Point c = Native.Cursor();
            double scale = Native.Scale(this);
            double delta = Math.Max(c.X - pressCursor.X, c.Y - pressCursor.Y) / scale;
            SetSize(pressSize + delta);
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            SetSize(settings.MascotSize + (e.Delta > 0 ? 8 : -8));
            RememberPosition();
            e.Handled = true;
        }

        void SetSize(double size)
        {
            size = Settings.Clamp(Math.Round(size), 64, 320);
            if (size == settings.MascotSize) return;
            settings.MascotSize = size;
            Width = Height = size;
            if (PositionMoved != null) PositionMoved();
        }

        // ---------------------------------------------------------------- menu, hotkeys, housekeeping

        void ShowMenu()
        {
            var menu = new ContextMenu();
            Action<string, string, string> add = (text, command, gesture) =>
            {
                var item = new MenuItem { Header = text, InputGestureText = gesture ?? "" };
                item.Click += delegate { if (MenuCommand != null) MenuCommand(command); };
                menu.Items.Add(item);
            };
            add("Répondre au mail sélectionné", "reply", null);
            add("Répondre à une zone de l'écran…", "zone", settings.Hotkeys ? "Ctrl+Alt+R" : null);
            add("Suggestions Outlook", "suggestions", null);
            add("Poser une question…", "ask", settings.Hotkeys ? "Ctrl+Alt+N" : null);
            menu.Items.Add(new Separator());
            add("Paramètres…", "settings", null);
            add("Masquer Nomi", "hide", null);
            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }

        void SetHotkeys(bool on)
        {
            IntPtr h = Native.Handle(this);
            if (h == IntPtr.Zero || on == hotkeysOn) return;
            const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
            if (on)
            {
                bool a = Native.RegisterHotKey(h, HotkeyPanel, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x4E /* N */);
                bool b = Native.RegisterHotKey(h, HotkeyZone, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x52 /* R */);
                if (!a || !b) Log.Write("Hotkeys: Ctrl+Alt+N " + (a ? "ok" : "already taken") + ", Ctrl+Alt+R " + (b ? "ok" : "already taken"));
            }
            else
            {
                Native.UnregisterHotKey(h, HotkeyPanel);
                Native.UnregisterHotKey(h, HotkeyZone);
            }
            hotkeysOn = on;
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HotkeyPanel && HotkeyPanelPressed != null) { handled = true; HotkeyPanelPressed(); }
                if (id == HotkeyZone && HotkeyZonePressed != null) { handled = true; HotkeyZonePressed(); }
            }
            else if (msg == Native.WM_MOUSEACTIVATE)
            {
                // Clicking the mascot must not take the focus away from Outlook.
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            }
            return IntPtr.Zero;
        }

        void Housekeeping()
        {
            if (!IsVisible) return;
            if (settings.AlwaysOnTop) Native.KeepOnTop(this);
            if (settings.SleepMinutes > 0 && !View.Sleeping && View.Activity == MascotView.Idle && !pressed &&
                DateTime.UtcNow - lastInteraction > TimeSpan.FromMinutes(settings.SleepMinutes))
                View.Sleeping = true;
        }
    }
}
