using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MacDock
{
    // Temporizador de animacion sincronizado con el refresco real de la pantalla (60, 144, 165, 180 Hz...).
    // Un hilo espera a cada composicion de Windows (DwmFlush) y ejecuta el cuadro en el hilo de la interfaz.
    // Cuando no hay animacion queda dormido: 0% de CPU.
    class VsyncTimer
    {
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        [DllImport("dwmapi.dll")] static extern int DwmIsCompositionEnabled(out bool on);

        readonly Control owner;
        readonly ManualResetEvent run = new ManualResetEvent(false);
        volatile bool enabled, closed;
        Thread th;
        public event EventHandler Tick;
        public int Interval { get; set; }   // se ignora: el ritmo lo marca la pantalla
        public bool Enabled { get { return enabled; } }

        public VsyncTimer(Control owner) { this.owner = owner; }

        public void Start()
        {
            if (closed) return;
            enabled = true; run.Set();
            if (th == null)
            {
                th = new Thread(Loop) { IsBackground = true, Name = "MacDockVsync", Priority = ThreadPriority.AboveNormal };
                th.Start();
            }
        }

        public void Stop() { enabled = false; run.Reset(); }

        public void Close() { closed = true; enabled = false; run.Set(); }

        void Loop()
        {
            var sw = Stopwatch.StartNew();
            while (!closed)
            {
                run.WaitOne();
                if (closed) break;
                bool comp;
                long t0 = sw.ElapsedTicks;
                if (DwmIsCompositionEnabled(out comp) != 0 || !comp || DwmFlush() != 0) Thread.Sleep(7);
                // si DwmFlush volvio al instante (pantalla apagada, sesion remota) se evita girar en vacio
                if ((sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency < 1.0) Thread.Sleep(4);
                if (!enabled || closed) continue;
                try
                {
                    if (owner.IsDisposed || !owner.IsHandleCreated) continue;
                    owner.Invoke(new Action(() => { if (enabled && Tick != null) Tick(this, EventArgs.Empty); }));
                }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { }
                catch (Exception ex) { Program.Log(ex); }
            }
        }
    }

    // Lienzo para ventanas transparentes (layered) que vive en una seccion DIB:
    // GDI+ dibuja directo en la memoria que lee UpdateLayeredWindow, sin copiar el bitmap en cada cuadro.
    class LayeredCanvas : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        public readonly int Width, Height;
        public readonly Bitmap Bitmap;
        IntPtr memDc, hBmp, oldBmp;

        public LayeredCanvas(int w, int h)
        {
            Width = w; Height = h;
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr screen = N.GetDC(IntPtr.Zero), bits;
            try
            {
                memDc = N.CreateCompatibleDC(screen);
                hBmp = CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
                if (hBmp == IntPtr.Zero) throw new OutOfMemoryException("CreateDIBSection");
                oldBmp = N.SelectObject(memDc, hBmp);
                Bitmap = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
            }
            finally { N.ReleaseDC(IntPtr.Zero, screen); }
        }

        public void Push(IntPtr hwnd, int x, int y)
        {
            IntPtr screen = N.GetDC(IntPtr.Zero);
            try
            {
                var size = new N.SIZE { cx = Width, cy = Height };
                var src = new N.POINT(); var dst = new N.POINT { x = x, y = y };
                var blend = new N.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                N.UpdateLayeredWindow(hwnd, screen, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally { N.ReleaseDC(IntPtr.Zero, screen); }
        }

        public void Dispose()
        {
            Bitmap.Dispose();
            if (memDc != IntPtr.Zero) { N.SelectObject(memDc, oldBmp); N.DeleteDC(memDc); memDc = IntPtr.Zero; }
            if (hBmp != IntPtr.Zero) { N.DeleteObject(hBmp); hBmp = IntPtr.Zero; }
        }
    }

    // Iconos ya reducidos a cada tamano que usa la lupa (en pasos de 2 px). Reducir un icono de 256 px
    // con filtro bicubico cuesta mucho; asi se hace una sola vez y en cada cuadro solo se copia.
    static class IconSizes
    {
        static readonly Dictionary<Bitmap, Dictionary<int, Bitmap>> cache = new Dictionary<Bitmap, Dictionary<int, Bitmap>>();
        const int Step = 2;

        public static Bitmap Get(Bitmap src, float px)
        {
            int size = Math.Max(Step, ((int)Math.Ceiling(px) + Step - 1) / Step * Step);
            if (size >= src.Width) return src;
            Dictionary<int, Bitmap> bySize;
            if (!cache.TryGetValue(src, out bySize)) { bySize = new Dictionary<int, Bitmap>(); cache[src] = bySize; }
            Bitmap b;
            if (bySize.TryGetValue(size, out b)) return b;
            b = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                using (var ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);   // evita el borde gris que deja el bicubico
                    g.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }
            bySize[size] = b;
            return b;
        }

        // dibuja el icono en r con el tamano ya preparado (copia casi 1:1, muy rapida)
        public static void Draw(Graphics g, Bitmap src, RectangleF r, ImageAttributes ia = null)
        {
            var b = Get(src, Math.Max(r.Width, r.Height));
            if (ia == null) g.DrawImage(b, r);
            else g.DrawImage(b, new[] { new PointF(r.X, r.Y), new PointF(r.Right, r.Y), new PointF(r.X, r.Bottom) }, new RectangleF(0, 0, b.Width, b.Height), GraphicsUnit.Pixel, ia);
        }

        public static void Clear()
        {
            foreach (var d in cache.Values) foreach (var b in d.Values) b.Dispose();
            cache.Clear();
        }
    }

    static class Ease
    {
        public static float Clamp01(double t) { return t <= 0 ? 0f : t >= 1 ? 1f : (float)t; }
        public static float OutCubic(double t) { float x = Clamp01(t); return 1 - (1 - x) * (1 - x) * (1 - x); }
        public static float InOutCubic(double t) { float x = Clamp01(t); return x < 0.5f ? 4 * x * x * x : 1 - (float)Math.Pow(-2 * x + 2, 3) / 2; }
        // suavizado exponencial independiente de los fps: igual de rapido a 60 que a 180 Hz
        public static float Approach(float cur, float target, float dt, float tau) { return cur + (target - cur) * (1 - (float)Math.Exp(-dt / tau)); }
    }
}

namespace MacDock
{
    // Vidrio estilo macOS: desenfoque de lo que hay detras (composicion de Windows) + ventana algo translucida
    // y esquinas redondeadas. Se usa en menus y paneles propios.
    static class Glass
    {
        [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int State, Flags, Color, Anim; }
        [StructLayout(LayoutKind.Sequential)] struct WinCompAttr { public int Attr; public IntPtr Data; public int Size; }
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WinCompAttr d);

        public static bool Enabled = true;
        public const double Opacity = 0.92;

        public static bool Blur(IntPtr h)
        {
            if (!Enabled || h == IntPtr.Zero) return false;
            try
            {
                var acc = new AccentPolicy { State = 3 };
                int size = Marshal.SizeOf(acc); IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(acc, ptr, false);
                    var d = new WinCompAttr { Attr = 19, Data = ptr, Size = size };
                    return SetWindowCompositionAttribute(h, ref d) != 0;
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch { return false; }
        }

        // una ventana translucida pierde el redondeo de Windows 11, asi que se recorta con una region redondeada
        public static void RoundRegion(Control c, float radius)
        {
            if (c.Width <= 0 || c.Height <= 0) return;
            using (var p = new GraphicsPath())
            {
                float d = radius * 2; var r = new RectangleF(0, 0, c.Width, c.Height);
                p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                var old = c.Region; c.Region = new Region(p); if (old != null) old.Dispose();
            }
        }

        // aplica el vidrio a un formulario propio (panel, buscador): aparece con un fundido corto
        public static void Apply(Form f, float radius)
        {
            if (!Enabled) return;
            try
            {
                f.Opacity = 0;
                Blur(f.Handle);
                RoundRegion(f, radius);
                f.Resize += (o, e) => RoundRegion(f, radius);
                var clock = Stopwatch.StartNew();
                var t = new System.Windows.Forms.Timer { Interval = 8 };
                t.Tick += (o, e) =>
                {
                    double p = clock.Elapsed.TotalSeconds / 0.14;
                    try { if (!f.IsDisposed) f.Opacity = Opacity * Ease.OutCubic(p); } catch { }
                    if (p >= 1 || f.IsDisposed) { t.Stop(); t.Dispose(); }
                };
                t.Start();
            }
            catch { }
        }
    }
}
