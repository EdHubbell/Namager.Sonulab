using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Sonulab.Core.Transport;

/// <summary>Raises the Windows scheduler tick to 1 ms for the lifetime of the scope.
///
/// Why the transport needs this: the pipelined batch loop paces sends against a 30 ms floor, and
/// at Windows' default ~15.6 ms tick <c>Task.Delay</c> cannot resolve anything finer — measured
/// ~15.5 ms for EVERY requested value from 1 to 10 ms. The loop then sleeps past the moment its
/// floor opens, which cost 43.3 ms/chunk against an achievable 33 (hardware validation,
/// 2026-07-24). With the tick at 1 ms, <c>Task.Delay(3)</c> measures ~3.6 ms and the same pacing
/// needs no busy-wait at all: 31.9 ms for a 30 ms floor, at idle CPU.
///
/// Scope it to a burst, never to the process. The setting is global and slightly raises idle
/// power draw while active, so it is acquired around a bulk read and released immediately after.
/// Windows refcounts begin/end pairs per process, so nested and concurrent scopes are safe.
///
/// Non-Windows, or a winmm that will not load: <see cref="IsActive"/> stays false and callers
/// fall back to their own strategy. Nothing here throws.</summary>
internal sealed class TimerResolutionScope : IDisposable
{
    private const uint PeriodMs = 1;
    private static int _activeCount;
    private readonly bool _held;
    private bool _disposed;

    [SupportedOSPlatform("windows")]
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [SupportedOSPlatform("windows")]
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    /// <summary>True while at least one scope holds the raised resolution. Callers use this to
    /// decide whether a plain Task.Delay is accurate enough or a busy-wait fallback is needed.</summary>
    public static bool IsActive => Volatile.Read(ref _activeCount) > 0;

    /// <summary>Exposed for tests only. IsActive cannot tell a balanced 0 from an over-released
    /// -1, so it cannot catch a double-release on its own.</summary>
    internal static int ActiveCount => Volatile.Read(ref _activeCount);

    /// <summary>Never throws and never returns null — on any platform or failure the scope is
    /// simply inert, and disposing it is a no-op.</summary>
    public static TimerResolutionScope Acquire()
    {
        bool held = false;
        if (OperatingSystem.IsWindows())
        {
            // Swallowed deliberately: the contract is that acquiring a nicety never fails a
            // bulk read. A trimmed or NativeAOT publish can fail the marshal rather than the load.
            try { held = TimeBeginPeriod(PeriodMs) == 0; }   // 0 = TIMERR_NOERROR
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
            catch (MarshalDirectiveException) { }
        }
        if (held) Interlocked.Increment(ref _activeCount);
        return new TimerResolutionScope(held);
    }

    private TimerResolutionScope(bool held) => _held = held;

    public void Dispose()
    {
        // Guard double dispose: a second decrement would corrupt the refcount and unbalance the
        // OS's own begin/end pairing, quietly leaving the process at the default tick.
        if (!_held || _disposed) return;
        _disposed = true;
        Interlocked.Decrement(ref _activeCount);
        if (OperatingSystem.IsWindows())
        {
            try { TimeEndPeriod(PeriodMs); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
            catch (MarshalDirectiveException) { }
        }
    }
}
