using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using Kind = MonsterElementalAilments.Kind;

// The player's chill, freeze and shock on monsters (MonsterElementalAilments, Agents/docs/protocol/20-damage.md §3):
// inflicted by every outgoing hit, shock as increased damage taken, chill and freeze in the monster AI, and their buffs.
partial class Program
{
    /// <summary>The factor on damage a monster takes: its mods' increased damage taken, its shock and Glacial Wave's chill,
    /// added together (PoB DamageTaken INC), then a spell's less damage taken.</summary>
    internal static double MonsterDamageTaken(SpawnedMonster m, bool spell, double now) =>
        m.Mods.Effects.DamageTaken(spell, m.Ailments.Magnitude(Kind.Shock, now) + m.Ailments.ChillDamageTakenPct(now));

    /// <summary>Within the Tempest (enemy_shock_on_kill): a monster the player just killed (its life reached 0 from a hit
    /// or Righteous Fire) is shocked through the player's modifiers, and its corpse spreads that shock. Under the Monsters
    /// lock.</summary>
    static void ShockOnKillLocked(ClientState conn, SpawnedMonster m)
    {
        var player = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        if (Get("enemy_shock_on_kill") != 0)
            m.Ailments.ShockOnKill(ProjectileWorldTime(conn), Get, name => m.Mods.Effects[name]);
    }

    /// <summary>Shock proliferation, once per AI tick before the buffs are sent: every living hostile monster within a
    /// spreading shock's radius (cells, 10 to the metre) of the monster it is on, a corpse included, has the strongest
    /// such shock until it ends (MonsterElementalAilments.Proliferated). Under the Monsters lock.</summary>
    static void ProliferateShocksLocked(ClientState conn, double now)
    {
        var sources = new List<(SpawnedMonster M, (int X, int Y) At, MonsterElementalAilments.Effect Shock)>();
        foreach (var m in conn.Monsters.Values)
            if (m.Ailments.Spreading(now) is { } shock) sources.Add((m, m.Walk.Position(now), shock));
        foreach (var m in conn.Monsters.Values)
        {
            if (m.Dead || !m.Hostile || sources.Count == 0) { m.Ailments.Proliferated(null, _ => 0); continue; }
            var at = m.Walk.Position(now);
            m.Ailments.Proliferated(sources.Where(s => !ReferenceEquals(s.M, m) && Dist(s.At, at) <= s.Shock.Spread)
                .Select(s => s.Shock).MaxBy(s => s.Magnitude), name => m.Mods.Effects[name]);
        }
    }

    /// <summary>Its action speed: 0 frozen, else less by its chill. Unstoppable (action_speed_cannot_be_reduced_below_base)
    /// keeps it at 1.</summary>
    internal static double MonsterActionFactor(SpawnedMonster m, double now) =>
        m.Mods.Effects["action_speed_cannot_be_reduced_below_base"] > 0 ? 1 : m.Ailments.ActionFactor(now);

    /// <summary>
    /// A player hit that landed on a living monster inflicts its ailments (MonsterElementalAilments.Inflict) from the
    /// damage the monster took per type: the mitigated packet × its damage taken × the critical multiplier on a critical
    /// strike. The threshold is its maximum life (wiki: equal to it for most monsters). <paramref name="player"/> is
    /// CharacterPlayerStats, <paramref name="skill"/> the skill's own stats; both count. Under the Monsters lock, after the
    /// hit's damage, so the hit does not gain from its own shock.
    /// </summary>
    internal static void AilmentsOnHitLocked(ClientState conn, SpawnedMonster m, DamagePacket mitigated, bool spell,
        bool critical, int criticalMultiplier, IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? skill,
        double now, Random random)
    {
        if (m.Dead || m.Life == 0) return;
        double factor = MonsterDamageTaken(m, spell, now) * (critical ? criticalMultiplier / 100.0 : 1);
        int Player(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        int Skill(string name) => skill is not null && Stats.Key(name) is uint key ? skill.GetValueOrDefault(key) : 0;
        var added = m.Ailments.Inflict(mitigated[DamageType.Cold].Min * factor, mitigated[DamageType.Lightning].Min * factor,
            critical, m.MaxLife, now, name => Player(name) + PanelStats.Of(name, Skill), name => m.Mods.Effects[name], random);
        // Mirrored: the ailments act on the player as well (wiki Reflect), as they landed, then through the player's own
        // modifiers; the exact reflected magnitude is not verified.
        if (added.Count == 0 || conn.PlayerLife == 0
            || !RandomRolls.Chance(m.Mods.Effects["reflect_non_damaging_ailments_%_chance"], 100, random)) return;
        double previous = conn.ElementalAilments.ActionFactor(now);
        foreach (var e in added)
            conn.ElementalAilments.Inflict(Enum.Parse<PlayerElementalAilments.Kind>(e.Kind.ToString()), e.Magnitude,
                e.Until - e.From, now, Player, random);
        PlayerAilmentsChangedLocked(conn, previous, now);
    }

    /// <summary>
    /// What changed on the monsters' ailments since the last AI tick, spread shocks included, as packets: the chilled, frozen and shocked buffs
    /// (BuffDefinitions 1, 2, 3, which draw BuffVisuals 3, 4, 5) and the virtual_action_speed_+% the native walk
    /// (0x141e7c0b0) and action clips read, which network stats do not derive (04 §4.1a). A chill re-times the server's
    /// walk by the same factor, so both walk the monster alike. A freeze is not sent there (−100 would divide the native
    /// clip by zero); the AI stops the monster instead. Under the Monsters lock.
    /// </summary>
    internal static List<(byte[] Packet, string Log)> MonsterAilmentChangesLocked(ClientState conn, double now)
    {
        ProliferateShocksLocked(conn, now);
        var packets = new List<(byte[], string)>();
        foreach (var (id, m) in conn.Monsters)
        {
            var a = m.Ailments;
            if (a.Idle) continue;
            foreach (var (kind, removed, added, magnitude) in a.Present(now, () => conn.NextBuff++))
            {
                if (removed is ushort r) packets.Add((InstanceEntities.RemoveBuff(id, r), $"0x01c2 monster 0x{id:x} {kind} ends"));
                if (added is ushort b) packets.Add((AilmentBuff(id, b, kind, magnitude), $"0x01c1 monster 0x{id:x} ({m.Label}) {kind} {magnitude}%"));
            }
            double action = m.Mods.Effects["action_speed_cannot_be_reduced_below_base"] > 0 ? 1 : 1 - a.Magnitude(Kind.Chill, now) / 100;
            double walk = m.Mods.Effects["movement_speed_cannot_be_reduced_below_base"] > 0 ? Math.Max(1, action) : action;
            if (walk != a.WalkFactor)
            {
                m.Walk.SetSpeed(m.Walk.Speed / a.WalkFactor * walk, now);
                a.WalkFactor = walk;
            }
            int speed = (int)Math.Round(100 * (action - 1));
            if (speed != a.SentActionSpeed && Stats.Key("virtual_action_speed_+%") is uint key)
            {
                a.SentActionSpeed = speed;
                packets.Add((InstanceEntities.StatsUpdate(id, [(key, speed)]), $"0x01b3 monster 0x{id:x} virtual_action_speed_+% = {speed}"));
            }
        }
        return packets;
    }

    /// <summary>
    /// The ailment buff on a monster (0x1c1): no duration (the server ends it with 0x1c2, so a pause cannot desync it).
    /// All three rows have the +0xff bool, so the reader takes a u16 (+0x82); chilled and shocked are IsRecovery and
    /// carry their one GrantedStats value (action_speed_-%_from_chill, base_damage_taken_+%_from_shock). The magnitude
    /// goes in both; what the client does with +0x82 is not known.
    /// </summary>
    internal static byte[] AilmentBuff(uint id, ushort instance, Kind kind, int magnitude) =>
        InstanceEntities.AddBuff(id, instance, (ushort)(kind + 1), PlayerId, u82: (ushort)magnitude,
            values: kind == Kind.Freeze ? null : [(uint)magnitude]);
}
