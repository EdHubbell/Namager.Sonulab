namespace Sonulab.Distill;

/// <summary>.wav -> root\ir device blob (format pinned in docs/ir-format.md, gate 1).
/// PCM 16/24-bit int and 32-bit IEEE float, mono or multi-channel (averaged),
/// any sample rate (polyphase-resampled to 44100 Hz), truncated/zero-padded to
/// IrFormat.SampleCount, unit-L2-normalized over that window, then encoded per IrFormat.</summary>
public static class WavToIr
{
    public static byte[] Convert(string wavPath)
    {
        var mono = ReadWavMono44k1(wavPath);
        var samples = new double[IrFormat.SampleCount];
        Array.Copy(mono, samples, Math.Min(mono.Length, IrFormat.SampleCount));
        // Scaling rule per docs/ir-format.md (gate 1): the device stores the truncated
        // 1024-sample window scaled to unit L2 norm (sum(s_i^2) == 1.0), confirmed exact
        // (gain === 1/L2norm(window)) against all 4 dobro wav/blob pairs.
        samples = IrFormat.NormalizeUnitL2(samples);
        return IrFormat.Encode(samples);
    }

    public static double[] ReadWavMono44k1(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs);
        try
        {
            if (r.ReadUInt32() != 0x46464952u) throw new InvalidDataException("Not a RIFF file.");        // "RIFF"
            r.ReadUInt32();                                                                                // riff size
            if (r.ReadUInt32() != 0x45564157u) throw new InvalidDataException("Not a WAVE file.");         // "WAVE"

            short format = 0, channels = 0, bits = 0;
            int sampleRate = 0;
            byte[]? data = null;
            while (fs.Position + 8 <= fs.Length)
            {
                uint id = r.ReadUInt32();
                int size = r.ReadInt32();
                if (size < 0 || fs.Position + size > fs.Length)
                    throw new InvalidDataException($"Corrupt WAV: chunk size {size} exceeds file bounds.");
                if (id == 0x20746D66u)                                                                     // "fmt "
                {
                    if (size < 16) throw new InvalidDataException("Corrupt WAV: fmt chunk too small.");
                    long next = fs.Position + size + (size & 1);
                    format = r.ReadInt16(); channels = r.ReadInt16(); sampleRate = r.ReadInt32();
                    r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                    if (format == unchecked((short)0xFFFE) && size >= 40)                                  // WAVE_FORMAT_EXTENSIBLE
                    { r.ReadInt16(); r.ReadInt16(); r.ReadInt32(); format = r.ReadInt16(); }               // cbSize, validBits, channelMask, then SubFormat GUID's leading tag
                    fs.Position = next;
                }
                else if (id == 0x61746164u) { data = r.ReadBytes(size); break; }                            // "data"
                else fs.Position += size + (size & 1);                                                      // skip (word-aligned)
            }
            if (data is null || channels <= 0 || sampleRate <= 0)
                throw new InvalidDataException("WAV has no readable fmt/data chunks.");

            double[] frames = (format, bits) switch
            {
                (1, 16) => Pcm16(data),
                (1, 24) => Pcm24(data),
                (3, 32) => Float32(data),
                _ => throw new InvalidDataException($"Unsupported WAV format (tag {format}, {bits}-bit) — use 16/24-bit PCM or 32-bit float."),
            };

            var mono = new double[frames.Length / channels];
            for (int i = 0; i < mono.Length; i++)
            {
                double acc = 0;
                for (int c = 0; c < channels; c++) acc += frames[i * channels + c];
                mono[i] = acc / channels;
            }
            return sampleRate == 44100 ? mono : Resampler.ResamplePoly(mono, 44100, sampleRate);
        }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated WAV file.", e); }
    }

    private static double[] Pcm16(byte[] d)
    {
        var v = new double[d.Length / 2];
        for (int i = 0; i < v.Length; i++) v[i] = BitConverter.ToInt16(d, i * 2) / 32768.0;
        return v;
    }

    private static double[] Pcm24(byte[] d)
    {
        var v = new double[d.Length / 3];
        for (int i = 0; i < v.Length; i++)
        {
            int q = d[i * 3] | (d[i * 3 + 1] << 8) | (d[i * 3 + 2] << 16);
            if (q >= 1 << 23) q -= 1 << 24;
            v[i] = q / 8388608.0;
        }
        return v;
    }

    private static double[] Float32(byte[] d)
    {
        var v = new double[d.Length / 4];
        for (int i = 0; i < v.Length; i++) v[i] = BitConverter.ToSingle(d, i * 4);
        return v;
    }
}
