using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;
using WF = System.Windows.Forms;

namespace ShakeConvert
{
    /// <summary>
    /// Windows 11 acrylic material for transparent windows (radial menu and notifications).
    /// Follows the WinUI recipe: blurred, saturated backdrop, then a "luminosity" layer
    /// that brings the brightness to the theme's level while keeping only the color of what's behind,
    /// and finally a light tint. The result is nearly opaque, with a hint of the backdrop color,
    /// like Start, Notification Center and the system menus.
    /// </summary>
    internal static class Acrylic
    {
        // WinUI values (AcrylicBackgroundFillColorDefault)
        static (double r, double g, double b) Tint => Theme.IsDark ? (0x2C / 255.0, 0x2C / 255.0, 0x2C / 255.0) : (0xFC / 255.0, 0xFC / 255.0, 0xFC / 255.0);
        static double TintOpacity => Theme.IsDark ? 0.45 : 0.30;
        static double Saturation => Theme.IsDark ? 55 : 80;   // the backdrop color barely shows through
        static double LuminosityOpacity => Theme.IsDark ? 0.96 : 0.85;

        public static FrameworkElement Create(int left, int top, double sizeW, double sizeH, double scale)
        {
            var host = new Canvas { Width = sizeW, Height = sizeH, ClipToBounds = true, IsHitTestVisible = false };
            host.Children.Add(new Border { Width = sizeW, Height = sizeH, Background = Theme.FlyoutSolid }); // fallback
            if (!Theme.Transparency) return host; // "Transparency effects" off: solid tint, like Windows

            try
            {
                const int marginDip = 48; // extra margin: the blur doesn't darken the edges
                int m = (int)(marginDip * scale);
                var rect = new SD.Rectangle(left - m, top - m, (int)(sizeW * scale) + 2 * m, (int)(sizeH * scale) + 2 * m);
                rect.Intersect(WF.SystemInformation.VirtualScreen);
                if (rect.Width > 8 && rect.Height > 8)
                {
                    var img = new System.Windows.Controls.Image { Source = Material(rect), Width = rect.Width / scale, Height = rect.Height / scale, Stretch = Stretch.Fill };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    Canvas.SetLeft(img, (rect.Left - left) / scale);
                    Canvas.SetTop(img, (rect.Top - top) / scale);
                    host.Children.Add(img);
                }
            }
            catch { /* keep the solid tint */ }

            host.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
            return host;
        }

        /// <summary>
        /// The same material as a brush: it can fill any shape (the menu slices).
        /// Coordinates are window coordinates, so each shape "sees" the piece of backdrop behind it.
        /// </summary>
        public static Brush CreateBrush(int left, int top, double sizeW, double sizeH, double scale)
        {
            if (!Theme.Transparency) return Theme.FlyoutSolid;
            try
            {
                const int marginDip = 48;
                int m = (int)(marginDip * scale);
                var rect = new SD.Rectangle(left - m, top - m, (int)(sizeW * scale) + 2 * m, (int)(sizeH * scale) + 2 * m);
                rect.Intersect(WF.SystemInformation.VirtualScreen);
                if (rect.Width <= 8 || rect.Height <= 8) return Theme.FlyoutSolid;
                var brush = new ImageBrush(Material(rect))
                {
                    Stretch = Stretch.Fill,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect((rect.Left - left) / scale, (rect.Top - top) / scale, rect.Width / scale, rect.Height / scale)
                };
                RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
                brush.Freeze();
                return brush;
            }
            catch { return Theme.FlyoutSolid; }
        }

        static BitmapSource Material(SD.Rectangle r)
        {
            // 1) capture the screen behind the window and downscale to 1/8 (done by Windows graphics)
            int w = Math.Max(4, r.Width / 8), h = Math.Max(4, r.Height / 8);
            using var small = new SD.Bitmap(w, h, SDI.PixelFormat.Format24bppRgb);
            using (var full = new SD.Bitmap(r.Width, r.Height, SDI.PixelFormat.Format24bppRgb))
            {
                using (var g = SD.Graphics.FromImage(full))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, SD.CopyPixelOperation.SourceCopy);
                using var gs = SD.Graphics.FromImage(small);
                gs.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBilinear;
                gs.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
                gs.DrawImage(full, new SD.Rectangle(0, 0, w, h));
            }

            // 2) pixels in memory (BGR)
            var data = small.LockBits(new SD.Rectangle(0, 0, w, h), SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format24bppRgb);
            var raw = new byte[data.Stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, raw, 0, raw.Length);
            int stride = data.Stride;
            small.UnlockBits(data);

            var rr = new double[w * h]; var gg = new double[w * h]; var bb = new double[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s0 = y * stride + x * 3, i = y * w + x;
                    bb[i] = raw[s0] / 255.0; gg[i] = raw[s0 + 1] / 255.0; rr[i] = raw[s0 + 2] / 255.0;
                }

            // 3) blur: 3 box-blur passes ≈ Gaussian, smooth and band-free
            foreach (var ch in new[] { rr, gg, bb })
                for (int pass = 0; pass < 3; pass++) BoxBlur(ch, w, h, 3);

            // 4) muted colors + luminosity layer + tint (Windows 11 acrylic recipe)
            var (tr, tg, tb) = Tint;
            double tl = Lum(tr, tg, tb), lo = LuminosityOpacity, to = TintOpacity, sat = Saturation / 100.0;
            var outPx = new byte[w * h * 3];
            for (int i = 0; i < w * h; i++)
            {
                double gray = Lum(rr[i], gg[i], bb[i]);
                double br = gray + (rr[i] - gray) * sat, bg = gray + (gg[i] - gray) * sat, bl = gray + (bb[i] - gray) * sat;
                var (sr, sg, sb) = SetLum(br, bg, bl, tl);
                double r1 = br + (sr - br) * lo, g1 = bg + (sg - bg) * lo, b1 = bl + (sb - bl) * lo;
                double r2 = r1 + (tr - r1) * to, g2 = g1 + (tg - g1) * to, b2 = b1 + (tb - b1) * to;
                outPx[i * 3] = ToByte(b2);
                outPx[i * 3 + 1] = ToByte(g2);
                outPx[i * 3 + 2] = ToByte(r2);
            }
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, outPx, w * 3);
            src.Freeze();
            return src;
        }

        /// <summary>Separable box blur (horizontal + vertical), with clamped edges.</summary>
        static void BoxBlur(double[] c, int w, int h, int radius)
        {
            var tmp = new double[c.Length];
            double norm = 1.0 / (radius * 2 + 1);
            for (int y = 0; y < h; y++)
            {
                double acc = 0;
                for (int k = -radius; k <= radius; k++) acc += c[y * w + Math.Clamp(k, 0, w - 1)];
                for (int x = 0; x < w; x++)
                {
                    tmp[y * w + x] = acc * norm;
                    acc += c[y * w + Math.Min(x + radius + 1, w - 1)] - c[y * w + Math.Max(x - radius, 0)];
                }
            }
            for (int x = 0; x < w; x++)
            {
                double acc = 0;
                for (int k = -radius; k <= radius; k++) acc += tmp[Math.Clamp(k, 0, h - 1) * w + x];
                for (int y = 0; y < h; y++)
                {
                    c[y * w + x] = acc * norm;
                    acc += tmp[Math.Min(y + radius + 1, h - 1) * w + x] - tmp[Math.Max(y - radius, 0) * w + x];
                }
            }
        }

        static double Lum(double r, double g, double b) => 0.3 * r + 0.59 * g + 0.11 * b;

        static (double, double, double) SetLum(double r, double g, double b, double l)
        {
            double d = l - Lum(r, g, b);
            r += d; g += d; b += d;
            double lum = Lum(r, g, b), n = Math.Min(r, Math.Min(g, b)), x = Math.Max(r, Math.Max(g, b));
            if (n < 0 && lum - n > 1e-9) { r = lum + (r - lum) * lum / (lum - n); g = lum + (g - lum) * lum / (lum - n); b = lum + (b - lum) * lum / (lum - n); }
            if (x > 1 && x - lum > 1e-9) { r = lum + (r - lum) * (1 - lum) / (x - lum); g = lum + (g - lum) * (1 - lum) / (x - lum); b = lum + (b - lum) * (1 - lum) / (x - lum); }
            return (r, g, b);
        }

        static byte ToByte(double v) => (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
    }
}
