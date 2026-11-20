using static Program;

public static partial class Game
{
    // A committed area hit belongs to the recipient. The original caster, projectile or runner can leave while the
    // recipient's socket is waiting. Project the result under its send gate and the same instance's combat lock.
    internal static async Task PublishInstanceOutput(InstanceState instance, ClientState player, Func<Func<bool>, Task> publish)
    {
        var rules = CombatLifetime;
        bool Here() => CurrentGameRules && ReferenceEquals(rules, CombatLifetime) && !player.Closed && !player.LeavingArea
            && !player.WorldOutput.Closed && player.Instance == instance;
        var previous = ProjectileOutputGuard.Value;
        ProjectileOutputGuard.Value = Here;
        try { if (Here()) await publish(Here); }
        finally { ProjectileOutputGuard.Value = previous; }
    }

    static Task PublishInstanceContact(InstanceState instance, ClientState player, int? epoch, bool survived,
        Func<WorldAdmission, bool, OutputBatch?> atGate) =>
        PublishInstanceOutput(instance, player, async current =>
        {
            bool flask;
            lock (instance.World.Monsters)
            {
                if (!current()) return;
                flask = survived && epoch == player.IncomingHitEpoch;
            }
            if (flask) await player.FlaskBarGate.WaitAsync();
            bool sent;
            try
            {
                WorldObject[] required;
                lock (instance.World.Monsters)
                {
                    if (!current()) return;
                    required = player.LockstepClient && epoch == player.IncomingHitEpoch ? [player.WorldOutput.PublishedObject(PlayerId)] : [];
                }
                sent = await SendWorldBatch(player, required, admission =>
                {
                    lock (instance.World.Monsters)
                    {
                        if (!current()) return null;
                        OutputBatch? result = null;
                        CombatLifetime!.TryRunSynchronous(() => result = atGate(admission, flask));
                        return result;
                    }
                }, still: current);
            }
            finally { if (flask) player.FlaskBarGate.Release(); }
            if (sent && flask && current())
            {
                var wire = player.Wire;
                await PublishFlaskChanges(wire.Remote, wire.Stream, wire.Ciphers, player);
            }
        });

    static OutputBatch CaptureInstanceHit(ClientState player, WorldAdmission admission, uint source, MonsterHitOutcome hit, bool flask)
    {
        var wire = player.Wire;
        return CaptureWorldOutput(player, admission, () => PresentMonsterHit(wire.Remote, wire.Stream, wire.Ciphers, player, source, hit), flask);
    }

    internal static Task PresentVesselHit(InstanceState instance, ClientState player, uint source, int epoch, MonsterHitOutcome hit, Action acknowledge) =>
        PublishInstanceContact(instance, player, epoch, hit.Life > 0, (admission, flask) =>
        {
            if (player.IncomingHitEpoch != epoch) { acknowledge(); return null; }
            var records = new WorldRecordBuilder(player, admission);
            if (player.MonsterViews.ContainsKey(source) && !player.AsleepMonsters.Contains(source))
                records.Presentation(source, hit.HitPacket);
            records.Add(CaptureInstanceHit(player, admission, source, hit, flask));
            var batch = records.Build();
            acknowledge();
            return batch;
        });
}
