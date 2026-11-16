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

namespace MacDock
{
    // Arrastre de ventanas como en macOS: la ventana sigue al puntero 1:1 en cada refresco de la pantalla
    // (hasta 180 Hz) y su barra de titulo estilo Mac se mueve en el mismo cuadro, sin desfase.
    static class WindowDrag
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        static VsyncTimer timer; static IntPtr win, comp; static Point grab, compOff;

        public static bool Active { get { return timer != null; } }

        public static void Start(Control owner, IntPtr h, IntPtr companion)
        {
            Stop();
            win = h; comp = companion;
            var c = Cursor.Position;
            N.RECT r; N.GetWindowRect(h, out r);
            if (N.IsZoomed(h))
            {
                // arrastrar una ventana maximizada la restaura bajo el puntero, manteniendo la proporcion
                float ratio = (c.X - r.left) / (float)Math.Max(1, r.right - r.left);
                N.ShowWindow(h, 9);
                N.GetWindowRect(h, out r);
                int w = r.right - r.left, top = Screen.FromPoint(c).WorkingArea.Top;
                N.SetWindowPos(h, IntPtr.Zero, c.X - (int)(w * ratio), top, 0, 0, 0x0001 | 0x0004 | 0x0010);
                N.GetWindowRect(h, out r);
            }
            EdgeTiler.BeginExternal(h);   // recupera el tamano si estaba acomodada y muestra las zonas al llegar a un borde
            N.GetWindowRect(h, out r);
            grab = new Point(c.X - r.left, c.Y - r.top);
            if (comp != IntPtr.Zero) { N.RECT cr; N.GetWindowRect(comp, out cr); compOff = new Point(cr.left - r.left, cr.top - r.top); }
            timer = new VsyncTimer(owner);
            timer.Tick += (o, e) => Tick();
            timer.Start();
        }

        static void Tick()
        {
            int vk = GetSystemMetrics(23) != 0 ? 0x02 : 0x01;   // botones invertidos para zurdos
            if ((GetAsyncKeyState(vk) & 0x8000) == 0) { var h = win; Stop(); EdgeTiler.EndExternal(h); return; }
            var c = Cursor.Position;
            int x = c.X - grab.X, y = Math.Max(Screen.FromPoint(c).WorkingArea.Top - grab.Y + 4, c.Y - grab.Y);
            N.SetWindowPos(win, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200);
            if (comp != IntPtr.Zero) N.SetWindowPos(comp, IntPtr.Zero, x + compOff.X, y + compOff.Y, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200);
        }

        public static void Stop()
        {
            if (timer == null) return;
            timer.Stop(); timer.Close(); timer = null;
        }
    }
}

namespace MacDock
{
    // Efecto "genio" de macOS al minimizar: la ventana se curva, se estrecha y entra al icono de su app en el dock.
    // Al restaurarla desde el dock sale del icono con la animacion inversa.
    static class Genie
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out N.RECT r, int size);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct ANIMATIONINFO { public int cbSize, iMinAnimate; }
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] static extern bool SpiAnim(int a, int u, ref ANIMATIONINFO p, int f);

        public static bool Enabled = true;
        public static Func<IntPtr, Rectangle> TargetFor;   // lo conecta el dock: rectangulo del icono de la app en pantalla
        static readonly Dictionary<IntPtr, KeyValuePair<Bitmap, Rectangle>> shots = new Dictionary<IntPtr, KeyValuePair<Bitmap, Rectangle>>();
        static int animOff;   // minimizaciones en curso con la animacion de Windows apagada

        static Rectangle Frame(IntPtr h)
        {
            N.RECT r;
            if (DwmGetWindowAttribute(h, 9, out r, 16) != 0) N.GetWindowRect(h, out r);
            return Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
        }

        static Bitmap Capture(IntPtr h, Rectangle frame)
        {
            N.RECT wr; N.GetWindowRect(h, out wr);
            int w = wr.right - wr.left, hh = wr.bottom - wr.top;
            if (w < 40 || hh < 40 || w > 8000 || hh > 8000) return null;
            using (var full = new Bitmap(w, hh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(full))
                {
                    IntPtr dc = g.GetHdc();
                    bool ok;
                    try { ok = PrintWindow(h, dc, 2); } finally { g.ReleaseHdc(dc); }
                    if (!ok) return null;
                }
                // solo la parte visible (sin los bordes invisibles de Windows 11), reducida para animar rapido
                var crop = new Rectangle(frame.Left - wr.left, frame.Top - wr.top, frame.Width, frame.Height);
                crop.Intersect(new Rectangle(0, 0, w, hh));
                float k = Math.Min(1f, 1100f / Math.Max(crop.Width, crop.Height));
                var b = new Bitmap(Math.Max(1, (int)(crop.Width * k)), Math.Max(1, (int)(crop.Height * k)), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(full, new Rectangle(0, 0, b.Width, b.Height), crop, GraphicsUnit.Pixel);
                }
                return b;
            }
        }

        static void SystemAnim(bool on)
        {
            var ai = new ANIMATIONINFO { cbSize = 8 };
            if (!on) { if (animOff++ == 0) { SpiAnim(0x48, 8, ref ai, 0); savedAnim = ai.iMinAnimate; ai.iMinAnimate = 0; SpiAnim(0x49, 8, ref ai, 0); } }
            else if (--animOff <= 0) { animOff = 0; ai.iMinAnimate = savedAnim; SpiAnim(0x49, 8, ref ai, 0); }
        }
        static int savedAnim = 1;

        public static void Minimize(IntPtr h)
        {
            if (!Enabled || TargetFor == null || N.IsIconic(h)) { N.ShowWindow(h, 6); return; }
            try
            {
                var frame = Frame(h);
                var shot = Capture(h, frame);
                if (shot == null) { N.ShowWindow(h, 6); return; }
                var target = TargetFor(h);
                Remember(h, shot, frame);
                var fx = new GenieForm(shot, frame, target, false);
                fx.Shown += (o, e) =>
                {
                    // la ventana real desaparece al instante; la animacion la hace la capa
                    SystemAnim(false);
                    N.ShowWindow(h, 6);
                    var t = new System.Windows.Forms.Timer { Interval = 250 };
                    t.Tick += (o2, e2) => { t.Stop(); t.Dispose(); SystemAnim(true); };
                    t.Start();
                };
                fx.Show();
            }
            catch (Exception ex) { Program.Log(ex); N.ShowWindow(h, 6); }
        }

        // devuelve true si hizo la animacion (y la ventana se restaura al final)
        public static bool Restore(IntPtr h)
        {
            KeyValuePair<Bitmap, Rectangle> kv;
            if (!Enabled || TargetFor == null || !shots.TryGetValue(h, out kv)) return false;
            shots.Remove(h);
            try
            {
                var fx = new GenieForm(kv.Key, kv.Value, TargetFor(h), true);
                fx.FormClosed += (o, e) =>
                {
                    SystemAnim(false);
                    N.ShowWindow(h, 9); N.SetForegroundWindow(h);
                    var t = new System.Windows.Forms.Timer { Interval = 250 };
                    t.Tick += (o2, e2) => { t.Stop(); t.Dispose(); SystemAnim(true); };
                    t.Start();
                    kv.Key.Dispose();
                };
                fx.Show();
                return true;
            }
            catch (Exception ex) { Program.Log(ex); return false; }
        }

        static void Remember(IntPtr h, Bitmap shot, Rectangle frame)
        {
            KeyValuePair<Bitmap, Rectangle> old;
            if (shots.TryGetValue(h, out old)) old.Key.Dispose();
            shots[h] = new KeyValuePair<Bitmap, Rectangle>(shot, frame);
            if (shots.Count > 24)   // no guardar capturas de ventanas que ya no existen
                foreach (var k in new List<IntPtr>(shots.Keys)) if (!N.IsIconic(k)) { shots[k].Key.Dispose(); shots.Remove(k); }
        }

        public static void RestoreAnimOnExit() { if (animOff > 0) { animOff = 1; SystemAnim(true); } }
    }

    class GenieForm : Form
    {
        readonly Bitmap img; readonly Rectangle src, dst; readonly bool reverse;
        readonly Rectangle box; LayeredCanvas canvas; VsyncTimer anim; Stopwatch clk = new Stopwatch();
        const double Dur = 0.52;   // duracion del genio en macOS (aprox.)
        const int Strips = 64;

        public GenieForm(Bitmap img, Rectangle src, Rectangle dst, bool reverse)
        {
            this.img = img; this.src = src; this.dst = dst; this.reverse = reverse;
            box = Rectangle.Union(src, dst); box.Inflate(4, 4);
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Bounds = box;
            anim = new VsyncTimer(this); anim.Tick += (o, e) => Frame();
        }

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8; return cp; } }
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnShown(EventArgs e)
        {
            canvas = new LayeredCanvas(box.Width, box.Height);
            Draw(reverse ? 1f : 0f);
            base.OnShown(e);
            clk.Start(); anim.Start();
        }

        void Frame()
        {
            double p = clk.Elapsed.TotalSeconds / Dur;
            if (p >= 1) { anim.Stop(); Close(); return; }
            Draw((float)(reverse ? 1 - p : p));
        }

        static float Smooth(float x) { x = x < 0 ? 0 : x > 1 ? 1 : x; return x * x * (3 - 2 * x); }

        // t = 0: ventana completa; t = 1: dentro del icono.
        // Cada franja horizontal avanza con retraso segun su altura: la parte de abajo se estira primero hacia el icono
        // formando el embudo, y el resto de la ventana pasa despues por el (como el genio de macOS).
        void Draw(float t)
        {
            float sx = src.Left - box.Left, sy = src.Top - box.Top, sw = src.Width, sh = src.Height;
            float tx = dst.Left - box.Left, ty = dst.Top - box.Top, tw = dst.Width, th = dst.Height;
            bool down = dst.Top + dst.Height / 2 >= src.Top + src.Height / 2;
            int strips = Math.Max(64, Math.Min(220, src.Height / 4));
            const float lag = 0.62f;
            using (var g = Graphics.FromImage(canvas.Bitmap))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                float ih = img.Height / (float)strips;
                for (int i = 0; i < strips; i++)
                {
                    float v0 = i / (float)strips, v1 = (i + 1) / (float)strips;
                    // si el dock estuviera arriba, lidera la parte superior
                    float lead0 = down ? 1 - v0 : v0, lead1 = down ? 1 - v1 : v1;
                    float q0 = Smooth(t * (1 + lag) - lead0 * lag), q1 = Smooth(t * (1 + lag) - lead1 * lag);
                    float y0 = (sy + v0 * sh) + ((ty + v0 * th) - (sy + v0 * sh)) * q0;
                    float y1 = (sy + v1 * sh) + ((ty + v1 * th) - (sy + v1 * sh)) * q1;
                    float k = Smooth((q0 + q1) / 2 * 1.35f);
                    float l = sx + (tx - sx) * k, r = sx + sw + (tx + tw - sx - sw) * k;
                    if (y1 - y0 < 0.25f || r - l < 0.5f) continue;
                    g.DrawImage(img, new RectangleF(l, y0, r - l, y1 - y0 + 0.6f), new RectangleF(0, i * ih, img.Width, ih), GraphicsUnit.Pixel);
                }
            }
            canvas.Push(Handle, box.Left, box.Top);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { anim.Close(); if (canvas != null) { canvas.Dispose(); canvas = null; } if (!reverse) { } }
            base.Dispose(disposing);
        }
    }
}
