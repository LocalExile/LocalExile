using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

/// <summary>Blind inputs captured before an accepted hit chooses its attack/spell and critical result.</summary>
public readonly record struct BlindHitInput(long GlobalChance, long AttackChance, long CriticalChance, int Effect, double Seconds)
{
    static int Saturate(long value) => (int)Math.Clamp(value, (long)int.MinValue, int.MaxValue);
    public static BlindHitInput Capture(IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? own = null)
    {
        long Get(uint key) => (long)player.GetValueOrDefault(key)
            + (own?.GetValueOrDefault(key) ?? 0);
        return Capture(Get);
    }
    public static BlindHitInput Capture(Func<uint, int> get) => new(
        get(Stat.GlobalChanceToBlindOnHitPct), get(Stat.AttacksChanceToBlindOnHitPct),
        get(Stat.PctChanceToBlindOnCriticalStrike), get(Stat.BlindEffectPlusPct), Game.BlindSeconds(get));
    public static BlindHitInput Capture(Func<uint, long> get) => new(
        get(Stat.GlobalChanceToBlindOnHitPct), get(Stat.AttacksChanceToBlindOnHitPct),
        get(Stat.PctChanceToBlindOnCriticalStrike), Saturate(get(Stat.BlindEffectPlusPct)),
        Game.BlindSeconds(name => Saturate(get(name))));
    public int Chance(bool spell, bool critical) => (int)Math.Clamp(GlobalChance
        + (spell ? 0 : AttackChance) + (critical ? CriticalChance : 0), 0, 100);
}

public static partial class Game
{
    static bool BlindImmune(Player conn, IReadOnlyDictionary<uint, int>? player = null)
    {
        uint key = Stat.CannotBeBlinded;
        return (player ?? CharacterStatSources(conn).Stats).GetValueOrDefault(key) != 0;
    }
    static (int Less, double Until) PlayerBlindValue(Player conn, IReadOnlyDictionary<uint, int>? player = null)
    {
        var state = ResourceEffectsOf(conn);
        double now = ResourceEffectsTime(conn);
        var value = state.PlayerBlind.Read(now, conn.PlayerLife == 0);
        if (state.PlayerBlind.NextEnd is double end) StatsChangeAt(end, () => ResourceEffectsTime(conn));
        return value.Less > 0 && BlindImmune(conn, player) ? state.PlayerBlind.Read(now, true) : value;
    }
    public static int PlayerBlindAccuracy(Player conn, int rating, IReadOnlyDictionary<uint, int>? player = null) =>
        TimedBlind.Apply(rating, PlayerBlindValue(conn, player).Less);
    public static int PlayerAccuracy(Player conn, IReadOnlyDictionary<uint, int>? player = null) =>
        PlayerBlindAccuracy(conn, conn.Pools.Accuracy, player);
    public static int PlayerEvasion(Player conn, IReadOnlyDictionary<uint, int>? player = null) =>
        TimedBlind.Apply(PlayerEvasionBase(conn, ResourceEffectsTime(conn)), PlayerBlindValue(conn, player).Less);
    public static int PlayerEvasion(Player conn, double now) =>
        TimedBlind.Apply(PlayerEvasionBase(conn, now), PlayerBlindValue(conn).Less);
    // The steps that read the player's blind.
    internal static void MarkPlayerBlind(Player conn)
    {
        MarkPlayerStats(conn, PlayerStatStep.ClassAttributes);
        MarkPlayerStats(conn, PlayerStatStep.Blind);
        MarkPlayerStats(conn, PlayerStatStep.Recovery);
    }
    static void WithBlind(Player conn, StatMap stats)
    {
        var blind = PlayerBlindValue(conn, stats);
        if (blind.Less > 0 || conn.ResourceEffectsState is ResourceEffectsState state && state.PlayerBlind.Sent.Less > 0)
        { stats[973] = blind.Less > 0 ? 1 : 0; stats[15023] = stats[15024] = -blind.Less; }
    }
    public static bool InflictPlayerBlind(Player conn, double seconds, int effect = 0,
        IReadOnlyDictionary<uint, int>? player = null)
    {
        lock (conn.Monsters)
        {
            var state = ResourceEffectsOf(conn);
            if (!(ResourceEffectsCurrent(conn, state) && conn.PlayerLife > 0 && !conn.LeavingArea && !conn.Paused
                && state.PlayerBlind.Inflict(ResourceEffectsTime(conn), seconds, effect, BlindImmune(conn, player)))) return false;
            MarkPlayerBlind(conn);
            PresentNearby(conn);
            return true;
        }
    }
    static int BlindEffectLess(IReadOnlyDictionary<uint, int> stats) =>
        (int)Math.Clamp(20L * Math.Max(0L, 100L + (Stat.BlindEffectPlusPct is var key ? stats.GetValueOrDefault(key) : 0)), 0, 10000) / 100;
    static (int Less, double Until) MonsterBlindValue(Player conn, uint id, SpawnedMonster target, double at, int smokeLess)
    {
        var owner = ResourceEffectsOf(conn);
        if (!ResourceEffectsCurrent(conn, owner)) return (0, 0);
        var state = owner.TargetBlind(id, target);
        bool immune = target.Dead || target.Life == 0 || target.Mods.Effects[Stat.CannotBeBlinded] != 0;
        var value = state.Read(at, immune);
        if (immune) return (0, 0);
        if (target.RampageBlindUntil > at && value.Less < 20) value = (20, target.RampageBlindUntil);
        return (conn.FlaskGrounds.Blinded(id) || conn.BlindAura.Blinded(id) || PhysicalAegisBlinds(conn, id))
            && smokeLess > value.Less ? (smokeLess, 0) : value;
    }
    public static int MonsterBlindRating(Player conn, uint id, SpawnedMonster target, int rating, double at,
        IReadOnlyDictionary<uint, int>? player = null)
    {
        // The synchronous caller reuses its captured sources. Never build a full player panel for each target.
        int smoke = !conn.FlaskGrounds.Blinded(id) && !conn.BlindAura.Blinded(id) && !PhysicalAegisBlinds(conn, id) ? 0
            : BlindEffectLess(player ?? CharacterStatSources(conn).Stats);
        return TimedBlind.Apply(rating, MonsterBlindValue(conn, id, target, at, smoke).Less);
    }
    public static (int Chance, int Effect, double Seconds) BlindInputs(IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? own = null)
    {
        int Get(uint key) => (int)Math.Clamp((long)player.GetValueOrDefault(key)
            + (own?.GetValueOrDefault(key) ?? 0), int.MinValue, int.MaxValue);
        return (Math.Clamp(Get(Stat.GlobalChanceToBlindOnHitPct), 0, 100), Get(Stat.BlindEffectPlusPct),
            4 * Math.Max(0d, 1 + Get(Stat.BlindDurationPlusPct) / 100d));
    }
    public static bool InflictMonsterBlind(Player conn, uint id, SpawnedMonster target, double at,
        (int Chance, int Effect, double Seconds) input, Random? random = null)
        => InflictMonsterBlind(conn, id, target, at, input.Chance, input.Effect, input.Seconds, random);
    public static bool InflictMonsterBlind(Player conn, uint id, SpawnedMonster target, double at,
        BlindHitInput input, bool spell, bool critical, Random? random = null)
        => InflictMonsterBlind(conn, id, target, at, input.Chance(spell, critical), input.Effect, input.Seconds, random);
    static bool InflictMonsterBlind(Player conn, uint id, SpawnedMonster target, double at, int chance,
        int effect, double seconds, Random? random)
    {
        if (!target.Hostile || target.Dead || target.Life == 0
            || !conn.Monsters.TryGetValue(id, out var registered) || !ReferenceEquals(registered, target)) return false;
        if (chance <= 0 || !CurrentGameRules || conn.LeavingArea || conn.Paused
            || !RandomRolls.Chance(chance, 100, random ?? AreaRng(conn))) return false;
        var state = ResourceEffectsOf(conn);
        if (!(ResourceEffectsCurrent(conn, state) && state.TargetBlind(id, target)
            .Inflict(at, seconds, effect, target.Mods.Effects[Stat.CannotBeBlinded] != 0))) return false;
        target.PresentationChanged?.Invoke();
        return true;
    }
    public static byte[]? BlindPresentation(Player conn, IReadOnlyDictionary<uint, int>? snapshot = null)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameRules || conn.Closed || conn.LeavingArea || conn.RageRetired) return null;
            var state = ResourceEffectsOf(conn);
            if (!ResourceEffectsCurrent(conn, state))
            {
                // Actual death has already retired the state synchronously. Publish primitive cleanup receipts
                // without binding a new live state or acknowledging an obsolete module continuation.
                if (conn.PlayerLife != 0 || conn.RetiredResourceBuffs.Count == 0) return null;
                var cleanup = new List<byte>();
                foreach (var (actor, buff) in conn.RetiredResourceBuffs)
                {
                    cleanup.AddRange(InstanceEntities.RemoveBuff(actor, buff));
                    cleanup.AddRange(InstanceEntities.StatsUpdate(actor, [(973u, 0), (15023u, 0), (15024u, 0)]));
                }
                conn.RetiredResourceBuffs.Clear(); return [.. cleanup];
            }
            double now = ResourceEffectsTime(conn);
            state.CollectRetiredTargets(conn);
            // Each Blind's end shows again (a nearby presentation, PresentIn).
            double next = state.PlayerBlind.NextEnd ?? double.PositiveInfinity;
            foreach (var (_, target) in state.Targets) next = Math.Min(next, target.Blind.NextEnd ?? double.PositiveInfinity);
            foreach (var m in conn.Monsters.Values) if (m.RampageBlindUntil > now) next = Math.Min(next, m.RampageBlindUntil);
            if (next > now && double.IsFinite(next)) PresentIn(conn, next - now + 1e-6);
            bool work = state.PlayerBlind.Read(now).Less > 0 || state.PlayerBlind.Sent.Less > 0 || state.Targets.Count > 0
                || conn.RetiredResourceBuffs.Count > 0 || conn.FlaskGrounds.BlindedMonsters().Any()
                || conn.Monsters.Keys.Any(conn.BlindAura.Blinded)
                || conn.Monsters.Keys.Any(conn.PhysicalAegis.Blind.Blinded)
                || conn.Monsters.Values.Any(m => m.RampageBlindUntil > now);
            if (!work) return null;
            var player = snapshot ?? CharacterPlayerStats(conn); // One fresh snapshot for the whole publication pass, at the gate.
            int smoke = BlindEffectLess(player);
            var bytes = new List<byte>();
            foreach (var (actor, buff) in conn.RetiredResourceBuffs)
            {
                bytes.AddRange(InstanceEntities.RemoveBuff(actor, buff));
                bytes.AddRange(InstanceEntities.StatsUpdate(actor, [(973u, 0), (15023u, 0), (15024u, 0)]));
                if (actor == PlayerId) state.PlayerBlind.Sent = (-1, 0);
                else if (conn.Monsters.TryGetValue(actor, out var target)) state.TargetBlind(actor, target).Sent = (-1, 0);
            }
            conn.RetiredResourceBuffs.Clear();
            void Present(uint actor, TimedBlind blind, (int Less, double Until) value, bool immune, bool isPlayer)
            {
                var own = blind.Read(now, immune);
                bool ownChanged = own != blind.SentOwn, mergedChanged = value != blind.Sent;
                if (!ownChanged && !mergedChanged) return;
                // A weaker source renewed under stronger smoke still owns a new native duration.
                if (ownChanged)
                {
                    if (blind.Buff != 0) bytes.AddRange(InstanceEntities.RemoveBuff(actor, blind.Buff));
                    blind.Buff = 0;
                    if (own.Less > 0) bytes.AddRange(InstanceEntities.AddBuff(actor, blind.Buff = conn.NextBuff++, 179, PlayerId,
                        u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, own.Until - now))));
                    blind.SentOwn = own;
                }
                if (mergedChanged) bytes.AddRange(InstanceEntities.StatsUpdate(actor,
                    [(973u, value.Less > 0 ? 1 : 0), (15023u, -value.Less), (15024u, -value.Less)]));
                if (isPlayer) bytes.AddRange(PlayerStatsOffFrames(conn));
                blind.Sent = value;
            }
            Present(PlayerId, state.PlayerBlind, PlayerBlindValue(conn, player), conn.PlayerLife == 0 || BlindImmune(conn, player), true);
            foreach (var (id, target) in conn.Monsters)
            {
                if (!state.Targets.ContainsKey(id) && target.RampageBlindUntil <= now && !conn.FlaskGrounds.Blinded(id)
                    && !conn.BlindAura.Blinded(id) && !conn.PhysicalAegis.Blind.Blinded(id)) continue;
                var value = MonsterBlindValue(conn, id, target, now, smoke);
                Present(id, state.TargetBlind(id, target), value,
                    target.Dead || target.Life == 0 || target.Mods.Effects[Stat.CannotBeBlinded] != 0, false);
            }
            return bytes.Count == 0 ? null : [.. bytes];
        }
    }
    public static Task SendBlind(Player conn) =>
        Send(conn, [], null, atGate: () => BlindPresentation(conn));

    public const uint VsBlindedCriticalStat = Stat.CriticalStrikeChancePlusPctVsBlindedEnemies;
    public const uint FromBlindedDamageTakenStat = Stat.DamageTakenPlusPctFromBlindedEnemies;

    public static bool Blinded(Player conn, uint monster) => Blinded(conn, monster, ResourceEffectsTime(conn));

    public static bool Blinded(Player conn, uint monster, double at) =>
        conn.Monsters.TryGetValue(monster, out var target)
        && !target.Dead && target.Life > 0 && target.Mods.Effects[Stat.CannotBeBlinded] == 0
        && (conn.FlaskGrounds.Blinded(monster) || conn.BlindAura.Blinded(monster) || PhysicalAegisBlinds(conn, monster)
            || target.RampageBlindUntil > at
            || conn.ResourceEffectsState is ResourceEffectsState owner && owner.Targets.TryGetValue(monster, out var value)
                && ReferenceEquals(value.Target, target) && value.Blind.Read(at).Less > 0);

    public static int UnderBlind(Player conn, uint monster, int value) =>
        conn.Monsters.TryGetValue(monster, out var target) ? MonsterBlindRating(conn, monster, target, value, ResourceEffectsTime(conn)) : value;

    static int StatOf(IReadOnlyDictionary<uint, int> stats, uint name) =>
        stats.GetValueOrDefault(name);

    // critical_strike_chance_+%_vs_blinded_enemies (PoB "against Blinded Enemies": ActorCondition enemy Blinded) is an
    // increase: a hit's chance against a Blinded target is recomputed from its base with it in the increased sum, never
    // the final chance scaled. Each site computes that second chance once (null without the stat) and picks it here.
    public static int CriticalChanceOn(Player conn, uint target, int chance, int? vsBlinded, GroundCritical? vsGround = null) =>
        Math.Clamp((GroundCriticalOn(conn, target, vsGround) is int ground ? ground
        : vsBlinded is int blinded && Blinded(conn, target) ? blinded : chance)
        + (conn.Monsters.GetValueOrDefault(target)?.Curses[Stat.EnemyAdditionalCriticalStrikeChanceAgainstSelf] ?? 0), 0, 10000);

    // The default attack's (and an ordinary bow's) chance against a Blinded target: the main hand's base with the pooled
    // attack increases and the vs-Blinded one, as Pools.AttackCriticalStrikeChance is built. A release keeps the
    // unprohibited chance, as the bow's ordinary one; the roll checks the prohibitions.
    public static int? AttackCriticalChanceVsBlinded(Player conn, IReadOnlyDictionary<uint, int> player, bool release = false) =>
        StatOf(player, VsBlindedCriticalStat) is not (not 0 and var extra)
            ? null : AttackCriticalChanceWith(conn, player, extra, release);

    /// <summary>A hit's chances against a target on the player's grounds, worked out once from the chance's base: on
    /// profane ground (+100% increased, PoB's SelfCritChance INC for enemies on Profane Ground), on consecrated ground with
    /// Bottled Faith's increase (null without it), and on both (the increases add). Picked per target at the hit.</summary>
    public readonly record struct GroundCritical(int? Profane, int? Consecrated, int? Both,
        int? BlindedProfane, int? BlindedConsecrated, int? BlindedBoth)
    {
        public GroundCritical(int? profane, int? consecrated, int? both) : this(profane, consecrated, both, null, null, null) { }
        public void Deconstruct(out int? profane, out int? consecrated, out int? both) =>
            (profane, consecrated, both) = (Profane, Consecrated, Both);
    }

    static GroundCritical GroundCriticalWith(Player conn, Func<long, int?> with, int blinded)
    {
        int consecrated = UtilityFlaskStat(conn, FlaskGrounds.VsConsecratedCriticalStat);
        return new(with(FlaskGrounds.ProfaneCritIncrease), consecrated > 0 ? with(consecrated) : null,
            consecrated > 0 ? with(FlaskGrounds.ProfaneCritIncrease + (long)consecrated) : null,
            blinded != 0 ? with(FlaskGrounds.ProfaneCritIncrease + (long)blinded) : null,
            blinded != 0 && consecrated > 0 ? with(consecrated + (long)blinded) : null,
            blinded != 0 && consecrated > 0 ? with(FlaskGrounds.ProfaneCritIncrease + (long)consecrated + blinded) : null);
    }

    static int? GroundCriticalOn(Player conn, uint target, GroundCritical? vsGround)
    {
        if (vsGround is not { } g) return null;
        bool profane = conn.FlaskGrounds.Profane(target);
        bool consecrated = g.Consecrated is not null && conn.Monsters.ContainsKey(target)
            && InFlaskGround(conn, FlaskGrounds.Kind.ConsecratedGround, target);
        if (Blinded(conn, target))
            return profane && consecrated ? g.BlindedBoth ?? g.Both
                : profane ? g.BlindedProfane ?? g.Profane : consecrated ? g.BlindedConsecrated ?? g.Consecrated : null;
        return profane && consecrated ? g.Both : profane ? g.Profane : consecrated ? g.Consecrated : null;
    }

    // The default attack's (and an ordinary bow's) chances against a target on the player's grounds (GroundCritical).
    public static GroundCritical AttackCriticalChanceVsGround(Player conn, IReadOnlyDictionary<uint, int> player, bool release = false) =>
        GroundCriticalWith(conn, extra => AttackCriticalChanceWith(conn, player, extra, release), StatOf(player, VsBlindedCriticalStat));

    static int? AttackCriticalChanceWith(Player conn, IReadOnlyDictionary<uint, int> player, long extra, bool release)
    {
        var weapon = WeaponProfileFor(conn, activeSet: true);
        return AttackBaseCriticalStrike.Chance(!weapon.Unarmed, weapon.Critical, conn.Pools.CriticalStrikeChancePct + extra,
            name => release && IsCriticalProhibition(name) ? 0 : StatOf(player, name));
    }

    // A supported gem's chance against a Blinded target (its attack or spell base, the pooled and own increases).
    public static int? SupportedCriticalChanceVsBlinded(Player conn, ushort skillId, bool release) =>
        conn.Pools.VsBlindedCriticalPct is not 0 and var extra ? SupportedCriticalChanceFor(conn, skillId, release, extra) : null;

    // A passive-granted spell's chances against a target on the player's grounds, from its own chance function.
    public static GroundCritical SpellCriticalChanceVsGround(Player conn, Func<long, int> with) =>
        SpellCriticalChanceVsGround(conn, with, 0);

    public static GroundCritical SpellCriticalChanceVsGround(Player conn, Func<long, int> with, int blinded) =>
        GroundCriticalWith(conn, extra => with(extra), blinded);

    // A supported gem's chances against a target on the player's grounds (GroundCritical).
    public static GroundCritical SupportedCriticalChanceVsGround(Player conn, ushort skillId, bool release) =>
        GroundCriticalWith(conn, extra => SupportedCriticalChanceFor(conn, skillId, release, extra), conn.Pools.VsBlindedCriticalPct);

    // damage_taken_+%_from_blinded_enemies (PoB DamageTaken INC, "from Blinded Enemies") for a Blinded monster's hits, where
    // it joins the other increased damage taken (shock), and the ticks of the ignite its hit inflicted (Advance's takenFrom).
    public static int DamageTakenFromBlindedPct(Player conn, uint monster, IReadOnlyDictionary<uint, int> player) =>
        Blinded(conn, monster) ? StatOf(player, FromBlindedDamageTakenStat) : 0;
}
