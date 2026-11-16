using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDock
{
    // Botones de Mac (rojo/amarillo/verde) sobre TODAS las ventanas abiertas, como en macOS:
    // a color en la ventana activa y en gris en las demas. Cada capa queda "pegada" a su ventana
    // (Windows la mantiene justo encima de ella y la oculta si se minimiza), asi nunca flota sobre otras apps.
    // No modifica ni inyecta nada en las apps.
    class MacButtons : Form
    {
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, int flags);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

        WinEventProc proc; IntPtr hookSys, hookObj, hookLoc, hookName; uint hookThreadId;
        public static bool MacTitles = true;   // barra de titulo estilo Mac (puntos a la izquierda, titulo centrado)
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, int msg, IntPtr w, IntPtr l);
        readonly HashSet<IntPtr> tracked = new HashSet<IntPtr>();      // ventanas con botones (para filtrar rapido en el hilo de eventos)
        readonly HashSet<IntPtr> pendingMove = new HashSet<IntPtr>();  // movimientos agrupados
        bool moveScheduled, closing;

        // Corre en el hilo de eventos: descarta casi todo sin tocar el hilo principal.
        void OnEventBg(IntPtr hk, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            if (obj != 0 || child != 0 || hwnd == IntPtr.Zero || closing) return;
            if (ev == 0x800B)
            {
                lock (tracked) { if (!tracked.Contains(hwnd)) return; }
                lock (pendingMove) { pendingMove.Add(hwnd); if (moveScheduled) return; moveScheduled = true; }
                try { BeginInvoke(new Action(FlushMoves)); } catch { }
                return;
            }
            if (ev == 0x800C)
            {
                lock (tracked) { if (!tracked.Contains(hwnd)) return; }
                try { BeginInvoke(new Action(() => { ButtonOverlay o2; if (overlays.TryGetValue(hwnd, out o2)) o2.TitleChanged(); })); } catch { }
                return;
            }
            if (ev != 0x0003 && ev != 0x000A && ev != 0x000B && ev != 0x0017 && ev != 0x8000 && ev != 0x8001 && ev != 0x8002 && ev != 0x8003) return;
            if ((ev == 0x000A || ev == 0x000B || ev == 0x0017 || ev == 0x8001 || ev == 0x8003)) { lock (tracked) { if (!tracked.Contains(hwnd)) return; } }
            try { BeginInvoke(new Action(() => OnEvent(hk, ev, hwnd, obj, child, thread, time))); } catch { }
        }

        void FlushMoves()
        {
            List<IntPtr> list; lock (pendingMove) { list = pendingMove.ToList(); pendingMove.Clear(); moveScheduled = false; }
            if (paused) return;
            ButtonOverlay ov;
            foreach (var h in list) if (overlays.TryGetValue(h, out ov)) ov.Place(h == active);
        }
        readonly Dictionary<IntPtr, ButtonOverlay> overlays = new Dictionary<IntPtr, ButtonOverlay>();
        IntPtr active = IntPtr.Zero;
        Timer cleanup = new Timer { Interval = 2000 };
        public static readonly uint MyPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        public static MacButtons Instance;
        public ButtonOverlay OverlayFor(IntPtr h) { ButtonOverlay o; return overlays.TryGetValue(h, out o) && !o.IsDisposed ? o : null; }
        public bool IsActive(IntPtr h) { return h == active; }

        public MacButtons(bool lightTheme)
        {
            Instance = this;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-200, -200, 1, 1);
            cleanup.Tick += (o, e) => Cleanup();
        }

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x8000000; return cp; } }
        protected override bool ShowWithoutActivation { get { return true; } }
        public void SetTheme(bool lightTheme) { }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            N.ShowWindow(Handle, 0);
            proc = OnEventBg;
            var th = new System.Threading.Thread(() =>
            {
                hookThreadId = GetCurrentThreadId();
                hookSys = SetWinEventHook(0x0003, 0x0017, IntPtr.Zero, proc, 0, 0, 0);
                hookObj = SetWinEventHook(0x8000, 0x8003, IntPtr.Zero, proc, 0, 0, 0);
                hookLoc = SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, proc, 0, 0, 0);
                hookName = SetWinEventHook(0x800C, 0x800C, IntPtr.Zero, proc, 0, 0, 0);
                Application.Run();
                UnhookWinEvent(hookSys); UnhookWinEvent(hookObj); UnhookWinEvent(hookLoc); UnhookWinEvent(hookName);
            });
            th.IsBackground = true; th.SetApartmentState(System.Threading.ApartmentState.STA); th.Start();
            active = N.GetForegroundWindow();
            var all = new List<IntPtr>();
            N.EnumWindows((h, l) => { all.Add(h); return true; }, IntPtr.Zero);
            foreach (var h in all) Ensure(h);
            cleanup.Start();
        }

        // Con un juego o video a pantalla completa no se muestra ningun boton encima.
        bool paused;
        public void SetPaused(bool p)
        {
            if (paused == p) return;
            paused = p;
            Diag(N.GetForegroundWindow(), p ? "PAUSA: se detecto pantalla completa, botones ocultos" : "fin de la pausa");
            foreach (var kv in overlays.ToList()) { if (p) kv.Value.Hide2(); else kv.Value.Place(kv.Key == active); }
        }

        void OnEvent(IntPtr hk, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            if (obj != 0 || child != 0 || hwnd == IntPtr.Zero || paused) return;
            try
            {
                ButtonOverlay ov;
                switch (ev)
                {
                    case 0x0003:
                        var old = active; active = hwnd;
                        if (overlays.TryGetValue(old, out ov)) ov.Place(false);
                        if (!overlays.ContainsKey(hwnd)) Ensure(hwnd);
                        if (overlays.TryGetValue(hwnd, out ov)) ov.Place(true);
                        break;
                    case 0x000A: if (overlays.TryGetValue(hwnd, out ov) && !ov.MacTitle) ov.Hide2(); break;
                    case 0x000B: case 0x0017: if (overlays.TryGetValue(hwnd, out ov)) ov.Place(hwnd == active); break;
                    case 0x8002: case 0x8000: Ensure(hwnd); break;
                    case 0x8001: case 0x8003: Remove(hwnd); break;

                }
            }
            catch { }
        }

        void Ensure(IntPtr h)
        {
            if (overlays.ContainsKey(h)) return;
            string why = WhyNot(h);
            if (why != null) { if (why != "invisible" && why != "no es ventana principal" && N.TitleOf(h) != "") Diag(h, "SIN botones: " + why); return; }
            Diag(h, "con botones");
            var ov = new ButtonOverlay(h);
            overlays[h] = ov; lock (tracked) tracked.Add(h);
            ov.Show();
            ov.Place(h == active);
        }

        void Remove(IntPtr h)
        {
            ButtonOverlay ov;
            if (!overlays.TryGetValue(h, out ov)) return;
            overlays.Remove(h); lock (tracked) tracked.Remove(h);
            try { if (!ov.IsDisposed) { ov.Close(); ov.Dispose(); } } catch { }
        }

        void Cleanup()
        {
            foreach (var h in overlays.Keys.ToList())
                if (!IsWindow(h) || !N.IsWindowVisible(h) || overlays[h].IsDisposed) Remove(h);
        }

        public static bool Eligible(IntPtr h) { return WhyNot(h) == null; }

        // null = la ventana lleva botones de Mac; si no, el motivo (para el registro de diagnostico)
        public static string WhyNot(IntPtr h)
        {
            if (h == IntPtr.Zero || !N.IsWindowVisible(h)) return "invisible";
            if (GetAncestor(h, 2) != h) return "no es ventana principal";
            uint pid; N.GetWindowThreadProcessId(h, out pid); if (pid == MyPid) return "es de MacDock";
            int st = N.GetWindowLong(h, -16), ex = N.GetWindowLong(h, -20);
            if ((st & 0x20000) == 0 && (st & 0x10000) == 0) return "sin minimizar ni maximizar";
            if ((st & 0x40000) == 0 && (st & 0x00C00000) != 0x00C00000) return "sin borde ni barra de titulo";
            if ((ex & 0x80) != 0) return "ventana de herramientas";
            int cloaked; if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return "oculta por Windows (cloaked)";
            string c = N.ClassOf(h);
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Windows.UI.Core.CoreWindow" || c == "XamlExplorerHostIslandWindow" || c == "#32770") return "ventana del sistema";
            return null;
        }

        // ---- registro de diagnostico: %LOCALAPPDATA%\MacDock\botones.log ----
        public static string LogFile { get { return System.IO.Path.Combine(Program.DataDir, "botones.log"); } }
        static int logLines; static readonly HashSet<string> logged = new HashSet<string>();
        public static void Diag(IntPtr h, string what)
        {
            try
            {
                if (logLines > 600) return;
                string key = h + "|" + what; lock (logged) { if (!logged.Add(key)) return; }
                string line = DateTime.Now.ToString("HH:mm:ss") + "  [" + N.ClassOf(h) + "] \"" + Short(N.TitleOf(h)) + "\"  " + what;
                if (logLines++ == 0) System.IO.File.WriteAllText(LogFile, "MacDock - diagnostico de botones rojo/amarillo/verde (" + DateTime.Now + ")\r\n");
                System.IO.File.AppendAllText(LogFile, line + "\r\n");
            }
            catch { }
        }
        static string Short(string t) { return t.Length > 50 ? t.Substring(0, 47) + "..." : t; }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true; try { if (hookThreadId != 0) PostThreadMessage(hookThreadId, 0x12, IntPtr.Zero, IntPtr.Zero); } catch { }
            cleanup.Stop();
            foreach (var h in overlays.Keys.ToList()) Remove(h);
            base.OnFormClosing(e);
        }
    }

    // La capa de una ventana: tapa sus botones de Windows con los de Mac.
    class ButtonOverlay : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out N.RECT r, int size);
        [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out N.POINT p);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);

        readonly IntPtr target;
        Rectangle area; Color bgColor = Color.Black; bool isActive, inside, drawn, drawnActive; float s = 1f;
        Timer hoverTimer = new Timer { Interval = 40 };
        int pressedIdx = -1; DateTime greenSince = DateTime.MaxValue;
        public bool MacTitle; int macChecked; string lastTitle = "";
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l, int flags, int timeout, out IntPtr res);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();

        static IntPtr MakeLParam(int x, int y) { return new IntPtr((y << 16) | (x & 0xFFFF)); }

        int HitTest(int x, int y)
        {
            IntPtr r;
            if (SendMessageTimeout(target, 0x0084, IntPtr.Zero, MakeLParam(x, y), 0x0002, 120, out r) == IntPtr.Zero) return -1;
            return r.ToInt32();
        }

        // ventana con la barra de titulo normal de Windows: icono (menu de sistema) a la izquierda y titulo arrastrable.
        // Las apps con barra propia (Chrome, Explorador con pestanas, VS Code...) no pasan esta prueba y conservan los botones a la derecha.
        bool StandardCaption(N.RECT fr, int capTop, int capH, int buttonsLeft, float dpi)
        {
            int cy = capTop + capH / 2;
            return HitTest(fr.left + (int)(16 * dpi), cy) == 3 && HitTest(buttonsLeft - (int)(40 * dpi), cy) == 2;
        }

        public void TitleChanged() { if (MacTitle && N.TitleOf(target) != lastTitle) Render(); }
        public static TilePopover Popover;

        public ButtonOverlay(IntPtr target)
        {
            this.target = target;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-200, -200, 1, 1);
            hoverTimer.Tick += (o, e) =>
            {
                N.POINT p; GetCursorPos(out p);
                if (!area.Contains(p.x, p.y) || !OverDots(new PointF(p.x - area.Left, p.y - area.Top))) { inside = false; greenSince = DateTime.MaxValue; hoverTimer.Stop(); Render(); return; }
                // como en macOS: al dejar el mouse sobre el boton verde aparece el panel para acomodar la ventana
                var org = Origin();
                bool overGreen = TrafficLights.Hit(new PointF(p.x - area.Left, p.y - area.Top), org.X, org.Y, s) == 2;
                if (!overGreen || pressedIdx >= 0) { greenSince = DateTime.MaxValue; return; }
                if (greenSince == DateTime.MaxValue) greenSince = DateTime.Now;
                else if ((DateTime.Now - greenSince).TotalMilliseconds > 550 && (Popover == null || Popover.IsDisposed || Popover.Target != target))
                {
                    greenSince = DateTime.MaxValue;
                    if (Popover != null && !Popover.IsDisposed) Popover.Close();
                    float gx = area.Left + org.X + TrafficLights.D(s) / 2 + TrafficLights.Step(s) * 2;
                    Popover = new TilePopover(target, new Point((int)gx, (int)(area.Top + org.Y + TrafficLights.D(s) + 6 * s)), s, bgColor.GetBrightness() > 0.55f, area);
                    Popover.Show();
                }
            };
        }

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x8000000; return cp; } }
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowLongPtr(Handle, -8, target);   // pegada a su ventana
        }

        public void Hide2() { if (IsHandleCreated && !IsDisposed) N.ShowWindow(Handle, 0); }

        public void Place(bool activeWin)
        {
            if (IsDisposed) return;
            try
            {
                IntPtr h = target;
                if (N.IsIconic(h) || Wobbly.IsWobbling(h)) { Hide2(); return; }
                string why = MacButtons.WhyNot(h);
                if (why != null) { MacButtons.Diag(h, "oculto: " + why); Hide2(); return; }
                isActive = activeWin;
                N.RECT fr; if (DwmGetWindowAttribute(h, 9, out fr, 16) != 0) N.GetWindowRect(h, out fr);
                var scr = Screen.FromHandle(h);
                if (!N.IsZoomed(h) && fr.left <= scr.Bounds.Left && fr.top <= scr.Bounds.Top && fr.right >= scr.Bounds.Right && fr.bottom >= scr.Bounds.Bottom && (N.GetWindowLong(h, -16) & 0x00C00000) != 0x00C00000) { MacButtons.Diag(h, "oculto: parece pantalla completa"); Hide2(); return; }
                float dpi = 1f; try { dpi = N.GetDpiForWindow(h) / 96f; } catch { }
                s = dpi;
                N.RECT cb = new N.RECT(), wr; N.GetWindowRect(h, out wr);
                Rectangle a;
                // Chrome, Brave, Edge y apps Electron (Claude, Discord, VS Code) dibujan sus propios botones, mas altos:
                // ahi se usa su geometria real y no la que reporta Windows
                bool chromeLike = N.ClassOf(h).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal);
                bool haveCb = !chromeLike && DwmGetWindowAttribute(h, 5, out cb, 16) == 0 && cb.right - cb.left > 30 * dpi;
                if (haveCb && MacButtons.MacTitles)
                {
                    int capTop = Math.Max(fr.top, wr.top + cb.top), capH = Math.Max(20, cb.bottom - cb.top);
                    if (macChecked < 3 && !MacTitle) { MacTitle = StandardCaption(fr, capTop, capH, wr.left + cb.left, dpi); macChecked++; }
                    if (MacTitle) a = new Rectangle(fr.left + 1, capTop, fr.right - fr.left - 2, capH);
                    else a = new Rectangle(wr.left + cb.left, capTop, cb.right - cb.left, capH);
                }
                else if (haveCb)
                    a = new Rectangle(wr.left + cb.left, Math.Max(fr.top, wr.top + cb.top), cb.right - cb.left, Math.Max(20, cb.bottom - cb.top));
                else
                {
                    string wc = N.ClassOf(h); bool tall = wc == "WinUIDesktopWin32WindowClass" || wc == "ApplicationFrameWindow";
                    int w = (int)(138 * dpi), hh = (int)((tall ? 46 : chromeLike ? 37 : 31) * dpi);
                    a = new Rectangle(fr.right - w, fr.top + 1, w - 1, hh);
                }
                if (a.Width < 40 || a.Height < 14 || fr.right - fr.left < 300) { MacButtons.Diag(h, "oculto: ventana o zona de botones muy pequena (" + a + ")"); Hide2(); return; }
                MacButtons.Diag(h, (MacTitle ? "barra Mac" : chromeLike ? "botones sobre app tipo Chrome" : "botones a la derecha") + " (" + a.Width + "x" + a.Height + ")");
                // Solo se movio: mover la capa sin redibujar ni leer la pantalla (lo que causaba el lag)
                if (drawn && a.Size == area.Size && activeWin == drawnActive && N.IsWindowVisible(Handle))
                {
                    if (a.Location != area.Location) { area = a; N.SetWindowPos(Handle, IntPtr.Zero, a.Left, a.Top, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200); }
                    return;
                }
                area = a; drawn = true; drawnActive = activeWin;
                IntPtr dc = N.GetDC(IntPtr.Zero);
                int sx = MacTitle ? a.Right - (int)(150 * dpi) : a.Left - (int)(6 * dpi);
                uint px = GetPixel(dc, sx, a.Top + Math.Max(1, (int)(2 * dpi)));
                N.ReleaseDC(IntPtr.Zero, dc);
                if (px != 0xFFFFFFFF) bgColor = Color.FromArgb(255, (int)(px & 0xFF), (int)((px >> 8) & 0xFF), (int)((px >> 16) & 0xFF));
                Render();
                N.ShowWindow(Handle, 4);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        PointF Origin()
        {
            float y = (area.Height - TrafficLights.D(s)) / 2f;
            return MacTitle ? new PointF(12 * s, y) : new PointF((area.Width - TrafficLights.Width(s)) / 2f, y);
        }

        void Render()
        {
            if (area.Width <= 0 || IsDisposed) return;
            using (var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp)) DrawContent(g);
                IntPtr screenDc = N.GetDC(IntPtr.Zero), memDc = N.CreateCompatibleDC(screenDc), hb = bmp.GetHbitmap(Color.FromArgb(0)), old = N.SelectObject(memDc, hb);
                try
                {
                    var size = new N.SIZE { cx = area.Width, cy = area.Height }; var src = new N.POINT(); var dst = new N.POINT { x = area.Left, y = area.Top };
                    var bl = new N.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                    N.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref bl, 2);
                }
                finally { N.ReleaseDC(IntPtr.Zero, screenDc); N.SelectObject(memDc, old); N.DeleteObject(hb); N.DeleteDC(memDc); }
            }
        }

        public Rectangle Area { get { return area; } }
        public bool Shown { get { return IsHandleCreated && !IsDisposed && N.IsWindowVisible(Handle); } }

        // pinta la capa (fondo, puntos y titulo) en otro lienzo: lo usa la ondulacion para que no aparezcan los botones de Windows
        public void PaintInto(Graphics g, float ox, float oy, float k)
        {
            var st = g.Save();
            g.TranslateTransform(ox, oy); g.ScaleTransform(k, k);
            g.SetClip(new RectangleF(0, 0, area.Width, area.Height));
            DrawContent(g);
            g.Restore(st);
        }

        void DrawContent(Graphics g)
        {
            {
                {
                    using (var bb = new SolidBrush(bgColor)) g.FillRectangle(bb, 0, 0, area.Width, area.Height);
                    var o = Origin();
                    bool lightBg = bgColor.GetBrightness() > 0.55f;
                    TrafficLights.Draw(g, o.X, o.Y, s, isActive, inside, lightBg, pressedIdx);
                    if (MacTitle)
                    {
                        // titulo centrado, en negrita suave y atenuado si la ventana no esta activa (como macOS)
                        lastTitle = N.TitleOf(target);
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                        Color tc = lightBg ? Color.FromArgb(isActive ? 230 : 120, 30, 30, 32) : Color.FromArgb(isActive ? 235 : 120, 236, 236, 240);
                        float side = o.X + TrafficLights.Width(s) + 16 * s;
                        var tr = new RectangleF(side, 0, Math.Max(10, area.Width - side * 2), area.Height);
                        using (var f = new Font(UiFont.Semi, 13f * s, FontStyle.Regular, GraphicsUnit.Pixel))
                        using (var b = new SolidBrush(tc))
                        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                            g.DrawString(lastTitle, f, b, tr, sf);
                    }
                }
            }
        }

        bool OverDots(PointF local)
        {
            if (!MacTitle) return true;
            var o = Origin();
            return local.X >= o.X - 5 * s && local.X <= o.X + TrafficLights.Width(s) + 5 * s && local.Y >= 0 && local.Y <= area.Height;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = OverDots(e.Location);
            if (over && !inside) { inside = true; Render(); hoverTimer.Start(); }
            else if (!over && inside && MacTitle) { inside = false; greenSince = DateTime.MaxValue; Render(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var o = Origin();
            pressedIdx = TrafficLights.Hit(e.Location, o.X, o.Y, s);
            if (pressedIdx >= 0) { if (Popover != null && !Popover.IsDisposed) Popover.Close(); Render(); return; }
            if (!MacTitle) return;
            if (e.Clicks >= 2)
            {
                // doble clic en la barra de titulo: zoom, como en macOS
                N.PostMessage(target, 0x0112, new IntPtr(N.IsZoomed(target) ? 0xF120 : 0xF030), IntPtr.Zero);
                return;
            }
            // el resto de la barra arrastra la ventana real, como en macOS: 1:1 y al ritmo de la pantalla
            N.SetForegroundWindow(target);
            WindowDrag.Start(this, target, Handle);
        }

        // Ventanas visibles de la misma app (para Option+clic)
        List<IntPtr> SameApp()
        {
            uint pid; N.GetWindowThreadProcessId(target, out pid);
            var list = new List<IntPtr>();
            N.EnumWindows((w, l) => { uint p2; N.GetWindowThreadProcessId(w, out p2); if (p2 == pid && MacButtons.Eligible(w)) list.Add(w); return true; }, IntPtr.Zero);
            return list;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right && MacTitle)
            {
                var sp = PointToScreen(e.Location);
                N.PostMessage(target, 0x0313, IntPtr.Zero, MakeLParam(sp.X, sp.Y));   // menu de la ventana
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            var o = Origin();
            int i = TrafficLights.Hit(e.Location, o.X, o.Y, s), was = pressedIdx;
            pressedIdx = -1; Render();
            if (i < 0 || i != was) return;   // como en macOS: soltar fuera del boton cancela
            IntPtr h = target;
            bool option = (Control.ModifierKeys & Keys.Alt) != 0;   // Alt hace de tecla Option
            if (i == 0)
            {
                if (option) foreach (var w in SameApp()) N.PostMessage(w, 0x0112, new IntPtr(0xF060), IntPtr.Zero);   // cerrar todas las ventanas de la app
                else N.PostMessage(h, 0x0112, new IntPtr(0xF060), IntPtr.Zero);
            }
            else if (i == 1)
            {
                if (option) foreach (var w in SameApp()) N.PostMessage(w, 0x0112, new IntPtr(0xF020), IntPtr.Zero);   // minimizar todas
                else Genie.Minimize(h);   // efecto genio hacia el dock
            }
            else if (option) MenuBar.TileWindow(h, 10);   // Option+verde: llenar la pantalla sin pantalla completa (zoom)
            else
            {
                N.RECT fr; if (DwmGetWindowAttribute(h, 9, out fr, 16) != 0) N.GetWindowRect(h, out fr);
                var wa = Screen.FromHandle(h).WorkingArea;
                bool max = N.IsZoomed(h) || (fr.left <= wa.Left + 2 && fr.top <= wa.Top + 2 && fr.right >= wa.Right - 2 && fr.bottom >= wa.Bottom - 2);
                N.PostMessage(h, 0x0112, new IntPtr(max ? 0xF120 : 0xF030), IntPtr.Zero);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { hoverTimer.Stop(); base.OnFormClosing(e); }
    }

    // Panel que aparece al dejar el mouse sobre el boton verde (macOS Sequoia y posteriores):
    // "Mover y redimensionar" (mitades; con Alt, cuartos), "Llenar y organizar" (llenar, centrar) y pantalla completa.
    class TilePopover : Form
    {
        public readonly IntPtr Target;
        readonly Point anchor; readonly float s; readonly bool light; readonly Rectangle buttonArea;
        LayeredCanvas canvas; VsyncTimer anim; System.Diagnostics.Stopwatch clk = System.Diagnostics.Stopwatch.StartNew();
        Timer watch = new Timer { Interval = 50 };
        DateTime awaySince = DateTime.MaxValue;
        float appear; bool closingAnim; int hover = -1, W, H;
        readonly List<KeyValuePair<RectangleF, int>> cells = new List<KeyValuePair<RectangleF, int>>();
        RectangleF rFull;

        public TilePopover(IntPtr target, Point anchor, float s, bool light, Rectangle buttonArea)
        {
            Target = target; this.anchor = anchor; this.s = s; this.light = light; this.buttonArea = buttonArea;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            W = (int)(244 * s); H = (int)(176 * s);
            var wa = Screen.FromPoint(anchor).WorkingArea;
            int x = Math.Max(wa.Left + 4, Math.Min(wa.Right - W - 4, anchor.X - (int)(30 * s))), y = Math.Min(wa.Bottom - H - 4, anchor.Y);
            Bounds = new Rectangle(x, y, W, H);
            anim = new VsyncTimer(this); anim.Tick += (o, e) => Frame();
            watch.Tick += (o, e) => Watch();
        }

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x8000000 | 0x8; return cp; } }
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnShown(EventArgs e) { base.OnShown(e); clk.Restart(); anim.Start(); watch.Start(); }

        void Frame()
        {
            double p = clk.Elapsed.TotalSeconds / (closingAnim ? 0.1 : 0.16);
            appear = closingAnim ? 1f - Ease.OutCubic(p) : Ease.OutCubic(p);
            Draw();
            if (p >= 1) { anim.Stop(); if (closingAnim) Close(); }
        }

        void Watch()
        {
            var c = Cursor.Position;
            if (Bounds.Contains(c) || buttonArea.Contains(c)) { awaySince = DateTime.MaxValue; int hv = HitAt(new PointF(c.X - Left, c.Y - Top)); if (hv != hover) { hover = hv; Draw(); } return; }
            if (hover != -1) { hover = -1; Draw(); }
            if (awaySince == DateTime.MaxValue) awaySince = DateTime.Now;
            else if ((DateTime.Now - awaySince).TotalMilliseconds > 320) FadeAway();
        }

        void FadeAway() { if (closingAnim) return; closingAnim = true; watch.Stop(); clk.Restart(); anim.Start(); }

        int HitAt(PointF p)
        {
            foreach (var kv in cells) if (kv.Key.Contains(p)) return kv.Value;
            if (rFull.Contains(p)) return 100;
            return -1;
        }

        void Draw()
        {
            if (IsDisposed) return;
            if (canvas == null) canvas = new LayeredCanvas(W, H);
            bool alt = (Control.ModifierKeys & Keys.Alt) != 0;
            Color bg = light ? Color.FromArgb(246, 248, 248, 250) : Color.FromArgb(244, 38, 38, 42);
            Color fg = light ? Color.FromArgb(30, 30, 32) : Color.FromArgb(238, 238, 242);
            Color dim = light ? Color.FromArgb(130, 130, 136) : Color.FromArgb(150, 150, 158);
            Color accent = Color.FromArgb(10, 122, 255);
            cells.Clear();
            using (var g = Graphics.FromImage(canvas.Bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                float k = 0.96f + 0.04f * appear;   // aparece creciendo un poco desde el boton
                g.TranslateTransform(30 * s, 0); g.ScaleTransform(k, k); g.TranslateTransform(-30 * s, 0);
                int a = (int)(255 * appear);
                var panel = new RectangleF(6 * s, 4 * s, W - 12 * s, H - 12 * s);
                for (int i = 4; i >= 1; i--)
                    using (var sp = Rr(new RectangleF(panel.X - i * s, panel.Y - i * s + 2 * s, panel.Width + 2 * i * s, panel.Height + 2 * i * s), (12 + i) * s))
                    using (var b = new SolidBrush(Color.FromArgb(a * 9 / 255, 0, 0, 0))) g.FillPath(b, sp);
                using (var pp = Rr(panel, 12 * s))
                {
                    using (var b = new SolidBrush(Color.FromArgb(a * bg.A / 255, bg))) g.FillPath(b, pp);
                    using (var pen = new Pen(Color.FromArgb(a * (light ? 40 : 70) / 255, light ? Color.Black : Color.White), Math.Max(1f, 0.8f * s))) g.DrawPath(pen, pp);
                }
                using (var fT = new Font(UiFont.Semi, 11f * s, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var fR = new Font(UiFont.Reg, 13f * s, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var dimB = new SolidBrush(Color.FromArgb(a, dim))) using (var fgB = new SolidBrush(Color.FromArgb(a, fg)))
                {
                    float x0 = panel.X + 12 * s, y = panel.Y + 10 * s, cw = 42 * s, ch = 28 * s, gap = 10 * s;
                    g.DrawString("Mover y redimensionar", fT, dimB, x0, y); y += 18 * s;
                    int[] row1 = alt ? new[] { 5, 6, 7, 8 } : new[] { 1, 2, 3, 4 };
                    for (int i = 0; i < 4; i++) Cell(g, new RectangleF(x0 + i * (cw + gap), y, cw, ch), row1[i], a, fg, accent);
                    y += ch + 14 * s;
                    g.DrawString("Llenar y organizar", fT, dimB, x0, y); y += 18 * s;
                    Cell(g, new RectangleF(x0, y, cw, ch), 10, a, fg, accent);
                    Cell(g, new RectangleF(x0 + cw + gap, y, cw, ch), 9, a, fg, accent);
                    y += ch + 10 * s;
                    using (var pen = new Pen(Color.FromArgb(a * 40 / 255, fg))) g.DrawLine(pen, panel.X + 10 * s, y, panel.Right - 10 * s, y);
                    y += 4 * s;
                    rFull = new RectangleF(panel.X + 6 * s, y, panel.Width - 12 * s, 22 * s);
                    if (hover == 100) using (var hp = Rr(rFull, 6 * s)) using (var b = new SolidBrush(Color.FromArgb(a, accent))) g.FillPath(b, hp);
                    g.DrawString(N.IsZoomed(Target) ? "Salir de pantalla completa" : "Pantalla completa", fR, hover == 100 ? new SolidBrush(Color.FromArgb(a, 255, 255, 255)) : fgB, rFull.X + 8 * s, rFull.Y + 3 * s);
                }
            }
            canvas.Push(Handle, Left, Top);
        }

        // miniatura de pantalla con la zona destino resaltada
        void Cell(Graphics g, RectangleF r, int mode, int a, Color fg, Color accent)
        {
            cells.Add(new KeyValuePair<RectangleF, int>(r, mode));
            bool hv = hover == mode;
            using (var p = Rr(r, 5 * s))
            {
                using (var b = new SolidBrush(Color.FromArgb(a * (hv ? 40 : 18) / 255, hv ? accent : fg))) g.FillPath(b, p);
                using (var pen = new Pen(Color.FromArgb(a * (hv ? 255 : 90) / 255, hv ? accent : fg), Math.Max(1f, 1.1f * s))) g.DrawPath(pen, p);
            }
            float ins = 3 * s; var z = new RectangleF(r.X + ins, r.Y + ins, r.Width - ins * 2, r.Height - ins * 2);
            float hw = (z.Width - 2 * s) / 2, hh = (z.Height - 2 * s) / 2;
            RectangleF f;
            switch (mode)
            {
                case 1: f = new RectangleF(z.X, z.Y, hw, z.Height); break;
                case 2: f = new RectangleF(z.Right - hw, z.Y, hw, z.Height); break;
                case 3: f = new RectangleF(z.X, z.Y, z.Width, hh); break;
                case 4: f = new RectangleF(z.X, z.Bottom - hh, z.Width, hh); break;
                case 5: f = new RectangleF(z.X, z.Y, hw, hh); break;
                case 6: f = new RectangleF(z.Right - hw, z.Y, hw, hh); break;
                case 7: f = new RectangleF(z.X, z.Bottom - hh, hw, hh); break;
                case 8: f = new RectangleF(z.Right - hw, z.Bottom - hh, hw, hh); break;
                case 9: f = new RectangleF(z.X + z.Width * 0.18f, z.Y + z.Height * 0.15f, z.Width * 0.64f, z.Height * 0.7f); break;
                default: f = z; break;
            }
            using (var fp = Rr(f, 2.5f * s)) using (var b = new SolidBrush(Color.FromArgb(a * (hv ? 255 : 150) / 255, hv ? accent : fg))) g.FillPath(b, fp);
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int hv = HitAt(e.Location); if (hv != hover) { hover = hv; Draw(); } }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int hv = HitAt(e.Location);
            if (hv < 0) return;
            if (hv == 100) N.PostMessage(Target, 0x0112, new IntPtr(N.IsZoomed(Target) ? 0xF120 : 0xF030), IntPtr.Zero);
            else MenuBar.TileWindow(Target, hv);
            FadeAway();
        }

        static System.Drawing.Drawing2D.GraphicsPath Rr(RectangleF r, float rad)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { watch.Stop(); watch.Dispose(); anim.Close(); if (canvas != null) { canvas.Dispose(); canvas = null; } }
            base.Dispose(disposing);
        }
    }
}
