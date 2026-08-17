using System.Windows;
using System.Windows.Media;

namespace Patchthrough.App.Theme;

/// <summary>
/// The Patchthrough mark, on its native 24 by 24 grid.
///
/// Ported from PatchthroughMark in Sources/patchthrough/UI/Theme.swift. It is
/// geometry rather than an image for the same reason it is there: it stays crisp
/// at any size and any scale factor, and it takes the colour it is given, which
/// the tray needs in order to sit on either a light or a dark taskbar.
///
/// The whole mark is shifted up by 0.45 grid units. That offset is optical
/// centring and it is measured against the box, so this never normalises the
/// geometry to its own bounds. A Viewbox with Stretch would do exactly that and
/// would quietly move the mark inside its frame.
/// </summary>
public static class Mark
{
    /// <summary>Stroke weight in grid units, at rest.</summary>
    public const double RegularWeight = 1.6;

    /// <summary>Stroke weight in grid units while transcribing.</summary>
    public const double HeavyWeight = 2.1;

    private const double Grid = 24;
    private const double LiftY = 0.45;
    private const double Radius = 6.3;

    /// <summary>
    /// The mark drawn to fill a square of <paramref name="size"/> device-independent
    /// pixels, with the stroke width that belongs to it.
    /// </summary>
    public static (Geometry Geometry, double Thickness) Build(double size, double weight = RegularWeight)
    {
        var s = size / Grid;
        Point P(double x, double y) => new(x * s, (y - LiftY) * s);

        var geometry = new GeometryGroup();
        geometry.Children.Add(new EllipseGeometry(P(12, 12), Radius * s, Radius * s));

        var stroke = new PathFigure { StartPoint = P(2.8, 19.2), IsClosed = false, IsFilled = false };
        stroke.Segments.Add(new BezierSegment(P(5.2, 14.8), P(7.8, 10.9), P(10.3, 9.6), isStroked: true));
        stroke.Segments.Add(new BezierSegment(P(12.8, 8.3), P(16.8, 7.2), P(21.2, 6.4), isStroked: true));
        var path = new PathGeometry();
        path.Figures.Add(stroke);
        geometry.Children.Add(path);

        geometry.Freeze();
        return (geometry, weight * s);
    }

    /// <summary>
    /// A pen for the mark. Round caps, because the mark's stroke ends are round
    /// in the source geometry and a butt cap shortens the sweep visibly.
    /// </summary>
    public static Pen PenFor(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// Where the recording dot sits, given the mark's box. Bottom right, sized
    /// from the same grid as the menu bar dot on macOS: 7 units against an 18
    /// unit mark.
    /// </summary>
    public static Rect RecordDot(double size)
    {
        var diameter = size * (PT.M.RecordDotSize / PT.M.StatusItemSize);
        return new Rect(size - diameter, size - diameter, diameter, diameter);
    }
}
