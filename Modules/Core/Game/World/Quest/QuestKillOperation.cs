using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // An admitted quest kill completes under QuestGate -> cipher -> level -> Monsters.
    // Captured factories run now; only owned bytes and host incarnation tickets leave this bounded scope.
    [ThreadStatic] static QuestOutputCapture? QuestKillCommitOutput;

    sealed class QuestOutputCapture(Player connection, WorldAdmission admission)
    {
        public Player Connection => connection;
        public WorldAdmission Admission => admission;
        readonly List<(PlayerMinions.Output? Legacy, OutputBatch? Typed)> entries = [];
        public void Add(PlayerMinions.Output output) => entries.Add((output, null));
        public void AddTyped(OutputBatch batch) => entries.Add((null, batch));
        public OutputBatch Project()
        {
            List<OutputRecord> records = [];
            foreach (var (legacy, typed) in entries)
            {
                if (typed is not null) { records.AddRange(typed.Records); continue; }
                var output = legacy!;
                if (output.Kind == PlayerMinions.OutputKind.QuestTransition)
                {
                    if (QuestTransitionRecords(connection, admission, output.TargetId!.Value, output.Transition!) is { } transition)
                        records.AddRange(transition.Records);
                    continue;
                }
                if (output.Kind == PlayerMinions.OutputKind.QuestFlags)
                {
                    if (QuestFlagPresentation(connection) is { } flags)
                        records.Add(connection.LockstepClient
                            ? PresentedRecord(connection.WorldOutput.PublishedObject(PlayerId), flags, output.Log)
                            : OutputRecord.Immediate(flags, output.Log));
                    continue;
                }
                byte[] bytes = output.Kind switch
                {
                    PlayerMinions.OutputKind.MonsterType => connection.MonsterTypes.Add(output.MonsterType!.Value)
                        ? GameServer.Core.Protocol.Instance.InstanceEntities.MonsterTypes([output.MonsterType.Value]) : [],
                    PlayerMinions.OutputKind.Passives => PassivesMessage(connection).Bytes,
                    _ => output.Packet,
                };
                if (output.WorldId is { } target)
                {
                    var builder = new WorldRecordBuilder(connection, admission);
                    if (output.CreatesObject) builder.Create(target, bytes, output.Log);
                    else if (output.RemovesObject) builder.Remove(target, output.Log);
                    else builder.Presentation(target, bytes, output.Log);
                    records.AddRange(builder.Build().Records);
                    continue;
                }
                // Explicit compatibility for producers not migrated yet. Do not infer World boundaries or keys.
                if (bytes.Length != 0) records.Add(OutputRecord.Immediate(bytes, output.Log));
            }
            return new([.. records]);
        }
    }

    public static OutputBatch CaptureQuestWorldOutput(Player conn, WorldAdmission admission, Func<Task> commit)
    {
        if (QuestKillCommitOutput is not null) throw new InvalidOperationException("Nested quest kill transaction");
        var capture = new QuestOutputCapture(conn, admission);
        QuestKillCommitOutput = capture;
        try
        {
            var work = commit();
            if (!work.IsCompleted) throw new InvalidOperationException("Quest kill commit must not yield");
            work.GetAwaiter().GetResult();
            return capture.Project();
        }
        finally { QuestKillCommitOutput = null; }
    }

    static OutputBatch CommitQuestKillOperation(Player conn, PlayerMinions.Output receipt, WorldAdmission admission)
    {
        if (receipt.Target?.Variety is not { } variety) return OutputBatch.Empty;
        return CaptureQuestWorldOutput(conn, admission, () => QuestKillEffects(conn,
            receipt.Target, variety, receipt.At!.Value, dropsCommitted: true));
    }

    // The outer admission owns the gate and all accepted birth reservations. Nested typed sends cannot await it
    // again or wait for a future frame. Pure post-birth presentation is represented explicitly in the returned batch.
    public static Task<bool> SendWorldBatch(Player conn, IReadOnlyList<WorldObject> requires,
        Func<WorldAdmission, OutputBatch?> atGate, Func<bool>? still = null, bool quest = false, 
        IReadOnlyList<WorldObject>? creates = null)
    {
        if (PresentVerifyTag.Value is { } presentTag)
        {
            var tagged = atGate;
            atGate = admission => tagged(admission) is not { } records ? null : records.Records.Count == 0 ? records
                : Logged(records, () => Console.WriteLine($"[present-verify] {conn.Remote} {presentTag} sent unmarked: (records)"));
        }
        if (Mirroring.Value)
        {
            var project = atGate;
            atGate = admission => project(admission) is { } batch
                ? new([.. batch.Records.Select(record => record.AsSharedView())]) : null;
        }
        // A lockstep client's monster dies in the frame its death is written in, whichever producer writes it.
        if (conn.LockstepClient)
        {
            var project = atGate;
            atGate = admission =>
            {
                var batch = project(admission);
                if (batch is not null) CorpseDeathsWritten(conn, batch);
                return batch;
            };
        }
        if (QuestKillCommitOutput is not { } capture)
        {
            if (!conn.LockstepClient) return PredictiveBatch();
            return Program.SendRecords(conn, requires, atGate, still,
                creates: creates, rules: Program.GameLogicOrNull as IWorldAdmissionRules, quest: quest);
        }
        if (!ReferenceEquals(capture.Connection, conn)) throw new ArgumentException("A quest capture belongs to one connection.");
        foreach (var target in requires) conn.WorldOutput.CheckOwner(target);
        if (requires.Any(target => !target.IsReady))
            throw new InvalidOperationException("A synchronous quest producer cannot await an object birth; prepare presentation or admit later.");
        if (still is not null && !still()) return Task.FromResult(false);
        if (capture.Admission.Capture(atGate) is not { } batch) return Task.FromResult(false);
        capture.AddTyped(batch);
        return Task.FromResult(true);

        async Task<bool> PredictiveBatch()
        {
            bool projected = false;
            await Program.Send(conn, [], null, still, atGate: () =>
            {
                // Preserve predictive's existing batching, guards, projection time and combined wire write.
                var admission = new WorldAdmission(conn.WorldOutput, []);
                try
                {
                    if (atGate(admission) is not { } batch) return null;
                    projected = true;
                    return [.. batch.Records.SelectMany(record => record.CopyBytes())];
                }
                finally { admission.Finish(false); }
            });
            return projected;
        }
    }

    // Outside this bounded transaction, use the ordinary guarded cipher gate. Inside, evaluate projections
    // under the mutation lock and retain bytes. None of these awaits can yield to a retired Game callback.
    static T Logged<T>(T value, Action log) { log(); return value; }

    static Task Send(Player conn, byte[] bytes,
        string? log, Func<bool>? still = null, Func<byte[]?>? atGate = null, string? sync = null)
    {
        if (PresentVerifyTag.Value is not null)
        {
            if (atGate is null && bytes.Length > 0) Console.WriteLine($"[present-verify] {conn.Remote} {PresentVerifyTag.Value} sent unmarked: {log}");
            atGate = Tagged(atGate, conn.Remote, log);
        }
        if (QuestKillCommitOutput is not { } output)
            return Program.Send(conn, bytes, log, still, atGate, sync);
        if (!ReferenceEquals(output.Connection, conn))
            throw new ArgumentException("A quest capture belongs to one cipher stream.");
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
