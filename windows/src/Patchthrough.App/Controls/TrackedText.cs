using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Patchthrough.App.Theme;

namespace Patchthrough.App.Controls;

/// <summary>
/// A short label with letter spacing.
///
/// WPF has no letter-spacing property, and the design's uppercase micro-labels are
/// specified with 0.95 of tracking at 10.5. Without it they set too tight and read
/// as a smaller version of body text rather than as a label. This draws the glyphs
/// itself and adds the tracking between them, which is the only way to get it.
///
/// It is used for the sidebar's date headers and the settings section headers, so
/// it stays deliberately small: one line, no wrapping, no trimming.
/// </summary>
public sealed class TrackedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TrackedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Upper-case the text when drawing, leaving the source string alone.</summary>
    public static readonly DependencyProperty UppercaseProperty = DependencyProperty.Register(
        nameof(Uppercase), typeof(bool), typeof(TrackedText),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(TrackedText),
        new FrameworkPropertyMetadata(PT.C.Text4Brush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(TrackedText),
        new FrameworkPropertyMetadata(PT.F.SectionHead, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(
        nameof(Tracking), typeof(double), typeof(TrackedText),
        new FrameworkPropertyMetadata(PT.F.LabelTracking, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool Uppercase
    {
        get => (bool)GetValue(UppercaseProperty);
        set => SetValue(UppercaseProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public double Tracking
    {
        get => (double)GetValue(TrackingProperty);
        set => SetValue(TrackingProperty, value);
    }

    private string Rendered => Uppercase
        ? (Text ?? "").ToUpper(CultureInfo.CurrentCulture)
        : Text ?? "";

    private FormattedText? _formatted;

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Build();
        if (text is null) return new Size(0, 0);
        // Every gap but the last one after the final glyph.
        var glyphs = Rendered.Length;
        var extra = glyphs > 1 ? (glyphs - 1) * Tracking : 0;
        return new Size(text.WidthIncludingTrailingWhitespace + extra, text.Height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var rendered = Rendered;
        if (rendered.Length == 0) return;

        var x = 0d;
        foreach (var character in rendered)
        {
            var glyph = Format(character.ToString());
            drawingContext.DrawText(glyph, new Point(x, 0));
            x += glyph.WidthIncludingTrailingWhitespace + Tracking;
        }
    }

    private FormattedText? Build() => _formatted = Rendered.Length == 0 ? null : Format(Rendered);

    private FormattedText Format(string value) => new(
        value,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(PT.F.Ui, FontStyles.Normal, PT.F.SectionHeadWeight, FontStretches.Normal),
        FontSize,
        Foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
