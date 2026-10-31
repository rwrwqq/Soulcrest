using System.Drawing;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Network.HiddenCubes;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LiveHiddenCubeTests
{
    // Same formula as scripts/build_tester_data.py: px = ((w - dx·s)/2 + (x - minX)·s) · logical.
    private static (double X, double Y) ToPixel(double x, double y)
    {
        const double minX = -200_000, minY = -150_000, scale = 0.04, logical = 0.5;
        return ((10 + (x - minX) * scale) * logical, (25 + (y - minY) * scale) * logical);
    }

    private static ResourceNodeDefinition Node(int index, double nativeX, double nativeY)
    {
        var (x, y) = ToPixel(nativeX, nativeY);
        return new ResourceNodeDefinition("resource:" + index, "verteron", 1, "resource-ore", "Ore", null,
            Math.Round(x, 3), Math.Round(y, 3), nativeX, nativeY);
    }

    [Fact]
    public void The_native_transform_is_fitted_exactly_from_resource_placements()
    {
        var nodes = new[] { Node(1, -50_000, 120_000), Node(2, 70_962, 99_276), Node(3, 10_000, -20_000) };
        var transform = Assert.IsType<NativeMapTransform>(NativeMapTransforms.Fit("verteron", 1010, nodes));
        var (x, y) = transform.ToMap(-50_205, 126_283);
        var expected = ToPixel(-50_205, 126_283);
        Assert.Equal(expected.X, x, 2);
        Assert.Equal(expected.Y, y, 2);
        var back = transform.ToNative(x, y);
        Assert.Equal(-50_205.0, back.X, 0);
        Assert.Equal(126_283.0, back.Y, 0);
        Assert.Equal(1010, transform.SourceMapId);
    }

    [Fact]
    public void A_map_without_a_consistent_fit_gets_no_transform()
    {
        var nodes = new[] { Node(1, -50_000, 120_000), Node(2, 70_962, 99_276) with { X = 5 } , Node(3, 10_000, -20_000) };
        Assert.Null(NativeMapTransforms.Fit("verteron", 1010, nodes));
        Assert.Null(NativeMapTransforms.Fit("verteron", 1010, [Node(1, 0, 0)]));
    }

    [Fact]
    public void Labels_show_distance_height_and_state()
    {
        var at = DateTimeOffset.UtcNow;
        Assert.Equal("42 m ▲12", RouteOverlayForm.LiveCubeLabel(new LiveCubeMarker(1, 0, 0, HiddenCubeStatus.Visible, 42.2, 12.4, at)));
        Assert.Equal("42 m", RouteOverlayForm.LiveCubeLabel(new LiveCubeMarker(1, 0, 0, HiddenCubeStatus.Visible, 42.2, 1, at)));
        Assert.Null(RouteOverlayForm.LiveCubeLabel(new LiveCubeMarker(1, 0, 0, HiddenCubeStatus.Visible, null, null, at)));
        Assert.NotNull(RouteOverlayForm.LiveCubeLabel(new LiveCubeMarker(1, 0, 0, HiddenCubeStatus.Gone, 5, null, at)));
    }

    [Fact]
    public void An_offscreen_cube_points_from_the_player_to_the_rim()
    {
        var rim = RouteOverlayForm.RimPoint(new PointF(50, 50), new PointF(250, 50), new RectangleF(0, 0, 100, 100));
        Assert.Equal(new PointF(100, 50), rim!.Value);
        Assert.Null(RouteOverlayForm.RimPoint(new PointF(150, 50), new PointF(250, 50), new RectangleF(0, 0, 100, 100)));
    }

    [Fact]
    public void The_alarm_beep_is_a_pcm_wav_scaled_by_volume()
    {
        var loud = HiddenCubeSound.Beep(100);
        var quiet = HiddenCubeSound.Beep(10);
        Assert.Equal("RIFF"u8.ToArray(), loud[..4]);
        Assert.Equal(loud.Length, quiet.Length);
        Assert.True(loud.Skip(44).Select(b => (int)b).Sum() != quiet.Skip(44).Select(b => (int)b).Sum());
        Assert.Equal(0.0, HiddenCubeSound.Gain(0));
        Assert.Equal(1.0, HiddenCubeSound.Gain(100));
    }
}
