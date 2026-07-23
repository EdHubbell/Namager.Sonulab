using System.Globalization;
using Avalonia.Media;
using Namager.App.Converters;
using Namager.App.Services;
using Xunit;

public class StatusKindToBrushTests
{
    [Theory]
    [InlineData(StatusKind.Error)]
    [InlineData(StatusKind.Success)]
    [InlineData(StatusKind.Busy)]
    [InlineData(StatusKind.Idle)]
    public void Returns_a_brush_for_every_kind(StatusKind kind)
    {
        var result = StatusKindToBrush.Instance.Convert(kind, typeof(IBrush), null, CultureInfo.InvariantCulture);
        Assert.IsAssignableFrom<IBrush>(result);
    }

    [Fact]
    public void Error_and_success_map_to_different_brushes()
    {
        var err = StatusKindToBrush.Instance.Convert(StatusKind.Error, typeof(IBrush), null, CultureInfo.InvariantCulture);
        var ok  = StatusKindToBrush.Instance.Convert(StatusKind.Success, typeof(IBrush), null, CultureInfo.InvariantCulture);
        Assert.NotEqual(err, ok);
    }
}
