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
        get(Stat.PctChanceToBlindOnCriticalStrike), get(Stat.BlindEffectPlusPct), Game.BlindSeconds(get))
        {
            GlobalVsBleedingChance = get(Stat.GlobalChanceToBlindOnHitPctVsBleedingEnemies),
            AttackVsBleedingChance = get(Stat.AttackChanceToBlindOnHitPctVsBleedingEnemies),
        };
    public static BlindHitInput Capture(Func<uint, long> get) => new(
        get(Stat.GlobalChanceToBlindOnHitPct), get(Stat.AttacksChanceToBlindOnHitPct),
        get(Stat.PctChanceToBlindOnCriticalStrike), Saturate(get(Stat.BlindEffectPlusPct)),
        Game.BlindSeconds(name => Saturate(get(name))))
        {
            GlobalVsBleedingChance = get(Stat.GlobalChanceToBlindOnHitPctVsBleedingEnemies),
            AttackVsBleedingChance = get(Stat.AttackChanceToBlindOnHitPctVsBleedingEnemies),
        };
    public long GlobalVsBleedingChance { get; init; }
    public long AttackVsBleedingChance { get; init; }
    public int Chance(bool spell, bool critical) => Chance(spell, critical, bleeding: false);

    public int Chance(bool spell, bool critical, bool bleeding) => (int)Math.Clamp(GlobalChance
        + (spell ? 0 : AttackChance) + (critical ? CriticalChance : 0)
        + (bleeding ? GlobalVsBleedingChance + (spell ? 0 : AttackVsBleedingChance) : 0), 0, 100);
}

public static partial class Game
{
    static bool BlindImmune(Player player, IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        uint key = Stat.CannotBeBlinded;
        return (playerStats ?? StatSourceValues(player)).GetValueOrDefault(key) != 0;
    }
    static (int Less, double Until) PlayerBlindValue(Player player, IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        var state = ResourceEffectsOf(player);
        double now = ResourceEffectsTime(player);
        var value = state.PlayerBlind.Read(now, player.PlayerLife == 0);
        if (state.PlayerBlind.NextEnd is double end) StatsChangeAt(end, () => ResourceEffectsTime(player));
        var stats = playerStats ?? StatSourceValues(player);
        bool selfBlinded = player.PlayerLife > 0 && stats.GetValueOrDefault(Stat.KeystoneBlindMonk) != 0;
        if ((value.Less > 0 || selfBlinded) && BlindImmune(player, stats))
            return state.PlayerBlind.Read(now, true);
        return selfBlinded && value.Less < 20 ? (20, double.PositiveInfinity) : value;
    }
    static int PlayerBlindPenalty(Player player, IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        int less = PlayerBlindValue(player, playerStats).Less;
        return less != 0 && (playerStats ?? StatSourceValues(player)).GetValueOrDefault(Stat.UnaffectedByBlind) != 0
            ? 0 : less;
    }

    public static int PlayerBlindAccuracy(Player player, int rating, IReadOnlyDictionary<uint, int>? playerStats = null) =>
        TimedBlind.Apply(rating, PlayerBlindPenalty(player, playerStats));
    public static int PlayerAccuracy(Player player, IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        int blindLess = PlayerBlindPenalty(player, playerStats);
        if (blindLess == 0) return player.Pools.Accuracy;
        // BaseStats calls this while building the final dictionary; read raw sources when that dictionary is absent.
        uint moreKey = PlayerAccuracyModifiers.BootsMore;
        int more = playerStats is not null ? playerStats.GetValueOrDefault(moreKey)
            : StatSourceValues(player).GetValueOrDefault(moreKey);
        return PlayerAccuracyModifiers.Unselected(player.Pools, more, blindLess);
    }
    public static int PlayerEvasion(Player player, IReadOnlyDictionary<uint, int>? playerStats = null) =>
        TimedBlind.Apply(PlayerEvasionBase(player, ResourceEffectsTime(player)), PlayerBlindPenalty(player, playerStats));
    public static int PlayerEvasion(Player player, double now) =>
        TimedBlind.Apply(PlayerEvasionBase(player, now), PlayerBlindPenalty(player));
    // The steps that read the player's blind.
    internal static void MarkPlayerBlind(Player player)
    {
        MarkPlayerStats(player, PlayerStatStep.ClassAttributes);
        MarkPlayerStats(player, PlayerStatStep.Blind);
        MarkPlayerStats(player, PlayerStatStep.Recovery);
    }
    static void WithBlind(Player player, StatMap stats)
    {
        var blind = PlayerBlindValue(player, stats);
        if (blind.Less > 0 || player.ResourceEffectsState is ResourceEffectsState state && state.PlayerBlind.Sent.Less > 0)
        {
            stats[973] = blind.Less > 0 ? 1 : 0;
            stats[15023] = stats[15024] = stats.GetValueOrDefault(Stat.UnaffectedByBlind) != 0 ? 0 : -blind.Less;
        }
    }
    public static bool InflictPlayerBlind(Player player, double seconds, int effect = 0,
        IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        var state = ResourceEffectsOf(player);
        if (!(ResourceEffectsCurrent(player, state) && player.PlayerLife > 0 && !player.LeavingArea && !player.Paused
            && state.PlayerBlind.Inflict(ResourceEffectsTime(player), seconds, effect, BlindImmune(player, playerStats)))) return false;
        MarkPlayerBlind(player);
        PresentNearby(player);
        return true;
    }
    static int BlindEffectLess(IReadOnlyDictionary<uint, int> stats) =>
        (int)Math.Clamp(20L * Math.Max(0L, 100L + (Stat.BlindEffectPlusPct is var key ? stats.GetValueOrDefault(key) : 0)), 0, 10000) / 100;
    static bool HasAreaBlind(Player player, uint id) =>
        player.FlaskGrounds.Blinded(id) || player.BlindAura.Blinded(id) || PhysicalAegisBlinds(player, id);

    static (int Less, double Until) MonsterBlindValue(Player player, uint id, SpawnedMonster target, double at, int smokeLess,
        bool? auraBlinded = null)
    {
        var owner = ResourceEffectsOf(player);
        if (!ResourceEffectsCurrent(player, owner)) return (0, 0);
        bool immune = target.Dead || target.Life == 0 || target.Mods.Effects[Stat.CannotBeBlinded] != 0;
        if (immune)
        {
            if (owner.Targets.TryGetValue(id, out var existing) && ReferenceEquals(existing.Target, target))
                existing.Blind.Read(at, true);
            return (0, 0);
        }
        var value = owner.Targets.TryGetValue(id, out var entry) && ReferenceEquals(entry.Target, target)
            ? entry.Blind.Read(at, false) : (0, 0);
        if (target.RampageBlindUntil > at && value.Less < 20) value = (20, target.RampageBlindUntil);
        bool external = auraBlinded ?? HasAreaBlind(player, id);
        int areaLess = Math.Max(external ? smokeLess : 0, SandstormBlindLess(target));
        return areaLess > value.Less ? (areaLess, 0) : value;
    }
    public static int MonsterBlindRating(Player player, uint id, SpawnedMonster target, int rating, double at,
        IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        // The synchronous caller reuses its captured sources. Never build a full player panel for each target.
        bool auraBlinded = HasAreaBlind(player, id);
        int smoke = !auraBlinded ? 0 : BlindEffectLess(playerStats ?? StatSourceValues(player));
        return TimedBlind.Apply(rating, MonsterBlindValue(player, id, target, at, smoke, auraBlinded).Less);
    }
    public static (int Chance, int Effect, double Seconds) BlindInputs(IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? own = null)
    {
        int Get(uint key) => (int)Math.Clamp((long)player.GetValueOrDefault(key)
            + (own?.GetValueOrDefault(key) ?? 0), int.MinValue, int.MaxValue);
        return (Math.Clamp(Get(Stat.GlobalChanceToBlindOnHitPct), 0, 100), Get(Stat.BlindEffectPlusPct),
            4 * Math.Max(0d, 1 + Get(Stat.BlindDurationPlusPct) / 100d));
    }

    static void BlindOnElementalSkillUseLocked(Player player, int effect, double now, IReadOnlyDictionary<uint, int> sources)
    {
        int chance = sources.GetValueOrDefault(Stat.ElementalSkillChanceToBlindNearbyEnemiesPct);
        if (chance <= 0) return;
        var skills = SkillsFor(player);
        if (!(skills.HasType(effect, "Fire") || skills.HasType(effect, "Cold") || skills.HasType(effect, "Lightning"))
            || !RandomRolls.Chance(chance, 100, AreaRng(player))) return;
        const int radius = 30;
        var at = player.Walk.Position(now);
        var input = (100, sources.GetValueOrDefault(Stat.BlindEffectPlusPct), BlindSeconds(sources.GetValueOrDefault));
        foreach (var (id, enemy) in player.Area.MonstersNear(at, radius))
            if (Dist(enemy.Walk.Position(now), at) <= radius) InflictMonsterBlind(player, id, enemy, now, input);
    }
    public static bool InflictMonsterBlind(Player player, uint id, SpawnedMonster target, double at,
        (int Chance, int Effect, double Seconds) input, Random? random = null)
        => InflictMonsterBlind(player, id, target, at, input.Chance, input.Effect, input.Seconds, random);
    public static bool InflictMonsterBlind(Player player, uint id, SpawnedMonster target, double at,
        BlindHitInput input, bool spell, bool critical, Random? random = null)
        => InflictMonsterBlind(player, id, target, at, input.Chance(spell, critical,
                (input.GlobalVsBleedingChance > 0 || !spell && input.AttackVsBleedingChance > 0) && target.Bleeds.IgnitedAt(at)),
            input.Effect, input.Seconds, random);
    static bool InflictMonsterBlind(Player player, uint id, SpawnedMonster target, double at, int chance,
        int effect, double seconds, Random? random)
    {
        if (!target.Hostile || target.Dead || target.Life == 0
            || !player.Area.Monsters.TryGetValue(id, out var registered) || !ReferenceEquals(registered, target)) return false;
        if (chance <= 0 || !CurrentGameRules || player.LeavingArea || player.Paused
            || !RandomRolls.Chance(chance, 100, random ?? AreaRng(player))) return false;
        var state = ResourceEffectsOf(player);
        if (!(ResourceEffectsCurrent(player, state) && state.TargetBlind(id, target)
            .Inflict(at, seconds, effect, target.Mods.Effects[Stat.CannotBeBlinded] != 0))) return false;
        target.PresentationChanged?.Invoke();
        return true;
    }
    public static byte[]? BlindPresentation(Player player, IReadOnlyDictionary<uint, int>? snapshot = null)
    {
        if (!CurrentGameRules || player.Closed || player.LeavingArea || player.RageRetired) return null;
        var state = ResourceEffectsOf(player);
        if (!ResourceEffectsCurrent(player, state))
        {
            // Actual death has already retired the state synchronously. Publish primitive cleanup receipts
            // without binding a new live state or acknowledging an obsolete module continuation.
            if (player.PlayerLife != 0 || player.RetiredResourceBuffs.Count == 0) return null;
            var cleanup = new List<byte>();
            foreach (var (actor, buff) in player.RetiredResourceBuffs)
            {
                player.Area.Buffs.Clear(actor, 179, player.Id);
                SetStats(player, actor, [(973u, 0), (15023u, 0), (15024u, 0)]);
            }
            player.RetiredResourceBuffs.Clear(); return [.. cleanup];
        }
        double now = ResourceEffectsTime(player);
        state.CollectRetiredTargets(player);
        bool hasFlaskBlind = player.FlaskGrounds.HasBlindedMonsters;
        bool hasAuraBlind = player.BlindAura.Any || player.ProjectileRuntime is ProjectileSession { HasBladestormBlind: true };
        bool hasAegisBlind = player.PhysicalAegis.Blind.Any;
        bool hasRampageBlind = player.StandingRampageWorld is { Blinds.Count: > 0 };
        // Each Blind's end shows again (a nearby presentation, PresentIn).
        double next = state.PlayerBlind.NextEnd ?? double.PositiveInfinity;
        foreach (var (_, target) in state.Targets) next = Math.Min(next, target.Blind.NextEnd ?? double.PositiveInfinity);
        if (hasRampageBlind)
            foreach (var (until, _) in player.StandingRampageWorld!.Blinds.Values)
                if (until > now) next = Math.Min(next, until);
        if (next > now && double.IsFinite(next)) PresentIn(player, next - now + 1e-6);
        bool work = state.PlayerBlind.Read(now).Less > 0 || state.PlayerBlind.Sent.Less > 0 || state.Targets.Count > 0
            || player.RetiredResourceBuffs.Count > 0 || hasFlaskBlind || hasAuraBlind || hasAegisBlind || hasRampageBlind;
        if (!work) return null;
        var playerStats = snapshot ?? PlayerStatValues(player); // One fresh snapshot for the whole publication pass, at the gate.
        int smoke = BlindEffectLess(playerStats);
        var bytes = new List<byte>();
        foreach (var (actor, buff) in player.RetiredResourceBuffs)
        {
            player.Area.Buffs.Clear(actor, 179, player.Id);
            SetStats(player, actor, [(973u, 0), (15023u, 0), (15024u, 0)]);
            if (actor == player.Id) state.PlayerBlind.Sent = (-1, 0);
            else if (player.Area.Monsters.TryGetValue(actor, out var target)) state.TargetBlind(actor, target).Sent = (-1, 0);
        }
        player.RetiredResourceBuffs.Clear();
        void Present(uint actor, TimedBlind blind, (int Less, double Until) value, bool immune, bool isPlayer)
        {
            var own = blind.Read(now, immune);
            bool ownChanged = own != blind.SentOwn, mergedChanged = value != blind.Sent;
            if (!ownChanged && !mergedChanged) return;
            // A weaker source renewed under stronger smoke still owns a new native duration.
            if (ownChanged)
            {
                blind.Buff = (ushort)(own.Less > 0 ? 1 : 0);
                if (own.Less > 0) player.Area.Buffs.Set(actor, new(179, player.Id, U68: Buff.Seconds(own.Until - now)));
                else player.Area.Buffs.Clear(actor, 179, player.Id);
                blind.SentOwn = own;
            }
            if (mergedChanged) SetStats(player, actor, [(973u, value.Less > 0 ? 1 : 0), (15023u, -value.Less), (15024u, -value.Less)]);
            if (isPlayer) bytes.AddRange(PlayerStatsOffFrames(player));
            blind.Sent = value;
        }
        Present(player.Id, state.PlayerBlind, PlayerBlindValue(player, playerStats), player.PlayerLife == 0 || BlindImmune(player, playerStats), true);
        if (!hasFlaskBlind && !hasAuraBlind && !hasAegisBlind && !hasRampageBlind)
        {
            foreach (var (id, entry) in state.Targets)
            {
                if (!player.Area.Monsters.TryGetValue(id, out var target) || !ReferenceEquals(entry.Target, target)) continue;
                var value = MonsterBlindValue(player, id, target, now, smoke, auraBlinded: false);
                Present(id, entry.Blind, value,
                    target.Dead || target.Life == 0 || target.Mods.Effects[Stat.CannotBeBlinded] != 0, false);
            }
        }
        else
        {
            foreach (var (id, target) in player.Area.Monsters)
            {
                if (!state.Targets.ContainsKey(id) && target.RampageBlindUntil <= now
                    && SandstormBlindLess(target) == 0 && !HasAreaBlind(player, id)) continue;
                var value = MonsterBlindValue(player, id, target, now, smoke);
                Present(id, state.TargetBlind(id, target), value,
                    target.Dead || target.Life == 0 || target.Mods.Effects[Stat.CannotBeBlinded] != 0, false);
            }
        }
        return bytes.Count == 0 ? null : [.. bytes];
    }
    public static Task SendBlind(Player player) =>
        Send(player, [], null, atGate: () => BlindPresentation(player));

    public const uint VsBlindedCriticalStat = Stat.CriticalStrikeChancePlusPctVsBlindedEnemies;
    public const uint FromBlindedDamageTakenStat = Stat.DamageTakenPlusPctFromBlindedEnemies;

    public static bool Blinded(Player player, uint monster) => Blinded(player, monster, ResourceEffectsTime(player));

    public static bool Blinded(Player player, uint monster, double at) =>
        player.Area.Monsters.TryGetValue(monster, out var target)
        && !target.Dead && target.Life > 0 && target.Mods.Effects[Stat.CannotBeBlinded] == 0
        && (SandstormBlindLess(target) > 0 || HasAreaBlind(player, monster)
            || target.RampageBlindUntil > at
            || player.ResourceEffectsState is ResourceEffectsState owner && owner.Targets.TryGetValue(monster, out var value)
                && ReferenceEquals(value.Target, target) && value.Blind.Read(at).Less > 0);

    public static int UnderBlind(Player player, uint monster, int value) =>
        player.Area.Monsters.TryGetValue(monster, out var target) ? MonsterBlindRating(player, monster, target, value, ResourceEffectsTime(player)) : value;

    static int StatOf(IReadOnlyDictionary<uint, int> stats, uint name) =>
        stats.GetValueOrDefault(name);

    // critical_strike_chance_+%_vs_blinded_enemies (PoB "against Blinded Enemies": ActorCondition enemy Blinded) is an
    // increase: a hit's chance against a Blinded target is recomputed from its base with it in the increased sum, never
    // the final chance scaled. Each site computes that second chance once (null without the stat) and picks it here.
    public static int CriticalChanceOn(Player player, uint target, int chance, int? vsBlinded, GroundCritical? vsGround = null) =>
        Math.Clamp((GroundCriticalOn(player, target, vsGround) is int ground ? ground
        : vsBlinded is int blinded && Blinded(player, target) ? blinded : chance)
        + (player.Area.Monsters.GetValueOrDefault(target) is { } m
            ? m.Curses[Stat.EnemyAdditionalCriticalStrikeChanceAgainstSelf] + BrittleCriticalChance(m, ProjectileWorldTime(player)) : 0),
        0, 10000);

    // The default attack's (and an ordinary bow's) chance against a Blinded target: the main hand's base with the pooled
    // attack increases and the vs-Blinded one, as Pools.AttackCriticalStrikeChance is built. A release keeps the
    // unprohibited chance, as the bow's ordinary one; the roll checks the prohibitions.
    // hand: the swinging hand of a dual-wielded use (AttackHands), whose weapon base replaces the main hand's.
    public static int? AttackCriticalChanceVsBlinded(Player player, IReadOnlyDictionary<uint, int> playerStats, bool release = false,
        AttackHand hand = AttackHand.Main) =>
        StatOf(playerStats, VsBlindedCriticalStat) is not (not 0 and var extra)
            ? null : AttackCriticalChanceWith(player, playerStats, extra, release, hand);

    /// <summary>A hit's chances against a target on ground effects, worked out once from the chance's base: on
    /// profane ground (+100% increased, PoB's SelfCritChance INC for enemies on Profane Ground), on consecrated ground with
    /// Bottled Faith's increase (null without it), and on both (the increases add). Picked per target at the hit.</summary>
    public readonly record struct GroundCritical(int? Profane, int? Consecrated, int? Both,
        int? BlindedProfane, int? BlindedConsecrated, int? BlindedBoth)
    {
        public GroundCritical(int? profane, int? consecrated, int? both) : this(profane, consecrated, both, null, null, null) { }
        public void Deconstruct(out int? profane, out int? consecrated, out int? both) =>
            (profane, consecrated, both) = (Profane, Consecrated, Both);
    }

    static GroundCritical GroundCriticalWith(Player player, Func<long, int?> with, int blinded)
    {
        int consecrated = UtilityFlaskStat(player, FlaskGrounds.VsConsecratedCriticalStat);
        return new(with(FlaskGrounds.ProfaneCritIncrease), consecrated > 0 ? with(consecrated) : null,
            consecrated > 0 ? with(FlaskGrounds.ProfaneCritIncrease + (long)consecrated) : null,
            blinded != 0 ? with(FlaskGrounds.ProfaneCritIncrease + (long)blinded) : null,
            blinded != 0 && consecrated > 0 ? with(consecrated + (long)blinded) : null,
            blinded != 0 && consecrated > 0 ? with(FlaskGrounds.ProfaneCritIncrease + (long)consecrated + blinded) : null);
    }

    static int? GroundCriticalOn(Player player, uint target, GroundCritical? vsGround)
    {
        if (vsGround is not { } g) return null;
        bool profane = player.FlaskGrounds.Profane(target);
        bool consecrated = g.Consecrated is not null && EnemyOnConsecratedGround(player, target);
        if (Blinded(player, target))
            return profane && consecrated ? g.BlindedBoth ?? g.Both
                : profane ? g.BlindedProfane ?? g.Profane : consecrated ? g.BlindedConsecrated ?? g.Consecrated : null;
        return profane && consecrated ? g.Both : profane ? g.Profane : consecrated ? g.Consecrated : null;
    }

    // The default attack's (and an ordinary bow's) chances against a target on the player's grounds (GroundCritical).
    public static GroundCritical AttackCriticalChanceVsGround(Player player, IReadOnlyDictionary<uint, int> playerStats, bool release = false,
        AttackHand hand = AttackHand.Main) =>
        GroundCriticalWith(player, extra => AttackCriticalChanceWith(player, playerStats, extra, release, hand), StatOf(playerStats, VsBlindedCriticalStat));

    static int? AttackCriticalChanceWith(Player player, IReadOnlyDictionary<uint, int> playerStats, long extra, bool release,
        AttackHand hand)
    {
        var weapon = WeaponProfileFor(player, activeSet: true);
        return AttackBaseCriticalStrike.Chance(!weapon.Unarmed,
            hand == AttackHand.Main ? weapon.Critical : HandWeaponCritical(player, hand, playerStats),
            player.Pools.CriticalStrikeChancePct + extra,
            name => release && IsCriticalProhibition(name) ? 0 : StatOf(playerStats, name));
    }

    // A supported gem's chance against a Blinded target (its attack or spell base, the pooled and own increases).
    public static int? SupportedCriticalChanceVsBlinded(Player player, ushort skillId, bool release, int? weaponCritical = null) =>
        SupportedCriticalChanceVsBlinded(player, skillId, release, weaponCritical, 0);

    static int? SupportedCriticalChanceVsBlinded(Player player, ushort skillId, bool release,
        int? weaponCritical, long extraIncrease) =>
        player.Pools.VsBlindedCriticalPct is not 0 and var extra
            ? SupportedCriticalChanceFor(player, skillId, release, extra + extraIncrease, weaponCritical) : null;

    // A passive-granted spell's chances against a target on the player's grounds, from its own chance function.
    public static GroundCritical SpellCriticalChanceVsGround(Player player, Func<long, int> with) =>
        SpellCriticalChanceVsGround(player, with, 0);

    public static GroundCritical SpellCriticalChanceVsGround(Player player, Func<long, int> with, int blinded) =>
        GroundCriticalWith(player, extra => with(extra), blinded);

    // A supported gem's chances against a target on the player's grounds (GroundCritical).
    public static GroundCritical SupportedCriticalChanceVsGround(Player player, ushort skillId, bool release, int? weaponCritical = null) =>
        SupportedCriticalChanceVsGround(player, skillId, release, weaponCritical, 0);

    static GroundCritical SupportedCriticalChanceVsGround(Player player, ushort skillId, bool release,
        int? weaponCritical, long extraIncrease) =>
        GroundCriticalWith(player, extra => SupportedCriticalChanceFor(player, skillId, release, extra + extraIncrease, weaponCritical),
            player.Pools.VsBlindedCriticalPct);

    // damage_taken_+%_from_blinded_enemies (PoB DamageTaken INC, "from Blinded Enemies") for a Blinded monster's hits, where
    // it joins the other increased damage taken (shock), and the ticks of the ignite its hit inflicted (Advance's takenFrom).
    public static int DamageTakenFromBlindedPct(Player player, uint monster, IReadOnlyDictionary<uint, int> playerStats) =>
        Blinded(player, monster) ? StatOf(playerStats, FromBlindedDamageTakenStat) : 0;
}
