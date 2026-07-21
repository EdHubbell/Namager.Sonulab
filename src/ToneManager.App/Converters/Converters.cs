using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ToneManager.App.Converters;

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
/// true  => Sonulab.SuccessBrush (connected)   false => Sonulab.TextMutedBrush
/// Resolves theme tokens at convert time; falls back to fixed brushes if the
/// theme layer isn't loaded (e.g. bare control tests). Note: a live OS theme
/// switch updates the dot on the next IsConnected change, not instantly.
/// </summary>
public sealed class BoolToBrush : IValueConverter
{
    public static readonly BoolToBrush Connected = new();
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
        => value is true
            ? ResolveBrush("Sonulab.SuccessBrush", Brushes.LimeGreen)
            : ResolveBrush("Sonulab.TextMutedBrush", Brushes.Gray);
    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();

    internal static object ResolveBrush(string key, IBrush fallback) =>
        Application.Current is { } app &&
        app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is IBrush b
            ? b : fallback;
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

/// <summary>enable-state (bool?) -> brush: true => Sonulab.SuccessBrush (on), false/null => muted (off).</summary>
public sealed class EnabledToBrush : IValueConverter
{
    public static readonly EnabledToBrush Instance = new();
    public object? Convert(object? value, Type _, object? __, CultureInfo ___) =>
        value is true
            ? BoolToBrush.ResolveBrush("Sonulab.SuccessBrush", Brushes.LimeGreen)
            : BoolToBrush.ResolveBrush("Sonulab.TextMutedBrush", Brushes.Gray);
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
