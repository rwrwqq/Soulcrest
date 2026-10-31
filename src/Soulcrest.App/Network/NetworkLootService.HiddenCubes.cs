using Soulcrest.Core.Network.HiddenCubes;

namespace Soulcrest.App.Network;

/// <summary>
/// Live Hidden Cube records (object spawn/remove/open, map load, teleport) for the cube radar. Only an
/// observer: it reads the same already decoded live messages, never changes the progress dispatch and
/// never sends anything. Replayed login-prefix messages are not observed twice.
/// </summary>
public sealed partial class NetworkLootService
{
    /// <summary>Opcode, message body (after the two opcode bytes, a private copy) and arrival time.</summary>
    public event Action<ushort, byte[], DateTimeOffset>? HiddenCubeRecord;

    private void ObserveHiddenCubeRecord(byte[] message)
    {
        if (HiddenCubeRecord is not { } handler || message.Length < 2) return;
        var opcode = (ushort)(message[0] | message[1] << 8);
        if (!HiddenCubeMessages.IsRelevant(opcode)) return;
        try { handler(opcode, message.AsSpan(2).ToArray(), DateTimeOffset.UtcNow); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A radar failure must never disturb the soul and loot tracking on this thread.
            System.Diagnostics.Trace.TraceWarning("Hidden Cube radar failed: {0}", error.Message);
        }
    }
}
