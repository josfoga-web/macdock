using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MacDock
{
    // "Agitar el puntero para localizarlo" de macOS: al mover el mouse rapido de lado a lado, el puntero crece un momento.
    class CursorShake : Form
    {
        System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 16 };
        VsyncTimer anim; Stopwatch clk = Stopwatch.StartNew();
        LayeredCanvas canvas;
        readonly Queue<KeyValuePair<double, Point>> trail = new Queue<KeyValuePair<double, Point>>();
        float scale = 1f, target = 1f; double lastShake = -10, lastT;
        readonly float dpi;
        const int Box = 160;   // tamano del lienzo del puntero grande (a 1x)

        public CursorShake(float dpi)
        {
            this.dpi = dpi;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-500, -500, 1, 1);
            anim = new VsyncTimer(this); anim.Tick += (o, e) => Frame();
            poll.Tick += (o, e) => Poll();
        }

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8; return cp; } }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnShown(EventArgs e) { base.OnShown(e); N.ShowWindow(Handle, 0); poll.Start(); }

        void Poll()
        {
            double now = clk.Elapsed.TotalSeconds;
            var p = Cursor.Position;
            trail.Enqueue(new KeyValuePair<double, Point>(now, p));
            while (trail.Count > 0 && now - trail.Peek().Key > 0.55) trail.Dequeue();
            // cuenta cambios de direccion horizontales amplios en el ultimo medio segundo
            int flips = 0, dir = 0; float path = 0; Point prev = Point.Empty; bool first = true; int segStartX = p.X;
            foreach (var kv in trail)
            {
                if (!first)
                {
                    int dx = kv.Value.X - prev.X;
                    path += Math.Abs(dx);
                    int d = Math.Sign(dx);
                    if (d != 0 && d != dir)
                    {
                        if (dir != 0 && Math.Abs(prev.X - segStartX) > 60 * dpi) flips++;
                        dir = d; segStartX = prev.X;
                    }
                }
                prev = kv.Value; first = false;
            }
            if (flips >= 3 && path > 1100 * dpi) { lastShake = now; if (target < 3.2f) { target = 3.6f; Start(); } }
            else if (target > 1f && now - lastShake > 0.35) { target = 1f; Start(); }
            if (scale > 1.01f && !anim.Enabled) Draw();   // seguir al puntero
        }

        void Start()
        {
            lastT = clk.Elapsed.TotalSeconds;
            if (!Visible || !N.IsWindowVisible(Handle)) { N.ShowWindow(Handle, 4); N.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); }
            anim.Start();
        }

        void Frame()
        {
            double now = clk.Elapsed.TotalSeconds; float dt = (float)Math.Min(0.05, now - lastT); lastT = now;
            scale = Ease.Approach(scale, target, dt, target > scale ? 0.06f : 0.09f);
            if (Math.Abs(scale - target) < 0.01f) scale = target;
            Draw();
            if (scale == target) { anim.Stop(); if (scale <= 1f) { N.ShowWindow(Handle, 0); if (canvas != null) { canvas.Dispose(); canvas = null; } } }
        }

        void Draw()
        {
            int size = (int)(Box * dpi);
            if (canvas == null) canvas = new LayeredCanvas(size, size);
            using (var g = Graphics.FromImage(canvas.Bitmap))
            {
                g.Clear(Color.Transparent);
                if (scale > 1.02f)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float k = scale * dpi;
                    // flecha generica: punta en (0,0)
                    var pts = new[] { new PointF(0, 0), new PointF(0, 17), new PointF(4.2f, 13.2f), new PointF(7, 19.5f), new PointF(9.6f, 18.4f), new PointF(6.9f, 12.2f), new PointF(12.3f, 12.2f) };
                    for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(2 * dpi + pts[i].X * k, 2 * dpi + pts[i].Y * k);
                    using (var path = new GraphicsPath()) { path.AddPolygon(pts);
                        using (var sh = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) { g.TranslateTransform(1.5f * k, 2f * k); g.FillPath(sh, path); g.ResetTransform(); }
                        g.FillPath(Brushes.Black, path);
                        using (var pen = new Pen(Color.White, Math.Max(1.2f, 1.3f * k)) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path); }
                }
            }
            var c = Cursor.Position;
            canvas.Push(Handle, c.X - (int)(2 * dpi), c.Y - (int)(2 * dpi));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { poll.Stop(); anim.Close(); if (canvas != null) { canvas.Dispose(); canvas = null; } }
            base.Dispose(disposing);
        }
    }
}
