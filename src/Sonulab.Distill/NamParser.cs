using System.Text.Json;

namespace Sonulab.Distill;

/// <summary>.nam JSON -> NamModel (port of nam_runner.py's parsers + _WeightReader).
/// Weight order is the contract; any unsupported feature throws NamFormatException
/// rather than silently mis-rendering.</summary>
public static class NamParser
{
    public static NamModel Load(string path) => Parse(File.ReadAllText(path));

    public static NamModel Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string arch = root.GetProperty("architecture").GetString()
                      ?? throw new NamFormatException("missing architecture");
        int? sr = root.TryGetProperty("sample_rate", out var srEl) && srEl.ValueKind == JsonValueKind.Number
            ? (int)srEl.GetDouble() : null;

        List<NamLayerArray> arrays;
        float headScale;
        if (arch == "WaveNet")
        {
            var r = new WeightReader(root.GetProperty("weights"));
            (arrays, headScale) = ParseStandard(root.GetProperty("config"), r);
        }
        else if (arch == "SlimmableContainer")
        {
            var subs = root.GetProperty("config").GetProperty("submodels");
            int idx = FullSubmodelIndex(subs);
            var sub = subs[idx].GetProperty("model");
            string subArch = sub.GetProperty("architecture").GetString() ?? "";
            Require(subArch == "WaveNet", $"submodel architecture {subArch}");
            if (sr is null && sub.TryGetProperty("sample_rate", out var s2) && s2.ValueKind == JsonValueKind.Number)
                sr = (int)s2.GetDouble();
            var r = new WeightReader(sub.GetProperty("weights"));
            (arrays, headScale) = ParseFork(sub.GetProperty("config"), r);
        }
        else throw new NamFormatException($"unhandled architecture: {arch}");

        return new NamModel { Arch = arch, SampleRate = sr, Arrays = arrays, HeadScale = headScale };
    }

    private static int FullSubmodelIndex(JsonElement subs)
    {
        int best = 0; double bestV = double.MinValue;
        for (int i = 0; i < subs.GetArrayLength(); i++)
        {
            double v = subs[i].GetProperty("max_value").GetDouble();
            if (v > bestV) { bestV = v; best = i; }
        }
        Require(bestV == 1.0, $"no full submodel (max max_value = {bestV})");
        return best;
    }

    private static (List<NamLayerArray>, float) ParseStandard(JsonElement config, WeightReader r)
    {
        Require(IsNullOrAbsent(config, "head"), "top-level WaveNet head module");
        var arrays = new List<NamLayerArray>();
        foreach (var lg in config.GetProperty("layers").EnumerateArray())
        {
            int cin = lg.GetProperty("input_size").GetInt32();
            int cond = lg.GetProperty("condition_size").GetInt32();
            int ch = lg.GetProperty("channels").GetInt32();
            int k = lg.GetProperty("kernel_size").GetInt32();
            bool gated = lg.GetProperty("gated").GetBoolean();
            int mid = gated ? 2 * ch : ch;
            var act = MakeActivation(lg.GetProperty("activation"));

            var rechannel = r.Take2(ch, cin);
            var layers = new List<NamLayer>();
            foreach (var d in lg.GetProperty("dilations").EnumerateArray())
            {
                layers.Add(new NamLayer
                {
                    ConvW = r.Take3(mid, ch, k), ConvB = r.Take1(mid),
                    MixinW = r.Take2(mid, cond),
                    OneW = r.Take2(ch, ch), OneB = r.Take1(ch),
                    Dilation = d.GetInt32(), Activation = act, Gated = gated, Channels = ch,
                });
            }
            int headSize = lg.GetProperty("head_size").GetInt32();
            var headW = r.Take3(headSize, ch, 1);
            var headB = lg.GetProperty("head_bias").GetBoolean() ? r.Take1(headSize) : null;
            arrays.Add(new NamLayerArray { RechannelW = rechannel, Layers = layers, HeadW = headW, HeadB = headB });
        }
        float headScale = r.Take1(1)[0];
        Require(r.Remaining == 0, $"{r.Remaining} unconsumed weights");
        return (arrays, headScale);
    }

    private static (List<NamLayerArray>, float) ParseFork(JsonElement config, WeightReader r)
    {
        Require(IsNullOrAbsent(config, "head"), "top-level fork head module");
        var arrays = new List<NamLayerArray>();
        foreach (var lg in config.GetProperty("layers").EnumerateArray())
        {
            int cin = lg.GetProperty("input_size").GetInt32();
            int cond = lg.GetProperty("condition_size").GetInt32();
            int ch = lg.GetProperty("channels").GetInt32();
            Require(GetIntOr(lg, "bottleneck", ch) == ch, $"bottleneck {GetIntOr(lg, "bottleneck", ch)} != channels");
            Require(GetIntOr(lg, "groups_input", 1) == 1 && GetIntOr(lg, "groups_input_mixin", 1) == 1,
                    "grouped convolutions");
            Require(!ActiveFlag(lg, "head1x1", false), "active head1x1");
            Require(ActiveFlag(lg, "layer1x1", true) && GroupsOf(lg, "layer1x1") == 1, "layer1x1 variant");
            foreach (var f in new[] { "conv_pre_film", "conv_post_film", "input_mixin_pre_film",
                     "input_mixin_post_film", "activation_pre_film", "activation_post_film",
                     "layer1x1_post_film", "head1x1_post_film" })
                Require(!ActiveFlag(lg, f, false), $"active {f}");

            var kernels = lg.GetProperty("kernel_sizes").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            if (lg.TryGetProperty("gating_mode", out var gm))
                foreach (var g in gm.EnumerateArray())
                    Require(g.GetString() == "none", $"gating_mode {g.GetString()}");
            if (lg.TryGetProperty("secondary_activation", out var sa) && sa.ValueKind == JsonValueKind.Array)
                foreach (var s in sa.EnumerateArray())
                    Require(s.ValueKind == JsonValueKind.Null, "secondary_activation");

            var rechannel = r.Take2(ch, cin);
            var dils = lg.GetProperty("dilations").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var acts = lg.GetProperty("activation").EnumerateArray().ToArray();
            var layers = new List<NamLayer>();
            for (int i = 0; i < kernels.Length; i++)
            {
                layers.Add(new NamLayer
                {
                    ConvW = r.Take3(ch, ch, kernels[i]), ConvB = r.Take1(ch),
                    MixinW = r.Take2(ch, cond),
                    OneW = r.Take2(ch, ch), OneB = r.Take1(ch),
                    Dilation = dils[i], Activation = MakeActivation(acts[i]), Gated = false, Channels = ch,
                });
            }
            var hd = lg.GetProperty("head");
            int hout = hd.GetProperty("out_channels").GetInt32();
            int hk = hd.GetProperty("kernel_size").GetInt32();
            var headW = r.Take3(hout, ch, hk);
            var headB = hd.TryGetProperty("bias", out var hb) && hb.GetBoolean() ? r.Take1(hout) : null;
            arrays.Add(new NamLayerArray { RechannelW = rechannel, Layers = layers, HeadW = headW, HeadB = headB });
        }
        float headScale = r.Take1(1)[0];
        Require(r.Remaining == 0, $"{r.Remaining} unconsumed weights");
        return (arrays, headScale);
    }

    private static Func<double, double> MakeActivation(JsonElement spec)
    {
        string kind;
        double slope = 0.01;
        if (spec.ValueKind == JsonValueKind.Object)
        {
            kind = spec.GetProperty("type").GetString() ?? "";
            if (kind == "LeakyReLU" && spec.TryGetProperty("negative_slope", out var ns))
                slope = ns.GetDouble();
        }
        else kind = spec.GetString() ?? "";
        return kind switch
        {
            "Tanh" => Math.Tanh,
            "ReLU" => z => Math.Max(z, 0.0),
            "Sigmoid" => z => 1.0 / (1.0 + Math.Exp(-z)),
            "Hardtanh" => z => Math.Clamp(z, -1.0, 1.0),
            "LeakyReLU" => z => z >= 0.0 ? z : (float)slope * z,
            _ => throw new NamFormatException($"unsupported activation: {kind}"),
        };
    }

    private static bool IsNullOrAbsent(JsonElement e, string prop) =>
        !e.TryGetProperty(prop, out var v) || v.ValueKind == JsonValueKind.Null;

    private static int GetIntOr(JsonElement e, string prop, int fallback) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    private static bool ActiveFlag(JsonElement e, string prop, bool fallback) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object
            ? v.TryGetProperty("active", out var a) ? a.GetBoolean() : fallback
            : fallback;

    private static int GroupsOf(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object
            ? (v.TryGetProperty("groups", out var g) ? g.GetInt32() : 1) : 1;

    private static void Require(bool cond, string what)
    {
        if (!cond) throw new NamFormatException($"unsupported .nam feature: {what}");
    }

    /// <summary>Flat-weight consumption in declaration order (port of _WeightReader).</summary>
    private sealed class WeightReader(JsonElement weights)
    {
        private readonly float[] _flat = weights.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        private int _pos;

        public int Remaining => _flat.Length - _pos;

        public float[] Take1(int n)
        {
            if (_pos + n > _flat.Length)
                throw new NamFormatException($"weight underrun: wanted {n} at {_pos}, have {_flat.Length}");
            var a = _flat[_pos..(_pos + n)];
            _pos += n;
            return a;
        }

        public float[][] Take2(int a, int b)
        {
            var y = new float[a][];
            for (int i = 0; i < a; i++) y[i] = Take1(b);
            return y;
        }

        public float[][][] Take3(int a, int b, int c)
        {
            var y = new float[a][][];
            for (int i = 0; i < a; i++) y[i] = Take2(b, c);
            return y;
        }
    }
}
