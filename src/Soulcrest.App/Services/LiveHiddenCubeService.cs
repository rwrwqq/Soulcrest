using Soulcrest.App.Network;
using Soulcrest.Core.Network.HiddenCubes;

namespace Soulcrest.App.Services;

/// <summary>A live cube in map coordinates of the data package, ready to draw.</summary>
public sealed record LiveCubeMarker(uint ObjectId, double X, double Y, HiddenCubeStatus Status, double? DistanceMeters,
    double? HeightMeters, DateTimeOffset FirstSeen);

/// <summary>Short state of the live radar for the settings page.</summary>
public sealed record LiveCubeRadarStatus(bool Enabled, bool NetworkRunning, int Visible, int OutOfView, int Templates,
    long Records, bool MapCalibrated);

/// <summary>
/// Live Hidden Cube radar inside Soulcrest (cube detection and alarm of dr89mh/aion2-cube-radar, GPL-3.0).
/// It observes the object records of Soulcrest's existing passive network source (no second capture), keeps
/// the cube states in <see cref="HiddenCubeRadar"/>, beeps for every new cube and hands the cubes in map
/// coordinates to the in-game map overlay. The own position comes from the network records and from the
/// minimap tracking. Nothing is sent to the game and nothing leaves the PC.
/// </summary>
public sealed class LiveHiddenCubeService : IDisposable
{
    private static readonly TimeSpan AlarmSpacing = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan OwnFresh = TimeSpan.FromSeconds(15);
    private readonly NetworkLootService _network;
    private readonly MapTrackingService _tracking;
    private readonly SettingsService _settings;
    private readonly ProgressService _progress;
    private readonly HiddenCubeRadar _radar;
    private readonly NativeMapTransforms _transforms;
    private readonly object _gate = new();
    private string? _spotsMap;
    private DateTimeOffset _lastOwnFed;
    private DateTimeOffset _lastAlarm;
    private bool _disposed;

    /// <summary>The running instance, for UI parts that are rendered without this service registered (tests).</summary>
    public static LiveHiddenCubeService? Current { get; private set; }

    public LiveHiddenCubeService(NetworkLootService network, MapTrackingService tracking, SettingsService settings, ProgressService progress)
    {
        _network = network;
        _tracking = tracking;
        _settings = settings;
        _progress = progress;
        _transforms = NativeMapTransforms.Load(progress.MapDataDirectory);
        _radar = new HiddenCubeRadar(learned: LoadLearned());
        _radar.CubeAppeared += Alarm;
        _radar.Changed += RaiseChanged;
        _radar.TemplateLearned += SaveLearned;
        _network.HiddenCubeRecord += OnRecord;
        _settings.Changed += SettingsChanged;
        Current = this;
    }

    public event Action? Changed;

    public static string LearnedTemplatesFile => Path.Combine(AppPaths.LearnedDirectory, "hidden-cube-templates.json");

    public bool Enabled => _settings.Current.LiveCubeRadarEnabled;

    public LiveCubeRadarStatus Status
    {
        get
        {
            var snapshot = _radar.Snapshot(DateTimeOffset.UtcNow);
            var map = _tracking.Position?.MapId;
            return new(Enabled, _network.Running,
                snapshot.Cubes.Count(c => c.Status is HiddenCubeStatus.Visible or HiddenCubeStatus.Opening),
                snapshot.Cubes.Count(c => c.Status == HiddenCubeStatus.OutOfView),
                _radar.Templates.Count, snapshot.Records, _transforms.For(map) is not null);
        }
    }

    /// <summary>Plays the alarm once at the given volume (settings preview).</summary>
    public static bool Preview(int volumePercent) => HiddenCubeSound.Play(volumePercent);

    /// <summary>Cubes on one data package map, in its map coordinates; empty when the radar is off.</summary>
    public IReadOnlyList<LiveCubeMarker> CubesFor(string mapId)
    {
        if (!Enabled || _transforms.For(mapId) is not { } transform) return [];
        var now = DateTimeOffset.UtcNow;
        FeedTracking(mapId, transform, now);
        var snapshot = _radar.Snapshot(now);
        // A cube of another game map must never land on this one (when both ids are known).
        if (snapshot.MapId is { } gameMap && transform.SourceMapId is { } source && gameMap != source) return [];
        var own = snapshot.Own is { } o && snapshot.OwnAt is { } at && now - at <= OwnFresh ? o : ((double X, double Y, double Z)?)null;
        var result = new List<LiveCubeMarker>(snapshot.Cubes.Count);
        foreach (var cube in snapshot.Cubes)
        {
            if (cube.MapId is { } cubeMap && transform.SourceMapId is { } cubeSource && cubeMap != cubeSource) continue;
            var (x, y) = transform.ToMap(cube.X, cube.Y);
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
            double? distance = null, height = null;
            if (own is { } self)
            {
                distance = Math.Sqrt((cube.X - self.X) * (cube.X - self.X) + (cube.Y - self.Y) * (cube.Y - self.Y)) / 100;
                if (self.Z != 0) height = (cube.Z - self.Z) / 100;
            }
            result.Add(new LiveCubeMarker(cube.ObjectId, x, y, cube.Status, distance, height, cube.FirstSeen));
        }
        return result;
    }

    private void OnRecord(ushort opcode, byte[] body, DateTimeOffset at)
    {
        if (_disposed || !Enabled) return;
        _radar.OnMessage(opcode, body, at);
    }

    /// <summary>Own position and known spots of the current minimap map, converted to native units.</summary>
    private void FeedTracking(string mapId, NativeMapTransform transform, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_spotsMap != mapId)
            {
                _spotsMap = mapId;
                _radar.SetSpots(SpotsFor(mapId, transform));
            }
            if (_tracking.Position is { } position && position.MapId == mapId && position.At > _lastOwnFed
                && now - position.At <= OwnFresh)
            {
                _lastOwnFed = position.At;
                var (x, y) = transform.ToNative(position.X, position.Y);
                _radar.SetOwnPosition(x, y, null, position.At);
            }
        }
    }

    private IReadOnlyList<HiddenCubeSpot> SpotsFor(string mapId, NativeMapTransform transform)
    {
        if (_progress.MapDataDirectory is not { } mapData) return [];
        var category = MapPetMarkers.CategoryKey("Collectibles", "Hidden Cube");
        try
        {
            return MapPetMarkers.SymbolsFor(mapData, mapId).Where(s => s.Category == category)
                .Select(s => transform.ToNative(s.X, s.Y)).Select(p => new HiddenCubeSpot(p.X, p.Y)).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    private void Alarm(HiddenCubeView cube)
    {
        var settings = _settings.Current;
        if (_disposed || !settings.LiveCubeRadarEnabled || !settings.LiveCubeAlarmEnabled || settings.LiveCubeAlarmVolume <= 0) return;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (now - _lastAlarm < AlarmSpacing) return; // several cubes in one bundle: one beep
            _lastAlarm = now;
        }
        var volume = settings.LiveCubeAlarmVolume;
        _ = Task.Run(() => HiddenCubeSound.Play(volume));
    }

    private void SettingsChanged()
    {
        if (!Enabled) _radar.Reset();
        RaiseChanged();
    }

    private void RaiseChanged() => SafeEvent.Raise(Changed, "Hidden Cube Radar");

    private static List<HiddenCubeTemplate> LoadLearned()
    {
        try { return JsonFile.Load<List<HiddenCubeTemplate>>(LearnedTemplatesFile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException) { return []; }
    }

    private void SaveLearned(HiddenCubeTemplate learned)
    {
        try
        {
            var templates = _radar.Templates.Where(t => t.Status == "key-item").ToList();
            JsonFile.Save(LearnedTemplatesFile, templates);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Hidden Cube template {0} could not be saved: {1}", learned.Template, error.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _network.HiddenCubeRecord -= OnRecord;
        _settings.Changed -= SettingsChanged;
        if (ReferenceEquals(Current, this)) Current = null;
        HiddenCubeSound.Close();
    }
}
