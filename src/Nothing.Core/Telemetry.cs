using System.Numerics;

namespace Nothing.Core;

public sealed record BatteryReading(int? Percent, bool Charging, string Source, int? ProtocolPercent, int? WindowsPercent)
{
    public bool Disagrees => ProtocolPercent.HasValue && WindowsPercent.HasValue && ProtocolPercent != WindowsPercent;
    public static BatteryReading Resolve(int? protocol, bool charging, int? windows)
    {
        if (protocol is < 0 or > 100 || windows is < 0 or > 100) throw new ArgumentException("Invalid battery percentage.");
        return new(windows ?? protocol, charging, windows.HasValue ? "Windows" : protocol.HasValue ? "Headphone" : "Unavailable", protocol, windows);
    }
}

public static class EqualizerResponse
{
    public const double SampleRate = 48000;
    public static double FrequencyAt(double position) => 20 * Math.Pow(1000, Math.Clamp(position, 0, 1));
    public static double PositionAt(double frequency) => Math.Log(frequency / 20) / Math.Log(1000);

    public static double Decibels(EqualizerProfile profile, double frequency)
    {
        if (!double.IsFinite(frequency) || frequency <= 0 || frequency >= SampleRate / 2) throw new ArgumentException("Frequency is outside the modeled range.");
        var z = Complex.FromPolarCoordinates(1, -2 * Math.PI * frequency / SampleRate);
        var z2 = z * z;
        var decibels = (double)profile.Preamp;
        foreach (var band in profile.Bands)
        {
            if (Math.Abs(band.Gain) < 0.00001) continue;
            var a = Math.Pow(10, band.Gain / 40.0);
            var omega = 2 * Math.PI * Math.Min(band.Frequency, SampleRate / 2 - 1) / SampleRate;
            var cos = Math.Cos(omega); var alpha = Math.Sin(omega) / (2 * band.Q);
            double b0, b1, b2, a0, a1, a2;
            var shelf = 2 * Math.Sqrt(a) * alpha;
            switch (band.Type)
            {
                case 0:
                    b0 = a * ((a + 1) - (a - 1) * cos + shelf);
                    b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                    b2 = a * ((a + 1) - (a - 1) * cos - shelf);
                    a0 = (a + 1) + (a - 1) * cos + shelf;
                    a1 = -2 * ((a - 1) + (a + 1) * cos);
                    a2 = (a + 1) + (a - 1) * cos - shelf;
                    break;
                case 1:
                    b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
                    a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
                    break;
                case 2:
                    b0 = a * ((a + 1) + (a - 1) * cos + shelf);
                    b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                    b2 = a * ((a + 1) + (a - 1) * cos - shelf);
                    a0 = (a + 1) - (a - 1) * cos + shelf;
                    a1 = 2 * ((a - 1) - (a + 1) * cos);
                    a2 = (a + 1) - (a - 1) * cos - shelf;
                    break;
                default: throw new ArgumentException("Unknown equalizer filter type.");
            }
            var response = (b0 + b1 * z + b2 * z2) / (a0 + a1 * z + a2 * z2);
            decibels += 20 * Math.Log10(Math.Max(response.Magnitude, 1e-12));
        }
        return decibels;
    }
}
