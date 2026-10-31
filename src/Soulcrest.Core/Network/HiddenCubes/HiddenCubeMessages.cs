// Ported from dr89mh/aion2-cube-radar (GPL-3.0): src/CubeRadar.Core/Protocol/ObjectMessages.cs and Wire.cs
// (Wire.cs itself derived from cyberbadger6969/aion2-dps-meter, GPL-3.0). Changes: namespace, opcodes in
// Soulcrest's little-endian ushort notation, only the records the live Hidden Cube radar needs.
using System.Buffers.Binary;

namespace Soulcrest.Core.Network.HiddenCubes;

/// <summary>World object (gadget) spawn, e.g. a Hidden Cube. Coordinates are native game units (centimetres).</summary>
public readonly record struct HiddenCubeObjectSpawn(uint Id, ushort Kind, uint Template, float X, float Y, float Z, long CreatedMs);

/// <summary>An object left the view range (reason 01) or was destroyed/opened (reason 04).</summary>
public readonly record struct HiddenCubeObjectRemoved(uint Id, byte Reason);

/// <summary>A player starts (<see cref="Done"/> = false) or finishes opening an object.</summary>
public readonly record struct HiddenCubeInteraction(uint ObjectId, uint ActorId, bool Done, uint Count, uint Item);

/// <summary>
/// Server → client records of the object lifecycle (verified by Cube Radar on Global build 25800038, 2026-10-13).
/// Wire bytes / Soulcrest opcode (first two message bytes read little-endian):
/// <code>
/// 34 36 = 0x3634  object spawn: id varint, kind u16, template u32, x y z f32, f32, heading f32, u8 u8 state u8, created u64 ms
/// 35 36 = 0x3635  object removed: id varint, reason u8 (04 = destroyed / opened, 01 = left view)
/// 38 36 = 0x3638  open start: object varint, actor varint, 01
/// 3a 36 = 0x363A  open done: object varint, actor varint, count u32, item u32
/// 21 36 = 0x3621  map load: …, map id u32 at 4, own x y z f32 at 16
/// 23 36 = 0x3623  teleport: own x y z f32 at 6
/// </code>
/// All parsers take the message body after the two opcode bytes.
/// </summary>
public static class HiddenCubeMessages
{
    public const ushort Spawn = 0x3634, Removed = 0x3635, OpenStart = 0x3638, OpenDone = 0x363A, MapLoad = 0x3621, Teleport = 0x3623;
    public const byte ReasonDestroyed = 0x04, ReasonLeftView = 0x01;

    /// <summary>True for every opcode the radar reads; everything else is never copied.</summary>
    public static bool IsRelevant(ushort opcode) => opcode is Spawn or Removed or OpenStart or OpenDone or MapLoad or Teleport;

    public static bool TryParseSpawn(ReadOnlySpan<byte> b, out HiddenCubeObjectSpawn spawn)
    {
        spawn = default;
        var o = 0;
        if (!TryVarint(b, ref o, out var id) || o + 26 > b.Length) return false;
        var kind = BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
        var template = BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 2)..]);
        var x = BinaryPrimitives.ReadSingleLittleEndian(b[(o + 6)..]);
        var y = BinaryPrimitives.ReadSingleLittleEndian(b[(o + 10)..]);
        var z = BinaryPrimitives.ReadSingleLittleEndian(b[(o + 14)..]);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
        long created = 0;
        if (o + 37 <= b.Length)
        {
            created = BinaryPrimitives.ReadInt64LittleEndian(b[(o + 29)..]);
            if (created is < 1_600_000_000_000 or > 2_600_000_000_000) created = 0;
        }
        spawn = new HiddenCubeObjectSpawn(id, kind, template, x, y, z, created);
        return true;
    }

    public static bool TryParseRemoved(ReadOnlySpan<byte> b, out HiddenCubeObjectRemoved removed)
    {
        removed = default;
        var o = 0;
        if (!TryVarint(b, ref o, out var id) || o >= b.Length) return false;
        removed = new HiddenCubeObjectRemoved(id, b[o]);
        return true;
    }

    public static bool TryParseInteraction(ReadOnlySpan<byte> b, bool done, out HiddenCubeInteraction interaction)
    {
        interaction = default;
        var o = 0;
        if (!TryVarint(b, ref o, out var obj) || !TryVarint(b, ref o, out var actor)) return false;
        uint count = 0, item = 0;
        if (done && o + 8 <= b.Length)
        {
            count = BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
            item = BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 4)..]);
        }
        interaction = new HiddenCubeInteraction(obj, actor, done, count, item);
        return true;
    }

    /// <summary>Map id of a map load record (body offset 4), or null.</summary>
    public static int? TryParseMapId(ReadOnlySpan<byte> b) =>
        b.Length >= 8 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) : null;

    /// <summary>The own world position carried by a map load (offset 16) or teleport (offset 6) record.</summary>
    public static bool TryParseOwnPosition(ushort opcode, ReadOnlySpan<byte> b, out (float X, float Y, float Z) position)
    {
        position = default;
        var at = opcode switch { MapLoad => 16, Teleport => 6, _ => -1 };
        if (at < 0 || at + 12 > b.Length) return false;
        var x = BinaryPrimitives.ReadSingleLittleEndian(b[at..]);
        var y = BinaryPrimitives.ReadSingleLittleEndian(b[(at + 4)..]);
        var z = BinaryPrimitives.ReadSingleLittleEndian(b[(at + 8)..]);
        if (!IsWorldPosition(x, y, z)) return false;
        position = (x, y, z);
        return true;
    }

    /// <summary>Finite, inside the map bounds (±420,000), and not a small vector near the origin.</summary>
    public static bool IsWorldPosition(float x, float y, float z) =>
        float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) &&
        MathF.Abs(x) < 420_000 && MathF.Abs(y) < 420_000 && MathF.Abs(z) < 120_000 &&
        MathF.Sqrt(x * x + y * y) > 2_000;

    /// <summary>LEB128 unsigned varint, at most 5 bytes / 32 bits.</summary>
    internal static bool TryVarint(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        value = 0;
        var shift = 0;
        for (var i = offset; i < data.Length && i - offset < 5; i++)
        {
            var b = data[i];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                offset = i + 1;
                return true;
            }
            shift += 7;
        }
        value = 0;
        return false;
    }
}
