using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Nomi
{
    // The "Petit explorateur" mascot, rebuilt from mascot.svg (240×240 coordinates, same named
    // parts) and animated procedurally: one timer evaluates the pose of every part for the
    // current state, which keeps the 12 states and their blending in one readable place.
    class MascotView : Viewbox
    {
        public const string Idle = "idle", Listen = "listen", Think = "think", Speak = "speak", Success = "success",
            Error = "error", Surprise = "surprise", Sleep = "sleep", Loading = "loading", Greeting = "greeting",
            Drag = "drag", Edge = "edge";

        static readonly Color Ink = C("#263342"), Cream = C("#E8DED0"), Orange = C("#F5A623");

        // Parts and their transforms.
        readonly Canvas floatRig = new Canvas(), antennaRig = new Canvas(), leftSensor = new Canvas(), rightSensor = new Canvas(),
            head = new Canvas(), eyes = new Canvas(), cheeks = new Canvas(), brows = new Canvas(),
            sweat = new Canvas(), sleepBubble = new Canvas(), looseScrew = new Canvas();
        Path eyeL, eyeR, browL, browR, mouth;
        readonly Rig rigFloat, rigAntenna, rigLeftSensor, rigRightSensor, rigHead, rigEyes, rigEyeL, rigEyeR, rigBrows;

        readonly DispatcherTimer timer;
        readonly DateTime born = DateTime.UtcNow;
        readonly Random random = new Random();
        string activity = Idle;      // what Nomi is doing (idle, listen, think, loading, speak)
        string oneShot;              // success, error, surprise, greeting
        DateTime oneShotUntil;
        DateTime stateStart = DateTime.UtcNow;
        string shown;
        double nextBlink = 2.5;
        bool dragging, sleeping, edge;
        int edgeSide = 1;            // +1 right edge, -1 left edge
        double dragX, dragY;
        bool animate = true;

        public MascotView()
        {
            Stretch = Stretch.Uniform;
            // Extra room around the 240×240 drawing so jumps and bounces are never clipped.
            var stage = new Canvas { Width = 280, Height = 280, ClipToBounds = false };
            floatRig.RenderTransform = Nothing();
            Canvas.SetLeft(floatRig, 20);
            Canvas.SetTop(floatRig, 26);
            stage.Children.Add(floatRig);
            Child = stage;

            Build();
            rigFloat = new Rig(floatRig, 120, 130);
            rigAntenna = new Rig(antennaRig, 120, 51);
            rigLeftSensor = new Rig(leftSensor, 38, 128);
            rigRightSensor = new Rig(rightSensor, 202, 128);
            rigHead = new Rig(head, 120, 123);
            rigEyes = new Rig(eyes, 120, 124);
            rigEyeL = new Rig(eyeL, 93, 124);
            rigEyeR = new Rig(eyeR, 147, 124);
            rigBrows = new Rig(brows, 120, 102);

            timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            timer.Tick += delegate { Frame(); };
            Loaded += delegate { timer.Start(); Frame(); };
            Unloaded += delegate { timer.Stop(); };
            IsVisibleChanged += delegate { if (IsVisible) timer.Start(); else timer.Stop(); };
        }

        public string Activity { get { return activity; } }
        public string CurrentState { get { return shown ?? Idle; } }

        public bool Animate
        {
            get { return animate; }
            set { animate = value; Frame(); }
        }

        public void SetActivity(string state)
        {
            if (activity == state) return;
            activity = state;
            if (state != Idle) sleeping = false;
        }

        public void Pulse(string state, int ms = 1300)
        {
            oneShot = state;
            oneShotUntil = DateTime.UtcNow.AddMilliseconds(ms);
            sleeping = false;
        }

        public void SetDragging(bool on, double vx, double vy)
        {
            dragging = on;
            dragX = Math.Max(-1, Math.Min(1, vx));
            dragY = Math.Max(-1, Math.Min(1, vy));
            if (on) sleeping = false;
        }

        public void SetEdge(bool on, int side) { edge = on; edgeSide = side >= 0 ? 1 : -1; }
        public bool Sleeping { get { return sleeping; } set { sleeping = value; } }

        string Effective()
        {
            if (dragging) return Drag;
            if (oneShot != null && DateTime.UtcNow < oneShotUntil) return oneShot;
            oneShot = null;
            if (activity != Idle) return activity;
            if (edge) return Edge;
            if (sleeping) return Sleep;
            return Idle;
        }

        // ------------------------------------------------------------------ drawing

        void Build()
        {
            // Antenna
            antennaRig.Children.Add(Stroke("M120 51 Q119 39 124 29", "#344252", 8));
            antennaRig.Children.Add(Stroke("M120 51 Q119 39 124 29", "#697582", 3));
            antennaRig.Children.Add(Circle(126, 23, 11, "#F5A623", "#263342", 4));
            antennaRig.Children.Add(Stroke("M122 19 Q126 15 130 20", "#FFD17A", 2.5));
            Add(floatRig, antennaRig);

            foreach (var pair in new[] { new { c = leftSensor, x = 38.0 }, new { c = rightSensor, x = 202.0 } })
            {
                pair.c.Children.Add(Ellipse(pair.x, 128, 17, 28, "#D5C9B8", "#263342", 5));
                pair.c.Children.Add(Ellipse(pair.x, 128, 9, 19, "#F5A623", "#263342", 3));
                pair.c.Children.Add(Circle(pair.x, 128, 4, "#FFE0A0", null, 0));
                Add(floatRig, pair.c);
            }

            // Head
            var shell = Shape("M48 92 Q52 58 91 58 L149 58 Q188 61 192 94 L198 145 Q195 180 162 188 L78 188 Q45 181 42 149 Z", "#E8DED0", "#263342", 6);
            shell.StrokeLineJoin = PenLineJoin.Round;
            head.Children.Add(shell);
            head.Children.Add(Stroke("M59 89 Q65 69 94 68 L147 68 Q174 70 181 94", "#F8F1E8", 7));
            head.Children.Add(Stroke("M55 160 Q62 177 82 179", "#C9BDAE", 4));
            head.Children.Add(Stroke("M69 77 l-7 6 M172 169 l-6 5", "#B6AA9B", 3));
            head.Children.Add(Circle(61, 104, 3.2, "#59616A", null, 0));
            head.Children.Add(Circle(179, 104, 3.2, "#59616A", null, 0));
            head.Children.Add(new Path
            {
                Data = Frozen(new RectangleGeometry(new Rect(57, 83, 126, 82), 31, 31)),
                Fill = B("#263342"), Stroke = B("#1C2836"), StrokeThickness = 4
            });

            browL = Stroke("M80 104 Q92 96 104 102", "#F5A623", 5);
            browR = Stroke("M136 102 Q148 96 160 104", "#F5A623", 5);
            brows.Children.Add(browL);
            brows.Children.Add(browR);
            head.Children.Add(brows);

            eyeL = Ellipse(93, 124, 10, 15, "#F5A623", null, 0);
            eyeR = Ellipse(147, 124, 10, 15, "#F5A623", null, 0);
            eyes.Children.Add(eyeL);
            eyes.Children.Add(eyeR);
            head.Children.Add(eyes);

            cheeks.Children.Add(Ellipse(72, 143, 9, 5, "#E98969", null, 0));
            cheeks.Children.Add(Ellipse(168, 143, 9, 5, "#E98969", null, 0));
            cheeks.Opacity = 0;
            head.Children.Add(cheeks);

            mouth = Stroke(MouthRest, "#F5A623", 4);
            mouth.StrokeLineJoin = PenLineJoin.Round;
            head.Children.Add(mouth);

            head.Children.Add(Circle(57, 166, 4.2, "#B5A797", "#263342", 2));
            head.Children.Add(Stroke("M55 166 h4", "#263342", 1.5));
            head.Effect = new DropShadowEffect { Color = C("#182638"), Opacity = 0.22, BlurRadius = 8, ShadowDepth = 5, Direction = 270 };
            Add(floatRig, head);

            sweat.Children.Add(Shape("M183 88 C183 88 174 100 174 105 A9 9 0 0 0 192 105 C192 100 183 88 183 88Z", "#8EC9C9", "#263342", 2.5));
            sleepBubble.Children.Add(Circle(188, 80, 5, "#D8E8E6", "#263342", 2));
            sleepBubble.Children.Add(Circle(199, 68, 3, "#D8E8E6", "#263342", 1.5));
            looseScrew.Children.Add(Circle(211, 178, 5, "#B5A797", "#263342", 2));
            looseScrew.Children.Add(Stroke("M208 178 h6", "#263342", 1.5));
            foreach (var c in new[] { sweat, sleepBubble, looseScrew }) { c.Opacity = 0; Add(floatRig, c); }
        }

        const string MouthRest = "M113 143 Q120 149 127 143";
        static readonly Dictionary<string, Geometry> geometryCache = new Dictionary<string, Geometry>();
        static readonly string[] MouthTalk = { "M113 143 Q120 149 127 143", "M111 140 Q120 157 129 140", "M116 141 Q120 137 124 141", "M108 141 Q120 155 132 141" };

        static Geometry G(string data)
        {
            Geometry g;
            if (!geometryCache.TryGetValue(data, out g))
            {
                g = Geometry.Parse(data);
                g.Freeze();
                geometryCache[data] = g;
            }
            return g;
        }

        static Path Shape(string data, string fill, string stroke, double thickness)
        {
            var p = new Path { Data = G(data), StrokeThickness = thickness };
            if (fill != null) p.Fill = B(fill);
            if (stroke != null) p.Stroke = B(stroke);
            return p;
        }

        static Path Stroke(string data, string color, double thickness)
        {
            return new Path
            {
                Data = G(data), Stroke = B(color), StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
        }

        static Path Ellipse(double cx, double cy, double rx, double ry, string fill, string stroke, double thickness)
        {
            var p = new Path { Data = Frozen(new EllipseGeometry(new Point(cx, cy), rx, ry)), Fill = B(fill), StrokeThickness = thickness };
            if (stroke != null) p.Stroke = B(stroke);
            return p;
        }

        static Path Circle(double cx, double cy, double r, string fill, string stroke, double thickness)
        {
            return Ellipse(cx, cy, r, r, fill, stroke, thickness);
        }

        static void Add(Canvas parent, UIElement child) { parent.Children.Add(child); }
        static Geometry Frozen(Geometry g) { g.Freeze(); return g; }
        static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        static readonly Dictionary<string, Brush> brushCache = new Dictionary<string, Brush>();
        static Brush B(string hex)
        {
            Brush b;
            if (!brushCache.TryGetValue(hex, out b)) { b = new SolidColorBrush(C(hex)); b.Freeze(); brushCache[hex] = b; }
            return b;
        }
        static Transform Nothing() { return Transform.Identity; }

        // ------------------------------------------------------------------ animation

        // A part's pose: translate, rotate and scale around a fixed pivot (SVG coordinates).
        class Rig
        {
            readonly ScaleTransform scale;
            readonly RotateTransform rotate;
            readonly TranslateTransform translate;
            public double X, Y, Angle, Sx = 1, Sy = 1;

            public Rig(UIElement e, double cx, double cy)
            {
                scale = new ScaleTransform(1, 1, cx, cy);
                rotate = new RotateTransform(0, cx, cy);
                translate = new TranslateTransform();
                var g = new TransformGroup();
                g.Children.Add(scale);
                g.Children.Add(rotate);
                g.Children.Add(translate);
                e.RenderTransform = g;
            }

            public void Reset() { X = Y = Angle = 0; Sx = Sy = 1; }

            public void Commit()
            {
                if (scale.ScaleX != Sx) scale.ScaleX = Sx;
                if (scale.ScaleY != Sy) scale.ScaleY = Sy;
                if (rotate.Angle != Angle) rotate.Angle = Angle;
                if (translate.X != X) translate.X = X;
                if (translate.Y != Y) translate.Y = Y;
            }
        }

        // Smooth keyframe interpolation: t in seconds, keys as (time, value) pairs.
        static double Key(double t, params double[] kv)
        {
            if (t <= kv[0]) return kv[1];
            for (int i = 2; i < kv.Length; i += 2)
            {
                if (t <= kv[i])
                {
                    double t0 = kv[i - 2], t1 = kv[i];
                    double u = (t - t0) / Math.Max(1e-6, t1 - t0);
                    u = u * u * (3 - 2 * u);
                    return kv[i - 1] + (kv[i + 1] - kv[i - 1]) * u;
                }
            }
            return kv[kv.Length - 1];
        }

        // Ease-in-out oscillation between a and b with the given period.
        static double Wave(double time, double period, double a, double b)
        {
            double u = 0.5 - 0.5 * Math.Cos(2 * Math.PI * time / period);
            return a + (b - a) * u;
        }

        // The little mechanical "tick" of the antenna: still, then a quick wiggle.
        static double Tick(double time, double period)
        {
            double p = (time % period) / period;
            return p < 0.85 ? 0 : Key(p, 0.85, 0, 0.88, -4, 0.92, 3, 1.0, 0);
        }

        void Frame()
        {
            string state = Effective();
            if (state != shown)
            {
                shown = state;
                stateStart = DateTime.UtcNow;
            }
            double T = (DateTime.UtcNow - born).TotalSeconds;
            double t = (DateTime.UtcNow - stateStart).TotalSeconds;
            if (!animate) { T = 0; t = 10; }

            foreach (var r in new[] { rigFloat, rigAntenna, rigLeftSensor, rigRightSensor, rigHead, rigEyes, rigEyeL, rigEyeR, rigBrows }) r.Reset();
            double sweatO = 0, bubbleO = 0, screwO = 0, cheeksO = 0, rightSensorO = 1, leftSensorO = 1, rigO = 1;
            string mouthData = MouthRest, browLData = "M80 104 Q92 96 104 102", browRData = "M136 102 Q148 96 160 104";

            // Base levitation and antenna sway, shared by most states.
            rigFloat.Y = Wave(T, 3, 0, -6);
            rigAntenna.Angle = Wave(T, 2.7, -3, 4);

            switch (state)
            {
                case Idle:
                    if (animate && T > nextBlink)
                    {
                        double b = T - nextBlink;
                        double k = b < 0.08 ? 1 - b / 0.08 * 0.92 : b < 0.16 ? 0.08 + (b - 0.08) / 0.08 * 0.92 : 1;
                        rigEyes.Sy = k;
                        if (b >= 0.16) nextBlink = T + 2.2 + random.NextDouble() * 4.2 + (random.NextDouble() < 0.2 ? -1.8 : 0);
                    }
                    rigEyes.X = Wave(T, 7.3, -2, 2.2);   // gaze drifting
                    break;

                case Listen:
                    rigFloat.Y = Wave(T, 1.2, 0, -6);
                    rigHead.Angle = -8;
                    rigHead.Y = Wave(T, 1.8, 0, -2);
                    rigAntenna.Y = -4;
                    rigAntenna.Angle = 7;
                    rigLeftSensor.Sx = rigRightSensor.Sx = 1.12;
                    rigEyes.Sx = rigEyes.Sy = 1.13;
                    break;

                case Think:
                    rigHead.Angle = Wave(T, 2.1, 5, 2);
                    rigHead.Y = Wave(T, 2.1, 0, -2);
                    browLData = "M80 108 Q92 105 104 110";
                    browRData = "M136 100 Q148 88 160 97";
                    rigEyeL.Sy = 0.62;
                    rigEyeR.Sx = rigEyeR.Sy = 0.78;
                    rigAntenna.Angle = Tick(T, 0.9);
                    sweatO = 1;
                    break;

                case Speak:
                    rigFloat.Y = Wave(T, 0.42, 0, -3);
                    mouthData = MouthTalk[(int)(T / 0.175) % MouthTalk.Length];
                    break;

                case Success:
                    rigFloat.Y = Key(t, 0, 0, 0.21, 5, 0.47, -20, 0.66, 2, 0.85, 0);
                    rigFloat.Sx = Key(t, 0, 1, 0.21, 1.08, 0.47, 0.92, 0.66, 1.03, 0.85, 1);
                    rigFloat.Sy = Key(t, 0, 1, 0.21, 0.88, 0.47, 1.08, 0.66, 0.96, 0.85, 1);
                    rigBrows.Y = -5;
                    rigEyeL.Sy = rigEyeR.Sy = 0.45;
                    cheeksO = 1;
                    rigAntenna.Angle = t < 1.05 ? Tick(t + 0.3, 0.35) * 1.5 : 0;
                    break;

                case Error:
                    rigFloat.Y = Key(t, 0, 0, 0.25, 8, 0.38, 5, 0.7, 0);
                    rigFloat.Angle = Key(t, 0, 0, 0.25, -4, 0.38, 3, 0.7, 0);
                    rigEyeL.Angle = 45; rigEyeL.Sy = 0.7;
                    rigEyeR.Angle = -45; rigEyeR.Sy = 0.7;
                    mouthData = "M111 149 Q120 137 129 149";
                    rigAntenna.Angle = -28;
                    screwO = 1;
                    break;

                case Surprise:
                    rigFloat.Y = Key(t, 0, 0, 0.14, 8, 0.32, -9, 0.7, 0);
                    rigFloat.Sx = Key(t, 0, 1, 0.14, 1.08, 0.32, 0.96, 0.7, 1);
                    rigFloat.Sy = Key(t, 0, 1, 0.14, 0.9, 0.32, 1.06, 0.7, 1);
                    rigEyes.Sx = rigEyes.Sy = 1.35;
                    mouthData = "M114 141 A6 7 0 1 0 126 141 A6 7 0 1 0 114 141";
                    rigAntenna.Angle = t < 0.5 ? Tick(t + 0.21, 0.25) * 2 : 0;
                    break;

                case Sleep:
                    rigFloat.Y = Wave(T, 3.5, 5, 8);
                    rigFloat.Sx = Wave(T, 3.5, 1, 1.015);
                    rigFloat.Sy = Wave(T, 3.5, 1, 0.99);
                    rigEyeL.Sy = rigEyeR.Sy = 0.12;
                    rigAntenna.Angle = -42;
                    bubbleO = 1;
                    mouthData = "M114 144 Q120 148 126 144";
                    break;

                case Loading:
                    double p = (T % 0.8) / 0.8;
                    rigFloat.Y = Key(p, 0, 0, 0.3, 3, 1, 0);
                    rigFloat.Sx = Key(p, 0, 1, 0.3, 1.02, 1, 1);
                    rigFloat.Sy = Key(p, 0, 1, 0.3, 0.98, 1, 1);
                    rigEyes.X = 4 * Math.Sin(T * 2.4);   // following an imaginary dot
                    mouthData = "M115 142 A5 5 0 1 0 125 142 A5 5 0 1 0 115 142";
                    rigAntenna.Angle = Tick(T, 0.7);
                    break;

                case Greeting:
                    rigFloat.Y = Key(t, 0, 35, 0.5, -8, 0.7, 3, 0.9, 0);
                    rigFloat.Sx = rigFloat.Sy = Key(t, 0, 0.65, 0.5, 1.08, 0.7, 0.97, 0.9, 1);
                    rigO = Key(t, 0, 0, 0.5, 1);
                    if (t < 0.8) rigEyes.Sy = Key(t, 0, 1, 0.35, 1, 0.37, 0.08, 0.42, 1);
                    if (t > 0.7) rigHead.Angle = Tick(t - 0.7 + 0.47, 0.55) * 1.2;
                    break;

                case Drag:
                    double s = Wave(T, 0.55, 0, 1);
                    rigFloat.Sx = (1 + 0.09 * s) * (1 + dragX * 0.07);
                    rigFloat.Sy = (1 - 0.06 * s) * (1 + dragY * 0.04);
                    rigFloat.Angle = dragX * 6;
                    rigEyes.Angle = -12;
                    rigEyes.Sx = rigEyes.Sy = 0.9;
                    rigAntenna.Angle = 18 - dragX * 10;
                    break;

                case Edge:
                    rigFloat.X = edgeSide * Wave(T, 1.8, 0, 6);
                    rigEyes.X = -4 * edgeSide;
                    rigEyes.Sx = rigEyes.Sy = 0.9;
                    if (edgeSide > 0) rightSensorO = 0.25; else leftSensorO = 0.25;
                    break;
            }

            foreach (var r in new[] { rigFloat, rigAntenna, rigLeftSensor, rigRightSensor, rigHead, rigEyes, rigEyeL, rigEyeR, rigBrows }) r.Commit();
            SetOpacity(sweat, sweatO);
            SetOpacity(sleepBubble, bubbleO);
            SetOpacity(looseScrew, screwO);
            SetOpacity(cheeks, cheeksO);
            SetOpacity(rightSensor, rightSensorO);
            SetOpacity(leftSensor, leftSensorO);
            SetOpacity(floatRig, rigO);
            if (bubbleO > 0) sleepBubble.RenderTransform = new TranslateTransform(0, -2 * Math.Abs(Math.Sin(T * 1.6)));
            SetData(mouth, mouthData);
            SetData(browL, browLData);
            SetData(browR, browRData);

            // Sleep is slow: no need for 30 frames per second.
            var interval = TimeSpan.FromMilliseconds(!animate ? 250 : state == Sleep ? 66 : 33);
            if (timer.Interval != interval) timer.Interval = interval;
        }

        static void SetOpacity(UIElement e, double v) { if (e.Opacity != v) e.Opacity = v; }
        static void SetData(Path p, string data) { var g = G(data); if (p.Data != g) p.Data = g; }
    }
}
