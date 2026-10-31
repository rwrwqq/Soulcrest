using System.Drawing;
using System.Drawing.Drawing2D;
using Soulcrest.App.Services;
using Soulcrest.Core.Network.HiddenCubes;

namespace Soulcrest.App.Overlay;

public sealed partial class RouteOverlayForm
{
    private const float LiveCubeSize = 9f;

    /// <summary>
    /// Live Hidden Cubes from the network radar (port of Cube Radar, GPL-3.0) on the same minimap / map overlay
    /// as the static cube spots: a glowing red cube with distance and height (▲ above, ▼ below). A cube outside
    /// the visible map area becomes an arrow at the rim pointing towards it.
    /// </summary>
    private static void DrawLiveCubes(Graphics graphics, MapPlacement placement, PointF origin, RectangleF area, PointF? player,
        IReadOnlyList<LiveCubeMarker> cubes, OverlayThemeDefinition theme)
    {
        if (cubes.Count == 0 || area.Width <= 20 || area.Height <= 20) return;
        using var font = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
        // Faded ones first, so live cubes stay on top.
        foreach (var cube in cubes.OrderByDescending(c => c.Status))
        {
            var at = Offset(placement.WorldToScreen(cube.X, cube.Y), origin);
            if (!float.IsFinite(at.X) || !float.IsFinite(at.Y)) continue;
            var color = LiveCubeColor(cube.Status);
            var label = LiveCubeLabel(cube);
            if (area.Contains(at))
            {
                DrawLiveCube(graphics, at, cube.Status, color);
                if (label is not null) DrawLiveCubeLabel(graphics, font, new PointF(at.X, at.Y + LiveCubeSize + 3), label, color, theme, area);
            }
            else if (player is { } from && cube.Status is HiddenCubeStatus.Visible or HiddenCubeStatus.OutOfView or HiddenCubeStatus.Opening
                && RimPoint(from, at, RectangleF.Inflate(area, -12, -12)) is { } rim)
            {
                DrawRimArrow(graphics, rim, MathF.Atan2(at.Y - from.Y, at.X - from.X), color);
                if (label is not null) DrawLiveCubeLabel(graphics, font, PointF.Add(rim, new SizeF(0, 11)), label, color, theme, area);
            }
        }
    }

    internal static Color LiveCubeColor(HiddenCubeStatus status) => status switch
    {
        HiddenCubeStatus.Visible => Color.FromArgb(255, 239, 68, 68),
        HiddenCubeStatus.OutOfView => Color.FromArgb(230, 251, 146, 60),
        HiddenCubeStatus.Opening => Color.FromArgb(255, 250, 204, 21),
        _ => Color.FromArgb(150, 148, 163, 184),
    };

    /// <summary>"42 m ▲3", "wird geöffnet", "geöffnet" or "weg"; null when nothing is known yet.</summary>
    internal static string? LiveCubeLabel(LiveCubeMarker cube)
    {
        var state = cube.Status switch
        {
            HiddenCubeStatus.Opening => UiText.T("wird geöffnet"),
            HiddenCubeStatus.Opened => UiText.T("geöffnet"),
            HiddenCubeStatus.Gone => UiText.T("weg"),
            _ => null,
        };
        if (state is not null) return state;
        if (cube.DistanceMeters is not { } distance) return null;
        var text = DistanceText(distance);
        if (cube.HeightMeters is { } height && Math.Abs(height) >= 3)
            text += height > 0 ? $" ▲{Math.Round(height):0}" : $" ▼{Math.Round(-height):0}";
        return text;
    }

    private static void DrawLiveCube(Graphics graphics, PointF at, HiddenCubeStatus status, Color color)
    {
        var live = status is HiddenCubeStatus.Visible or HiddenCubeStatus.Opening;
        if (live)
        {
            using var glow = new GraphicsPath();
            glow.AddEllipse(at.X - LiveCubeSize * 2.2f, at.Y - LiveCubeSize * 2.2f, LiveCubeSize * 4.4f, LiveCubeSize * 4.4f);
            using var brush = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(150, color), SurroundColors = [Color.FromArgb(0, color)] };
            graphics.FillPath(brush, glow);
        }
        // An isometric cube: top, left and right faces.
        var s = LiveCubeSize;
        var top = new[] { new PointF(at.X, at.Y - s), new PointF(at.X + s, at.Y - s / 2), new PointF(at.X, at.Y), new PointF(at.X - s, at.Y - s / 2) };
        var left = new[] { new PointF(at.X - s, at.Y - s / 2), new PointF(at.X, at.Y), new PointF(at.X, at.Y + s), new PointF(at.X - s, at.Y + s / 2) };
        var right = new[] { new PointF(at.X + s, at.Y - s / 2), new PointF(at.X, at.Y), new PointF(at.X, at.Y + s), new PointF(at.X + s, at.Y + s / 2) };
        using var topBrush = new SolidBrush(Shade(color, 1.25f));
        using var leftBrush = new SolidBrush(Shade(color, 0.85f));
        using var rightBrush = new SolidBrush(Shade(color, 0.6f));
        using var outline = new Pen(Color.FromArgb(Math.Min(255, color.A), 10, 10, 14), 1.4f) { LineJoin = LineJoin.Round };
        if (status == HiddenCubeStatus.OutOfView)
        {
            // Not in view right now: only the outline, so it reads as "probably still there".
            using var dashed = new Pen(color, 1.8f) { DashStyle = DashStyle.Dash, LineJoin = LineJoin.Round };
            graphics.DrawPolygon(dashed, top);
            graphics.DrawPolygon(dashed, left);
            graphics.DrawPolygon(dashed, right);
            return;
        }
        graphics.FillPolygon(topBrush, top);
        graphics.FillPolygon(leftBrush, left);
        graphics.FillPolygon(rightBrush, right);
        graphics.DrawPolygon(outline, top);
        graphics.DrawPolygon(outline, left);
        graphics.DrawPolygon(outline, right);
        if (status is HiddenCubeStatus.Opened or HiddenCubeStatus.Gone)
        {
            using var cross = new Pen(Color.FromArgb(220, 15, 15, 20), 2f);
            graphics.DrawLine(cross, at.X - s * 0.7f, at.Y - s * 0.7f, at.X + s * 0.7f, at.Y + s * 0.7f);
            graphics.DrawLine(cross, at.X + s * 0.7f, at.Y - s * 0.7f, at.X - s * 0.7f, at.Y + s * 0.7f);
        }
    }

    private static void DrawRimArrow(Graphics graphics, PointF at, float angle, Color color)
    {
        var (sin, cos) = MathF.SinCos(angle);
        PointF Rotate(float x, float y) => new(at.X + x * cos - y * sin, at.Y + x * sin + y * cos);
        var arrow = new[] { Rotate(9, 0), Rotate(-6, -7), Rotate(-3, 0), Rotate(-6, 7) };
        using var fill = new SolidBrush(color);
        using var outline = new Pen(Color.FromArgb(220, 10, 10, 14), 1.4f) { LineJoin = LineJoin.Round };
        graphics.FillPolygon(fill, arrow);
        graphics.DrawPolygon(outline, arrow);
    }

    private static void DrawLiveCubeLabel(Graphics graphics, Font font, PointF centreTop, string text, Color color, OverlayThemeDefinition theme,
        RectangleF area)
    {
        var size = graphics.MeasureString(text, font);
        var box = new RectangleF(centreTop.X - size.Width / 2 - 3, centreTop.Y, size.Width + 6, size.Height);
        // Keep the label inside the map area.
        box.X = Math.Clamp(box.X, area.Left, Math.Max(area.Left, area.Right - box.Width));
        box.Y = Math.Clamp(box.Y, area.Top, Math.Max(area.Top, area.Bottom - box.Height));
        using var background = new SolidBrush(theme.IsLegacy ? Color.FromArgb(200, 8, 12, 20) : Color.FromArgb(200, theme.Surface));
        using var path = RoundedBox(box, 4);
        graphics.FillPath(background, path);
        using var textBrush = new SolidBrush(Color.FromArgb(255, Shade(color, 1.35f)));
        graphics.DrawString(text, font, textBrush, box.X + 3, box.Y);
    }

    private static GraphicsPath RoundedBox(RectangleF box, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(box.Left, box.Top, d, d, 180, 90);
        path.AddArc(box.Right - d, box.Top, d, d, 270, 90);
        path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
        path.AddArc(box.Left, box.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Where the ray from <paramref name="from"/> towards <paramref name="to"/> leaves the rectangle.</summary>
    internal static PointF? RimPoint(PointF from, PointF to, RectangleF rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || !rect.Contains(from)) return null;
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        if (dx == 0 && dy == 0) return null;
        var t = float.PositiveInfinity;
        if (dx > 0) t = Math.Min(t, (rect.Right - from.X) / dx);
        if (dx < 0) t = Math.Min(t, (rect.Left - from.X) / dx);
        if (dy > 0) t = Math.Min(t, (rect.Bottom - from.Y) / dy);
        if (dy < 0) t = Math.Min(t, (rect.Top - from.Y) / dy);
        if (!float.IsFinite(t) || t < 0) return null;
        return new PointF(from.X + dx * t, from.Y + dy * t);
    }

    private static Color Shade(Color color, float factor) => Color.FromArgb(color.A,
        (int)Math.Clamp(color.R * factor, 0, 255), (int)Math.Clamp(color.G * factor, 0, 255), (int)Math.Clamp(color.B * factor, 0, 255));
}
