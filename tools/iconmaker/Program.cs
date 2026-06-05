using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// Generates app.ico for MpvGrid from the InterMoor logo: a brand-blue gradient
// disc with the white "fountain" blades sampled directly from the logo so the
// emblem shape matches exactly. Supersampled at 1024px, downscaled into a
// 9-size .ico (256..16). Usage: iconmaker <srcJpg> <outIco> <previewPng>
internal static class Program
{
    // Circle location in the source logo (measured): center (400,258), radius 135.
    const double SCX = 400.0, SCY = 258.0, SR = 135.0;

    private static int Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: iconmaker <srcJpg> <outIco> <previewPng> <mode>");
            Console.Error.WriteLine("  mode A = white fountain fills disc (faithful, white-dominant)");
            Console.Error.WriteLine("  mode B = blue badge + inset white fountain (blue-dominant)");
            Console.Error.WriteLine("  mode C = blue badge + thin white rays");
            return 2;
        }
        Run(args[0], args[1], args[2], args[3]);
        Console.WriteLine("Wrote " + args[1] + " (mode " + args[3] + ")");
        Console.WriteLine("Preview " + args[2]);
        return 0;
    }

    private static void Run(string srcPath, string outIco, string previewPng, string mode)
    {
        using var src = new Bitmap(srcPath);
        int sw = src.Width, sh = src.Height;
        var rect = new Rectangle(0, 0, sw, sh);
        var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int[] spx = new int[sw * sh];
        Marshal.Copy(data.Scan0, spx, 0, spx.Length);
        src.UnlockBits(data);

        // Brand-blue radial gradient for the disc, white for the fountain jets.
        int[] c0 = { 20, 124, 196 };  // center (cerulean)
        int[] c1 = { 0, 64, 135 };    // edge (royal)

        // Mode controls how the fountain is rendered on the disc.
        //   A: fountain fills the disc, jets painted white (faithful, white-dominant)
        //   B: fountain inset inside a blue ring, jets white (blue badge)
        //   C: invert -> blue disc with the gaps painted white (thin white rays)
        double frFactor = 1.0; bool invert = false;
        if (mode == "B") { frFactor = 0.80; invert = false; }
        else if (mode == "C") { frFactor = 0.90; invert = true; }

        int M = 1024;
        double cx = (M - 1) / 2.0, cy = (M - 1) / 2.0;
        double R = M * 0.485;
        double FR = R * frFactor;
        int[] mpx = new int[M * M];

        for (int y = 0; y < M; y++)
        {
            for (int x = 0; x < M; x++)
            {
                double dx = x - cx, dy = y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double cov = R - dist + 0.5;          // antialiased disc edge
                double tr = dist / R; if (tr > 1) tr = 1;
                int br = (int)(c0[0] + (c1[0] - c0[0]) * tr);
                int bg = (int)(c0[1] + (c1[1] - c0[1]) * tr);
                int bb = (int)(c0[2] + (c1[2] - c0[2]) * tr);

                if (cov <= 0)
                {
                    // Outside disc: transparent, but keep edge color in RGB so
                    // high-quality downscaling does not bleed a dark halo.
                    mpx[y * M + x] = (0 << 24) | (c1[0] << 16) | (c1[1] << 8) | c1[2];
                    continue;
                }
                if (cov > 1) cov = 1;

                // Map this disc pixel back into the source circle and read the
                // jet mask from the green channel (jets ~112, gaps/bg ~70). Only
                // sample within the fountain radius FR; beyond it stays pure blue.
                double jet = 0.0;
                if (dist <= FR)
                {
                    double sx = SCX + (dx / FR) * SR;
                    double sy = SCY + (dy / FR) * SR;
                    if (sx >= 0 && sx < sw - 1 && sy >= 0 && sy < sh - 1)
                    {
                        int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                        double fx = sx - x0, fy = sy - y0;
                        double g00 = (spx[y0 * sw + x0] >> 8) & 0xFF;
                        double g10 = (spx[y0 * sw + x0 + 1] >> 8) & 0xFF;
                        double g01 = (spx[(y0 + 1) * sw + x0] >> 8) & 0xFF;
                        double g11 = (spx[(y0 + 1) * sw + x0 + 1] >> 8) & 0xFF;
                        double gt = (g00 * (1 - fx) + g10 * fx) * (1 - fy)
                                  + (g01 * (1 - fx) + g11 * fx) * fy;
                        jet = (gt - 85.0) / (99.0 - 85.0);   // soft threshold
                        if (jet < 0) jet = 0; if (jet > 1) jet = 1;
                        if (invert) jet = 1.0 - jet;
                    }
                }

                int r = (int)(br + (255 - br) * jet);
                int g = (int)(bg + (255 - bg) * jet);
                int b = (int)(bb + (255 - bb) * jet);
                int a = (int)(cov * 255);
                mpx[y * M + x] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }

        using var master = new Bitmap(M, M, PixelFormat.Format32bppArgb);
        var mdata = master.LockBits(new Rectangle(0, 0, M, M), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(mpx, 0, mdata.Scan0, mpx.Length);
        master.UnlockBits(mdata);

        int[] sizes = { 256, 128, 64, 48, 40, 32, 24, 20, 16 };
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using var bmp = Downscale(master, s);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            pngs.Add(ms.ToArray());
        }

        // Write the .ico (all entries PNG-compressed; supported Vista+).
        using (var fs = new FileStream(outIco, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((short)0);
            bw.Write((short)1);
            bw.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                bw.Write((byte)(s == 256 ? 0 : s));
                bw.Write((byte)(s == 256 ? 0 : s));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((short)1);
                bw.Write((short)32);
                bw.Write(pngs[i].Length);
                bw.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) bw.Write(p);
        }

        SavePreview(master, previewPng);
    }

    private static Bitmap Downscale(Bitmap master, int size)
    {
        var b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);
        g.DrawImage(master, new Rectangle(0, 0, size, size),
                    0, 0, master.Width, master.Height, GraphicsUnit.Pixel);
        return b;
    }

    private static void SavePreview(Bitmap master, string path)
    {
        int[] sizes = { 256, 48, 32, 16 };
        int pad = 16, x = pad;
        int w = pad; foreach (int s in sizes) w += s + pad;
        int h = 256 + pad * 2;
        using var canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            // Left half light, right half dark, to judge both taskbar themes.
            g.FillRectangle(new SolidBrush(Color.FromArgb(238, 238, 238)), 0, 0, w / 2, h);
            g.FillRectangle(new SolidBrush(Color.FromArgb(32, 34, 37)), w / 2, 0, w - w / 2, h);
            foreach (int s in sizes)
            {
                using var ic = Downscale(master, s);
                int y = (h - s) / 2;
                g.DrawImage(ic, x, y, s, s);
                x += s + pad;
            }
        }
        canvas.Save(path, ImageFormat.Png);
    }
}
