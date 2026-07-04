using System.Text.Json;

namespace Sonulab.Distill.Tests;

public class NamModelTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Synthetic_receptive_field_matches_golden()
    {
        // dilations [1,2,4,511], K=3, head K=1: rf = 2*(1+2+4+511) + 0 = 1036
        var model = NamParser.Load(Fixture("synthetic.nam"));
        var golden = JsonDocument.Parse(File.ReadAllText(Fixture("golden_process.json")));
        Assert.Equal(golden.RootElement.GetProperty("receptive_field").GetInt32(),
                     model.ReceptiveField);
        Assert.Equal(1036, model.ReceptiveField);
        Assert.Equal(48000, model.SampleRate);
    }

    [Fact]
    public void Process_matches_python_golden()
    {
        var model = NamParser.Load(Fixture("synthetic.nam"));
        var golden = JsonDocument.Parse(File.ReadAllText(Fixture("golden_process.json"))).RootElement;
        var x = golden.GetProperty("input").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        var expected = golden.GetProperty("output").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        var y = model.Process(x);
        Assert.Equal(expected.Length, y.Length);
        for (int i = 0; i < y.Length; i++)
            Assert.True(Math.Abs(y[i] - expected[i]) <= 1e-5,
                $"sample {i}: cs={y[i]} py={expected[i]}");
    }

    [Fact]
    public void Silence_in_is_exactly_zero_out()
    {
        var model = NamParser.Load(Fixture("synthetic.nam"));
        var y = model.Process(new float[100]);
        Assert.All(y, v => Assert.Equal(0f, v));   // DC removal guarantees this
    }

    [Fact]
    public void Weight_underrun_throws()
    {
        var json = File.ReadAllText(Fixture("synthetic.nam"));
        using var doc = JsonDocument.Parse(json);
        var truncated = json.Replace("\"weights\":", "\"weights_orig\":")
            .Replace("\"architecture\"", "\"weights\": [0.1, 0.2, 0.3], \"architecture\"");
        Assert.Throws<NamFormatException>(() => NamParser.Parse(truncated));
    }

    [Fact]
    public void Unsupported_architecture_throws()
    {
        Assert.Throws<NamFormatException>(() => NamParser.Parse(
            """{"architecture": "LSTM", "config": {}, "weights": []}"""));
    }

    [Fact]
    public void Unsupported_fork_feature_throws()
    {
        // SlimmableContainer whose full submodel uses grouped convolutions
        var json = """
        {"architecture": "SlimmableContainer", "sample_rate": 48000, "config": {"submodels": [
          {"max_value": 1.0, "model": {"architecture": "WaveNet", "config": {"layers": [
            {"input_size": 1, "condition_size": 1, "channels": 2, "groups_input": 2,
             "kernel_sizes": [3], "dilations": [1], "activation": [{"type": "Tanh"}],
             "head": {"out_channels": 1, "kernel_size": 1, "bias": false}}], "head": null},
           "weights": []}}]}}
        """;
        var ex = Assert.Throws<NamFormatException>(() => NamParser.Parse(json));
        Assert.Contains("grouped", ex.Message);
    }
}
