/// <summary>
/// The monsters' chill, freeze and shock, knockbacks and proliferation, presented once for the area: every two frames,
/// right before the AI decides (a chill re-times the walks it reads, as in the AI pass that used to run it), what changed
/// is written to every player who has the monster awake, its buffs naming their source as that player's client knows it
/// (ForObserver). Only the area's first player was sent them while the pass ran on its loop.
/// </summary>
public static partial class Game
{
    public static void StartMonsterAilments(GameArea area, Player player, object source)
    {
        var frames = player.Frames;
        area.Systems.Add("monster ailments", AreaPhase.Ai, a =>
        {
            if (!CurrentGameWork || frames.View is not { } view || view.Paused || view.Closed || !view.Connection.PlayerSent) return;
            List<(byte[] Packet, string Log)> changes;
            // The mutation takes the experience gate, then the area's frame: taking Monsters first here deadlocked against a
            // monster spawn holding the gate (live 2026-11-20, the Prison's held-back monsters revealed).
            using (ProjectileMutation(view, phase: "ailments"))
                changes = AreaAilmentChangesLocked(view, Math.Max(ProjectileWorldTime(view), a.Time));
            if (changes.Count == 0) return;
            // Once, for every client in the area.
            foreach (var (packet, log) in changes)
                a.Delta.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)), packet, log);
        }, order: -1, source: source);
    }
}
