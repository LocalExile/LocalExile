using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // An admitted quest kill completes synchronously under QuestGate -> cipher -> level -> Monsters.
    // Helpers may await only captured Send or synchronously completed helpers. No module continuation or
    // callback survives the scope. One cipher batch orders every creation before dependent actor output.
    // The mutation scope finishes one-shot unlocks and phase extras before replacement can take it.
    [ThreadStatic] static List<PlayerMinions.Output>? QuestKillCommitOutput;

    static byte[] CommitQuestKillOperation(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, PlayerMinions.Output receipt)
    {
        if (receipt.Target?.Variety is not { } variety) return [];
        if (QuestKillCommitOutput is not null) throw new InvalidOperationException("Nested quest kill transaction");
        var packets = new List<PlayerMinions.Output>();
        QuestKillCommitOutput = packets;
        try
        {
            var commit = QuestKillEffects(remote, stream, ciphers, conn, receipt.Target, variety,
                receipt.At!.Value, dropsCommitted: true);
            if (!commit.IsCompleted) throw new InvalidOperationException("Quest kill commit must not yield");
            commit.GetAwaiter().GetResult();
            // The caller owns the cipher gate and the normal level -> actor mutation scope.
            // Publish every creation before an AI sender can acquire that gate for these actors.
            return [.. packets.SelectMany(output => output.Kind switch
            {
                PlayerMinions.OutputKind.MonsterType => conn.MonsterTypes.Add(output.MonsterType!.Value)
                    ? GameServer.Core.Protocol.Instance.InstanceEntities.MonsterTypes([output.MonsterType.Value]) : [],
                PlayerMinions.OutputKind.QuestFlags => QuestFlagPresentation(conn) ?? [],
                PlayerMinions.OutputKind.QuestTransition => QuestTransitionPresentation(conn, output.TargetId!.Value, output.Transition!) ?? [],
                PlayerMinions.OutputKind.Passives => PassivesMessage(conn).Bytes,
                _ => output.Packet,
            })];
        }
        finally { QuestKillCommitOutput = null; }
    }

    // Outside this bounded transaction, use the ordinary guarded cipher gate. Inside, evaluate projections
    // under the mutation lock and retain bytes. None of these awaits can yield to a retired Game callback.
    static Task Send(string remote, NetworkStream stream, ConnectionCiphers ciphers, byte[] bytes,
        string? log, Func<bool>? still = null, Func<byte[]?>? atGate = null, string? sync = null)
    {
        if (QuestKillCommitOutput is not { } output)
            return Program.Send(remote, stream, ciphers, bytes, log, still, atGate, sync);
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
