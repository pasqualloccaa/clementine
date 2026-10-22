using System.Windows;
using System.Windows.Media;

namespace Clementine.App.Wheel;

// Angoli in radianti: 0 = in alto, crescono in senso orario. Lo spicchio i è centrato su i * step.
public static class WheelLayout
{
    /// <summary>Indice dello spicchio sotto (dx, dy) relativo al centro, -1 se nel mozzo o fuori.</summary>
    public static int HitTest(double dx, double dy, double inner, double outer, int count)
    {
        var r = Math.Sqrt(dx * dx + dy * dy);
        if (count <= 0 || r < inner || r > outer) return -1;
        var step = 2 * Math.PI / count;
        var a = Math.Atan2(dx, -dy);
        return (int)Math.Floor((a + step / 2) / step + count) % count;
    }

    public static Point PointAt(Point c, double r, double a) => new(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));

    public static Geometry Sector(Point c, double inner, double outer, double a0, double a1)
    {
        var large = a1 - a0 > Math.PI;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(PointAt(c, outer, a0), true, true);
            ctx.ArcTo(PointAt(c, outer, a1), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
            ctx.LineTo(PointAt(c, inner, a1), true, false);
            ctx.ArcTo(PointAt(c, inner, a0), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
