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
            Wobbly.BeginExternal(h, true);
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
            if ((GetAsyncKeyState(vk) & 0x8000) == 0) { var h = win; Stop(); Wobbly.EndExternal(h); EdgeTiler.EndExternal(h); return; }
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

        internal static Rectangle Frame(IntPtr h)
        {
            N.RECT r;
            if (DwmGetWindowAttribute(h, 9, out r, 16) != 0) N.GetWindowRect(h, out r);
            return Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
        }

        internal static Bitmap Capture(IntPtr h, Rectangle frame) { return Capture(h, frame, 1100); }
        internal static Bitmap Capture(IntPtr h, Rectangle frame, int maxSide)
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
                float k = Math.Min(1f, maxSide / (float)Math.Max(crop.Width, crop.Height));
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
                ours[h] = DateTime.Now;
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

        public static void RestoreAnimOnExit()
        {
            if (animOff > 0) { animOff = 1; SystemAnim(true); }
            SetGlobal(false);
            if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; }
        }

        // ---- todas las minimizaciones del sistema (boton de Windows, Win+abajo, barra propia de la app) ----
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct WINDOWPLACEMENT { public int length, flags, showCmd; public N.POINT ptMin, ptMax; public N.RECT rcNormal; }
        static WinEventProc proc; static IntPtr hook;
        static readonly Dictionary<IntPtr, DateTime> ours = new Dictionary<IntPtr, DateTime>();
        static bool globalOff; static int globalSaved = 1;

        // Mientras MacDock corre, la animacion de minimizar de Windows se apaga (sin guardarlo) para que solo se vea la de Mac
        public static void SetGlobal(bool macOnly)
        {
            var ai = new ANIMATIONINFO { cbSize = 8 };
            if (macOnly && !globalOff) { SpiAnim(0x48, 8, ref ai, 0); globalSaved = ai.iMinAnimate; ai.iMinAnimate = 0; SpiAnim(0x49, 8, ref ai, 0); globalOff = true; savedAnim = 0; }
            else if (!macOnly && globalOff) { ai.iMinAnimate = globalSaved; SpiAnim(0x49, 8, ref ai, 0); globalOff = false; savedAnim = globalSaved; }
        }

        public static void Install()
        {
            if (hook == IntPtr.Zero) { proc = OnMinimize; hook = SetWinEventHook(0x0016, 0x0016, IntPtr.Zero, proc, 0, 0, 0x0002); }
            SetGlobal(Enabled);
        }

        static void OnMinimize(IntPtr hk, uint ev, IntPtr h, int obj, int child, uint thread, uint time)
        {
            try
            {
                if (!Enabled || obj != 0 || h == IntPtr.Zero || TargetFor == null) return;
                DateTime t; if (ours.TryGetValue(h, out t) && (DateTime.Now - t).TotalSeconds < 1.5) return;   // ya animada por MacDock
                if (!MacButtons.Eligible(h) && (N.GetWindowLong(h, -16) & 0x00C00000) != 0x00C00000) return;
                // rectangulo que tenia antes de minimizarse (coordenadas de area de trabajo -> pantalla)
                var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
                if (!GetWindowPlacement(h, ref wp)) return;
                var scr = Screen.FromHandle(h);
                Rectangle from;
                if ((wp.flags & 2) != 0) from = scr.WorkingArea;   // estaba maximizada
                else
                {
                    // las coordenadas de area de trabajo se cuentan desde el area de trabajo del monitor principal
                    var pr = Screen.PrimaryScreen; int ox = pr.WorkingArea.Left - pr.Bounds.Left, oy = pr.WorkingArea.Top - pr.Bounds.Top;
                    from = new Rectangle(wp.rcNormal.left + ox, wp.rcNormal.top + oy, wp.rcNormal.right - wp.rcNormal.left, wp.rcNormal.bottom - wp.rcNormal.top);
                }
                if (from.Width < 60 || from.Height < 60) return;
                new ScaleFx(h, from, TargetFor(h)).Show();
            }
            catch (Exception ex) { Program.Log(ex); }
        }
    }

    // Efecto de escala de macOS con la miniatura en vivo que mantiene Windows (DWM): para minimizaciones que no pasan por MacDock
    class ScaleFx : Form
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref THUMBPROPS p);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct THUMBPROPS { public int dwFlags; public N.RECT rcDestination, rcSource; public byte opacity; public int fVisible, fSourceClientAreaOnly; }

        readonly IntPtr src; readonly Rectangle from, to; IntPtr thumb; VsyncTimer anim; Stopwatch clk = new Stopwatch();
        const double Dur = 0.34;

        public ScaleFx(IntPtr src, Rectangle from, Rectangle to)
        {
            this.src = src; this.from = from; this.to = to;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black; Bounds = new Rectangle(from.X, from.Y, 1, 1);   // crece cuando la miniatura ya esta lista
            anim = new VsyncTimer(this); anim.Tick += (o, e) => Frame();
        }

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x20 | 0x80 | 0x8000000 | 0x8; return cp; } }
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (DwmRegisterThumbnail(Handle, src, out thumb) != 0) { Close(); return; }
            Update2(0);
            clk.Start(); anim.Start();
        }

        void Update2(float t)
        {
            float k = 1 - (1 - t) * (1 - t);   // ease-out
            var r = new Rectangle((int)(from.X + (to.X - from.X) * k), (int)(from.Y + (to.Y - from.Y) * k),
                Math.Max(2, (int)(from.Width + (to.Width - from.Width) * k)), Math.Max(2, (int)(from.Height + (to.Height - from.Height) * k)));
            N.SetWindowPos(Handle, new IntPtr(-1), r.X, r.Y, r.Width, r.Height, 0x0010);
            var p = new THUMBPROPS { dwFlags = 0x1 | 0x4 | 0x8 | 0x10, rcDestination = new N.RECT { left = 0, top = 0, right = r.Width, bottom = r.Height }, opacity = (byte)(255 * (1 - t * 0.35f)), fVisible = 1, fSourceClientAreaOnly = 0 };
            DwmUpdateThumbnailProperties(thumb, ref p);
        }

        void Frame()
        {
            double p = clk.Elapsed.TotalSeconds / Dur;
            if (p >= 1) { anim.Stop(); Close(); return; }
            Update2((float)p);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { anim.Close(); if (thumb != IntPtr.Zero) { DwmUnregisterThumbnail(thumb); thumb = IntPtr.Zero; } }
            base.Dispose(disposing);
        }
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
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                float ih = img.Height / (float)strips;
                int prevBottom = int.MinValue;
                using (var clamp = new System.Drawing.Imaging.ImageAttributes())
                {
                    clamp.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
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
                        // franjas contiguas en pixeles enteros: nunca queda una rendija
                        int top = prevBottom == int.MinValue ? (int)Math.Round(y0) : prevBottom;
                        int bottom = (int)Math.Round(y1);
                        if (bottom <= top) continue;   // franja comprimida a menos de un pixel: la siguiente la cubre
                        prevBottom = bottom;
                        int xl = (int)Math.Round(l), xr = (int)Math.Round(r);
                        if (xr <= xl) continue;
                        g.DrawImage(img, new Rectangle(xl, top, xr - xl, bottom - top), 0, i * ih, img.Width, ih, GraphicsUnit.Pixel, clamp);
                    }
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

namespace MacDock
{
    // Escritorio limpio estilo Mac: iconos squircle en los accesos directos (con respaldo para restaurar),
    // papelera fuera del escritorio (ya esta en el dock) y sin las flechas de acceso directo.
    static class DesktopStyle
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern void SHChangeNotify(int ev, int flags, IntPtr a, IntPtr b);
        static string Dir { get { return System.IO.Path.Combine(Program.DataDir, "IconosEscritorio"); } }
        static string BackupFile { get { return System.IO.Path.Combine(Dir, "respaldo.txt"); } }
        const string BinGuid = "{645FF040-5081-101B-9F08-00AA002F954E}";

        static IEnumerable<string> Shortcuts()
        {
            foreach (var d in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            {
                if (string.IsNullOrEmpty(d) || !System.IO.Directory.Exists(d)) continue;
                foreach (var f in System.IO.Directory.GetFiles(d, "*.lnk")) yield return f;
            }
        }

        public static bool Applied { get { return System.IO.File.Exists(BackupFile); } }

        // devuelve cuantos se cambiaron y cuantos no se pudieron (los del escritorio publico piden permisos)
        public static void ApplyIcons(out int done, out int failed)
        {
            done = 0; failed = 0;
            System.IO.Directory.CreateDirectory(Dir);
            var backup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (System.IO.File.Exists(BackupFile))
                foreach (var line in System.IO.File.ReadAllLines(BackupFile)) { int i = line.IndexOf('|'); if (i > 0) backup[line.Substring(0, i)] = line.Substring(i + 1); }
            foreach (var lnk in Shortcuts())
            {
                try
                {
                    string ip; int ii;
                    if (!Shell.GetLnkIcon(lnk, out ip, out ii)) { failed++; continue; }
                    if (ip.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)) continue;   // ya tiene el estilo Mac
                    var raw = Shell.GetIcon(lnk, 256);
                    if (raw == null) { failed++; continue; }
                    var tile = MacIcon.Tile(raw, 256);
                    string name = System.IO.Path.GetFileNameWithoutExtension(lnk);
                    foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                    string ico = System.IO.Path.Combine(Dir, name + "-" + (lnk.GetHashCode() & 0xFFFF).ToString("x4") + ".ico");
                    SaveIco(tile, ico);
                    if (!backup.ContainsKey(lnk)) backup[lnk] = ip + "|" + ii;
                    if (Shell.SetLnkIcon(lnk, ico, 0)) done++; else failed++;
                }
                catch (Exception ex) { failed++; Program.Log(ex); }
            }
            var lines = new List<string>(); foreach (var kv in backup) lines.Add(kv.Key + "|" + kv.Value);
            System.IO.File.WriteAllLines(BackupFile, lines.ToArray());
            Refresh();
        }

        public static void RestoreIcons()
        {
            if (!System.IO.File.Exists(BackupFile)) return;
            foreach (var line in System.IO.File.ReadAllLines(BackupFile))
            {
                var p = line.Split('|'); if (p.Length < 3) continue;
                int idx; int.TryParse(p[2], out idx);
                try { if (System.IO.File.Exists(p[0])) Shell.SetLnkIcon(p[0], p[1], idx); } catch { }
            }
            try { System.IO.File.Delete(BackupFile); } catch { }
            Refresh();
        }

        public static bool BinHidden
        {
            get
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel"))
                    return k != null && Convert.ToInt32(k.GetValue(BinGuid, 0)) == 1;
            }
        }

        public static void HideBin(bool hide)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel"))
                k.SetValue(BinGuid, hide ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
            Refresh();
        }

        // Las flechas son una opcion de todo el equipo (HKLM): Windows pide permiso de administrador una vez.
        public static void RemoveArrows(bool remove)
        {
            string blank = System.IO.Path.Combine(Program.DataDir, "sin-flecha.ico");
            if (remove)
                using (var b = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb)) SaveIco(b, blank);
            string key = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";
            string cmd = remove
                ? "New-Item -Path '" + key + "' -Force | Out-Null; Set-ItemProperty -Path '" + key + "' -Name '29' -Value '" + blank.Replace("'", "''") + ",0'"
                : "Remove-ItemProperty -Path '" + key + "' -Name '29' -ErrorAction SilentlyContinue";
            var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"" + cmd + "\"") { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            try { using (var p = Process.Start(psi)) p.WaitForExit(15000); } catch (System.ComponentModel.Win32Exception) { return; }   // el usuario cancelo el permiso
            Refresh();
        }

        public static bool ArrowsRemoved
        {
            get
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons"))
                    return k != null && k.GetValue("29") != null;
            }
        }

        static void Refresh() { try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { } }

        // ICO con imagenes PNG (256, 64, 48, 32 y 16 px), como los que usa Windows
        public static void SaveIco(Bitmap src, string file)
        {
            int[] sizes = { 256, 64, 48, 32, 16 };
            var pngs = new List<byte[]>();
            foreach (int sz in sizes)
                using (var b = new Bitmap(sz, sz, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(b)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(src, 0, 0, sz, sz); }
                    using (var ms = new System.IO.MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Png); pngs.Add(ms.ToArray()); }
                }
            using (var fs = new System.IO.FileStream(file, System.IO.FileMode.Create))
            using (var w = new System.IO.BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset); offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }
    }
}

namespace MacDock
{
    // Ventanas "gelatina" al moverlas (como los wobbly windows de KDE/Compiz): mientras se mueve la ventana se muestra
    // una captura sobre una malla de resortes; el punto agarrado sigue al puntero y el resto se retrasa y rebota.
    // La ventana real se mueve igual (invisible) y reaparece al asentarse. Funciona con cualquier forma de arrastrar.
    static class Wobbly
    {
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int v);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr rc, IntPtr rgn, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        public static bool Enabled = true;
        static WinEventProc proc; static IntPtr hook;
        static IntPtr pending; static Rectangle pendingFrame;   // movimiento iniciado, la gelatina arranca con el primer desplazamiento
        static System.Windows.Forms.Timer pendingTimer;
        static WobbleForm cur;

        public static void Install()
        {
            if (hook != IntPtr.Zero) return;
            proc = OnEvent;
            hook = SetWinEventHook(0x000A, 0x000B, IntPtr.Zero, proc, 0, 0, 0x0002);
            pendingTimer = new System.Windows.Forms.Timer { Interval = 8 };
            pendingTimer.Tick += (o, e) => CheckPending();
        }

        public static void Uninstall()
        {
            if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; }
            if (cur != null) cur.Finish(true);
        }

        static void OnEvent(IntPtr hk, uint ev, IntPtr h, int obj, int child, uint thread, uint time)
        {
            if (obj != 0 || h == IntPtr.Zero) return;
            if (ev == 0x000A) BeginExternal(h); else EndExternal(h);
        }

        public static void BeginExternal(IntPtr h) { BeginExternal(h, false); }
        // own = ventana que MacDock arrastra por su cuenta (su Finder): no hace falta que tenga barra de Windows
        public static void BeginExternal(IntPtr h, bool own)
        {
            if (!Enabled || cur != null || N.IsZoomed(h) || N.IsIconic(h)) return;
            int st = N.GetWindowLong(h, -16);
            if (!own && (st & 0x00C00000) != 0x00C00000 && (st & 0x00040000) == 0) return;
            pending = h; pendingOwn = own; pendingFrame = Genie.Frame(h);
            if (preShot != null) { preShot.Dispose(); preShot = null; }
            if ((N.GetWindowLong(h, -20) & 0x80000) == 0) preShot = Genie.Capture(h, pendingFrame, 3000);
            if (pendingTimer != null) pendingTimer.Start();
        }
        static Bitmap preShot; static bool pendingOwn;

        public static void EndExternal(IntPtr h)
        {
            if (h == pending) { pending = IntPtr.Zero; pendingTimer.Stop(); }
            if (cur != null && cur.Target == h) cur.Release();
        }

        static void CheckPending()
        {
            if (pending == IntPtr.Zero) { pendingTimer.Stop(); return; }
            int vk = SystemInformation.MouseButtonsSwapped ? 0x02 : 0x01;
            if ((GetAsyncKeyState(vk) & 0x8000) == 0) { pending = IntPtr.Zero; pendingTimer.Stop(); return; }
            var f = Genie.Frame(pending);
            if (f.Size != pendingFrame.Size) { pending = IntPtr.Zero; pendingTimer.Stop(); return; }   // es una redimension
            if (f.Location == pendingFrame.Location) return;
            var h = pending; pending = IntPtr.Zero; pendingTimer.Stop();
            Start(h, f, pendingOwn);
        }

        static void Start(IntPtr h, Rectangle frame, bool own)
        {
            int ex = N.GetWindowLong(h, -20);
            if ((ex & 0x80000) != 0) return;   // ya es transparente por su cuenta: no se toca
            var shot = preShot; preShot = null;
            if (shot == null) shot = Genie.Capture(h, frame, 3000);
            if (shot == null) return;
            float k = shot.Width / (float)frame.Width;
            // la barra estilo Mac de MacDock se pinta en la captura (si no, se verian los botones de Windows)
            var mb = MacButtons.Instance; ButtonOverlay ov = mb != null ? mb.OverlayFor(h) : null;
            using (var g = Graphics.FromImage(shot))
            {
                if (ov != null && ov.Shown) ov.PaintInto(g, (ov.Area.Left - frame.Left) * k, (ov.Area.Top - frame.Top) * k, k);
                // esquinas redondeadas de Windows 11
                float rad = 8f * k * (N.GetDpiForWindow(h) / 96f);
                using (var outside = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    outside.AddRectangle(new RectangleF(0, 0, shot.Width, shot.Height));
                    var r = new RectangleF(0, 0, shot.Width, shot.Height); float d = rad * 2;
                    outside.AddArc(r.X, r.Y, d, d, 180, 90); outside.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                    outside.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); outside.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                    outside.CloseFigure(); outside.FillMode = System.Drawing.Drawing2D.FillMode.Alternate;
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    using (var clear = new SolidBrush(Color.Transparent)) g.FillPath(clear, outside);
                }
            }
            cur = new WobbleForm(h, shot, frame, Cursor.Position, ex, ov, !own);
            cur.FormClosed += (o, e) => { if (cur == o) cur = null; };
            cur.Show();
            if (!own)
            {
                // MacDock toma el arrastre: Windows deja de mover (y la app de repintar) la ventana en cada pixel
                PostMessage(h, 0x001F, IntPtr.Zero, IntPtr.Zero);   // WM_CANCELMODE
                var t = new System.Windows.Forms.Timer { Interval = 60 };
                t.Tick += (o, e) => { t.Stop(); t.Dispose(); if (cur != null && cur.Target == h) EdgeTiler.BeginExternal(h); };
                t.Start();
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public static bool IsWobbling(IntPtr h) { return cur != null && cur.Target == h && !cur.IsDisposed; }

        internal static void HideReal(IntPtr h, int ex) { SetWindowLong(h, -20, ex | 0x80000); SetLayeredWindowAttributes(h, 0, 0, 2); }
        internal static void ShowReal(IntPtr h, int ex) { SetWindowLong(h, -20, ex); RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0400); }
    }

    class WobbleForm : Form
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        public readonly IntPtr Target;
        readonly Bitmap img; readonly int exStyle; readonly ButtonOverlay overlay;
        readonly int W, H, NX = 5, NY = 5, TX = 16, TY = 12;
        readonly PointF[] pos, vel, rest; readonly float[] stiff;
        readonly PointF grab;   // punto agarrado, relativo a la ventana
        int margin; LayeredCanvas canvas; Stopwatch clk = Stopwatch.StartNew(); double lastT;
        volatile bool released, finishing, running;
        bool hidden;
        readonly bool takeOver;   // arrastre de Windows tomado por MacDock: la ventana real se reubica pocas veces por segundo
        System.Threading.Thread th; double lastMove; Point releasePos;
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmFlush();

        public WobbleForm(IntPtr target, Bitmap img, Rectangle frame, Point cursor, int exStyle, ButtonOverlay overlay, bool takeOver)
        {
            Target = target; this.img = img; this.exStyle = exStyle; this.overlay = overlay; this.takeOver = takeOver;
            W = frame.Width; H = frame.Height;
            margin = (int)Math.Min(380, Math.Max(120, Math.Max(W, H) * 0.3f));
            grab = new PointF(Math.Max(0, Math.Min(W, cursor.X - frame.Left)), Math.Max(0, Math.Min(H, cursor.Y - frame.Top)));
            int n = NX * NY; pos = new PointF[n]; vel = new PointF[n]; rest = new PointF[n]; stiff = new float[n];
            float diag = (float)Math.Sqrt(W * W + H * H);
            for (int j = 0; j < NY; j++)
                for (int i = 0; i < NX; i++)
                {
                    int id = j * NX + i;
                    rest[id] = new PointF(i * W / (float)(NX - 1), j * H / (float)(NY - 1));
                    pos[id] = new PointF(frame.Left + rest[id].X, frame.Top + rest[id].Y);
                    float d = (float)Math.Sqrt((rest[id].X - grab.X) * (rest[id].X - grab.X) + (rest[id].Y - grab.Y) * (rest[id].Y - grab.Y)) / diag;
                    float f = 1 - Math.Min(1, d * 1.25f);
                    stiff[id] = 140f + 1300f * f * f;   // cerca del agarre: rigido; lejos: muy blando (se arrastra y rebota)
                }
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(frame.Left - margin, frame.Top - margin, W + margin * 2, H + margin * 2);
        }

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8; return cp; } }
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            canvas = new LayeredCanvas(W + margin * 2, H + margin * 2);
            var f = Genie.Frame(Target);
            Draw(f.Location);
            // la captura ya cubre la ventana: ahora se oculta la real (y su barra Mac)
            Wobbly.HideReal(Target, exStyle); hidden = true;
            if (overlay != null && !overlay.IsDisposed) overlay.Hide2();
            lastT = clk.Elapsed.TotalSeconds;
            // hilo de dibujo propio, al ritmo de la pantalla: no depende del hilo principal de MacDock
            running = true;
            th = new System.Threading.Thread(() =>
            {
                while (running)
                {
                    long t0 = clk.ElapsedTicks;
                    if (DwmFlush() != 0 || (clk.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency < 1.0) System.Threading.Thread.Sleep(4);
                    if (!running) break;
                    try { Frame(); }
                    catch (Exception ex) { Program.Log(ex); running = false; EndOnUi(true); }
                }
            }) { IsBackground = true, Name = "MacDockWobble", Priority = System.Threading.ThreadPriority.AboveNormal };
            th.Start();
        }

        // el fin de la gelatina se hace en el hilo principal
        void EndOnUi(bool now) { try { BeginInvoke(new Action(() => Finish(now))); } catch { } }

        public void Release() { if (!takeOver) released = true; }   // con el arrastre tomado, el soltar lo detecta MacDock

        void Frame()
        {
            if (finishing) return;
            double now = clk.Elapsed.TotalSeconds; float dt = (float)Math.Min(0.034, now - lastT); lastT = now;
            if (!N.IsWindowVisible(Target) || N.IsIconic(Target)) { running = false; EndOnUi(true); return; }
            var real = Genie.Frame(Target);
            if (real.Width != W || real.Height != H) { running = false; EndOnUi(true); return; }   // se redimensiono o se acomodo
            var cpos = Cursor.Position;
            if (!released)
            {
                int vk = SystemInformation.MouseButtonsSwapped ? 0x02 : 0x01;
                if ((GetAsyncKeyState(vk) & 0x8000) == 0)
                {
                    released = true;
                    if (takeOver)
                    {
                        // al soltar, la ventana real queda exactamente donde la dejaste, y se revisan los bordes (acomodo)
                        releasePos = new Point((int)(cpos.X - grab.X), (int)(cpos.Y - grab.Y));
                        MoveReal(releasePos, false);
                        var h0 = Target; try { BeginInvoke(new Action(() => EdgeTiler.EndExternal(h0))); } catch { }
                    }
                }
                else if (takeOver && now - lastMove > 0.08) { lastMove = now; MoveReal(new Point((int)(cpos.X - grab.X), (int)(cpos.Y - grab.Y)), true); }
            }
            var f = !released ? new Rectangle((int)(cpos.X - grab.X), (int)(cpos.Y - grab.Y), W, H)
                  : takeOver ? new Rectangle(releasePos, new Size(W, H)) : real;
            // fisica: cada nodo es atraido a su lugar en la ventana real con un resorte poco amortiguado
            int steps = Math.Max(1, (int)Math.Ceiling(dt / 0.004f)); float h = dt / steps;
            float maxOff = 0, maxV = 0;
            for (int s = 0; s < steps; s++)
                for (int id = 0; id < pos.Length; id++)
                {
                    float tx = f.Left + rest[id].X, ty = f.Top + rest[id].Y, k = stiff[id], c = 2f * (float)Math.Sqrt(k) * 0.38f;
                    float ax = k * (tx - pos[id].X) - c * vel[id].X, ay = k * (ty - pos[id].Y) - c * vel[id].Y;
                    vel[id] = new PointF(vel[id].X + ax * h, vel[id].Y + ay * h);
                    float px = pos[id].X + vel[id].X * h, py = pos[id].Y + vel[id].Y * h;
                    // sin salirse del lienzo
                    px = Math.Max(tx - margin + 2, Math.Min(tx + margin - 2, px)); py = Math.Max(ty - margin + 2, Math.Min(ty + margin - 2, py));
                    pos[id] = new PointF(px, py);
                }
            for (int id = 0; id < pos.Length; id++)
            {
                float ox = Math.Abs(pos[id].X - (f.Left + rest[id].X)), oy = Math.Abs(pos[id].Y - (f.Top + rest[id].Y));
                maxOff = Math.Max(maxOff, Math.Max(ox, oy)); maxV = Math.Max(maxV, Math.Max(Math.Abs(vel[id].X), Math.Abs(vel[id].Y)));
            }
            Draw(f.Location);
            if (released && maxOff < 0.6f && maxV < 12f) { running = false; EndOnUi(false); }
        }

        // reubica la ventana real (invisible) sin esperar a la app
        void MoveReal(Point topLeftOfFrame, bool async)
        {
            N.RECT wr; N.GetWindowRect(Target, out wr);
            var fr = Genie.Frame(Target);
            int dx = topLeftOfFrame.X - fr.Left, dy = topLeftOfFrame.Y - fr.Top;
            if (dx == 0 && dy == 0) return;
            N.SetWindowPos(Target, IntPtr.Zero, wr.left + dx, wr.top + dy, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200 | (async ? 0x4000 : 0));
        }

        // posicion de un punto de la ventana (u,v en 0..1) interpolando la malla
        PointF At(float u, float v)
        {
            float gx = u * (NX - 1), gy = v * (NY - 1);
            int i = Math.Min(NX - 2, (int)gx), j = Math.Min(NY - 2, (int)gy);
            float fx = gx - i, fy = gy - j;
            PointF a = pos[j * NX + i], b = pos[j * NX + i + 1], c = pos[(j + 1) * NX + i], d = pos[(j + 1) * NX + i + 1];
            float top = a.X + (b.X - a.X) * fx, bot = c.X + (d.X - c.X) * fx;
            float topY = a.Y + (b.Y - a.Y) * fx, botY = c.Y + (d.Y - c.Y) * fx;
            return new PointF(top + (bot - top) * fy, topY + (botY - topY) * fy);
        }

        Rectangle prevBox = Rectangle.Empty;
        bool columns;   // dibujar en columnas (arrastre vertical) en vez de filas
        readonly System.Drawing.Imaging.ImageAttributes edgeClamp = MakeClamp();
        static System.Drawing.Imaging.ImageAttributes MakeClamp() { var a = new System.Drawing.Imaging.ImageAttributes(); a.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY); return a; }

        // Rapido: la ventana se dibuja en filas finas, cada una desplazada y estirada segun la malla (copia casi 1:1),
        // y solo se limpia y se envia a la pantalla el rectangulo que ocupa
        int[] src, buf; int srcW, srcH;

        // Copia de pixeles propia (sin GDI+): cada pixel del lienzo se calcula una vez con aritmetica entera exacta,
        // asi no hay suavizado de bordes, ni muestreos raros, ni rendijas entre franjas. Se escribe solo el rectangulo ocupado.
        void Draw(Point origin)
        {
            int cx = origin.X - margin, cy = origin.Y - margin, CW = canvas.Width, CH = canvas.Height;
            if (src == null)
            {
                srcW = img.Width; srcH = img.Height; src = new int[srcW * srcH];
                var bd = img.LockBits(new Rectangle(0, 0, srcW, srcH), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try { for (int y = 0; y < srcH; y++) System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, src, y * srcW, srcW); }
                finally { img.UnlockBits(bd); }
                buf = new int[CW * CH];
            }
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in pos) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
            var box = Rectangle.FromLTRB((int)Math.Floor(minX) - cx - 2, (int)Math.Floor(minY) - cy - 2, (int)Math.Ceiling(maxX) - cx + 3, (int)Math.Ceiling(maxY) - cy + 3);
            box.Intersect(new Rectangle(0, 0, CW, CH));
            var dirty = prevBox.IsEmpty ? box : Rectangle.Union(prevBox, box);
            for (int y = dirty.Top; y < dirty.Bottom; y++) Array.Clear(buf, y * CW + dirty.Left, dirty.Width);

            float defX = 0, defY = 0;
            for (int id = 0; id < pos.Length; id++) { defX += Math.Abs(pos[id].X - (origin.X + rest[id].X)); defY += Math.Abs(pos[id].Y - (origin.Y + rest[id].Y)); }
            if (columns ? defX > defY * 1.3f : defY > defX * 1.3f) columns = !columns;
            const int Strip = 3;
            int prev = int.MinValue;
            if (!columns)
                for (int y = 0; y < H; y += Strip)   // filas: se desplazan y estiran a los lados
                {
                    int rh = Math.Min(Strip, H - y);
                    float v0 = y / (float)H, v1 = (y + rh) / (float)H, vm = (v0 + v1) / 2;
                    int top = prev == int.MinValue ? (int)Math.Round((At(0, v0).Y + At(1, v0).Y) / 2 - cy) : prev;
                    int bottom = Math.Max(top + 1, (int)Math.Round((At(0, v1).Y + At(1, v1).Y) / 2 - cy));
                    prev = bottom;
                    int xl = (int)Math.Round(At(0, vm).X - cx), xr = Math.Max(xl + 1, (int)Math.Round(At(1, vm).X - cx));
                    Blit(xl, top, xr - xl, bottom - top, 0, y * srcH / H, srcW, Math.Max(1, (y + rh) * srcH / H - y * srcH / H), CW, CH);
                }
            else
                for (int x = 0; x < W; x += Strip)   // columnas: se desplazan y estiran hacia arriba y abajo
                {
                    int cw = Math.Min(Strip, W - x);
                    float u0 = x / (float)W, u1 = (x + cw) / (float)W, um = (u0 + u1) / 2;
                    int left = prev == int.MinValue ? (int)Math.Round((At(u0, 0).X + At(u0, 1).X) / 2 - cx) : prev;
                    int right = Math.Max(left + 1, (int)Math.Round((At(u1, 0).X + At(u1, 1).X) / 2 - cx));
                    prev = right;
                    int yt = (int)Math.Round(At(um, 0).Y - cy), yb = Math.Max(yt + 1, (int)Math.Round(At(um, 1).Y - cy));
                    Blit(left, yt, right - left, yb - yt, x * srcW / W, 0, Math.Max(1, (x + cw) * srcW / W - x * srcW / W), srcH, CW, CH);
                }
            // solo las filas del rectangulo tocado pasan a la memoria que lee la pantalla
            IntPtr bits = canvas.Bits;
            for (int y = dirty.Top; y < dirty.Bottom; y++)
                System.Runtime.InteropServices.Marshal.Copy(buf, y * CW + dirty.Left, bits + (y * CW + dirty.Left) * 4, dirty.Width);
            prevBox = box;
            canvas.PushRegion(Handle, cx + box.X, cy + box.Y, box);
        }

        // copia un rectangulo de la captura (sx,sy,sw,sh) estirado a (dx,dy,dw,dh) con vecino mas cercano en punto fijo
        void Blit(int dx, int dy, int dw, int dh, int sx, int sy, int sw, int sh, int CW, int CH)
        {
            if (dw <= 0 || dh <= 0) return;
            int stepX = (sw << 16) / dw, stepY = (sh << 16) / dh;
            int y0 = Math.Max(0, dy), y1 = Math.Min(CH, dy + dh), x0 = Math.Max(0, dx), x1 = Math.Min(CW, dx + dw);
            for (int y = y0; y < y1; y++)
            {
                int srow = sy + Math.Min(sh - 1, (int)(((long)(y - dy) * stepY) >> 16));
                int sOff = srow * srcW + sx, dOff = y * CW;
                int acc = (x0 - dx) * stepX;
                for (int x = x0; x < x1; x++, acc += stepX)
                {
                    int sc = acc >> 16; if (sc >= sw) sc = sw - 1;
                    buf[dOff + x] = src[sOff + sc];
                }
            }
        }

        public void Finish(bool now)
        {
            if (finishing) return;
            finishing = true; running = false;
            if (th != null && th != System.Threading.Thread.CurrentThread) th.Join(200);
            if (hidden) { Wobbly.ShowReal(Target, exStyle); hidden = false; }
            var mb = MacButtons.Instance;
            if (overlay != null && !overlay.IsDisposed && mb != null) overlay.Place(mb.IsActive(Target));
            // se espera un refresco para que la ventana real ya este pintada antes de quitar la captura
            var t = new System.Windows.Forms.Timer { Interval = now ? 1 : 30 };
            t.Tick += (o, e) => { t.Stop(); t.Dispose(); Close(); };
            t.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { running = false; if (th != null) th.Join(200); if (hidden) Wobbly.ShowReal(Target, exStyle); if (canvas != null) { canvas.Dispose(); canvas = null; } img.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
