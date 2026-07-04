namespace Sonulab.Distill;

/// <summary>The five named float32 tensors of a device amp model (see tools/vxamp-re/arch.py).</summary>
public sealed record WhTensors(
    float[] PreFir,        // 1024 taps, short tone-shaping FIR; taps 1008..1023 always 0
    float[] G2Header,      // 3 floats = TLV chunk header bytes (metadata, not DSP)
    float[] G2Fir,         // 1024 taps, cab/speaker IR (carries the calibrated gain)
    float[] NlmixHeader,   // 4 floats = TLV chunk header bytes (metadata, not DSP)
    float Nlmix);          // nonlinear-mix scalar, 0 = fully linear
