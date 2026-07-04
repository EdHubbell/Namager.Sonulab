namespace Sonulab.Distill;

public sealed class NamFormatException(string message) : Exception(message);

internal sealed class NamLayer
{
    public required float[][][] ConvW;   // [mid][ch][k]
    public required float[] ConvB;       // [mid]
    public required float[][] MixinW;    // [mid][cond]  (the 1x1 squeezed)
    public required float[][] OneW;      // [ch][ch]
    public required float[] OneB;        // [ch]
    public required int Dilation;
    public required Func<double, double> Activation;
    public required bool Gated;
    public required int Channels;
}

internal sealed class NamLayerArray
{
    public required float[][] RechannelW;  // [ch][in]
    public required List<NamLayer> Layers;
    public required float[][][] HeadW;     // [headOut][ch][kh]
    public required float[]? HeadB;
}

/// <summary>Numpy-WaveNet forward port (tools/distiller/nam_runner.py). Causal,
/// prewarmed (a receptive field of leading silence) and DC-removed, so
/// silence in -> exactly 0 out.</summary>
public sealed class NamModel : INamProcessor
{
    public required string Arch { get; init; }
    public int? SampleRate { get; set; }
    // Not `required`: a required member's accessor cannot be less visible than the
    // containing (public) type, so these stay plain internal init props — NamParser
    // (same assembly) always supplies both via the object initializer below.
    internal List<NamLayerArray> Arrays { get; init; } = [];
    internal float HeadScale { get; init; }

    private float? _silenceLevel;

    public int ReceptiveField
    {
        get
        {
            int rf = 0;
            foreach (var arr in Arrays)
            {
                foreach (var lyr in arr.Layers)
                    rf += (lyr.ConvW[0][0].Length - 1) * lyr.Dilation;
                rf += arr.HeadW[0][0].Length - 1;
            }
            return rf;
        }
    }

    public float[] Process(float[] x)
    {
        int pad = ReceptiveField, n = x.Length;
        var padded = new float[pad + n];
        x.CopyTo(padded, pad);
        var y = Raw(padded);
        float dc = SilenceLevel();
        var outBuf = new float[n];
        for (int i = 0; i < n; i++) outBuf[i] = y[pad + i] - dc;
        return outBuf;
    }

    private float SilenceLevel()
    {
        if (_silenceLevel is null)
        {
            var y = Raw(new float[ReceptiveField + 8]);
            _silenceLevel = y[^1];
        }
        return _silenceLevel.Value;
    }

    private float[] Raw(float[] x)
    {
        var cond = new[] { x };            // (1, N) — condition = raw input
        var h = cond;
        float[][]? skips = null;
        foreach (var arr in Arrays)
        {
            h = Mul1x1(arr.RechannelW, h, bias: null);
            foreach (var lyr in arr.Layers)
            {
                var z = CausalConv(h, lyr.ConvW, lyr.ConvB, lyr.Dilation);
                AddInPlace(z, Mul1x1(lyr.MixinW, cond, bias: null));
                z = lyr.Gated ? GatedActivate(z, lyr) : Activate(z, lyr.Activation);
                skips = skips is null ? z : Sum(skips, z);
                h = Sum(h, Mul1x1(lyr.OneW, z, lyr.OneB));
            }
            skips = CausalConv(skips!, arr.HeadW, arr.HeadB, dilation: 1);
        }
        var head = skips![0];
        var outBuf = new float[head.Length];
        for (int i = 0; i < head.Length; i++) outBuf[i] = HeadScale * head[i];
        return outBuf;
    }

    /// <summary>Causal dilated conv, PyTorch cross-correlation convention with zero
    /// left-pad: out[n] uses in[<= n]; tap k-1 aligns with the current sample.</summary>
    private static float[][] CausalConv(float[][] x, float[][][] w, float[]? b, int dilation)
    {
        int cout = w.Length, cin = w[0].Length, k = w[0][0].Length, n = x[0].Length;
        var y = new float[cout][];
        for (int o = 0; o < cout; o++)
        {
            var row = new float[n];
            double bo = b?[o] ?? 0.0;
            for (int t = 0; t < n; t++)
            {
                double acc = bo;
                for (int tap = 0; tap < k; tap++)
                {
                    int idx = t - (k - 1 - tap) * dilation;
                    if (idx < 0) continue;
                    var wt = w[o];
                    for (int c = 0; c < cin; c++) acc += wt[c][tap] * x[c][idx];
                }
                row[t] = (float)acc;
            }
            y[o] = row;
        }
        return y;
    }

    private static float[][] Mul1x1(float[][] w, float[][] x, float[]? bias)
    {
        int cout = w.Length, cin = x.Length, n = x[0].Length;
        var y = new float[cout][];
        for (int o = 0; o < cout; o++)
        {
            var row = new float[n];
            double bo = bias?[o] ?? 0.0;
            for (int t = 0; t < n; t++)
            {
                double acc = bo;
                for (int c = 0; c < cin; c++) acc += w[o][c] * x[c][t];
                row[t] = (float)acc;
            }
            y[o] = row;
        }
        return y;
    }

    private static void AddInPlace(float[][] a, float[][] b)
    {
        for (int c = 0; c < a.Length; c++)
            for (int t = 0; t < a[c].Length; t++) a[c][t] += b[c][t];
    }

    private static float[][] Sum(float[][] a, float[][] b)
    {
        var y = new float[a.Length][];
        for (int c = 0; c < a.Length; c++)
        {
            y[c] = new float[a[c].Length];
            for (int t = 0; t < a[c].Length; t++) y[c][t] = a[c][t] + b[c][t];
        }
        return y;
    }

    private static float[][] Activate(float[][] z, Func<double, double> act)
    {
        var y = new float[z.Length][];
        for (int c = 0; c < z.Length; c++)
        {
            y[c] = new float[z[c].Length];
            for (int t = 0; t < z[c].Length; t++) y[c][t] = (float)act(z[c][t]);
        }
        return y;
    }

    private static float[][] GatedActivate(float[][] z, NamLayer lyr)
    {
        int c2 = lyr.Channels;                       // z has 2*c rows: act(z[:c]) * sigmoid(z[c:])
        var y = new float[c2][];
        for (int c = 0; c < c2; c++)
        {
            y[c] = new float[z[c].Length];
            for (int t = 0; t < z[c].Length; t++)
            {
                double a = lyr.Activation(z[c][t]);
                double g = 1.0 / (1.0 + Math.Exp(-z[c + c2][t]));
                y[c][t] = (float)(a * g);
            }
        }
        return y;
    }
}
