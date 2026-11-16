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
            Wobbly.BeginExternal(h);
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

        internal static Bitmap Capture(IntPtr h, Rectangle frame)
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

        public static void BeginExternal(IntPtr h)
        {
            if (!Enabled || cur != null || N.IsZoomed(h) || N.IsIconic(h)) return;
            int st = N.GetWindowLong(h, -16);
            if ((st & 0x00C00000) != 0x00C00000 && (st & 0x00040000) == 0) return;
            pending = h; pendingFrame = Genie.Frame(h);
            if (pendingTimer != null) pendingTimer.Start();
        }

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
            Start(h, f);
        }

        static void Start(IntPtr h, Rectangle frame)
        {
            int ex = N.GetWindowLong(h, -20);
            if ((ex & 0x80000) != 0) return;   // ya es transparente por su cuenta: no se toca
            var shot = Genie.Capture(h, frame);
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
            cur = new WobbleForm(h, shot, frame, Cursor.Position, ex, ov);
            cur.FormClosed += (o, e) => { if (cur == o) cur = null; };
            cur.Show();
        }

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
        int margin; LayeredCanvas canvas; VsyncTimer anim; Stopwatch clk = Stopwatch.StartNew(); double lastT;
        bool released, hidden, finishing;

        public WobbleForm(IntPtr target, Bitmap img, Rectangle frame, Point cursor, int exStyle, ButtonOverlay overlay)
        {
            Target = target; this.img = img; this.exStyle = exStyle; this.overlay = overlay;
            W = frame.Width; H = frame.Height;
            margin = (int)Math.Min(260, Math.Max(90, Math.Max(W, H) * 0.22f));
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
                    float f = 1 - Math.Min(1, d * 1.4f);
                    stiff[id] = 300f + 1500f * f * f;   // cerca del agarre: rigido; lejos: blando (rebota)
                }
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(frame.Left - margin, frame.Top - margin, W + margin * 2, H + margin * 2);
            anim = new VsyncTimer(this); anim.Tick += (o, e) => Frame();
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
            anim.Start();
        }

        public void Release() { released = true; }

        void Frame()
        {
            if (finishing) return;
            double now = clk.Elapsed.TotalSeconds; float dt = (float)Math.Min(0.034, now - lastT); lastT = now;
            if (!N.IsWindowVisible(Target) || N.IsIconic(Target)) { Finish(true); return; }
            var f = Genie.Frame(Target);
            if (f.Width != W || f.Height != H) { Finish(true); return; }   // se redimensiono o se acomodo: fuera la gelatina
            if (!released)
            {
                int vk = SystemInformation.MouseButtonsSwapped ? 0x02 : 0x01;
                if ((GetAsyncKeyState(vk) & 0x8000) == 0) released = true;
            }
            // fisica: cada nodo es atraido a su lugar en la ventana real con un resorte poco amortiguado
            int steps = Math.Max(1, (int)Math.Ceiling(dt / 0.004f)); float h = dt / steps;
            float maxOff = 0, maxV = 0;
            for (int s = 0; s < steps; s++)
                for (int id = 0; id < pos.Length; id++)
                {
                    float tx = f.Left + rest[id].X, ty = f.Top + rest[id].Y, k = stiff[id], c = 2f * (float)Math.Sqrt(k) * 0.5f;
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
            if (released && maxOff < 0.6f && maxV < 12f) Finish(false);
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

        void Draw(Point origin)
        {
            int cx = origin.X - margin, cy = origin.Y - margin;
            using (var g = Graphics.FromImage(canvas.Bitmap))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                float sw = img.Width / (float)TX, sh = img.Height / (float)TY;
                for (int j = 0; j < TY; j++)
                    for (int i = 0; i < TX; i++)
                    {
                        float u0 = i / (float)TX, v0 = j / (float)TY, u1 = (i + 1) / (float)TX, v1 = (j + 1) / (float)TY;
                        PointF p00 = At(u0, v0), p10 = At(u1, v0), p01 = At(u0, v1);
                        // se agranda cada pieza medio pixel para que no queden rendijas entre ellas
                        float ex = 0.7f;
                        var dx = new PointF(p10.X - p00.X, p10.Y - p00.Y); var dy = new PointF(p01.X - p00.X, p01.Y - p00.Y);
                        float lx = Math.Max(1f, (float)Math.Sqrt(dx.X * dx.X + dx.Y * dx.Y)), ly = Math.Max(1f, (float)Math.Sqrt(dy.X * dy.X + dy.Y * dy.Y));
                        var q00 = new PointF(p00.X - cx, p00.Y - cy);
                        var q10 = new PointF(q00.X + dx.X * (1 + ex / lx), q00.Y + dx.Y * (1 + ex / lx));
                        var q01 = new PointF(q00.X + dy.X * (1 + ex / ly), q00.Y + dy.Y * (1 + ex / ly));
                        g.DrawImage(img, new[] { q00, q10, q01 }, new RectangleF(i * sw, j * sh, sw, sh), GraphicsUnit.Pixel);
                    }
            }
            canvas.Push(Handle, cx, cy);
        }

        public void Finish(bool now)
        {
            if (finishing) return;
            finishing = true; anim.Stop();
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
            if (disposing) { anim.Close(); if (hidden) Wobbly.ShowReal(Target, exStyle); if (canvas != null) { canvas.Dispose(); canvas = null; } img.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
