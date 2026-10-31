// Frames from dr89mh/aion2-cube-radar (GPL-3.0) fixtures/cubes/*.json: real Hidden Cube lifecycles captured on
// Global build 25800038 (2026-10-13). "op" is the wire byte order (34 36), Soulcrest's opcode reads it little-endian.
using Soulcrest.Core.Network.HiddenCubes;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class HiddenCubeRadarTests
{
    private static readonly (long T, string Op, string Hex)[] TwoCubes =
    [
        (1791884810211L, "3436", "e5ff030400a74f1200001d44c780a5f647006471460000000000000000010101aae4a534a101000000000000000000002c4d4502070000"),
        (1791884822412L, "3436", "868d020400a74f1200a0b447c7b26c034800de964600000000002490430101014ab6a734a101000000000000000000002c4d4502070000"),
        (1791884833267L, "3836", "e5ff03dc4701"),
        (1791884837514L, "3a36", "e5ff03dc470100000023c59a25"),
        (1791884838212L, "3536", "e5ff03040300"),
        (1791884863915L, "3836", "868d02dc4701"),
        (1791884868164L, "3a36", "868d02dc470100000023c59a25"),
        (1791884868866L, "3536", "868d02040300"),
    ];

    private static readonly (long T, string Op, string Hex)[] Teleport =
    [
        (1791887249327L, "2136", "0b000000f20300003a03870000000000587274c71c5ea74700bc4e4680e333430200000000000000000000000000"),
        (1791887252725L, "2336", "000000000000587274c71c5ea74700bc4e4605000000a805"),
        (1791887770691L, "3436", "c3af030400a94f120023998a47d3e5c147036f09460000000000000000010106c080d534a10100000000000000000000508a10ef070000"),
        (1791887788894L, "3836", "c3af03dc4701"),
        (1791887793092L, "3a36", "c3af03dc470100000023c59a25"),
        (1791887793795L, "3536", "c3af03040300"),
    ];

    private static (DateTimeOffset At, ushort Opcode, byte[] Body) Frame((long T, string Op, string Hex) f)
    {
        var op = Convert.FromHexString(f.Op);
        return (DateTimeOffset.FromUnixTimeMilliseconds(f.T), (ushort)(op[0] | op[1] << 8), Convert.FromHexString(f.Hex));
    }

    private static void Feed(HiddenCubeRadar radar, IEnumerable<(long T, string Op, string Hex)> frames, Func<long, bool>? until = null)
    {
        foreach (var raw in frames)
        {
            if (until is not null && !until(raw.T)) break;
            var (at, opcode, body) = Frame(raw);
            radar.OnMessage(opcode, body, at);
        }
    }

    [Fact]
    public void Wire_order_maps_to_soulcrest_opcodes()
    {
        Assert.Equal(HiddenCubeMessages.Spawn, Frame(TwoCubes.First(f => f.Op == "3436")).Opcode);
        Assert.Equal(HiddenCubeMessages.OpenDone, Frame(TwoCubes.First(f => f.Op == "3a36")).Opcode);
        Assert.Equal(HiddenCubeMessages.MapLoad, Frame(Teleport.First(f => f.Op == "2136")).Opcode);
    }

    [Fact]
    public void Two_verteron_cubes_appear_with_an_alarm_and_end_opened()
    {
        var radar = new HiddenCubeRadar();
        var appeared = new List<HiddenCubeView>();
        radar.CubeAppeared += appeared.Add;
        radar.SetSelfActor(9180);
        Feed(radar, TwoCubes);

        Assert.Equal(new uint[] { 65509, 34438 }, appeared.Select(c => c.ObjectId).OrderByDescending(id => id));
        var first = appeared.Single(c => c.ObjectId == 65509);
        Assert.Equal(1200039u, first.Template);
        Assert.Equal(-50205.0, first.X, 1);
        Assert.Equal(126283.0, first.Y, 1);
        Assert.Equal(HiddenCubeStatus.Visible, first.Status);

        var last = DateTimeOffset.FromUnixTimeMilliseconds(TwoCubes[^1].T);
        var snapshot = radar.Snapshot(last);
        Assert.Equal(2, snapshot.Cubes.Count);
        Assert.All(snapshot.Cubes, c => Assert.Equal(HiddenCubeStatus.Opened, c.Status));
        Assert.All(snapshot.Cubes, c => Assert.True(c.OpenedBySelf));
        // Opened cubes are kept for a minute, then forgotten.
        Assert.Empty(radar.Snapshot(last.AddMinutes(2)).Cubes);
    }

    [Fact]
    public void Opening_is_reported_between_start_and_done()
    {
        var radar = new HiddenCubeRadar();
        var openStart = TwoCubes.First(f => f.Op == "3836").T;
        Feed(radar, TwoCubes, t => t <= openStart);
        var cube = radar.Snapshot(DateTimeOffset.FromUnixTimeMilliseconds(openStart)).Cubes.Single(c => c.Status == HiddenCubeStatus.Opening);
        Assert.Equal(65509u, cube.ObjectId);
    }

    [Fact]
    public void Map_load_and_teleport_give_the_own_position_and_template_1200041_is_a_cube()
    {
        var radar = new HiddenCubeRadar();
        var appeared = new List<HiddenCubeView>();
        radar.CubeAppeared += appeared.Add;
        var spawn = Teleport.First(f => f.Op == "3436").T;
        Feed(radar, Teleport, t => t <= spawn);

        var snapshot = radar.Snapshot(DateTimeOffset.FromUnixTimeMilliseconds(spawn));
        Assert.Equal(1010, snapshot.MapId);
        Assert.NotNull(snapshot.Own);
        var cube = Assert.Single(appeared);
        Assert.Equal(55235u, cube.ObjectId);
        Assert.Equal(1200041u, cube.Template);
        Assert.Equal(70962.0, cube.X, 0);
        Assert.Equal(99276.0, cube.Y, 0);
    }

    [Fact]
    public void A_template_opened_with_the_key_is_learned()
    {
        var radar = new HiddenCubeRadar(new HiddenCubeRadarOptions { Templates = new Dictionary<uint, HiddenCubeTemplate>() });
        var learned = new List<HiddenCubeTemplate>();
        radar.TemplateLearned += learned.Add;
        Feed(radar, Teleport);
        Assert.Equal(1200041u, Assert.Single(learned).Template);
        Assert.Contains(radar.Templates, t => t.Template == 1200041 && t.Status == "key-item");
    }

    [Fact]
    public void A_spawn_on_a_known_spot_teaches_its_template()
    {
        var radar = new HiddenCubeRadar(new HiddenCubeRadarOptions { Templates = new Dictionary<uint, HiddenCubeTemplate>() });
        radar.SetSpots([new HiddenCubeSpot(70962.4, 99275.6)]);
        var appeared = new List<HiddenCubeView>();
        radar.CubeAppeared += appeared.Add;
        Feed(radar, Teleport, t => t <= Teleport.First(f => f.Op == "3436").T);
        Assert.True(Assert.Single(appeared).Learned);
    }

    [Fact]
    public void An_out_of_view_cube_that_is_not_sent_again_nearby_is_gone()
    {
        var radar = new HiddenCubeRadar();
        var spawnFrame = Frame(Teleport.First(f => f.Op == "3436"));
        radar.OnMessage(spawnFrame.Opcode, spawnFrame.Body, spawnFrame.At);
        // Left the view range (35 36 reason 01).
        radar.OnMessage(HiddenCubeMessages.Removed, [0xC3, 0xAF, 0x03, 0x01], spawnFrame.At.AddSeconds(5));
        Assert.Equal(HiddenCubeStatus.OutOfView, radar.Snapshot(spawnFrame.At.AddSeconds(5)).Cubes.Single().Status);
        // We fly back next to it; the server does not send it again within the grace period.
        radar.SetOwnPosition(70962, 99276 + 2_000, 8796, spawnFrame.At.AddSeconds(60));
        Assert.Equal(HiddenCubeStatus.OutOfView, radar.Snapshot(spawnFrame.At.AddSeconds(61)).Cubes.Single().Status);
        Assert.Equal(HiddenCubeStatus.Gone, radar.Snapshot(spawnFrame.At.AddSeconds(64)).Cubes.Single().Status);
    }

    [Fact]
    public void Unrelated_records_and_garbage_are_ignored()
    {
        var radar = new HiddenCubeRadar();
        var changed = 0;
        radar.Changed += () => changed++;
        radar.OnMessage(0x900D, [1, 2, 3], DateTimeOffset.UtcNow);
        radar.OnMessage(HiddenCubeMessages.Spawn, [0xFF, 0xFF], DateTimeOffset.UtcNow);
        radar.OnMessage(HiddenCubeMessages.Removed, [], DateTimeOffset.UtcNow);
        Assert.Equal(0, changed);
        Assert.Empty(radar.Snapshot(DateTimeOffset.UtcNow).Cubes);
    }
}
