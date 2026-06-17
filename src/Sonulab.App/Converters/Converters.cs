using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Sonulab.App.Converters;

/// <summary>
/// Equality / predicate converters for XAML bindings.
/// </summary>
public static class Eq
{
    // --- Kind converters (ParameterFieldViewModel.Kind -> bool for IsVisible) ---
    public static readonly IValueConverter Float       = new KindConverter("float");
    public static readonly IValueConverter Enum        = new KindConverter("enum");
    public static readonly IValueConverter Plist       = new KindConverter("plist");
    public static readonly IValueConverter Str         = new KindConverter("string");
    /// <summary>Visible for both enum and plist (rendered as ComboBox).</summary>
    public static readonly IValueConverter EnumOrPlist = new KindMultiConverter("enum", "plist");
    /// <summary>int == 0 -> true (used for empty-state TextBlock visibility).</summary>
    public static readonly IValueConverter ZeroCount   = new ZeroCountConverter();

    private sealed class KindConverter(string kind) : IValueConverter
    {
        public object? Convert(object? value, Type _, object? __, CultureInfo ___) =>
            value is string s && s == kind;
        public object? ConvertBack(object? v, Type _, object? __, CultureInfo ___) =>
            throw new NotSupportedException();
    }

    private sealed class KindMultiConverter(params string[] kinds) : IValueConverter
    {
        public object? Convert(object? value, Type _, object? __, CultureInfo ___) =>
            value is string s && kinds.Contains(s);
        public object? ConvertBack(object? v, Type _, object? __, CultureInfo ___) =>
            throw new NotSupportedException();
    }

    private sealed class ZeroCountConverter : IValueConverter
    {
        public object? Convert(object? value, Type _, object? __, CultureInfo ___) =>
            value is int i && i == 0;
        public object? ConvertBack(object? v, Type _, object? __, CultureInfo ___) =>
            throw new NotSupportedException();
    }
}

/// <summary>
/// Converts IsConnected (bool) to a brush for the status dot Ellipse.
/// true  => LimeGreen (connected)
/// false => Gray (disconnected)
/// </summary>
public sealed class BoolToBrush : IValueConverter
{
    public static readonly BoolToBrush Connected = new();
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
        => value is true ? Brushes.LimeGreen : Brushes.Gray;
    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// bool -> FontStyle: true => Italic (empty preset slot), false => Normal.
/// </summary>
public sealed class BoolToItalic : IValueConverter
{
    public static readonly BoolToItalic Instance = new();
    public object? Convert(object? value, Type _, object? __, CultureInfo ___) =>
        value is bool b && b ? FontStyle.Italic : FontStyle.Normal;
    public object? ConvertBack(object? v, Type _, object? __, CultureInfo ___) =>
        throw new NotSupportedException();
}

/// <summary>
/// bool -> double opacity: true => 0.4 (empty slot dimmed), false => 1.0.
/// </summary>
public sealed class BoolToOpacity : IValueConverter
{
    public static readonly BoolToOpacity Instance = new();
    public object? Convert(object? value, Type _, object? __, CultureInfo ___) =>
        value is bool b && b ? 0.4 : 1.0;
    public object? ConvertBack(object? v, Type _, object? __, CultureInfo ___) =>
        throw new NotSupportedException();
}

/// <summary>bool? -> bool: true when non-null. Used to show the enable icon only when a block has an on_off toggle.</summary>
public sealed class NotNull : IValueConverter
{
    public static readonly NotNull Instance = new();
    public object? Convert(object? value, Type _, object? __, CultureInfo ___) => value is not null;
    public object? ConvertBack(object? v, Type _, object? __, CultureInfo ___) => throw new NotSupportedException();
}

/// <summary>enable-state (bool?) -> brush: true => LimeGreen (on), false/null => Gray (off).</summary>
public sealed class EnabledToBrush : IValueConverter
{
    public static readonly EnabledToBrush Instance = new();
    public object? Convert(object? value, Type _, object? __, CultureInfo ___) =>
        value is true ? Brushes.LimeGreen : Brushes.Gray;
    public object? ConvertBack(object? v, Type _, object? __, CultureInfo ___) => throw new NotSupportedException();
}

/// <summary>enable-state (bool?) -> tooltip text: true => "Effect on", else "Effect off".</summary>
public sealed class EnabledToTooltip : IValueConverter
{
    public static readonly EnabledToTooltip Instance = new();
    public object? Convert(object? value, Type _, object? __, CultureInfo ___) =>
        value is true ? "Effect on" : "Effect off";
    public object? ConvertBack(object? v, Type _, object? __, CultureInfo ___) => throw new NotSupportedException();
}
