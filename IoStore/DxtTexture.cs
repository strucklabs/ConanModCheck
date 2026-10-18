using System.Buffers.Binary;
using System.Text;

namespace ConanModCheck.IoStore;

/// <summary>
/// Liest eine DXT1-Textur (z. B. die Spielkarte) aus einem Texturpaket und wandelt sie in ein
/// Graustufenbild um. Unterstützt nur, was die Karten von Conan brauchen: DXT1, eine Ebene,
/// Daten direkt im Paket.
/// </summary>
static class DxtTexture
{
    public sealed record GrayImage(int Width, int Height, byte[] Pixels);

    /// <param name="downscale">Nur jeden n-ten Block lesen (1 = volle Größe, 2 = halbe …).</param>
    public static GrayImage? DecodeGray(byte[] package, int downscale = 1)
    {
        // Pixelformat als FString: Länge 8, "PF_DXT1\0"; davor Breite, Höhe, Schichten, Länge
        var marker = Encoding.ASCII.GetBytes("PF_DXT1\0");
        int i = package.AsSpan().IndexOf(marker);
        while (i >= 0 && (i < 16 || BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(i - 4)) != 8))
        {
            int next = package.AsSpan(i + 1).IndexOf(marker);
            i = next < 0 ? -1 : i + 1 + next;
        }
        if (i < 0) return null;

        int w = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(i - 16));
        int h = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(i - 12));
        int start = i + 8 + 12; // FString, erste Ebene, Ebenenanzahl, Bulk-Kopf
        if (w is < 64 or > 16384 || h is < 64 or > 16384 || w % 4 != 0 || h % 4 != 0) return null;
        if (start + (long)w * h / 2 > package.Length) return null;

        int bw = w / 4, bh = h / 4;
        int step = Math.Max(1, downscale);
        int ow = (bw / step) * 4, oh = (bh / step) * 4;
        var gray = new byte[ow * oh];
        Span<int> pal = stackalloc int[4];

        for (int by = 0, oy = 0; by + step <= bh; by += step, oy += 4)
        {
            for (int bx = 0, ox = 0; bx + step <= bw; bx += step, ox += 4)
            {
                int o = start + (by * bw + bx) * 8;
                ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(o));
                ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(o + 2));
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(o + 4));
                int l0 = Luma(c0), l1 = Luma(c1);
                pal[0] = l0; pal[1] = l1;
                if (c0 > c1) { pal[2] = (2 * l0 + l1) / 3; pal[3] = (l0 + 2 * l1) / 3; }
                else { pal[2] = (l0 + l1) / 2; pal[3] = 0; }
                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                        gray[(oy + py) * ow + ox + px] = (byte)pal[(int)((bits >> (2 * (py * 4 + px))) & 3)];
            }
        }
        return new GrayImage(ow, oh, gray);
    }

    /// <summary>Helligkeit einer RGB565-Farbe (0–255).</summary>
    static int Luma(ushort c)
    {
        int r = (c >> 11 & 31) * 255 / 31, g = (c >> 5 & 63) * 255 / 63, b = (c & 31) * 255 / 31;
        return (r * 299 + g * 587 + b * 114) / 1000;
    }
}
