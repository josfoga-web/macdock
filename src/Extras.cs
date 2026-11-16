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
                Motion.Ignore(h);
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
    // Al restaurarla desde el dock sale del icono con la animacion inversa. La animacion la hace Motion con la imagen
    // en vivo que guarda Windows de la ventana (sin capturas ni dibujo en el procesador).
    static class Genie
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out N.RECT r, int size);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct ANIMATIONINFO { public int cbSize, iMinAnimate; }
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] static extern bool SpiAnim(int a, int u, ref ANIMATIONINFO p, int f);

        public static bool Enabled = true;
        public static Func<IntPtr, Rectangle> TargetFor;   // lo conecta el dock: rectangulo del icono de la app en pantalla

        internal static Rectangle Frame(IntPtr h)
        {
            N.RECT r;
            if (DwmGetWindowAttribute(h, 9, out r, 16) != 0) N.GetWindowRect(h, out r);
            return Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
        }

        public static void Minimize(IntPtr h)
        {
            if (!Enabled || TargetFor == null || N.IsIconic(h)) { N.ShowWindow(h, 6); return; }
            try { if (!Motion.GenieMinimize(h)) N.ShowWindow(h, 6); }
            catch (Exception ex) { Program.Log(ex); N.ShowWindow(h, 6); }
        }

        // devuelve true si hizo la animacion (y la ventana se restaura al final)
        public static bool Restore(IntPtr h)
        {
            if (!Enabled || TargetFor == null) return false;
            try { return Motion.GenieRestore(h); }
            catch (Exception ex) { Program.Log(ex); return false; }
        }

        public static void RestoreAnimOnExit()
        {
            SetGlobal(false);
            Motion.Uninstall();
        }

        static bool globalOff; static int globalSaved = 1;

        // Mientras MacDock corre, las animaciones de minimizar y maximizar de Windows se apagan (sin guardarlo)
        // para que solo se vean las de Mac
        public static void SetGlobal(bool macOnly)
        {
            var ai = new ANIMATIONINFO { cbSize = 8 };
            if (macOnly && !globalOff) { SpiAnim(0x48, 8, ref ai, 0); globalSaved = ai.iMinAnimate; ai.iMinAnimate = 0; SpiAnim(0x49, 8, ref ai, 0); globalOff = true; }
            else if (!macOnly && globalOff) { ai.iMinAnimate = globalSaved; SpiAnim(0x49, 8, ref ai, 0); globalOff = false; }
        }

        public static void Install()
        {
            Motion.DockTarget = TargetFor;
            Motion.Enabled = Enabled;
            Motion.Install();
            SetGlobal(Enabled);
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
    // Ventanas "gelatina" al moverlas (como los wobbly windows de KDE/Compiz).
    // Diseno pensado para que no haya tirones ni parpadeos:
    //  - la captura se copia de la pantalla (no se le pide a la app que se dibuje, asi no se congela su arrastre)
    //  - MacDock toma el arrastre y la ventana real se aparta fuera de la vista (sin cambiarle la transparencia,
    //    que obligaba a Windows a recrearla); vuelve a su sitio al terminar, ya pintada
    //  - el dibujo va en un hilo propio al ritmo de la pantalla, con copia de pixeles exacta (sin rendijas)
    static class Wobbly
    {
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l, int flags, int timeout, out IntPtr result);

        public static bool Enabled = true;
        public static Func<Rectangle[]> AvoidRects;   // lo conecta el dock: zonas de MacDock que no deben salir en la captura
        static WinEventProc proc; static IntPtr hook;
        static IntPtr pending; static Rectangle pendingFrame; static bool pendingOwn; static Point pendingCursor;
        static Snapshot preShot;
        static bool cancelled;      // el arrastre de Windows ya se corto al presionar (antes de que la ventana se mueva)
        static WobbleForm warm;     // ventana de la gelatina creada al presionar, lista para el primer movimiento
        static VsyncTimer pendingTimer; static Control helper;
        static WobbleForm cur;

        public sealed class Snapshot { public int[] Px; public int W, H; }

        public static void Install()
        {
            if (hook != IntPtr.Zero) return;
            helper = new Control(); helper.CreateControl();
            proc = OnEvent;
            hook = SetWinEventHook(0x000A, 0x000B, IntPtr.Zero, proc, 0, 0, 0x0002);
            pendingTimer = new VsyncTimer(helper);   // revisa el primer movimiento en cada refresco (no cada 15 ms)
            pendingTimer.Tick += (o, e) => CheckPending();
        }

        public static void Uninstall()
        {
            if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; }
            if (pendingTimer != null) pendingTimer.Stop();
            if (cur != null) cur.Finish(true);
        }

        static void OnEvent(IntPtr hk, uint ev, IntPtr h, int obj, int child, uint thread, uint time)
        {
            if (obj != 0 || h == IntPtr.Zero) return;
            if (ev == 0x000A) BeginExternal(h);
            else if (!(h == pending && cancelled)) EndExternal(h);   // el fin que provoca el propio corte no cuenta
        }

        public static void BeginExternal(IntPtr h) { BeginExternal(h, false); }
        // own = ventana que MacDock arrastra por su cuenta (su Finder): no hace falta que tenga barra de Windows
        public static void BeginExternal(IntPtr h, bool own)
        {
            if (!Enabled || cur != null || N.IsZoomed(h) || N.IsIconic(h)) return;
            int st = N.GetWindowLong(h, -16);
            if (!own && (st & 0x00C00000) != 0x00C00000 && (st & 0x00040000) == 0) return;
            if ((N.GetWindowLong(h, -20) & 0x80000) != 0) return;   // ya es transparente por su cuenta: no se toca
            var c = Cursor.Position;
            // solo arrastres desde la barra de titulo (no redimensiones ni el teclado)
            if (!own && !OnCaption(h, c)) return;
            pending = h; pendingOwn = own; pendingFrame = Genie.Frame(h); pendingCursor = c;
            preShot = Capture(h, pendingFrame);   // antes de que se mueva, y sin bloquear a la app
            if (preShot == null) { pending = IntPtr.Zero; return; }
            // El arrastre de Windows se corta YA, con la ventana aun quieta: al cortarlo Windows la devuelve a donde
            // empezo, y si eso pasa con la gelatina en marcha la ventana real reaparece un instante (el parpadeo).
            cancelled = false;
            if (!own) { PostMessage(h, 0x001F, IntPtr.Zero, IntPtr.Zero); cancelled = true; }
            WobbleForm.Prepare(pendingFrame.Width, pendingFrame.Height);   // lienzo listo antes del primer movimiento
            try { warm = new WobbleForm(h, preShot, pendingFrame, pendingCursor); var wh = warm.Handle; } catch (Exception ex) { Program.Log(ex); warm = null; }
            pendingTimer.Start();
        }

        static bool OnCaption(IntPtr h, Point c)
        {
            IntPtr r;
            if (SendMessageTimeout(h, 0x0084, IntPtr.Zero, new IntPtr((c.Y << 16) | (c.X & 0xFFFF)), 0x0002, 40, out r) == IntPtr.Zero) return false;
            return r.ToInt64() == 2;   // HTCAPTION
        }

        static void Abort()
        {
            var h = pending; bool wasCut = cancelled;
            pending = IntPtr.Zero; pendingTimer.Stop(); preShot = null; cancelled = false;
            if (warm != null) { warm.Dispose(); warm = null; }
            if (wasCut && h != IntPtr.Zero) EdgeTiler.EndExternal(h);   // su fin de arrastre se ignoro: se avisa aqui
        }

        // la ventana esta en manos de la gelatina (o lo va a estar en cuanto se mueva el puntero)
        public static bool Owns(IntPtr h) { return IsWobbling(h) || (h == pending && cancelled); }

        public static void EndExternal(IntPtr h)
        {
            if (h == pending) Abort();
            // con el arrastre tomado, el soltar lo detecta MacDock (el fin de arrastre de Windows llega al tomarlo)
        }

        public static bool IsWobbling(IntPtr h) { return cur != null && cur.Target == h && !cur.IsDisposed; }

        static void CheckPending()
        {
            if (pending == IntPtr.Zero) { pendingTimer.Stop(); return; }
            int vk = SystemInformation.MouseButtonsSwapped ? 0x02 : 0x01;
            if ((GetAsyncKeyState(vk) & 0x8000) == 0) { Abort(); return; }   // fue solo un clic
            var f = Genie.Frame(pending);
            if (f.Size != pendingFrame.Size) { Abort(); return; }   // es una redimension
            var c = Cursor.Position;
            if (Math.Abs(c.X - pendingCursor.X) + Math.Abs(c.Y - pendingCursor.Y) < 3 && (cancelled || f.Location == pendingFrame.Location)) return;
            var h = pending; pending = IntPtr.Zero; pendingTimer.Stop();
            Start(h, pendingOwn);
        }

        static void Start(IntPtr h, bool own)
        {
            var shot = preShot; preShot = null; cancelled = false;
            if (shot == null) return;
            if (own) WindowDrag.Stop();   // el Finder deja de moverse por su cuenta: lo mueve la gelatina
            // el punto agarrado se mide respecto a la ventana ANTES de moverse (asi el primer cuadro no salta)
            cur = warm ?? new WobbleForm(h, shot, pendingFrame, pendingCursor);
            warm = null;
            cur.FormClosed += (o, e) => { if (cur == o) cur = null; };
            cur.Show();
        }

        // ---------- captura ----------
        // Se copia de la pantalla lo que se ve de la ventana: no le envia nada a la app, asi su arrastre no se congela.
        // Si el dock o la barra de MacDock la tapan, se usa PrintWindow (mas lento, pero sin pedazos del dock).
        static Snapshot Capture(IntPtr h, Rectangle frame)
        {
            if (frame.Width < 60 || frame.Height < 60 || frame.Width > 8000 || frame.Height > 8000) return null;
            bool covered = false;
            if (AvoidRects != null) foreach (var r in AvoidRects()) if (r.IntersectsWith(frame)) { covered = true; break; }
            var vs = SystemInformation.VirtualScreen;
            if (!vs.Contains(frame)) covered = true;   // parte fuera de la pantalla: la pantalla no la tiene
            int w = frame.Width, hh = frame.Height;
            var snap = new Snapshot { W = w, H = hh, Px = new int[w * hh] };
            using (var bmp = new Bitmap(w, hh, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
            {
                try
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        if (!covered) g.CopyFromScreen(frame.Location, Point.Empty, frame.Size, CopyPixelOperation.SourceCopy);
                        else
                        {
                            N.RECT wr; N.GetWindowRect(h, out wr);
                            IntPtr dc = g.GetHdc();
                            try
                            {
                                using (var full = new Bitmap(wr.right - wr.left, wr.bottom - wr.top, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
                                {
                                    g.ReleaseHdc(dc); dc = IntPtr.Zero;
                                    using (var gf = Graphics.FromImage(full)) { IntPtr fdc = gf.GetHdc(); try { PrintWindow(h, fdc, 2); } finally { gf.ReleaseHdc(fdc); } }
                                    g.DrawImageUnscaled(full, wr.left - frame.Left, wr.top - frame.Top);
                                }
                            }
                            finally { if (dc != IntPtr.Zero) g.ReleaseHdc(dc); }
                        }
                    }
                }
                catch (Exception ex) { Program.Log(ex); return null; }
                var bd = bmp.LockBits(new Rectangle(0, 0, w, hh), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                try { for (int y = 0; y < hh; y++) System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, snap.Px, y * w, w); }
                finally { bmp.UnlockBits(bd); }
            }
            // opaco y con las esquinas redondeadas de Windows 11 recortadas (alfa premultiplicado = 0)
            int rad = (int)Math.Round(8 * N.GetDpiForWindow(h) / 96f);
            for (int i = 0; i < snap.Px.Length; i++) snap.Px[i] |= unchecked((int)0xFF000000);
            for (int y = 0; y < rad; y++)
                for (int x = 0; x < rad; x++)
                {
                    float dx = rad - x - 0.5f, dy = rad - y - 0.5f;
                    if (dx * dx + dy * dy <= rad * rad) continue;
                    snap.Px[y * w + x] = 0; snap.Px[y * w + (w - 1 - x)] = 0;
                    snap.Px[(hh - 1 - y) * w + x] = 0; snap.Px[(hh - 1 - y) * w + (w - 1 - x)] = 0;
                }
            return snap;
        }
    }

    class WobbleForm : Form
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmFlush();
        public readonly IntPtr Target;
        readonly int[] src; readonly int srcW, srcH;
        readonly int W, H, NX = 5, NY = 5;
        readonly PointF[] pos, vel, rest; readonly float[] stiff;
        readonly PointF grab;   // punto agarrado, relativo a la ventana
        readonly int margin;
        readonly Point realHome;   // donde estaba la ventana real antes de apartarla
        Stopwatch clk = Stopwatch.StartNew(); double lastT;
        volatile bool released, finishing, running;
        volatile bool parked; double lastPark; System.Threading.Thread th; Point releasePos;
        Rectangle prevBox = Rectangle.Empty; bool columns;

        // lienzo y memoria compartidos entre arrastres (crearlos al vuelo causaba una pausa al empezar)
        static LayeredCanvas canvas; static int[] buf;
        public static int MarginFor(int w, int h) { return (int)Math.Min(380, Math.Max(120, Math.Max(w, h) * 0.3f)); }
        public static void Prepare(int w, int h)
        {
            int m = MarginFor(w, h), cw = w + m * 2, ch = h + m * 2;
            if (canvas != null && canvas.Width >= cw && canvas.Height >= ch) return;
            int nw = Math.Max(cw, canvas != null ? canvas.Width : 0), nh = Math.Max(ch, canvas != null ? canvas.Height : 0);
            if (canvas != null) canvas.Dispose();
            canvas = new LayeredCanvas(nw, nh); buf = new int[nw * nh];
        }

        public WobbleForm(IntPtr target, Wobbly.Snapshot shot, Rectangle frame, Point cursor)
        {
            Target = target; src = shot.Px; srcW = shot.W; srcH = shot.H;
            W = frame.Width; H = frame.Height; margin = MarginFor(W, H);
            Prepare(W, H);
            grab = new PointF(Math.Max(0, Math.Min(W, cursor.X - frame.Left)), Math.Max(0, Math.Min(H, cursor.Y - frame.Top)));
            N.RECT wr; N.GetWindowRect(target, out wr); realHome = new Point(wr.left, wr.top);
            int n = NX * NY; pos = new PointF[n]; vel = new PointF[n]; rest = new PointF[n]; stiff = new float[n];
            float diag = (float)Math.Sqrt(W * W + H * H);
            // el primer cuadro ya parte de donde esta el puntero ahora (sin salto)
            var c = Cursor.Position; float ox = c.X - grab.X, oy = c.Y - grab.Y;
            for (int j = 0; j < NY; j++)
                for (int i = 0; i < NX; i++)
                {
                    int id = j * NX + i;
                    rest[id] = new PointF(i * W / (float)(NX - 1), j * H / (float)(NY - 1));
                    pos[id] = new PointF(ox + rest[id].X, oy + rest[id].Y);
                    float d = (float)Math.Sqrt((rest[id].X - grab.X) * (rest[id].X - grab.X) + (rest[id].Y - grab.Y) * (rest[id].Y - grab.Y)) / diag;
                    float f = 1 - Math.Min(1, d * 1.25f);
                    stiff[id] = 140f + 1300f * f * f;   // cerca del agarre: rigido; lejos: muy blando (se arrastra y rebota)
                }
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle((int)ox - margin, (int)oy - margin, 1, 1);
        }

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8; return cp; } }
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var c = Cursor.Position;
            Draw(new Point((int)(c.X - grab.X), (int)(c.Y - grab.Y)));
            N.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            Park();   // la captura ya cubre la ventana: la real se aparta
            lastT = clk.Elapsed.TotalSeconds;
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

        // La ventana real se aparta fuera de todas las pantallas (Windows conserva su imagen, asi al volver no hay que repintarla)
        void Park()
        {
            var vs = SystemInformation.VirtualScreen;
            N.SetWindowPos(Target, IntPtr.Zero, vs.Left - W - 200, vs.Top, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200 | 0x4000);
            parked = true;
        }

        // devuelve la ventana real a una posicion (esquina visible de la ventana = p)
        void Unpark(Point p, bool async)
        {
            N.RECT wr; N.GetWindowRect(Target, out wr);
            var fr = Genie.Frame(Target);
            int offX = fr.Left - wr.left, offY = fr.Top - wr.top;   // bordes invisibles de Windows 11
            N.SetWindowPos(Target, IntPtr.Zero, p.X - offX, p.Y - offY, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200 | (async ? 0x4000 : 0));
            parked = false;
        }

        void EndOnUi(bool now) { try { BeginInvoke(new Action(() => Finish(now))); } catch { } }

        void Frame()
        {
            if (finishing) return;
            double now = clk.Elapsed.TotalSeconds; float dt = (float)Math.Min(0.034, now - lastT); lastT = now;
            if (!N.IsWindow(Target) || N.IsIconic(Target)) { running = false; EndOnUi(true); return; }
            var real = Genie.Frame(Target);
            if (real.Width != W || real.Height != H) { running = false; EndOnUi(true); return; }   // la redimensionaron
            // si el arrastre de Windows tardo en soltarse y devolvio la ventana real a la vista, se aparta otra vez
            if (parked && !released && now - lastPark > 0.004 && SystemInformation.VirtualScreen.IntersectsWith(real)) { lastPark = now; Park(); }
            var cpos = Cursor.Position;
            if (!released)
            {
                int vk = SystemInformation.MouseButtonsSwapped ? 0x02 : 0x01;
                if ((GetAsyncKeyState(vk) & 0x8000) == 0)
                {
                    released = true;
                    releasePos = new Point((int)(cpos.X - grab.X), (int)(cpos.Y - grab.Y));
                    if (EdgeTiler.Zone != 0)
                    {
                        // va a acomodarse en un borde: la ventana real vuelve y el acomodo la anima desde aqui
                        Unpark(releasePos, false);
                        var h0 = Target; try { BeginInvoke(new Action(() => { EdgeTiler.EndExternal(h0); Finish(true); })); } catch { }
                        running = false; return;
                    }
                    var h1 = Target; try { BeginInvoke(new Action(() => EdgeTiler.EndExternal(h1))); } catch { }
                }
            }
            var f = released ? new Rectangle(releasePos, new Size(W, H)) : new Rectangle((int)(cpos.X - grab.X), (int)(cpos.Y - grab.Y), W, H);
            // fisica: cada nodo es atraido a su lugar con un resorte poco amortiguado
            int steps = Math.Max(1, (int)Math.Ceiling(dt / 0.004f)); float h = dt / steps;
            float maxOff = 0, maxV = 0;
            for (int s = 0; s < steps; s++)
                for (int id = 0; id < pos.Length; id++)
                {
                    float tx = f.Left + rest[id].X, ty = f.Top + rest[id].Y, k = stiff[id], c = 2f * (float)Math.Sqrt(k) * 0.38f;
                    float ax = k * (tx - pos[id].X) - c * vel[id].X, ay = k * (ty - pos[id].Y) - c * vel[id].Y;
                    vel[id] = new PointF(vel[id].X + ax * h, vel[id].Y + ay * h);
                    float px = pos[id].X + vel[id].X * h, py = pos[id].Y + vel[id].Y * h;
                    px = Math.Max(tx - margin + 2, Math.Min(tx + margin - 2, px)); py = Math.Max(ty - margin + 2, Math.Min(ty + margin - 2, py));
                    pos[id] = new PointF(px, py);
                }
            for (int id = 0; id < pos.Length; id++)
            {
                float ox = Math.Abs(pos[id].X - (f.Left + rest[id].X)), oy = Math.Abs(pos[id].Y - (f.Top + rest[id].Y));
                maxOff = Math.Max(maxOff, Math.Max(ox, oy)); maxV = Math.Max(maxV, Math.Max(Math.Abs(vel[id].X), Math.Abs(vel[id].Y)));
            }
            Draw(f.Location);
            if (released && maxOff < 0.6f && maxV < 12f)
            {
                // asentada: la ventana real vuelve exactamente aqui (ya tiene su imagen) y en el siguiente refresco se quita la captura
                Unpark(releasePos, false);
                DwmFlush();
                running = false; EndOnUi(false);
            }
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

        // Copia de pixeles propia: cada pixel se calcula una vez con aritmetica entera exacta, franjas contiguas sin rendijas.
        // Filas si la ventana se deforma de lado, columnas si se deforma hacia arriba o abajo. Solo se envia el rectangulo ocupado.
        void Draw(Point origin)
        {
            int cx = origin.X - margin, cy = origin.Y - margin, CW = canvas.Width, CH = canvas.Height;
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
                for (int y = 0; y < H; y += Strip)
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
                for (int x = 0; x < W; x += Strip)
                {
                    int cw = Math.Min(Strip, W - x);
                    float u0 = x / (float)W, u1 = (x + cw) / (float)W, um = (u0 + u1) / 2;
                    int left = prev == int.MinValue ? (int)Math.Round((At(u0, 0).X + At(u0, 1).X) / 2 - cx) : prev;
                    int right = Math.Max(left + 1, (int)Math.Round((At(u1, 0).X + At(u1, 1).X) / 2 - cx));
                    prev = right;
                    int yt = (int)Math.Round(At(um, 0).Y - cy), yb = Math.Max(yt + 1, (int)Math.Round(At(um, 1).Y - cy));
                    Blit(left, yt, right - left, yb - yt, x * srcW / W, 0, Math.Max(1, (x + cw) * srcW / W - x * srcW / W), srcH, CW, CH);
                }
            IntPtr bits = canvas.Bits;
            for (int y = dirty.Top; y < dirty.Bottom; y++)
                System.Runtime.InteropServices.Marshal.Copy(buf, y * CW + dirty.Left, bits + (y * CW + dirty.Left) * 4, dirty.Width);
            prevBox = box;
            canvas.PushRegion(Handle, cx + box.X, cy + box.Y, box);
        }

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
            if (parked)
            {
                // terminar de golpe (redimension, salida...): la ventana vuelve donde iba la gelatina o a su sitio
                var c = Cursor.Position;
                Unpark(released ? releasePos : new Point((int)(c.X - grab.X), (int)(c.Y - grab.Y)), false);
            }
            // limpiar el lienzo compartido para el proximo arrastre
            if (!prevBox.IsEmpty)
            {
                int CW = canvas.Width;
                for (int y = prevBox.Top; y < prevBox.Bottom; y++) { Array.Clear(buf, y * CW + prevBox.Left, prevBox.Width); System.Runtime.InteropServices.Marshal.Copy(buf, y * CW + prevBox.Left, canvas.Bits + (y * CW + prevBox.Left) * 4, prevBox.Width); }
            }
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { running = false; if (th != null) th.Join(200); if (parked) Unpark(realHome, false); }
            base.Dispose(disposing);
        }
    }
}
