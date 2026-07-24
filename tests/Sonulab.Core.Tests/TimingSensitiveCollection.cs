using Xunit;

/// <summary>Serializes the tests that touch the PROCESS-WIDE timer resolution.
///
/// TimerResolutionScope's refcount is static, and SerialSonuLink.SendBatchAsync acquires a scope
/// on every batch. xUnit runs test classes in parallel by default, so without this collection a
/// batch test in one class could hold the resolution while another asserts it has been released —
/// a flake that would look like a refcount bug. The wall-clock timing assertions in these classes
/// are also steadier when they are not competing with each other for CPU.</summary>
[CollectionDefinition(TimingSensitive.Name, DisableParallelization = true)]
public class TimingSensitiveCollection { }

public static class TimingSensitive
{
    public const string Name = "timing-sensitive";
}
