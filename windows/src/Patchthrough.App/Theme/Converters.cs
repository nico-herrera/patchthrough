using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Patchthrough.App.Theme;

/// <summary>Shows an element only when the bound value is true.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Show when false instead.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Shows an element only when the bound value has something in it. Null and an
/// empty string both collapse, so an absent status line takes no space rather
/// than leaving a gap.
/// </summary>
public sealed class PresenceToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value is not null && (value is not string text || text.Length > 0);
        if (Invert) present = !present;
        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The widest a transcript turn may be, from the width of the column it sits in.
///
/// This is what enforces PT.M.TurnMaxWidthFraction, and it is load-bearing.
/// Trailing alignment can only offset an element narrower than its line: at full
/// width both speakers span the column and the me-right, them-left structure
/// silently disappears. The column's own padding comes off first, so the fraction
/// applies to the text area rather than to the pane.
/// </summary>
public sealed class TurnMaxWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double width || double.IsNaN(width) || width <= 0) return double.PositiveInfinity;
        var content = width - (PT.M.TranscriptPad * 2);
        if (content <= 0) return double.PositiveInfinity;
        return content * PT.M.TurnMaxWidthFraction;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A fraction as a percentage width, for the model download bar. Returns zero
/// when the phase reports no byte count, which keeps a bar from claiming progress
/// it does not have.
/// </summary>
public sealed class ProgressFractionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Patchthrough.Windows.Transcription.ModelInstallProgress progress
            ? (progress.Fraction ?? 0) * 100
            : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
