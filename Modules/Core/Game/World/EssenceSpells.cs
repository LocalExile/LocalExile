using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// The essence spells the hit kit cannot represent as damage/summon/projectile: the resistance pulses (Hatred/Anger/Wrath),
/// the chaos damage-over-time pulse (Misery) and the grasping-hand grab (Envy). Their daemon effects carry no hit, so the
/// rare casts them as kit specials (<see cref="MapEssences.EssenceKit"/> marks them with an <see cref="EssenceSpellInfo"/>);
/// the rare's own action plays the cast on its rig, and at the contact <see cref="EssenceSpellContact"/> lands the effect on
/// the players near the rare instead of a hit:
/// <list type="bullet">
/// <item>RepeatedPulseCold/Fire/Lightning: <c>base_&lt;type&gt;_damage_resistance_%</c> (-10) on the player for the pulse's
/// duration, the essence elemental-damage-taken debuff (BuffDefinitions 746, invisible).</item>
/// <item>RepeatedPulseDegen: <c>base_chaos_damage_to_deal_per_minute</c> as a chaos degeneration on the player for the
/// pulse's duration, after chaos resistance, through <c>TrapDegenPerSecond</c> (the essence daemon degen aura, buff 100).</item>
/// <item>EssenceDaemonGrab: teleports the player to the rare; moving out of reach before the contact evades it.</item>
/// </list>
/// The wiki: Hatred/Anger/Wrath "debuffs the player with -10% resistance", Misery "pulses that inflict chaos damage over
/// time", Envy "a grasping hand that teleports the player to the monster's location … can be evaded by moving". The pulses
/// are self-centred novas (ActiveSkill repeated_pulse, TargetType 4); the grab is dominus_grab (DominusGrab).
/// </summary>
public static partial class MapEssences
{
    static double Now => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    /// <summary>The contact of an essence pulse/grab the rare just cast (Program.MonsterSwing). The rare's own rig has no
    /// RepeatedPulse/DominusGrab clip, so the essence's daemon — whose rig does — is manifested riding the rare (attached,
    /// no separate blocker) to play the cast; the pulse's debuff/degen or the grab then lands on the players near it.</summary>
    public static async Task EssenceSpellContact(ClientState conn, SpawnedMonster monster, MonsterAttack attack)
    {
        if (attack.EssenceSpell is not { } spell || conn.Closed || conn.LeavingArea) return;
        var at = monster.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        var player = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        double range = Math.Max(attack.Range, 30);
        uint daemon = await ShowDaemon(conn, monster, spell, at, player);
        try
        {
            switch (spell.Kind)
            {
                case "cold" or "fire" or "lightning":
                    await Task.Delay(TimeSpan.FromSeconds(0.6));
                    if (!conn.Closed && !conn.LeavingArea) await ResDebuff(conn, at, range, spell);
                    break;
                case "chaos":
                    await Task.Delay(TimeSpan.FromSeconds(0.6));
                    if (!conn.Closed && !conn.LeavingArea) ChaosDegen(conn, at, range, spell);
                    break;
                case "grab":
                    // The hand trap appears where the player stood; after its windup, only a player still inside it is
                    // pulled to the rare — stepping out evades it (the wiki).
                    await Task.Delay(TimeSpan.FromSeconds(1.0));
                    if (!conn.Closed && !conn.LeavingArea) await Grab(conn, monster, player, 10);
                    break;
            }
            await Task.Delay(TimeSpan.FromSeconds(0.5));
        }
        finally { RemoveDaemon(conn, daemon); }
        Console.WriteLine($"[map-content] essence {spell.Kind} pulse by 0x{monster.ObjectId:x} at ({at.X}, {at.Y}), reach {range:0}");
    }

    /// <summary>The essence's daemon riding the rare, playing its skill action so the client shows the pulse/grab on the
    /// daemon's rig (the rare's has no clip). Attached to the rare's root so it adds no blocker of its own; friendly,
    /// invulnerable, out of the AI, no bar. The grab aims at the player (the hand appears there); a pulse is self-centred.</summary>
    static async Task<uint> ShowDaemon(ClientState conn, SpawnedMonster monster, EssenceSpellInfo spell, (int X, int Y) at, (int X, int Y) player)
    {
        var catalog = SkillsFor(conn);
        if (Monsters.Find(spell.Daemon) is not { Supported: true } variety || catalog.ActionHash(spell.Effect) is not ushort hash) return 0;
        ushort skillId = (ushort)(hash & 0x3fff);
        var stats = catalog.EffectStats(spell.Effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        var action = new MonsterAttack(skillId, Math.Max(0.1, catalog.CastMs(spell.Effect) / 1000.0), 26, DamagePacket.Empty) { Spell = true };
        foreach (var (key, value) in action.InPlaceSkillStats()) stats[key] = value;
        if (catalog.FindByRow(skillId, spell.Effect, 1, [.. stats.Select(p => (p.Key, p.Value))]) is not { } skill) return 0;
        var (remote, stream, ciphers) = conn.Wire;
        var target = spell.Kind == "grab" ? player : at;
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, (byte)AreaLevel(conn), MonsterRarity.Normal, at.X, at.Y,
            friendly: true, log: false, alone: true, scripted: true, ownedSkills: [skill], instanceOwned: true,
            placement: MonsterPlacement.Established, attachTo: (monster.ObjectId, "<root>", 0, 3),
            extraStats: [(StatKeys.HideMiniLifeBar, 1)],
            owned: (_, m) =>
            {
                m.NoRewards = true; m.Attack = null; m.Aggro = false; m.BusyUntil = double.PositiveInfinity;
                m.EncounterStats = new Dictionary<string, int> { ["base_cannot_be_damaged"] = 1 };
            });
        if (id == 0) return 0;
        await SendWorldRecord(remote, stream, ciphers, conn, id,
            [.. InstanceProtocol.ActionStarted(id, at.X, at.Y, target.X, target.Y, skillId, 0xffff, conn.MonsterOrderFlags), .. InstanceProtocol.ActionEnded(id)],
            $"essence daemon {spell.Daemon} {spell.Kind}");
        return id;
    }

    static void RemoveDaemon(ClientState conn, uint id)
    {
        if (id == 0) return;
        lock (conn.Monsters)
            if (conn.Monsters.Remove(id, out var monster))
            {
                monster.Dead = true; monster.Life = 0; monster.ActionGeneration++;
                conn.MonsterViews.Remove(id); conn.AsleepMonsters.Remove(id);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), "essence daemon ends", id, monster,
                    PlayerMinions.OutputKind.Remove, WorldId: id, RemovesObject: true));
            }
    }

    static IEnumerable<ClientState> PlayersNear(ClientState conn, (int X, int Y) at, double radius) => Members(conn)
        .Where(c => c.PlayerSent && c.PlayerLife > 0 && Dist(c.Walk.Position(c.Clock.Elapsed.TotalSeconds), at) <= radius);

    /// <summary>The resistance pulse: -10% of its element on the players near the rare for the pulse's duration. The same
    /// element's debuff refreshes rather than stacks; the buff (746) is invisible, so the debuff is the stat alone.</summary>
    static async Task ResDebuff(ClientState source, (int X, int Y) at, double range, EssenceSpellInfo spell)
    {
        string statName = spell.Kind switch
        {
            "cold" => "base_cold_damage_resistance_%",
            "fire" => "base_fire_damage_resistance_%",
            _ => "base_lightning_damage_resistance_%",
        };
        if (Stats.Key(statName) is not uint key || spell.Value == 0) return;
        double seconds = Math.Max(1, spell.Duration);
        foreach (var conn in PlayersNear(source, at, range).ToArray())
        {
            if (conn.Immune || InGracePeriod(conn)) continue;
            var effects = Effects.GetOrCreateValue(conn);
            ushort buff = conn.NextBuff++;
            lock (effects)
            {
                effects.Debuffs.RemoveAll(d => d.Until <= Now || d.Key == key);
                effects.Debuffs.Add((new Dictionary<uint, int> { [key] = spell.Value }, Now + seconds, buff, key));
            }
            var (remote, stream, ciphers) = conn.Wire;
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, buff, 746,
                u68: BitConverter.SingleToUInt32Bits((float)seconds)), $"essence {spell.Kind} resistance pulse {spell.Value}%");
            await RefreshStats(conn);
        }
    }

    /// <summary>The chaos pulse: a chaos degeneration on the players near the rare for the pulse's duration, after their
    /// chaos resistance, dealt through <c>TrapDegenPerSecond</c> (the tick recomputes the share). Pulses stack.</summary>
    static void ChaosDegen(ClientState source, (int X, int Y) at, double range, EssenceSpellInfo spell)
    {
        if (spell.Dps <= 0) return;
        double seconds = Math.Max(1, spell.Duration);
        foreach (var conn in PlayersNear(source, at, range).ToArray())
        {
            if (conn.Immune || InGracePeriod(conn)) continue;
            double dps = spell.Dps * Math.Max(0, 1 - Math.Min(MaxResistance(conn, DamageType.Chaos), Resistance(conn, DamageType.Chaos)) / 100.0);
            var effects = Effects.GetOrCreateValue(conn);
            ushort buff = conn.NextBuff++;
            lock (effects)
            {
                effects.Degens.RemoveAll(d => d.Until <= Now);
                effects.Degens.Add((dps, Now + seconds, buff));
            }
            var (remote, stream, ciphers) = conn.Wire;
            _ = SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, buff, 100,
                u68: BitConverter.SingleToUInt32Bits((float)seconds)), $"essence chaos degen pulse {dps:0.#}/s");
        }
    }

    /// <summary>The grab: the hand trap appeared where the player stood at the cast; after its windup, a player still
    /// inside it is pulled to the rare, one who stepped out is not (the wiki). The teleport is the gate's own (0x01b7).</summary>
    static async Task Grab(ClientState conn, SpawnedMonster monster, (int X, int Y) trapAt, double trapRadius)
    {
        if (conn.Immune || InGracePeriod(conn)) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (Dist(conn.Walk.Position(now), trapAt) > trapRadius) return;
        var at = monster.Walk.Position(now);
        var (remote, stream, ciphers) = conn.Wire;
        using (ProjectileMutation(conn))
            conn.Walk.Teleport(at.X, at.Y, ProjectileWorldTime(conn));
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.Teleport(PlayerId, at.X, at.Y),
            $"essence grab to ({at.X}, {at.Y})");
    }

    sealed class EssenceEffects
    {
        public readonly List<(Dictionary<uint, int> Stats, double Until, ushort Buff, uint Key)> Debuffs = [];
        public readonly List<(double Dps, double Until, ushort Buff)> Degens = [];
        public double DegenShare;
    }
    static readonly RuleTable<ClientState, EssenceEffects> Effects = new();

    /// <summary>GameEvents.PlayerStatSources: the resistance pulses on this player, while they last.</summary>
    public static Dictionary<uint, int> EssenceDebuffStats(ClientState conn, Dictionary<uint, int> stats)
    {
        if (!Effects.TryGetValue(conn, out var effects) || effects.Debuffs.Count == 0) return stats;
        double now = Now;
        lock (effects)
            foreach (var (debuff, until, _, _) in effects.Debuffs)
                if (until > now)
                    foreach (var (key, value) in debuff)
                        stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return stats;
    }

    /// <summary>Called from the essence tick: expires the player's pulses (their buffs removed, resistances refreshed) and
    /// keeps the chaos degeneration share of <c>TrapDegenPerSecond</c> equal to the active pulses' sum.</summary>
    static void TickEffects(ClientState conn)
    {
        if (!Effects.TryGetValue(conn, out var effects)) return;
        double now = Now;
        var goneBuffs = new List<ushort>();
        bool debuffChanged = false;
        lock (effects)
        {
            foreach (var gone in effects.Debuffs.Where(d => d.Until <= now).ToArray())
            { effects.Debuffs.Remove(gone); goneBuffs.Add(gone.Buff); debuffChanged = true; }
            foreach (var gone in effects.Degens.Where(d => d.Until <= now).ToArray())
            { effects.Degens.Remove(gone); goneBuffs.Add(gone.Buff); }
            double dps = effects.Degens.Sum(d => d.Dps);
            if (dps != effects.DegenShare)
            {
                lock (conn.Monsters) conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - effects.DegenShare + dps);
                effects.DegenShare = dps;
            }
        }
        if (conn.Closed) return;
        var (remote, stream, ciphers) = conn.Wire;
        foreach (ushort buff in goneBuffs)
            _ = SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.RemoveBuff(PlayerId, buff), null);
        if (debuffChanged && !conn.LeavingArea) _ = RefreshStats(conn);
    }

    /// <summary>A leaving player's pulses and their degeneration share go with them.</summary>
    static void LeaveEffects(ClientState conn)
    {
        if (Effects.TryGetValue(conn, out var effects))
        {
            lock (conn.Monsters) conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - effects.DegenShare);
            lock (effects) { effects.Debuffs.Clear(); effects.Degens.Clear(); effects.DegenShare = 0; }
        }
        Effects.Remove(conn);
    }

    static async Task RefreshStats(ClientState c)
    {
        lock (c.Monsters) { c.Pools = PoolsFor(c, c.Pools.Level); c.ApplyMovementSpeed(); }
        var w = c.Wire;
        await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, c, () => PlayerStatsUpdateAtGate(c, PlayerStatsWithCleared(c)), null);
    }
}