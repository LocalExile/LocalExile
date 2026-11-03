using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // Stable-operation contract: an admitted quest kill completes synchronously under QuestGate + Monsters.
    // Helpers may await only captured Send or synchronously completed helpers. No module continuation or
    // callback survives the scope; only Gateway packet values cross replacement. A replacement cannot take
    // the mutation lock until this admitted operation finishes, including one-shot unlocks and phase extras.
    [ThreadStatic] static List<PlayerMinions.Output>? QuestKillCommitOutput;

    static void CommitQuestKillOperation(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, PlayerMinions.Output receipt)
    {
        if (receipt.Target?.Variety is not { } variety) return;
        if (QuestKillCommitOutput is not null) throw new InvalidOperationException("Nested quest kill transaction");
        var packets = new List<PlayerMinions.Output>();
        QuestKillCommitOutput = packets;
        try
        {
            var commit = QuestKillEffects(remote, stream, ciphers, conn, receipt.Target, variety,
                receipt.At!.Value, dropsCommitted: true);
            if (!commit.IsCompleted) throw new InvalidOperationException("Quest kill commit must not yield");
            commit.GetAwaiter().GetResult();
            conn.Minions.OutputQueue.InsertRange(1, packets);
        }
        finally { QuestKillCommitOutput = null; }
    }

    // Outside this bounded transaction, use the ordinary guarded cipher gate. Inside, evaluate projections
    // under the mutation lock and retain bytes. None of these awaits can yield to a retired Game callback.
    static Task Send(string remote, NetworkStream stream, ConnectionCiphers ciphers, byte[] bytes,
        string? log, Func<bool>? still = null, Func<byte[]?>? atGate = null)
    {
        if (QuestKillCommitOutput is not { } output)
            return Program.Send(remote, stream, ciphers, bytes, log, still, atGate);
        if (still is not null && !still()) return Task.CompletedTask;
        if (atGate is not null)
        {
            if (atGate() is not { } current) return Task.CompletedTask;
            bytes = current;
        }
        output.Add(new(bytes, log));
        return Task.CompletedTask;
    }
}
