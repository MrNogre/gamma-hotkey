namespace GammaHotkey;

public sealed class GammaRamp
{
    public const int Length = 768;
    public const int ReadbackTolerance = 1024;
    private readonly ushort[] values;

    public GammaRamp(ushort[] values)
    {
        if (values.Length != Length) throw new ArgumentException("A ramp needs 768 WORD values.", nameof(values));
        this.values = (ushort[])values.Clone();
    }

    public ushort this[int index] => values[index];
    public ushort[] ToArray() => (ushort[])values.Clone();

    public bool IsSafe()
    {
        for (int channel = 0; channel < 3; channel++)
        {
            int offset = channel * 256;
            for (int i = 0; i < 256; i++)
            {
                int identity = i * 65535 / 255;
                if (Math.Abs(values[offset + i] - identity) > 32768 ||
                    (i > 0 && values[offset + i] < values[offset + i - 1])) return false;
            }
        }
        return true;
    }

    public GammaRamp Bright(double gamma)
    {
        if (!double.IsFinite(gamma) || gamma < 0.5 || gamma > 4)
            throw new ArgumentOutOfRangeException(nameof(gamma));
        if (!IsSafe()) throw new ArgumentException("The source ramp is unsafe or non-monotonic.");
        if (gamma == 1) return new GammaRamp(values);

        var result = new ushort[Length];
        for (int i = 0; i < 256; i++)
        {
            double coordinate = 255 * Math.Pow(i / 255.0, 1 / gamma);
            int lower = Math.Min((int)Math.Floor(coordinate), 255);
            int upper = Math.Min(lower + 1, 255);
            double fraction = coordinate - lower;
            for (int channel = 0; channel < 3; channel++)
            {
                int offset = channel * 256;
                result[offset + i] = (ushort)Math.Clamp(
                    (int)Math.Round((1 - fraction) * values[offset + lower] +
                                    fraction * values[offset + upper]), 0, 65535);
            }
        }
        for (int channel = 0; channel < 3; channel++)
        {
            result[channel * 256] = values[channel * 256];
            result[channel * 256 + 255] = values[channel * 256 + 255];
        }
        var bright = new GammaRamp(result);
        for (int channel = 0; channel < 3; channel++)
            for (int i = 1; i < 256; i++)
                if (bright[channel * 256 + i] < bright[channel * 256 + i - 1])
                    throw new ArgumentException("The transformed ramp is non-monotonic.");
        return bright;
    }

    public bool Matches(GammaRamp other, int tolerance = ReadbackTolerance)
    {
        for (int i = 0; i < Length; i++)
            if (Math.Abs(values[i] - other.values[i]) > tolerance) return false;
        return true;
    }
}
