using System.Globalization;

namespace Post.Core;

/// <summary>
/// A parsed .cube colour lookup table. ffmpeg reads the file directly on export; this
/// exists so the editor can apply the same table to previews and thumbnails.
/// </summary>
public sealed class CubeLut
{
    private readonly float[] _data;

    private CubeLut(int size, float[] data) { Size = size; _data = data; }

    public int Size { get; }

    /// <summary>Reads a .cube file. Rows run with red varying fastest.</summary>
    public static CubeLut Load(string path)
    {
        var size = 0;
        var values = new List<float>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
            {
                size = int.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1], CultureInfo.InvariantCulture);
                continue;
            }
            if (!char.IsDigit(line[0]) && line[0] != '-' && line[0] != '.') continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            for (var i = 0; i < 3; i++) values.Add(float.Parse(parts[i], CultureInfo.InvariantCulture));
        }
        if (size < 2) throw new InvalidDataException("The .cube file has no usable LUT_3D_SIZE.");
        var expected = size * size * size * 3;
        if (values.Count < expected) throw new InvalidDataException($"The .cube file holds {values.Count / 3} entries but {size}³ needs {expected / 3}.");
        return new CubeLut(size, values.ToArray());
    }

    /// <summary>Trilinear lookup of one 0–1 RGB triple.</summary>
    public (double R, double G, double B) Sample(double r, double g, double b)
    {
        var last = Size - 1;
        double Position(double value) => Math.Clamp(value, 0, 1) * last;
        var (rf, gf, bf) = (Position(r), Position(g), Position(b));
        int R0 = (int)rf, G0 = (int)gf, B0 = (int)bf;
        int R1 = Math.Min(R0 + 1, last), G1 = Math.Min(G0 + 1, last), B1 = Math.Min(B0 + 1, last);
        double dr = rf - R0, dg = gf - G0, db = bf - B0;

        (double R, double G, double B) At(int ri, int gi, int bi)
        {
            var index = (ri + gi * Size + bi * Size * Size) * 3;
            return (_data[index], _data[index + 1], _data[index + 2]);
        }

        (double R, double G, double B) Mix((double R, double G, double B) a, (double R, double G, double B) c, double t)
            => (a.R + (c.R - a.R) * t, a.G + (c.G - a.G) * t, a.B + (c.B - a.B) * t);

        var c00 = Mix(At(R0, G0, B0), At(R1, G0, B0), dr);
        var c10 = Mix(At(R0, G1, B0), At(R1, G1, B0), dr);
        var c01 = Mix(At(R0, G0, B1), At(R1, G0, B1), dr);
        var c11 = Mix(At(R0, G1, B1), At(R1, G1, B1), dr);
        return Mix(Mix(c00, c10, dg), Mix(c01, c11, dg), db);
    }
}
