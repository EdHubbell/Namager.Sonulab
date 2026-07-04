using System.Globalization;
using Avalonia.Data.Converters;

namespace Sonulab.App.Converters;

/// <summary>0-based slot index -> 1-based display slot number ("Slot 7").</summary>
public sealed class IndexToSlotNumber : IValueConverter
{
    public static readonly IndexToSlotNumber Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? $"Slot {i + 1}" : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
