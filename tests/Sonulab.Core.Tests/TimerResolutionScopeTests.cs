using Sonulab.Core.Transport;
using Xunit;

/// <summary>The scope exists so the pipelined batch loop can pace itself with plain Task.Delay
/// instead of a busy-wait (see SerialSonuLink.PipelineWaitAsync). These tests pin the contract
/// the loop depends on: it is refcounted, it always releases, and it never throws.</summary>
[Collection(TimingSensitive.Name)]
public class TimerResolutionScopeTests
{
    [Fact]
    public void Scope_is_active_for_its_lifetime_and_released_after()
    {
        Assert.False(TimerResolutionScope.IsActive);
        using (TimerResolutionScope.Acquire())
        {
            // Only assertable where the platform can actually provide it. Everywhere else the
            // scope is inert by design and the caller falls back — which the next test covers.
            if (OperatingSystem.IsWindows()) Assert.True(TimerResolutionScope.IsActive);
        }
        Assert.False(TimerResolutionScope.IsActive);
    }

    [Fact]
    public void Nested_scopes_refcount_so_an_inner_release_does_not_drop_the_outer()
    {
        // Concurrent bulk reads would otherwise let the first one to finish yank the resolution
        // out from under the other, silently returning it to ~15.6 ms pacing mid-batch.
        using (TimerResolutionScope.Acquire())
        {
            using (TimerResolutionScope.Acquire())
            {
                if (OperatingSystem.IsWindows()) Assert.True(TimerResolutionScope.IsActive);
            }
            if (OperatingSystem.IsWindows()) Assert.True(TimerResolutionScope.IsActive);
        }
        Assert.False(TimerResolutionScope.IsActive);
    }

    [Fact]
    public void Acquire_and_dispose_never_throw()
    {
        // The transport wraps every batch in this. A throw here would fail bulk reads outright
        // on any host where winmm is missing or refuses the request.
        var scope = TimerResolutionScope.Acquire();
        scope.Dispose();
        scope.Dispose();                       // double dispose must also be harmless
        Assert.False(TimerResolutionScope.IsActive);
    }
}
