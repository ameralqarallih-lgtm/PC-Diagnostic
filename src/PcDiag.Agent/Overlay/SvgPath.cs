using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// Turns SVG path data (M, L, H, V, C, S, Q, A and Z, absolute and relative) into a GDI+ path, so the recorder draws
/// the icons from the same data as the editor's preview. Elliptical arcs become cubic Béziers.
/// </summary>
internal static class SvgPath
{
    /// <summary>The path for <paramref name="data"/>, scaled by <paramref name="scale"/> and moved to <paramref name="origin"/>.</summary>
    public static GraphicsPath Parse(string data, float scale, PointF origin)
    {
        ArgumentNullException.ThrowIfNull(data);
        var path = new GraphicsPath();
        var reader = new Reader(data);
        var current = PointF.Empty;
        var start = PointF.Empty;
        PointF? lastControl = null;
        var command = ' ';
        PointF Map(PointF p) => new(origin.X + (p.X * scale), origin.Y + (p.Y * scale));

        while (reader.More())
        {
            if (reader.Command() is { } next)
            {
                command = next;
            }
            else if (command == ' ')
            {
                break;
            }

            var relative = char.IsLower(command);
            PointF Point(PointF from)
            {
                var x = reader.Number();
                var y = reader.Number();
                return relative ? new PointF(from.X + x, from.Y + y) : new PointF(x, y);
            }

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    current = start = Point(current);
                    path.StartFigure();
                    lastControl = null;

                    // Further pairs after a move are lines.
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                {
                    var to = Point(current);
                    path.AddLine(Map(current), Map(to));
                    current = to;
                    lastControl = null;
                    break;
                }

                case 'H':
                {
                    var x = reader.Number();
                    var to = new PointF(relative ? current.X + x : x, current.Y);
                    path.AddLine(Map(current), Map(to));
                    current = to;
                    lastControl = null;
                    break;
                }

                case 'V':
                {
                    var y = reader.Number();
                    var to = new PointF(current.X, relative ? current.Y + y : y);
                    path.AddLine(Map(current), Map(to));
                    current = to;
                    lastControl = null;
                    break;
                }

                case 'C':
                {
                    var c1 = Point(current);
                    var c2 = Point(current);
                    var to = Point(current);
                    path.AddBezier(Map(current), Map(c1), Map(c2), Map(to));
                    lastControl = c2;
                    current = to;
                    break;
                }

                case 'S':
                {
                    var c1 = lastControl is { } last ? new PointF((2 * current.X) - last.X, (2 * current.Y) - last.Y) : current;
                    var c2 = Point(current);
                    var to = Point(current);
                    path.AddBezier(Map(current), Map(c1), Map(c2), Map(to));
                    lastControl = c2;
                    current = to;
                    break;
                }

                case 'Q':
                {
                    var q = Point(current);
                    var to = Point(current);
                    var c1 = new PointF(current.X + (2f / 3 * (q.X - current.X)), current.Y + (2f / 3 * (q.Y - current.Y)));
                    var c2 = new PointF(to.X + (2f / 3 * (q.X - to.X)), to.Y + (2f / 3 * (q.Y - to.Y)));
                    path.AddBezier(Map(current), Map(c1), Map(c2), Map(to));
                    lastControl = null;
                    current = to;
                    break;
                }

                case 'A':
                {
                    var rx = reader.Number();
                    var ry = reader.Number();
                    var rotation = reader.Number();
                    var large = reader.Flag();
                    var sweep = reader.Flag();
                    var to = Point(current);
                    Arc(path, current, rx, ry, rotation, large, sweep, to, Map);
                    current = to;
                    lastControl = null;
                    break;
                }

                case 'Z':
                    path.CloseFigure();
                    current = start;
                    lastControl = null;
                    command = ' ';
                    break;
                default:
                    // An unknown command ends the path (the data is ours, so this does not happen).
                    return path;
            }
        }

        return path;
    }

    /// <summary>An SVG elliptical arc (endpoint form) as cubic Béziers of at most a quarter turn each.</summary>
    private static void Arc(GraphicsPath path, PointF from, float rx, float ry, float rotationDegrees, bool large, bool sweep, PointF to, Func<PointF, PointF> map)
    {
        if (from == to)
        {
            return;
        }

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx < 1e-6 || ry < 1e-6)
        {
            path.AddLine(map(from), map(to));
            return;
        }

        var phi = rotationDegrees * Math.PI / 180;
        var cos = Math.Cos(phi);
        var sin = Math.Sin(phi);
        var dx = (from.X - to.X) / 2.0;
        var dy = (from.Y - to.Y) / 2.0;
        var x1 = (cos * dx) + (sin * dy);
        var y1 = (-sin * dx) + (cos * dy);

        double a = rx, b = ry;
        var lambda = (x1 * x1 / (a * a)) + (y1 * y1 / (b * b));
        if (lambda > 1)
        {
            a *= Math.Sqrt(lambda);
            b *= Math.Sqrt(lambda);
        }

        var numerator = (a * a * b * b) - (a * a * y1 * y1) - (b * b * x1 * x1);
        var denominator = (a * a * y1 * y1) + (b * b * x1 * x1);
        var coefficient = (large != sweep ? 1 : -1) * Math.Sqrt(Math.Max(0, numerator / denominator));
        var cx1 = coefficient * a * y1 / b;
        var cy1 = coefficient * -b * x1 / a;
        var cx = (cos * cx1) - (sin * cy1) + ((from.X + to.X) / 2.0);
        var cy = (sin * cx1) + (cos * cy1) + ((from.Y + to.Y) / 2.0);

        static double Angle(double ux, double uy, double vx, double vy)
        {
            var angle = Math.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy));
            return angle;
        }

        var theta = Angle(1, 0, (x1 - cx1) / a, (y1 - cy1) / b);
        var delta = Angle((x1 - cx1) / a, (y1 - cy1) / b, (-x1 - cx1) / a, (-y1 - cy1) / b);
        if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }
        else if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }

        var segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
        var step = delta / segments;
        var t = 4.0 / 3 * Math.Tan(step / 4);
        PointF On(double angle) => new((float)(cx + (a * cos * Math.Cos(angle)) - (b * sin * Math.Sin(angle))), (float)(cy + (a * sin * Math.Cos(angle)) + (b * cos * Math.Sin(angle))));
        (double X, double Y) Tangent(double angle) => ((-a * cos * Math.Sin(angle)) - (b * sin * Math.Cos(angle)), (-a * sin * Math.Sin(angle)) + (b * cos * Math.Cos(angle)));

        var p0 = from;
        for (var i = 0; i < segments; i++)
        {
            var a0 = theta + (i * step);
            var a1 = a0 + step;
            var (t0x, t0y) = Tangent(a0);
            var (t1x, t1y) = Tangent(a1);
            var p3 = i == segments - 1 ? to : On(a1);
            var c1 = new PointF((float)(p0.X + (t * t0x)), (float)(p0.Y + (t * t0y)));
            var c2 = new PointF((float)(p3.X - (t * t1x)), (float)(p3.Y - (t * t1y)));
            path.AddBezier(map(p0), map(c1), map(c2), map(p3));
            p0 = p3;
        }
    }

    /// <summary>Reads commands, numbers and arc flags from path data.</summary>
    private sealed class Reader(string data)
    {
        private int _at;

        public bool More()
        {
            Skip();
            return _at < data.Length;
        }

        public char? Command()
        {
            Skip();
            return _at < data.Length && char.IsAsciiLetter(data[_at]) && data[_at] is not ('e' or 'E') ? data[_at++] : null;
        }

        public bool Flag()
        {
            Skip();
            if (_at < data.Length && data[_at] is '0' or '1')
            {
                return data[_at++] == '1';
            }

            throw new FormatException("An arc flag is 0 or 1.");
        }

        public float Number()
        {
            Skip();
            var begin = _at;
            if (_at < data.Length && data[_at] is '+' or '-')
            {
                _at++;
            }

            var dot = false;
            while (_at < data.Length && (char.IsAsciiDigit(data[_at]) || (data[_at] == '.' && !dot)))
            {
                dot |= data[_at] == '.';
                _at++;
            }

            if (_at < data.Length && data[_at] is 'e' or 'E')
            {
                _at++;
                if (_at < data.Length && data[_at] is '+' or '-')
                {
                    _at++;
                }

                while (_at < data.Length && char.IsAsciiDigit(data[_at]))
                {
                    _at++;
                }
            }

            if (_at == begin)
            {
                throw new FormatException($"A number was expected at {begin}.");
            }

            return float.Parse(data.AsSpan(begin, _at - begin), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private void Skip()
        {
            while (_at < data.Length && (char.IsWhiteSpace(data[_at]) || data[_at] == ','))
            {
                _at++;
            }
        }
    }
}
