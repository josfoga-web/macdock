using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MacDock
{
    enum Kind { App, Start, Clock, Downloads, Trash, Separator, Stack }

    class DockItem
    {
        public Kind Kind; public string Name = ""; public string Lnk; public string Target = ""; public string Args = "";
        public Bitmap Icon; public bool Pinned; public bool IsExplorer;
        public List<IntPtr> Windows = new List<IntPtr>();
        public DateTime LaunchAt = DateTime.MinValue;
        public int Badge;
        public float X, W, Scale = 1f, Cx, Life = 1f;
        public DateTime BornAt = DateTime.MinValue, DyingAt = DateTime.MinValue;
        public bool Running { get { return Windows.Count > 0; } }
    }

    static class Program
    {
        public static string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDock");

        public static void Log(object o)
        {
            try { Directory.CreateDirectory(DataDir); File.AppendAllText(Path.Combine(DataDir, "error.log"), DateTime.Now + "  " + o + "\r\n\r\n"); } catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--finder" || args[0] == "--spotlight"))
            {
                IntPtr h = N.FindWindow(null, "MacDockWindow");
                if (h != IntPtr.Zero) N.PostMessage(h, N.RegisterWindowMessage(args[0] == "--finder" ? "MacDockOpenFinder" : "MacDockOpenSpotlight"), IntPtr.Zero, IntPtr.Zero);
                else if (args[0] == "--finder") System.Diagnostics.Process.Start("explorer.exe");
                return;
            }
            if (args.Length > 0 && args[0] == "--exit")
            {
                IntPtr h = N.FindWindow(null, "MacDockWindow");
                if (h != IntPtr.Zero) N.PostMessage(h, 0x10, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            string exeName = Path.GetFileNameWithoutExtension(Application.ExecutablePath).ToLowerInvariant();
            bool uninstall = (args.Length > 0 && args[0] == "--uninstall") || exeName.Contains("desinstal") || exeName.Contains("uninstall");
            bool install = !uninstall && ((args.Length > 0 && args[0] == "--install") || exeName.Contains("instal") || exeName.Contains("setup"));
            if (uninstall || install)
            {
                try { N.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                Application.EnableVisualStyles();
                if (uninstall) Setup.Uninstall(); else Setup.Install();
                return;
            }
            bool created;
            using (var mutex = new Mutex(true, "MacDock_Singleton_v1", out created))
            {
                if (!created) return;
                try { N.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.EnableVisualStyles();
                var form = new DockForm();
                Application.ThreadException += (s, e) => { Log(e.Exception); form.SafeRestore(); Application.Exit(); };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => { Log(e.ExceptionObject); if (e.IsTerminating) form.SafeRestore(); };
                Application.Run(form);
                form.SafeRestore();
            }
        }
    }

    class Config
    {
        public int IconSize = 48;
        public float Magnification = 1.8f;
        public bool ReserveSpace = false;
        public bool AutoHide = true;
        public bool AutoRefresh = false;
        public bool AppNap = false;
        public bool HotCorners = false;
        public bool MenuBar = true;
        public bool AutoTheme = false;
        public bool DynWall = false;
        public bool UseFinder = true;
        public bool SpotlightKey = true;
        public bool QuickLookExplorer = true;
        public bool MacButtons = true;
        public bool DesktopMenu = true;
        public bool WindowPreviews = true;
        public bool MultiMonitor = true;
        public int DeviceMode = 0; // 0 = Auto, 1 = PC de Escritorio, 2 = Laptop
        public bool GamingMode = true;
        public TimeSpan LightFrom = new TimeSpan(7, 0, 0);
        public TimeSpan DarkFrom = new TimeSpan(18, 30, 0);
        public bool HideTaskbar = true;
        public bool Magnify = true;
        public bool MacIcons = true;
        public bool BarAutoHide = true;
        public bool EdgeTiling = true;
        public bool LiveDrag = true;
        public bool Glass = true;
        public bool ShakeCursor = true;
        public bool TileKeys = true;
        public bool GenieFx = true;
        public bool MacTitles = true;
        string path;

        public Config(string p)
        {
            path = p;
            if (!File.Exists(p)) { Save(); return; }
            foreach (var line in File.ReadAllLines(p))
            {
                var kv = line.Split(new[] { '=' }, 2); if (kv.Length < 2) continue;
                string k = kv[0].Trim().ToLowerInvariant(), v = kv[1].Trim();
                float f; int i;
                if (k == "iconsize" && int.TryParse(v, out i)) IconSize = Math.Max(24, Math.Min(96, i));
                else if (k == "magnification" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) Magnification = Math.Max(1f, Math.Min(2.5f, f));
                else if (k == "reservespace") ReserveSpace = v == "1";
                else if (k == "hidetaskbar") HideTaskbar = v == "1";
                else if (k == "magnify") Magnify = v == "1";
                else if (k == "autohide") AutoHide = v == "1";
                else if (k == "autorefresh") AutoRefresh = v == "1";
                else if (k == "appnap") AppNap = v == "1";
                else if (k == "hotcorners") HotCorners = v == "1";
                else if (k == "menubar") MenuBar = v == "1";
                else if (k == "autotheme") AutoTheme = v == "1";
                else if (k == "dynwall") DynWall = v == "1";
                else if (k == "usefinder") UseFinder = v == "1";
                else if (k == "spotlight") SpotlightKey = v == "1";
                else if (k == "quicklookexplorer") QuickLookExplorer = v == "1";
                else if (k == "macbuttons") MacButtons = v == "1";
                else if (k == "desktopmenu") DesktopMenu = v == "1";
                else if (k == "windowpreviews") WindowPreviews = v == "1";
                else if (k == "multimonitor") MultiMonitor = v == "1";
                else if (k == "macicons") MacIcons = v == "1";
                else if (k == "barautohide") BarAutoHide = v == "1";
                else if (k == "edgetiling") EdgeTiling = v == "1";
                else if (k == "livedrag") LiveDrag = v == "1";
                else if (k == "glass") Glass = v == "1";
                else if (k == "shakecursor") ShakeCursor = v == "1";
                else if (k == "tilekeys") TileKeys = v == "1";
                else if (k == "geniefx") GenieFx = v == "1";
                else if (k == "mactitles") MacTitles = v == "1";
                else if (k == "devicemode" && int.TryParse(v, out i)) DeviceMode = Math.Max(0, Math.Min(2, i));
                else if (k == "gamingmode") GamingMode = v == "1";
                else if (k == "lightfrom") { TimeSpan t; if (TimeSpan.TryParse(v, out t)) LightFrom = t; }
                else if (k == "darkfrom") { TimeSpan t; if (TimeSpan.TryParse(v, out t)) DarkFrom = t; }
            }
            DeviceProfile.Mode = DeviceMode;
            DeviceProfile.GamingMode = GamingMode;
        }

        public void Save()
        {
            DeviceProfile.Mode = DeviceMode;
            DeviceProfile.GamingMode = GamingMode;
            try
            {
                File.WriteAllText(path,
                    "# Configuracion del dock. Cambia un valor, guarda y reinicia el dock.\r\n" +
                    "IconSize=" + IconSize + "\r\n" +
                    "Magnification=" + Magnification.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n" +
                    "Magnify=" + (Magnify ? 1 : 0) + "\r\n" +
                    "AutoHide=" + (AutoHide ? 1 : 0) + "\r\n" +
                    "AutoRefresh=" + (AutoRefresh ? 1 : 0) + "\r\n" +
                    "AppNap=" + (AppNap ? 1 : 0) + "\r\n" +
                    "HotCorners=" + (HotCorners ? 1 : 0) + "\r\n" +
                    "MenuBar=" + (MenuBar ? 1 : 0) + "\r\n" +
                    "AutoTheme=" + (AutoTheme ? 1 : 0) + "\r\n" +
                    "LightFrom=" + LightFrom.ToString(@"hh\:mm") + "\r\n" +
                    "DarkFrom=" + DarkFrom.ToString(@"hh\:mm") + "\r\n" +
                    "DynWall=" + (DynWall ? 1 : 0) + "\r\n" +
                    "UseFinder=" + (UseFinder ? 1 : 0) + "\r\n" +
                    "Spotlight=" + (SpotlightKey ? 1 : 0) + "\r\n" +
                    "QuickLookExplorer=" + (QuickLookExplorer ? 1 : 0) + "\r\n" +
                    "MacButtons=" + (MacButtons ? 1 : 0) + "\r\n" +
                    "DesktopMenu=" + (DesktopMenu ? 1 : 0) + "\r\n" +
                    "WindowPreviews=" + (WindowPreviews ? 1 : 0) + "\r\n" +
                    "MultiMonitor=" + (MultiMonitor ? 1 : 0) + "\r\n" +
                    "MacIcons=" + (MacIcons ? 1 : 0) + "\r\n" +
                    "BarAutoHide=" + (BarAutoHide ? 1 : 0) + "\r\n" +
                    "EdgeTiling=" + (EdgeTiling ? 1 : 0) + "\r\n" +
                    "LiveDrag=" + (LiveDrag ? 1 : 0) + "\r\n" +
                    "Glass=" + (Glass ? 1 : 0) + "\r\n" +
                    "ShakeCursor=" + (ShakeCursor ? 1 : 0) + "\r\n" +
                    "TileKeys=" + (TileKeys ? 1 : 0) + "\r\n" +
                    "GenieFx=" + (GenieFx ? 1 : 0) + "\r\n" +
                    "MacTitles=" + (MacTitles ? 1 : 0) + "\r\n" +
                    "DeviceMode=" + DeviceMode + "\r\n" +
                    "GamingMode=" + (GamingMode ? 1 : 0) + "\r\n" +
                    "ReserveSpace=" + (ReserveSpace ? 1 : 0) + "\r\n" +
                    "HideTaskbar=" + (HideTaskbar ? 1 : 0) + "\r\n");
            }
            catch { }
        }
    }

    class DarkColors : ProfessionalColorTable
    {
        static Color bg = Color.FromArgb(44, 44, 48), sel = Color.FromArgb(10, 100, 220), bd = Color.FromArgb(70, 70, 76);
        public override Color ToolStripDropDownBackground { get { return bg; } }
        public override Color ImageMarginGradientBegin { get { return bg; } }
        public override Color ImageMarginGradientMiddle { get { return bg; } }
        public override Color ImageMarginGradientEnd { get { return bg; } }
        public override Color MenuBorder { get { return bd; } }
        public override Color MenuItemBorder { get { return sel; } }
        public override Color MenuItemSelected { get { return sel; } }
        public override Color SeparatorDark { get { return bd; } }
        public override Color SeparatorLight { get { return bg; } }
    }

    class DockForm : Form
    {
        Config cfg;
        string appsDir, orderFile;
        float s = 1f;
        DockItem startItem, clockItem, downloadsItem, trashItem, sepItem, stackItem;
        StackPopup stackPopup; DateTime stackClosedAt = DateTime.MinValue; Kind stackKind, closedKind;
        List<DockItem> pinned = new List<DockItem>();
        List<DockItem> transient = new List<DockItem>();
        List<DockItem> all = new List<DockItem>();
        Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        VsyncTimer fast;
        Stopwatch frameClock = Stopwatch.StartNew();
        double lastFrameT;
        float slideFrom; double slideT0, slideDur = 0.3;
        bool scannedOnce;
        const double BornDur = 0.32, DieDur = 0.28;
        Font labelFont; float labelFontScale;
        System.Windows.Forms.Timer slow = new System.Windows.Forms.Timer();
        FileSystemWatcher watcher;

        LayeredCanvas canvas;
        int winX, winY, W, H;
        float mag = 0f, lastMx = -1;
        int hoverIdx = -1;
        bool hiddenFull = false, light = false, restored = false, appBarOn = false, trashFull = false;
        bool savedAutoHide = false, haveSavedState = false;
        string lastClock = "";
        DateTime idleSince = DateTime.MaxValue;
        int appBarMsg, taskbarCreatedMsg;

        DockItem pressItem; Point pressPt; bool dragging;
        Point lastCursor; float lastMag = -1; int stillFrames;
        System.Windows.Forms.Timer edge = new System.Windows.Forms.Timer();
        bool fullApp, peeking, menuOpen; DateTime leaveSince = DateTime.MaxValue;
        float slide = 0f, slideTarget = 0f;
        static readonly uint myPid = (uint)Process.GetCurrentProcess().Id;
        PowerLineStatus lastPower = (PowerLineStatus)(-1);
        DateTime refreshCheckAt = DateTime.MinValue;
        bool loweredRefresh;
        // Modo claro/oscuro automatico y fondo dinamico (se revisa cada 30 s; no hace nada si no toca cambiar)
        DateTime lookAt = DateTime.MinValue;
        string lastWallPeriod;
        void UpdateLook()
        {
            try
            {
                var now = DateTime.Now.TimeOfDay;
                if (cfg.AutoTheme)
                {
                    bool wantLight = cfg.LightFrom <= cfg.DarkFrom ? (now >= cfg.LightFrom && now < cfg.DarkFrom) : !(now >= cfg.DarkFrom && now < cfg.LightFrom);
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", true))
                    {
                        if (k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) != (wantLight ? 1 : 0))
                        {
                            k.SetValue("SystemUsesLightTheme", wantLight ? 1 : 0, RegistryValueKind.DWord);
                            k.SetValue("AppsUseLightTheme", wantLight ? 1 : 0, RegistryValueKind.DWord);
                            N.BroadcastSetting("ImmersiveColorSet");
                        }
                    }
                }
                if (cfg.DynWall)
                {
                    string period = now < new TimeSpan(6, 0, 0) ? "noche" : now < new TimeSpan(9, 0, 0) ? "amanecer" :
                                    now < new TimeSpan(17, 0, 0) ? "dia" : now < new TimeSpan(19, 30, 0) ? "atardecer" : "noche";
                    string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MacDock", period + ".png");
                    if (File.Exists(file))
                    {
                        string cur = "";
                        using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop")) if (k != null) cur = Convert.ToString(k.GetValue("WallPaper", ""));
                        // Solo cambia de fondo cuando cambia la hora del dia, y nunca si elegiste otro fondo:
                        // si el fondo actual no es uno de MacDock, el fondo dinamico se apaga solo y se respeta tu eleccion.
                        string dynDir = Path.GetDirectoryName(file);
                        bool ours = cur == "" || cur.StartsWith(dynDir, StringComparison.OrdinalIgnoreCase);
                        if (!ours) { cfg.DynWall = false; cfg.Save(); }
                        else if (period != lastWallPeriod && !string.Equals(cur, file, StringComparison.OrdinalIgnoreCase)) N.SpiStr(0x14, 0, file, 3);
                        lastWallPeriod = period;
                    }
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // =====================  funciones tipo Mac: Spotlight, Quick Look, soltar en el dock, aviso de descargas  =====================
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, int mods, int vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public N.RECT rcCaret; }
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);

        int msgFinder = N.RegisterWindowMessage("MacDockOpenFinder"), msgSpot = N.RegisterWindowMessage("MacDockOpenSpotlight");
        FileSystemWatcher dlWatcher;
        System.Windows.Forms.Timer inputTimer = new System.Windows.Forms.Timer { Interval = 45 };
        bool spaceHotKeyOn = false, deskIconsCached = true, rmbWasDown = false;
        Point rmbDownPt;

        void InitMacFeatures()
        {
            if (cfg.SpotlightKey) RegisterHotKey(Handle, 7, 0x0001 | 0x4000, 0x20);   // Alt + Espacio
            deskIconsCached = DesktopIcons.Visible;
            inputTimer.Tick += InputTick;
            inputTimer.Start();
            AllowDrop = true;
            global::MacDock.MacButtons.MacTitles = cfg.MacTitles;
            global::MacDock.Glass.Enabled = cfg.Glass;
            SetShake(cfg.ShakeCursor);
            SetTileKeys(cfg.TileKeys);
            global::MacDock.Genie.Enabled = cfg.GenieFx;
            global::MacDock.Genie.TargetFor = GenieTarget;
            global::MacDock.Genie.Install();
            SetMacButtons(cfg.MacButtons);
            SetDesktopMenu(cfg.DesktopMenu);
            WatchDownloads();
            WatchTaskbarPins();
        }

        // En macOS la ventana entera se mueve con el puntero. Windows puede tener apagado
        // "Mostrar el contenido de la ventana mientras se arrastra" y entonces solo mueve un contorno.
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] static extern bool SpiGetBool(int a, int u, ref int p, int f);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] static extern bool SpiSetPtr(int a, int u, IntPtr p, int f);
        bool liveDragChanged;
        void SetLiveDrag(bool on)
        {
            try
            {
                int cur = 0; SpiGetBool(0x0026, 0, ref cur, 0);
                if (on && cur == 0) { SpiSetPtr(0x0025, 1, IntPtr.Zero, 3); liveDragChanged = true; }
                else if (!on && liveDragChanged) { SpiSetPtr(0x0025, 0, IntPtr.Zero, 3); liveDragChanged = false; }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // rectangulo en pantalla del icono de la app duena de la ventana (destino del efecto genio)
        Rectangle GenieTarget(IntPtr h)
        {
            var scr = Screen.FromHandle(h).Bounds;
            DockItem it = all.FirstOrDefault(i => i.Windows.Contains(h));
            if (it == null)
            {
                uint pid; N.GetWindowThreadProcessId(h, out pid); string path = N.ProcessPath(pid);
                it = all.FirstOrDefault(i => i.Kind == Kind.App && !string.IsNullOrEmpty(path) && string.Equals(i.Target, path, StringComparison.OrdinalIgnoreCase));
            }
            if (it != null && DockShown && !hiddenFull && all.Contains(it))
            {
                float size = Ic * it.Scale, x = winX + it.X + (it.W - size) / 2, y = winY + SlidePx + BarTop + PadY + Ic - size;
                return new Rectangle((int)x, (int)y, Math.Max(8, (int)size), Math.Max(8, (int)size));
            }
            int w = (int)(48 * s);
            return new Rectangle(scr.Left + scr.Width / 2 - w / 2, scr.Bottom - w / 3, w, w / 3);
        }

        CursorShake shake;
        void SetShake(bool on)
        {
            if (on && shake == null) { shake = new CursorShake(s); shake.Show(); }
            else if (!on && shake != null) { shake.Close(); shake.Dispose(); shake = null; }
        }

        // Atajos para acomodar la ventana activa, como Fn+Control+flechas de macOS Sequoia:
        // Ctrl+Alt+flechas = mitades, Ctrl+Alt+F = llenar, Ctrl+Alt+C = centrar
        static readonly int[] tileVk = { 0x25, 0x27, 0x26, 0x28, 0x46, 0x43 };
        static readonly int[] tileMode = { 1, 2, 3, 4, 10, 9 };
        void SetTileKeys(bool on)
        {
            for (int i = 0; i < tileVk.Length; i++)
            {
                UnregisterHotKey(Handle, 30 + i);
                if (on) RegisterHotKey(Handle, 30 + i, 0x0002 | 0x0001 | 0x4000, tileVk[i]);
            }
        }

        global::MacDock.MacButtons macBtns;
        void SetMacButtons(bool on)
        {
            if (on && macBtns == null) { macBtns = new global::MacDock.MacButtons(light); macBtns.Show(); }
            else if (!on && macBtns != null) { macBtns.Close(); macBtns.Dispose(); macBtns = null; }
        }

        // ---- clic derecho en el escritorio y Espacio en el Explorador (sin SetWindowsHookEx para evitar falsos positivos de antivirus) ----
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(N.POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, int flags);
        DateTime nativeUntil = DateTime.MinValue;

        void SetDesktopMenu(bool on) { }
        void SetKeyHook(bool on)
        {
            if (!on && spaceHotKeyOn) { try { UnregisterHotKey(Handle, 8); } catch { } spaceHotKeyOn = false; }
        }

        static bool IsDesktopAt(N.POINT p)
        {
            IntPtr h = WindowFromPoint(p);
            if (h == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(h, 2);
            string rc = N.ClassOf(root), hc = N.ClassOf(h);
            return (rc == "Progman" || rc == "WorkerW") && (hc == "SHELLDLL_DefView" || hc == "SysListView32" || hc == "WorkerW" || hc == "Progman");
        }

        bool ShouldInterceptExplorerSpace()
        {
            if (!cfg.QuickLookExplorer) return false;
            if (QuickLook.IsOpenAndFocused()) return true;
            IntPtr fg = N.GetForegroundWindow();
            if (fg == IntPtr.Zero || N.ClassOf(fg) != "CabinetWClass") return false;
            uint pid; uint tid = N.GetWindowThreadProcessId(fg, out pid);
            var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
            if (!GetGUIThreadInfo(tid, ref gi) || gi.hwndCaret != IntPtr.Zero) return false;
            string fc = gi.hwndFocus == IntPtr.Zero ? "" : N.ClassOf(gi.hwndFocus);
            return fc == "DirectUIHWND" || fc == "SysListView32";
        }

        void InputTick(object sender, EventArgs e)
        {
            try
            {
                if (fullApp && cfg.GamingMode)
                {
                    if (spaceHotKeyOn) { UnregisterHotKey(Handle, 8); spaceHotKeyOn = false; }
                    rmbWasDown = false;
                    return;
                }
                bool wantSpace = ShouldInterceptExplorerSpace();
                if (wantSpace && !spaceHotKeyOn) { if (RegisterHotKey(Handle, 8, 0x4000, 0x20)) spaceHotKeyOn = true; }
                else if (!wantSpace && spaceHotKeyOn) { UnregisterHotKey(Handle, 8); spaceHotKeyOn = false; }

                if (cfg.DesktopMenu && !deskIconsCached && !menuOpen && DateTime.Now > nativeUntil)
                {
                    bool rmb = (GetAsyncKeyState(0x02) & 0x8000) != 0;
                    if (rmb && !rmbWasDown) { rmbWasDown = true; rmbDownPt = Cursor.Position; }
                    else if (!rmb && rmbWasDown)
                    {
                        rmbWasDown = false;
                        var pt = Cursor.Position;
                        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                        if (!shift && Math.Abs(pt.X - rmbDownPt.X) < 8 && Math.Abs(pt.Y - rmbDownPt.Y) < 8 && IsDesktopAt(new N.POINT { x = pt.X, y = pt.Y }))
                            DesktopMenuAt(pt);
                    }
                }
                else rmbWasDown = false;
            }
            catch { }
        }

        ShellMenu lastShellMenu;
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        static IntPtr DesktopView()
        {
            IntPtr dv = IntPtr.Zero, w = IntPtr.Zero;
            IntPtr pm = N.FindWindow("Progman", null);
            dv = FindWindowEx(pm, IntPtr.Zero, "SHELLDLL_DefView", null);
            while (dv == IntPtr.Zero && (w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero) dv = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
            return dv;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr after, string cls, string title);

        void DesktopMenuAt(Point pt)
        {
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var m = NewMenu();
            // estilo Mac
            Add(m, "Nueva carpeta", () => { FileOps.NewFolder(desk); Finder.OpenOrFocus(light, s, desk); });
            Add(m, "Abrir el escritorio en Finder", () => Finder.OpenOrFocus(light, s, desk));
            Add(m, "Ver lo que hay en el escritorio (pila)", () => { if (stackItem != null) ToggleStack(stackItem); });
            Add(m, DesktopIcons.Visible ? "Ocultar iconos del escritorio" : "Mostrar iconos del escritorio", () => { DesktopIcons.Toggle(); deskIconsCached = DesktopIcons.Visible; });
            Add(m, "Cambiar fondo de pantalla...", () => Process.Start("ms-settings:personalization-background"));
            m.Items.Add(new ToolStripSeparator());
            // Ordenar por / Actualizar (los mismos comandos que usa el escritorio de Windows)
            var sort = new ToolStripMenuItem("Ordenar por");
            string[] names = { "Nombre", "Tamaño", "Tipo de elemento", "Fecha de modificación" };
            for (int i = 0; i < names.Length; i++) { int cmd = 0x7602 + i; var si = new ToolStripMenuItem(names[i]); si.Click += (o, e) => { IntPtr dv = DesktopView(); if (dv != IntPtr.Zero) SendMessage(dv, 0x111, new IntPtr(cmd), IntPtr.Zero); }; sort.DropDownItems.Add(si); }
            m.Items.Add(sort);
            Add(m, "Actualizar", () => { IntPtr dv = DesktopView(); if (dv != IntPtr.Zero) SendMessage(dv, 0x111, new IntPtr(0x7103), IntPtr.Zero); });
            m.Items.Add(new ToolStripSeparator());
            // todas las opciones reales de Windows (Pegar, Nuevo, Git, NVIDIA, Configuracion de pantalla, Personalizar...)
            if (lastShellMenu != null) { lastShellMenu.Dispose(); lastShellMenu = null; }
            var sm = new ShellMenu();
            if (sm.AppendTo(m.Items, Handle)) lastShellMenu = sm; else sm.Dispose();
            while (m.Items.Count > 0 && m.Items[m.Items.Count - 1] is ToolStripSeparator) m.Items.RemoveAt(m.Items.Count - 1);
            if (m.Items.Count == 0) return;
            menuOpen = true;
            m.Closed += (o, e) => { menuOpen = false; };
            N.SetForegroundWindow(Handle);
            m.Show(pt);
        }

        void StopMacFeatures()
        {
            try { inputTimer.Stop(); } catch { }
            try { UnregisterHotKey(Handle, 7); } catch { }
            try { if (spaceHotKeyOn) UnregisterHotKey(Handle, 8); spaceHotKeyOn = false; } catch { }
            try { if (dlWatcher != null) dlWatcher.Dispose(); } catch { }
            try { SetMacButtons(false); } catch { }
            try { ClosePreview(); } catch { }
        }

        void QuickLookExplorer(IntPtr explorerWnd)
        {
            try
            {
                var t = Type.GetTypeFromProgID("Shell.Application");
                dynamic sh = Activator.CreateInstance(t);
                foreach (dynamic w in sh.Windows())
                {
                    try
                    {
                        if (new IntPtr((long)w.HWND) != explorerWnd) continue;
                        dynamic items = w.Document.SelectedItems();
                        if (items.Count > 0) { string p = items.Item(0).Path; QuickLook.Toggle(p, light); }
                        break;
                    }
                    catch { }
                }
                Marshal.ReleaseComObject(sh);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // ---- aviso de descargas: rebote + numerito ----
        void WatchDownloads()
        {
            try
            {
                if (downloadsItem == null || !Directory.Exists(downloadsItem.Target)) return;
                dlWatcher = new FileSystemWatcher(downloadsItem.Target) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
                FileSystemEventHandler onNew = (o, e) => NewDownload(e.FullPath);
                dlWatcher.Created += onNew;
                dlWatcher.Renamed += (o, e) => NewDownload(e.FullPath);
                dlWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        static bool IsTempDownload(string p)
        {
            string n = Path.GetFileName(p).ToLowerInvariant();
            return n.StartsWith("~") || n.StartsWith(".") || n.EndsWith(".crdownload") || n.EndsWith(".part") || n.EndsWith(".tmp") || n.EndsWith(".download") || n.EndsWith(".partial") || n == "desktop.ini";
        }

        void NewDownload(string path)
        {
            if (IsTempDownload(path)) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (downloadsItem == null) return;
                    downloadsItem.Badge++;
                    downloadsItem.LaunchAt = DateTime.Now;
                    StartAnim(); Render();
                }));
            }
            catch { }
        }

        void DrawBadge(Graphics g, RectangleF icon, int n)
        {
            string t = n > 99 ? "99+" : n.ToString();
            using (var f = new Font(UiFont.Semi, Math.Max(9f, icon.Width * 0.2f), FontStyle.Regular, GraphicsUnit.Pixel))
            {
                var sz = g.MeasureString(t, f);
                float h = sz.Height * 1.05f, w = Math.Max(h, sz.Width + h * 0.5f);
                var r = new RectangleF(icon.Right - w * 0.75f, icon.Top - h * 0.2f, w, h);
                using (var p = Round(r, h / 2)) using (var b = new SolidBrush(Color.FromArgb(255, 59, 48))) g.FillPath(b, p);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(t, f, Brushes.White, new RectangleF(r.X, r.Y + 0.5f, r.Width, r.Height), sf);
            }
        }

        static bool AllApps(string[] files)
        {
            return files.Length > 0 && files.All(f => { string x = Path.GetExtension(f).ToLowerInvariant(); return x == ".lnk" || x == ".exe" || x == ".url" || x == ".appref-ms"; });
        }

        // fija apps en el dock (copia el acceso directo a la carpeta del dock) en la posicion indicada
        void PinApps(string[] files, int at)
        {
            try
            {
                var order = pinned.Select(p => Path.GetFileName(p.Lnk)).ToList();
                foreach (var f in files)
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (ext == ".exe") { try { var vi = FileVersionInfo.GetVersionInfo(f); if (!string.IsNullOrEmpty(vi.FileDescription) && vi.FileDescription.Length < 40) name = vi.FileDescription; } catch { } }
                    name = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                    string dst = Path.Combine(appsDir, name + (ext == ".exe" ? ".lnk" : ext == ".lnk" ? ".lnk" : ext));
                    if (File.Exists(dst)) { order.Remove(Path.GetFileName(dst)); }
                    else if (ext == ".exe") Shell.CreateLnk(dst, f);
                    else File.Copy(f, dst, true);
                    order.Insert(Math.Min(at, order.Count), Path.GetFileName(dst)); at++;
                }
                File.WriteAllLines(orderFile, order);
                ScheduleReload();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // lo que fijes en la barra de tareas de Windows aparece solo en el dock
        FileSystemWatcher pinWatcher;
        void WatchTaskbarPins()
        {
            try
            {
                string tb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                if (!Directory.Exists(tb)) return;
                pinWatcher = new FileSystemWatcher(tb, "*.lnk") { IncludeSubdirectories = false };
                FileSystemEventHandler h = (o, e) =>
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            string t, a; Shell.ReadLnk(e.FullPath, out t, out a);
                            bool exists = pinned.Any(p => string.Equals(Path.GetFileName(p.Lnk), Path.GetFileName(e.FullPath), StringComparison.OrdinalIgnoreCase)
                                                       || (t != "" && string.Equals(p.Target, t, StringComparison.OrdinalIgnoreCase) && p.Args == a));
                            if (!exists) PinApps(new[] { e.FullPath }, pinned.Count);
                        }));
                    }
                    catch { }
                };
                pinWatcher.Created += h;
                pinWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // ---- soltar archivos en el dock: Papelera = borrar, app = abrir con ella, carpeta = mover/copiar ----
        DockItem dropItem;

        static string[] DraggedFiles(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            return e.Data.GetData(DataFormats.FileDrop) as string[];
        }

        DragDropEffects DropEffectFor(DockItem it, DragEventArgs e, string[] files)
        {
            if (files == null || files.Length == 0) return DragDropEffects.None;
            if (AllApps(files)) return DragDropEffects.Link;   // fijar apps en el dock
            if (it == null) return DragDropEffects.None;
            switch (it.Kind)
            {
                case Kind.Trash: return DragDropEffects.Move;
                case Kind.Downloads:
                case Kind.Stack:
                    if ((e.KeyState & 8) != 0) return DragDropEffects.Copy;
                    return string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(it.Target), StringComparison.OrdinalIgnoreCase) ? DragDropEffects.Move : DragDropEffects.Copy;
                case Kind.App: return (it.IsExplorer || (it.Target != "" && File.Exists(it.Target))) ? DragDropEffects.Link : DragDropEffects.None;
            }
            return DragDropEffects.None;
        }

        protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); StartAnim(); OnDragOver(e); }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            var pt = PointToClient(new Point(e.X, e.Y));
            int i = HitIndex(pt.X);
            dropItem = i >= 0 ? all[i] : null;
            e.Effect = DropEffectFor(dropItem, e, DraggedFiles(e));
            StartAnim();
        }

        protected override void OnDragLeave(EventArgs e) { base.OnDragLeave(e); dropItem = null; }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            var files = DraggedFiles(e);
            var it = dropItem; dropItem = null;
            if (files != null && AllApps(files))
            {
                var pt0 = PointToClient(new Point(e.X, e.Y));
                int at = pinned.Count;
                for (int i = 0; i < pinned.Count; i++) if (pt0.X < pinned[i].X + pinned[i].W / 2) { at = i; break; }
                BeginInvoke(new Action(() => PinApps(files, at)));
                return;
            }
            if (files == null || it == null) return;
            Keys mods = (e.KeyState & 8) != 0 ? Keys.Control : Keys.None;
            BeginInvoke(new Action(() =>
            {
                try
                {
                    switch (it.Kind)
                    {
                        case Kind.Trash: FileOps.Recycle(Handle, files); trashFull = N.RecycleBinFull(); trashItem.Icon = TrashIcon() ?? trashItem.Icon; Render(); break;
                        case Kind.Downloads:
                        case Kind.Stack: FileOps.Drop(Handle, files, it.Target, mods); break;
                        case Kind.App:
                            if (it.IsExplorer) { Finder.OpenOrFocus(light, s, Directory.Exists(files[0]) ? files[0] : Path.GetDirectoryName(files[0])); break; }
                            string args = string.Join(" ", files.Select(f => "\"" + f + "\"").ToArray());
                            if (!string.IsNullOrEmpty(it.Args)) args = it.Args + " " + args;
                            Process.Start(new ProcessStartInfo(it.Target, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(it.Target) });
                            it.LaunchAt = DateTime.Now; StartAnim();
                            break;
                    }
                }
                catch (Exception ex) { Program.Log(ex); }
            }));
        }

        // Algunas apps (navegadores basados en Chromium) se maximizan a su manera y quedan debajo de la barra superior.
        // Si la ventana activa ocupa todo el ancho y empieza encima del area de trabajo, se ajusta al espacio correcto.
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out N.RECT r, int size);
        HashSet<string> fixedOnce = new HashSet<string>();
        void FixUnderMenuBar()
        {
            try
            {
                CheckFullscreen();   // nunca mover un juego o video a pantalla completa
                if (bar == null || fullApp) return;
                IntPtr h = N.GetForegroundWindow();
                if (h == IntPtr.Zero || N.IsIconic(h)) return;
                uint pid; N.GetWindowThreadProcessId(h, out pid);
                if (pid == myPid || FullscreenApp.IsIgnoredOverlayProcess(pid)) return;
                string cls = N.ClassOf(h);
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" ||
                    cls == "Windows.UI.Core.CoreWindow" || cls == "XamlExplorerHostIslandWindow" || cls == "ScreenClippingWindow" ||
                    cls == "Xaml_WindowedPopupClass") return;
                int style = N.GetWindowLong(h, -16);
                // Exigir ventana normal con barra de titulo (WS_CAPTION) y borde redimensionable; nunca tocar overlays de captura ni ventanas sin bordes
                if ((style & 0x00C00000) != 0x00C00000 || (style & 0x00040000) == 0) return;
                N.RECT r; if (DwmGetWindowAttribute(h, 9, out r, 16) != 0) N.GetWindowRect(h, out r);   // borde visible real
                var scr = Screen.FromHandle(h); var wa = scr.WorkingArea; var b = scr.Bounds;
                if (N.IsZoomed(h)) return;   // las ventanas maximizadas por Windows ya respetan la barra
                // Solo ajustar si la ventana pertenece exclusivamente a este monitor (nunca cortar ventanas multi-monitor como capturas de pantalla)
                if (r.top >= wa.Top - 2 || Math.Abs(r.left - b.Left) > 12 || Math.Abs(r.right - b.Right) > 12) return;
                if ((r.bottom - r.top) > b.Height + 16) return;
                string key = h + "|" + r.top + "|" + r.bottom; if (fixedOnce.Contains(key)) return; fixedOnce.Add(key);
                N.SetWindowPos(h, IntPtr.Zero, wa.Left, wa.Top, wa.Width, wa.Height, 0x0004 | 0x0010);
            }
            catch { }
        }

        // Docker / WSL: su maquina virtual sigue gastando RAM y bateria aunque cierres la ventana de Docker.
        static bool DockerRunning()
        {
            foreach (var n in new[] { "vmmem", "vmmemWSL", "Docker Desktop", "com.docker.backend", "wslservice", "wsl" })
                if (Process.GetProcessesByName(n).Length > 0) return true;
            return false;
        }

        void StopDocker()
        {
            if (MessageBox.Show("Se cerrara Docker Desktop y se apagara WSL (sus contenedores se detienen).\n\nGuarda lo que tengas corriendo ahi. Continuar?",
                "MacDock", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var th = new Thread(() =>
            {
                try
                {
                    string docker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Docker\Docker\resources\bin\docker.exe");
                    if (File.Exists(docker)) RunHidden(docker, "desktop stop", 40000);
                    foreach (var n in new[] { "Docker Desktop", "com.docker.backend", "com.docker.build" })
                        foreach (var p in Process.GetProcessesByName(n)) { try { p.Kill(); } catch { } }
                    RunHidden(Path.Combine(Environment.SystemDirectory, "wsl.exe"), "--shutdown", 30000);
                    BeginInvoke(new Action(() => MessageBox.Show("Listo: Docker y WSL estan apagados. La RAM y la bateria que usaban ya quedaron libres.", "MacDock", MessageBoxButtons.OK, MessageBoxIcon.Information)));
                }
                catch (Exception ex) { Program.Log(ex); }
            });
            th.IsBackground = true; th.Start();
        }

        static void RunHidden(string exe, string args, int waitMs)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                if (p != null) p.WaitForExit(waitMs);
            }
            catch { }
        }

        // Bandeja del sistema: muestra un momento la barra de Windows y abre los iconos ocultos (Win+B, Enter).
        DateTime trayUntil = DateTime.MinValue;
        void ShowTray()
        {
            try
            {
                trayUntil = DateTime.Now.AddSeconds(10);
                IntPtr tray = N.FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return;
                N.ShowWindow(tray, 5);
                N.SetForegroundWindow(tray);
                N.PressKeys(0x5B, 0x42);
                var t = new System.Windows.Forms.Timer { Interval = 350 };
                t.Tick += (o, e) => { t.Stop(); t.Dispose(); N.PressKeys(0x0D); };
                t.Start();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // Boton sol/luna: cambia claro <-> oscuro al instante. Si estaba el modo automatico, se apaga (manda tu eleccion).
        void ToggleTheme()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", true))
                {
                    if (k == null) return;
                    int next = Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1 ? 0 : 1;
                    k.SetValue("SystemUsesLightTheme", next, RegistryValueKind.DWord);
                    k.SetValue("AppsUseLightTheme", next, RegistryValueKind.DWord);
                }
                if (cfg.AutoTheme) { cfg.AutoTheme = false; cfg.Save(); }
                N.BroadcastSetting("ImmersiveColorSet");
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        MenuBar bar;
        Screen curScreen = Screen.PrimaryScreen;
        DockPreview previewPopup;
        DockItem previewItem;
        DateTime hoverSince = DateTime.MaxValue;
        int prevHoverIdx = -1;

        void SetMenuBar(bool on)
        {
            if (on && bar == null)
            {
                MenuBar.AutoHideMax = cfg.BarAutoHide;
                bar = new MenuBar(s, light, Screen.PrimaryScreen, false, cfg.MultiMonitor);
                bar.ToggleTheme = ToggleTheme;
                bar.ShowTray = ShowTray;
                bar.OpenFolder = path =>
                {
                    if (cfg.UseFinder) Finder.OpenOrFocus(light, s, path);
                    else Process.Start("explorer.exe", path == null ? "" : "\"" + path + "\"");
                };
                bar.Show();
            }
            else if (!on && bar != null)
            {
                bar.Close(); bar.Dispose(); bar = null;
            }
        }

        void ClosePreview()
        {
            previewItem = null;
            if (previewPopup != null && !previewPopup.IsDisposed) { try { previewPopup.Close(); previewPopup.Dispose(); } catch { } }
            previewPopup = null;
        }

        void UpdatePreview()
        {
            if (!cfg.WindowPreviews || dragging || menuOpen || stackPopup != null || hiddenFull)
            {
                ClosePreview();
                return;
            }
            DockItem hov = hoverIdx >= 0 && hoverIdx < all.Count ? all[hoverIdx] : null;
            if (hov != null && hov.Kind == Kind.App && hov.Running && hov.Windows.Count > 0)
            {
                if (previewItem == hov && previewPopup != null && !previewPopup.IsDisposed) return;
                if ((DateTime.Now - hoverSince).TotalMilliseconds >= 360)
                {
                    ClosePreview();
                    previewItem = hov;
                    var anchor = new Point(winX + (int)(hov.X + hov.W / 2), winY + (int)(SlidePx + BarTop - (cfg.Magnify ? Ic * (MaxMag - 1f) : 0) - 28 * s));
                    previewPopup = new DockPreview(s, light, hov, anchor, curScreen.Bounds, Focus, () => { RefreshRunning(); Render(); });
                    previewPopup.Show();
                }
            }
            else if (previewPopup != null && !previewPopup.IsDisposed)
            {
                if (!previewPopup.IsMouseInsideOrBridge(Cursor.Position)) ClosePreview();
            }
        }
        AppNap nap = new AppNap();
        HotCorners corners = new HotCorners();
        HashSet<string> windowExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime napAt = DateTime.MinValue;

        void UpdateNap()
        {
            try
            {
                if (DeviceProfile.IsDesktop) { if (nap.Count > 0) nap.WakeAll(); return; }
                bool battery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
                if (!cfg.AppNap || !battery) { if (nap.Count > 0) nap.WakeAll(); return; }
                string fgExe = "";
                IntPtr fg = N.GetForegroundWindow();
                if (fg != IntPtr.Zero) { uint pid; N.GetWindowThreadProcessId(fg, out pid); fgExe = N.ProcessPath(pid); }
                nap.Update(windowExes, fgExe);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // Con bateria: pantalla a 60 Hz (ahorra mucha energia). Enchufado: la maxima (ej. 144 Hz).
        void ApplyRefresh()
        {
            try
            {
                if (DeviceProfile.IsDesktop) { if (loweredRefresh) RestoreRefresh(); return; }
                var pw = SystemInformation.PowerStatus.PowerLineStatus;
                if (pw == lastPower && Display.Current(Screen.PrimaryScreen.DeviceName) != 0) return;
                lastPower = pw;
                if (!cfg.AutoRefresh) { if (loweredRefresh) RestoreRefresh(); return; }
                string dev = Screen.PrimaryScreen.DeviceName;
                var rates = Display.Rates(dev);
                if (rates.Count < 2) return;
                int cur = Display.Current(dev);
                int target;
                if (pw == PowerLineStatus.Offline)
                {
                    target = rates.Contains(60) ? 60 : rates.Where(r => r >= 48).DefaultIfEmpty(rates[0]).Min();
                    if (cur != target && Display.Set(dev, target)) loweredRefresh = true;
                }
                else
                {
                    target = rates.Max();
                    if (cur != target) Display.Set(dev, target);
                    loweredRefresh = false;
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        void RestoreRefresh()
        {
            try
            {
                string dev = Screen.PrimaryScreen.DeviceName;
                var rates = Display.Rates(dev);
                if (rates.Count > 0) Display.Set(dev, rates.Max());
                loweredRefresh = false;
            }
            catch { }
        }

        public DockForm()
        {
            Text = "MacDockWindow";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(0, 0, 1, 1);
            Directory.CreateDirectory(Program.DataDir);
            appsDir = Path.Combine(Program.DataDir, "Apps");
            orderFile = Path.Combine(Program.DataDir, "order.txt");
            cfg = new Config(Path.Combine(Program.DataDir, "config.ini"));
            appBarMsg = N.RegisterWindowMessage("MacDockAppBarMsg");
            taskbarCreatedMsg = N.RegisterWindowMessage("TaskbarCreated");
            fast = new VsyncTimer(this); fast.Tick += FastTick;
            slow.Interval = 1000; slow.Tick += SlowTick;
            edge.Interval = 120; edge.Tick += EdgeTick;
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x8000000 | 0x8; return cp; }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // ---------- metrics ----------
        float Ic { get { return cfg.IconSize * s; } }
        float Gap { get { return 7 * s; } }
        float SepW { get { return 17 * s; } }
        float PadX { get { return 5 * s; } }
        float PadY { get { return 5 * s; } }
        float BarH { get { return Ic + PadY * 2 + 3 * s; } }
        float BottomM { get { return 5 * s; } }
        float MaxMag { get { return cfg.Magnify ? cfg.Magnification : 1f; } }
        float BarTop { get { return H - BottomM - BarH; } }
        float Slot(DockItem it) { return it.Kind == Kind.Separator ? SepW : Ic + Gap; }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { s = N.GetDpiForWindow(Handle) / 96f; } catch { s = DeviceDpi / 96f; }
            if (s <= 0) s = 1f;
            ReadTheme();
            SystemEvents.UserPreferenceChanged += (o, a) => { ReadTheme(); Render(); if (bar != null) bar.SetTheme(light); };
            SystemEvents.DisplaySettingsChanged += (o, a) => BeginInvoke(new Action(() => { Relayout(); if (appBarOn) SetAppBarPos(); if (bar != null) bar.OnDisplayChanged(); }));
            BuildItems();
            SetupTaskbar();
            if (cfg.ReserveSpace) RegisterAppBar();
            RefreshRunning();
            Relayout();
            if (cfg.EdgeTiling) try { EdgeTiler.Start(s); } catch (Exception ex) { Program.Log(ex); }
            SetLiveDrag(cfg.LiveDrag);
            SetupWatcher();
            SystemEvents.PowerModeChanged += (o, a) => BeginInvoke(new Action(() => { lastPower = (PowerLineStatus)(-1); refreshCheckAt = DateTime.Now.AddSeconds(2); }));
            ApplyRefresh();
            if (cfg.MenuBar) SetMenuBar(true);
            InitMacFeatures();
            slow.Start();
            edge.Start();
            idleSince = DateTime.Now;
        }

        void ReadTheme()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    light = k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1;
            }
            catch { light = false; }
        }

        // ---------- items ----------
        int IconPx { get { if (cfg.MacIcons) return 256; int need = (int)Math.Ceiling(Ic * Math.Max(1f, MaxMag)); return need <= 128 ? 128 : 256; } }

        Bitmap IconFor(string parsingName)
        {
            Bitmap b;
            if (iconCache.TryGetValue(parsingName, out b)) return b;
            b = Shell.GetIcon(parsingName, IconPx);
            if (cfg.MacIcons && b != null) b = MacIcon.Tile(b, IconPx);
            iconCache[parsingName] = b;
            return b;
        }

        // Iconos propios, como "Obtener informacion > icono" de macOS: un PNG o ICO en %LOCALAPPDATA%\MacDock\Iconos
        // con el nombre de la app (o del .exe) reemplaza su icono. Se usa tal cual, asi sirven iconos estilo Mac descargados.
        string IconsDir { get { return Path.Combine(Program.DataDir, "Iconos"); } }

        string CustomIconPath(DockItem it)
        {
            try
            {
                var names = new List<string>();
                if (!string.IsNullOrEmpty(it.Lnk)) names.Add(Path.GetFileNameWithoutExtension(it.Lnk));
                if (!string.IsNullOrEmpty(it.Target)) names.Add(Path.GetFileNameWithoutExtension(it.Target));
                if (!string.IsNullOrEmpty(it.Name)) names.Add(it.Name);
                foreach (var n in names)
                    foreach (var ext in new[] { ".png", ".ico" })
                    {
                        string f = Path.Combine(IconsDir, n + ext);
                        if (File.Exists(f)) return f;
                    }
            }
            catch { }
            return null;
        }

        Bitmap LoadCustomIcon(string f)
        {
            string key = "custom|" + f + "|" + File.GetLastWriteTimeUtc(f).Ticks;
            Bitmap b;
            if (iconCache.TryGetValue(key, out b)) return b;
            try
            {
                using (var src = f.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ? new Icon(f, 256, 256).ToBitmap() : new Bitmap(f))
                {
                    b = new Bitmap(IconPx, IconPx, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(b))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, 0, 0, IconPx, IconPx);
                    }
                }
            }
            catch (Exception ex) { Program.Log(ex); return null; }
            iconCache[key] = b;
            return b;
        }

        void ChooseIcon(DockItem it)
        {
            using (var dlg = new OpenFileDialog { Title = "Elegir icono para " + it.Name, Filter = "Imágenes (*.png;*.ico)|*.png;*.ico" })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                Directory.CreateDirectory(IconsDir);
                string name = !string.IsNullOrEmpty(it.Lnk) ? Path.GetFileNameWithoutExtension(it.Lnk) : Path.GetFileNameWithoutExtension(it.Target);
                foreach (var ext in new[] { ".png", ".ico" }) { try { File.Delete(Path.Combine(IconsDir, name + ext)); } catch { } }
                File.Copy(dlg.FileName, Path.Combine(IconsDir, name + Path.GetExtension(dlg.FileName).ToLowerInvariant()), true);
            }
            RefreshIcons();
        }

        void ResetIcon(DockItem it)
        {
            string f = CustomIconPath(it);
            if (f != null) try { File.Delete(f); } catch { }
            RefreshIcons();
        }

        // icono de un acceso directo; se regenera solo si el archivo cambio
        Bitmap PinnedIcon(DockItem it)
        {
            string custom = CustomIconPath(it);
            if (custom != null) { var cb = LoadCustomIcon(custom); if (cb != null) return cb; }
            if (it.IsExplorer && cfg.MacIcons)
            {
                Bitmap e; if (!iconCache.TryGetValue("::explorer", out e)) { e = MacIcon.Explorer(IconPx); iconCache["::explorer"] = e; }
                return e;
            }
            string key = it.Lnk;
            try { key += "|" + File.GetLastWriteTimeUtc(it.Lnk).Ticks; } catch { }
            Bitmap b;
            if (iconCache.TryGetValue(key, out b)) return b;
            b = Shell.GetIcon(it.Lnk, IconPx);
            if (cfg.MacIcons && b != null) b = MacIcon.Tile(b, IconPx);
            iconCache[key] = b;
            return b;
        }

        Bitmap FolderIcon(string dir, int glyph) { return cfg.MacIcons ? MacIcon.Folder(IconPx, glyph) : Shell.GetIcon(dir, IconPx); }

        Bitmap startIcon, macShadow;

        // vuelve a generar todos los iconos (al activar o desactivar los iconos estilo Mac)
        void RefreshIcons()
        {
            iconCache.Clear(); IconSizes.Clear(); startIcon = null; macShadow = null; trashMacEmpty = trashMacFull = null;
            foreach (var it in pinned) if (it.Lnk != null) it.Icon = PinnedIcon(it);
            if (trashItem != null) trashItem.Icon = TrashIcon() ?? trashItem.Icon;
            foreach (var it in transient) if (!string.IsNullOrEmpty(it.Target)) { string cip = CustomIconPath(it); it.Icon = (cip != null ? LoadCustomIcon(cip) : null) ?? IconFor(it.Target); }
            if (downloadsItem != null) downloadsItem.Icon = FolderIcon(downloadsItem.Target, 1);
            if (stackItem != null) stackItem.Icon = FolderIcon(stackItem.Target, 2);
            Render();
        }

        void BuildItems()
        {
            Directory.CreateDirectory(appsDir);
            bool first = !File.Exists(orderFile);
            if (first) ImportTaskbarPins();

            var order = File.Exists(orderFile) ? File.ReadAllLines(orderFile).ToList() : new List<string>();
            var files = Directory.GetFiles(appsDir, "*.lnk").ToList();
            files = files.OrderBy(f => { int i = order.IndexOf(Path.GetFileName(f)); return i < 0 ? 10000 : i; })
                         .ThenBy(f => Path.GetFileName(f)).ToList();

            pinned.Clear();
            foreach (var f in files)
            {
                var it = new DockItem { Kind = Kind.App, Lnk = f, Pinned = true, Name = Path.GetFileNameWithoutExtension(f) };
                Shell.ReadLnk(f, out it.Target, out it.Args);
                string n = it.Name.ToLowerInvariant();
                it.IsExplorer = it.Target.ToLowerInvariant().EndsWith("\\explorer.exe") ||
                                (it.Target == "" && (n.Contains("explorer") || n.Contains("explorador")));
                if (it.IsExplorer) it.Name = "Explorador de archivos";
                it.Icon = PinnedIcon(it);
                pinned.Add(it);
            }
            if (first) SaveOrder();

            if (startItem == null)
            {
                startItem = new DockItem { Kind = Kind.Start, Name = "Inicio" };
                clockItem = new DockItem { Kind = Kind.Clock, Name = "Centro de control" };
                string dl = N.Downloads();
                downloadsItem = new DockItem { Kind = Kind.Downloads, Name = "Descargas", Target = dl, Icon = FolderIcon(dl, 1) };
                trashFull = N.RecycleBinFull();
                trashItem = new DockItem { Kind = Kind.Trash, Name = "Papelera", Icon = TrashIcon() };
                sepItem = new DockItem { Kind = Kind.Separator };
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                stackItem = new DockItem { Kind = Kind.Stack, Name = "Escritorio", Target = desk, Icon = FolderIcon(desk, 2) };
            }
            Compose();
        }

        Bitmap trashMacEmpty, trashMacFull;
        Bitmap TrashIcon()
        {
            if (cfg.MacIcons)
            {
                if (trashFull) return trashMacFull ?? (trashMacFull = MacIcon.Trash(IconPx, true));
                return trashMacEmpty ?? (trashMacEmpty = MacIcon.Trash(IconPx, false));
            }
            return Shell.GetIcon("::{645FF040-5081-101B-9F08-00AA002F954E}", IconPx);
        }

        void ImportTaskbarPins()
        {
            string src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            if (Directory.Exists(src))
            {
                var lnks = Directory.GetFiles(src, "*.lnk")
                    .OrderBy(f => { var n = Path.GetFileName(f).ToLowerInvariant(); return (n.Contains("explorer") || n.Contains("explorador")) ? 0 : 1; })
                    .ThenBy(f => f).ToList();
                foreach (var f in lnks)
                {
                    string t, a; Shell.ReadLnk(f, out t, out a);
                    string key = (t + "|" + a).ToLowerInvariant();
                    if (t != "" && !seen.Add(key)) continue;
                    try { File.Copy(f, Path.Combine(appsDir, Path.GetFileName(f)), true); names.Add(Path.GetFileName(f)); } catch { }
                }
            }
            try { File.WriteAllLines(orderFile, names); } catch { }
        }

        void SaveOrder()
        {
            try { File.WriteAllLines(orderFile, pinned.Select(p => Path.GetFileName(p.Lnk)).ToArray()); } catch { }
        }

        void Compose()
        {
            all = new List<DockItem>();
            all.Add(startItem);
            all.AddRange(pinned);
            all.AddRange(transient);
            all.Add(sepItem);
            all.Add(clockItem);
            all.Add(stackItem);
            all.Add(downloadsItem);
            all.Add(trashItem);
        }

        void SetupWatcher()
        {
            try
            {
                watcher = new FileSystemWatcher(appsDir, "*.lnk");
                watcher.IncludeSubdirectories = false;
                FileSystemEventHandler h = (o, e) => BeginInvoke(new Action(ScheduleReload));
                watcher.Created += h; watcher.Deleted += h; watcher.Changed += h;
                watcher.Renamed += (o, e) => BeginInvoke(new Action(ScheduleReload));
                watcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        System.Windows.Forms.Timer reloadTimer;
        void ScheduleReload()
        {
            if (reloadTimer == null)
            {
                reloadTimer = new System.Windows.Forms.Timer { Interval = 600 };
                reloadTimer.Tick += (o, e) => { reloadTimer.Stop(); BuildItems(); RefreshRunning(); Relayout(); };
            }
            reloadTimer.Stop(); reloadTimer.Start();
        }

        // ---------- running windows ----------
        void RefreshRunning()
        {
            var wins = new List<KeyValuePair<IntPtr, string>>();
            var pidPath = new Dictionary<uint, string>();
            IntPtr me = Handle;
            N.EnumWindows((h, l) =>
            {
                if (h == me || !N.IsWindowVisible(h)) return true;
                uint wpid; N.GetWindowThreadProcessId(h, out wpid); if (wpid == myPid) return true;
                if (N.GetWindow(h, 4) != IntPtr.Zero) return true;
                int ex = N.GetWindowLong(h, -20);
                if ((ex & 0x80) != 0) return true;
                if (N.GetWindowTextLength(h) == 0) return true;
                int cloaked; N.DwmGetWindowAttribute(h, 14, out cloaked, 4);
                if (cloaked != 0) return true;
                uint pid; N.GetWindowThreadProcessId(h, out pid);
                string path;
                if (!pidPath.TryGetValue(pid, out path)) { path = N.ProcessPath(pid); pidPath[pid] = path; }
                string cls = N.ClassOf(h);
                if (path.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase) && cls != "CabinetWClass") return true;
                wins.Add(new KeyValuePair<IntPtr, string>(h, path));
                return true;
            }, IntPtr.Zero);

            windowExes = new HashSet<string>(wins.Select(w => w.Value).Where(v => v != ""), StringComparer.OrdinalIgnoreCase);
            var before = all.Select(i => i.Windows.Count).ToList();
            int beforeCount = all.Count;
            foreach (var p in pinned) p.Windows.Clear();
            foreach (var t in transient) t.Windows.Clear();
            var explorerItem = pinned.FirstOrDefault(p => p.IsExplorer); if (explorerItem != null && Finder.Current != null && !Finder.Current.IsDisposed && Finder.Current.Visible) explorerItem.Windows.Add(Finder.Current.Handle);

            foreach (var kv in wins)
            {
                string path = kv.Value;
                if (path == "") continue;
                if (path.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (explorerItem != null) explorerItem.Windows.Add(kv.Key);
                    continue;
                }
                if (path.EndsWith("\\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) continue;
                var it = pinned.FirstOrDefault(p => !p.IsExplorer && string.Equals(p.Target, path, StringComparison.OrdinalIgnoreCase));
                if (it == null)
                {
                    string fn = Path.GetFileName(path);
                    it = pinned.FirstOrDefault(p => !p.IsExplorer && p.Target != "" && string.Equals(Path.GetFileName(p.Target), fn, StringComparison.OrdinalIgnoreCase));
                }
                if (it == null)
                {
                    it = transient.FirstOrDefault(t => string.Equals(t.Target, path, StringComparison.OrdinalIgnoreCase));
                    if (it != null && it.DyingAt != DateTime.MinValue) { it.DyingAt = DateTime.MinValue; it.BornAt = DateTime.Now; }
                    if (it == null)
                    {
                        string name = Path.GetFileNameWithoutExtension(path);
                        try { var vi = FileVersionInfo.GetVersionInfo(path); if (!string.IsNullOrEmpty(vi.FileDescription)) name = vi.FileDescription; } catch { }
                        it = new DockItem { Kind = Kind.App, Target = path, Name = name };
                        string cip = CustomIconPath(it); it.Icon = (cip != null ? LoadCustomIcon(cip) : null) ?? IconFor(path);
                        if (scannedOnce) it.BornAt = DateTime.Now;   // aparece creciendo, como en macOS
                        transient.Add(it);
                    }
                }
                it.Windows.Add(kv.Key);
            }
            // las apps que se cierran se encogen antes de salir del dock
            bool anim = false; var nowD = DateTime.Now;
            foreach (var t in transient)
                if (t.Windows.Count == 0 && t.DyingAt == DateTime.MinValue) { t.DyingAt = scannedOnce ? nowD : nowD.AddSeconds(-DieDur); t.BornAt = DateTime.MinValue; anim = true; }
            transient.RemoveAll(t => t.DyingAt != DateTime.MinValue && (nowD - t.DyingAt).TotalSeconds >= DieDur);
            if (!scannedOnce) scannedOnce = true;
            Compose();
            if (anim || AnyTransition()) StartAnim();

            bool changed = all.Count != beforeCount;
            if (!changed) for (int i = 0; i < all.Count; i++) if ((all[i].Windows.Count > 0) != (before[i] > 0)) { changed = true; break; }
            if (changed) { if (all.Count != beforeCount) Relayout(); else Render(); }
        }

        // ---------- timers ----------
        void SlowTick(object sender, EventArgs e)
        {
            try
            {
                CheckFullscreen();
                // Modo Gaming / Render (0% CPU): cuando hay un juego o app en pantalla completa / sin bordes,
                // se suspende todo el escaneo de ventanas, iconos y procesos en segundo plano.
                if (fullApp && cfg.GamingMode)
                {
                    try { HideTaskbarIfShown(); } catch { }
                    ClosePreview();
                    if (fast.Enabled) fast.Stop();
                    return;
                }
                deskIconsCached = DesktopIcons.Visible;
                HideTaskbarIfShown();
                if (DateTime.Now >= refreshCheckAt) { refreshCheckAt = DateTime.Now.AddSeconds(5); ApplyRefresh(); }
                if (DateTime.Now >= napAt) { napAt = DateTime.Now.AddSeconds(3); UpdateNap(); }
                if (DateTime.Now >= lookAt) { lookAt = DateTime.Now.AddSeconds(30); UpdateLook(); }
                FixUnderMenuBar();
                if (!hiddenFull && !fast.Enabled && mag > 0f && Cursor.Position != lastCursor) StartAnim();
                RefreshRunning();
                bool tf = N.RecycleBinFull();
                if (tf != trashFull) { trashFull = tf; trashItem.Icon = TrashIcon() ?? trashItem.Icon; Render(); }
                string clk = DateTime.Now.ToString("HH:mm") + (DeviceProfile.IsDesktop ? "D" : SystemInformation.PowerStatus.BatteryLifePercent.ToString());
                if (clk != lastClock) { lastClock = clk; if (!fast.Enabled) Render(); }
                if (!fast.Enabled && idleSince != DateTime.MaxValue && (DateTime.Now - idleSince).TotalSeconds > 3)
                {
                    idleSince = DateTime.MaxValue;
                    if (canvas != null) { canvas.Dispose(); canvas = null; }
                    GC.Collect();
                    N.SetProcessWorkingSetSize(N.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        void StartAnim()
        {
            if (!fast.Enabled) { lastFrameT = frameClock.Elapsed.TotalSeconds - 1.0 / 120; fast.Start(); }
            idleSince = DateTime.MaxValue;
        }

        float LifeOf(DockItem it)
        {
            float a = 1f; var now = DateTime.Now;
            if (it.BornAt != DateTime.MinValue)
            {
                double t = (now - it.BornAt).TotalSeconds;
                if (t >= BornDur) it.BornAt = DateTime.MinValue; else a = Ease.OutCubic(t / BornDur);
            }
            if (it.DyingAt != DateTime.MinValue) a *= 1f - Ease.InOutCubic((now - it.DyingAt).TotalSeconds / DieDur);
            return a;
        }

        bool AnyTransition()
        {
            var now = DateTime.Now;
            foreach (var it in all)
                if (it.BornAt != DateTime.MinValue || (it.DyingAt != DateTime.MinValue && (now - it.DyingAt).TotalSeconds < DieDur)) return true;
            return false;
        }

        bool AnyBouncing()
        {
            foreach (var it in all) if (it.LaunchAt != DateTime.MinValue)
                {
                    if ((DateTime.Now - it.LaunchAt).TotalSeconds > 3 || it.Running) it.LaunchAt = DateTime.MinValue;
                    else return true;
                }
            return false;
        }

        void FastTick(object sender, EventArgs e)
        {
            if (hiddenFull) { ClosePreview(); fast.Stop(); return; }
            double nowT = frameClock.Elapsed.TotalSeconds;
            float dt = (float)Math.Max(0.001, Math.Min(0.05, nowT - lastFrameT)); lastFrameT = nowT;
            bool sliding = slide != slideTarget;
            if (sliding)
            {
                // ocultar: entra y sale suave; mostrar: arranca rapido y frena (como macOS)
                double p = (nowT - slideT0) / slideDur;
                float e2 = slideTarget > slideFrom ? Ease.InOutCubic(p) : Ease.OutCubic(p);
                slide = p >= 1 ? slideTarget : slideFrom + (slideTarget - slideFrom) * e2;
                if (slide >= 1f && slideTarget >= 1f)
                {
                    hiddenFull = true; mag = 0f; hoverIdx = -1; ClosePreview(); fast.Stop();
                    N.ShowWindow(Handle, 0); idleSince = DateTime.Now; return;
                }
            }
            Point c = Cursor.Position;
            float mx = c.X - winX, my = c.Y - (winY + SlidePx);
            float left = all[0].X - PadX, right = all[all.Count - 1].X + all[all.Count - 1].W + PadX;
            float topZone = BarTop - (mag > 0.05f ? Ic * (MaxMag - 1f) : 0f);
            bool inside = mx >= left - 2 && mx <= right + 2 && my >= topZone && my <= H + 2;
            if (dragging) inside = true;
            float target = inside ? 1f : 0f;
            mag = Ease.Approach(mag, target, dt, inside ? 0.055f : 0.075f);
            if (Math.Abs(target - mag) < 0.003f) mag = target;
            if (inside) lastMx = mx;
            hoverIdx = inside ? HitIndex(mx) : -1;
            if (hoverIdx != prevHoverIdx)
            {
                prevHoverIdx = hoverIdx;
                hoverSince = hoverIdx >= 0 ? DateTime.Now : DateTime.MaxValue;
                if (hoverIdx < 0 && previewPopup != null && !previewPopup.IsMouseInsideOrBridge(c)) ClosePreview();
            }
            UpdatePreview();
            bool bouncing = AnyBouncing() || AnyTransition();
            bool same = c == lastCursor && mag == lastMag && !bouncing && !dragging && !sliding;
            lastCursor = c; lastMag = mag;
            if (same)
            {
                if (++stillFrames > 90) { fast.Stop(); stillFrames = 0; idleSince = DateTime.Now; }
                return;
            }
            stillFrames = 0;
            DoLayout(lastMx, mag);
            Render();
            if (!inside && mag == 0f && !bouncing && !dragging && slide == slideTarget)
            {
                fast.Stop(); hoverIdx = -1; Render(); idleSince = DateTime.Now;
            }
        }

        // ---------- layout ----------
        void Relayout()
        {
            if (curScreen == null || !Screen.AllScreens.Any(sc => sc.DeviceName == curScreen.DeviceName)) curScreen = Screen.PrimaryScreen;
            Screen scr = cfg.MultiMonitor ? curScreen : Screen.PrimaryScreen;
            float restTotal = all.Sum(i => Slot(i));
            W = (int)Math.Min(scr.Bounds.Width, restTotal + PadX * 2 + Ic * (MaxMag - 1f) * 3f + 240 * s);
            H = (int)(BottomM + BarH + Ic * Math.Max(MaxMag - 1f, 0.6f) + 50 * s);
            winX = scr.Bounds.X + (scr.Bounds.Width - W) / 2;
            winY = scr.Bounds.Bottom - H;
            if (canvas != null && (canvas.Width != W || canvas.Height != H)) { canvas.Dispose(); canvas = null; }
            DoLayout(lastMx, mag);
            Render();
        }

        void DoLayout(float mx, float m)
        {
            float restTotal = 0; foreach (var it in all) { it.Life = LifeOf(it); restTotal += Slot(it) * it.Life; }
            float restStart = (W - restTotal) / 2f;
            float cx = restStart;
            foreach (var it in all) { float sl = Slot(it) * it.Life; it.Cx = cx + sl / 2f; cx += sl; }
            float total = 0;
            float spread = Ic * 2.7f;
            foreach (var it in all)
            {
                it.Scale = 1f;
                if (m > 0 && it.Kind != Kind.Separator && MaxMag > 1f)
                {
                    float d = Math.Abs(mx - it.Cx) / spread;
                    if (d < 1f) it.Scale = 1f + (MaxMag - 1f) * m * (float)((Math.Cos(Math.PI * d) + 1) / 2);
                }
                it.W = (Slot(it) + (it.Kind == Kind.Separator ? 0 : Ic * (it.Scale - 1f))) * it.Life;
                total += it.W;
            }
            float t = restTotal > 0 ? Math.Max(0f, Math.Min(1f, (mx - restStart) / restTotal)) : 0.5f;
            float start = restStart - (total - restTotal) * t;
            float x = start;
            foreach (var it in all) { it.X = x; x += it.W; }
        }

        int HitIndex(float mx)
        {
            for (int i = 0; i < all.Count; i++)
                if (mx >= all[i].X && mx < all[i].X + all[i].W) return all[i].Kind == Kind.Separator || all[i].DyingAt != DateTime.MinValue ? -1 : i;
            return -1;
        }

        // ---------- rendering ----------
        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        float Bounce(DockItem it)
        {
            if (it.LaunchAt == DateTime.MinValue) return 0;
            double t = (DateTime.Now - it.LaunchAt).TotalSeconds;
            if (t > 3 || (it.Kind == Kind.Downloads && t > 0.6)) return 0;
            return (float)(Ic * 0.42 * Math.Abs(Math.Sin(t * Math.PI / 0.6)));
        }

        void Render()
        {
            if (W <= 0 || H <= 0 || all.Count == 0 || hiddenFull) return;
            if (canvas == null) canvas = new LayeredCanvas(W, H);
            using (var g = Graphics.FromImage(canvas.Bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // los iconos ya vienen reducidos al tamano justo: basta un filtro bilineal (mucho mas rapido)
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.CompositingQuality = CompositingQuality.AssumeLinear;

                float left = all[0].X - PadX, right = all[all.Count - 1].X + all[all.Count - 1].W + PadX;
                float top = BarTop, bottom = H - BottomM;
                using (var hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                    g.FillRectangle(hit, left, bottom - 1, right - left, H - bottom + 1);

                var bar = new RectangleF(left, top, right - left, bottom - top);
                using (var p = Round(bar, Ic * 0.36f))
                {
                    using (var b = new SolidBrush(light ? Color.FromArgb(178, 246, 246, 248) : Color.FromArgb(168, 28, 28, 32))) g.FillPath(b, p);
                    using (var pen = new Pen(light ? Color.FromArgb(46, 0, 0, 0) : Color.FromArgb(64, 255, 255, 255), Math.Max(1f, s * 0.8f))) g.DrawPath(pen, p);
                }

                DockItem hover = hoverIdx >= 0 && hoverIdx < all.Count ? all[hoverIdx] : null;
                float hoverTop = 0;
                foreach (var it in all)
                {
                    if (it.Kind == Kind.Separator)
                    {
                        using (var pen = new Pen(light ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255), Math.Max(1f, s)))
                            g.DrawLine(pen, it.X + it.W / 2, top + PadY + 6 * s, it.X + it.W / 2, bottom - PadY - 4 * s);
                        continue;
                    }
                    if (it.Life < 0.02f) continue;
                    float size = Ic * it.Scale * it.Life;
                    float ix = it.X + (it.W - size) / 2f;
                    float iy = top + PadY + Ic - size - Bounce(it);
                    var r = new RectangleF(ix, iy, size, size);
                    if (it.Kind == Kind.Start) DrawStart(g, r);
                    else if (it.Kind == Kind.Clock) DrawClock(g, r);
                    else if (it.Icon != null)
                    {
                        float fade = it.Life < 1f ? it.Life : 1f, dark = it == pressItem && !dragging ? 0.62f : 1f;
                        if (fade < 1f || dark < 1f)
                            using (var ia = new ImageAttributes())
                            {
                                ia.SetColorMatrix(new ColorMatrix(new[] { new float[] { dark, 0, 0, 0, 0 }, new float[] { 0, dark, 0, 0, 0 }, new float[] { 0, 0, dark, 0, 0 }, new float[] { 0, 0, 0, fade, 0 }, new float[] { 0, 0, 0, 0, 1 } }));
                                IconSizes.Draw(g, it.Icon, r, ia);
                            }
                        else IconSizes.Draw(g, it.Icon, r);
                    }
                    else DrawPlaceholder(g, r, it.Name);
                    if (it == hover) hoverTop = iy; if (it.Badge > 0) DrawBadge(g, r, it.Badge);

                    if (it.Running)
                    {
                        float dr = 2f * s;
                        using (var b = new SolidBrush(light ? Color.FromArgb(200, 30, 30, 30) : Color.FromArgb(220, 255, 255, 255)))
                            g.FillEllipse(b, it.X + it.W / 2 - dr, bottom - PadY / 2f - dr - 0.5f * s, dr * 2, dr * 2);
                    }
                }

                if (hover != null && mag > 0.35f && !dragging)
                {
                    string label = hover.Name;
                    if (hover.Kind == Kind.Clock) label = DateTime.Now.ToString("dddd d 'de' MMMM, HH:mm");
                    if (labelFont == null || labelFontScale != s) { if (labelFont != null) labelFont.Dispose(); labelFont = new Font(UiFont.Reg, 12.5f * s, FontStyle.Regular, GraphicsUnit.Pixel); labelFontScale = s; }
                    var font = labelFont;
                    {
                        var ts = g.MeasureString(label, font);
                        float lw = ts.Width + 18 * s, lh = ts.Height + 8 * s;
                        float lx = hover.X + hover.W / 2 - lw / 2, ly = hoverTop - lh - 9 * s;
                        lx = Math.Max(2, Math.Min(W - lw - 2, lx)); ly = Math.Max(1, ly);
                        int a = (int)(255 * Math.Min(1f, (mag - 0.35f) / 0.4f));
                        using (var p = Round(new RectangleF(lx, ly, lw, lh), 6 * s))
                        {
                            using (var b = new SolidBrush(light ? Color.FromArgb(a * 235 / 255, 250, 250, 252) : Color.FromArgb(a * 235 / 255, 40, 40, 44))) g.FillPath(b, p);
                            using (var pen = new Pen(light ? Color.FromArgb(a / 5, 0, 0, 0) : Color.FromArgb(a / 4, 255, 255, 255), Math.Max(1f, s * 0.8f))) g.DrawPath(pen, p);
                        }
                        using (var b = new SolidBrush(light ? Color.FromArgb(a, 20, 20, 20) : Color.FromArgb(a, 240, 240, 242)))
                            g.DrawString(label, font, b, lx + 9 * s, ly + 4 * s);
                    }
                }
            }
            Push();
        }

        void DrawStart(Graphics g, RectangleF r)
        {
            if (cfg.MacIcons)
            {
                if (startIcon == null)
                {
                    using (var raw = new Bitmap(256, 256, PixelFormat.Format32bppArgb))
                    {
                        using (var gr = Graphics.FromImage(raw))
                        {
                            gr.SmoothingMode = SmoothingMode.AntiAlias;
                            using (var b = new LinearGradientBrush(new Rectangle(0, -1, 256, 258), Color.FromArgb(94, 176, 255), Color.FromArgb(18, 96, 220), 90f)) gr.FillRectangle(b, 0, 0, 256, 256);
                            float sq = 50, sg = 14, o = (256 - sq * 2 - sg) / 2;
                            using (var w = new SolidBrush(Color.FromArgb(248, 255, 255, 255)))
                                for (int i = 0; i < 4; i++)
                                    using (var rp = Round(new RectangleF(o + (i % 2) * (sq + sg), o + (i / 2) * (sq + sg), sq, sq), 11)) gr.FillPath(w, rp);
                        }
                        startIcon = MacIcon.Tile(raw, 256);
                    }
                }
                IconSizes.Draw(g, startIcon, r); return;
            }
            using (var p = Round(r, r.Width * 0.225f))
            using (var b = new LinearGradientBrush(r, Color.FromArgb(77, 163, 255), Color.FromArgb(16, 92, 214), 90f))
                g.FillPath(b, p);
            float q = r.Width * 0.19f, gap = r.Width * 0.035f;
            float ox = r.X + (r.Width - q * 2 - gap) / 2, oy = r.Y + (r.Height - q * 2 - gap) / 2;
            using (var w = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
            {
                g.FillRectangle(w, ox, oy, q, q); g.FillRectangle(w, ox + q + gap, oy, q, q);
                g.FillRectangle(w, ox, oy + q + gap, q, q); g.FillRectangle(w, ox + q + gap, oy + q + gap, q, q);
            }
        }

        void DrawClock(Graphics g, RectangleF r)
        {
            if (cfg.MacIcons)
            {
                // misma forma, tamano y sombra que los demas iconos
                if (macShadow == null) macShadow = MacIcon.ShadowOnly(256);
                IconSizes.Draw(g, macShadow, r);
                float bw = r.Width * MacIcon.BodyRatio;
                r = new RectangleF(r.X + (r.Width - bw) / 2, r.Y + (r.Height - bw) / 2 - r.Height * MacIcon.LiftRatio, bw, bw);
            }
            using (var p = cfg.MacIcons ? MacIcon.Squircle(r) : Round(r, r.Width * 0.225f))
            {
                using (var b = new SolidBrush(Color.FromArgb(252, 252, 252))) g.FillPath(b, p);
                using (var pen = new Pen(Color.FromArgb(40, 0, 0, 0), Math.Max(1f, s * 0.6f))) g.DrawPath(pen, p);
            }
            var now = DateTime.Now;
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            string[] dias = { "DOM", "LUN", "MAR", "MIÉ", "JUE", "VIE", "SÁB" };
            string day = dias[(int)now.DayOfWeek] + " " + now.Day;
            using (var f1 = new Font(UiFont.Semi, r.Width * 0.155f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var b1 = new SolidBrush(Color.FromArgb(255, 59, 48)))
                g.DrawString(day, f1, b1, new RectangleF(r.X, r.Y + r.Height * 0.08f, r.Width, r.Height * 0.22f), sf);
            using (var f2 = new Font(UiFont.Semi, r.Width * 0.27f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var b2 = new SolidBrush(Color.FromArgb(28, 28, 30)))
                g.DrawString(now.ToString("HH:mm"), f2, b2, new RectangleF(r.X, r.Y + r.Height * 0.3f, r.Width, r.Height * 0.36f), sf);

            var ps = SystemInformation.PowerStatus;
            float pct = ps.BatteryLifePercent;
            if (!DeviceProfile.IsDesktop && pct >= 0 && pct <= 1 && ps.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery)
            {
                float bw = r.Width * 0.24f, bh = r.Height * 0.11f;
                float bx = r.X + r.Width * 0.2f, by = r.Y + r.Height * 0.73f;
                using (var pen = new Pen(Color.FromArgb(140, 0, 0, 0), Math.Max(1f, r.Width * 0.018f)))
                using (var bp = Round(new RectangleF(bx, by, bw, bh), bh * 0.25f)) g.DrawPath(pen, bp);
                Color fill = pct <= 0.2f ? Color.FromArgb(255, 59, 48) : (ps.PowerLineStatus == PowerLineStatus.Online ? Color.FromArgb(52, 199, 89) : Color.FromArgb(60, 60, 64));
                using (var fb = new SolidBrush(fill)) g.FillRectangle(fb, bx + bw * 0.1f, by + bh * 0.22f, (bw * 0.8f) * pct, bh * 0.56f);
                using (var tb = new SolidBrush(Color.FromArgb(140, 0, 0, 0))) g.FillRectangle(tb, bx + bw, by + bh * 0.3f, bw * 0.07f, bh * 0.4f);
                using (var f3 = new Font(UiFont.Reg, r.Width * 0.13f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var b3 = new SolidBrush(Color.FromArgb(90, 90, 96)))
                    g.DrawString((int)Math.Round(pct * 100) + "%", f3, b3, bx + bw * 1.15f, by - r.Height * 0.035f);
            }
            else
            {
                using (var f3 = new Font(UiFont.Semi, r.Width * 0.125f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var b3 = new SolidBrush(Color.FromArgb(10, 122, 255)))
                    g.DrawString("CONTROL", f3, b3, new RectangleF(r.X, r.Y + r.Height * 0.69f, r.Width, r.Height * 0.22f), sf);
            }
        }

        void DrawPlaceholder(Graphics g, RectangleF r, string name)
        {
            using (var p = Round(r, r.Width * 0.225f))
            using (var b = new SolidBrush(Color.FromArgb(120, 120, 128))) g.FillPath(b, p);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var f = new Font(UiFont.Semi, r.Width * 0.42f, FontStyle.Regular, GraphicsUnit.Pixel))
                g.DrawString(name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?", f, Brushes.White, r, sf);
        }

        void Push()
        {
            canvas.Push(Handle, winX, winY + (int)Math.Round(SlidePx));
        }

        // ---------- mouse ----------
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            StartAnim();
            if (pressItem != null && e.Button == MouseButtons.Left && pressItem.Pinned)
            {
                if (!dragging && (Math.Abs(e.X - pressPt.X) > 8 * s || Math.Abs(e.Y - pressPt.Y) > 8 * s)) dragging = true;
                if (dragging)
                {
                    int cur = pinned.IndexOf(pressItem);
                    int target = cur;
                    for (int i = 0; i < pinned.Count; i++)
                        if (e.X >= pinned[i].X && e.X < pinned[i].X + pinned[i].W) { target = i; break; }
                    if (target != cur && cur >= 0) { pinned.RemoveAt(cur); pinned.Insert(target, pressItem); Compose(); }
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ClosePreview();
            int i = HitIndex(e.X);
            pressItem = i >= 0 ? all[i] : null;
            pressPt = e.Location;
            dragging = false;
            if (pressItem != null) Render();   // el icono se oscurece mientras se presiona, como en macOS
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var item = pressItem; pressItem = null;
            if (item != null) Render();
            if (dragging) { dragging = false; SaveOrder(); return; }
            int i = HitIndex(e.X);
            var it = i >= 0 ? all[i] : null;
            try
            {
                if (e.Button == MouseButtons.Left && it != null && it == item) Activate(it);
                else if (e.Button == MouseButtons.Right) ShowMenu(it);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        void Launch(DockItem it)
        {
            var psi = new ProcessStartInfo(it.Lnk ?? it.Target) { UseShellExecute = true };
            if (it.Lnk == null && it.Target != "") psi.WorkingDirectory = Path.GetDirectoryName(it.Target);
            Process.Start(psi);
            if (!it.IsExplorer) { it.LaunchAt = DateTime.Now; StartAnim(); }
        }

        void Activate(DockItem it)
        {
            ClosePreview();
            switch (it.Kind)
            {
                case Kind.Start: N.PressKeys(0x5B); return;
                case Kind.Clock:
                    var ccAnchor = new Point(winX + (int)(it.X + it.W / 2), winY + (int)(SlidePx + BarTop - 6 * s));
                    ControlCenter.Toggle(s, light, ccAnchor, curScreen.Bounds, false, ToggleTheme);
                    return;
                case Kind.Downloads: it.Badge = 0; Render(); ToggleStack(it); return;
                case Kind.Stack: ToggleStack(it); return;
                case Kind.Trash: Process.Start("explorer.exe", "shell:RecycleBinFolder"); return;
            }
            RefreshRunning();
            var ws = it.Windows;
            if (ws.Count == 0) { if (it.IsExplorer && cfg.UseFinder) { Finder.OpenOrFocus(light, s); return; } Launch(it); return; }
            IntPtr fg = N.GetForegroundWindow();
            int idx = ws.IndexOf(fg);
            if (idx < 0) Focus(ws[0]);
            else if (ws.Count > 1) Focus(ws[(idx + 1) % ws.Count]);
            else global::MacDock.Genie.Minimize(fg);
        }

        void ToggleStack(DockItem it)
        {
            if (stackPopup != null && !stackPopup.IsDisposed)
            {
                bool same = stackKind == it.Kind;
                stackPopup.Close();
                if (same) return;
                stackClosedAt = DateTime.MinValue;
            }
            stackKind = it.Kind;
            // el clic que cierra el panel (al perder foco) no debe volver a abrirlo
            if ((DateTime.Now - stackClosedAt).TotalMilliseconds < 300 && closedKind == it.Kind) return;
            var anchor = new Point(winX + (int)(it.X + it.W / 2), winY + (int)(SlidePx + BarTop - 8 * s));
            stackPopup = it.Kind == Kind.Downloads
                ? new StackPopup(s, light, anchor, curScreen.Bounds, "Descargas", new[] { it.Target }, true)
                : new StackPopup(s, light, anchor, curScreen.Bounds);
            stackPopup.FormClosed += (o, e) => { stackClosedAt = DateTime.Now; closedKind = stackKind; stackPopup = null; };
            stackPopup.Show();
            N.SetForegroundWindow(stackPopup.Handle);
        }

        void Focus(IntPtr h)
        {
            if (N.IsIconic(h)) { if (global::MacDock.Genie.Restore(h)) return; N.ShowWindow(h, 9); }
            N.SetForegroundWindow(h);
        }

        // ---------- context menu ----------
        ContextMenuStrip NewMenu()
        {
            return MenuStyle.New(light);
        }

        void Add(ContextMenuStrip m, string text, Action a, bool bold = false)
        {
            var mi = new ToolStripMenuItem(text);
            if (bold) mi.Font = MenuStyle.MenuFontBold;
            mi.Click += (o, e) => { try { a(); } catch (Exception ex) { Program.Log(ex); } };
            m.Items.Add(mi);
        }

        void ShowMenu(DockItem it)
        {
            ClosePreview();
            var m = NewMenu();
            if (it != null && it.Kind == Kind.App)
            {
                foreach (var h in it.Windows.Take(10))
                {
                    var hh = h; string t = N.TitleOf(h); if (t.Length > 60) t = t.Substring(0, 57) + "...";
                    Add(m, t, () => Focus(hh));
                }
                if (it.Windows.Count > 0) m.Items.Add(new ToolStripSeparator());
                if ((it.Target + it.Name).IndexOf("docker", StringComparison.OrdinalIgnoreCase) >= 0 && DockerRunning())
                {
                    Add(m, "Apagar Docker y WSL (libera RAM y bateria)", () => StopDocker(), true);
                    m.Items.Add(new ToolStripSeparator());
                }
                var optsApp = new ToolStripMenuItem("Opciones");
                Action<string, Action> addSub = (txt, act) => { var mi = new ToolStripMenuItem(txt); mi.Click += (o, e) => { try { act(); } catch (Exception ex) { Program.Log(ex); } }; optsApp.DropDownItems.Add(mi); };
                if (it.Pinned)
                    addSub("Quitar del Dock", () => { try { File.Delete(it.Lnk); } catch { } });
                else if (it.Target != "")
                    addSub("Mantener en el Dock", () =>
                    {
                        string name = string.Join("_", it.Name.Split(Path.GetInvalidFileNameChars()));
                        string lnk = Path.Combine(appsDir, name + ".lnk");
                        Shell.CreateLnk(lnk, it.Target);
                        var order = pinned.Select(p => Path.GetFileName(p.Lnk)).ToList(); order.Add(Path.GetFileName(lnk));
                        try { File.WriteAllLines(orderFile, order); } catch { }
                    });
                addSub("Cambiar icono...", () => ChooseIcon(it));
                if (CustomIconPath(it) != null) addSub("Restaurar icono original", () => ResetIcon(it));
                addSub("Abrir carpeta de iconos", () => { Directory.CreateDirectory(IconsDir); Process.Start("explorer.exe", "\"" + IconsDir + "\""); });
                if (!string.IsNullOrEmpty(it.Target) && File.Exists(it.Target))
                    addSub("Mostrar en Finder", () => Process.Start("explorer.exe", "/select,\"" + it.Target + "\""));
                m.Items.Add(optsApp);
                m.Items.Add(new ToolStripSeparator());
                if (it.Running)
                {
                    Add(m, "Nueva ventana", () => Launch(it));
                    Add(m, "Mostrar todas las ventanas", () => { foreach (var h in it.Windows.ToList()) { if (N.IsIconic(h)) N.ShowWindow(h, 9); N.SetForegroundWindow(h); } });
                    Add(m, "Ocultar", () => { foreach (var h in it.Windows.ToList()) global::MacDock.Genie.Minimize(h); });
                    Add(m, "Salir", () => { foreach (var h in it.Windows.ToList()) N.PostMessage(h, 0x10, IntPtr.Zero, IntPtr.Zero); });
                }
                else Add(m, "Abrir", () => Launch(it), true);
            }
            else if (it != null && it.Kind == Kind.Trash)
            {
                Add(m, "Abrir", () => Activate(it), true);
                Add(m, "Vaciar papelera...", () => { N.SHEmptyRecycleBin(Handle, null, 0); });
                m.Items.Add(new ToolStripSeparator());
            }
            else if (it != null && it.Kind == Kind.Clock)
            {
                Add(m, "Abrir Centro de Control (estilo macOS)", () => Activate(it), true);
                Add(m, "Panel rápido de Windows (Win + A)", () => N.PressKeys(0x5B, 0x41));
                Add(m, "Notificaciones y calendario", () => N.PressKeys(0x5B, 0x4E));
                Add(m, "Configuracion de Windows", () => Process.Start("ms-settings:"));
                m.Items.Add(new ToolStripSeparator());
            }
            else if (it != null && it.Kind == Kind.Stack)
            {
                Add(m, "Ver lo que hay en el escritorio", () => ToggleStack(it), true);
                Add(m, DesktopIcons.Visible ? "Ocultar iconos del escritorio" : "Mostrar iconos del escritorio", () => { DesktopIcons.Toggle(); deskIconsCached = DesktopIcons.Visible; });
                Add(m, "Abrir en el Explorador", () => Process.Start("explorer.exe", "\"" + it.Target + "\""));
                m.Items.Add(new ToolStripSeparator());
            }
            else if (it != null && it.Kind == Kind.Downloads)
            {
                Add(m, "Ver descargas", () => ToggleStack(it), true);
                Add(m, "Abrir en el Explorador", () => Process.Start("explorer.exe", "\"" + it.Target + "\""));
                m.Items.Add(new ToolStripSeparator());
            }

            bool general = it == null || it.Kind == Kind.Separator || it.Kind == Kind.Start;
            var opts = new ToolStripMenuItem("Opciones del dock");
            Action<string, bool, Action> addOpt = (text, chk, act) =>
            {
                var mi = new ToolStripMenuItem(text) { Checked = chk };
                mi.Click += (o, e) => { try { act(); } catch (Exception ex) { Program.Log(ex); } };
                opts.DropDownItems.Add(mi);
            };

            // Selector de modo PC de Escritorio vs Laptop
            var modeMenu = new ToolStripMenuItem("Modo de equipo (" + (DeviceProfile.IsDesktop ? "PC de Escritorio" : "Laptop") + ")");
            Action<string, int> addMode = (txt, md) =>
            {
                var mi = new ToolStripMenuItem(txt) { Checked = cfg.DeviceMode == md };
                mi.Click += (o, e) =>
                {
                    cfg.DeviceMode = md; cfg.Save();
                    ApplyRefresh(); UpdateNap();
                    if (bar != null) bar.Redraw(true);
                    Render();
                };
                modeMenu.DropDownItems.Add(mi);
            };
            addMode("Automático (Detecta si tiene batería)", 0);
            addMode("Forzar modo PC de Escritorio (Alto Rendimiento)", 1);
            addMode("Forzar modo Laptop (Batería y Eficiencia)", 2);
            opts.DropDownItems.Add(modeMenu);

            addOpt("Modo Gaming / Render 0% CPU (pantalla completa y sin bordes)", cfg.GamingMode, () => { cfg.GamingMode = !cfg.GamingMode; cfg.Save(); });
            opts.DropDownItems.Add(new ToolStripSeparator());

            addOpt("Iconos estilo macOS (forma redondeada)", cfg.MacIcons, () => { cfg.MacIcons = !cfg.MacIcons; cfg.Save(); RefreshIcons(); });
            addOpt("Ocultar barra superior con ventanas maximizadas", cfg.BarAutoHide, () => { cfg.BarAutoHide = !cfg.BarAutoHide; cfg.Save(); MenuBar.AutoHideMax = cfg.BarAutoHide; });
            addOpt("Acomodar ventanas arrastrándolas a los bordes (macOS)", cfg.EdgeTiling, () => { cfg.EdgeTiling = !cfg.EdgeTiling; cfg.Save(); if (cfg.EdgeTiling) EdgeTiler.Start(s); else EdgeTiler.Stop(); });
            addOpt("Mover ventanas con su contenido (como Mac)", cfg.LiveDrag, () => { cfg.LiveDrag = !cfg.LiveDrag; cfg.Save(); SetLiveDrag(cfg.LiveDrag); });
            addOpt("Barra de título estilo Mac (puntos a la izquierda)", cfg.MacTitles, () => { cfg.MacTitles = !cfg.MacTitles; cfg.Save(); global::MacDock.MacButtons.MacTitles = cfg.MacTitles; SetMacButtons(false); SetMacButtons(cfg.MacButtons); });
            addOpt("Transparencia estilo macOS (menús y paneles)", cfg.Glass, () => { cfg.Glass = !cfg.Glass; cfg.Save(); global::MacDock.Glass.Enabled = cfg.Glass; });
            addOpt("Agitar el mouse para agrandar el puntero", cfg.ShakeCursor, () => { cfg.ShakeCursor = !cfg.ShakeCursor; cfg.Save(); SetShake(cfg.ShakeCursor); });
            addOpt("Atajos para acomodar ventanas (Ctrl+Alt+flechas, F, C)", cfg.TileKeys, () => { cfg.TileKeys = !cfg.TileKeys; cfg.Save(); SetTileKeys(cfg.TileKeys); });
            addOpt("Efecto genio al minimizar (como macOS)", cfg.GenieFx, () => { cfg.GenieFx = !cfg.GenieFx; cfg.Save(); global::MacDock.Genie.Enabled = cfg.GenieFx; global::MacDock.Genie.SetGlobal(cfg.GenieFx); });
            addOpt("Diagnóstico de botones (abrir registro)", false, () => { if (File.Exists(global::MacDock.MacButtons.LogFile)) Process.Start("notepad.exe", "\"" + global::MacDock.MacButtons.LogFile + "\""); else MessageBox.Show("Todavia no hay registro. Activa \"Botones de Mac en todas las ventanas\" y abre algunas apps.", "MacDock"); });
            var desk = new ToolStripMenuItem("Escritorio estilo Mac");
            Action<string, Action> addDesk = (txt, act) => { var mi = new ToolStripMenuItem(txt); mi.Click += (o, e) => { try { act(); } catch (Exception ex) { Program.Log(ex); } }; desk.DropDownItems.Add(mi); };
            if (!global::MacDock.DesktopStyle.Applied)
                addDesk("Iconos estilo Mac en los accesos directos", () => { int ok, bad; global::MacDock.DesktopStyle.ApplyIcons(out ok, out bad); MessageBox.Show(ok + " iconos cambiados" + (bad > 0 ? ", " + bad + " no se pudieron (los del escritorio publico piden permisos)" : "") + ".\nPuedes volver a los originales desde este mismo menu.", "MacDock"); });
            else
            {
                addDesk("Actualizar iconos estilo Mac (accesos nuevos)", () => { int ok, bad; global::MacDock.DesktopStyle.ApplyIcons(out ok, out bad); });
                addDesk("Restaurar iconos originales", () => global::MacDock.DesktopStyle.RestoreIcons());
            }
            addDesk(global::MacDock.DesktopStyle.BinHidden ? "Mostrar la papelera en el escritorio" : "Quitar la papelera del escritorio (ya esta en el dock)", () => global::MacDock.DesktopStyle.HideBin(!global::MacDock.DesktopStyle.BinHidden));
            addDesk(global::MacDock.DesktopStyle.ArrowsRemoved ? "Volver a mostrar flechas de acceso directo (pide permiso)" : "Quitar flechas de acceso directo (pide permiso de administrador)", () =>
            {
                bool rem = !global::MacDock.DesktopStyle.ArrowsRemoved;
                global::MacDock.DesktopStyle.RemoveArrows(rem);
                if (MessageBox.Show("Para ver el cambio hay que reiniciar el Explorador de Windows (se cierran sus ventanas). ¿Reiniciarlo ahora?", "MacDock", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    foreach (var p in Process.GetProcessesByName("explorer")) try { p.Kill(); } catch { }
                }
            });
            opts.DropDownItems.Add(desk);
            addOpt("Efecto lupa", cfg.Magnify, () => { cfg.Magnify = !cfg.Magnify; cfg.Save(); Relayout(); });
            addOpt("Vista previa de ventanas al pasar el mouse", cfg.WindowPreviews, () => { cfg.WindowPreviews = !cfg.WindowPreviews; cfg.Save(); if (!cfg.WindowPreviews) ClosePreview(); });
            addOpt("Soporte multi-monitor (dock y barra superior)", cfg.MultiMonitor, () =>
            {
                cfg.MultiMonitor = !cfg.MultiMonitor; cfg.Save();
                if (!cfg.MultiMonitor) curScreen = Screen.PrimaryScreen;
                Relayout();
                if (appBarOn) SetAppBarPos();
                if (bar != null) bar.SetMultiMonitor(cfg.MultiMonitor);
            });
            addOpt("Ocultar cuando una ventana lo tape", cfg.AutoHide && !cfg.ReserveSpace, () =>
            {
                cfg.AutoHide = !(cfg.AutoHide && !cfg.ReserveSpace);
                if (cfg.AutoHide && cfg.ReserveSpace) { cfg.ReserveSpace = false; UnregisterAppBar(); }
                cfg.Save();
            });
            if (!DeviceProfile.IsDesktop)
            {
                addOpt("Pantalla a 60 Hz con bateria (ahorro Laptop)", cfg.AutoRefresh, () =>
                {
                    cfg.AutoRefresh = !cfg.AutoRefresh; cfg.Save();
                    lastPower = (PowerLineStatus)(-1); ApplyRefresh();
                });
                addOpt("App Nap: apps sin usar en modo eficiencia (con bateria)", cfg.AppNap, () =>
                {
                    cfg.AppNap = !cfg.AppNap; cfg.Save(); UpdateNap();
                });
            }
            addOpt("Menu de Mac al hacer clic derecho en el escritorio", cfg.DesktopMenu, () => { cfg.DesktopMenu = !cfg.DesktopMenu; cfg.Save(); SetDesktopMenu(cfg.DesktopMenu); });
            addOpt("Botones de Mac en todas las ventanas", cfg.MacButtons, () => { cfg.MacButtons = !cfg.MacButtons; cfg.Save(); SetMacButtons(cfg.MacButtons); });
            addOpt("Abrir Finder en vez del Explorador", cfg.UseFinder, () => { cfg.UseFinder = !cfg.UseFinder; cfg.Save(); });
            addOpt("Spotlight con Alt + Espacio", cfg.SpotlightKey, () =>
            {
                cfg.SpotlightKey = !cfg.SpotlightKey; cfg.Save();
                if (cfg.SpotlightKey) RegisterHotKey(Handle, 7, 0x0001 | 0x4000, 0x20); else UnregisterHotKey(Handle, 7);
            });
            addOpt("Vista rapida con Espacio en el Explorador", cfg.QuickLookExplorer, () =>
            {
                cfg.QuickLookExplorer = !cfg.QuickLookExplorer; cfg.Save();
                if (cfg.QuickLookExplorer) SetKeyHook(true);
                else SetKeyHook(false);
            });
            addOpt("Modo claro de dia, oscuro de noche", cfg.AutoTheme, () =>
            {
                cfg.AutoTheme = !cfg.AutoTheme; cfg.Save(); lookAt = DateTime.MinValue;
            });
            addOpt("Fondo dinamico segun la hora", cfg.DynWall, () =>
            {
                cfg.DynWall = !cfg.DynWall; cfg.Save(); lookAt = DateTime.MinValue;
            });
            addOpt("Barra superior estilo Mac", cfg.MenuBar, () =>
            {
                cfg.MenuBar = !cfg.MenuBar; cfg.Save(); SetMenuBar(cfg.MenuBar);
            });
            addOpt("Escritorio limpio (ocultar iconos)", !DesktopIcons.Visible, () => { DesktopIcons.Toggle(); deskIconsCached = DesktopIcons.Visible; });
            addOpt("Esquinas activas (arriba-izq: ventanas, abajo-izq: escritorio)", cfg.HotCorners, () =>
            {
                cfg.HotCorners = !cfg.HotCorners; cfg.Save();
            });
            addOpt("Reservar espacio (las ventanas no lo tapan)", cfg.ReserveSpace, () =>
            {
                cfg.ReserveSpace = !cfg.ReserveSpace; cfg.Save();
                if (cfg.ReserveSpace) RegisterAppBar(); else UnregisterAppBar();
            });
            addOpt("Editar apps del dock (carpeta)", false, () => Process.Start("explorer.exe", "\"" + appsDir + "\""));
            addOpt("Importar de nuevo apps de la barra", false, () => { ImportTaskbarPins(); ScheduleReload(); });
            if (general)
            {
                Add(m, "Abrir Finder", () => Finder.OpenOrFocus(light, s));
                Add(m, "Buscar con Spotlight (Alt + Espacio)", () => Spotlight.Toggle(light, s));
                if (DockerRunning()) Add(m, "Apagar Docker y WSL (libera RAM y bateria)", () => StopDocker());
                m.Items.Add(opts);
                Add(m, "Cerrar dock y volver a la barra de Windows", () => Close());
            }
            while (m.Items.Count > 0 && m.Items[m.Items.Count - 1] is ToolStripSeparator) m.Items.RemoveAt(m.Items.Count - 1);
            if (m.Items.Count == 0) return;

            menuOpen = true;
            m.Closed += (o, e) => { menuOpen = false; StartAnim(); };
            N.SetForegroundWindow(Handle);
            // En macOS el menu sale centrado encima del icono, justo sobre el dock
            foreach (ToolStripItem ti in m.Items) ti.Padding = new Padding(8, 5, 16, 5);
            m.PerformLayout();
            var ps = m.PreferredSize;
            var scr = (cfg.MultiMonitor ? curScreen : Screen.PrimaryScreen).Bounds;
            int mx, my;
            if (it != null && all.Contains(it))
            {
                float iconTop = winY + SlidePx + BarTop + PadY + Ic - Ic * it.Scale;
                mx = (int)(winX + it.X + it.W / 2 - ps.Width / 2f);
                my = (int)(iconTop - ps.Height - 10 * s);
            }
            else { mx = Cursor.Position.X - ps.Width / 2; my = (int)(winY + SlidePx + BarTop - ps.Height - 10 * s); }
            mx = Math.Max(scr.Left + 4, Math.Min(scr.Right - ps.Width - 4, mx));
            my = Math.Max(scr.Top + 4, my);
            m.Show(new Point(mx, my));
        }

        // ---------- taskbar / appbar ----------
        IEnumerable<IntPtr> Trays()
        {
            var list = new List<IntPtr>();
            IntPtr main = N.FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) list.Add(main);
            N.EnumWindows((h, l) => { if (N.ClassOf(h) == "Shell_SecondaryTrayWnd") list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        void SetupTaskbar()
        {
            if (!cfg.HideTaskbar) return;
            IntPtr mainTray = N.FindWindow("Shell_TrayWnd", null);
            if (mainTray != IntPtr.Zero)
            {
                var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA));
                abd.hWnd = mainTray;
                int state = (int)N.SHAppBarMessage(4, ref abd);
                if (!haveSavedState) { savedAutoHide = (state & 1) != 0; haveSavedState = true; }
                if ((state & 1) == 0) { abd.lParam = new IntPtr(state | 1); N.SHAppBarMessage(10, ref abd); }
            }
            HideTaskbarIfShown();
            EnsureFullWorkingArea();
        }

        void HideTaskbarIfShown()
        {
            if (!cfg.HideTaskbar) return;
            if (DateTime.Now < trayUntil) return;
            IntPtr fg = N.GetForegroundWindow();
            string fc = fg != IntPtr.Zero ? N.ClassOf(fg) : "";
            if (fc == "TopLevelWindowForOverflowXamlIsland" || fc == "NotifyIconOverflowWindow")
                return;
            bool anyHidden = false;
            IntPtr mainTray = N.FindWindow("Shell_TrayWnd", null);
            if (mainTray != IntPtr.Zero && N.IsWindowVisible(mainTray))
            {
                var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA));
                abd.hWnd = mainTray;
                int state = (int)N.SHAppBarMessage(4, ref abd);
                if ((state & 1) == 0) { abd.lParam = new IntPtr(state | 1); N.SHAppBarMessage(10, ref abd); }
            }
            foreach (var t in Trays())
            {
                if (N.IsWindowVisible(t))
                {
                    N.ShowWindow(t, 0);
                    anyHidden = true;
                }
            }
            if (anyHidden || NeedsWorkingAreaFix())
                EnsureFullWorkingArea();
        }

        bool NeedsWorkingAreaFix()
        {
            if (!cfg.HideTaskbar || cfg.ReserveSpace) return false;
            foreach (var scr in Screen.AllScreens)
            {
                if (scr.WorkingArea.Bottom < scr.Bounds.Bottom - 2) return true;
            }
            return false;
        }

        // Si al reiniciar explorer.exe Windows dejo reservados los 48px inferiores de la barra de tareas
        // (mostrando una franja del fondo de pantalla abajo), libera ese espacio en todos los monitores y reajusta las ventanas maximizadas.
        void EnsureFullWorkingArea()
        {
            if (!cfg.HideTaskbar || cfg.ReserveSpace) return;
            try
            {
                bool changed = false;
                foreach (var scr in Screen.AllScreens)
                {
                    var b = scr.Bounds;
                    var wa = scr.WorkingArea;
                    int wantTop = b.Top + (bar != null && (scr.Primary || cfg.MultiMonitor) && bar.ReservesTop(scr) ? bar.BarHeight : 0);
                    if (wa.Bottom < b.Bottom - 2 || Math.Abs(wa.Top - wantTop) > 4)
                    {
                        var rc = new N.RECT { left = b.Left, top = wantTop, right = b.Right, bottom = b.Bottom };
                        N.SpiRect(0x002F, 0, ref rc, 0); // SPI_SETWORKAREA
                        changed = true;
                    }
                }
                if (changed)
                {
                    if (bar != null) bar.ReRegister();
                    N.EnumWindows((h, l) =>
                    {
                        if (N.IsWindowVisible(h) && N.IsZoomed(h))
                        {
                            var scr = Screen.FromHandle(h);
                            var wa = scr.WorkingArea;
                            N.RECT wr;
                            if (N.GetWindowRect(h, out wr) && wr.bottom < scr.Bounds.Bottom - 16)
                            {
                                N.ShowWindow(h, 9); // SW_RESTORE
                                N.ShowWindow(h, 3); // SW_MAXIMIZE
                            }
                        }
                        return true;
                    }, IntPtr.Zero);
                }
            }
            catch { }
        }

        void RegisterAppBar()
        {
            if (appBarOn) return;
            var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA));
            abd.hWnd = Handle; abd.uCallbackMessage = appBarMsg;
            N.SHAppBarMessage(0, ref abd);
            appBarOn = true;
            SetAppBarPos();
        }

        void SetAppBarPos()
        {
            if (!appBarOn) return;
            var b = (cfg.MultiMonitor ? curScreen : Screen.PrimaryScreen).Bounds;
            var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA));
            abd.hWnd = Handle; abd.uEdge = 3;
            abd.rc.left = b.Left; abd.rc.right = b.Right; abd.rc.bottom = b.Bottom;
            abd.rc.top = b.Bottom - (int)Math.Ceiling(BottomM + BarH + 2 * s);
            N.SHAppBarMessage(2, ref abd);
            abd.rc.top = abd.rc.bottom - (int)Math.Ceiling(BottomM + BarH + 2 * s);
            N.SHAppBarMessage(3, ref abd);
        }

        void UnregisterAppBar()
        {
            if (!appBarOn) return;
            var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA)); abd.hWnd = Handle;
            N.SHAppBarMessage(1, ref abd);
            appBarOn = false;
        }

        void CheckFullscreen()
        {
            bool full = FullscreenApp.Active(Handle, cfg.MultiMonitor ? curScreen : Screen.PrimaryScreen);
            if (bar != null) bar.CheckFullscreenState();
            if (full == fullApp) return;
            fullApp = full;
            if (macBtns != null) macBtns.SetPaused(full);
        }

        float SlidePx { get { return slide * (BottomM + BarH + 8 * s); } }
        bool DockShown { get { return !hiddenFull && slideTarget == 0f; } }

        // Esconde el dock deslizandolo hacia abajo (como el "ocultar automaticamente" de Mac).
        void HideDock()
        {
            peeking = false;
            ClosePreview();
            if (hiddenFull || slideTarget == 1f) return;
            StartAnim();
            slideFrom = slide; slideT0 = frameClock.Elapsed.TotalSeconds; slideDur = 0.34; slideTarget = 1f;
        }

        void ShowDock()
        {
            if (hiddenFull)
            {
                hiddenFull = false; slide = 1f;
                N.ShowWindow(Handle, 8);
                N.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            }
            if (slideTarget != 0f) { StartAnim(); slideFrom = slide; slideT0 = frameClock.Elapsed.TotalSeconds; slideDur = 0.28; }
            slideTarget = 0f;
            DoLayout(lastMx, mag);
            StartAnim();
        }

        // Una ventana (que no es el escritorio) toca la zona del dock.
        bool Covered()
        {
            IntPtr fg = N.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle || N.IsIconic(fg) || !N.IsWindowVisible(fg)) return false;
            string c = N.ClassOf(fg);
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" ||
                c == "Windows.UI.Core.CoreWindow" || c == "XamlExplorerHostIslandWindow" || c == "#32768" ||
                c == "TopLevelWindowForOverflowXamlIsland" || c.StartsWith("WindowsForms10")) return false;
            uint pid; N.GetWindowThreadProcessId(fg, out pid);
            if (pid == myPid) return false;
            N.RECT r; N.GetWindowRect(fg, out r);
            float left = winX + all[0].X - PadX, right = winX + all[all.Count - 1].X + all[all.Count - 1].W + PadX;
            float top = winY + BarTop;
            return r.bottom > top && r.top < winY + H && r.right > left && r.left < right;
        }

        // Cada 120 ms: decide si el dock se ve, se esconde, cambia de monitor o se asoma al llevar el mouse al borde inferior.
        IntPtr trayCache;
        void QuickTaskbarCheck()
        {
            if (!cfg.HideTaskbar || DateTime.Now < trayUntil) return;
            if (trayCache == IntPtr.Zero || !N.IsWindowVisible(trayCache) && N.ClassOf(trayCache) != "Shell_TrayWnd") trayCache = N.FindWindow("Shell_TrayWnd", null);
            if (trayCache == IntPtr.Zero || !N.IsWindowVisible(trayCache)) return;
            IntPtr fg = N.GetForegroundWindow(); string fc = fg != IntPtr.Zero ? N.ClassOf(fg) : "";
            // con el menu Inicio o la busqueda abiertos Windows necesita su barra un momento
            if (fc == "Windows.UI.Core.CoreWindow" || fc == "XamlExplorerHostIslandWindow" || fc == "Shell_TrayWnd") return;
            HideTaskbarIfShown();
        }

        void EdgeTick(object sender, EventArgs e)
        {
            if (!restored) try { QuickTaskbarCheck(); } catch { }
            if (restored || all.Count == 0) return;
            if (menuOpen || dragging || stackPopup != null) return;
            if (previewPopup != null && !previewPopup.IsDisposed)
            {
                if (previewPopup.IsMouseInsideOrBridge(Cursor.Position)) return;
                if (hoverIdx < 0) ClosePreview();
            }
            Point c = Cursor.Position;
            var mouseScr = Screen.FromPoint(c);
            if (cfg.MultiMonitor && Screen.AllScreens.Length > 1 && mouseScr.DeviceName != curScreen.DeviceName)
            {
                var mb = mouseScr.Bounds;
                if (c.Y >= mb.Bottom - 4 && c.X >= mb.Left + mb.Width * 0.15f && c.X <= mb.Right - mb.Width * 0.15f && !FullscreenApp.Active(Handle, mouseScr))
                {
                    curScreen = mouseScr;
                    Relayout();
                    if (appBarOn) SetAppBarPos();
                    peeking = true; leaveSince = DateTime.MaxValue;
                    ShowDock();
                    return;
                }
            }

            if (cfg.HotCorners && !fullApp) corners.Tick(cfg.MultiMonitor ? mouseScr : Screen.PrimaryScreen, bar != null);
            bool hide = fullApp || (cfg.AutoHide && !cfg.ReserveSpace && Covered());
            var b = (cfg.MultiMonitor ? curScreen : Screen.PrimaryScreen).Bounds;
            bool inX = c.X >= winX && c.X <= winX + W;
            float zoneTop = winY + BarTop - (mag > 0.05f ? Ic * (MaxMag - 1f) : 0f) - 10 * s;
            bool overZone = inX && c.Y >= zoneTop && c.Y <= b.Bottom + 4;

            if (!hide)
            {
                peeking = false;
                if (!DockShown) ShowDock();
                return;
            }
            if (!DockShown)
            {
                if (inX && c.Y >= b.Bottom - 2) { peeking = true; leaveSince = DateTime.MaxValue; ShowDock(); }
                return;
            }
            if (overZone) { peeking = true; leaveSince = DateTime.MaxValue; return; }
            if (leaveSince == DateTime.MaxValue) { leaveSince = DateTime.Now; return; }
            if ((DateTime.Now - leaveSince).TotalMilliseconds > (peeking ? 600 : 0)) { leaveSince = DateTime.MaxValue; HideDock(); }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 7) { Spotlight.Toggle(light, s); return; }
            if (m.Msg == 0x0312 && m.WParam.ToInt32() >= 30 && m.WParam.ToInt32() < 30 + tileVk.Length)
            {
                IntPtr fg = N.GetForegroundWindow(); string fc = fg == IntPtr.Zero ? "" : N.ClassOf(fg);
                if (fg != IntPtr.Zero && fg != Handle && fc != "Progman" && fc != "WorkerW" && fc != "Shell_TrayWnd") MenuBar.TileWindow(fg, tileMode[m.WParam.ToInt32() - 30]);
                return;
            }
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 8)
            {
                if (QuickLook.IsOpenAndFocused()) QuickLook.Toggle(null, light);
                else QuickLookExplorer(N.GetForegroundWindow());
                return;
            }
            if (msgFinder != 0 && m.Msg == msgFinder) { Finder.OpenOrFocus(light, s); return; }
            if (msgSpot != 0 && m.Msg == msgSpot) { Spotlight.Toggle(light, s); return; }
            if (m.Msg == appBarMsg && appBarMsg != 0)
            {
                if (m.WParam.ToInt32() == 1) SetAppBarPos();
            }
            else if (m.Msg == taskbarCreatedMsg && taskbarCreatedMsg != 0)
            {
                BeginInvoke(new Action(() =>
                {
                    SetupTaskbar();
                    if (appBarOn) { appBarOn = false; RegisterAppBar(); }
                    if (bar != null) bar.ReRegister();
                    foreach (int delay in new[] { 700, 1800, 3200 })
                    {
                        var t = new System.Windows.Forms.Timer { Interval = delay };
                        t.Tick += (s2, a2) => { t.Stop(); t.Dispose(); if (!restored) { SetupTaskbar(); if (bar != null) bar.ReRegister(); } };
                        t.Start();
                    }
                }));
            }
            else if (m.Msg == 0x0011 || m.Msg == 0x0016)
            {
                SafeRestore();
            }
            base.WndProc(ref m);
        }

        public void SafeRestore()
        {
            if (restored) return;
            restored = true;
            try { slow.Stop(); fast.Stop(); fast.Close(); edge.Stop(); } catch { }
            if (loweredRefresh) RestoreRefresh();
            try { nap.WakeAll(); } catch { }
            try { StopMacFeatures(); } catch { }
            try { EdgeTiler.Stop(); } catch { }
            try { if (liveDragChanged) SetLiveDrag(false); } catch { }
            try { SetShake(false); SetTileKeys(false); global::MacDock.Genie.RestoreAnimOnExit(); } catch { }
            try { if (bar != null) { bar.Close(); bar = null; } } catch { }
            try { UnregisterAppBar(); } catch { }
            try
            {
                if (haveSavedState && !savedAutoHide)
                {
                    var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA));
                    abd.hWnd = N.FindWindow("Shell_TrayWnd", null);
                    int state = (int)N.SHAppBarMessage(4, ref abd);
                    abd.lParam = new IntPtr(state & ~1); N.SHAppBarMessage(10, ref abd);
                }
            }
            catch { }
            try { foreach (var t in Trays()) N.ShowWindow(t, 5); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SafeRestore();
            base.OnFormClosing(e);
        }
    }

    // Vista previa en miniatura en vivo (DWM) al pasar el mouse por una app abierta en el Dock.
    class DockPreview : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public N.RECT rcDestination;
            public N.RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out N.SIZE size);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        class WinSlot
        {
            public IntPtr Hwnd;
            public IntPtr Thumb;
            public string Title;
            public Rectangle CardRect;
            public Rectangle CloseRect;
            public bool FallbackIcon;
        }

        float s; bool light;
        DockItem item;
        List<WinSlot> slots = new List<WinSlot>();
        int hoverIdx = -1; bool hoverClose = false;
        Action<IntPtr> onFocus;
        Action onChanged;

        public DockPreview(float scale, bool lightTheme, DockItem dockItem, Point anchorBottomCenter, Rectangle screen, Action<IntPtr> focusAction, Action changedAction)
        {
            s = scale; light = lightTheme; item = dockItem; onFocus = focusAction; onChanged = changedAction;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; DoubleBuffered = true;
            BackColor = light ? Color.FromArgb(244, 244, 248) : Color.FromArgb(34, 34, 40);

            var wins = item.Windows.Where(win => N.IsWindowVisible(win)).Take(4).ToList();
            if (wins.Count == 0) wins = item.Windows.Take(4).ToList();
            int cardW = (int)(192 * s), cardH = (int)(136 * s), pad = (int)(10 * s), gap = (int)(8 * s);
            int n = Math.Max(1, wins.Count);
            int w = pad * 2 + n * cardW + (n - 1) * gap;
            int h = pad * 2 + cardH;

            for (int i = 0; i < wins.Count; i++)
            {
                int cx = pad + i * (cardW + gap);
                var cr = new Rectangle(cx, pad, cardW, cardH);
                string t = N.TitleOf(wins[i]);
                if (string.IsNullOrEmpty(t)) t = item.Name;
                slots.Add(new WinSlot
                {
                    Hwnd = wins[i],
                    Title = t,
                    CardRect = cr,
                    CloseRect = new Rectangle(cr.Right - (int)(24 * s), cr.Top + (int)(5 * s), (int)(18 * s), (int)(18 * s))
                });
            }

            int x = Math.Max(screen.Left + 8, Math.Min(screen.Right - w - 8, anchorBottomCenter.X - w / 2));
            int y = Math.Max(screen.Top + 8, anchorBottomCenter.Y - h);
            Bounds = new Rectangle(x, y, w, h);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x8000000; cp.ClassStyle |= 0x20000; return cp; }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        public bool IsMouseInsideOrBridge(Point screenPt)
        {
            if (IsDisposed) return false;
            var r = Bounds;
            r.Inflate((int)(12 * s), (int)(36 * s));
            return r.Contains(screenPt);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
                int dark = light ? 0 : 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                int border = light ? 0x00CFCFD6 : 0x00484852; DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RegisterThumbnails();
        }

        void RegisterThumbnails()
        {
            foreach (var sl in slots)
            {
                IntPtr th;
                if (DwmRegisterThumbnail(Handle, sl.Hwnd, out th) == 0 && th != IntPtr.Zero)
                {
                    sl.Thumb = th;
                    N.SIZE srcSz;
                    if (DwmQueryThumbnailSourceSize(th, out srcSz) == 0 && srcSz.cx > 20 && srcSz.cy > 20)
                    {
                        var box = new Rectangle(sl.CardRect.X + (int)(8 * s), sl.CardRect.Y + (int)(26 * s), sl.CardRect.Width - (int)(16 * s), sl.CardRect.Height - (int)(34 * s));
                        float k = Math.Min((float)box.Width / srcSz.cx, (float)box.Height / srcSz.cy);
                        int tw = Math.Max(1, (int)(srcSz.cx * k)), thH = Math.Max(1, (int)(srcSz.cy * k));
                        int tx = box.X + (box.Width - tw) / 2, ty = box.Y + (box.Height - thH) / 2;
                        var pr = new DWM_THUMBNAIL_PROPERTIES
                        {
                            dwFlags = 0x1 | 0x4 | 0x8, // DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY | DWM_TNP_VISIBLE
                            rcDestination = new N.RECT { left = tx, top = ty, right = tx + tw, bottom = ty + thH },
                            opacity = 255,
                            fVisible = true,
                            fSourceClientAreaOnly = false
                        };
                        DwmUpdateThumbnailProperties(th, ref pr);
                    }
                    else sl.FallbackIcon = true;
                }
                else sl.FallbackIcon = true;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            Color fg = light ? Color.FromArgb(24, 24, 28) : Color.FromArgb(238, 238, 244);
            Color hovBg = light ? Color.FromArgb(224, 224, 232) : Color.FromArgb(54, 54, 64);
            Color cardBg = light ? Color.FromArgb(234, 234, 240) : Color.FromArgb(42, 42, 50);

            using (var f = new Font(UiFont.Semi, 11.5f * s, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fgB = new SolidBrush(fg))
            {
                var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
                for (int i = 0; i < slots.Count; i++)
                {
                    var sl = slots[i];
                    bool on = i == hoverIdx;
                    using (var p = Round(sl.CardRect, 8 * s))
                    using (var b = new SolidBrush(on ? hovBg : cardBg))
                        g.FillPath(b, p);

                    var tr = new RectangleF(sl.CardRect.X + 8 * s, sl.CardRect.Y + 4 * s, sl.CardRect.Width - (on ? 32 * s : 16 * s), 20 * s);
                    g.DrawString(sl.Title, f, fgB, tr, sf);

                    if (on)
                    {
                        using (var cb = new SolidBrush(hoverClose ? Color.FromArgb(255, 69, 58) : Color.FromArgb(120, 120, 130)))
                            g.FillEllipse(cb, sl.CloseRect);
                        using (var pen = new Pen(Color.White, 1.5f * s))
                        {
                            float cx = sl.CloseRect.X + sl.CloseRect.Width / 2f, cy = sl.CloseRect.Y + sl.CloseRect.Height / 2f, d = 3.2f * s;
                            g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                            g.DrawLine(pen, cx - d, cy + d, cx + d, cy - d);
                        }
                    }

                    if (sl.FallbackIcon && item.Icon != null)
                    {
                        float isz = 54 * s;
                        g.DrawImage(item.Icon, sl.CardRect.X + (sl.CardRect.Width - isz) / 2f, sl.CardRect.Y + 26 * s + (sl.CardRect.Height - 26 * s - isz) / 2f, isz, isz);
                    }
                }
            }
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hi = -1; bool hc = false;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].CardRect.Contains(e.Location))
                {
                    hi = i;
                    hc = slots[i].CloseRect.Contains(e.Location);
                    break;
                }
            }
            if (hi != hoverIdx || hc != hoverClose) { hoverIdx = hi; hoverClose = hc; Invalidate(); }
            Cursor = hi >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverIdx != -1) { hoverIdx = -1; hoverClose = false; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].CardRect.Contains(e.Location))
                {
                    IntPtr h = slots[i].Hwnd;
                    if (slots[i].CloseRect.Contains(e.Location))
                    {
                        N.PostMessage(h, 0x10, IntPtr.Zero, IntPtr.Zero);
                        Close();
                        if (onChanged != null) onChanged();
                    }
                    else
                    {
                        Close();
                        if (onFocus != null) onFocus(h);
                    }
                    return;
                }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (var sl in slots)
                if (sl.Thumb != IntPtr.Zero) { try { DwmUnregisterThumbnail(sl.Thumb); } catch { } sl.Thumb = IntPtr.Zero; }
            base.OnFormClosed(e);
        }
    }
}
