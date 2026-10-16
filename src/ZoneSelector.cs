using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using WpfPoint = System.Windows.Point;
using WpfColor = System.Windows.Media.Color;
using Rectangle = System.Drawing.Rectangle;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Nomi
{
    class ZoneResult
    {
        public Rectangle Pixels;      // selected area, screen pixels
        public Bitmap Image;          // what was on screen there when the selection started
        public string ProcessName;    // app under the selection ("OUTLOOK", ...)
    }

    // Assisted manual reply: the user drags a rectangle over any part of the screen. The area is
    // highlighted with the configured color while it is drawn, and stays highlighted (click-
    // through) while Nomi reads and answers it. The screen is captured before the overlay shows,
    // so the highlight never ends up in the recognized image.
    class ZoneSelector
    {
        Settings settings;
        readonly List<Overlay> overlays = new List<Overlay>();
        readonly Dictionary<WinForms.Screen, Bitmap> shots = new Dictionary<WinForms.Screen, Bitmap>();
        TaskCompletionSource<ZoneResult> tcs;
        Overlay kept;
        DispatcherTimer keepTimer;

        public ZoneSelector(Settings settings) { this.settings = settings; }

        public void Preview(Settings s) { settings = s; }

        public bool Active { get { return tcs != null && !tcs.Task.IsCompleted; } }

        public Task<ZoneResult> PickAsync()
        {
            ReleaseHighlight();
            tcs = new TaskCompletionSource<ZoneResult>();
            try
            {
                foreach (var screen in WinForms.Screen.AllScreens)
                {
                    var b = screen.Bounds;
                    var shot = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(b.Location, System.Drawing.Point.Empty, b.Size);
                    shots[screen] = shot;
                }
                foreach (var screen in WinForms.Screen.AllScreens)
                {
                    var o = new Overlay(this, screen);
                    overlays.Add(o);
                    o.Show();
                }
                // Keyboard focus on the screen under the cursor, for Escape.
                var cursor = Native.Cursor();
                foreach (var o in overlays)
                    if (o.Screen.Bounds.Contains((int)cursor.X, (int)cursor.Y)) { o.Activate(); o.Focus(); }
            }
            catch (Exception e)
            {
                Log.Error("Zone overlay", e);
                Finish(null, null);
            }
            return tcs.Task;
        }

        internal void Finish(Overlay from, Rectangle? pixels)
        {
            if (tcs == null || tcs.Task.IsCompleted) return;
            ZoneResult result = null;
            if (from != null && pixels.HasValue)
            {
                try
                {
                    var p = pixels.Value;
                    var shot = shots[from.Screen];
                    var local = new Rectangle(p.X - from.Screen.Bounds.X, p.Y - from.Screen.Bounds.Y, p.Width, p.Height);
                    local.Intersect(new Rectangle(0, 0, shot.Width, shot.Height));
                    if (local.Width > 2 && local.Height > 2)
                        result = new ZoneResult
                        {
                            Pixels = p,
                            Image = shot.Clone(local, PixelFormat.Format32bppArgb),
                            ProcessName = Native.ProcessAt(p.X + p.Width / 2, p.Y + p.Height / 2)
                        };
                }
                catch (Exception e) { Log.Error("Zone crop", e); }
            }

            foreach (var o in overlays)
            {
                if (o == from && result != null) { kept = o; o.KeepAsHighlight(); }
                else o.Close();
            }
            overlays.Clear();
            foreach (var shot in shots.Values) shot.Dispose();
            shots.Clear();

            if (kept != null)
            {
                // Safety net: the highlight never stays forever.
                keepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
                keepTimer.Tick += delegate { ReleaseHighlight(); };
                keepTimer.Start();
            }
            tcs.TrySetResult(result);
        }

        // Fades out the highlight left on screen after a selection.
        public void ReleaseHighlight()
        {
            if (keepTimer != null) { keepTimer.Stop(); keepTimer = null; }
            if (kept != null) { kept.FadeOutAndClose(); kept = null; }
        }

        internal WpfColor Highlight { get { return ColorUtil.Parse(settings.HighlightColor, Theme.Orange); } }
        internal double HighlightOpacity { get { return settings.HighlightOpacity; } }

        // One full-screen overlay per monitor, so each one renders at its own DPI.
        internal class Overlay : Window
        {
            readonly ZoneSelector owner;
            public readonly WinForms.Screen Screen;
            readonly Canvas canvas = new Canvas();
            readonly System.Windows.Shapes.Path dim;
            readonly System.Windows.Shapes.Rectangle mark;
            readonly Border hint;
            bool selecting;
            WpfPoint startPx;
            Rectangle current;
            double scale = 1;

            public Overlay(ZoneSelector owner, WinForms.Screen screen)
            {
                this.owner = owner;
                Screen = screen;
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false;
                Topmost = true;
                Background = new SolidColorBrush(WpfColor.FromArgb(1, 0, 0, 0));   // invisible but catches the mouse
                Cursor = Cursors.Cross;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -32000; Top = -32000; Width = 10; Height = 10;   // real bounds set in pixels below

                WpfColor c = owner.Highlight;
                dim = new System.Windows.Shapes.Path { Fill = new SolidColorBrush(WpfColor.FromArgb(46, 10, 16, 24)), IsHitTestVisible = false };
                mark = new System.Windows.Shapes.Rectangle
                {
                    Fill = new SolidColorBrush(ColorUtil.WithAlpha(c, owner.HighlightOpacity)),
                    Stroke = new SolidColorBrush(c),
                    StrokeThickness = 2,
                    RadiusX = 3, RadiusY = 3,
                    Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false
                };
                var hintText = new TextBlock
                {
                    Text = "Surlignez le passage auquel répondre  ·  Échap pour annuler",
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(0xEE, 0xE8, 0xDE)),
                    FontSize = 13,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI")
                };
                hint = new Border
                {
                    Background = new SolidColorBrush(WpfColor.FromArgb(235, 0x1B, 0x25, 0x31)),
                    BorderBrush = new SolidColorBrush(c),
                    BorderThickness = new Thickness(0, 0, 0, 2),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16, 9, 16, 9),
                    Child = hintText,
                    IsHitTestVisible = false
                };
                canvas.Children.Add(dim);
                canvas.Children.Add(mark);
                canvas.Children.Add(hint);
                Content = canvas;

                SourceInitialized += delegate
                {
                    Native.AddExStyle(this, Native.WS_EX_TOOLWINDOW);
                    var b = screen.Bounds;
                    Native.SetBounds(this, b.X, b.Y, b.Width, b.Height);
                };
                // Moving onto a monitor with another DPI makes WPF rescale the window: cover the screen again.
                Action cover = () => { var b = screen.Bounds; Native.SetBounds(this, b.X, b.Y, b.Width, b.Height); };
                DpiChanged += delegate { Dispatcher.BeginInvoke(cover, DispatcherPriority.Send); };
                Loaded += delegate { cover(); Layout(); };
                SizeChanged += delegate { Layout(); };
                MouseLeftButtonDown += OnDown;
                MouseMove += OnMove;
                MouseLeftButtonUp += OnUp;
                MouseRightButtonUp += delegate { owner.Finish(null, null); };
                PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) owner.Finish(null, null); };

            }

            void Layout()
            {
                scale = Native.Scale(this);
                dim.Data = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
                hint.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(hint, Math.Max(0, (ActualWidth - hint.DesiredSize.Width) / 2));
                Canvas.SetTop(hint, 28);
            }

            WpfPoint ToLocal(double px, double py)
            {
                return new WpfPoint((px - Screen.Bounds.X) / scale, (py - Screen.Bounds.Y) / scale);
            }

            void OnDown(object sender, MouseButtonEventArgs e)
            {
                selecting = true;
                startPx = Native.Cursor();
                current = Rectangle.Empty;
                CaptureMouse();
                hint.Visibility = Visibility.Collapsed;
            }

            void OnMove(object sender, MouseEventArgs e)
            {
                if (!selecting) return;
                WpfPoint c = Native.Cursor();
                // Stay within this monitor.
                var b = Screen.Bounds;
                double cx = Math.Max(b.Left, Math.Min(b.Right - 1, c.X)), cy = Math.Max(b.Top, Math.Min(b.Bottom - 1, c.Y));
                int x = (int)Math.Min(startPx.X, cx), y = (int)Math.Min(startPx.Y, cy);
                current = new Rectangle(x, y, (int)Math.Abs(cx - startPx.X), (int)Math.Abs(cy - startPx.Y));

                WpfPoint tl = ToLocal(current.X, current.Y);
                var r = new Rect(tl.X, tl.Y, current.Width / scale, current.Height / scale);
                Canvas.SetLeft(mark, r.X);
                Canvas.SetTop(mark, r.Y);
                mark.Width = r.Width;
                mark.Height = r.Height;
                mark.Visibility = Visibility.Visible;
                dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)), new RectangleGeometry(r, 3, 3));
            }

            void OnUp(object sender, MouseButtonEventArgs e)
            {
                if (!selecting) return;
                selecting = false;
                if (IsMouseCaptured) ReleaseMouseCapture();
                if (current.Width < 12 || current.Height < 8)
                {
                    // A click rather than a selection: let the user try again.
                    mark.Visibility = Visibility.Collapsed;
                    dim.Data = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
                    hint.Visibility = Visibility.Visible;
                    return;
                }
                owner.Finish(this, current);
            }

            // After the selection: only the colored mark stays, and the mouse goes through.
            public void KeepAsHighlight()
            {
                dim.Visibility = Visibility.Collapsed;
                hint.Visibility = Visibility.Collapsed;
                Background = System.Windows.Media.Brushes.Transparent;
                Cursor = null;
                Native.AddExStyle(this, Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE);
            }

            public void FadeOutAndClose()
            {
                int step = 0;
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                t.Tick += delegate
                {
                    step++;
                    Opacity = Math.Max(0, 1 - step / 10.0);
                    if (step >= 10) { t.Stop(); Close(); }
                };
                t.Start();
            }
        }
    }
}
