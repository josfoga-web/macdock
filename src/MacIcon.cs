using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MacDock
{
    // Convierte los iconos de Windows en iconos estilo macOS: superelipse (squircle),
    // sombra suave, brillo superior y borde fino. Todo dibujado por codigo.
    static class MacIcon
    {
        // Proporciones de la plantilla de iconos de macOS (lienzo 1024, cuerpo 824)
        const float Body = 0.86f, Lift = 0.012f;
        public const float BodyRatio = Body, LiftRatio = Lift;

        // solo la sombra, para iconos que se dibujan en vivo (el reloj)
        public static Bitmap ShadowOnly(int px)
        {
            int bs = (int)Math.Round(px * Body);
            return Shadow(px, bs, (px - bs) / 2f, (px - bs) / 2f - px * Lift);
        }

        public static Bitmap Tile(Bitmap src, int px)
        {
            if (src == null) return null;
            try
            {
                int bs = (int)Math.Round(px * Body);
                var norm = Normalize(src, px);
                int[] p = Pixels(norm);
                Rectangle bb; float cover; Analyze(p, px, out bb, out cover);
                if (bb.Width <= 0) { norm.Dispose(); return src; }

                bool tile = cover > 0.86f && Math.Abs(bb.Width - bb.Height) < Math.Max(bb.Width, bb.Height) * 0.08f;
                var body = new Bitmap(bs, bs, PixelFormat.Format32bppArgb);
                using (var g = Hq(Graphics.FromImage(body)))
                {
                    if (tile)
                    {
                        // ya es un cuadrado lleno (apps UWP, iconos tipo tile): se recorta el borde y se llena el squircle
                        float ins = bb.Width * 0.03f;
                        g.DrawImage(norm, new RectangleF(-0.5f, -0.5f, bs + 1, bs + 1), new RectangleF(bb.X + ins, bb.Y + ins, bb.Width - ins * 2, bb.Height - ins * 2), GraphicsUnit.Pixel);
                    }
                    else
                    {
                        Color top, bot; Palette(p, out top, out bot);
                        using (var br = new LinearGradientBrush(new Rectangle(0, -1, bs, bs + 2), top, bot, 90f)) g.FillRectangle(br, 0, 0, bs, bs);
                        float fit = bs * 0.68f, k = fit / Math.Max(bb.Width, bb.Height);
                        float w = bb.Width * k, h = bb.Height * k;
                        var dst = new RectangleF((bs - w) / 2f, (bs - h) / 2f, w, h);
                        // sombra del glifo para dar volumen
                        using (var gs = GlyphShadow(norm, bb, dst, bs)) g.DrawImage(gs, 0, bs * 0.012f);
                        g.DrawImage(norm, dst, bb, GraphicsUnit.Pixel);
                    }
                    // luz superior muy sutil
                    using (var gl = new LinearGradientBrush(new RectangleF(0, 0, bs, bs * 0.55f), Color.FromArgb(tile ? 22 : 40, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                        g.FillRectangle(gl, 0, 0, bs, bs * 0.55f - 1);
                }
                norm.Dispose();

                using (var path = Squircle(new RectangleF(0, 0, bs, bs)))
                using (var mask = Mask(path, bs)) ApplyMask(body, mask);

                var outp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
                float ox = (px - bs) / 2f, oy = (px - bs) / 2f - px * Lift;
                using (var g = Hq(Graphics.FromImage(outp)))
                {
                    using (var sh = Shadow(px, bs, ox, oy)) g.DrawImage(sh, 0, 0);
                    g.DrawImage(body, ox, oy, bs, bs);
                    float lw = Math.Max(1f, px / 256f);
                    using (var path = Squircle(new RectangleF(ox + lw / 2, oy + lw / 2, bs - lw, bs - lw)))
                    {
                        using (var pen = new Pen(Color.FromArgb(38, 0, 0, 0), lw)) g.DrawPath(pen, path);
                    }
                    using (var path = Squircle(new RectangleF(ox + lw * 1.5f, oy + lw * 1.5f, bs - lw * 3, bs - lw * 3)))
                    using (var pen = new Pen(new LinearGradientBrush(new RectangleF(ox, oy - 1, bs, bs + 2), Color.FromArgb(70, 255, 255, 255), Color.FromArgb(8, 255, 255, 255), 90f), lw))
                        g.DrawPath(pen, path);
                }
                body.Dispose();
                return outp;
            }
            catch { return src; }
        }

        // Explorador de archivos: placa azul con una carpeta blanca
        public static Bitmap Explorer(int px)
        {
            using (var raw = new Bitmap(256, 256, PixelFormat.Format32bppArgb))
            {
                using (var g = Hq(Graphics.FromImage(raw)))
                {
                    using (var b = new LinearGradientBrush(new Rectangle(0, -1, 256, 258), Color.FromArgb(104, 196, 255), Color.FromArgb(22, 110, 236), 90f)) g.FillRectangle(b, 0, 0, 256, 256);
                    using (var back = Rr(new RectangleF(52, 74, 152, 112), 14))
                    using (var tab = Rr(new RectangleF(52, 64, 62, 26), 9))
                    using (var front = Rr(new RectangleF(52, 94, 152, 100), 14))
                    {
                        using (var sh = new SolidBrush(Color.FromArgb(55, 0, 40, 110)))
                        using (var m = new Matrix()) { var f2 = (GraphicsPath)front.Clone(); m.Translate(0, 6); f2.Transform(m); g.FillPath(sh, f2); f2.Dispose(); }
                        using (var b = new SolidBrush(Color.FromArgb(205, 232, 255))) { g.FillPath(b, back); g.FillPath(b, tab); }
                        using (var b = new LinearGradientBrush(new RectangleF(52, 93, 152, 102), Color.White, Color.FromArgb(232, 243, 255), 90f)) g.FillPath(b, front);
                    }
                }
                return Tile(raw, px);
            }
        }

        // Papelera translucida (vacia o con papeles)
        public static Bitmap Trash(int px, bool full)
        {
            var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using (var g = Hq(Graphics.FromImage(bmp)))
            {
                float u = px / 100f;
                float topY = 17 * u, botY = 92 * u, tw = 66 * u, bw = 54 * u, cx = 50 * u;
                var body = new GraphicsPath();
                body.AddLines(new[] { new PointF(cx - tw / 2, topY + 4 * u), new PointF(cx + tw / 2, topY + 4 * u), new PointF(cx + bw / 2, botY - 4 * u) });
                body.AddArc(cx + bw / 2 - 8 * u, botY - 8 * u, 8 * u, 8 * u, 0, 90);
                body.AddArc(cx - bw / 2, botY - 8 * u, 8 * u, 8 * u, 90, 90);
                body.CloseFigure();
                // sombra
                using (var sh = new Bitmap(px, px, PixelFormat.Format32bppArgb))
                {
                    using (var gs = Hq(Graphics.FromImage(sh))) using (var b = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) gs.FillEllipse(b, cx - bw / 2, botY - 4 * u, bw, 8 * u);
                    Blur(sh, Math.Max(2, (int)(2.5f * u)));
                    g.DrawImage(sh, 0, 0);
                }
                if (full)
                {
                    using (var pb = new SolidBrush(Color.FromArgb(250, 250, 252)))
                    using (var pp = new Pen(Color.FromArgb(70, 0, 0, 0), Math.Max(1f, 0.6f * u)))
                    {
                        var blobs = new[] { new RectangleF(cx - 26 * u, topY - 9 * u, 24 * u, 20 * u), new RectangleF(cx - 6 * u, topY - 13 * u, 26 * u, 24 * u), new RectangleF(cx + 8 * u, topY - 6 * u, 20 * u, 17 * u) };
                        foreach (var r in blobs) { using (var bp = Crumple(r)) { g.FillPath(pb, bp); g.DrawPath(pp, bp); } }
                    }
                }
                // interior oscuro
                using (var b = new SolidBrush(Color.FromArgb(60, 30, 30, 36))) g.FillEllipse(b, cx - tw / 2 + 2 * u, topY, tw - 4 * u, 9 * u);
                // cuerpo esmerilado
                using (var b = new LinearGradientBrush(new RectangleF(cx - tw / 2, 0, tw, px), Color.FromArgb(215, 214, 218, 226), Color.FromArgb(215, 246, 247, 250), 0f))
                {
                    var cb = new ColorBlend { Positions = new[] { 0f, 0.35f, 0.65f, 1f }, Colors = new[] { Color.FromArgb(225, 196, 198, 208), Color.FromArgb(215, 250, 250, 252), Color.FromArgb(215, 236, 237, 242), Color.FromArgb(225, 188, 190, 200) } };
                    b.InterpolationColors = cb; g.FillPath(b, body);
                }
                // nervaduras verticales
                using (var pen = new Pen(Color.FromArgb(70, 90, 94, 108), Math.Max(1f, 1.1f * u)))
                    for (int i = 1; i < 8; i++)
                    {
                        float t = i / 8f;
                        g.DrawLine(pen, cx - tw / 2 + tw * t, topY + 9 * u, cx - bw / 2 + bw * t, botY - 5 * u);
                    }
                using (var pen = new Pen(Color.FromArgb(120, 110, 112, 124), Math.Max(1f, 0.8f * u))) g.DrawPath(pen, body);
                // aro superior
                using (var rim = Rr(new RectangleF(cx - tw / 2 - 2 * u, topY - 1 * u, tw + 4 * u, 9 * u), 4.5f * u))
                {
                    using (var b = new LinearGradientBrush(new RectangleF(0, topY - 2 * u, px, 11 * u), Color.FromArgb(250, 252, 252, 254), Color.FromArgb(240, 200, 202, 212), 90f)) g.FillPath(b, rim);
                    using (var pen = new Pen(Color.FromArgb(120, 110, 112, 124), Math.Max(1f, 0.8f * u))) g.DrawPath(pen, rim);
                }
                body.Dispose();
            }
            return bmp;
        }

        static GraphicsPath Crumple(RectangleF r)
        {
            var pts = new PointF[9]; var rnd = new Random((int)(r.X * 7 + r.Y * 13));
            for (int i = 0; i < pts.Length; i++)
            {
                double a = Math.PI * 2 * i / pts.Length, k = 0.78 + rnd.NextDouble() * 0.22;
                pts[i] = new PointF(r.X + r.Width / 2 + (float)(Math.Cos(a) * r.Width / 2 * k), r.Y + r.Height / 2 + (float)(Math.Sin(a) * r.Height / 2 * k));
            }
            var p = new GraphicsPath(); p.AddClosedCurve(pts, 0.35f); return p;
        }

        // Carpeta azul estilo Mac con un glifo grabado (0 = ninguno, 1 = descargas, 2 = escritorio)
        public static Bitmap Folder(int px, int glyph)
        {
            var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using (var g = Hq(Graphics.FromImage(bmp)))
            {
                float u = px / 100f;
                using (var sh = new Bitmap(px, px, PixelFormat.Format32bppArgb))
                {
                    using (var gs = Hq(Graphics.FromImage(sh)))
                    using (var pth = Rr(new RectangleF(8 * u, 24 * u, 84 * u, 64 * u), 7 * u))
                    using (var b = new SolidBrush(Color.FromArgb(120, 0, 30, 80))) gs.FillPath(b, pth);
                    Blur(sh, (int)Math.Max(2, 3 * u));
                    g.DrawImage(sh, 0, 1.5f * u);
                }
                using (var back = Rr(new RectangleF(8 * u, 17 * u, 84 * u, 68 * u), 7 * u))
                using (var tab = Rr(new RectangleF(8 * u, 12 * u, 34 * u, 14 * u), 5 * u))
                using (var b = new LinearGradientBrush(new RectangleF(0, 12 * u, px, 72 * u), Color.FromArgb(70, 160, 240), Color.FromArgb(38, 118, 214), 90f))
                { g.FillPath(b, back); g.FillPath(b, tab); }
                var fr = new RectangleF(8 * u, 27 * u, 84 * u, 61 * u);
                using (var front = Rr(fr, 7 * u))
                {
                    using (var b = new LinearGradientBrush(new RectangleF(fr.X, fr.Y - 1, fr.Width, fr.Height + 2), Color.FromArgb(128, 202, 255), Color.FromArgb(78, 164, 247), 90f)) g.FillPath(b, front);
                    using (var pen = new Pen(Color.FromArgb(110, 255, 255, 255), Math.Max(1f, 0.8f * u))) g.DrawLine(pen, fr.X + 6 * u, fr.Y + 0.6f * u, fr.Right - 6 * u, fr.Y + 0.6f * u);
                }
                if (glyph > 0)
                {
                    float cx = 50 * u, cy = 58 * u;
                    using (var gp = new GraphicsPath())
                    {
                        if (glyph == 1)
                        {
                            gp.AddEllipse(cx - 17 * u, cy - 17 * u, 34 * u, 34 * u);
                            var arrow = new GraphicsPath();
                            arrow.AddPolygon(new[] { new PointF(cx - 3.2f * u, cy - 10 * u), new PointF(cx + 3.2f * u, cy - 10 * u), new PointF(cx + 3.2f * u, cy + 1 * u), new PointF(cx + 8 * u, cy + 1 * u),
                                new PointF(cx, cy + 10 * u), new PointF(cx - 8 * u, cy + 1 * u), new PointF(cx - 3.2f * u, cy + 1 * u) });
                            gp.AddPath(arrow, false); gp.FillMode = FillMode.Alternate;
                        }
                        else
                        {
                            using (var m = Rr(new RectangleF(cx - 18 * u, cy - 13 * u, 36 * u, 23 * u), 3 * u)) gp.AddPath(m, false);
                            gp.AddRectangle(new RectangleF(cx - 15 * u, cy - 10 * u, 30 * u, 17 * u));
                            gp.AddRectangle(new RectangleF(cx - 7 * u, cy + 12 * u, 14 * u, 2.5f * u));
                            gp.FillMode = FillMode.Alternate;
                        }
                        using (var m = new Matrix()) { m.Translate(0, 0.9f * u); gp.Transform(m); }
                        using (var b = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillPath(b, gp);
                        using (var m = new Matrix()) { m.Translate(0, -0.9f * u); gp.Transform(m); }
                        using (var b = new SolidBrush(Color.FromArgb(150, 30, 100, 190))) g.FillPath(b, gp);
                    }
                }
            }
            return bmp;
        }

        // ---------- forma ----------
        public static GraphicsPath Squircle(RectangleF r)
        {
            // superelipse |x|^n + |y|^n = 1 con n = 5, muy cercana a la forma de los iconos de macOS
            const int N = 160; const double n = 5.0;
            var pts = new PointF[N];
            float a = r.Width / 2f, b = r.Height / 2f, cx = r.X + a, cy = r.Y + b;
            for (int i = 0; i < N; i++)
            {
                double t = 2 * Math.PI * i / N, c = Math.Cos(t), s = Math.Sin(t);
                pts[i] = new PointF(cx + (float)(a * Math.Sign(c) * Math.Pow(Math.Abs(c), 2 / n)), cy + (float)(b * Math.Sign(s) * Math.Pow(Math.Abs(s), 2 / n)));
            }
            var p = new GraphicsPath(); p.AddPolygon(pts); return p;
        }

        static GraphicsPath Rr(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        // ---------- analisis del icono ----------
        static void Analyze(int[] p, int px, out Rectangle bb, out float cover)
        {
            int x0 = px, y0 = px, x1 = -1, y1 = -1;
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                    if ((p[y * px + x] >> 24 & 255) > 20) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            if (x1 < 0) { bb = Rectangle.Empty; cover = 0; return; }
            bb = Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
            int solid = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if ((p[y * px + x] >> 24 & 255) > 200) solid++;
            cover = solid / (float)(bb.Width * bb.Height);
        }

        static void Palette(int[] p, out Color top, out Color bot)
        {
            double r = 0, g = 0, b = 0, w = 0, lum = 0, la = 0;
            foreach (int c in p)
            {
                int a = c >> 24 & 255; if (a < 128) continue;
                int R = c >> 16 & 255, G = c >> 8 & 255, B = c & 255;
                int mx = Math.Max(R, Math.Max(G, B)), mn = Math.Min(R, Math.Min(G, B));
                double sat = mx == 0 ? 0 : (mx - mn) / (double)mx, wt = 0.05 + sat * sat;
                r += R * wt; g += G * wt; b += B * wt; w += wt;
                lum += (0.299 * R + 0.587 * G + 0.114 * B) / 255.0; la++;
            }
            if (w == 0) { top = Color.FromArgb(252, 252, 254); bot = Color.FromArgb(226, 226, 232); return; }
            Color c0 = Color.FromArgb((int)(r / w), (int)(g / w), (int)(b / w));
            if (la > 0 && lum / la > 0.8)
            {
                // glifo claro (blanco): fondo oscuro tenido del color del icono
                top = Mix(Color.FromArgb(72, 72, 78), c0, 0.35); bot = Mix(Color.FromArgb(28, 28, 32), c0, 0.25);
            }
            else
            {
                // glifo de color: fondo claro casi blanco, con un toque del color del icono
                top = Mix(Color.White, c0, 0.04); bot = Mix(Color.FromArgb(232, 232, 238), c0, 0.12);
            }
        }

        static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // ---------- efectos ----------
        static Bitmap Shadow(int px, int bs, float ox, float oy)
        {
            var sh = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using (var g = Hq(Graphics.FromImage(sh)))
            using (var path = Squircle(new RectangleF(ox + bs * 0.02f, oy + px * 0.022f, bs * 0.96f, bs * 0.96f)))
            using (var b = new SolidBrush(Color.FromArgb(105, 0, 0, 0))) g.FillPath(b, path);
            Blur(sh, Math.Max(2, px / 52));
            return sh;
        }

        static Bitmap GlyphShadow(Bitmap norm, Rectangle bb, RectangleF dst, int bs)
        {
            var sh = new Bitmap(bs, bs, PixelFormat.Format32bppArgb);
            using (var g = Hq(Graphics.FromImage(sh))) g.DrawImage(norm, dst, bb, GraphicsUnit.Pixel);
            int[] p = Pixels(sh);
            for (int i = 0; i < p.Length; i++) p[i] = ((p[i] >> 24 & 255) * 50 / 255) << 24;
            SetPixels(sh, p);
            Blur(sh, Math.Max(1, bs / 70));
            return sh;
        }

        static Bitmap Mask(GraphicsPath path, int bs)
        {
            var m = new Bitmap(bs, bs, PixelFormat.Format32bppArgb);
            using (var g = Hq(Graphics.FromImage(m))) g.FillPath(Brushes.White, path);
            return m;
        }

        static void ApplyMask(Bitmap bmp, Bitmap mask)
        {
            int[] p = Pixels(bmp), m = Pixels(mask);
            for (int i = 0; i < p.Length; i++)
            {
                int a = (p[i] >> 24 & 255) * (m[i] >> 24 & 255) / 255;
                p[i] = (a << 24) | (p[i] & 0xFFFFFF);
            }
            SetPixels(bmp, p);
        }

        // desenfoque por cajas (3 pasadas ~ gaussiano), solo sobre el canal alfa; el color se conserva
        static void Blur(Bitmap bmp, int rad)
        {
            int w = bmp.Width, h = bmp.Height;
            int[] p = Pixels(bmp);
            var a = new int[w * h]; var t = new int[w * h];
            for (int i = 0; i < a.Length; i++) a[i] = p[i] >> 24 & 255;
            for (int pass = 0; pass < 3; pass++) { BoxH(a, t, w, h, rad); BoxV(t, a, w, h, rad); }
            int rgb = 0; for (int i = 0; i < p.Length; i++) if ((p[i] >> 24 & 255) > 0) { rgb = p[i] & 0xFFFFFF; break; }
            for (int i = 0; i < p.Length; i++) p[i] = (a[i] << 24) | rgb;
            SetPixels(bmp, p);
        }

        static void BoxH(int[] s, int[] d, int w, int h, int r)
        {
            int n = r * 2 + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w, sum = 0;
                for (int x = -r; x <= r; x++) sum += s[row + Math.Max(0, Math.Min(w - 1, x))];
                for (int x = 0; x < w; x++)
                {
                    d[row + x] = sum / n;
                    sum += s[row + Math.Min(w - 1, x + r + 1)] - s[row + Math.Max(0, x - r)];
                }
            }
        }

        static void BoxV(int[] s, int[] d, int w, int h, int r)
        {
            int n = r * 2 + 1;
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int y = -r; y <= r; y++) sum += s[Math.Max(0, Math.Min(h - 1, y)) * w + x];
                for (int y = 0; y < h; y++)
                {
                    d[y * w + x] = sum / n;
                    sum += s[Math.Min(h - 1, y + r + 1) * w + x] - s[Math.Max(0, y - r) * w + x];
                }
            }
        }

        // ---------- utilidades ----------
        static Bitmap Normalize(Bitmap src, int px)
        {
            var b = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using (var g = Hq(Graphics.FromImage(b)))
            {
                float k = Math.Min(px / (float)src.Width, px / (float)src.Height);
                if (k > 1f && src.Width >= px / 2) k = 1f; // no estirar si ya es grande: el recorte por bordes lo ajusta luego
                float w = src.Width * k, h = src.Height * k;
                g.DrawImage(src, (px - w) / 2f, (px - h) / 2f, w, h);
            }
            return b;
        }

        static Graphics Hq(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            return g;
        }

        static int[] Pixels(Bitmap b)
        {
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var p = new int[b.Width * b.Height];
            try { for (int y = 0; y < b.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, p, y * b.Width, b.Width); }
            finally { b.UnlockBits(d); }
            return p;
        }

        static void SetPixels(Bitmap b, int[] p)
        {
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < b.Height; y++) Marshal.Copy(p, y * b.Width, d.Scan0 + y * d.Stride, b.Width); }
            finally { b.UnlockBits(d); }
        }
    }
}
