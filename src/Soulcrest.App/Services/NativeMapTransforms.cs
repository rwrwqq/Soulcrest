using System.Text.Json;

namespace Soulcrest.App.Services;

/// <summary>
/// Linear mapping between native game units (centimetres, as the network records carry them) and the map
/// coordinates of the data package. The package is built with px = offset + scale · native per axis
/// (scripts/build_tester_data.py, to_pixel × logical scale), so it is fitted exactly from the resource
/// placements that carry both (resource-nodes.json). Nothing is guessed: a map without enough placements
/// or with a residual above one pixel has no transform.
/// </summary>
public sealed record NativeMapTransform(string MapId, int? SourceMapId, double ScaleX, double OffsetX, double ScaleY, double OffsetY)
{
    public (double X, double Y) ToMap(double nativeX, double nativeY) => (OffsetX + ScaleX * nativeX, OffsetY + ScaleY * nativeY);
    public (double X, double Y) ToNative(double mapX, double mapY) => ((mapX - OffsetX) / ScaleX, (mapY - OffsetY) / ScaleY);
    /// <summary>Native units per map unit (average of both axes).</summary>
    public double NativePerMapUnit => 2 / (Math.Abs(ScaleX) + Math.Abs(ScaleY));
}

public sealed class NativeMapTransforms
{
    private const double MaxResidualPixels = 1.0;
    private readonly Dictionary<string, NativeMapTransform> _byMap;
    public static NativeMapTransforms Empty { get; } = new([]);

    public NativeMapTransforms(IEnumerable<NativeMapTransform> transforms) =>
        _byMap = transforms.ToDictionary(t => t.MapId, StringComparer.Ordinal);

    public NativeMapTransform? For(string? mapId) => mapId is null ? null : _byMap.GetValueOrDefault(mapId);

    /// <summary>The data package map that belongs to a game map id (manifest sourceMapId), if any.</summary>
    public NativeMapTransform? ForSource(int sourceMapId) =>
        _byMap.Values.FirstOrDefault(t => t.SourceMapId == sourceMapId);

    public static NativeMapTransforms Load(string? mapDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(mapDataDirectory)) return Empty;
        try
        {
            var sources = ReadSourceMapIds(Path.Combine(mapDataDirectory, "manifest.json"));
            var catalog = ResourceNodeCatalog.Load(mapDataDirectory);
            var transforms = new List<NativeMapTransform>();
            foreach (var group in catalog.Nodes.GroupBy(n => n.MapId, StringComparer.Ordinal))
            {
                int? source = sources.TryGetValue(group.Key, out var sourceId) ? sourceId : null;
                if (Fit(group.Key, source, group.ToList()) is { } transform) transforms.Add(transform);
            }
            return new(transforms);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or ArgumentException)
        {
            return Empty;
        }
    }

    internal static NativeMapTransform? Fit(string mapId, int? sourceMapId, IReadOnlyList<ResourceNodeDefinition> nodes)
    {
        if (nodes.Count < 2) return null;
        if (FitAxis(nodes.Select(n => (n.NativeX, n.X)).ToList()) is not { } x
            || FitAxis(nodes.Select(n => (n.NativeY, n.Y)).ToList()) is not { } y) return null;
        return new NativeMapTransform(mapId, sourceMapId, x.Scale, x.Offset, y.Scale, y.Offset);
    }

    private static (double Scale, double Offset)? FitAxis(IReadOnlyList<(double Native, double Map)> points)
    {
        var n = points.Count;
        var meanNative = points.Average(p => p.Native);
        var meanMap = points.Average(p => p.Map);
        double covariance = 0, variance = 0;
        foreach (var (native, map) in points)
        {
            covariance += (native - meanNative) * (map - meanMap);
            variance += (native - meanNative) * (native - meanNative);
        }
        if (n < 2 || variance <= 0 || !double.IsFinite(variance)) return null;
        var scale = covariance / variance;
        var offset = meanMap - scale * meanNative;
        if (!double.IsFinite(scale) || !double.IsFinite(offset) || scale == 0) return null;
        var residual = points.Max(p => Math.Abs(offset + scale * p.Native - p.Map));
        return residual <= MaxResidualPixels ? (scale, offset) : null;
    }

    private static Dictionary<string, int> ReadSourceMapIds(string manifestPath)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(manifestPath)) return result;
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("maps", out var maps) || maps.ValueKind != JsonValueKind.Array) return result;
        foreach (var map in maps.EnumerateArray())
        {
            if (map.ValueKind != JsonValueKind.Object || !map.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || id.GetString() is not { Length: > 0 } mapId || !map.TryGetProperty("sourceMapId", out var source)) continue;
            var parsed = source.ValueKind switch
            {
                JsonValueKind.String when int.TryParse(source.GetString(), out var value) => value,
                JsonValueKind.Number when source.TryGetInt32(out var value) => value,
                _ => (int?)null,
            };
            if (parsed is { } sourceId) result[mapId] = sourceId;
        }
        return result;
    }
}
