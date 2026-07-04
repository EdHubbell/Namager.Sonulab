namespace Sonulab.Distill.Tests;

public class WavToIrTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "ir-pair", name);

    /// <summary>Minimal PCM WAV writer for test inputs (16-bit int, 24-bit int, or 32-bit float).</summary>
    private static string WriteWav(int sampleRate, int channels, int bits, float[] mono, bool floatFmt = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wavtoir-{Guid.NewGuid():N}.wav");
        int bytesPer = bits / 8;
        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        int dataLen = mono.Length * channels * bytesPer;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)(floatFmt ? 3 : 1)); w.Write((short)channels);
        w.Write(sampleRate); w.Write(sampleRate * channels * bytesPer);
        w.Write((short)(channels * bytesPer)); w.Write((short)bits);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var v in mono)
            for (int c = 0; c < channels; c++)
            {
                if (floatFmt) w.Write(v);
                else if (bits == 16) w.Write((short)Math.Clamp((int)Math.Round(v * 32767.0), short.MinValue, short.MaxValue));
                else if (bits == 24)
                {
                    int q = Math.Clamp((int)Math.Round(v * 8388607.0), -8388608, 8388607);
                    w.Write((byte)q); w.Write((byte)(q >> 8)); w.Write((byte)(q >> 16));
                }
            }
        return path;
    }

    /// <summary>WAVE_FORMAT_EXTENSIBLE wrapper: fmt size 40, tag 0xFFFE, tail = cbSize(22),
    /// validBits, channelMask, then the KSDATAFORMAT SubFormat GUID whose first 2 bytes are
    /// the real tag (1 = PCM).</summary>
    private static string WriteExtensibleWav16(int sampleRate, float[] mono)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wavtoir-ext-{Guid.NewGuid():N}.wav");
        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        int dataLen = mono.Length * 2;
        w.Write("RIFF"u8); w.Write(60 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(40); w.Write(unchecked((short)0xFFFE)); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write((short)22);                       // cbSize
        w.Write((short)16);                       // wValidBitsPerSample
        w.Write(4);                               // dwChannelMask (mono, FRONT_CENTER)
        w.Write((short)1);                        // SubFormat GUID bytes 0-1: PCM tag
        w.Write((short)0); w.Write(0x00100000); w.Write(0xAA000080u); w.Write(0x719B3800u); // rest of KSDATAFORMAT_SUBTYPE_PCM
        w.Write("data"u8); w.Write(dataLen);
        foreach (var v in mono)
            w.Write((short)Math.Clamp((int)Math.Round(v * 32767.0), short.MinValue, short.MaxValue));
        return path;
    }

    private static float[] Ramp(int n) =>
        Enumerable.Range(0, n).Select(i => (float)Math.Sin(i * 0.02) * 0.7f).ToArray();

    /// <summary>L2 norm of the first SampleCount samples (or fewer, zero-padded) — mirrors the
    /// pinned scaling rule (docs/ir-format.md) so tests can predict the normalized output.</summary>
    private static double WindowL2Norm(float[] samples)
    {
        double sumSq = 0;
        for (int i = 0; i < Math.Min(samples.Length, IrFormat.SampleCount); i++)
            sumSq += (double)samples[i] * samples[i];
        return Math.Sqrt(sumSq);
    }

    [Fact]
    public void Sixteen_bit_mono_44k1_roundtrips_through_encode()
    {
        var full = Ramp(IrFormat.SampleCount * 2);
        var wav = WriteWav(44100, 1, 16, full);
        try
        {
            var blob = WavToIr.Convert(wav);
            Assert.Equal(IrFormat.BlobBytes, blob.Length);
            var dec = IrFormat.Decode(blob);
            var expect = full;
            double norm = WindowL2Norm(full);          // unit-L2 rule (gate 1) applies to the truncated window
            for (int i = 0; i < 32; i++) Assert.Equal(expect[i] / norm, dec[i], 3);   // 16-bit quantization tolerance
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public void Stereo_is_averaged_and_float32_is_read_exactly()
    {
        var samples = Ramp(IrFormat.SampleCount);
        var wav = WriteWav(44100, 2, 32, samples, floatFmt: true);
        try
        {
            var dec = IrFormat.Decode(WavToIr.Convert(wav));
            double norm = WindowL2Norm(samples);        // unit-L2 rule (gate 1) applies to the truncated window
            for (int i = 0; i < 32; i++) Assert.Equal(samples[i] / norm, dec[i], 5);  // L==R -> mean == sample
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public void Foreign_sample_rate_is_resampled_to_44k1()
    {
        var wav = WriteWav(48000, 1, 16, Ramp(4800));                          // 100 ms at 48k
        try
        {
            var mono = WavToIr.ReadWavMono44k1(wav);
            Assert.InRange(mono.Length, 4380, 4440);                            // ~4410 samples after 48k->44.1k
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public void Short_wav_is_zero_padded_to_SampleCount()
    {
        var wav = WriteWav(44100, 1, 16, Ramp(100));
        try
        {
            var dec = IrFormat.Decode(WavToIr.Convert(wav));
            Assert.NotEqual(0.0, dec[50]);
            Assert.All(dec.Skip(200), v => Assert.Equal(0.0, v));
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public void Malformed_wav_throws_InvalidDataException()
    {
        var bad = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4 });
        try { Assert.Throws<InvalidDataException>(() => WavToIr.Convert(bad)); }
        finally { File.Delete(bad); }
    }

    [Fact]
    public void Dobro_pair_wav_converts_to_the_device_blob()
    {
        // THE arbiter: converting Ed's source .wav must reproduce what VoidX uploaded.
        var blob = WavToIr.Convert(Fixture("source.wav"));
        var device = File.ReadAllBytes(Fixture("device.irblob"));
        var a = IrFormat.Decode(blob);
        var b = IrFormat.Decode(device);
        double corr = Pearson(a, b);
        Assert.True(corr > 0.999, $"conversion vs device dump corr={corr:F5} — scaling/truncation rule is off (see docs/ir-format.md)");
    }

    [Fact]
    public void Extensible_format_pcm16_is_read()
    {
        var samples = Enumerable.Range(0, 256).Select(i => (float)Math.Sin(i * 0.02) * 0.7f).ToArray();
        var wav = WriteExtensibleWav16(44100, samples);
        try
        {
            var mono = WavToIr.ReadWavMono44k1(wav);
            Assert.Equal(256, mono.Length);
            Assert.Equal(samples[10], mono[10], 3);
        }
        finally { File.Delete(wav); }
    }

    private static double Pearson(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), cov = 0, va = 0, vb = 0;
        for (int i = 0; i < a.Length; i++)
        { cov += (a[i] - ma) * (b[i] - mb); va += (a[i] - ma) * (a[i] - ma); vb += (b[i] - mb) * (b[i] - mb); }
        return va <= 0 || vb <= 0 ? 0 : cov / Math.Sqrt(va * vb);
    }
}
