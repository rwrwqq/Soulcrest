// Ported from dr89mh/aion2-cube-radar (GPL-3.0): src/CubeRadar.Core/Radar/RadarEngine.cs (cube part).
// Changes: only Hidden Cubes (no player radar, ring fix, SQLite store or web UI); the own position comes
// from map load / teleport records and from Soulcrest's minimap tracking; spot learning uses Soulcrest's
// own Hidden Cube markers; events are raised outside the lock.
namespace Soulcrest.Core.Network.HiddenCubes;

public enum HiddenCubeStatus
{
    /// <summary>In view range.</summary>
    Visible,
    /// <summary>Left the view range (35 36 reason 01): most likely still standing there.</summary>
    OutOfView,
    /// <summary>A player started opening it (38 36).</summary>
    Opening,
    /// <summary>Opened (3a 36) or destroyed after opening (35 36 reason 04).</summary>
    Opened,
    /// <summary>Removed without us seeing who opened it, or not sent again although we came close.</summary>
    Gone,
}

/// <summary>A cube template: configured (verified/hypothesis) or learned in operation ("key-item", "spot").</summary>
public sealed record HiddenCubeTemplate(uint Template, string Zone, string Status);

/// <summary>A known Hidden Cube spawn point in native game units (from the map data package).</summary>
public readonly record struct HiddenCubeSpot(double X, double Y);

/// <summary>Immutable view of one cube for the overlay and the UI. Coordinates are native game units.</summary>
public sealed record HiddenCubeView(uint ObjectId, uint Template, string? Zone, bool Learned, double X, double Y, double Z,
    HiddenCubeStatus Status, DateTimeOffset StatusSince, DateTimeOffset FirstSeen, bool OpenedBySelf, int? MapId);

public sealed record HiddenCubeRadarSnapshot(int? MapId, IReadOnlyList<HiddenCubeView> Cubes, (double X, double Y, double Z)? Own,
    DateTimeOffset? OwnAt, long Records);

public sealed class HiddenCubeRadarOptions
{
    public IReadOnlyDictionary<uint, HiddenCubeTemplate> Templates { get; init; } = new Dictionary<uint, HiddenCubeTemplate>
    {
        [1200039] = new(1200039, "Verteron", "verified"),
        [1200041] = new(1200041, "Verteron", "verified"),
        [1301199] = new(1301199, "Altgard", "hypothesis"),
    };
    /// <summary>Item of the 3a 36 tail when a cube is opened with a key (presumably the consumed Hidden Cube Key).</summary>
    public uint OpenDoneItem { get; init; } = 0x259AC523;
    public TimeSpan KeepOutOfView { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan KeepGone { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>A spawn this close to a known cube spot teaches its template (session only).</summary>
    public double SpotMatchUnits { get; init; } = 50;
    /// <summary>Coming this close to an out-of-view cube requires the server to send it again.</summary>
    public double GoneCheckUnits { get; init; } = 8_000;
    public TimeSpan GoneCheckGrace { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>An own position older than this is not used for the gone check.</summary>
    public TimeSpan OwnFresh { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Live Hidden Cube state from the passively read server → client records (Cube Radar, GPL-3.0). Thread-safe:
/// records come from the capture thread, snapshots are read by the overlay. Read-only: nothing is sent.
/// </summary>
public sealed class HiddenCubeRadar
{
    private readonly object _gate = new();
    private readonly HiddenCubeRadarOptions _options;
    private readonly Dictionary<uint, HiddenCubeTemplate> _templates;
    private readonly Dictionary<uint, ObjectInfo> _objects = [];
    private readonly Dictionary<uint, Cube> _cubes = [];
    private readonly Dictionary<uint, DateTimeOffset> _goneDeadline = [];
    private IReadOnlyList<HiddenCubeSpot> _spots = [];
    private (double X, double Y, double Z)? _own;
    private DateTimeOffset? _ownAt;
    private uint? _selfActor;
    private int? _mapId;
    private long _records;

    private sealed class ObjectInfo(uint template, float x, float y, float z, DateTimeOffset at, long createdMs)
    {
        public uint Template { get; } = template;
        public float X { get; } = x;
        public float Y { get; } = y;
        public float Z { get; } = z;
        public DateTimeOffset At { get; } = at;
        public long CreatedMs { get; } = createdMs;
        public byte Removed { get; set; }
    }

    private sealed class Cube
    {
        public required uint ObjectId { get; init; }
        public required uint Template { get; init; }
        public string? Zone { get; init; }
        public bool Learned { get; init; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public DateTimeOffset FirstSeen { get; init; }
        public HiddenCubeStatus Status { get; set; }
        public DateTimeOffset StatusSince { get; set; }
        public bool OpenedBySelf { get; set; }
        public int? MapId { get; init; }

        public HiddenCubeView View() => new(ObjectId, Template, Zone, Learned, X, Y, Z, Status, StatusSince, FirstSeen, OpenedBySelf, MapId);
    }

    public HiddenCubeRadar(HiddenCubeRadarOptions? options = null, IEnumerable<HiddenCubeTemplate>? learned = null)
    {
        _options = options ?? new HiddenCubeRadarOptions();
        _templates = new Dictionary<uint, HiddenCubeTemplate>(_options.Templates);
        foreach (var template in learned ?? []) _templates.TryAdd(template.Template, template);
    }

    /// <summary>A new cube was seen (raised outside the lock).</summary>
    public event Action<HiddenCubeView>? CubeAppeared;
    /// <summary>Any cube changed, appeared or disappeared (raised outside the lock).</summary>
    public event Action? Changed;
    /// <summary>A template was confirmed by opening a cube with the key; worth saving.</summary>
    public event Action<HiddenCubeTemplate>? TemplateLearned;

    public IReadOnlyCollection<HiddenCubeTemplate> Templates
    {
        get { lock (_gate) return _templates.Values.ToList(); }
    }

    /// <summary>Known spawn points of the current map (native units), used to learn templates of other zones.</summary>
    public void SetSpots(IReadOnlyList<HiddenCubeSpot> spots)
    {
        lock (_gate) _spots = spots;
    }

    /// <summary>Own actor id, when Soulcrest's own-character recognition knows it (optional).</summary>
    public void SetSelfActor(uint? actor)
    {
        lock (_gate) _selfActor = actor;
    }

    /// <summary>Own position from the minimap tracking, converted to native units.</summary>
    public void SetOwnPosition(double x, double y, double? z, DateTimeOffset at)
    {
        var notify = new Notifications();
        lock (_gate)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) return;
            AdoptOwn(x, y, z ?? _own?.Z ?? 0, at);
            CheckGone(at, notify);
        }
        notify.Raise(this);
    }

    /// <summary>Feeds one message body (after the two opcode bytes). Unknown opcodes are ignored.</summary>
    public void OnMessage(ushort opcode, ReadOnlySpan<byte> body, DateTimeOffset at)
    {
        if (!HiddenCubeMessages.IsRelevant(opcode)) return;
        var notify = new Notifications();
        lock (_gate)
        {
            _records++;
            switch (opcode)
            {
                case HiddenCubeMessages.MapLoad:
                    if (HiddenCubeMessages.TryParseMapId(body) is { } mapId) OnMap(mapId, notify);
                    if (HiddenCubeMessages.TryParseOwnPosition(opcode, body, out var loaded)) AdoptOwn(loaded.X, loaded.Y, loaded.Z, at);
                    break;
                case HiddenCubeMessages.Teleport:
                    if (HiddenCubeMessages.TryParseOwnPosition(opcode, body, out var teleported)) AdoptOwn(teleported.X, teleported.Y, teleported.Z, at);
                    break;
                case HiddenCubeMessages.Spawn:
                    if (HiddenCubeMessages.TryParseSpawn(body, out var spawn)) OnSpawn(spawn, at, notify);
                    break;
                case HiddenCubeMessages.Removed:
                    if (HiddenCubeMessages.TryParseRemoved(body, out var removed)) OnRemoved(removed, at, notify);
                    break;
                case HiddenCubeMessages.OpenStart:
                case HiddenCubeMessages.OpenDone:
                    if (HiddenCubeMessages.TryParseInteraction(body, opcode == HiddenCubeMessages.OpenDone, out var interaction))
                        OnInteraction(interaction, at, notify);
                    break;
            }
            CheckGone(at, notify);
        }
        notify.Raise(this);
    }

    /// <summary>Forgets every cube (network gap, character change). Learned templates stay.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _cubes.Clear();
            _objects.Clear();
            _goneDeadline.Clear();
            _own = null;
            _ownAt = null;
            _mapId = null;
        }
        Changed?.Invoke();
    }

    public HiddenCubeRadarSnapshot Snapshot(DateTimeOffset now)
    {
        var notify = new Notifications();
        HiddenCubeRadarSnapshot snapshot;
        lock (_gate)
        {
            CheckGone(now, notify);
            foreach (var cube in _cubes.Values.ToList())
            {
                var age = now - cube.StatusSince;
                if ((cube.Status is HiddenCubeStatus.Opened or HiddenCubeStatus.Gone && age > _options.KeepGone)
                    || (cube.Status == HiddenCubeStatus.OutOfView && age > _options.KeepOutOfView))
                    _cubes.Remove(cube.ObjectId);
            }
            snapshot = new HiddenCubeRadarSnapshot(_mapId,
                _cubes.Values.OrderBy(c => c.Status).ThenByDescending(c => c.FirstSeen).Select(c => c.View()).ToList(),
                _own, _ownAt, _records);
        }
        notify.Raise(this);
        return snapshot;
    }

    // ------------------------------------------------------------------ records

    private void AdoptOwn(double x, double y, double z, DateTimeOffset at)
    {
        if (_ownAt is { } last && at < last) return;
        _own = (x, y, z);
        _ownAt = at;
        foreach (var cube in _cubes.Values)
            if (cube.Status == HiddenCubeStatus.OutOfView && !_goneDeadline.ContainsKey(cube.ObjectId)
                && Planar(x, y, cube.X, cube.Y) < _options.GoneCheckUnits)
                _goneDeadline[cube.ObjectId] = at + _options.GoneCheckGrace; // must be sent again, else someone opened it
    }

    private void OnSpawn(HiddenCubeObjectSpawn spawn, DateTimeOffset at, Notifications notify)
    {
        _objects[spawn.Id] = new ObjectInfo(spawn.Template, spawn.X, spawn.Y, spawn.Z, at, spawn.CreatedMs);
        if (_objects.Count > 20_000)
            foreach (var old in _objects.Where(kv => at - kv.Value.At > TimeSpan.FromMinutes(30)).Select(kv => kv.Key).ToList())
                _objects.Remove(old);
        var template = _templates.GetValueOrDefault(spawn.Template) ?? LearnBySpot(spawn, notify);
        if (template is not null) UpsertCube(spawn.Id, spawn.Template, spawn.X, spawn.Y, spawn.Z, at, template, notify);
    }

    private void UpsertCube(uint id, uint template, double x, double y, double z, DateTimeOffset at, HiddenCubeTemplate definition,
        Notifications notify, HiddenCubeStatus status = HiddenCubeStatus.Visible)
    {
        if (_cubes.TryGetValue(id, out var known) && known.Status is not (HiddenCubeStatus.Opened or HiddenCubeStatus.Gone))
        {
            known.X = x;
            known.Y = y;
            known.Z = z;
            _goneDeadline.Remove(id);
            if (known.Status == HiddenCubeStatus.OutOfView) SetStatus(known, HiddenCubeStatus.Visible, at, notify);
            return;
        }
        var cube = new Cube
        {
            ObjectId = id, Template = template, Zone = definition.Zone,
            Learned = definition.Status is "key-item" or "spot",
            X = x, Y = y, Z = z, FirstSeen = at, Status = status, StatusSince = at, MapId = _mapId,
        };
        _cubes[id] = cube;
        notify.Changed = true;
        if (status == HiddenCubeStatus.Visible) notify.Appeared.Add(cube.View());
    }

    private void OnRemoved(HiddenCubeObjectRemoved removed, DateTimeOffset at, Notifications notify)
    {
        if (_objects.TryGetValue(removed.Id, out var info)) info.Removed = removed.Reason;
        if (!_cubes.TryGetValue(removed.Id, out var cube)) return;
        if (removed.Reason == HiddenCubeMessages.ReasonLeftView)
        {
            if (cube.Status is HiddenCubeStatus.Visible or HiddenCubeStatus.Opening) SetStatus(cube, HiddenCubeStatus.OutOfView, at, notify);
        }
        else if (cube.Status != HiddenCubeStatus.Opened)
            SetStatus(cube, HiddenCubeStatus.Gone, at, notify);
    }

    private void OnInteraction(HiddenCubeInteraction interaction, DateTimeOffset at, Notifications notify)
    {
        if (interaction.Done && interaction.Count == 1 && interaction.Item == _options.OpenDoneItem
            && _objects.TryGetValue(interaction.ObjectId, out var info) && !_templates.ContainsKey(info.Template))
        {
            // Opened with the cube key: this template is a cube (e.g. another zone's).
            var learned = new HiddenCubeTemplate(info.Template, "?", "key-item");
            Learn(learned, notify);
            notify.Learned.Add(learned);
        }
        if (!_cubes.TryGetValue(interaction.ObjectId, out var cube)) return;
        cube.OpenedBySelf = _selfActor is { } self && interaction.ActorId == self;
        SetStatus(cube, interaction.Done ? HiddenCubeStatus.Opened : HiddenCubeStatus.Opening, at, notify);
    }

    private HiddenCubeTemplate? LearnBySpot(HiddenCubeObjectSpawn spawn, Notifications notify)
    {
        if (_spots.Count == 0) return null;
        var limit = _options.SpotMatchUnits;
        if (!_spots.Any(spot => Planar(spot.X, spot.Y, spawn.X, spawn.Y) <= limit)) return null;
        var learned = new HiddenCubeTemplate(spawn.Template, "?", "spot");
        Learn(learned, notify, except: spawn.Id);
        return learned;
    }

    /// <summary>Takes a template as cube template and turns the objects already seen with it into cubes.</summary>
    private void Learn(HiddenCubeTemplate template, Notifications notify, uint? except = null)
    {
        _templates[template.Template] = template;
        foreach (var (id, info) in _objects.ToList())
        {
            if (info.Template != template.Template || id == except || _cubes.ContainsKey(id) || info.Removed is not (0 or 1)) continue;
            UpsertCube(id, info.Template, info.X, info.Y, info.Z, info.At, template, notify,
                info.Removed == HiddenCubeMessages.ReasonLeftView ? HiddenCubeStatus.OutOfView : HiddenCubeStatus.Visible);
        }
    }

    private void CheckGone(DateTimeOffset now, Notifications notify)
    {
        if (_goneDeadline.Count == 0) return;
        foreach (var (id, deadline) in _goneDeadline.ToList())
        {
            if (now < deadline) continue;
            _goneDeadline.Remove(id);
            if (_cubes.TryGetValue(id, out var cube) && cube.Status == HiddenCubeStatus.OutOfView)
                SetStatus(cube, HiddenCubeStatus.Gone, now, notify);
        }
    }

    private void OnMap(int mapId, Notifications notify)
    {
        if (_mapId == mapId) return; // same map again = in-map teleport: cubes stay where they are
        var first = _mapId is null;
        _mapId = mapId;
        // First load since the start: it may just be a teleport inside the zone we were already in.
        if (first) return;
        _cubes.Clear();
        _objects.Clear();
        _goneDeadline.Clear();
        notify.Changed = true;
    }

    private static void SetStatus(Cube cube, HiddenCubeStatus status, DateTimeOffset at, Notifications notify)
    {
        if (cube.Status == status) return;
        cube.Status = status;
        cube.StatusSince = at;
        notify.Changed = true;
    }

    private static double Planar(double x0, double y0, double x1, double y1) =>
        Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

    private sealed class Notifications
    {
        public bool Changed { get; set; }
        public List<HiddenCubeView> Appeared { get; } = [];
        public List<HiddenCubeTemplate> Learned { get; } = [];

        public void Raise(HiddenCubeRadar radar)
        {
            foreach (var template in Learned) radar.TemplateLearned?.Invoke(template);
            foreach (var cube in Appeared) radar.CubeAppeared?.Invoke(cube);
            if (Changed || Appeared.Count > 0) radar.Changed?.Invoke();
        }
    }
}
