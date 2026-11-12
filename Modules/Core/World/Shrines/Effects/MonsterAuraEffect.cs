using GameServer.Core.Protocol.Instance;
using static Game;

namespace GameServer.Modules.Shrines;

/// <summary>
/// The shrine's monster-side buff on the monsters around it while it is unclaimed: the Shrines row's
/// <c>Monster_ShrineBuffsKey</c> put on every hostile monster in the shrine's neighbourhood, and taken off again when the
/// player claims the shrine or the monster leaves. The monster's own <see cref="SpawnedMonster.EncounterStats"/> are
/// saved and restored, so the aura leaves nothing behind on a monster that outlives it.
/// <para>Reusable: a shrine whose data names a monster buff and whose gameplay accelerates or weakens the monsters around
/// it is one line in its <see cref="ShrineDefinition.BeforeActivation"/>.</para>
/// </summary>
sealed class MonsterAuraEffect : IShrineEffect
{
    sealed class Buffed
    {
        /// <summary>The monster's EncounterStats before the aura (null when it had none).</summary>
        public IReadOnlyDictionary<string, int>? Previous;
    }

    readonly Dictionary<uint, Buffed> buffed = [];

    /// <summary>Says so when the row this aura was registered for names no monster buff: then no monster can ever enter
    /// doing anything, and the shrine is player-side only by data rather than by accident.</summary>
    public Task OnSpawn(ShrineContext ctx, ShrineInstance shrine)
    {
        if (shrine.Definition.Monster is null)
            Console.WriteLine($"[gateway] shrine {shrine.Definition.Id}: its Shrines row names no Monster_ShrineBuffsKey; " +
                "the monster aura has nothing to apply");
        return Task.CompletedTask;
    }

    public Task OnNearbyMonsterEnter(ShrineContext ctx, ShrineInstance shrine, uint monster)
    {
        if (shrine.Definition.Monster is not { } buff || buffed.ContainsKey(monster)) return Task.CompletedTask;
        var stats = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in buff.Stats)
            if (key >= 1 && key <= Stats.Names.Count)
            {
                string name = Stats.Name(key);
                stats[name] = stats.GetValueOrDefault(name) + value;
            }
        if (stats.Count == 0) return Task.CompletedTask;
        lock (ctx.Conn.Monsters)
        {
            if (!ctx.Conn.Monsters.TryGetValue(monster, out var m) || m.Dead) return Task.CompletedTask;
            var merged = new Dictionary<string, int>(StringComparer.Ordinal);
            if (m.EncounterStats is { } previous)
                foreach (var (name, value) in previous) merged[name] = value;
            foreach (var (name, value) in stats) merged[name] = merged.GetValueOrDefault(name) + value;
            buffed[monster] = new Buffed { Previous = m.EncounterStats };
            m.EncounterStats = merged;
        }
        // No buff is sent to the client for the aura: the stats are the server's own, and a shrine buff row would draw
        // its icon over the monster's head, which the monsters around an unclaimed shrine should not wear.
        Console.WriteLine($"[gateway] {ctx.Conn.Wire.Remote} monster 0x{monster:x} has shrine aura {buff.Template} " +
            $"(BuffDefinitions row {buff.BuffRow}), server-side only");
        return Task.CompletedTask;
    }

    public Task OnNearbyMonsterLeave(ShrineContext ctx, ShrineInstance shrine, uint monster) => Remove(ctx, monster);

    /// <summary>The shrine is claimed: the monsters it accelerated lose the buff with the shrine's own activation.</summary>
    public async Task OnActivation(ShrineContext ctx, ShrineInstance shrine)
    {
        foreach (uint monster in buffed.Keys.ToArray()) await Remove(ctx, monster);
    }

    /// <summary>The area is going: its monsters and their buffs go with it, so nothing is sent.</summary>
    public Task OnMapUnload(ShrineContext ctx, ShrineInstance shrine)
    {
        buffed.Clear();
        return Task.CompletedTask;
    }

    Task Remove(ShrineContext ctx, uint monster)
    {
        if (!buffed.Remove(monster, out var entry)) return Task.CompletedTask;
        lock (ctx.Conn.Monsters)
            if (ctx.Conn.Monsters.TryGetValue(monster, out var m)) m.EncounterStats = entry.Previous;
        return Task.CompletedTask;
    }
}
