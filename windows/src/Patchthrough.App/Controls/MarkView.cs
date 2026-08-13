using System.Windows;
using System.Windows.Media;
using Patchthrough.App.Theme;

namespace Patchthrough.App.Controls;

/// <summary>
/// The Patchthrough mark as a view. Square, and it draws itself at whatever size
/// it is given rather than scaling a bitmap.
/// </summary>
public sealed class MarkView : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(MarkView),
        new FrameworkPropertyMetadata(PT.C.TextBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Stroke weight in grid units. It goes heavy while transcribing, which is
    /// the same signal the macOS menu bar icon uses.
    /// </summary>
    public static readonly DependencyProperty WeightProperty = DependencyProperty.Register(
        nameof(Weight), typeof(double), typeof(MarkView),
        new FrameworkPropertyMetadata(Mark.RegularWeight, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double Weight
    {
        get => (double)GetValue(WeightProperty);
        set => SetValue(WeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Square, and never larger than the space offered. An infinite constraint
        // means the parent is asking for a natural size, which is the mark's own.
        var side = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(side) || double.IsNaN(side)) side = PT.M.MarkSize;
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;

        var (geometry, thickness) = Mark.Build(side, Weight);
        drawingContext.DrawGeometry(null, Mark.PenFor(Stroke, thickness), geometry);
    }
}
