# Native C# Distiller (Sonulab.Distill) Implementation Plan — 2b Phase 1

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the Python `.nam → .vxamp` distiller (`tools/distiller/` + the encoder half of `tools/vxamp-re/`) to a native C# library `src/Sonulab.Distill`, parity-validated against the Python oracle, so the app never shells out to Python.

**Architecture:** New class library `Sonulab.Distill` (no UI, no device I/O) + `tests/Sonulab.Distill.Tests`. Module-for-module port: codec (XOR keystream + TLV container), DSP primitives (FFT/convolution/FIR), a scipy-exact polyphase resampler, the pinned nonlinearity, the device simulator, the NAM WaveNet runner, the Wiener–Hammerstein fitter, and a `Distiller` orchestrator with staged progress + cancellation. The Python distiller stays untouched as the reference oracle; parity is enforced by golden tests.

**Tech Stack:** .NET 10 (`net10.0`), xUnit 2.9.3, MathNet.Numerics 5.0.0 (FFT only, referenced ONLY by Sonulab.Distill), System.Text.Json (built in).

## Global Constraints

- Every project: `net10.0`, `ImplicitUsings` enable, `Nullable` enable (mirror `src/Sonulab.Core/Sonulab.Core.csproj`).
- The ONLY new NuGet dependency is `MathNet.Numerics` 5.0.0, referenced ONLY by `src/Sonulab.Distill`. Do NOT add FluentAvalonia or any UI package.
- **Numerics rule:** compute in `double` internally; materialize `float` (float32) exactly where the Python code materializes a float32 array (`.astype(np.float32)` points). These cast points are called out per task — they matter for parity.
- **FFT convention:** numpy's (unscaled forward, 1/N inverse) = MathNet `FourierOptions.Matlab`. Always pass it.
- `tools/distiller/` and `tools/vxamp-re/` are the reference oracle: do NOT modify any existing file there. Task 8 ADDS one new script (`tools/distiller/make_cs_fixtures.py`); that is the only allowed change under `tools/`.
- All commands run from the repo root `C:\Development\Buckdrivers\Sonulab\StompStationManager`. Shell is PowerShell.
- `dotnet build` and `dotnet test` must be green at the end of every task (146 existing tests + the new ones).
- Commit after every task (message prefix `distill-cs:`).
- Python reference files (read them, port them): `tools/distiller/{distill,fit,nam_runner,nonlinearity,probe,device_sim}.py`, `tools/vxamp-re/{nam_to_vxamp,codec,arch,decode_body,vxamp}.py`. Each is heavily commented; when this plan says "port function X", the Python body of X is the authoritative algorithm and the plan lists the C#-specific pitfalls.

## Baked oracle constants (generated 2026-07-03, scipy 1.17.1 / numpy ≥1.26)

These were computed from the Python oracle on the corpus machine; they are the runtime constants that free the C# distiller from needing the corpus:

- `DeviceReferenceDb = 13.531918973745606` — `distill.device_reference_db()` exact float64 (median VoidX loudness on the 0.3-RMS reference; see distill.py docstring).
- Drive/reference signal — `np.random.default_rng(0).standard_normal(16000) * 0.3` as float32. NOT portable to C# (PCG64+ziggurat); embedded as a 64,000-byte resource in Task 8. Used identically by `fit._fit_nl` and `distill._drive_signal`.
- `g2_header` bytes (TLV, corpus-constant): `0C 10 00 00 00 00 00 00 47 32 00 00` (u32 len=0x100C, u32 0, tag "G2\0\0") = 3 float32.
- `nlmix_header` bytes: `14 00 00 00 00 00 00 00 6E 6C 6D 69 78 00 00 00` (u32 len=0x14, u32 0, tag "nlmix\0\0\0") = 4 float32.
- Slot header (32 bytes, hex): `4020000000000000416d70206d6f64656c000000797653442122ff009ae7c4be`.
- XOR keystream: `k[i] = (byte)((K0[i % 32] - 0x20 * (i / 32)) mod 256)` with `K0 = 99 97 77 6f 67 44 45 22 21 02 01 de dd bf ab a2 93 86 63 64 55 46 33 24 01 02 df e0 bd b6 a4 9e`.
- Sizes: slot 12288 B = 32 B header + 8224 B obfuscated body + zero pad; body = 2056 float32 LE: `pre_fir` 1024, `g2_header` 3, `g2_fir` 1024, `nlmix_header` 4, `nlmix` 1 (in that order).

## Parity tolerances (Task 12; rationale)

float32 accumulation order in the WaveNet forward differs between numpy BLAS and C# loops, so bit-exactness is impossible by design. Gates:

- per-FIR-tensor relative L2 (`‖cs − py‖₂ / (‖py‖₂ + 1e−12)`): ≤ 1e-3
- `nlmix`: |Δ| ≤ 0.010001 (one grid step)
- fidelity metric `our_err`: |Δ| ≤ 1e-3

If a parity test fails marginally, use superpowers:systematic-debugging to find the divergence point (compare stage-by-stage against Python) — do NOT loosen a tolerance beyond 5e-3 without user sign-off.

## File Structure

```
src/Sonulab.Distill/
  Sonulab.Distill.csproj          (Task 1)
  VxampFormat.cs                  (Task 2)  constants, keystream, header/TLV bytes
  WhTensors.cs                    (Task 2)  the 5-tensor model record
  VxampCodec.cs                   (Task 2)  Encode/Decode 12288-byte slots
  Dsp.cs                          (Task 3)  Convolve, FirFilter, Rms/RmsDb, Norm, Dot, Fft/Ifft wrappers
  Resampler.cs                    (Task 4)  BesselI0, Kaiser, Firwin, Upfirdn, ResamplePoly (scipy-exact)
  Nonlinearity.cs                 (Task 5)  ApplyNl
  DeviceSim.cs                    (Task 6)  Simulate, LinearIr, SampleRate=44100
  Probe.cs                        (Task 7)  LinearIrOfModel, Logmag, LogmagCorr
  Resources/drive_signal.f32      (Task 8)  16000 float32 LE, embedded resource
  NamModel.cs                     (Task 9)  model + Process (prewarm + DC removal)
  NamParser.cs                    (Task 9)  .nam JSON → NamModel (WaveNet + SlimmableContainer)
  FirFitter.cs                    (Task 10) DesignLinear, FitNl, FitWh
  Fidelity.cs                     (Task 11) BestLag, AlignedNrmse, ShapeErr, FidelityVsNam
  Distiller.cs                    (Task 11) Distill, DistillAsync, LoudnessNormalize, DistillException, DistillProgress
tests/Sonulab.Distill.Tests/
  Sonulab.Distill.Tests.csproj    (Task 1)
  VxampCodecTests.cs              (Task 2)
  DspTests.cs                     (Task 3)
  ResamplerTests.cs               (Task 4)
  NonlinearityTests.cs            (Task 5)
  DeviceSimTests.cs               (Task 6)
  ProbeTests.cs                   (Task 7)
  fixtures/                       (Task 8)  synthetic.nam, synthetic.golden.vxamp, golden_process.json, golden_metrics.json
  NamModelTests.cs                (Task 9)
  FirFitterTests.cs               (Task 10)
  DistillerTests.cs               (Task 11)
  ParityTests.cs                  (Task 12)
tools/distiller/make_cs_fixtures.py (Task 8) fixture/golden generator (committed; run on the corpus machine)
Sonulab.slnx                      (Task 1)  + 2 project entries
```

---

### Task 1: Scaffold Sonulab.Distill + test project

**Files:**
- Create: `src/Sonulab.Distill/Sonulab.Distill.csproj`
- Create: `tests/Sonulab.Distill.Tests/Sonulab.Distill.Tests.csproj`
- Create: `tests/Sonulab.Distill.Tests/SmokeTests.cs`
- Modify: `Sonulab.slnx`

**Interfaces:**
- Produces: the two projects every later task compiles into. Namespace root `Sonulab.Distill`.

- [ ] **Step 1: Create the library csproj**

`src/Sonulab.Distill/Sonulab.Distill.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MathNet.Numerics" Version="5.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the test csproj**

`tests/Sonulab.Distill.Tests/Sonulab.Distill.Tests.csproj` (mirrors `tests/Sonulab.Core.Tests`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Sonulab.Distill\Sonulab.Distill.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Include="fixtures\**\*">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Register both projects in `Sonulab.slnx`**

Add inside the existing `/src/` and `/tests/` folders respectively:

```xml
    <Project Path="src/Sonulab.Distill/Sonulab.Distill.csproj" />
```
```xml
    <Project Path="tests/Sonulab.Distill.Tests/Sonulab.Distill.Tests.csproj" />
```

- [ ] **Step 4: Write a failing smoke test**

`tests/Sonulab.Distill.Tests/SmokeTests.cs`:

```csharp
namespace Sonulab.Distill.Tests;

public class SmokeTests
{
    [Fact]
    public void MathNet_Fft_Matlab_convention_matches_numpy()
    {
        // numpy: fft([1,0,0,0]) == [1,1,1,1] (unscaled forward)
        var z = new System.Numerics.Complex[] { 1, 0, 0, 0 };
        MathNet.Numerics.IntegralTransforms.Fourier.Forward(
            z, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);
        Assert.All(z, c => Assert.Equal(1.0, c.Real, 12));
    }
}
```

- [ ] **Step 5: Build and run; verify the new test passes and the suite stays green**

Run: `dotnet test`
Expected: PASS — 146 existing + 1 new.

- [ ] **Step 6: Commit**

```powershell
git add src/Sonulab.Distill tests/Sonulab.Distill.Tests Sonulab.slnx
git commit -m "distill-cs: scaffold Sonulab.Distill + test project (MathNet FFT, numpy convention pinned)"
```

---

### Task 2: Vxamp container codec (keystream, tensors, encode/decode)

Ports: `tools/vxamp-re/decode_body.py` (keystream/deobfuscate), `arch.py` (tensor layout), `codec.py` + `nam_to_vxamp.write_vxamp` (container round-trip). Read those files first.

**Files:**
- Create: `src/Sonulab.Distill/VxampFormat.cs`, `src/Sonulab.Distill/WhTensors.cs`, `src/Sonulab.Distill/VxampCodec.cs`
- Test: `tests/Sonulab.Distill.Tests/VxampCodecTests.cs`

**Interfaces:**
- Produces:
  - `record WhTensors(float[] PreFir, float[] G2Header, float[] G2Fir, float[] NlmixHeader, float Nlmix)` — sizes 1024/3/1024/4/scalar.
  - `static class VxampFormat`: `SlotSize=12288`, `HeaderSize=32`, `BodySize=8224`, `byte[] Keystream(int n)`, `byte[] HeaderBytes` (32), `float[] G2HeaderFloats()` (3), `float[] NlmixHeaderFloats()` (4).
  - `static class VxampCodec`: `byte[] Encode(WhTensors t)` (throws `ArgumentException` on wrong sizes), `WhTensors Decode(ReadOnlySpan<byte> slot)`.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Distill.Tests/VxampCodecTests.cs`:

```csharp
namespace Sonulab.Distill.Tests;

public class VxampCodecTests
{
    private static WhTensors MakeTensors(float fill = 0.5f)
    {
        var pre = new float[1024]; pre[0] = 1f; pre[1] = fill;
        var g2 = new float[1024]; g2[0] = 2f; g2[500] = -fill;
        return new WhTensors(pre, VxampFormat.G2HeaderFloats(), g2,
                             VxampFormat.NlmixHeaderFloats(), 0.25f);
    }

    [Fact]
    public void Keystream_matches_python_formula()
    {
        // k[i] = (K0[i%32] - 0x20*(i//32)) % 256; K0[0]=0x99, K0[31]=0x9e
        var k = VxampFormat.Keystream(70);
        Assert.Equal(0x99, k[0]);
        Assert.Equal(0x9e, k[31]);
        Assert.Equal((byte)((0x99 - 0x20) & 0xFF), k[32]);   // second period
        Assert.Equal((byte)((0x97 - 0x40) & 0xFF), k[65]);   // third period, K0[1]
    }

    [Fact]
    public void Header_and_tlv_constants_are_exact()
    {
        Assert.Equal(32, VxampFormat.HeaderBytes.Length);
        Assert.Equal(0x40, VxampFormat.HeaderBytes[0]);
        // "Amp model" at offset 8
        Assert.Equal((byte)'A', VxampFormat.HeaderBytes[8]);
        // g2_header floats reinterpret bytes 0C 10 00 00 | 00 00 00 00 | 47 32 00 00
        var g2h = VxampFormat.G2HeaderFloats();
        Assert.Equal(3, g2h.Length);
        Assert.Equal(0x100C, BitConverter.SingleToInt32Bits(g2h[0]));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(g2h[1]));
        var nlh = VxampFormat.NlmixHeaderFloats();
        Assert.Equal(4, nlh.Length);
        Assert.Equal(0x14, BitConverter.SingleToInt32Bits(nlh[0]));
    }

    [Fact]
    public void Encode_produces_valid_slot_and_roundtrips()
    {
        var t = MakeTensors();
        var slot = VxampCodec.Encode(t);
        Assert.Equal(VxampFormat.SlotSize, slot.Length);
        Assert.Equal(VxampFormat.HeaderBytes, slot.Take(32).ToArray());
        // padding after byte 8256 is zero
        Assert.All(slot.Skip(8256), b => Assert.Equal(0, b));
        var back = VxampCodec.Decode(slot);
        Assert.Equal(t.PreFir, back.PreFir);
        Assert.Equal(t.G2Fir, back.G2Fir);
        Assert.Equal(t.Nlmix, back.Nlmix);
    }

    [Fact]
    public void Encode_rejects_wrong_sizes()
    {
        var t = MakeTensors() with { PreFir = new float[10] };
        Assert.Throws<ArgumentException>(() => VxampCodec.Encode(t));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter VxampCodecTests`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement**

`src/Sonulab.Distill/WhTensors.cs`:

```csharp
namespace Sonulab.Distill;

/// <summary>The five named float32 tensors of a device amp model (see tools/vxamp-re/arch.py).</summary>
public sealed record WhTensors(
    float[] PreFir,        // 1024 taps, short tone-shaping FIR; taps 1008..1023 always 0
    float[] G2Header,      // 3 floats = TLV chunk header bytes (metadata, not DSP)
    float[] G2Fir,         // 1024 taps, cab/speaker IR (carries the calibrated gain)
    float[] NlmixHeader,   // 4 floats = TLV chunk header bytes (metadata, not DSP)
    float Nlmix);          // nonlinear-mix scalar, 0 = fully linear
```

`src/Sonulab.Distill/VxampFormat.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Sonulab.Distill;

/// <summary>Container constants + XOR keystream for the vxamp slot format.
/// Source of truth: tools/vxamp-re/{vxamp,decode_body,arch}.py and docs/vxamp-format.md.</summary>
public static class VxampFormat
{
    public const int SlotSize = 12288;
    public const int HeaderSize = 32;
    public const int BodySize = 8224;      // 2056 float32 LE
    public const int PreFirTaps = 1024;
    public const int G2FirTaps = 1024;

    public static readonly byte[] HeaderBytes = Convert.FromHexString(
        "4020000000000000416d70206d6f64656c000000797653442122ff009ae7c4be");

    private static readonly byte[] KeystreamBase =
    {
        0x99, 0x97, 0x77, 0x6f, 0x67, 0x44, 0x45, 0x22, 0x21, 0x02, 0x01, 0xde,
        0xdd, 0xbf, 0xab, 0xa2, 0x93, 0x86, 0x63, 0x64, 0x55, 0x46, 0x33, 0x24,
        0x01, 0x02, 0xdf, 0xe0, 0xbd, 0xb6, 0xa4, 0x9e,
    };

    // TLV chunk headers, byte-identical across all 20 corpus models (arch.py / Task 4).
    private static readonly byte[] G2HeaderBytes =
        { 0x0C, 0x10, 0, 0, 0, 0, 0, 0, 0x47, 0x32, 0, 0 };
    private static readonly byte[] NlmixHeaderBytes =
        { 0x14, 0, 0, 0, 0, 0, 0, 0, 0x6E, 0x6C, 0x6D, 0x69, 0x78, 0, 0, 0 };

    /// <summary>k[i] = (K0[i % 32] - 0x20 * (i / 32)) mod 256.</summary>
    public static byte[] Keystream(int n)
    {
        var k = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int v = KeystreamBase[i % 32] - 0x20 * (i / 32);
            k[i] = (byte)(((v % 256) + 256) % 256);
        }
        return k;
    }

    public static float[] G2HeaderFloats() => MemoryMarshal.Cast<byte, float>(G2HeaderBytes).ToArray();
    public static float[] NlmixHeaderFloats() => MemoryMarshal.Cast<byte, float>(NlmixHeaderBytes).ToArray();
}
```

`src/Sonulab.Distill/VxampCodec.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Sonulab.Distill;

/// <summary>Encode/decode 12288-byte vxamp slots (port of codec.py + nam_to_vxamp.write_vxamp).</summary>
public static class VxampCodec
{
    public static byte[] Encode(WhTensors t)
    {
        Check(t.PreFir, VxampFormat.PreFirTaps, nameof(t.PreFir));
        Check(t.G2Header, 3, nameof(t.G2Header));
        Check(t.G2Fir, VxampFormat.G2FirTaps, nameof(t.G2Fir));
        Check(t.NlmixHeader, 4, nameof(t.NlmixHeader));

        var floats = new float[2056];
        t.PreFir.CopyTo(floats, 0);
        t.G2Header.CopyTo(floats, 1024);
        t.G2Fir.CopyTo(floats, 1027);
        t.NlmixHeader.CopyTo(floats, 2051);
        floats[2055] = t.Nlmix;

        var body = MemoryMarshal.AsBytes<float>(floats).ToArray();  // little-endian on all supported platforms
        var ks = VxampFormat.Keystream(body.Length);
        for (int i = 0; i < body.Length; i++) body[i] ^= ks[i];

        var slot = new byte[VxampFormat.SlotSize];
        VxampFormat.HeaderBytes.CopyTo(slot, 0);
        body.CopyTo(slot, VxampFormat.HeaderSize);
        return slot;   // remainder is already zero padding
    }

    public static WhTensors Decode(ReadOnlySpan<byte> slot)
    {
        if (slot.Length != VxampFormat.SlotSize)
            throw new ArgumentException($"expected {VxampFormat.SlotSize}-byte slot, got {slot.Length}");
        var body = slot.Slice(VxampFormat.HeaderSize, VxampFormat.BodySize).ToArray();
        var ks = VxampFormat.Keystream(body.Length);
        for (int i = 0; i < body.Length; i++) body[i] ^= ks[i];
        var f = MemoryMarshal.Cast<byte, float>(body);
        return new WhTensors(
            f.Slice(0, 1024).ToArray(),
            f.Slice(1024, 3).ToArray(),
            f.Slice(1027, 1024).ToArray(),
            f.Slice(2051, 4).ToArray(),
            f[2055]);
    }

    private static void Check(float[] a, int n, string name)
    {
        if (a.Length != n) throw new ArgumentException($"{name}: expected {n} elements, got {a.Length}");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter VxampCodecTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```powershell
git add src/Sonulab.Distill tests/Sonulab.Distill.Tests
git commit -m "distill-cs: vxamp container codec (keystream, TLV constants, encode/decode)"
```

---

### Task 3: DSP primitives (Dsp.cs)

Small static helpers used by every later task. Reference semantics: numpy (`np.convolve` full mode, `scipy.signal.lfilter(b, 1.0, x)` causal FIR).

**Files:**
- Create: `src/Sonulab.Distill/Dsp.cs`
- Test: `tests/Sonulab.Distill.Tests/DspTests.cs`

**Interfaces:**
- Produces (`static class Dsp`):
  - `double[] Convolve(double[] a, double[] b)` — full convolution, length `a+b-1`.
  - `double[] FirFilter(double[] taps, double[] x)` — causal FIR, same length as `x` (`lfilter(taps, 1, x)`).
  - `double Rms(double[] x)`, `double RmsDb(double[] x)` — `20*log10(rms + 1e-30)`.
  - `double Norm(ReadOnlySpan<double> x)` (L2), `double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)`.
  - `Complex[] Fft(double[] x, int n)` — zero-pad/truncate to n, forward FFT, `FourierOptions.Matlab`.
  - `Complex[] Ifft(Complex[] x)` — inverse, `FourierOptions.Matlab` (1/N scaling).
  - `double[] ToDouble(float[] x)`, `float[] ToFloat(double[] x)` — dtype-cast helpers.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Distill.Tests/DspTests.cs`:

```csharp
using System.Numerics;

namespace Sonulab.Distill.Tests;

public class DspTests
{
    [Fact]
    public void Convolve_matches_numpy_full()
    {
        // np.convolve([1,2,3],[0,1,0.5]) == [0, 1, 2.5, 4, 1.5]
        var y = Dsp.Convolve(new double[] { 1, 2, 3 }, new double[] { 0, 1, 0.5 });
        Assert.Equal(new double[] { 0, 1, 2.5, 4, 1.5 }, y.Select(v => Math.Round(v, 12)).ToArray());
    }

    [Fact]
    public void FirFilter_is_causal_same_length()
    {
        // lfilter([1, -0.5], 1, [1, 0, 0, 2]) == [1, -0.5, 0, 2]
        var y = Dsp.FirFilter(new double[] { 1, -0.5 }, new double[] { 1, 0, 0, 2 });
        Assert.Equal(4, y.Length);
        Assert.Equal(new double[] { 1, -0.5, 0, 2 }, y.Select(v => Math.Round(v, 12)).ToArray());
    }

    [Fact]
    public void RmsDb_matches_python()
    {
        // _rms_db(np.full(100, 0.5)) == 20*log10(0.5) == -6.020599913279624
        var x = Enumerable.Repeat(0.5, 100).ToArray();
        Assert.Equal(-6.020599913279624, Dsp.RmsDb(x), 10);
    }

    [Fact]
    public void Fft_ifft_roundtrip_and_convention()
    {
        var x = new double[] { 3, 1, -2, 5, 0, 0, 0, 0 };
        var z = Dsp.Fft(x, 8);
        Assert.Equal(x.Sum(), z[0].Real, 12);          // DC bin = sum (unscaled forward)
        var back = Dsp.Ifft(z);
        for (int i = 0; i < 8; i++) Assert.Equal(x[i], back[i].Real, 12);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter DspTests`
Expected: FAIL — `Dsp` not defined.

- [ ] **Step 3: Implement**

`src/Sonulab.Distill/Dsp.cs`:

```csharp
using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Sonulab.Distill;

/// <summary>numpy/scipy-semantics DSP primitives. FFT uses FourierOptions.Matlab
/// (= numpy convention: unscaled forward, 1/N inverse).</summary>
public static class Dsp
{
    public static double[] Convolve(double[] a, double[] b)
    {
        var y = new double[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                y[i + j] += a[i] * b[j];
        return y;
    }

    public static double[] FirFilter(double[] taps, double[] x)
    {
        var y = new double[x.Length];
        for (int n = 0; n < x.Length; n++)
        {
            double acc = 0;
            int kMax = Math.Min(taps.Length - 1, n);
            for (int k = 0; k <= kMax; k++) acc += taps[k] * x[n - k];
            y[n] = acc;
        }
        return y;
    }

    public static double Rms(double[] x)
    {
        double s = 0;
        foreach (var v in x) s += v * v;
        return Math.Sqrt(s / x.Length);
    }

    public static double RmsDb(double[] x) => 20.0 * Math.Log10(Rms(x) + 1e-30);

    public static double Norm(ReadOnlySpan<double> x)
    {
        double s = 0;
        foreach (var v in x) s += v * v;
        return Math.Sqrt(s);
    }

    public static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    public static Complex[] Fft(double[] x, int n)
    {
        var z = new Complex[n];
        for (int i = 0; i < Math.Min(n, x.Length); i++) z[i] = x[i];
        Fourier.Forward(z, FourierOptions.Matlab);
        return z;
    }

    public static Complex[] Ifft(Complex[] x)
    {
        var z = (Complex[])x.Clone();
        Fourier.Inverse(z, FourierOptions.Matlab);
        return z;
    }

    public static double[] ToDouble(float[] x)
    {
        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = x[i];
        return y;
    }

    public static float[] ToFloat(double[] x)
    {
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = (float)x[i];
        return y;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter DspTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "distill-cs: DSP primitives (convolve, causal FIR, rms-dB, numpy-convention FFT)"
```

---

### Task 4: scipy-exact polyphase resampler (Resampler.cs)

Ports `scipy.signal.resample_poly` (default `window=('kaiser', 5.0)`, `padtype='constant'`, cval 0) + its `firwin` filter design. Verified against scipy 1.17.1 source; the padding arithmetic below is copied from it and must be implemented EXACTLY. Used for 48 kHz ↔ 44.1 kHz (reduces to up/down 147/160 or 160/147 via gcd 300).

**Files:**
- Create: `src/Sonulab.Distill/Resampler.cs`
- Test: `tests/Sonulab.Distill.Tests/ResamplerTests.cs`

**Interfaces:**
- Produces (`static class Resampler`):
  - `double BesselI0(double x)` — modified Bessel I₀, power series to machine precision.
  - `double[] Kaiser(int m, double beta)` — symmetric Kaiser window, m points.
  - `double[] Firwin(int numtaps, double cutoff)` — lowpass, kaiser β=5.0, pass_zero, scaled (DC gain 1).
  - `double[] Upfirdn(double[] h, double[] x, int up, int down)` — output length `((x.Length - 1) * up + h.Length - 1) / down + 1` (integer division).
  - `double[] ResamplePoly(double[] x, int up, int down)`.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Distill.Tests/ResamplerTests.cs`:

```csharp
namespace Sonulab.Distill.Tests;

public class ResamplerTests
{
    [Fact]
    public void BesselI0_matches_reference_values()
    {
        Assert.Equal(1.0, Resampler.BesselI0(0.0), 15);
        Assert.Equal(27.239871823604442, Resampler.BesselI0(5.0), 9);   // scipy.special.i0(5)
    }

    [Fact]
    public void Firwin_center_tap_and_dc_gain_match_scipy()
    {
        // firwin(2*10*160+1, 1/160, window=('kaiser', 5.0)) — the exact filter
        // resample_poly designs for 44.1k<->48k. Values from scipy 1.17.1.
        var h = Resampler.Firwin(3201, 1.0 / 160.0);
        Assert.Equal(0.006254219468524473, h[1600], 12);   // center tap
        Assert.Equal(1.0, h.Sum(), 9);                     // DC gain scaled to 1
    }

    [Fact]
    public void Upfirdn_output_length_formula()
    {
        var y = Resampler.Upfirdn(new double[21], new double[100], 3, 2);
        Assert.Equal(((100 - 1) * 3 + 21 - 1) / 2 + 1, y.Length);   // == 159
    }

    [Fact]
    public void ResamplePoly_matches_scipy_golden()
    {
        // x = (arange(16) - 7.5) / 8.0; resample_poly(x, 3, 2) — scipy 1.17.1 output.
        var x = Enumerable.Range(0, 16).Select(i => (i - 7.5) / 8.0).ToArray();
        var expected = new[]
        {
            -0.9380682877066662, -0.9580548735963454, -0.7050461858662392,
            -0.6879167443182218, -0.6389696871368792, -0.4960984338170718,
            -0.43776520092977755, -0.36892175617266115, -0.26110131061571223,
            -0.18761365754133325, -0.10769739772373164, -0.0201632805243235,
            0.06253788584711109, 0.15085305426217982, 0.22118627998175916,
            0.31268942923555537, 0.4126946933836402, 0.45801864196656006,
            0.5628409726239997, 0.6861745164875451, 0.6746504237128529,
            0.812992516012444, 1.0331231942120538, 0.6604326293750675,
        };
        var y = Resampler.ResamplePoly(x, 3, 2);
        Assert.Equal(expected.Length, y.Length);
        for (int i = 0; i < y.Length; i++) Assert.Equal(expected[i], y[i], 10);
    }

    [Fact]
    public void ResamplePoly_reduces_by_gcd_and_handles_identity()
    {
        var x = new double[] { 1, 2, 3, 4 };
        Assert.Equal(x, Resampler.ResamplePoly(x, 5, 5));          // up==down -> copy
        // 44100 -> 48000 reduces to 160/147 internally: output length ceil(4*160/147) = 5
        Assert.Equal(5, Resampler.ResamplePoly(x, 48000, 44100).Length);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter ResamplerTests`
Expected: FAIL — `Resampler` not defined.

- [ ] **Step 3: Implement**

`src/Sonulab.Distill/Resampler.cs`:

```csharp
namespace Sonulab.Distill;

/// <summary>Port of scipy.signal.resample_poly (window=('kaiser',5.0), padtype='constant').
/// The padding arithmetic is copied verbatim from scipy 1.17.1 — do not "simplify" it;
/// parity with the Python oracle depends on it.</summary>
public static class Resampler
{
    public static double BesselI0(double x)
    {
        double t = x * x / 4.0, term = 1.0, sum = 1.0;
        for (int k = 1; k < 1000; k++)
        {
            term *= t / ((double)k * k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }

    public static double[] Kaiser(int m, double beta)
    {
        var w = new double[m];
        double denom = BesselI0(beta);
        for (int n = 0; n < m; n++)
        {
            double r = 2.0 * n / (m - 1) - 1.0;
            w[n] = BesselI0(beta * Math.Sqrt(Math.Max(0.0, 1.0 - r * r))) / denom;
        }
        return w;
    }

    private static double Sinc(double x) =>
        x == 0.0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    /// <summary>firwin(numtaps, cutoff, window=('kaiser', 5.0)): lowpass, pass_zero, scale=True.
    /// cutoff is relative to Nyquist (scipy default fs=2).</summary>
    public static double[] Firwin(int numtaps, double cutoff)
    {
        var win = Kaiser(numtaps, 5.0);
        var h = new double[numtaps];
        double center = (numtaps - 1) / 2.0, sum = 0;
        for (int i = 0; i < numtaps; i++)
        {
            double m = i - center;
            h[i] = cutoff * Sinc(cutoff * m) * win[i];
            sum += h[i];
        }
        for (int i = 0; i < numtaps; i++) h[i] /= sum;   // scale: unity DC gain
        return h;
    }

    /// <summary>Upsample by up (zero-stuff), FIR filter with h, downsample by down.
    /// Output length matches scipy's _output_len exactly.</summary>
    public static double[] Upfirdn(double[] h, double[] x, int up, int down)
    {
        int nOut = (int)(((long)(x.Length - 1) * up + h.Length - 1) / down + 1);
        var y = new double[nOut];
        for (int k = 0; k < nOut; k++)
        {
            long t = (long)k * down;   // index in the upsampled, filtered stream
            double acc = 0;
            // y[t] = sum_j h[j] * xu[t-j], where xu[m] = x[m/up] when m % up == 0, else 0
            for (long j = t % up; j < h.Length; j += up)
            {
                long m = (t - j) / up;
                if (m < 0) break;
                if (m < x.Length) acc += h[j] * x[m];
            }
            y[k] = acc;
        }
        return y;
    }

    public static double[] ResamplePoly(double[] x, int up, int down)
    {
        int g = Gcd(up, down);
        up /= g; down /= g;
        if (up == 1 && down == 1) return (double[])x.Clone();

        int nIn = x.Length;
        long nOutL = (long)nIn * up;
        int nOut = (int)(nOutL / down + (nOutL % down != 0 ? 1 : 0));

        int maxRate = Math.Max(up, down);
        double fc = 1.0 / maxRate;
        int halfLen = 10 * maxRate;
        var h = Firwin(2 * halfLen + 1, fc);
        for (int i = 0; i < h.Length; i++) h[i] *= up;

        int nPrePad = down - halfLen % down;
        int nPostPad = 0;
        int nPreRemove = (halfLen + nPrePad) / down;
        while (OutputLen(h.Length + nPrePad + nPostPad, nIn, up, down) < nOut + nPreRemove)
            nPostPad++;

        var hp = new double[nPrePad + h.Length + nPostPad];
        h.CopyTo(hp, nPrePad);

        var y = Upfirdn(hp, x, up, down);
        return y[nPreRemove..(nPreRemove + nOut)];
    }

    private static long OutputLen(int lenH, int inLen, int up, int down) =>
        ((long)(inLen - 1) * up + lenH - 1) / down + 1;

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter ResamplerTests`
Expected: PASS (5 tests). If `ResamplePoly_matches_scipy_golden` fails, the divergence is in Firwin/Kaiser/padding — compare `h` against scipy's `firwin` output at a few indices before touching anything else.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "distill-cs: scipy-exact polyphase resampler (kaiser firwin + upfirdn + resample_poly)"
```

---

### Task 5: Nonlinearity (Nonlinearity.cs)

Ports `tools/distiller/nonlinearity.py::apply_nl` — the pinned drive-normalized soft-clip mix. Read that file's docstring first. Deliberate C# deviation: the unused `header` parameter is dropped (it exists in Python only for discovery-task signature stability).

**Files:**
- Create: `src/Sonulab.Distill/Nonlinearity.cs`
- Test: `tests/Sonulab.Distill.Tests/NonlinearityTests.cs`

**Interfaces:**
- Produces: `static double[] Nonlinearity.ApplyNl(double[] x, double scalar)` — returns a NEW array; `scalar == 0` is an exact copy.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Distill.Tests/NonlinearityTests.cs`:

```csharp
namespace Sonulab.Distill.Tests;

public class NonlinearityTests
{
    [Fact]
    public void Scalar_zero_is_exact_identity()
    {
        var x = new double[] { 0.5, -1.25, 3.75e-8, 0 };
        var y = Nonlinearity.ApplyNl(x, 0.0);
        Assert.Equal(x, y);
        Assert.NotSame(x, y);   // a copy, like xf.copy()
    }

    [Fact]
    public void Silence_returns_copy()
    {
        var x = new double[64];   // rms < 1e-12
        Assert.Equal(x, Nonlinearity.ApplyNl(x, 0.5));
    }

    [Fact]
    public void Formula_matches_python()
    {
        // x = [1.0, -2.0]; r = sqrt(mean(x^2)) = sqrt(2.5); s = 0.4
        // y = 0.6*x + 0.4*r*tanh(x/r)
        var x = new double[] { 1.0, -2.0 };
        double r = Math.Sqrt(2.5);
        var y = Nonlinearity.ApplyNl(x, 0.4);
        Assert.Equal(0.6 * 1.0 + 0.4 * r * Math.Tanh(1.0 / r), y[0], 14);
        Assert.Equal(0.6 * -2.0 + 0.4 * r * Math.Tanh(-2.0 / r), y[1], 14);
    }

    [Fact]
    public void Compresses_peaks()
    {
        var x = Enumerable.Range(0, 1000).Select(i => Math.Sin(i * 0.1) * 2).ToArray();
        double p0 = Nonlinearity.ApplyNl(x, 0.0).Max(Math.Abs);
        double p5 = Nonlinearity.ApplyNl(x, 0.5).Max(Math.Abs);
        Assert.True(p5 < p0);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter NonlinearityTests`
Expected: FAIL — `Nonlinearity` not defined.

- [ ] **Step 3: Implement**

`src/Sonulab.Distill/Nonlinearity.cs`:

```csharp
namespace Sonulab.Distill;

/// <summary>The pinned firmware nonlinearity (port of tools/distiller/nonlinearity.py):
/// nl(u) = (1-s)*u + s*r*tanh(u/r), r = rms(u). s==0 is exact identity.</summary>
public static class Nonlinearity
{
    public static double[] ApplyNl(double[] x, double scalar)
    {
        if (scalar == 0.0) return (double[])x.Clone();
        double r = x.Length > 0 ? Dsp.Rms(x) : 0.0;
        if (r < 1e-12) return (double[])x.Clone();
        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
            y[i] = (1.0 - scalar) * x[i] + scalar * r * Math.Tanh(x[i] / r);
        return y;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter NonlinearityTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "distill-cs: pinned nonlinearity (drive-normalized soft-clip mix)"
```


---

### Task 6: Device simulator (DeviceSim.cs)

Ports `tools/distiller/device_sim.py`: `y = g2_fir ⊛ nl(pre_fir ⊛ x)`. Read that file first. **float32 cast points (parity-critical):** the Python `_apply_fir` returns float32 and `apply_nl(...)` is cast `.astype(np.float32)` — mirror: cast to float after EACH stage (pre-FIR, nonlinearity, g2-FIR).

**Files:**
- Create: `src/Sonulab.Distill/DeviceSim.cs`
- Test: `tests/Sonulab.Distill.Tests/DeviceSimTests.cs`

**Interfaces:**
- Consumes: `WhTensors`, `Dsp.FirFilter`, `Dsp.Convolve`, `Nonlinearity.ApplyNl`.
- Produces (`static class DeviceSim`):
  - `const int SampleRate = 44100`
  - `float[] Simulate(WhTensors t, float[] x)` — full model, nonlinearity driven by `t.Nlmix` (exact identity path when 0).
  - `float[] SimulateLinear(WhTensors t, float[] x)` — the `nl=lambda z: z` path.
  - `float[] LinearIr(WhTensors t)` — `convolve(pre_fir, g2_fir)`, length 2047, float32.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Distill.Tests/DeviceSimTests.cs`:

```csharp
namespace Sonulab.Distill.Tests;

public class DeviceSimTests
{
    private static WhTensors Delta(float preGain = 1f, float g2Gain = 1f, float nlmix = 0f)
    {
        var pre = new float[1024]; pre[0] = preGain;
        var g2 = new float[1024]; g2[0] = g2Gain;
        return new WhTensors(pre, VxampFormat.G2HeaderFloats(), g2,
                             VxampFormat.NlmixHeaderFloats(), nlmix);
    }

    [Fact]
    public void Delta_cascade_is_identity()
    {
        var x = new float[] { 0.1f, -0.5f, 0.25f, 0f };
        var y = DeviceSim.Simulate(Delta(), x);
        Assert.Equal(x, y);
    }

    [Fact]
    public void Gains_multiply_through_the_cascade()
    {
        var x = new float[] { 1f, 0f, 0f };
        var y = DeviceSim.Simulate(Delta(preGain: 2f, g2Gain: 3f), x);
        Assert.Equal(6f, y[0], 1e-5f);
    }

    [Fact]
    public void Nlmix_zero_equals_linear_path_exactly()
    {
        var t = Delta(preGain: 1.5f, g2Gain: 0.8f, nlmix: 0f);
        var x = Enumerable.Range(0, 500).Select(i => (float)Math.Sin(i * 0.13) * 0.4f).ToArray();
        Assert.Equal(DeviceSim.SimulateLinear(t, x), DeviceSim.Simulate(t, x));
    }

    [Fact]
    public void Nonzero_nlmix_changes_output()
    {
        var t = Delta(nlmix: 0.5f);
        var x = Enumerable.Range(0, 500).Select(i => (float)Math.Sin(i * 0.13)).ToArray();
        Assert.NotEqual(DeviceSim.SimulateLinear(t, x), DeviceSim.Simulate(t, x));
    }

    [Fact]
    public void LinearIr_is_full_convolution()
    {
        var t = Delta(preGain: 2f, g2Gain: 0.5f);
        var ir = DeviceSim.LinearIr(t);
        Assert.Equal(2047, ir.Length);
        Assert.Equal(1f, ir[0], 1e-6f);
        Assert.All(ir.Skip(1), v => Assert.Equal(0f, v));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter DeviceSimTests`
Expected: FAIL — `DeviceSim` not defined.

- [ ] **Step 3: Implement**

`src/Sonulab.Distill/DeviceSim.cs`:

```csharp
namespace Sonulab.Distill;

/// <summary>Device forward model (port of tools/distiller/device_sim.py):
/// y = g2_fir ⊛ nl(pre_fir ⊛ x). Float32 is materialized after every stage,
/// mirroring the Python dtype flow (parity-critical).</summary>
public static class DeviceSim
{
    public const int SampleRate = 44100;

    public static float[] Simulate(WhTensors t, float[] x) =>
        SimulateCore(t, x, mid => Nonlinearity.ApplyNl(mid, t.Nlmix));

    public static float[] SimulateLinear(WhTensors t, float[] x) =>
        SimulateCore(t, x, mid => mid);

    private static float[] SimulateCore(WhTensors t, float[] x, Func<double[], double[]> nl)
    {
        // Stage 1: pre_fir (float32 materialized, like _apply_fir)
        var mid32 = Dsp.ToFloat(Dsp.FirFilter(Dsp.ToDouble(t.PreFir), Dsp.ToDouble(x)));
        // Stage 2: nonlinearity (float64 in, float32 out, like apply_nl(...).astype(float32))
        var nl32 = Dsp.ToFloat(nl(Dsp.ToDouble(mid32)));
        // Stage 3: g2_fir
        return Dsp.ToFloat(Dsp.FirFilter(Dsp.ToDouble(t.G2Fir), Dsp.ToDouble(nl32)));
    }

    public static float[] LinearIr(WhTensors t) =>
        Dsp.ToFloat(Dsp.Convolve(Dsp.ToDouble(t.PreFir), Dsp.ToDouble(t.G2Fir)));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter DeviceSimTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "distill-cs: device simulator (WH FIR cascade with staged float32 casts)"
```

---

### Task 7: Probe (Probe.cs)

Ports `tools/distiller/probe.py`. `LinearIrOfModel` takes the not-yet-ported model as an interface so this task doesn't depend on Task 9.

**Files:**
- Create: `src/Sonulab.Distill/Probe.cs` (also defines `INamProcessor`)
- Test: `tests/Sonulab.Distill.Tests/ProbeTests.cs`

**Interfaces:**
- Produces:
  - `interface INamProcessor { float[] Process(float[] x); int? SampleRate { get; } }` — Task 9's `NamModel` implements it; tests fake it.
  - `static class Probe`: `float[] LinearIrOfModel(INamProcessor model, int n = 4096, double amp = 1e-3)`, `double[] Logmag(double[] ir, int nFft = 4096)` (rFFT magnitude in dB, floor 1e-12), `double LogmagCorr(double[] aIr, double[] bIr, int nFft = 4096)` (Pearson; 0.0 on degenerate/non-finite).

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Distill.Tests/ProbeTests.cs`:

```csharp
namespace Sonulab.Distill.Tests;

file sealed class FakeGainModel(float gain) : INamProcessor
{
    public int? SampleRate => 48000;
    public float[] Process(float[] x) => x.Select(v => v * gain).ToArray();
}

public class ProbeTests
{
    [Fact]
    public void LinearIr_normalizes_out_the_probe_amplitude()
    {
        var ir = Probe.LinearIrOfModel(new FakeGainModel(3f), n: 64);
        Assert.Equal(64, ir.Length);
        Assert.Equal(3f, ir[0], 1e-4f);       // gain-3 model -> IR = 3*delta
        Assert.All(ir.Skip(1), v => Assert.Equal(0f, v, 1e-4f));
    }

    [Fact]
    public void Logmag_of_delta_is_flat_zero_db()
    {
        var ir = new double[16]; ir[0] = 1.0;
        var lm = Probe.Logmag(ir, 4096);
        Assert.Equal(4096 / 2 + 1, lm.Length);
        Assert.All(lm, v => Assert.Equal(0.0, v, 9));
    }

    [Fact]
    public void LogmagCorr_identical_is_one_scaled_is_one()
    {
        var rng = new Random(7);
        var ir = Enumerable.Range(0, 512).Select(_ => rng.NextDouble() - 0.5).ToArray();
        Assert.Equal(1.0, Probe.LogmagCorr(ir, ir), 9);
        // uniform gain shifts log-mag by a constant -> correlation still 1
        var scaled = ir.Select(v => v * 7.3).ToArray();
        Assert.Equal(1.0, Probe.LogmagCorr(ir, scaled), 9);
    }

    [Fact]
    public void LogmagCorr_degenerate_returns_zero()
    {
        // all-zero IR -> flat floored spectrum -> std < 1e-12 -> 0.0
        Assert.Equal(0.0, Probe.LogmagCorr(new double[64], new double[64] ));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter ProbeTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement**

`src/Sonulab.Distill/Probe.cs`:

```csharp
namespace Sonulab.Distill;

/// <summary>Anything that can run audio through a NAM model (implemented by NamModel;
/// faked in tests). Process is float32 in/out, same length, causal, prewarmed, DC-removed.</summary>
public interface INamProcessor
{
    float[] Process(float[] x);
    int? SampleRate { get; }
}

/// <summary>Response prober (port of tools/distiller/probe.py).</summary>
public static class Probe
{
    public static float[] LinearIrOfModel(INamProcessor model, int n = 4096, double amp = 1e-3)
    {
        var x = new float[n];
        x[0] = (float)amp;
        var y = model.Process(x);
        var outIr = new float[n];
        for (int i = 0; i < n; i++) outIr[i] = (float)(y[i] / amp);
        return outIr;
    }

    public static double[] Logmag(double[] ir, int nFft = 4096)
    {
        var z = Dsp.Fft(ir, nFft);
        var lm = new double[nFft / 2 + 1];
        for (int i = 0; i < lm.Length; i++)
            lm[i] = 20.0 * Math.Log10(Math.Max(z[i].Magnitude, 1e-12));
        return lm;
    }

    public static double LogmagCorr(double[] aIr, double[] bIr, int nFft = 4096)
    {
        var a = Logmag(aIr, nFft);
        var b = Logmag(bIr, nFft);
        double ma = a.Average(), mb = b.Average();
        double sa = Math.Sqrt(a.Sum(v => (v - ma) * (v - ma)) / a.Length);
        double sb = Math.Sqrt(b.Sum(v => (v - mb) * (v - mb)) / b.Length);
        if (sa < 1e-12 || sb < 1e-12) return 0.0;
        double cov = 0;
        for (int i = 0; i < a.Length; i++) cov += (a[i] - ma) * (b[i] - mb);
        cov /= a.Length;
        double corr = cov / (sa * sb);
        return double.IsFinite(corr) ? corr : 0.0;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter ProbeTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "distill-cs: probe (linear IR, log-mag spectrum, spectral correlation)"
```

---

### Task 8: Oracle fixtures + embedded drive signal

Python-side task: a committed generator script produces (a) the embedded drive-signal resource, (b) a small committed synthetic `.nam` + its Python-golden outputs, (c) optional corpus goldens (gitignored). **Requires the corpus (`NAMFiles/`) present** — run on this machine, artifacts committed. This is the ONLY new file allowed under `tools/`.

**Files:**
- Create: `tools/distiller/make_cs_fixtures.py`
- Create (generated): `src/Sonulab.Distill/Resources/drive_signal.f32` (64,000 B), `tests/Sonulab.Distill.Tests/fixtures/synthetic.nam`, `tests/Sonulab.Distill.Tests/fixtures/synthetic.golden.vxamp`, `tests/Sonulab.Distill.Tests/fixtures/golden_process.json`, `tests/Sonulab.Distill.Tests/fixtures/golden_metrics.json`
- Modify: `src/Sonulab.Distill/Sonulab.Distill.csproj` (embed the resource), `.gitignore` (corpus goldens dir)

**Interfaces:**
- Produces: fixture files consumed by Tasks 9–12; `golden_process.json` = `{"input": [256 floats], "output": [256 floats]}` (synthetic model, Python `process()`); `golden_metrics.json` = `{"our_err": <float>, "device_reference_db": 13.531918973745606}`.

- [ ] **Step 1: Write the generator script**

`tools/distiller/make_cs_fixtures.py`:

```python
"""Generate fixtures + goldens for the C# port (Sonulab.Distill) from the Python oracle.

Usage (repo root, corpus machine):
    python tools/distiller/make_cs_fixtures.py            # committed fixtures
    python tools/distiller/make_cs_fixtures.py --corpus   # + gitignored corpus goldens

Everything here is READ-ONLY on NAMFiles/ and the existing distiller modules.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "vxamp-re"))

import distill  # noqa: E402
import vxamp as vx  # noqa: E402
from nam_runner import load_nam_model  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "Sonulab.Distill.Tests" / "fixtures"
RESOURCES = ROOT / "src" / "Sonulab.Distill" / "Resources"
CORPUS_GOLDENS = ROOT / "tests" / "Sonulab.Distill.Tests" / "goldens-corpus"


def write_drive_signal() -> None:
    """The fixed 0.3-RMS reference/drive signal (fit.py + distill.py, seed 0)."""
    x = (np.random.default_rng(0).standard_normal(16000) * 0.3).astype(np.float32)
    RESOURCES.mkdir(parents=True, exist_ok=True)
    (RESOURCES / "drive_signal.f32").write_bytes(x.tobytes())
    print(f"drive_signal.f32: {x.size} float32, rms={float(np.sqrt(np.mean(x*x))):.6f}")


def make_synthetic_nam() -> Path:
    """Small standard-WaveNet .nam with deterministic weights (seed 42).
    1 layer group: channels=4, kernel 3, dilations [1,2,4,8], Tanh, ungated,
    head_size 1, head_bias True -> 314 weights incl. trailing head_scale."""
    rng = np.random.default_rng(42)
    n_weights = 4 + 4 * (48 + 4 + 4 + 16 + 4) + 4 + 1   # rechannel + 4 layers + head(+bias)
    weights = (rng.standard_normal(n_weights) * 0.3).astype(np.float32).tolist()
    head_scale = 0.75
    nam = {
        "version": "0.5.4",
        "architecture": "WaveNet",
        "sample_rate": 48000,
        "config": {
            "layers": [{
                "input_size": 1, "condition_size": 1, "channels": 4,
                "kernel_size": 3, "dilations": [1, 2, 4, 8],
                "activation": "Tanh", "gated": False,
                "head_size": 1, "head_bias": True,
            }],
            "head": None,
        },
        "weights": weights + [head_scale],
    }
    FIXTURES.mkdir(parents=True, exist_ok=True)
    p = FIXTURES / "synthetic.nam"
    p.write_text(json.dumps(nam), encoding="utf-8")
    return p


def write_process_golden(nam_path: Path) -> None:
    model = load_nam_model(nam_path)
    n = np.arange(256)
    x = (0.4 * np.sin(2 * np.pi * 220.0 * n / 48000)).astype(np.float32)
    y = model.process(x)
    (FIXTURES / "golden_process.json").write_text(json.dumps({
        "input": [float(v) for v in x],
        "output": [float(v) for v in y],
        "receptive_field": int(model.receptive_field),
    }), encoding="utf-8")
    print(f"golden_process.json: rf={model.receptive_field}, out[0..2]={y[:3]}")


def write_distill_goldens(nam_path: Path) -> None:
    blob = distill.distill(nam_path)
    (FIXTURES / "synthetic.golden.vxamp").write_bytes(blob)
    r = distill.fidelity_vs_nam(nam_path)
    (FIXTURES / "golden_metrics.json").write_text(json.dumps({
        "our_err": r["our_err"],
        "device_reference_db": distill.device_reference_db(),
    }), encoding="utf-8")
    print(f"synthetic golden: {len(blob)} B, our_err={r['our_err']:.6f}, "
          f"ref_db={distill.device_reference_db()!r}")


def verify_header_constants() -> None:
    """Guard the baked TLV header constants (VxampFormat.cs) against the actual corpus."""
    import codec
    t = codec.decode(vx.load_vxamp(vx.vxamp_files()[0]))["tensors"]
    g2h = np.asarray(t["g2_header"], dtype="<f4").tobytes().hex()
    nlh = np.asarray(t["nlmix_header"], dtype="<f4").tobytes().hex()
    print(f"corpus g2_header bytes   : {g2h}")
    print(f"corpus nlmix_header bytes: {nlh}")
    assert g2h == "0c1000000000000047320000", f"g2_header mismatch: {g2h}"
    assert nlh == "14000000000000006e6c6d6978000000", f"nlmix_header mismatch: {nlh}"


def write_corpus_goldens() -> None:
    CORPUS_GOLDENS.mkdir(parents=True, exist_ok=True)
    metrics = {}
    for name, nam_path, _vf in vx.pairs():
        blob = distill.distill(nam_path)
        (CORPUS_GOLDENS / f"{name}.golden.vxamp").write_bytes(blob)
        (CORPUS_GOLDENS / f"{name}.nam.path.txt").write_text(str(nam_path), encoding="utf-8")
        metrics[name] = distill.fidelity_vs_nam(nam_path)["our_err"]
        print(f"  {name}: our_err={metrics[name]:.6f}")
    (CORPUS_GOLDENS / "metrics.json").write_text(json.dumps(metrics), encoding="utf-8")


if __name__ == "__main__":
    write_drive_signal()
    verify_header_constants()
    p = make_synthetic_nam()
    write_process_golden(p)
    write_distill_goldens(p)
    if "--corpus" in sys.argv:
        write_corpus_goldens()
    print("DONE")
```

**Note:** the two assertion literals are the baked `VxampFormat` constants as hex: `g2_header` = 12 bytes `0c1000000000000047320000`, `nlmix_header` = 16 bytes `14000000000000006e6c6d6978000000`. If either assertion fires, the bake is wrong — fix `VxampFormat.cs` to the printed corpus bytes and update the Task 2 test, then re-run.

- [ ] **Step 2: Run the generator and inspect output**

Run: `python tools/distiller/make_cs_fixtures.py --corpus`
Expected: prints drive-signal RMS (~0.30), the two corpus header hex lines matching the baked constants, `rf=` for the synthetic model, `synthetic golden: 12288 B`, `ref_db=13.531918973745606`, one `our_err` line per corpus pair, `DONE`. If a header assertion fails, STOP — the baked constants in Task 2 are wrong; fix `VxampFormat` to the printed corpus bytes and update the Task 2 test.

- [ ] **Step 3: Embed the drive signal + ignore corpus goldens**

Add to `src/Sonulab.Distill/Sonulab.Distill.csproj`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Resources/drive_signal.f32" />
  </ItemGroup>
```

Append to `.gitignore`:

```
tests/Sonulab.Distill.Tests/goldens-corpus/
```

- [ ] **Step 4: Verify build and suite stay green**

Run: `dotnet test`
Expected: PASS, same counts as after Task 7.

- [ ] **Step 5: Commit**

```powershell
git add tools/distiller/make_cs_fixtures.py src/Sonulab.Distill tests/Sonulab.Distill.Tests/fixtures .gitignore
git commit -m "distill-cs: oracle fixtures (synthetic .nam + goldens) and embedded drive signal"
```


---

### Task 9: NAM model runner (NamModel.cs + NamParser.cs)

Ports `tools/distiller/nam_runner.py` — the largest module. Read its docstring + both parsers fully before writing code; the flat-weight consumption ORDER is the contract. **float32 semantics:** weights are float32; internal accumulation is double but every array the Python code materializes as float32 (`z`, `skips`, `h`, the final output) is stored as `float` — this bounds parity drift to per-element rounding.

**Files:**
- Create: `src/Sonulab.Distill/NamModel.cs`, `src/Sonulab.Distill/NamParser.cs`
- Test: `tests/Sonulab.Distill.Tests/NamModelTests.cs`

**Interfaces:**
- Consumes: `INamProcessor` (Task 7), fixtures (Task 8).
- Produces:
  - `sealed class NamModel : INamProcessor` — `string Arch`, `int? SampleRate { get; set; }` (settable: the distiller applies the 48 kHz default), `int ReceptiveField`, `float[] Process(float[] x)`.
  - `static class NamParser` — `NamModel Load(string path)`, `NamModel Parse(string json)`.
  - `sealed class NamFormatException : Exception` — unsupported `.nam` features / weight over- or under-run.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Distill.Tests/NamModelTests.cs`:

```csharp
using System.Text.Json;

namespace Sonulab.Distill.Tests;

public class NamModelTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Synthetic_receptive_field_matches_golden()
    {
        // dilations [1,2,4,8], K=3, head K=1: rf = 2*(1+2+4+8) + 0 = 30
        var model = NamParser.Load(Fixture("synthetic.nam"));
        var golden = JsonDocument.Parse(File.ReadAllText(Fixture("golden_process.json")));
        Assert.Equal(golden.RootElement.GetProperty("receptive_field").GetInt32(),
                     model.ReceptiveField);
        Assert.Equal(30, model.ReceptiveField);
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
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter NamModelTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement the model**

`src/Sonulab.Distill/NamModel.cs`:

```csharp
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
    internal required List<NamLayerArray> Arrays { get; init; }
    internal required float HeadScale { get; init; }

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
```

- [ ] **Step 4: Implement the parser**

`src/Sonulab.Distill/NamParser.cs`:

```csharp
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
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter NamModelTests`
Expected: PASS (6 tests). If `Process_matches_python_golden` fails at >1e-5, the usual culprits in order: weight consumption order (compare `Take` sequence against `nam_runner.py` line by line), conv tap alignment (tap `k-1` = current sample), mixin added AFTER conv bias, DC subtraction using the settled level (`Raw(zeros(rf+8))[^1]`).

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "distill-cs: NAM WaveNet runner (standard + VoidX-fork parsers, prewarmed causal forward)"
```


---

### Task 10: Wiener–Hammerstein fitter (FirFitter.cs)

Ports `tools/distiller/fit.py`. Read its module docstring fully — it explains WHY each step exists. **Dtype notes (parity-critical):** `mid` in the nl fit stays float64 (fit.py never casts it to float32); the FIR candidates are designed in float64 and cast to float32 only at `_pad_taps`; the drive signal is the embedded float32 resource. The TLV header floats come from `VxampFormat` (replacing Python's `_corpus_headers()` corpus read — Task 8 verified the bytes match).

**Files:**
- Create: `src/Sonulab.Distill/FirFitter.cs` (also defines `DriveSignal`)
- Test: `tests/Sonulab.Distill.Tests/FirFitterTests.cs`

**Interfaces:**
- Consumes: `Dsp`, `Resampler`, `Nonlinearity`, `Probe`, `INamProcessor`, `VxampFormat`, embedded `Resources/drive_signal.f32`.
- Produces:
  - `static class DriveSignal`: `float[] Get()` — the 16000-sample 0.3-RMS reference (cached).
  - `static class FirFitter`:
    - `const int NTaps = 1024`, `const int PreZeroTail = 1008`, `const int NPreShort = 64`, `const double NlmixMax = 0.7`, `const double DriveLevel = 0.3`, `const int NamDefaultSampleRate = 48000`
    - `WhTensors FitWh(INamProcessor model)` — the full fit.
    - `(float[] pre, float[] g2) DesignLinear(double[] irDev)`
    - `(double nlmix, double gain) FitNl(INamProcessor model, float[] pre, float[] g2)`
    - `double[] ModelRefAtDeviceRate(INamProcessor model, float[] xDev)`
    - `MinphasePre`, `DeconvG2`, `CascadeErr` — public static (pure helpers; tests call them directly, no InternalsVisibleTo needed)

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Distill.Tests/FirFitterTests.cs`:

```csharp
namespace Sonulab.Distill.Tests;

/// <summary>Linear gain "model" at the device rate — no resampling, no nonlinearity.</summary>
file sealed class LinearDeviceRateModel(float gain) : INamProcessor
{
    public int? SampleRate => 44100;
    public float[] Process(float[] x) => x.Select(v => v * gain).ToArray();
}

public class FirFitterTests
{
    [Fact]
    public void CascadeErr_zero_for_exact_split()
    {
        var pre = new double[1024]; pre[0] = 1.0;
        var target = new double[2047]; target[0] = 0.5; target[100] = -0.25;
        var g2 = target[..1024];
        Assert.Equal(0.0, FirFitter.CascadeErr(pre, g2, target), 12);
    }

    [Fact]
    public void DesignLinear_reproduces_a_short_ir()
    {
        // IR fully inside 1024 taps -> the delta split is exact
        var ir = new double[2047];
        ir[0] = 1.2; ir[3] = -0.4; ir[900] = 0.05;
        var (pre, g2) = FirFitter.DesignLinear(ir);
        Assert.Equal(1024, pre.Length);
        Assert.Equal(1024, g2.Length);
        Assert.All(pre.Skip(FirFitter.PreZeroTail), v => Assert.Equal(0f, v));   // corpus invariant
        var cascade = Dsp.Convolve(Dsp.ToDouble(pre), Dsp.ToDouble(g2));
        Assert.True(FirFitter.CascadeErr(Dsp.ToDouble(pre), Dsp.ToDouble(g2), ir) < 1e-6);
    }

    [Fact]
    public void FitNl_snaps_to_zero_for_a_linear_model()
    {
        var model = new LinearDeviceRateModel(2.0f);
        var pre = new float[1024]; pre[0] = 1f;
        var g2 = new float[1024]; g2[0] = 2f;   // cascade == model -> already exact
        var (s, gain) = FirFitter.FitNl(model, pre, g2);
        Assert.Equal(0.0, s);
        Assert.Equal(1.0, gain, 2);
    }

    [Fact]
    public void FitWh_on_synthetic_fixture_returns_valid_tensors()
    {
        var model = NamParser.Load(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic.nam"));
        var t = FirFitter.FitWh(model);
        Assert.Equal(1024, t.PreFir.Length);
        Assert.Equal(1024, t.G2Fir.Length);
        Assert.All(t.PreFir.Skip(FirFitter.PreZeroTail), v => Assert.Equal(0f, v));
        Assert.InRange(t.Nlmix, 0f, (float)FirFitter.NlmixMax);
        Assert.Equal(VxampFormat.G2HeaderFloats(), t.G2Header);
        Assert.Equal(VxampFormat.NlmixHeaderFloats(), t.NlmixHeader);
    }

    [Fact]
    public void DriveSignal_is_the_embedded_reference()
    {
        var x = DriveSignal.Get();
        Assert.Equal(16000, x.Length);
        Assert.Equal(0.3, Dsp.Rms(Dsp.ToDouble(x)), 2);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter FirFitterTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement**

`src/Sonulab.Distill/FirFitter.cs`:

```csharp
using System.Numerics;
using System.Reflection;

namespace Sonulab.Distill;

/// <summary>The fixed 0.3-RMS drive/reference signal (numpy default_rng(0), 16000
/// samples). Embedded because numpy's PCG64+ziggurat is not portable; generated by
/// tools/distiller/make_cs_fixtures.py on 2026-07-03.</summary>
public static class DriveSignal
{
    private static float[]? _cached;

    public static float[] Get()
    {
        if (_cached is not null) return _cached;
        using var s = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Sonulab.Distill.Resources.drive_signal.f32")
            ?? throw new InvalidOperationException("drive_signal.f32 resource missing");
        var bytes = new byte[16000 * 4];
        s.ReadExactly(bytes);
        var x = new float[16000];
        Buffer.BlockCopy(bytes, 0, x, 0, bytes.Length);
        return _cached = x;
    }
}

/// <summary>Wiener–Hammerstein fitter (port of tools/distiller/fit.py). See that
/// file's docstring for the fit-procedure rationale; this port preserves its
/// numerics step for step.</summary>
public static class FirFitter
{
    public const int NTaps = 1024;
    public const int PreZeroTail = 1008;
    public const int NPreShort = 64;
    public const double NlmixMax = 0.7;
    public const double DriveLevel = 0.3;
    public const int NamDefaultSampleRate = 48000;

    public static WhTensors FitWh(INamProcessor model)
    {
        // 1) small-signal linear IR at the device rate
        var ir = Dsp.ToDouble(Probe.LinearIrOfModel(model, n: 8192));
        int sr = model.SampleRate ?? NamDefaultSampleRate;
        if (sr != DeviceSim.SampleRate)
            ir = Resampler.ResamplePoly(ir, DeviceSim.SampleRate, sr);

        // 2) linear cascade split
        var (pre, g2) = DesignLinear(ir);

        // 3) nlmix scalar + output-level calibration at drive level
        var (s, gain) = FitNl(model, pre, g2);
        var g2Cal = Dsp.ToFloat(Dsp.ToDouble(g2).Select(v => v * gain).ToArray());

        return new WhTensors(pre, VxampFormat.G2HeaderFloats(), g2Cal,
                             VxampFormat.NlmixHeaderFloats(), (float)s);
    }

    // ------------------------------------------------------------------ linear

    public static (float[] pre, float[] g2) DesignLinear(double[] irDev)
    {
        var target = irDev[..Math.Min(irDev.Length, 2 * NTaps - 1)];

        // candidate A: delta pre, truncated IR in g2
        var preA = new double[NTaps]; preA[0] = 1.0;
        var g2A = new double[NTaps];
        Array.Copy(target, g2A, Math.Min(target.Length, NTaps));
        double errA = CascadeErr(preA, g2A, target);

        // candidate B: short min-phase pre + deconvolved g2 (VoidX-like split)
        double[] preB, g2B;
        double errB;
        try
        {
            preB = MinphasePre(target);
            g2B = DeconvG2(target, preB);
            errB = CascadeErr(preB, g2B, target);
        }
        catch (ArithmeticException)   // degenerate IR
        {
            preB = preA; g2B = g2A; errB = double.PositiveInfinity;
        }

        var (preD, g2D) = errB < errA ? (preB, g2B) : (preA, g2A);
        var pre = PadTaps(preD);
        for (int i = PreZeroTail; i < NTaps; i++) pre[i] = 0f;   // corpus invariant
        return (pre, PadTaps(g2D));
    }

    private static float[] PadTaps(double[] taps)
    {
        var outT = new float[NTaps];
        for (int i = 0; i < Math.Min(taps.Length, NTaps); i++) outT[i] = (float)taps[i];
        return outT;
    }

    /// <summary>Homomorphic (real-cepstrum) minimum-phase pre: smooth log|H| by low
    /// quefrencies, fold to min phase, exponentiate, truncate to nPre taps.</summary>
    public static double[] MinphasePre(double[] ir, int nPre = NPreShort, int nFft = 8192,
                                       int nLifter = 32)
    {
        var mag = new double[nFft];
        var H = Dsp.Fft(ir, nFft);
        for (int i = 0; i < nFft; i++) mag[i] = Math.Log(Math.Max(H[i].Magnitude, 1e-9));
        var cep = Dsp.Ifft(mag.Select(v => new Complex(v, 0)).ToArray());

        var cepS = new Complex[nFft];
        for (int i = 0; i < nLifter; i++) cepS[i] = cep[i].Real;
        for (int i = nFft - (nLifter - 1); i < nFft; i++) cepS[i] = cep[i].Real;

        var fold = new Complex[nFft];
        fold[0] = cepS[0];
        for (int i = 1; i < nFft / 2; i++) fold[i] = 2.0 * cepS[i];
        fold[nFft / 2] = cepS[nFft / 2];

        var spec = fold.Select(v => v).ToArray();
        MathNet.Numerics.IntegralTransforms.Fourier.Forward(
            spec, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);
        for (int i = 0; i < nFft; i++) spec[i] = Complex.Exp(spec[i]);
        var hMin = Dsp.Ifft(spec);
        return hMin.Take(nPre).Select(v => v.Real).ToArray();
    }

    /// <summary>Regularized spectral deconvolution: g2 such that pre ⊛ g2 ≈ target.</summary>
    public static double[] DeconvG2(double[] target, double[] pre, int nOut = NTaps,
                                    double eps = 1e-3)
    {
        int nFft = 1;
        while (nFft < target.Length + nOut) nFft *= 2;
        var T = Dsp.Fft(target, nFft);
        var P = Dsp.Fft(pre, nFft);
        double p2Max = P.Max(c => c.Magnitude * c.Magnitude);
        var G = new Complex[nFft];
        for (int i = 0; i < nFft; i++)
        {
            double p2 = P[i].Magnitude * P[i].Magnitude;
            G[i] = T[i] * Complex.Conjugate(P[i]) / (p2 + eps * p2Max);
        }
        return Dsp.Ifft(G).Take(nOut).Select(v => v.Real).ToArray();
    }

    public static double CascadeErr(double[] pre, double[] g2, double[] target)
    {
        var casc = Dsp.Convolve(pre, g2);
        int n = Math.Max(casc.Length, target.Length);
        var c = new double[n]; casc.CopyTo(c, 0);
        var t = new double[n]; target.CopyTo(t, 0);
        var diff = new double[n];
        for (int i = 0; i < n; i++) diff[i] = c[i] - t[i];
        return Dsp.Norm(diff) / (Dsp.Norm(t) + 1e-12);
    }

    // ------------------------------------------------------------------ nonlinearity

    /// <summary>Run the model on device-rate input, resampling in/out if rates differ
    /// (port of fit._model_ref_at_device_rate — mind the float32 casts at the
    /// model.Process boundary).</summary>
    public static double[] ModelRefAtDeviceRate(INamProcessor model, float[] xDev)
    {
        int sr = model.SampleRate ?? NamDefaultSampleRate;
        if (sr == DeviceSim.SampleRate)
            return Dsp.ToDouble(model.Process(xDev)).Take(xDev.Length).ToArray();
        var xM = Resampler.ResamplePoly(Dsp.ToDouble(xDev), sr, DeviceSim.SampleRate);
        var yM = Dsp.ToDouble(model.Process(Dsp.ToFloat(xM)));
        var y = Resampler.ResamplePoly(yM, DeviceSim.SampleRate, sr);
        var outY = new double[xDev.Length];
        Array.Copy(y, outY, Math.Min(y.Length, outY.Length));
        return outY;
    }

    /// <summary>Grid-fit (nlmix, gain): RMS-matched shape scoring; snaps to exactly 0
    /// when the best nonzero scalar improves the match by less than 0.5%.</summary>
    public static (double nlmix, double gain) FitNl(INamProcessor model, float[] pre, float[] g2)
    {
        var x = DriveSignal.Get();
        var refY = ModelRefAtDeviceRate(model, x);
        double refRms = Dsp.Rms(refY);

        var mid = Dsp.FirFilter(Dsp.ToDouble(pre), Dsp.ToDouble(x));   // stays float64, like fit.py
        var g2D = Dsp.ToDouble(g2);

        (double err, double a) Score(double s)
        {
            var y = Dsp.FirFilter(g2D, Nonlinearity.ApplyNl(mid, s));
            double yRms = Dsp.Rms(y);
            var diff = new double[y.Length];
            if (yRms < 1e-12 || refRms < 1e-12)
            {
                for (int i = 0; i < y.Length; i++) diff[i] = y[i] - refY[i];
                return (Dsp.Norm(diff), 1.0);
            }
            double a = refRms / yRms;
            for (int i = 0; i < y.Length; i++) diff[i] = a * y[i] - refY[i];
            return (Dsp.Norm(diff), a);
        }

        var (e0, a0) = Score(0.0);
        double bestS = 0.0, bestE = e0, bestA = a0;
        for (int i = 1; i <= 70; i++)                     // s = 0.01 .. 0.70 step 0.01
        {
            double s = i * 0.01;
            var (e, a) = Score(s);
            if (e < bestE) { bestS = s; bestE = e; bestA = a; }
        }

        if (bestE > e0 * (1.0 - 0.005)) return (0.0, a0);   // no material nl improvement
        return (bestS, bestA);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter FirFitterTests`
Expected: PASS (5 tests). `FitWh_on_synthetic_fixture` runs the full fit (71 grid evaluations × 16k-sample FIR) — expect a few seconds.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "distill-cs: WH fitter (min-phase split, regularized deconv, nlmix grid + level calibration)"
```


---

### Task 11: Fidelity metric + Distiller orchestrator (Fidelity.cs, Distiller.cs)

Ports the rest of `tools/distiller/distill.py`: the gain/polarity/delay-invariant fidelity metric (`_best_lag`, `_aligned_nrmse`, `_shape_err`, `_nam_ir_dev`, `fidelity_vs_nam` — C# computes `our_err` only; the VoidX-pair comparisons stay Python-side) and the public `Distiller` entry point with staged progress, cancellation between DSP blocks, and the baked reference loudness.

**Files:**
- Create: `src/Sonulab.Distill/Fidelity.cs`, `src/Sonulab.Distill/Distiller.cs`
- Test: `tests/Sonulab.Distill.Tests/DistillerTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–10.
- Produces:
  - `static class Fidelity`: `const int AlignMaxLag = 128`, `int BestLag(double[] refY, double[] y, int maxLag = 128)`, `double AlignedNrmse(double[] refY, double[] y)`, `double ShapeErr(double[] refIr, double[] devIr, double[] refDriven, double[] devDriven)`, `double[] NamIrDev(INamProcessor model)`, `double FidelityVsNam(INamProcessor model, WhTensors tensors)`.
  - `enum DistillStage { LoadModel, ProbeIr, FitLinear, FitNonlinearity, Normalize, Encode, Done }`
  - `sealed record DistillProgress(DistillStage Stage, string Message)`
  - `sealed class DistillException : Exception` (message + inner)
  - `static class Distiller`: `const double DeviceReferenceDb = 13.531918973745606`, `WhTensors LoudnessNormalize(WhTensors t)`, `byte[] Distill(string namPath, IProgress<DistillProgress>? progress = null, CancellationToken ct = default)`, `Task DistillAsync(string namPath, string outPath, IProgress<DistillProgress>? progress = null, CancellationToken ct = default)`.
- **Phase 2 consumes `DistillAsync` — this signature is the app-facing seam from the spec.**

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Distill.Tests/DistillerTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter DistillerTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement Fidelity**

`src/Sonulab.Distill/Fidelity.cs`:

```csharp
namespace Sonulab.Distill;

/// <summary>Gain-, polarity- and delay-invariant fidelity metric (port of the
/// metric half of tools/distiller/distill.py). C# computes our_err only — the
/// VoidX-pair comparisons remain Python-side analysis tooling.</summary>
public static class Fidelity
{
    public const int AlignMaxLag = 128;   // ±samples (~2.9 ms) searched for bulk delay

    public static int BestLag(double[] refY, double[] y, int maxLag = AlignMaxLag)
    {
        int bestLag = 0;
        double bestC = -1.0;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            var (a, b) = Slice(refY, y, lag);
            double denom = Dsp.Norm(a) * Dsp.Norm(b);
            if (denom < 1e-30) continue;
            double c = Math.Abs(Dsp.Dot(a, b)) / denom;
            if (c > bestC) { bestC = c; bestLag = lag; }
        }
        return bestLag;
    }

    public static double AlignedNrmse(double[] refY, double[] y)
    {
        int n = Math.Min(refY.Length, y.Length);
        refY = refY[..n]; y = y[..n];
        int lag = BestLag(refY, y);
        var (a, b) = Slice(refY, y, lag);
        double aNrm = Dsp.Norm(a);
        double bSq = Dsp.Dot(b, b);
        if (aNrm < 1e-12 || bSq < 1e-24)
            return aNrm < 1e-12 && bSq < 1e-24 ? 0.0 : 1.0;
        double g = Dsp.Dot(a, b) / bSq;                 // signed gain (absorbs polarity)
        var diff = new double[a.Length];
        for (int i = 0; i < a.Length; i++) diff[i] = g * b[i] - a[i];
        return Dsp.Norm(diff) / aNrm;
    }

    /// <summary>lag >= 0: a = ref[lag:], b = y[:len(a)]; lag &lt; 0: a = ref[:lag], b = y[-lag:].</summary>
    private static (double[] a, double[] b) Slice(double[] refY, double[] y, int lag) =>
        lag >= 0
            ? (refY[lag..], y[..(refY.Length - lag)])
            : (refY[..^(-lag)], y[(-lag)..]);

    public static double ShapeErr(double[] refIr, double[] devIr,
                                  double[] refDriven, double[] devDriven) =>
        0.5 * ((1.0 - Probe.LogmagCorr(refIr, devIr)) + AlignedNrmse(refDriven, devDriven));

    /// <summary>NAM small-signal IR at the device rate (port of _nam_ir_dev).</summary>
    public static double[] NamIrDev(INamProcessor model)
    {
        var ir = Dsp.ToDouble(Probe.LinearIrOfModel(model, n: 8192));
        int sr = model.SampleRate ?? FirFitter.NamDefaultSampleRate;
        return sr != DeviceSim.SampleRate
            ? Resampler.ResamplePoly(ir, DeviceSim.SampleRate, sr)
            : ir;
    }

    public static double FidelityVsNam(INamProcessor model, WhTensors tensors)
    {
        var x = DriveSignal.Get();
        var namIr = NamIrDev(model);
        var namDriven = FirFitter.ModelRefAtDeviceRate(model, x);
        var ourIr = Dsp.ToDouble(DeviceSim.LinearIr(tensors));
        var ourDriven = Dsp.ToDouble(DeviceSim.Simulate(tensors, x));
        return ShapeErr(namIr, ourIr, namDriven, ourDriven);
    }
}
```

- [ ] **Step 4: Implement Distiller**

`src/Sonulab.Distill/Distiller.cs`:

```csharp
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
```

**Consistency note:** `Distill` inlines `FirFitter.FitWh`'s three steps so progress/cancellation land between them; `FitWh` stays as the single-call form used by tests. If you change one, change both.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter DistillerTests`
Expected: PASS (7 tests). The two full-distill tests each run the grid fit — expect several seconds.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "distill-cs: fidelity metric + Distiller orchestrator (staged progress, cancellation, baked reference dB)"
```

---

### Task 12: Parity gates vs the Python oracle + docs + full verification

The acceptance gate from the spec: C# output must match Python golden `.vxamp`s within the tolerances in the plan header, on the committed synthetic fixture (always) and the corpus (when present).

**Files:**
- Create: `tests/Sonulab.Distill.Tests/ParityTests.cs`
- Modify: `docs/distiller.md` (append section), `CLAUDE.md` (architecture line)

**Interfaces:**
- Consumes: everything; fixtures + gitignored `goldens-corpus/` from Task 8.

- [ ] **Step 1: Write the parity tests**

`tests/Sonulab.Distill.Tests/ParityTests.cs`:

```csharp
using System.Text.Json;

namespace Sonulab.Distill.Tests;

public class ParityTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static double RelL2(float[] cs, float[] py)
    {
        double num = 0, den = 0;
        for (int i = 0; i < cs.Length; i++)
        {
            double d = (double)cs[i] - py[i];
            num += d * d;
            den += (double)py[i] * py[i];
        }
        return Math.Sqrt(num) / (Math.Sqrt(den) + 1e-12);
    }

    private static void AssertParity(string label, byte[] csBlob, byte[] pyBlob,
                                     double csErr, double pyErr)
    {
        var cs = VxampCodec.Decode(csBlob);
        var py = VxampCodec.Decode(pyBlob);
        Assert.True(RelL2(cs.PreFir, py.PreFir) <= 1e-3,
            $"{label}: pre_fir relL2 {RelL2(cs.PreFir, py.PreFir):e2}");
        Assert.True(RelL2(cs.G2Fir, py.G2Fir) <= 1e-3,
            $"{label}: g2_fir relL2 {RelL2(cs.G2Fir, py.G2Fir):e2}");
        Assert.Equal(py.G2Header, cs.G2Header);
        Assert.Equal(py.NlmixHeader, cs.NlmixHeader);
        Assert.True(Math.Abs(cs.Nlmix - py.Nlmix) <= 0.010001,
            $"{label}: nlmix cs={cs.Nlmix} py={py.Nlmix}");
        Assert.True(Math.Abs(csErr - pyErr) <= 1e-3,
            $"{label}: our_err cs={csErr:f6} py={pyErr:f6}");
    }

    [Fact]
    public void Synthetic_fixture_matches_python_golden()
    {
        var golden = JsonDocument.Parse(File.ReadAllText(Fixture("golden_metrics.json"))).RootElement;
        Assert.Equal(golden.GetProperty("device_reference_db").GetDouble(),
                     Distiller.DeviceReferenceDb);   // exact — same baked constant

        var csBlob = Distiller.Distill(Fixture("synthetic.nam"));
        var pyBlob = File.ReadAllBytes(Fixture("synthetic.golden.vxamp"));
        var model = NamParser.Load(Fixture("synthetic.nam"));
        double csErr = Fidelity.FidelityVsNam(model, VxampCodec.Decode(csBlob));
        AssertParity("synthetic", csBlob, pyBlob, csErr,
                     golden.GetProperty("our_err").GetDouble());
    }

    [Fact]
    public void Corpus_goldens_match_when_present()
    {
        // Corpus goldens are gitignored (generated by make_cs_fixtures.py --corpus on
        // the corpus machine). Vacuously passes elsewhere — same convention as the
        // Python corpus tests.
        var dir = FindUp("tests/Sonulab.Distill.Tests/goldens-corpus");
        if (dir is null) return;
        var metrics = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir, "metrics.json"))).RootElement;

        foreach (var goldenFile in Directory.GetFiles(dir, "*.golden.vxamp"))
        {
            string name = Path.GetFileName(goldenFile).Replace(".golden.vxamp", "");
            string namPath = File.ReadAllText(
                Path.Combine(dir, $"{name}.nam.path.txt")).Trim();
            if (!File.Exists(namPath)) continue;   // corpus moved — skip this pair

            var csBlob = Distiller.Distill(namPath);
            var model = NamParser.Load(namPath);
            double csErr = Fidelity.FidelityVsNam(model, VxampCodec.Decode(csBlob));
            AssertParity(name, csBlob, File.ReadAllBytes(goldenFile), csErr,
                         metrics.GetProperty(name).GetDouble());
        }
    }

    private static string? FindUp(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
```

- [ ] **Step 2: Run the parity tests**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter ParityTests`
Expected: PASS. The corpus test distills every paired corpus amp (~14 full fits) — allow a few minutes. **If a gate fails:** this is the moment the whole plan exists for — use superpowers:systematic-debugging. Compare stage-by-stage against Python (IR bytes, post-resample IR, pre/g2 before nl fit, chosen nlmix, gain, post-normalize g2) by adding temporary dumps on both sides; find the FIRST diverging stage. Do not loosen tolerances beyond 5e-3 without user sign-off.

- [ ] **Step 3: Update docs**

Append to `docs/distiller.md`:

```markdown
## C# port (Sonulab.Distill)

The distiller is ported to .NET in `src/Sonulab.Distill` (sub-project 2b Phase 1) so the
app distills natively — no Python at runtime. This Python implementation remains the
REFERENCE ORACLE: it is the ear-validated original, and `tools/distiller/make_cs_fixtures.py`
generates the golden fixtures the C# port is parity-tested against
(`tests/Sonulab.Distill.Tests/ParityTests.cs`; tolerances in
`docs/superpowers/plans/2026-07-03-native-distiller.md`). If you change the fit here,
regenerate goldens (`python tools/distiller/make_cs_fixtures.py --corpus`) and port the
change to C#.
```

In `CLAUDE.md`, in the `## Architecture` section, add after the `src/Sonulab.Core` bullet:

```markdown
- **`src/Sonulab.Distill`** (no UI, unit-tested): native C# port of the .nam→.vxamp
  distiller (WaveNet runner, WH fitter, vxamp codec). Python `tools/distiller/` is the
  reference oracle; parity goldens via `tools/distiller/make_cs_fixtures.py`.
```

- [ ] **Step 4: Full-suite verification**

Run: `dotnet build` then `dotnet test`
Expected: build clean; ALL tests green (146 pre-existing + every new Distill test). Paste the final test-count line into the commit message body.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "distill-cs: parity gates vs Python oracle + docs (Phase 1 complete)"
```

---

## Phase 1 exit criteria (from the spec)

- `Sonulab.Distill` builds; all module tests + synthetic parity green everywhere; corpus parity green on the corpus machine.
- `Distiller.DistillAsync(namPath, outPath, IProgress<DistillProgress>, ct)` is the Phase-2 seam.
- Deferred to the bench session (add to the hardware checklist doc in Phase 2): upload one C#-distilled amp and ear-check against its Python-distilled twin.

