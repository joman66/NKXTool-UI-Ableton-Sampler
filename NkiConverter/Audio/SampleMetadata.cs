using System.Buffers.Binary;
using System.Text;

namespace NkiConverter.Audio;

/// <summary>Root key and loops which sample editors store inside WAV ('smpl' chunk) and AIFF ('INST' / 'MARK') files.</summary>
public sealed class SampleMetadata
{
    /// <summary>MIDI root note, or -1 when the file does not contain one.</summary>
    public int RootKey { get; init; } = -1;

    /// <summary>Fine tuning of the root key in cents (-50..50).</summary>
    public double RootTuningCents { get; init; }

    public List<(int Start, int End, bool Alternating)> Loops { get; } = [];

    public static SampleMetadata Read(string file)
    {
        try
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            return extension switch
            {
                ".wav" => ReadWav(file),
                ".aif" or ".aiff" => ReadAiff(file),
                _ => new SampleMetadata()
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return new SampleMetadata();
        }
    }

    private static SampleMetadata ReadWav(string file)
    {
        using var stream = File.OpenRead(file);
        var reader = new BinaryReader(stream);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) is not ("RIFF" or "RF64"))
        {
            return new SampleMetadata();
        }

        reader.ReadUInt32();
        reader.ReadBytes(4);
        while (stream.Position + 8 <= stream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            long size = reader.ReadUInt32();
            var start = stream.Position;
            if (id == "smpl" && size >= 36)
            {
                reader.ReadUInt32(); // manufacturer
                reader.ReadUInt32(); // product
                reader.ReadUInt32(); // sample period
                var unityNote = (int)reader.ReadUInt32();
                var pitchFraction = reader.ReadUInt32();
                reader.ReadUInt32(); // SMPTE format
                reader.ReadUInt32(); // SMPTE offset
                var numLoops = (int)reader.ReadUInt32();
                reader.ReadUInt32(); // sampler data
                var cents = pitchFraction / (double)0x80000000 * 50.0;
                var metadata = new SampleMetadata
                {
                    RootKey = unityNote is >= 0 and <= 127 ? unityNote : -1,
                    // The pitch fraction raises the pitch of the sample, i.e. the root note is played slightly flat
                    RootTuningCents = cents
                };

                for (var i = 0; i < numLoops && stream.Position + 24 <= start + size; i++)
                {
                    reader.ReadUInt32(); // cue point ID
                    var type = reader.ReadUInt32();
                    var loopStart = (int)reader.ReadUInt32();
                    var loopEnd = (int)reader.ReadUInt32(); // inclusive
                    reader.ReadUInt32(); // fraction
                    reader.ReadUInt32(); // play count
                    if (loopEnd > loopStart)
                    {
                        metadata.Loops.Add((loopStart, loopEnd + 1, type == 1));
                    }
                }

                return metadata;
            }

            stream.Position = start + size + (size & 1);
        }

        return new SampleMetadata();
    }

    private static SampleMetadata ReadAiff(string file)
    {
        var data = File.ReadAllBytes(file);
        if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 4) != "FORM")
        {
            return new SampleMetadata();
        }

        var markers = new Dictionary<int, int>();
        int baseNote = -1, detune = 0;
        int sustainMode = 0, sustainBegin = -1, sustainEnd = -1;

        var pos = 12;
        while (pos + 8 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data, pos, 4);
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4));
            var body = pos + 8;
            if (size < 0 || body + size > data.Length)
            {
                break;
            }

            if (id == "INST" && size >= 20)
            {
                baseNote = data[body];
                detune = (sbyte)data[body + 1];
                sustainMode = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(body + 8));
                sustainBegin = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(body + 10));
                sustainEnd = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(body + 12));
            }
            else if (id == "MARK" && size >= 2)
            {
                var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(body));
                var p = body + 2;
                for (var i = 0; i < count && p + 7 <= body + size; i++)
                {
                    var markerId = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(p));
                    var position = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p + 2));
                    var nameLength = data[p + 6];
                    markers[markerId] = position;
                    var total = 7 + nameLength;
                    p += total + (total & 1); // pascal string padded to even length
                }
            }

            pos = body + size + (size & 1);
        }

        var metadata = new SampleMetadata
        {
            RootKey = baseNote is >= 0 and <= 127 ? baseNote : -1,
            RootTuningCents = -detune
        };

        if (sustainMode != 0 && markers.TryGetValue(sustainBegin, out var loopStart) && markers.TryGetValue(sustainEnd, out var loopEnd) && loopEnd > loopStart)
        {
            metadata.Loops.Add((loopStart, loopEnd, sustainMode == 2));
        }

        return metadata;
    }
}
