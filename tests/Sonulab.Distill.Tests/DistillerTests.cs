namespace Sonulab.Distill.Tests;

public class DistillerTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void BestLag_finds_a_known_shift()
    {
        var rng = new Random(11);
        var refY = Enumerable.Range(0, 2000).Select(_ => rng.NextDouble() - 0.5).ToArray();
        // y[i] = ref[i+3]  (y leads ref by 3) -> lag == 3 per _best_lag's slicing convention
        var y = refY.Skip(3).Concat(new double[3]).ToArray();
        Assert.Equal(3, Fidelity.BestLag(refY, y));
    }

    [Fact]
    public void AlignedNrmse_absorbs_gain_polarity_and_delay()
    {
        var rng = new Random(12);
        var refY = Enumerable.Range(0, 4000).Select(_ => rng.NextDouble() - 0.5).ToArray();
        var y = new double[4000];
        for (int i = 7; i < 4000; i++) y[i] = -2.5 * refY[i - 7];   // inverted, scaled, delayed
        Assert.True(Fidelity.AlignedNrmse(refY, y) < 0.05);
        Assert.Equal(0.0, Fidelity.AlignedNrmse(refY, refY), 12);
    }

    [Fact]
    public void LoudnessNormalize_hits_the_device_reference()
    {
        var pre = new float[1024]; pre[0] = 1f;
        var g2 = new float[1024]; g2[0] = 0.05f;   // quiet cascade
        var t = new WhTensors(pre, VxampFormat.G2HeaderFloats(), g2,
                              VxampFormat.NlmixHeaderFloats(), 0f);
        var norm = Distiller.LoudnessNormalize(t);
        var y = DeviceSim.Simulate(norm, DriveSignal.Get());
        Assert.Equal(Distiller.DeviceReferenceDb, Dsp.RmsDb(Dsp.ToDouble(y)), 3);
    }

    [Fact]
    public void Distill_produces_a_valid_slot_with_stages_in_order()
    {
        var stages = new List<DistillStage>();
        var blob = Distiller.Distill(Fixture("synthetic.nam"),
            new SyncProgress(p => stages.Add(p.Stage)));
        Assert.Equal(VxampFormat.SlotSize, blob.Length);
        var t = VxampCodec.Decode(blob);
        Assert.All(t.PreFir.Skip(1008), v => Assert.Equal(0f, v));
        Assert.Equal(new[] { DistillStage.LoadModel, DistillStage.ProbeIr, DistillStage.FitLinear,
                             DistillStage.FitNonlinearity, DistillStage.Normalize, DistillStage.Encode },
                     stages);
    }

    [Fact]
    public async Task DistillAsync_writes_the_file_and_reports_done()
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"distill-test-{Guid.NewGuid():N}.vxamp");
        try
        {
            DistillStage last = DistillStage.LoadModel;
            await Distiller.DistillAsync(Fixture("synthetic.nam"), outPath,
                new SyncProgress(p => last = p.Stage));
            Assert.Equal(VxampFormat.SlotSize, new FileInfo(outPath).Length);
            Assert.Equal(DistillStage.Done, last);
        }
        finally { File.Delete(outPath); }
    }

    [Fact]
    public async Task DistillAsync_honors_pre_cancelled_token()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Distiller.DistillAsync(Fixture("synthetic.nam"), "unused.vxamp", null, cts.Token));
    }

    [Fact]
    public void Bad_nam_throws_DistillException()
    {
        var bad = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.nam");
        File.WriteAllText(bad, """{"architecture": "LSTM", "config": {}, "weights": []}""");
        try
        {
            var ex = Assert.Throws<DistillException>(() => Distiller.Distill(bad));
            Assert.IsType<NamFormatException>(ex.InnerException);
        }
        finally { File.Delete(bad); }
    }
}

/// <summary>Synchronous IProgress (xUnit has no sync context; Progress&lt;T&gt; would race).</summary>
file sealed class SyncProgress(Action<DistillProgress> handler) : IProgress<DistillProgress>
{
    public void Report(DistillProgress value) => handler(value);
}
