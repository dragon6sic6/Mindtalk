#if DEBUG
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Mindtalk.App.Controls;

namespace Mindtalk.App;

/// <summary>
/// Mindtalk 2.0's app icon — the orb's dot sphere, white on a black rounded square, as on the
/// Mac — drawn from the very same dots as <see cref="Orb"/>. Writes Assets\Mindtalk.ico
/// (16–256 px, PNG inside) and Mindtalk-256.png: <c>Mindtalk --render-icon &lt;folder&gt;</c>.
/// Small sizes get fewer, larger dots, so the sphere still reads at 16 px.
/// </summary>
internal static class IconArt
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    public static void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var images = new List<(int Size, byte[] Png)>();
        foreach (var size in Sizes)
        {
            using var bitmap = Draw(size);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            images.Add((size, stream.ToArray()));
            if (size == 256) File.WriteAllBytes(Path.Combine(folder, "Mindtalk-256.png"), stream.ToArray());
        }
        // Every size side by side, enlarged, to judge the small ones.
        using (var sheet = new Bitmap(Sizes.Sum(s => s * 4 + 16), 256 * 4 / 4 + 16 + 4 * 64))
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(240, 240, 240));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            var x = 8;
            foreach (var size in Sizes.Where(s => s <= 64))
            {
                using var image = Draw(size);
                g.DrawImage(image, x, 8, size * 4, size * 4);
                g.DrawImage(image, x, 8 + size * 4 + 8, size, size);
                x += size * 4 + 16;
            }
            sheet.Save(Path.Combine(folder, "sizes.png"), ImageFormat.Png);
        }
        using (var big = Draw(1024)) big.Save(Path.Combine(folder, "Mindtalk-1024.png"), ImageFormat.Png);
        WriteIco(Path.Combine(folder, "Mindtalk.ico"), images);
    }

    /// The tile and the sphere at <paramref name="px"/> pixels square.
    public static Bitmap Draw(int px)
    {
        // Full bleed at the small sizes (every pixel counts), a little air around the large ones.
        var margin = px <= 32 ? 0 : Math.Round(px * 0.035);
        var tile = px - 2 * margin;
        var corner = tile * 0.225;
        var pixels = new byte[px * px * 4];

        // The black tile: a touch of light at the top, as the Mac's glass tile has.
        for (var y = 0; y < px; y++)
        for (var x = 0; x < px; x++)
        {
            var coverage = RoundedCoverage(x + 0.5 - margin, y + 0.5 - margin, tile, corner);
            if (coverage <= 0) continue;
            var t = Math.Clamp((y + 0.5 - margin) / tile, 0, 1);
            var grey = (0.115 * (1 - t) + 0.015 * t) * 255;
            var i = (y * px + x) * 4;
            var c = (byte)Math.Round(grey * coverage);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = c;
            pixels[i + 3] = (byte)Math.Round(255 * coverage);
        }

        // The sphere, about four fifths of the tile.
        var spherePx = (int)Math.Round(tile * 0.82);
        var (count, scale, min) = px switch
        {
            <= 16 => (34, 2.6, 0.62),
            <= 24 => (60, 2.2, 0.62),
            <= 32 => (95, 1.9, 0.6),
            <= 48 => (170, 1.6, 0.55),
            <= 64 => (260, 1.45, 0.5),
            <= 128 => (440, 1.25, 0.5),
            _ => (720, 1.15, 0.5),
        };
        var sphere = new byte[spherePx * spherePx * 4];
        // Small sizes: the dots twice over, so the faint ones behind still read on the tile.
        var dots = Orb.Dots(count);
        for (var pass = 0; pass < (px <= 48 ? 2 : 1); pass++)
            Orb.Paint(sphere, spherePx, dots, 0.6, 0, onDark: true, dotScale: scale, minRadius: min);
        var offset = (int)Math.Round((px - spherePx) / 2.0);
        for (var y = 0; y < spherePx; y++)
        for (var x = 0; x < spherePx; x++)
        {
            var s = (y * spherePx + x) * 4;
            var a = sphere[s + 3] / 255.0;
            if (a <= 0) continue;
            var d = ((y + offset) * px + x + offset) * 4;
            // Premultiplied "over".
            for (var k = 0; k < 3; k++) pixels[d + k] = (byte)Math.Round(sphere[s + k] + pixels[d + k] * (1 - a));
            pixels[d + 3] = (byte)Math.Round(sphere[s + 3] + pixels[d + 3] * (1 - a));
        }

        var bitmap = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, px, px), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < px; y++) Marshal.Copy(pixels, y * px * 4, data.Scan0 + y * data.Stride, px * 4);
        bitmap.UnlockBits(data);
        return bitmap;
    }

    /// How much of the pixel at (x, y) is inside a rounded square — anti-aliased edges.
    private static double RoundedCoverage(double x, double y, double size, double r)
    {
        if (x < 0 || y < 0 || x > size || y > size) return 0;
        var cx = Math.Clamp(x, r, size - r);
        var cy = Math.Clamp(y, r, size - r);
        var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
        var inside = Math.Clamp(r - d + 0.5, 0, 1);
        var edge = Math.Min(Math.Min(x, y), Math.Min(size - x, size - y));
        return Math.Min(inside, Math.Clamp(edge + 0.5, 0, 1));
    }

    /// An .ico with PNG images inside (Windows Vista and later).
    private static void WriteIco(string path, List<(int Size, byte[] Png)> images)
    {
        using var file = new BinaryWriter(File.Create(path));
        file.Write((short)0);
        file.Write((short)1);
        file.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            file.Write((byte)(size >= 256 ? 0 : size));
            file.Write((byte)(size >= 256 ? 0 : size));
            file.Write((byte)0);
            file.Write((byte)0);
            file.Write((short)1);
            file.Write((short)32);
            file.Write(png.Length);
            file.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) file.Write(png);
    }
}
#endif
