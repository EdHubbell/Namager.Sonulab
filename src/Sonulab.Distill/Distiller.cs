namespace Sonulab.Distill;

public enum DistillStage { LoadModel, ProbeIr, FitLinear, FitNonlinearity, Normalize, Encode, Done }

public sealed record DistillProgress(DistillStage Stage, string Message);

public sealed class DistillException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>End-to-end .nam -> .vxamp distiller (port of tools/distiller/distill.py).
/// Cancellation is honored BETWEEN DSP stages; each stage runs to completion.</summary>
public static class Distiller
{
    /// <summary>Median VoidX output loudness on the 0.3-RMS reference signal.
    /// Baked from the Python oracle: distill.device_reference_db() == 13.531918973745606
    /// (corpus median over 14 pairs, computed 2026-07-03 via make_cs_fixtures.py; the
    /// Python docstring's "+13.6 dBFS" is this value rounded). The C# distiller
    /// therefore needs no corpus at runtime.</summary>
    public const double DeviceReferenceDb = 13.531918973745606;

    public static WhTensors LoudnessNormalize(WhTensors t)
    {
        var y = DeviceSim.Simulate(t, DriveSignal.Get());
        double gain = Math.Pow(10.0, (DeviceReferenceDb - Dsp.RmsDb(Dsp.ToDouble(y))) / 20.0);
        var g2 = new float[t.G2Fir.Length];
        for (int i = 0; i < g2.Length; i++) g2[i] = (float)(t.G2Fir[i] * gain);
        return t with { G2Fir = g2 };
    }

    public static byte[] Distill(string namPath, IProgress<DistillProgress>? progress = null,
                                 CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(DistillStage.LoadModel, "Loading NAM model…"));
            var model = NamParser.Load(namPath);
            model.SampleRate ??= FirFitter.NamDefaultSampleRate;   // NAM ecosystem default

            ct.ThrowIfCancellationRequested();
            progress?.Report(new(DistillStage.ProbeIr, "Probing small-signal response…"));
            var ir = Dsp.ToDouble(Probe.LinearIrOfModel(model, n: 8192));
            if (model.SampleRate != DeviceSim.SampleRate)
                ir = Resampler.ResamplePoly(ir, DeviceSim.SampleRate, model.SampleRate.Value);

            ct.ThrowIfCancellationRequested();
            progress?.Report(new(DistillStage.FitLinear, "Designing FIR cascade…"));
            var (pre, g2) = FirFitter.DesignLinear(ir);

            ct.ThrowIfCancellationRequested();
            progress?.Report(new(DistillStage.FitNonlinearity, "Fitting nonlinearity…"));
            var (s, gain) = FirFitter.FitNl(model, pre, g2);
            var g2Cal = new float[g2.Length];
            for (int i = 0; i < g2.Length; i++) g2Cal[i] = (float)(g2[i] * gain);
            var tensors = new WhTensors(pre, VxampFormat.G2HeaderFloats(), g2Cal,
                                        VxampFormat.NlmixHeaderFloats(), (float)s);

            ct.ThrowIfCancellationRequested();
            progress?.Report(new(DistillStage.Normalize, "Calibrating loudness…"));
            tensors = LoudnessNormalize(tensors);

            ct.ThrowIfCancellationRequested();
            progress?.Report(new(DistillStage.Encode, "Encoding .vxamp…"));
            return VxampCodec.Encode(tensors);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            throw new DistillException($"Distillation failed: {e.Message}", e);
        }
    }

    public static Task DistillAsync(string namPath, string outPath,
                                    IProgress<DistillProgress>? progress = null,
                                    CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var blob = Distill(namPath, progress, ct);
            File.WriteAllBytes(outPath, blob);
            progress?.Report(new(DistillStage.Done, "Done."));
        }, ct);
}
