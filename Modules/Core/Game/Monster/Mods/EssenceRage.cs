using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Rage's fire runes (<c>EssenceDaemonFireRuneTrapCluster</c>): the rare throws a cluster of fire rune traps around
/// its target (Malachai's fire traps). Each rune arms, then bursts into a fire explosion when a player steps on it, or
/// fizzles when its duration ends.
/// <para>The rune is the client's own trap object, not a ground-effect patch: the skill is a trap
/// (<c>is_trap</c>, <c>create_trap_at_target_location</c>, action TrapGroundFire) and its <c>trap_variation</c> 11 names
/// <c>Metadata/MiscellaneousObjects/TrapFireRune</c> in SkillTrapVariations. Spawning burning-ground art instead drew a fire
/// patch where retail shows a rune. The count, scatter circle, duration and area are the skill's own.</para></summary>
public static partial class MapEssences
{
    const string RuneExplosionArt = "Metadata/Effects/Spells/monsters_effects/act4/Malachai/rune_circle/explosion.ao";

    /// <summary>The least Manhattan distance between two runes, so a cluster never stacks two traps on one cell.</summary>
    const int RuneSpacing = 3;

    /// <summary>A placed fire rune: its cell, the radius that sets it off, when it arms and expires, its fire damage, its
    /// burst radius and the trap object's id.</summary>
    sealed record Rune((int X, int Y) At, double TriggerRadius, double Armed, double Until, DamagePacket Damage, uint Art, int Burst);

    static Task CastRunes(Player conn, SpawnedMonster monster, Player target, SpecialSpell spell)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area)) return Task.CompletedTask;
        var catalog = SkillsFor(conn);
        var damage = catalog.HitDamage(spell.Effect, monster.Level);
        if (damage.IsZero) return Task.CompletedTask;
        var stats = catalog.EffectStats(spell.Effect, catalog.MaxLevelAt(spell.Effect, monster.Level)).ToDictionary(s => s.Item1, s => s.Item2);
        int Get(uint key) => stats.GetValueOrDefault(key);
        // The skill throws five additional traps: six at once, the count seen on retail. The tier scale reduces it.
        int count = ScaledSpellCount(spell.Effect, spell.Tier, Math.Max(1, Get(Stat.NumberOfAdditionalTrapsToThrow) + 1));
        int circle = Get(Stat.ThrowTrapsInCircleRadius);
        double duration = Math.Max(1, Get(Stat.BaseTrapDuration) / 1000.0);
        int burst = Math.Max(4, SkillArea.Radius(SkillArea.BaseRadius(catalog.EffectId(spell.Effect), Get), Get, true));
        double trigger = TrapTriggerRadius(Get);
        double now = Now;
        var at = target.Walk.Position(AreaTime(target));
        var placed = new List<(int X, int Y)>();
        lock (area.Runes) placed.AddRange(area.Runes.Select(r => r.At));
        for (int i = 0; i < count; i++)
        {
            // A rune appears where it lands; it does not slide there from the caster. Two runes never share a cell: the
            // throw is re-rolled while its spot is taken, and a rune that cannot find a free cell is not placed.
            (int X, int Y) spot = at;
            bool free = false;
            for (int attempt = 0; attempt < 12 && !free; attempt++)
            {
                spot = PlacedThrowPoint(at, count, circle, Random.Shared);
                if (!conn.Layout.WalkableFor(spot.X, spot.Y, 0)) continue;
                spot = conn.Layout.Near(spot);
                free = placed.All(p => Math.Abs(p.X - spot.X) + Math.Abs(p.Y - spot.Y) >= RuneSpacing);
            }
            if (!free) continue;
            placed.Add(spot);
            uint art = SpawnRune(conn, catalog, spell, monster.Level, spot, stats);
            lock (area.Runes) area.Runes.Add(new(spot, trigger, now + .5, now + duration, damage, art, burst));
        }
        Console.WriteLine($"[map-content] essence rage: {count} fire runes by 0x{monster.ObjectId:x} at ({at.X}, {at.Y}), circle {circle}, burst {burst}");
        return Task.CompletedTask;
    }

    /// <summary>The client's fire rune trap object, created <b>at</b> its cell: the rune appears there, it is not thrown from
    /// the caster and does not slide across the ground (a MoveTo from the rare read as the runes drifting away from it).
    /// The client's own create plays the object's appear animation. Its id is removed when the rune bursts or fizzles.</summary>
    static uint SpawnRune(Player conn, SkillCatalog catalog, SpecialSpell spell, byte level,
        (int X, int Y) at, Dictionary<uint, int> stats)
    {
        int variation = stats.GetValueOrDefault(Stat.TrapVariation);
        string path = "Metadata/MiscellaneousObjects/TrapFireRune";
        foreach (var row in ClientTables.TableOrEmpty("SkillTrapVariations"))
            if (row.Int("Id") == variation && row.TextOrNull("Metadata") is { Length: > 0 } meta) { path = meta; break; }
        var own = new List<(uint, int)> { (Stat.MaximumLife, 1), (Stat.UsesNoMana, 1), (Stat.IsTrapObject, 1) };
        int effectLevel = catalog.MaxLevelAt(spell.Effect, level);
        var payload = new Dictionary<uint, int>(stats);
        foreach (var (key, value) in own) payload[key] = value;
        uint id = conn.TakeObjectId();
        var skills = catalog.FindByRow(0x4000, spell.Effect, effectLevel, [.. payload.Select(p => (p.Key, p.Value))]) is { } proxy
            ? new[] { proxy } : [];
        conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(path),
            new TrapMineObject(at.X, at.Y, false, skills, own).Blob()), null, WorldId: id, CreatesObject: true));
        conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(id), null, WorldId: id));
        return id;
    }

    /// <summary>Called from the essence tick: fizzles the expired runes and sets off any armed rune a player steps on.</summary>
    static void TickRunes(Player conn)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area)) return;
        double now = Now;
        Rune[] runes;
        lock (area.Runes)
        {
            foreach (var gone in area.Runes.Where(r => r.Until <= now).ToArray())
            {
                area.Runes.Remove(gone);
                RemoveEffect(conn, gone.Art);
            }
            runes = [.. area.Runes];
        }
        if (runes.Length == 0) return;
        foreach (var player in Members(conn).Where(c => c.Connection.PlayerSent && c.PlayerLife > 0 && !c.Immune && !InGracePeriod(c)).ToArray())
        {
            var here = player.Walk.Position(AreaTime(player));
            foreach (var rune in runes)
            {
                if (now < rune.Armed || Dist(here, rune.At) > rune.TriggerRadius) continue;
                lock (area.Runes)
                    if (!area.Runes.Remove(rune)) continue; // another player's tick already set it off
                RemoveEffect(conn, rune.Art);
                _ = ExplodeRune(conn, rune);
            }
        }
    }

    static async Task ExplodeRune(Player conn, Rune rune)
    {
        uint boom = SpawnEffect(conn, rune.At, RuneExplosionArt, 1.5f);
        _ = RemovePulseLater(conn, boom, 1.0);
        foreach (var player in PlayersNear(conn, rune.At, rune.Burst).ToArray())
        {
            if (player.Immune || InGracePeriod(player)) continue;
            int amount = (int)Math.Clamp(rune.Damage.Roll(Random.Shared).MinTotal, 0, int.MaxValue);
            if (amount <= 0 || !TrySyntheticEnemyHit(player, DamageType.Fire, amount, block: true, spell: true, suppress: true, out _)) continue;
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.LifeUpdate(player.Id, player.PlayerLife, player.PlayerReservedLife), "essence fire rune");
            await SendPlayerEnergyShield(player);
            if (player.PlayerLife == 0) await PlayerDied(player, "death from an essence fire rune");
        }
    }
}