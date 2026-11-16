using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MacDock
{
    // Animaciones de ventanas con la imagen en vivo que mantiene Windows de cada ventana (miniaturas de DWM,
    // las mismas de la vista de tareas). Las compone la tarjeta grafica: no se captura nada, la app no tiene que
    // redimensionarse ni repintarse en cada cuadro y todo corre en un hilo propio sincronizado con la pantalla.
    //  - genio al minimizar y al restaurar (la ventana se corta en franjas y cada una es una miniatura)
    //  - zoom al maximizar y al restaurar
    //  - acomodar ventanas (mitades, centrar, llenar)
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

        // ---------- escenario: una ventana transparente que cubre la pantalla, sobre la que DWM dibuja las miniaturas ----------
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

        static Stage stage; static Point origin; static int running;
        static readonly HashSet<IntPtr> animating = new HashSet<IntPtr>();
        public static bool IsAnimating(IntPtr h) { return animating.Contains(h); }

        static IntPtr StageHandle()
        {
            // se crea una sola vez y se reutiliza: asi la animacion empieza en el mismo cuadro, sin esperar a crear ventanas
            if (stage == null || stage.IsDisposed) stage = new Stage();
            return stage.Handle;
        }

        class Job
        {
            public IntPtr Src; public IntPtr[] Thumbs; public Size SrcSize;
            public Rectangle[] Dst, Cut; public bool[] Vis; public byte Opacity = 255;
            public double Dur, Delay; public Action<Job, double> Frame;
        }

        // Registra 'count' miniaturas de src y las anima en un hilo propio (un cuadro por refresco de la pantalla).
        // onStart corre justo despues de poner el primer cuadro (en el mismo refresco): ahi se aparta la ventana real.
        // onEnd corre en el hilo principal con el ultimo cuadro aun en pantalla; las miniaturas se quitan un instante despues.
        static bool Run(IntPtr src, int count, double dur, double delay, Action<Job, double> frame, Action onStart, Action onEnd)
        {
            IntPtr sh = StageHandle();
            if (running == 0)
            {
                var vs = SystemInformation.VirtualScreen; origin = vs.Location;
                N.SetWindowPos(sh, new IntPtr(-1), vs.X, vs.Y, vs.Width, vs.Height, 0x0040 | 0x0010);   // SHOWWINDOW | NOACTIVATE
            }
            else N.SetWindowPos(sh, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            var j = new Job { Src = src, Thumbs = new IntPtr[count], Dst = new Rectangle[count], Vis = new bool[count], Dur = dur, Delay = delay, Frame = frame };
            for (int i = 0; i < count; i++)
                if (DwmRegisterThumbnail(sh, src, out j.Thumbs[i]) != 0)
                {
                    for (int k = 0; k < i; k++) DwmUnregisterThumbnail(j.Thumbs[k]);
                    if (running == 0) N.ShowWindow(sh, 0);
                    return false;
                }
            PSIZE sz;
            if (DwmQueryThumbnailSourceSize(j.Thumbs[0], out sz) == 0 && sz.cx > 0 && sz.cy > 0) j.SrcSize = new Size(sz.cx, sz.cy);
            else { N.RECT wr; N.GetWindowRect(src, out wr); j.SrcSize = new Size(Math.Max(1, wr.right - wr.left), Math.Max(1, wr.bottom - wr.top)); }
            running++; animating.Add(src);
            try
            {
                frame(j, 0);
                Apply(j, delay > 0);
                if (onStart != null) onStart();
            }
            catch (Exception ex) { Program.Log(ex); }
            DwmFlush();
            var th = new Thread(() =>
            {
                try
                {
                    var clk = Stopwatch.StartNew();
                    while (true)
                    {
                        double p = (clk.Elapsed.TotalSeconds - j.Delay) / j.Dur;
                        if (p >= 1) break;
                        if (p >= 0) { j.Frame(j, p); Apply(j, false); }
                        if (DwmFlush() != 0) Thread.Sleep(5);
                    }
                    j.Frame(j, 1); Apply(j, false);
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
            // dos refrescos para que la ventana real ya este pintada en su sitio antes de quitar la miniatura
            var t = new System.Windows.Forms.Timer { Interval = 30 };
            t.Tick += (o, e) =>
            {
                t.Stop(); t.Dispose();
                foreach (var th in j.Thumbs) DwmUnregisterThumbnail(th);
                animating.Remove(j.Src);
                Resync(j.Src);
                if (--running <= 0) { running = 0; if (stage != null && !stage.IsDisposed) N.ShowWindow(stage.Handle, 0); }
            };
            t.Start();
        }

        static void Apply(Job j, bool hidden)
        {
            for (int i = 0; i < j.Thumbs.Length; i++)
            {
                var p = new THUMBPROPS();
                if (hidden || !j.Vis[i]) { p.dwFlags = 0x8; p.fVisible = 0; }
                else
                {
                    var r = j.Dst[i];
                    p.dwFlags = 0x1 | 0x4 | 0x8 | 0x10 | (j.Cut != null ? 0x2 : 0);
                    p.rcDestination = new N.RECT { left = r.Left - origin.X, top = r.Top - origin.Y, right = r.Right - origin.X, bottom = r.Bottom - origin.Y };
                    if (j.Cut != null) { var c = j.Cut[i]; p.rcSource = new N.RECT { left = c.Left, top = c.Top, right = c.Right, bottom = c.Bottom }; }
                    p.opacity = j.Opacity; p.fVisible = 1;
                }
                DwmUpdateThumbnailProperties(j.Thumbs[i], ref p);
            }
        }

        // tamano de la imagen que Windows guarda de la ventana (sirve para saber si la conserva estando minimizada)
        static Size Probe(IntPtr h)
        {
            IntPtr t;
            if (DwmRegisterThumbnail(StageHandle(), h, out t) != 0) return Size.Empty;
            PSIZE sz; bool ok = DwmQueryThumbnailSourceSize(t, out sz) == 0;
            DwmUnregisterThumbnail(t);
            return ok ? new Size(sz.cx, sz.cy) : Size.Empty;
        }

        // ---------- curvas ----------
        static float OutQuart(double t) { float x = 1 - Ease.Clamp01(t); return 1 - x * x * x * x; }
        static float Smooth(float x) { x = x < 0 ? 0 : x > 1 ? 1 : x; return x * x * (3 - 2 * x); }

        static Action<Job, double> Lerp(Rectangle from, Rectangle to, Func<double, float> curve, byte opFrom, byte opTo)
        {
            return (j, p) =>
            {
                float k = curve(p);
                j.Dst[0] = new Rectangle((int)Math.Round(from.X + (to.X - from.X) * k), (int)Math.Round(from.Y + (to.Y - from.Y) * k),
                    Math.Max(1, (int)Math.Round(from.Width + (to.Width - from.Width) * k)), Math.Max(1, (int)Math.Round(from.Height + (to.Height - from.Height) * k)));
                j.Vis[0] = true;
                j.Opacity = (byte)(opFrom + (opTo - opFrom) * Ease.Clamp01(p));
            };
        }

        // Genio: cada franja horizontal es una miniatura de un trozo de la ventana. La parte cercana al dock arranca
        // primero y se estira hacia el icono formando el embudo; el resto pasa despues por el, como en macOS.
        // Las franjas se colocan en pixeles enteros y contiguas, asi nunca queda una rendija entre ellas.
        static Action<Job, double> GenieFn(Rectangle win, Rectangle icon, bool reverse)
        {
            return (j, p) =>
            {
                int n = j.Dst.Length;
                if (j.Cut == null)
                {
                    var cut = new Rectangle[n];
                    for (int i = 0; i < n; i++)
                    {
                        int a = (int)Math.Round(i * (double)j.SrcSize.Height / n), b = (int)Math.Round((i + 1) * (double)j.SrcSize.Height / n);
                        cut[i] = Rectangle.FromLTRB(0, a, j.SrcSize.Width, Math.Max(a + 1, b));
                    }
                    j.Cut = cut;
                }
                float t = (float)(reverse ? 1 - p : p);
                float sx = win.Left, sy = win.Top, sw = win.Width, sh = win.Height;
                float tx = icon.Left, ty = icon.Top, tw = icon.Width, th = icon.Height;
                bool down = icon.Top + icon.Height / 2 >= win.Top + win.Height / 2;
                const float lag = 0.62f;
                int prevBottom = int.MinValue;
                for (int i = 0; i < n; i++)
                {
                    float v0 = i / (float)n, v1 = (i + 1) / (float)n;
                    float lead0 = down ? 1 - v0 : v0, lead1 = down ? 1 - v1 : v1;
                    float q0 = Smooth(t * (1 + lag) - lead0 * lag), q1 = Smooth(t * (1 + lag) - lead1 * lag);
                    float y0 = (sy + v0 * sh) + ((ty + v0 * th) - (sy + v0 * sh)) * q0;
                    float y1 = (sy + v1 * sh) + ((ty + v1 * th) - (sy + v1 * sh)) * q1;
                    float k = Smooth((q0 + q1) / 2 * 1.35f);
                    float l = sx + (tx - sx) * k, r = sx + sw + (tx + tw - sx - sw) * k;
                    int top = prevBottom == int.MinValue ? (int)Math.Round(y0) : prevBottom;
                    int bottom = (int)Math.Round(y1);
                    int xl = (int)Math.Round(l), xr = (int)Math.Round(r);
                    if (bottom <= top || xr <= xl) { j.Vis[i] = false; continue; }
                    prevBottom = bottom;
                    j.Dst[i] = Rectangle.FromLTRB(xl, top, xr, bottom); j.Vis[i] = true;
                }
                // al salir del icono aparece suavemente (la app puede tardar un instante en tener su imagen lista)
                j.Opacity = reverse ? (byte)(255 * Ease.Clamp01(p / 0.12)) : (byte)255;
            };
        }

        static int Strips(Rectangle win) { return Math.Max(40, Math.Min(110, win.Height / 9)); }

        // ---------- ventana real: apartarla y devolverla ----------
        // Windows sigue dibujando la imagen de una ventana fuera de la pantalla, y la miniatura la muestra
        static void Park(IntPtr h)
        {
            var vs = SystemInformation.VirtualScreen;
            N.RECT wr; N.GetWindowRect(h, out wr);
            N.SetWindowPos(h, IntPtr.Zero, vs.Left - (wr.right - wr.left) - 200, vs.Top, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200);
        }
        static void MoveTo(IntPtr h, Point p) { N.SetWindowPos(h, IntPtr.Zero, p.X, p.Y, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200); }

        static Rectangle WinRect(IntPtr h) { N.RECT r; N.GetWindowRect(h, out r); return Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom); }

        // rectangulo de ventana (con los bordes invisibles de Windows 11) para que su parte visible quede en 'frame'
        static Rectangle WindowRectFor(IntPtr h, Rectangle frame)
        {
            var wr = WinRect(h);
            var fr = Genie.Frame(h);
            int dL = fr.Left - wr.Left, dT = fr.Top - wr.Top, dR = wr.Right - fr.Right, dB = wr.Bottom - fr.Bottom;
            if (dL < 0 || dL > 32 || dT < 0 || dT > 32 || dR < 0 || dR > 32 || dB < 0 || dB > 32) dL = dT = dR = dB = 0;
            return Rectangle.FromLTRB(frame.Left - dL, frame.Top - dT, frame.Right + dR, frame.Bottom + dB);
        }

        static WINDOWPLACEMENT Placement(IntPtr h)
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
            if (!GetWindowPlacement(h, ref wp)) wp.length = 0;
            return wp;
        }

        // donde queda la ventana al restaurarla (las coordenadas de WINDOWPLACEMENT son del area de trabajo del monitor principal)
        static Rectangle RestoredRect(WINDOWPLACEMENT wp, Size img)
        {
            var pr = Screen.PrimaryScreen;
            int ox = pr.WorkingArea.Left - pr.Bounds.Left, oy = pr.WorkingArea.Top - pr.Bounds.Top;
            var normal = Rectangle.FromLTRB(wp.rcNormal.left + ox, wp.rcNormal.top + oy, wp.rcNormal.right + ox, wp.rcNormal.bottom + oy);
            if ((wp.flags & 2) == 0) return normal;   // no vuelve maximizada
            var wa = Screen.FromRectangle(normal).WorkingArea;
            int dx = Math.Max(0, (img.Width - wa.Width) / 2), dy = Math.Max(0, (img.Height - wa.Height) / 2);
            return new Rectangle(wa.X - dx, wa.Y - dy, wa.Width + dx * 2, wa.Height + dy * 2);
        }

        // ---------- genio desde MacDock (dock, barra de menus, Finder) ----------
        public static bool GenieMinimize(IntPtr h)
        {
            if (DockTarget == null || N.IsIconic(h) || animating.Contains(h)) return false;
            var wp = Placement(h);
            if (wp.length == 0) return false;
            var win = WinRect(h);
            if (win.Width < 60 || win.Height < 60) return false;
            bool zoomed = N.IsZoomed(h);
            Ignore(h);
            // la ventana se aparta en el mismo refresco en que aparece su imagen, y se minimiza de verdad al terminar
            return Run(h, Strips(win), 0.5, 0, GenieFn(win, DockTarget(h), false), () => Park(h), () =>
            {
                var w2 = wp; w2.showCmd = 6;   // SW_MINIMIZE, recuperando su posicion original
                if (zoomed) w2.flags |= 2;     // al restaurarla vuelve maximizada
                if (!SetWindowPlacement(h, ref w2)) N.ShowWindow(h, 6);
            });
        }

        public static bool GenieRestore(IntPtr h)
        {
            if (DockTarget == null || !N.IsIconic(h) || animating.Contains(h)) return false;
            var wp = Placement(h);
            if (wp.length == 0) return false;
            var icon = DockTarget(h);
            Ignore(h);
            var img = Probe(h);
            if (img.Width >= 100 && img.Height >= 100)
            {
                // Windows conserva la imagen de la ventana minimizada: el genio sale del icono con ella
                // y la ventana real se restaura justo cuando las franjas cubren su lugar
                var final = RestoredRect(wp, img);
                if (Run(h, Strips(final), 0.44, 0, GenieFn(final, icon, true), null, () => { N.ShowWindow(h, 9); N.SetForegroundWindow(h); })) return true;
            }
            // si no la conserva: se restaura fuera de la vista y se anima su imagen en vivo
            N.ShowWindow(h, 4);
            var wr = WinRect(h);
            Park(h);
            if (!Run(h, Strips(wr), 0.44, 0.03, GenieFn(wr, icon, true), null, () => { MoveTo(h, wr.Location); N.SetForegroundWindow(h); }))
            { MoveTo(h, wr.Location); N.SetForegroundWindow(h); }
            return true;
        }

        // ---------- acomodar / mover y redimensionar (la app se redimensiona una sola vez, fuera de la vista) ----------
        public static void MoveResize(IntPtr h, Rectangle frame)
        {
            var to = WindowRectFor(h, frame);
            var from = WinRect(h);
            if (N.IsIconic(h) || animating.Contains(h) || SystemInformation.TerminalServerSession || from.Width < 40)
            { N.SetWindowPos(h, IntPtr.Zero, to.X, to.Y, to.Width, to.Height, 0x0004 | 0x0010); return; }
            var vs = SystemInformation.VirtualScreen;
            bool ok = Run(h, 1, 0.3, 0, Lerp(from, to, OutQuart, 255, 255),
                // fuera de la vista ya con su tamano final: tiene toda la animacion para repintarse
                () => N.SetWindowPos(h, IntPtr.Zero, vs.Left - to.Width - 200, vs.Top, to.Width, to.Height, 0x0004 | 0x0010 | 0x0200),
                () => MoveTo(h, to.Location));
            if (!ok) N.SetWindowPos(h, IntPtr.Zero, to.X, to.Y, to.Width, to.Height, 0x0004 | 0x0010);
        }

        // ---------- minimizar, maximizar y restaurar con los botones de Windows ----------
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
        static WinEventProc proc; static IntPtr hookMin, hookLoc, hookFg, hookMove, sizing;

        class WinState { public bool Zoomed; public Rectangle Normal, Max; }
        static readonly Dictionary<IntPtr, WinState> known = new Dictionary<IntPtr, WinState>();
        static readonly Dictionary<IntPtr, DateTime> skip = new Dictionary<IntPtr, DateTime>();

        public static void Ignore(IntPtr h) { skip[h] = DateTime.Now; }   // MacDock ya anima esta ventana
        static bool Skipped(IntPtr h) { DateTime t; return skip.TryGetValue(h, out t) && (DateTime.Now - t).TotalSeconds < 1.2; }

        public static void Install()
        {
            StageHandle();
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
            bool z = N.IsZoomed(h); var r = WinRect(h);
            s = new WinState { Zoomed = z };
            if (z) s.Max = r; else s.Normal = r;
            known[h] = s;
            return null;   // recien visto: no hay estado anterior con que comparar
        }

        // despues de animar, el estado guardado vuelve a coincidir con el real (asi no se anima dos veces)
        static void Resync(IntPtr h)
        {
            WinState s;
            if (!known.TryGetValue(h, out s) || !N.IsWindow(h) || N.IsIconic(h)) return;
            s.Zoomed = N.IsZoomed(h); var r = WinRect(h);
            if (s.Zoomed) s.Max = r; else if (SystemInformation.VirtualScreen.IntersectsWith(r)) s.Normal = r;
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
            Run(h, 1, z ? 0.3 : 0.27, 0, Lerp(old, r, OutQuart, 255, 255), () => Park(h), () => MoveTo(h, r.Location));
        }

        // minimizar con el boton de Windows (o Win+abajo): genio con la imagen que Windows guarda de la ventana
        static void OnMinimize(IntPtr h)
        {
            if (Skipped(h) || DockTarget == null || animating.Contains(h)) return;
            int st = N.GetWindowLong(h, -16);
            if ((st & 0x00C00000) != 0x00C00000 && (st & 0x00040000) == 0) return;
            if (N.GetWindowLong(h, -8) != 0 || (N.GetWindowLong(h, -20) & 0x80) != 0) return;
            var wp = Placement(h);
            if (wp.length == 0) return;
            var img = Probe(h);
            if (img.Width < 100 || img.Height < 100) return;
            var win = RestoredRect(wp, img);
            if (win.Width < 60 || win.Height < 60) return;
            Run(h, Strips(win), 0.5, 0, GenieFn(win, DockTarget(h), false), null, null);
        }
    }
}
