using System.Buffers.Binary;

namespace Nothing.Core;

public sealed record Frame(ushort Command, byte Operation, byte[] Payload);

public static class Protocol
{
    public const int MaxPayload = 4096;
    public static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (ushort)((crc >> 1) ^ ((crc & 1) != 0 ? 0xA001 : 0));
        }
        return crc;
    }

    public static byte[] Encode(ushort command, byte operation, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload) throw new ArgumentException("Payload exceeds safety limit.");
        var frame = new byte[payload.Length + 10];
        frame[0] = 0x55; frame[1] = 0x60; frame[2] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3), command);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5), (ushort)payload.Length);
        frame[7] = operation;
        payload.CopyTo(frame.AsSpan(8));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), Crc(frame.AsSpan(0, frame.Length - 2)));
        return frame;
    }
}

public sealed class FrameParser
{
    private readonly List<byte> buffer = [];
    public long RejectedFrames { get; private set; }

    public List<Frame> Feed(ReadOnlySpan<byte> data)
    {
        foreach (var value in data) buffer.Add(value);
        List<Frame> frames = [];
        while (buffer.Count >= 10)
        {
            var bytes = buffer.ToArray();
            var found = false;
            var incomplete = -1;
            for (var offset = 0; offset <= bytes.Length - 8; offset++)
            {
                if (bytes[offset] != 0x55) continue;
                var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 5)) + 10;
                if (length > Protocol.MaxPayload + 10) { RejectedFrames++; continue; }
                if (bytes.Length - offset < length) { if (incomplete < 0) incomplete = offset; continue; }
                var segment = bytes.AsSpan(offset, length);
                if (Protocol.Crc(segment[..^2]) != BinaryPrimitives.ReadUInt16LittleEndian(segment[^2..]))
                { RejectedFrames++; continue; }
                frames.Add(new Frame(BinaryPrimitives.ReadUInt16LittleEndian(segment[3..]), segment[7], segment[8..^2].ToArray()));
                buffer.RemoveRange(0, offset + length);
                found = true;
                break;
            }
            if (found) continue;
            if (incomplete > 0) buffer.RemoveRange(0, incomplete);
            else if (incomplete < 0 && buffer.Count > 7) buffer.RemoveRange(0, buffer.Count - 7);
            break;
        }
        return frames;
    }
}

public static class DevicePayload
{
    public static (int Percent, bool Charging) Battery(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 || data.Length < 1 + data[0] * 2) throw new ArgumentException("Incomplete battery response.");
        for (var i = 0; i < data[0]; i++)
        {
            if (data[1 + i * 2] != 6) continue;
            var value = data[2 + i * 2];
            if ((value & 127) > 100) throw new ArgumentException("Invalid battery percentage.");
            return (value & 127, (value & 128) != 0);
        }
        throw new ArgumentException("No Headphone (1) battery field.");
    }

    public static byte Anc(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3 || data[0] != 1 || data[1] is not (1 or 2 or 3 or 4 or 5 or 7))
            throw new ArgumentException("Unsupported noise control response.");
        return data[1];
    }
}

public sealed record EqualizerBand(byte Type, float Gain, float Frequency, float Q);

public sealed class EqualizerProfile
{
    public float Preamp { get; set; }
    public EqualizerBand[] Bands { get; set; } = [];
    public static EqualizerProfile Flat() => new()
    {
        Bands = new float[] { 55, 110, 220, 440, 1320, 3300, 6600, 13200 }
            .Select((f, i) => new EqualizerBand((byte)(i == 0 ? 0 : i == 7 ? 2 : 1), 0, f, 1)).ToArray()
    };

    private void Validate()
    {
        if (Bands.Length != 8 || !float.IsFinite(Preamp) || Preamp is < -24 or > 12) throw new ArgumentException("Invalid eight-band profile.");
        foreach (var b in Bands)
            if (b.Type > 2 || !float.IsFinite(b.Gain) || b.Gain is < -12 or > 12 ||
                !float.IsFinite(b.Frequency) || b.Frequency is < 20 or > 24000 ||
                !float.IsFinite(b.Q) || b.Q is <= 0 or > 30) throw new ArgumentException("Unsafe equalizer value.");
    }

    public byte[] Encode()
    {
        Validate();
        var data = new byte[110]; data[0] = 0; data[1] = 8;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(2), Preamp);
        for (var i = 0; i < 8; i++)
        {
            var offset = 6 + i * 13; var b = Bands[i]; data[offset] = b.Type;
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 1), b.Gain);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 5), b.Frequency);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 9), b.Q);
        }
        return data;
    }

    public static EqualizerProfile Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != 110 || data[0] != 0 || data[1] != 8) throw new ArgumentException("Unsupported custom EQ layout.");
        var result = new EqualizerProfile { Preamp = BinaryPrimitives.ReadSingleLittleEndian(data[2..]), Bands = new EqualizerBand[8] };
        for (var i = 0; i < 8; i++)
        {
            var o = 6 + i * 13;
            result.Bands[i] = new(data[o], BinaryPrimitives.ReadSingleLittleEndian(data[(o + 1)..]), BinaryPrimitives.ReadSingleLittleEndian(data[(o + 5)..]), BinaryPrimitives.ReadSingleLittleEndian(data[(o + 9)..]));
        }
        result.Validate(); return result;
    }

    public bool EquivalentTo(EqualizerProfile other) =>
        Math.Abs(Preamp - other.Preamp) < 0.001 && Bands.Length == other.Bands.Length &&
        Bands.Zip(other.Bands).All(pair => pair.First.Type == pair.Second.Type &&
            Math.Abs(pair.First.Gain - pair.Second.Gain) < 0.01 &&
            Math.Abs(pair.First.Frequency - pair.Second.Frequency) < 0.1 && Math.Abs(pair.First.Q - pair.Second.Q) < 0.001);
}
