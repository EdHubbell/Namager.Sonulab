using Sonulab.App.Services;
using Xunit;

public class ParameterExposureTests
{
    static ParameterExposure Ex() => new(new[] { @"root\app\amp\sag", @"root\app\delay\ddfolder", @"*\_st" });

    [Fact] public void Exact_path_is_hidden() => Assert.True(Ex().IsHidden(@"root\app\amp\sag"));
    [Fact] public void Prefix_hides_descendants() =>
        Assert.True(Ex().IsHidden(@"root\app\delay\ddfolder\fdbkr"));
    [Fact] public void Prefix_does_not_hide_siblings() =>
        Assert.False(Ex().IsHidden(@"root\app\delay\fdbk"));
    [Fact] public void Suffix_glob_hides_by_ending() =>
        Assert.True(Ex().IsHidden(@"root\app\output\pst\ctl1\_st"));
    [Fact] public void Unlisted_is_shown() => Assert.False(Ex().IsHidden(@"root\app\gate\threshold"));
    [Fact] public void Default_loads_embedded() => Assert.NotNull(ParameterExposure.Default);
}
