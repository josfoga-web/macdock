using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MacDock
{
    // Animaciones de ventanas estilo macOS.
    //  - zoom (maximizar / restaurar) y acomodo: con la imagen en vivo que mantiene Windows de cada ventana (miniatura de DWM),
    //    compuesta por la tarjeta grafica; la app no se redimensiona en cada cuadro.
    //  - genio: la imagen de la ventana se deforma fila por fila en un hilo propio, en todos los nucleos del procesador,
    //    con bordes suavizados y la imagen reducida por niveles (sin escalones ni pixeles al encogerse).
    static class Motion
    {
        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref THUMBPROPS p);
        [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out PSIZE size);
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, int flags);
        [StructLayout(LayoutKind.Sequential)]
        struct THUMBPROPS { public int dwFlags; public N.RECT rcDestination, rcSource; public byte opacity; public int fVisible, fSourceClientAreaOnly; }
        [StructLayout(LayoutKind.Sequential)] struct PSIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] struct WINDOWPLACEMENT { public int length, flags, showCmd; public N.POINT ptMin, ptMax; public N.RECT rcNormal; }

        public static Func<IntPtr, Rectangle> DockTarget;   // icono de la app en el dock (lo conecta el dock)
        public static bool Enabled = true;                  // genio y zoom (el acomodo de ventanas siempre se anima)

        // ---------- escenario: ventana transparente que cubre la pantalla, donde DWM dibuja las miniaturas ----------
        class Stage : Form
        {
            public Stage()
            {
                FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
                BackColor = Color.Black; Bounds = new Rectangle(-32000, -32000, 1, 1);
            }
            // WS_EX_NOREDIRECTIONBITMAP: no tiene superficie propia, asi es transparente y solo se ven las miniaturas
            protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x00200000 | 0x20 | 0x80 | 0x8000000 | 0x8; return cp; } }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; }   // HTTRANSPARENT
                if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }    // MA_NOACTIVATE
                if (m.Msg == 0x0014) { m.Result = new IntPtr(1); return; }
                base.WndProc(ref m);
            }
            protected override void OnPaintBackground(PaintEventArgs e) { }
            protected override void OnPaint(PaintEventArgs e) { }
        }

        // ---------- capa del genio: ventana transparente con lienzo del tamano de la pantalla, creada de antemano ----------
        class Layer : Form
        {
            public Layer()
            {
                FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
                Bounds = new Rectangle(-32000, -32000, 1, 1);
            }
            protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8; return cp; } }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; }
                if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }
                base.WndProc(ref m);
            }
        }

        static Stage stage; static Point origin; static int running;
        static Layer layer; static IntPtr layerH; static LayeredCanvas canvas; static Point lorigin; static bool genieBusy;
        static readonly HashSet<IntPtr> animating = new HashSet<IntPtr>();
        public static bool IsAnimating(IntPtr h) { return animating.Contains(h); }

        static IntPtr StageHandle()
        {
            // se crea una sola vez y se reutiliza: asi la animacion empieza en el mismo cuadro, sin esperar a crear ventanas
            if (stage == null || stage.IsDisposed) stage = new Stage();
            return stage.Handle;
        }

        static void StageAcquire()
        {
            IntPtr sh = StageHandle();
            if (running == 0)
            {
                var vs = SystemInformation.VirtualScreen; origin = vs.Location;
                N.SetWindowPos(sh, new IntPtr(-1), vs.X, vs.Y, vs.Width, vs.Height, 0x0040 | 0x0010);   // SHOWWINDOW | NOACTIVATE
            }
            else N.SetWindowPos(sh, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            running++;
        }

        static void StageRelease()
        {
            if (--running <= 0) { running = 0; if (stage != null && !stage.IsDisposed) N.ShowWindow(stage.Handle, 0); }
        }

        static void PrepareLayer()
        {
            if (layer == null || layer.IsDisposed) { layer = new Layer(); layerH = layer.Handle; }
            var vs = SystemInformation.VirtualScreen;
            if (canvas == null || canvas.Width != vs.Width || canvas.Height != vs.Height)
            {
                if (canvas != null) canvas.Dispose();
                canvas = new LayeredCanvas(vs.Width, vs.Height);
            }
            lorigin = vs.Location;
        }

        static N.RECT ToStage(Rectangle r) { return new N.RECT { left = r.Left - origin.X, top = r.Top - origin.Y, right = r.Right - origin.X, bottom = r.Bottom - origin.Y }; }

        // ---------- motor de miniaturas (zoom, acomodo, escala) ----------
        class Job
        {
            public IntPtr Src; public IntPtr Thumb; public Rectangle Dst; public byte Opacity = 255;
            public double Dur; public Action<Job, double> Frame;
        }

        // onStart corre en el mismo refresco del primer cuadro (ahi se aparta la ventana real).
        // onEnd corre con el ultimo cuadro aun en pantalla; la miniatura se quita un instante despues.
        static bool Run(IntPtr src, double dur, Action<Job, double> frame, Action onStart, Action onEnd)
        {
            StageAcquire();
            var j = new Job { Src = src, Dur = dur, Frame = frame };
            if (DwmRegisterThumbnail(stage.Handle, src, out j.Thumb) != 0) { StageRelease(); return false; }
            animating.Add(src);
            try { frame(j, 0); Apply(j); if (onStart != null) onStart(); }
            catch (Exception ex) { Program.Log(ex); }
            DwmFlush();
            var th = new Thread(() =>
            {
                try
                {
                    var clk = Stopwatch.StartNew();
                    while (true)
                    {
                        double p = clk.Elapsed.TotalSeconds / j.Dur;
                        if (p >= 1) break;
                        j.Frame(j, p); Apply(j);
                        if (DwmFlush() != 0) Thread.Sleep(5);
                    }
                    j.Frame(j, 1); Apply(j);
                }
                catch (Exception ex) { Program.Log(ex); }
                try { stage.BeginInvoke(new Action(() => Finish(j, onEnd))); } catch { }
            }) { IsBackground = true, Name = "MacDockMotion", Priority = ThreadPriority.AboveNormal };
            th.Start();
            return true;
        }

        static void Finish(Job j, Action onEnd)
        {
            try { if (onEnd != null) onEnd(); } catch (Exception ex) { Program.Log(ex); }
            After(30, () =>
            {
                DwmUnregisterThumbnail(j.Thumb);
                animating.Remove(j.Src);
                Resync(j.Src);
                StageRelease();
            });
        }

        static void After(int ms, Action a)
        {
            var t = new System.Windows.Forms.Timer { Interval = ms };
            t.Tick += (o, e) => { t.Stop(); t.Dispose(); try { a(); } catch (Exception ex) { Program.Log(ex); } };
            t.Start();
        }

        static void Apply(Job j)
        {
            var p = new THUMBPROPS { dwFlags = 0x1 | 0x4 | 0x8 | 0x10, rcDestination = ToStage(j.Dst), opacity = j.Opacity, fVisible = 1 };
            DwmUpdateThumbnailProperties(j.Thumb, ref p);
        }

        static float OutQuart(double t) { float x = 1 - Ease.Clamp01(t); return 1 - x * x * x * x; }

        static Action<Job, double> Lerp(Rectangle from, Rectangle to, Func<double, float> curve, byte opFrom, byte opTo)
        {
            return (j, p) =>
            {
                float k = curve(p);
                j.Dst = new Rectangle((int)Math.Round(from.X + (to.X - from.X) * k), (int)Math.Round(from.Y + (to.Y - from.Y) * k),
                    Math.Max(1, (int)Math.Round(from.Width + (to.Width - from.Width) * k)), Math.Max(1, (int)Math.Round(from.Height + (to.Height - from.Height) * k)));
                j.Opacity = (byte)(opFrom + (opTo - opFrom) * Ease.Clamp01(p));
            };
        }

        // ---------- ventana real ----------
        // Windows sigue dibujando la imagen de una ventana fuera de la pantalla, y la miniatura la muestra
        static void Park(IntPtr h)
        {
            var vs = SystemInformation.VirtualScreen;
            N.RECT wr; N.GetWindowRect(h, out wr);
            N.SetWindowPos(h, IntPtr.Zero, vs.Left - (wr.right - wr.left) - 200, vs.Top, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200);
        }
        static void MoveTo(IntPtr h, Point p) { N.SetWindowPos(h, IntPtr.Zero, p.X, p.Y, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200); }
        static Rectangle WinRect(IntPtr h) { N.RECT r; N.GetWindowRect(h, out r); return Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom); }

        // bordes invisibles de Windows 11 entre el rectangulo de la ventana y lo que se ve
        static Padding Insets(IntPtr h)
        {
            var wr = WinRect(h); var fr = Genie.Frame(h);
            var p = new Padding(fr.Left - wr.Left, fr.Top - wr.Top, wr.Right - fr.Right, wr.Bottom - fr.Bottom);
            if (p.Left < 0 || p.Left > 32 || p.Top < 0 || p.Top > 32 || p.Right < 0 || p.Right > 32 || p.Bottom < 0 || p.Bottom > 32) return Padding.Empty;
            return p;
        }

        static Rectangle WindowRectFor(IntPtr h, Rectangle frame)
        {
            var d = Insets(h);
            return Rectangle.FromLTRB(frame.Left - d.Left, frame.Top - d.Top, frame.Right + d.Right, frame.Bottom + d.Bottom);
        }

        static WINDOWPLACEMENT Placement(IntPtr h)
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
            if (!GetWindowPlacement(h, ref wp)) wp.length = 0;
            return wp;
        }

        // donde queda (o quedaba) la ventana normal o maximizada; las coordenadas de WINDOWPLACEMENT son del area de trabajo
        static Rectangle RestoredRect(IntPtr h, WINDOWPLACEMENT wp, Size img, out Rectangle frame)
        {
            var pr = Screen.PrimaryScreen;
            int ox = pr.WorkingArea.Left - pr.Bounds.Left, oy = pr.WorkingArea.Top - pr.Bounds.Top;
            var normal = Rectangle.FromLTRB(wp.rcNormal.left + ox, wp.rcNormal.top + oy, wp.rcNormal.right + ox, wp.rcNormal.bottom + oy);
            if ((wp.flags & 2) == 0)
            {
                WinState s; Padding d;
                if (known.TryGetValue(h, out s) && s.HasIns) d = s.Ins;
                else
                {
                    int b = (N.GetWindowLong(h, -16) & 0x00040000) != 0 ? (int)Math.Round(7 * N.GetDpiForWindow(h) / 96f) : 0;
                    d = new Padding(b, 0, b, b);
                }
                frame = Rectangle.FromLTRB(normal.Left + d.Left, normal.Top + d.Top, normal.Right - d.Right, normal.Bottom - d.Bottom);
                return normal;
            }
            var wa = Screen.FromRectangle(normal).WorkingArea;
            int dx = Math.Max(0, (img.Width - wa.Width) / 2), dy = Math.Max(0, (img.Height - wa.Height) / 2);
            frame = wa;
            return new Rectangle(wa.X - dx, wa.Y - dy, wa.Width + dx * 2, wa.Height + dy * 2);
        }

        // ==================== genio ====================
        sealed class Img { public uint[][] Lv; public int[] W, H; public Rectangle Frame; }
        static readonly Dictionary<IntPtr, Img> shots = new Dictionary<IntPtr, Img>();
        static readonly List<IntPtr> shotOrder = new List<IntPtr>();

        static void Keep(IntPtr h, Img img)
        {
            shots[h] = img; shotOrder.Remove(h); shotOrder.Add(h);
            for (int i = shotOrder.Count - 1; i >= 0; i--)
            {
                var k = shotOrder[i];
                if (k != h && (!N.IsWindow(k) || !N.IsIconic(k))) { shots.Remove(k); shotOrder.RemoveAt(i); }
            }
            while (shotOrder.Count > 6) { shots.Remove(shotOrder[0]); shotOrder.RemoveAt(0); }
        }

        // copia de la pantalla lo que se ve en 'frame' (la miniatura ya esta encima de todo) y arma los niveles reducidos
        static Img Grab(Rectangle frame, bool round, int dpi)
        {
            var r = Rectangle.Intersect(frame, SystemInformation.VirtualScreen);
            if (r.Width < 40 || r.Height < 40) return null;
            int w = r.Width, h = r.Height;
            var px = new uint[w * h];
            using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size, CopyPixelOperation.SourceCopy);
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                try
                {
                    var tmp = new int[w];
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(bd.Scan0 + y * bd.Stride, tmp, 0, w);
                        int o = y * w;
                        for (int x = 0; x < w; x++) px[o + x] = (uint)tmp[x] | 0xFF000000u;
                    }
                }
                finally { bmp.UnlockBits(bd); }
            }
            // esquinas redondeadas de Windows 11, con borde suave (alfa premultiplicado)
            if (round)
            {
                float rad = 8 * dpi / 96f; int ri = (int)Math.Ceiling(rad);
                for (int y = 0; y < ri && y < h / 2; y++)
                    for (int x = 0; x < ri && x < w / 2; x++)
                    {
                        float dx = rad - x - 0.5f, dy = rad - y - 0.5f;
                        float cov = Ease.Clamp01(rad + 0.5f - (float)Math.Sqrt(dx * dx + dy * dy));
                        if (cov >= 1) continue;
                        uint a = (uint)(cov * 256);
                        int[] ids = { y * w + x, y * w + (w - 1 - x), (h - 1 - y) * w + x, (h - 1 - y) * w + (w - 1 - x) };
                        foreach (var id in ids) px[id] = Scale(px[id], a);
                    }
            }
            var lv = new List<uint[]> { px }; var ws = new List<int> { w }; var hs = new List<int> { h };
            while (ws[ws.Count - 1] >= 16 && hs[hs.Count - 1] >= 16)
            {
                uint[] s = lv[lv.Count - 1]; int sw = ws[ws.Count - 1], sh = hs[hs.Count - 1];
                int nw = sw / 2, nh = sh / 2; var d = new uint[nw * nh];
                for (int y = 0; y < nh; y++)
                {
                    int a0 = (y * 2) * sw, a1 = a0 + sw, o = y * nw;
                    for (int x = 0; x < nw; x++)
                    {
                        uint p0 = s[a0 + x * 2], p1 = s[a0 + x * 2 + 1], p2 = s[a1 + x * 2], p3 = s[a1 + x * 2 + 1];
                        uint rb = ((p0 & 0xFF00FFu) + (p1 & 0xFF00FFu) + (p2 & 0xFF00FFu) + (p3 & 0xFF00FFu) + 0x20002u) >> 2 & 0xFF00FFu;
                        uint ag = (((p0 >> 8) & 0xFF00FFu) + ((p1 >> 8) & 0xFF00FFu) + ((p2 >> 8) & 0xFF00FFu) + ((p3 >> 8) & 0xFF00FFu) + 0x20002u) >> 2 & 0xFF00FFu;
                        d[o + x] = rb | (ag << 8);
                    }
                }
                lv.Add(d); ws.Add(nw); hs.Add(nh);
            }
            return new Img { Lv = lv.ToArray(), W = ws.ToArray(), H = hs.ToArray(), Frame = r };
        }

        static uint Mix(uint p, uint q, uint w)
        {
            uint iw = 256 - w;
            return ((((p & 0xFF00FFu) * iw + (q & 0xFF00FFu) * w) >> 8) & 0xFF00FFu) | ((((p >> 8) & 0xFF00FFu) * iw + ((q >> 8) & 0xFF00FFu) * w) & 0xFF00FF00);
        }
        static uint Scale(uint p, uint a) { return ((((p & 0xFF00FFu) * a) >> 8) & 0xFF00FFu) | ((((p >> 8) & 0xFF00FFu) * a) & 0xFF00FF00); }
        static float Smooth(float x) { x = x < 0 ? 0 : x > 1 ? 1 : x; return x * x * (3 - 2 * x); }

        // Forma del genio en el instante t (0 = ventana completa, 1 = dentro del icono), muestreada en M+1 alturas de la ventana:
        // Y = fila de pantalla, L/R = bordes. La parte cercana al dock arranca primero y se estira hacia el icono (el embudo).
        static void Shape(Rectangle win, Rectangle icon, float t, float[] Y, float[] L, float[] R)
        {
            int M = Y.Length - 1;
            float sx = win.Left, sy = win.Top, sw = win.Width, sh = win.Height;
            float tx = icon.Left, ty = icon.Top, tw = icon.Width, th = icon.Height;
            bool down = icon.Top + icon.Height / 2 >= win.Top + win.Height / 2;
            const float lag = 0.62f;
            for (int i = 0; i <= M; i++)
            {
                float v = i / (float)M, lead = down ? 1 - v : v;
                float q = Smooth(t * (1 + lag) - lead * lag);
                Y[i] = (sy + v * sh) + ((ty + v * th) - (sy + v * sh)) * q;
                float k = Smooth(q * 1.35f);
                L[i] = sx + (tx - sx) * k; R[i] = sx + sw + (tx + tw - sx - sw) * k;
                if (i > 0 && Y[i] < Y[i - 1]) Y[i] = Y[i - 1];
            }
        }

        // dibuja el cuadro en el lienzo y devuelve el rectangulo ocupado (coordenadas del lienzo)
        static Rectangle Render(Img img, Rectangle icon, float t, float[] Y, float[] L, float[] R)
        {
            Shape(img.Frame, icon, t, Y, L, R);
            int M = Y.Length - 1;
            float minX = float.MaxValue, maxX = float.MinValue;
            for (int i = 0; i <= M; i++) { if (L[i] < minX) minX = L[i]; if (R[i] > maxX) maxX = R[i]; }
            int CW = canvas.Width, CH = canvas.Height;
            int bx0 = Math.Max(0, (int)Math.Floor(minX) - lorigin.X), bx1 = Math.Min(CW, (int)Math.Ceiling(maxX) - lorigin.X + 1);
            int by0 = Math.Max(0, (int)Math.Floor(Y[0]) - lorigin.Y), by1 = Math.Min(CH, (int)Math.Ceiling(Y[M]) - lorigin.Y + 1);
            if (bx1 <= bx0 || by1 <= by0) return Rectangle.Empty;
            IntPtr bits = canvas.Bits;
            int rows = by1 - by0, bands = Math.Max(1, Math.Min(Environment.ProcessorCount * 2, rows / 16));
            Parallel.For(0, bands, b => Rows(img, by0 + rows * b / bands, by0 + rows * (b + 1) / bands, bx0, bx1, Y, L, R, bits, CW));
            return Rectangle.FromLTRB(bx0, by0, bx1, by1);
        }

        static unsafe void Rows(Img img, int ya, int yb, int bx0, int bx1, float[] Y, float[] L, float[] R, IntPtr bitsPtr, int CW)
        {
            int M = Y.Length - 1, maxLv = img.Lv.Length - 1;
            float yMin = Y[0], yMax = Y[M];
            for (int cy = ya; cy < yb; cy++)
            {
                uint* row = (uint*)bitsPtr + (long)cy * CW;
                float ys = cy + lorigin.Y;
                float top = Math.Max(ys, yMin), bot = Math.Min(ys + 1, yMax), covY = bot - top;
                if (covY <= 0.002f) { for (int x = bx0; x < bx1; x++) row[x] = 0; continue; }
                float yc = (top + bot) * 0.5f;
                int lo = 0, hi = M;
                while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (Y[mid] <= yc) lo = mid; else hi = mid; }
                float dy = Y[hi] - Y[lo], f = dy > 1e-4f ? (yc - Y[lo]) / dy : 0f;
                float v = (lo + f) / M, l = L[lo] + (L[hi] - L[lo]) * f, r = R[lo] + (R[hi] - R[lo]) * f, span = r - l;
                if (span < 0.05f) { for (int x = bx0; x < bx1; x++) row[x] = 0; continue; }
                // nivel de la imagen segun cuanto se encoge esta fila (horizontal y vertical)
                float vs = dy > 1e-4f ? img.H[0] / (M * dy) : img.H[0];
                float scale = Math.Max(img.W[0] / span, vs);
                int lv = 0; while (lv < maxLv && scale >= 2f) { scale *= 0.5f; lv++; }
                uint[] src = img.Lv[lv]; int sw = img.W[lv], sh = img.H[lv];
                float syf = v * sh - 0.5f; if (syf < 0) syf = 0; if (syf > sh - 1) syf = sh - 1;
                int r0 = (int)syf, r1 = r0 + 1 < sh ? r0 + 1 : r0; uint wy = (uint)((syf - r0) * 256);
                float lc = l - lorigin.X, rc = r - lorigin.X;
                int xa = Math.Max(bx0, (int)Math.Floor(lc)), xb = Math.Min(bx1, (int)Math.Ceiling(rc));
                for (int x = bx0; x < xa; x++) row[x] = 0;
                for (int x = Math.Max(xa, xb); x < bx1; x++) row[x] = 0;
                if (xb <= xa) continue;
                uint ay = (uint)(covY * 256 + 0.5f);
                double step = sw / (double)span;
                long fx = (long)(((xa + 0.5 - lc) * step - 0.5) * 65536), fstep = (long)(step * 65536), maxFx = (long)(sw - 1) << 16;
                fixed (uint* sp = src)
                {
                    uint* p0 = sp + r0 * sw, p1 = sp + r1 * sw;
                    for (int x = xa; x < xb; x++, fx += fstep)
                    {
                        long c = fx < 0 ? 0 : fx > maxFx ? maxFx : fx;
                        int ci = (int)(c >> 16), c1 = ci + 1 < sw ? ci + 1 : ci; uint wx = (uint)((c >> 8) & 0xFF);
                        uint px = Mix(Mix(p0[ci], p0[c1], wx), Mix(p1[ci], p1[c1], wx), wy);
                        uint a = ay;
                        if (x == xa || x == xb - 1)
                        {
                            float cov = Math.Min(x + 1, rc) - Math.Max(x, lc);
                            a = (uint)(a * Ease.Clamp01(cov));
                        }
                        row[x] = a >= 256 ? px : Scale(px, a);
                    }
                }
            }
        }

        static void Push(Rectangle box)
        {
            if (box.IsEmpty)
            {
                unsafe { *(uint*)canvas.Bits = 0; }
                box = new Rectangle(0, 0, 1, 1);
            }
            canvas.PushRegion(layerH, lorigin.X + box.X, lorigin.Y + box.Y, box);
        }

        // Anima el genio en un hilo propio. getImg (en ese hilo) consigue la imagen; onShown corre en el hilo principal en cuanto
        // el primer cuadro esta en pantalla; onEnd al terminar, con el ultimo cuadro aun visible.
        static void RunGenie(Func<Img> getImg, Rectangle icon, bool reverse, double dur, Action<Img> onShown, Action onEnd, Action onFail)
        {
            genieBusy = true;
            PrepareLayer();
            IntPtr lh = layerH;
            var th = new Thread(() =>
            {
                Img img = null;
                try { img = getImg(); } catch (Exception ex) { Program.Log(ex); }
                if (img == null) { try { layer.BeginInvoke(new Action(() => { genieBusy = false; if (onFail != null) onFail(); })); } catch { } return; }
                try
                {
                    int M = Math.Max(64, Math.Min(512, img.Frame.Height / 3));
                    float[] Y = new float[M + 1], L = new float[M + 1], R = new float[M + 1];
                    Push(Render(img, icon, reverse ? 1f : 0f, Y, L, R));
                    N.SetWindowPos(lh, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
                    DwmFlush();
                    try { layer.BeginInvoke(new Action(() => { try { if (onShown != null) onShown(img); } catch (Exception ex) { Program.Log(ex); } })); } catch { }
                    var clk = Stopwatch.StartNew();
                    while (true)
                    {
                        double p = clk.Elapsed.TotalSeconds / dur;
                        if (p >= 1) break;
                        Push(Render(img, icon, (float)(reverse ? 1 - p : p), Y, L, R));
                        if (DwmFlush() != 0) Thread.Sleep(4);
                    }
                    Push(Render(img, icon, reverse ? 0f : 1f, Y, L, R));
                }
                catch (Exception ex) { Program.Log(ex); }
                try
                {
                    layer.BeginInvoke(new Action(() =>
                    {
                        try { if (onEnd != null) onEnd(); } catch (Exception ex) { Program.Log(ex); }
                        After(reverse ? 40 : 16, () => { N.ShowWindow(lh, 0); genieBusy = false; });
                    }));
                }
                catch { genieBusy = false; }
            }) { IsBackground = true, Name = "MacDockGenie", Priority = ThreadPriority.AboveNormal };
            th.Start();
        }

        // pone la imagen de la ventana tal cual encima de todo (para poder copiarla sin el dock ni la barra encima)
        static IntPtr Hold(IntPtr h, Rectangle at, out Size img)
        {
            img = Size.Empty;
            StageAcquire();
            IntPtr t;
            if (DwmRegisterThumbnail(stage.Handle, h, out t) != 0) { StageRelease(); return IntPtr.Zero; }
            PSIZE sz; if (DwmQueryThumbnailSourceSize(t, out sz) == 0) img = new Size(sz.cx, sz.cy);
            var p = new THUMBPROPS { dwFlags = 0x1 | 0x4 | 0x8 | 0x10, rcDestination = ToStage(at), opacity = 255, fVisible = 1 };
            DwmUpdateThumbnailProperties(t, ref p);
            return t;
        }
        static void Release(IntPtr t) { if (t != IntPtr.Zero) { DwmUnregisterThumbnail(t); StageRelease(); } }

        static Func<Img> Capture(Rectangle frame, bool round, int dpi)
        {
            return () => { DwmFlush(); DwmFlush(); return Grab(frame, round, dpi); };
        }

        // ---------- genio desde MacDock (dock, barra de menus, Finder) ----------
        public static bool GenieMinimize(IntPtr h)
        {
            if (DockTarget == null || genieBusy || N.IsIconic(h) || animating.Contains(h)) return false;
            var wp = Placement(h);
            if (wp.length == 0) return false;
            var win = WinRect(h); var frame = Genie.Frame(h);
            if (frame.Width < 60 || frame.Height < 60) return false;
            bool zoomed = N.IsZoomed(h);
            Size sz; IntPtr held = Hold(h, win, out sz);
            if (held == IntPtr.Zero) return false;
            Ignore(h); animating.Add(h);
            RunGenie(Capture(frame, !zoomed, (int)N.GetDpiForWindow(h)), DockTarget(h), false, 0.46,
                img =>
                {
                    // el genio ya cubre la ventana: se minimiza de verdad (sin animacion de Windows) y se quita la miniatura
                    Keep(h, img);
                    var w2 = wp; w2.showCmd = 6;
                    if (zoomed) w2.flags |= 2;
                    if (!SetWindowPlacement(h, ref w2)) N.ShowWindow(h, 6);
                    Release(held);
                },
                () => animating.Remove(h),
                () => { Release(held); animating.Remove(h); N.ShowWindow(h, 6); });
            return true;
        }

        public static bool GenieRestore(IntPtr h)
        {
            if (DockTarget == null || !N.IsIconic(h) || animating.Contains(h)) return false;
            var icon = DockTarget(h);
            Ignore(h);
            Img img;
            if (!genieBusy && shots.TryGetValue(h, out img))
            {
                shots.Remove(h); shotOrder.Remove(h);
                animating.Add(h);
                Action show = () => { N.ShowWindow(h, 9); N.SetForegroundWindow(h); };
                RunGenie(() => img, icon, true, 0.42, null, () => { show(); animating.Remove(h); }, () => { show(); animating.Remove(h); });
                return true;
            }
            // sin imagen guardada (minimizada antes de abrir MacDock): crece desde el icono con su imagen en vivo
            var wp = Placement(h);
            if (wp.length == 0) return false;
            Size sz; IntPtr t = Hold(h, new Rectangle(-32000, -32000, 1, 1), out sz);
            Release(t);
            if (sz.Width < 100 || sz.Height < 100) return false;
            Rectangle fr; var final = RestoredRect(h, wp, sz, out fr);
            var from = Fit(final, icon);
            return Run(h, 0.34, Lerp(from, final, OutQuart, 40, 255), null, () => { N.ShowWindow(h, 9); N.SetForegroundWindow(h); });
        }

        static Rectangle Fit(Rectangle win, Rectangle icon)
        {
            float k = Math.Min(icon.Width / (float)win.Width, icon.Height / (float)win.Height);
            int w = Math.Max(4, (int)(win.Width * k)), hh = Math.Max(4, (int)(win.Height * k));
            return new Rectangle(icon.X + (icon.Width - w) / 2, icon.Y + (icon.Height - hh) / 2, w, hh);
        }

        // minimizar con el boton de Windows (o Win+abajo): la ventana ya se escondio; su imagen se pone en su lugar,
        // se copia y entra al dock con el genio
        static void OnMinimize(IntPtr h)
        {
            if (Skipped(h) || DockTarget == null || genieBusy || animating.Contains(h)) return;
            int st = N.GetWindowLong(h, -16);
            if ((st & 0x00C00000) != 0x00C00000 && (st & 0x00040000) == 0) return;
            if (N.GetWindowLong(h, -8) != 0 || (N.GetWindowLong(h, -20) & 0x80) != 0) return;
            var wp = Placement(h);
            if (wp.length == 0) return;
            Size sz; IntPtr held = Hold(h, new Rectangle(-32000, -32000, 1, 1), out sz);
            if (held == IntPtr.Zero) return;
            if (sz.Width < 100 || sz.Height < 100) { Release(held); return; }
            Rectangle frame; var win = RestoredRect(h, wp, sz, out frame);
            var p = new THUMBPROPS { dwFlags = 0x1 | 0x4 | 0x8 | 0x10, rcDestination = ToStage(win), opacity = 255, fVisible = 1 };
            DwmUpdateThumbnailProperties(held, ref p);
            animating.Add(h);
            RunGenie(Capture(frame, (wp.flags & 2) == 0, (int)N.GetDpiForWindow(h)), DockTarget(h), false, 0.46,
                img => { Keep(h, img); Release(held); },
                () => animating.Remove(h),
                () => { Release(held); animating.Remove(h); });
        }

        // ---------- acomodar / mover y redimensionar (la app se redimensiona una sola vez, fuera de la vista) ----------
        public static void MoveResize(IntPtr h, Rectangle frame)
        {
            var to = WindowRectFor(h, frame);
            var from = WinRect(h);
            if (N.IsIconic(h) || animating.Contains(h) || SystemInformation.TerminalServerSession || from.Width < 40)
            { N.SetWindowPos(h, IntPtr.Zero, to.X, to.Y, to.Width, to.Height, 0x0004 | 0x0010); return; }
            var vs = SystemInformation.VirtualScreen;
            bool ok = Run(h, 0.3, Lerp(from, to, OutQuart, 255, 255),
                // fuera de la vista ya con su tamano final: tiene toda la animacion para repintarse
                () => N.SetWindowPos(h, IntPtr.Zero, vs.Left - to.Width - 200, vs.Top, to.Width, to.Height, 0x0004 | 0x0010 | 0x0200),
                () => MoveTo(h, to.Location));
            if (!ok) N.SetWindowPos(h, IntPtr.Zero, to.X, to.Y, to.Width, to.Height, 0x0004 | 0x0010);
        }

        // ---------- eventos del sistema ----------
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
        static WinEventProc proc; static IntPtr hookMin, hookLoc, hookFg, hookMove, sizing;

        class WinState { public bool Zoomed, HasIns; public Rectangle Normal, Max; public Padding Ins; }
        static readonly Dictionary<IntPtr, WinState> known = new Dictionary<IntPtr, WinState>();
        static readonly Dictionary<IntPtr, DateTime> skip = new Dictionary<IntPtr, DateTime>();

        public static void Ignore(IntPtr h) { skip[h] = DateTime.Now; }   // MacDock ya anima esta ventana
        static bool Skipped(IntPtr h) { DateTime t; return skip.TryGetValue(h, out t) && (DateTime.Now - t).TotalSeconds < 1.2; }

        public static void Install()
        {
            StageHandle();
            try { PrepareLayer(); } catch (Exception ex) { Program.Log(ex); }
            if (hookMin != IntPtr.Zero) return;
            proc = OnEvent;
            hookFg = SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, proc, 0, 0, 0x0002);    // ventana activa: se recuerda su estado
            hookMin = SetWinEventHook(0x0016, 0x0016, IntPtr.Zero, proc, 0, 0, 0x0002);   // empieza a minimizarse
            hookMove = SetWinEventHook(0x000A, 0x000B, IntPtr.Zero, proc, 0, 0, 0x0002);  // arrastres y redimensiones
            Follow(N.GetForegroundWindow());
            Track(N.GetForegroundWindow());
        }

        // Los cambios de posicion (maximizar, restaurar) se escuchan solo en la app activa: el sistema emite
        // ese evento con cada movimiento del puntero, y asi MacDock no despierta por cada uno.
        static uint hookedPid; static readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        static void Follow(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            uint pid; N.GetWindowThreadProcessId(h, out pid);
            if (pid == 0 || pid == ownPid || (pid == hookedPid && hookLoc != IntPtr.Zero)) return;   // los menus de MacDock no cuentan
            if (hookLoc != IntPtr.Zero) UnhookWinEvent(hookLoc);
            hookedPid = pid;
            hookLoc = SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, proc, pid, 0, 0x0002);
        }

        public static void Uninstall()
        {
            foreach (var hk in new[] { hookMin, hookLoc, hookFg, hookMove }) if (hk != IntPtr.Zero) UnhookWinEvent(hk);
            hookMin = hookLoc = hookFg = hookMove = IntPtr.Zero; hookedPid = 0;
        }

        static bool Animatable(IntPtr h)
        {
            if (!N.IsWindowVisible(h)) return false;
            int st = N.GetWindowLong(h, -16), ex = N.GetWindowLong(h, -20);
            if ((st & 0x00C00000) != 0x00C00000 && (st & 0x00040000) == 0) return false;   // sin barra de titulo ni borde
            if ((ex & 0x80) != 0) return false;   // ventanas de herramientas
            if (N.GetWindowLong(h, -8) != 0) return false;   // dialogos con dueno
            return !Wobbly.IsWobbling(h);
        }

        static void OnEvent(IntPtr hk, uint ev, IntPtr h, int obj, int child, uint thread, uint time)
        {
            if (obj != 0 || h == IntPtr.Zero) return;
            try
            {
                if (ev == 0x0003) { Follow(h); Track(h); return; }
                if (ev == 0x000A) { sizing = h; return; }
                if (ev == 0x000B) { if (sizing == h) sizing = IntPtr.Zero; Resync(h); return; }
                if (ev == 0x0016) { if (Enabled) OnMinimize(h); return; }
                if (GetAncestor(h, 2) != h) return;   // solo ventanas principales
                OnLocation(h);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        static WinState Track(IntPtr h)
        {
            if (h == IntPtr.Zero || animating.Contains(h) || !N.IsWindowVisible(h) || N.IsIconic(h)) return null;
            WinState s;
            if (known.TryGetValue(h, out s)) return s;
            if (known.Count > 400) foreach (var k in new List<IntPtr>(known.Keys)) if (!N.IsWindow(k)) known.Remove(k);
            s = new WinState();
            known[h] = s;
            Resync(h);
            return null;   // recien visto: no hay estado anterior con que comparar
        }

        // el estado guardado vuelve a coincidir con el real (asi no se anima dos veces)
        static void Resync(IntPtr h)
        {
            WinState s;
            if (!known.TryGetValue(h, out s) || !N.IsWindow(h) || N.IsIconic(h)) return;
            s.Zoomed = N.IsZoomed(h); var r = WinRect(h);
            if (s.Zoomed) s.Max = r;
            else if (SystemInformation.VirtualScreen.IntersectsWith(r)) { s.Normal = r; s.Ins = Insets(h); s.HasIns = true; }
        }

        static void OnLocation(IntPtr h)
        {
            if (animating.Contains(h) || !N.IsWindowVisible(h) || N.IsIconic(h)) return;
            var s = Track(h);
            if (s == null) return;
            bool z = N.IsZoomed(h); var r = WinRect(h);
            if (s.Zoomed == z)
            {
                if (z) s.Max = r;
                else if (SystemInformation.VirtualScreen.IntersectsWith(r)) s.Normal = r;   // apartada fuera de la pantalla no cuenta
                return;
            }
            var old = z ? s.Normal : s.Max;
            s.Zoomed = z;
            if (z) s.Max = r; else s.Normal = r;
            if (!Enabled || Skipped(h) || h == sizing || !Animatable(h)) return;   // arrastrar una ventana maximizada no se anima
            if (old.Width < 60 || old.Height < 60) old = Screen.FromHandle(h).WorkingArea;
            if (old == r) return;
            // la ventana ya cambio de tamano: se aparta en el mismo refresco y su imagen crece (o se encoge) desde el tamano anterior
            Run(h, z ? 0.3 : 0.27, Lerp(old, r, OutQuart, 255, 255), () => Park(h), () => MoveTo(h, r.Location));
        }
    }
}
