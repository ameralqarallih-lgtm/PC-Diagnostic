using System.Drawing;
using System.Drawing.Drawing2D;

namespace PcDiag.Agent.Tray;

/// <summary>The notification-area icon for each recorder state, drawn at the system's small-icon size.</summary>
internal enum TrayState
{
    Idle,
    Recording,
    Paused,
    CapturingIncident,
}

/// <summary>Creates tray icons. Each icon owns its handle and must be disposed.</summary>
internal static class TrayIcons
{
    public static Icon Create(TrayState state, Size size)
    {
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var inset = Math.Max(1, size.Width / 8);
            var circle = new Rectangle(inset, inset, size.Width - (2 * inset) - 1, size.Height - (2 * inset) - 1);
            switch (state)
            {
                case TrayState.Recording:
                    using (var fill = new SolidBrush(Color.FromArgb(220, 38, 38)))
                    {
                        g.FillEllipse(fill, circle);
                    }

                    break;
                case TrayState.CapturingIncident:
                    using (var fill = new SolidBrush(Color.FromArgb(220, 38, 38)))
                    using (var ring = new Pen(Color.White, Math.Max(1.5f, size.Width / 10f)))
                    {
                        g.FillEllipse(fill, circle);
                        var inner = Rectangle.Inflate(circle, -size.Width / 5, -size.Height / 5);
                        g.DrawEllipse(ring, inner);
                    }

                    break;
                case TrayState.Paused:
                    using (var fill = new SolidBrush(Color.FromArgb(217, 119, 6)))
                    using (var bars = new SolidBrush(Color.White))
                    {
                        g.FillEllipse(fill, circle);
                        var barWidth = Math.Max(1, size.Width / 8);
                        var barHeight = size.Height / 2;
                        var top = (size.Height - barHeight) / 2;
                        g.FillRectangle(bars, (size.Width / 2) - barWidth - (barWidth / 2), top, barWidth, barHeight);
                        g.FillRectangle(bars, (size.Width / 2) + (barWidth / 2), top, barWidth, barHeight);
                    }

                    break;
                default:
                    using (var outline = new Pen(Color.FromArgb(140, 140, 140), Math.Max(1.5f, size.Width / 8f)))
                    {
                        g.DrawEllipse(outline, Rectangle.Inflate(circle, -1, -1));
                    }

                    break;
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
