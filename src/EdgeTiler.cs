using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDock
{
    // Acomodo de ventanas como macOS: al arrastrar una ventana a un borde aparece un marco translucido
    // con la zona destino y, al soltarla, la ventana se desliza a su lugar. Al volver a arrastrarla recupera su tamano.
    //   borde izquierdo/derecho = mitad   esquinas = cuarto   borde superior = llenar la pantalla
    static class EdgeTiler
    {
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(int a, int u, ref int p, int f);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] static extern bool SpiSet(int a, int u, IntPtr p, int f);

        static WinEventProc proc; static IntPtr hook;
        static IntPtr dragWnd; static N.RECT startRect; static int zone;
        public static int Zone { get { return zone; } }   // zona de acomodo activa (0 = ninguna)
        static System.Windows.Forms.Timer track;
        static TilePreview preview;
        static readonly Dictionary<IntPtr, Size> preTile = new Dictionary<IntPtr, Size>();
        static bool snapWasOn, running;
        static float scale = 1f;

        public static void Start(float s)
        {
            if (running) return;
            running = true; scale = s;
            // se apaga el Snap de Windows solo mientras MacDock corre (sin guardarlo en el registro), para que no compitan
            int on = 1; SystemParametersInfo(0x0082, 0, ref on, 0); snapWasOn = on != 0;
            if (snapWasOn) SpiSet(0x0083, 0, IntPtr.Zero, 0);
            proc = OnEvent;
            hook = SetWinEventHook(0x000A, 0x000B, IntPtr.Zero, proc, 0, 0, 0x0002);   // MOVESIZESTART/END, sin el propio proceso
            track = new System.Windows.Forms.Timer { Interval = 15 };
            track.Tick += (o, e) => Track();
        }

        public static void Stop()
        {
            if (!running) return;
            running = false;
            try { if (hook != IntPtr.Zero) UnhookWinEvent(hook); } catch { }
            hook = IntPtr.Zero;
            if (track != null) { track.Stop(); track.Dispose(); track = null; }
            if (preview != null) { preview.Close(); preview.Dispose(); preview = null; }
            if (snapWasOn) SpiSet(0x0083, 1, IntPtr.Zero, 0);
        }

        // para el arrastre propio de MacDock (que no pasa por el movimiento de Windows)
        public static void BeginExternal(IntPtr h) { if (running) try { Begin(h); } catch (Exception ex) { Program.Log(ex); } }
        public static void EndExternal(IntPtr h) { if (running) try { End(h); } catch (Exception ex) { Program.Log(ex); } }

        static void OnEvent(IntPtr hk, uint ev, IntPtr h, int obj, int child, uint thread, uint time)
        {
            try
            {
                if (obj != 0 || h == IntPtr.Zero) return;
                if (ev == 0x000A) Begin(h);
                else if (!Wobbly.Owns(h)) End(h);   // con la gelatina, el fin real lo avisa ella al soltar
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        static bool Tileable(IntPtr h)
        {
            int style = N.GetWindowLong(h, -16);
            return (style & 0x00C00000) == 0x00C00000 && (style & 0x00040000) != 0 && !N.IsZoomed(h) && !N.IsIconic(h);
        }

        static void Begin(IntPtr h)
        {
            if (!Tileable(h)) return;
            dragWnd = h; zone = 0;
            N.GetWindowRect(h, out startRect);
            // una ventana acomodada recupera su tamano anterior al arrastrarla, como en macOS
            Size old;
            if (preTile.TryGetValue(h, out old))
            {
                preTile.Remove(h);
                int off = Cursor.Position.X - startRect.left;
                if (off < old.Width - 40)
                {
                    N.SetWindowPos(h, IntPtr.Zero, 0, 0, old.Width, old.Height, 0x0002 | 0x0004 | 0x0010);
                    N.GetWindowRect(h, out startRect);
                }
            }
            track.Start();
        }

        static void End(IntPtr h)
        {
            if (h != dragWnd) return;
            track.Stop();
            int z = zone; zone = 0; dragWnd = IntPtr.Zero;
            if (preview != null) preview.FadeOut();
            if (z == 0) return;
            N.RECT r; N.GetWindowRect(h, out r);
            if (r.right - r.left != startRect.right - startRect.left || r.bottom - r.top != startRect.bottom - startRect.top) return;   // fue redimension, no movimiento
            var scr = Screen.FromPoint(Cursor.Position);
            var dst = ZoneRect(scr, z);
            if (!preTile.ContainsKey(h)) preTile[h] = new Size(startRect.right - startRect.left, startRect.bottom - startRect.top);
            MenuBar.AnimateBounds(h, dst.X, dst.Y, dst.Width, dst.Height);
        }

        static void Track()
        {
            if (dragWnd == IntPtr.Zero) { track.Stop(); return; }
            N.RECT r; N.GetWindowRect(dragWnd, out r);
            bool wob = Wobbly.Owns(dragWnd);
            bool moved = wob || r.left != startRect.left || r.top != startRect.top;
            bool sameSize = wob || r.right - r.left == startRect.right - startRect.left && r.bottom - r.top == startRect.bottom - startRect.top;
            int z = 0;
            if (moved && sameSize)
            {
                var c = Cursor.Position; var b = Screen.FromPoint(c).Bounds;
                int corner = (int)(90 * scale);
                bool left = c.X <= b.Left + 1, right = c.X >= b.Right - 2, top = c.Y <= b.Top + 1;
                if (left || right)
                {
                    if (c.Y < b.Top + corner) z = left ? 5 : 6;
                    else if (c.Y > b.Bottom - corner) z = left ? 7 : 8;
                    else z = left ? 1 : 2;
                }
                else if (top) z = 10;
            }
            if (z == zone) return;
            zone = z;
            if (z == 0) { if (preview != null) preview.FadeOut(); return; }
            if (preview == null || preview.IsDisposed) preview = new TilePreview();
            var from = new Rectangle(r.left, r.top, r.right - r.left, r.bottom - r.top);
            preview.ShowAt(from, ZoneRect(Screen.FromPoint(Cursor.Position), z), scale);
        }

        // zonas con el pequeno margen que deja macOS entre ventanas acomodadas
        public static Rectangle ZoneRect(Screen scr, int mode)
        {
            var wa = scr.WorkingArea; int g = (int)Math.Round(6 * scale);
            int x = wa.Left + g, y = wa.Top + g, w = wa.Width - g * 2, h = wa.Height - g * 2;
            int hw = (w - g) / 2, hh = (h - g) / 2;
            switch (mode)
            {
                case 1: return new Rectangle(x, y, hw, h);
                case 2: return new Rectangle(x + hw + g, y, w - hw - g, h);
                case 5: return new Rectangle(x, y, hw, hh);
                case 6: return new Rectangle(x + hw + g, y, w - hw - g, hh);
                case 7: return new Rectangle(x, y + hh + g, hw, h - hh - g);
                case 8: return new Rectangle(x + hw + g, y + hh + g, w - hw - g, h - hh - g);
                default: return new Rectangle(x, y, w, h);
            }
        }
    }

    // Marco translucido redondeado que muestra donde quedara la ventana (se anima de la ventana a la zona)
    class TilePreview : Form
    {
        LayeredCanvas canvas; VsyncTimer anim; Stopwatch clk = Stopwatch.StartNew();
        RectangleF from, to, cur; double t0; float alpha, alphaFrom, alphaTo; float s = 1f;
        const double Dur = 0.2;

        public TilePreview()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(0, 0, 1, 1);
            anim = new VsyncTimer(this); anim.Tick += (o, e) => Frame();
        }

        protected override CreateParams CreateParams
        {
            // layered + transparente al mouse + sin activar + herramienta + siempre encima
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x8000000 | 0x80 | 0x8; return cp; }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        public void ShowAt(Rectangle start, Rectangle target, float scale)
        {
            s = scale;
            from = alpha > 0.05f ? cur : (RectangleF)start; to = target;
            alphaFrom = alpha; alphaTo = 1f; t0 = clk.Elapsed.TotalSeconds;
            if (!Visible) { Show(); N.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); }
            anim.Start();
        }

        public void FadeOut()
        {
            if (IsDisposed || alpha <= 0f) return;
            from = cur; to = cur; alphaFrom = alpha; alphaTo = 0f; t0 = clk.Elapsed.TotalSeconds;
            anim.Start();
        }

        void Frame()
        {
            double p = (clk.Elapsed.TotalSeconds - t0) / Dur;
            float k = Ease.OutCubic(p);
            cur = new RectangleF(from.X + (to.X - from.X) * k, from.Y + (to.Y - from.Y) * k, from.Width + (to.Width - from.Width) * k, from.Height + (to.Height - from.Height) * k);
            alpha = alphaFrom + (alphaTo - alphaFrom) * Ease.OutCubic(p * 1.3);
            Draw();
            if (p >= 1)
            {
                anim.Stop();
                if (alphaTo <= 0f) { alpha = 0f; Hide(); if (canvas != null) { canvas.Dispose(); canvas = null; } }
            }
        }

        void Draw()
        {
            int w = Math.Max(2, (int)Math.Ceiling(cur.Width)), h = Math.Max(2, (int)Math.Ceiling(cur.Height));
            if (canvas == null || canvas.Width != w || canvas.Height != h) { if (canvas != null) canvas.Dispose(); canvas = new LayeredCanvas(w, h); }
            using (var g = Graphics.FromImage(canvas.Bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float rad = 12 * s, lw = Math.Max(1f, 1.2f * s);
                var r = new RectangleF(lw, lw, w - lw * 2, h - lw * 2);
                using (var p = Round(r, rad))
                {
                    using (var b = new SolidBrush(Color.FromArgb((int)(70 * alpha), 220, 226, 240))) g.FillPath(b, p);
                    using (var pen = new Pen(Color.FromArgb((int)(170 * alpha), 255, 255, 255), lw)) g.DrawPath(pen, p);
                }
            }
            canvas.Push(Handle, (int)Math.Round(cur.X), (int)Math.Round(cur.Y));
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { anim.Close(); if (canvas != null) { canvas.Dispose(); canvas = null; } }
            base.Dispose(disposing);
        }
    }
}
