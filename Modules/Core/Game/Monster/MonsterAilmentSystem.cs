/// <summary>
/// The monsters' chill, freeze and shock, knockbacks and proliferation, presented once for the area: every two frames,
/// right before the AI decides (a chill re-times the walks it reads, as in the AI pass that used to run it), what changed
/// is written to every player who has the monster awake, its buffs naming their source as that player's client knows it
/// (ForObserver). Only the area's first player was sent them while the pass ran on its loop.
/// </summary>
public static partial class Game
{
    public static void StartMonsterAilments(GameArea area, Player conn, object source)
    {
        var frames = conn.Frames;
        var monsters = conn.Monsters;
        area.Systems.Add("monster ailments", AreaPhase.Ai, a =>
        {
            if (!CurrentGameWork || frames.View is not { } view || view.Paused || view.Closed || !view.PlayerSent) return;
            List<(byte[] Packet, string Log)> changes;
            lock (monsters)
                using (ProjectileMutation(view, phase: "ailments"))
                    changes = AreaAilmentChangesLocked(view, Math.Max(ProjectileWorldTime(view), a.Time));
            if (changes.Count == 0) return;
            foreach (var player in frames.Players())
            {
                if (player.Closed || player.LeavingArea || !player.PlayerSent) continue;
                var entity = new EntityId(player.PeerId);
                foreach (var (packet, log) in changes)
                {
                    uint id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2));
                    // A message wakes a sleeping object (SharedMonsters.cs): the others get those of monsters they have awake.
                    if (!ReferenceEquals(player, view) && Array.BinarySearch(player.AwakeMonsters, id) < 0) continue;
                    a.Output.To(entity, ForObserver(packet, player), log);
                }
            }
        }, order: -1, source: source);
    }
}
