using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public const string CatStealthCryEffect = "CatsStealthTriggeredIntimidatingCry";
    public const ushort IntimidatingCryBuffRow = 1035;
    // BuffDefinitions row 1579 (Invisible=1): client HUD widget (0x1401ec060 / 0x1401e5b30) and skill-bar icon
    // badge (0x140ad1143) match ActiveSkill (skillId, actionHash) and display Stacks as remaining exerted attacks.
    public const ushort EmpoweredAttacksBuffRow = 1579;
    const ushort IntimidatingExertTag = 1, SeismicExertTag = 2, AncestralExertTag = 3,
        RallyingExertTag = 4, InfernalExertTag = 5, BattlemagesExertTag = 6;
    public sealed class CatCrySource(WornGrantedSkills.Grant grant, double since)
    {
        public readonly WornGrantedSkills.Grant Grant = grant;
        public readonly double Since = since;
    }
    public sealed record CatCryLoss(object Rules, int Epoch, CatCrySource Source)
    {
        public bool Consumed;
        internal uint LifeCost;
    }
    sealed record CryGift(InstancePresence Instance, double Until, int Movement);
    sealed class CryRuntime
    {
        internal object? Rules;
        internal int Epoch;
        internal readonly Dictionary<WornGrantedSkills.Grant, CatCrySource> Sources = [];
        internal readonly Queue<CatCryLoss> Pending = [];
        internal readonly ConcurrentQueue<CryGift> Inbox = new();
        internal readonly List<CryGift> Gifts = [];
        internal int Exerts, SeismicExerts, SeismicArea, AncestralExerts, AncestralTargets, PresentedMovement;
        internal int RallyingExerts, RallyingDamage;
        internal int InfernalExerts, BattlemagesExerts;
        internal (ushort Id, ushort Action) IntimidatingDisplay, SeismicDisplay, AncestralDisplay,
            RallyingDisplay, InfernalDisplay, BattlemagesDisplay;
        internal CombustSource? Combust;
        internal BattlemagesCrySource? BattlemagesSource;
        internal ushort Buff;
    }
    static readonly Store<Player, CryRuntime> CryRuntimes = new();
    static readonly RuleTable<SpawnedMonster, List<CryGift>> MinionCryGifts = new();

    static CryRuntime CryOf(Player player)
    {
        var state = CryRuntimes.GetOrCreateValue(player);
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != player.ProjectileEpoch)
        {
            bool initialized = state.Rules is not null;
            state.Rules = CombatLifetime;
            state.Epoch = player.ProjectileEpoch;
            state.Sources.Clear(); state.Pending.Clear(); state.Gifts.Clear();
            state.Exerts = state.SeismicExerts = state.AncestralExerts = state.RallyingExerts = 0;
            state.InfernalExerts = state.BattlemagesExerts = 0;
            state.IntimidatingDisplay = state.SeismicDisplay = state.AncestralDisplay = default;
            state.RallyingDisplay = state.InfernalDisplay = state.BattlemagesDisplay = default;
            state.Combust = null;
            state.BattlemagesSource = null;
            if (initialized) while (state.Inbox.TryDequeue(out _)) { }
        }
        return state;
    }

    static (ushort Id, ushort Action) ResolveWarcryDisplaySkill(Player player, ushort skillId, string effectId)
    {
        var skills = SkillsFor(player);
        if (skillId != 0)
        {
            int effect = SupportedSkill(player, skillId).Effect;
            if (effect >= 0 && skills.ActionHash(effect) is ushort hash and not 0)
                return (skillId, hash);
        }
        foreach (var gem in GemSkillEffects(player))
            if (skills.EffectId(gem.Effect) == effectId && skills.ActionHash(gem.Effect) is ushort hash and not 0)
                return (gem.Id, hash);
        return default;
    }

    static void SyncExertedAttackBuffsLocked(Player player)
    {
        var cry = CryOf(player);
        void Sync(ushort tag, int count, (ushort Id, ushort Action) skill)
        {
            if (count > 0 && skill.Id != 0 && skill.Action != 0)
                player.Area.Buffs.Set(player.Id, new(EmpoweredAttacksBuffRow, player.Id,
                    SourceSkill: skill.Id, ActiveSkill: skill, Stacks: (ushort)Math.Min(count, ushort.MaxValue), Tag: tag));
            else
                player.Area.Buffs.Clear(player.Id, EmpoweredAttacksBuffRow, player.Id, tag: tag);
        }
        Sync(IntimidatingExertTag, cry.Exerts, cry.IntimidatingDisplay);
        Sync(SeismicExertTag, cry.SeismicExerts, cry.SeismicDisplay);
        Sync(AncestralExertTag, cry.AncestralExerts, cry.AncestralDisplay);
        Sync(RallyingExertTag, cry.RallyingExerts, cry.RallyingDisplay);
        Sync(InfernalExertTag, cry.InfernalExerts, cry.InfernalDisplay);
        Sync(BattlemagesExertTag, cry.BattlemagesExerts, cry.BattlemagesDisplay);
    }

    static void ReconcileCatCrySourcesLocked(Player player, double now)
    {
        var state = CryOf(player);
        var live = !CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0
            ? [] : AllWornGrantedSkillsOf(player).Where(g => g.EffectId == CatStealthCryEffect && !g.Support
                && !IsInactive(player, g.Host) && LeagueFor(player).Rules.AllowsGemEffect(g.EffectId)).ToArray();
        var wanted = live.ToHashSet();
        foreach (var old in state.Sources.Keys.Where(g => !wanted.Contains(g)).ToArray()) state.Sources.Remove(old);
        foreach (var grant in live) state.Sources.TryAdd(grant, new(grant, now));
    }

    static void QueueCatStealthLossLocked(Player player, double at, double now)
    {
        ReconcileCatCrySourcesLocked(player, now);
        var state = CryOf(player);
        if (state.Rules is null || player.Paused || InTown(player)) return;
        foreach (var source in state.Sources.Values)
            if (source.Since <= at + 1e-9 && state.Pending.Count < 64)
                state.Pending.Enqueue(new(state.Rules, player.ProjectileEpoch, source));
    }

    public static CatCryLoss[] PendingCatCryLosses(Player player) => [.. CryOf(player).Pending];

    public static bool CatCryLossCurrentLocked(Player player, CatCryLoss loss)
    {
        ReconcileCatCrySourcesLocked(player, AreaTime(player));
        return !loss.Consumed && CurrentGameWork && ReferenceEquals(loss.Rules, CombatLifetime)
            && player.ProjectileActive(loss.Epoch) && !player.RageRetired && player.PlayerLife > 0 && !player.Paused
            && !InTown(player) && CryOf(player).Sources.TryGetValue(loss.Source.Grant, out var current)
            && ReferenceEquals(current, loss.Source);
    }

    // Source, payment, cooldown and payload commit at the same native-send gate. A revoked receipt spends nothing.
    public static byte[]? AdmitCatCryLossLocked(Player player, CatCryLoss loss)
    {
        if (!CatCryLossCurrentLocked(player, loss)) return null;
        var grant = loss.Source.Grant;
        var source = GemSkillEffects(player).FirstOrDefault(g => g.Effect == grant.Effect
            && SupportedSkill(player, g.Id).WornGrant == grant);
        if (source.Name is null || GemSkillInstanceDisabled(player, source.Effect, source.Id, triggered: true) is not null)
            return null;
        var gem = SupportedSkill(player, source.Id);
        if (CooldownFor(player, source.Effect, source.Level, source.Quality) is not { } plan) return null;
        double now = AreaTime(player);
        int key = CooldownKey(player, source.Effect, source.Level);
        var cooldown = CooldownStateLocked(player, key, plan, now);
        if (!cooldown.Ready || !PayMana(player, source.Id, out _, out uint lifeCost)) return null;
        if (!cooldown.TryUse(now, plan)) return null;
        loss.Consumed = true;
        loss.LifeCost = lifeCost;
        player.SkillReadyAt[key] = now + cooldown.RemainingSeconds;
        cooldown.ConfirmNativeUse(cooldown.UseVersion, source.Id, synchronizeProgress: true);
        ApplyIntimidatingCryLocked(player, gem.Stats, CharacterPlayerStats(player), now, applyMovementBuff: true, skillId: source.Id);
        Console.WriteLine($"[gateway] Cat Stealth loss triggers Intimidating Cry skill=0x{source.Id:x4} source={grant.Host} t={now:0.000000}");
        var at = player.Walk.Position(now);
        return InstanceProtocol.InstantAction(player.Id, source.Id, at.X, at.Y,
            sequence: player.Connection.LockstepClient ? NextLockstepSequence(player) : (ushort)0xffff,
            flags: player.Connection.LockstepClient ? (ushort)0x101 : (ushort)0, triggered: true);
    }

    public static void ApplyIntimidatingCryLocked(Player player, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats, double now) =>
        ApplyIntimidatingCryLocked(player, own, playerStats, now, applyMovementBuff: true, skillId: 0);

    static int ApplyIntimidatingCryLocked(Player player, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats, double now, bool applyMovementBuff, ushort skillId = 0,
        int? radiusOverride = null)
    {
        int Own(uint n) => GolemStat(own, n);
        int Player(uint n) => GolemStat(playerStats, n);
        var at = player.Walk.Position(now);
        int radius = radiusOverride ?? SkillArea.Radius(WarcryRadius,
            n => ActorScale.Saturate((long)Player(n) + Own(n)), spell: false);
        var enemies = player.Area.Monsters.Values.Where(m => m.Hostile && !m.Dead && m.Life > 0
            && Dist(m.Walk.Position(now), at) <= radius).ToArray();
        int power = Player(Stat.WarcriesHaveInfinitePower) != 0 ? InfiniteWarcryPower
            : enemies.Sum(m => WarcryPower(m.Rarity));
        double taunt = TauntSeconds(key => ActorScale.Saturate((long)Player(key) + Own(key)));
        int taunted = TauntLocked(player, at, radius, now, taunt, Own(Stat.EnemiesTauntedByYourWarciesAreIntimidated) != 0,
            Player(Stat.EnemiesYouIntimidateHaveStunDurationOnSelfPlusPct));
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Player, Own)
            * SkillDurationModifiers.CallToArmsMore(Player);
        double effect = Math.Max(0, 1 + ((long)Player(Stat.WarcryBuffEffectPlusPct) + Own(Stat.SkillBuffEffectPlusPct)) / 100d);
        int movement = (int)Math.Clamp(Math.Floor(Math.Min(30, power) / 5d)
            * Own(Stat.IntimidatingCryMovementSpeedPlusPctPer5PowerUpToCap) * effect, 0, int.MaxValue);
        if (applyMovementBuff && duration > 0)
        {
            var gift = new CryGift(player.Instance.Presence, now + duration, movement);
            // Call to Arms: "Your Warcries do not grant Buffs or Charges to You"; the allies still get theirs.
            if (Player(Stat.KeystoneCallToArms) == 0)
            {
                CryOf(player).Gifts.Add(gift);
                CryGiftGained(player);
                MarkPlayerStats(player, PlayerStatStep.CryMovement);
            }
            // Deliver immutable release-time gifts. Recipients consume their own inbox, without cross-simulation reads.
            foreach (var peer in player.Instance.PlayersExcept(player.Id)
                         .Where(p => p.PlayerLife > 0 && Dist(p.Walk.Position(AreaTime(p)), at) <= radius))
                if (peer is { } recipient)
                {
                    var inbox = CryRuntimes.GetOrCreateValue(recipient).Inbox;
                    if (inbox.Count < 64) { inbox.Enqueue(gift); MarkPlayerStats(recipient, PlayerStatStep.CryMovement); }
                }
            foreach (var m in player.Area.Monsters.Values.Where(m => m.OwnerId is not null && !m.Hostile && !m.Dead && m.Life > 0
                         && Dist(m.Walk.Position(now), at) <= radius))
                MinionCryGifts.GetOrCreateValue(m).Add(gift);
        }
        var cry = CryOf(player);
        cry.Exerts = WarcryExertionCount(Own(Stat.SkillEmpowersNextXMeleeAttacks), playerStats, own, AreaRng(player));
        cry.IntimidatingDisplay = ResolveWarcryDisplaySkill(player, skillId, "IntimidatingCry");
        SyncExertedAttackBuffsLocked(player);
        player.RecentEvents.Record(StatConditions.UsedWarcry, now);
        player.AuraStatsDirty = true;
        Console.WriteLine($"[gateway] Intimidating Cry: power={power} taunted={taunted} movement={movement} "
            + $"duration={duration:0.###} exerts={cry.Exerts}");
        return taunted;
    }

    public static int IntimidatingCryMovement(Player player)
    {
        return IntimidatingCryMovementLocked(player);
    }

    static int IntimidatingCryMovementLocked(Player player)
    {
        var state = CryOf(player);
        double now = AreaTime(player);
        while (state.Inbox.TryDequeue(out var gift))
            if (ReferenceEquals(gift.Instance, player.Instance.Presence) && gift.Until > now) { state.Gifts.Add(gift); CryGiftGained(player); }
        if (player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0)
        {
            state.Gifts.Clear();
            bool hadExerts = (state.Exerts | state.SeismicExerts | state.AncestralExerts
                | state.RallyingExerts | state.InfernalExerts | state.BattlemagesExerts) != 0;
            state.Exerts = state.SeismicExerts = state.AncestralExerts = state.RallyingExerts = 0;
            state.InfernalExerts = state.BattlemagesExerts = 0;
            state.Combust = null;
            state.BattlemagesSource = null;
            if (hadExerts) SyncExertedAttackBuffsLocked(player);
        }
        state.Gifts.RemoveAll(g => !ReferenceEquals(g.Instance, player.Instance.Presence) || g.Until <= now);
        // The movement step runs again when the first gift ends (on the area's clock).
        if (state.Gifts.Count > 0) StatsChangeAt(state.Gifts.Min(g => g.Until), () => AreaTime(player));
        return state.Gifts.Select(g => g.Movement).DefaultIfEmpty().Max();
    }

    static int MinionIntimidatingCryMovement(Player player, SpawnedMonster? minion)
    {
        if (minion is null || !MinionCryGifts.TryGetValue(minion, out var gifts)) return 0;
        double now = AreaTime(player);
        gifts.RemoveAll(g => !ReferenceEquals(g.Instance, player.Instance.Presence) || g.Until <= now || minion.Dead);
        return gifts.Select(g => g.Movement).DefaultIfEmpty().Max();
    }

    public static int IntimidatingCryExerts(Player player)
    {
        IntimidatingCryMovement(player);
        return CryOf(player).Exerts;
    }

    public static bool ConsumeIntimidatingCryExertLocked(Player player, bool eligible)
    {
        if (!eligible || IntimidatingCryExerts(player) <= 0) return false;
        if (!ExertedAttackKeepsCountLocked(player)) CryOf(player).Exerts--;
        SyncExertedAttackBuffsLocked(player);
        return true;
    }

    internal static void GrantSeismicCryExertsLocked(Player player, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats, ushort skillId = 0)
    {
        var cry = CryOf(player);
        cry.SeismicExerts = WarcryExertionCount(own.GetValueOrDefault(Stat.SkillEmpowersNextXMeleeAttacks), playerStats, own, AreaRng(player));
        cry.SeismicArea = own.GetValueOrDefault(Stat.SeismicCryBaseSlamSkillAreaPlusPctFinal);
        cry.SeismicDisplay = ResolveWarcryDisplaySkill(player, skillId, "SeismicCry");
        SyncExertedAttackBuffsLocked(player);
    }

    internal static void GrantAncestralCryExertsLocked(Player player, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats, ushort skillId = 0)
    {
        var cry = CryOf(player);
        long count = (long)own.GetValueOrDefault(Stat.SkillEmpowersNextXMeleeAttacks)
            + playerStats.GetValueOrDefault(Stat.AncestralCryAttacksExertedPlus);
        cry.AncestralExerts = WarcryExertionCount(count, playerStats, own, AreaRng(player));
        cry.AncestralTargets = Math.Max(0, own.GetValueOrDefault(Stat.AncestralCryEmpoweredAttacksStrikeXAdditionalEnemies));
        cry.AncestralDisplay = ResolveWarcryDisplaySkill(player, skillId, "AncestralCry");
        SyncExertedAttackBuffsLocked(player);
    }

    internal static void GrantRallyingCryExertsLocked(Player player, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats, int affectedAllies, ushort skillId = 0)
    {
        var cry = CryOf(player);
        long count = (long)own.GetValueOrDefault(Stat.SkillEmpowersNextXMeleeAttacks)
            + playerStats.GetValueOrDefault(Stat.RallyingCryExertsXAdditionalAttacks);
        cry.RallyingExerts = WarcryExertionCount(count, playerStats, own, AreaRng(player));
        cry.RallyingDamage = (int)Math.Clamp((long)affectedAllies
            * own.GetValueOrDefault(Stat.RallyingCryDamagePlusPctFinalFromOsmPerNearbyAlly), 0, 25);
        cry.RallyingDisplay = ResolveWarcryDisplaySkill(player, skillId, "RallyingCry");
        SyncExertedAttackBuffsLocked(player);
    }

    static int? ConsumeRallyingCry(CryRuntime cry, bool consume = true)
    {
        if (cry.RallyingExerts <= 0) return null;
        if (consume) cry.RallyingExerts--;
        // A zero-ally cry still exerts the attack and counts for Overexertion.
        return cry.RallyingDamage;
    }

    static int WarcryExertionCount(long count, IReadOnlyDictionary<uint, int> stats,
        IReadOnlyDictionary<uint, int>? own = null, Random? random = null)
    {
        if (count <= 0) return 0;
        long total = count + stats.GetValueOrDefault(Stat.WarcryEmpowersNextXMeleeAttacks)
            + (own?.GetValueOrDefault(Stat.WarcryEmpowersNextXMeleeAttacks) ?? 0);
        int extra3Chance = ActorScale.Saturate((long)stats.GetValueOrDefault(Stat.ChanceToGain3AdditionalExertedAttacksPct)
            + (own?.GetValueOrDefault(Stat.ChanceToGain3AdditionalExertedAttacksPct) ?? 0));
        if (extra3Chance > 0 && random is not null && RandomRolls.Chance(extra3Chance, 100, random))
            total += 3;
        return Math.Max(0, ActorScale.Saturate(total * (stats.GetValueOrDefault(Stat.WarcriesExertTwiceAsManyAttacks) != 0 ? 2 : 1)));
    }

    static bool ExertedAttackKeepsCountLocked(Player player)
    {
        int chance = CharacterPlayerStats(player).GetValueOrDefault(Stat.ChanceForExertedAttacksToNotReduceCountPct);
        return chance > 0 && RandomRolls.Chance(chance, 100, AreaRng(player));
    }

    static int ConsumeAncestralCry(CryRuntime cry, bool consume = true)
    {
        if (cry.AncestralExerts <= 0) return 0;
        if (consume) cry.AncestralExerts--;
        return cry.AncestralTargets;
    }

    internal static (bool Intimidating, int? SeismicArea, int AncestralTargets, int? RallyingDamage) ConsumeMeleeWarcryExertsLocked(
        Player player, ushort skill, int effect, SkillCriticalRoll attack, bool eligible)
    {
        if (!eligible || !WarcryCanExert(player, skill, effect)) return default;
        var cry = CryOf(player);
        IntimidatingCryMovementLocked(player);
        bool consume = !ExertedAttackKeepsCountLocked(player);
        ConsumeMeleeWarcryTriggers(cry, attack, consume);
        bool intimidating = cry.Exerts > 0;
        if (intimidating && consume) cry.Exerts--;
        var skills = SkillsFor(player);
        int ancestralTargets = cry.AncestralExerts > 0 && skills.IsStrike(effect) ? ConsumeAncestralCry(cry, consume) : 0;
        int? rallyingDamage = ConsumeRallyingCry(cry, consume);
        int? seismicArea = null;
        if (cry.SeismicExerts > 0 && skills.HasType(effect, "Slam"))
        {
            if (consume) cry.SeismicExerts--;
            seismicArea = cry.SeismicArea;
        }
        SyncExertedAttackBuffsLocked(player);
        return (intimidating, seismicArea, ancestralTargets, rallyingDamage);
    }

    static bool WarcryCanExert(Player player, ushort skill, int effect)
    {
        var skills = SkillsFor(player);
        if (!skills.IsAttack(effect) || !skills.IsMelee(effect) || skills.IsVaal(effect)
            || skills.IsChannelled(effect) || skills.IsRetaliation(effect)
            || skills.HasType(effect, "NeverExertable") || skills.HasType(effect, "Triggered")
            || MeleeAttackRepeats(player, skill) != 0 || IsTotemSkill(player, skill))
            return false;
        var supported = SupportedSkill(player, skill);
        return PlacedKind(supported) is { Trap: false, Mine: false }
            && !supported.Supports.Any(s => s.Effect.Added.Contains(36))
            && supported.Stats.GetValueOrDefault(Stat.SkillCannotBeExerted) == 0
            && (!skills.IsTravel(effect) || CharacterPlayerStats(player).GetValueOrDefault(Stat.TravelSkillsCannotBeExerted) == 0);
    }

    public static HitRollModifiers WithIntimidatingCryExert(HitRollModifiers modifiers, bool exerted) =>
        exerted ? modifiers with { DoubleDamageChance = Math.Max(100, modifiers.DoubleDamageChance) } : modifiers;

    static HitRollModifiers WithExertedAttackRollModifiers(HitRollModifiers modifiers, bool intimidating,
        int warcries, IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int>? skillStats = null)
    {
        var next = WithIntimidatingCryExert(modifiers, intimidating);
        if (warcries <= 0) return next;
        long bonusDouble = (long)playerStats.GetValueOrDefault(Stat.EmpoweredAttackDoubleDamagePctChance)
            + (skillStats?.GetValueOrDefault(Stat.EmpoweredAttackDoubleDamagePctChance) ?? 0);
        if (bonusDouble <= 0) return next;
        return next with { DoubleDamageChance = ActorScale.Saturate((long)next.DoubleDamageChance + bonusDouble) };
    }

    static long ExertedAttackIncreasedDamage(IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int>? skillStats, int warcries, bool seismicExerted = false, bool ancestralExerted = false)
    {
        if (warcries <= 0) return 0;
        long Sum(uint key) => (long)playerStats.GetValueOrDefault(key) + (skillStats?.GetValueOrDefault(key) ?? 0);
        long total = Sum(Stat.EmpoweredAttackDamagePlusPct) + Sum(Stat.WarcryGrantDamagePlusPctToExertedAttacks);
        if (seismicExerted) total += Sum(Stat.SeismicCryExertedAttackDamagePlusPct);
        if (ancestralExerted) total += Sum(Stat.AncestralCryExertedAttackDamagePlusPct);
        return total;
    }

    static int ExertedAttackBonusCritPermyriad(int warcries, IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int>? skillStats = null)
    {
        if (warcries <= 0) return 0;
        long perWarcry = (long)playerStats.GetValueOrDefault(Stat.AdditionalCriticalStrikeChancePermyriadPerWarcryExertingAction)
            + (skillStats?.GetValueOrDefault(Stat.AdditionalCriticalStrikeChancePermyriadPerWarcryExertingAction) ?? 0);
        return perWarcry == 0 ? 0 : ActorScale.Saturate(perWarcry * warcries);
    }

    static long ExertedAttackAccuracyIncrease(int warcries, IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int>? skillStats = null) =>
        warcries <= 0 ? 0
            : (long)playerStats.GetValueOrDefault(Stat.EmpoweredAttackAccuracyRatingPlusPct)
                + (skillStats?.GetValueOrDefault(Stat.EmpoweredAttackAccuracyRatingPlusPct) ?? 0);

    static double ExertedAttackMore(IReadOnlyDictionary<uint, int>? skill, bool exerted, int otherWarcries = 0,
        int? rallyingDamage = null, IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        int warcries = (exerted ? 1 : 0) + otherWarcries + (rallyingDamage.HasValue ? 1 : 0);
        if (warcries == 0)
            return ((playerStats?.GetValueOrDefault(Stat.NonExertedAttacksDealNoDamage) ?? 0) != 0
                || (skill?.GetValueOrDefault(Stat.NonExertedAttacksDealNoDamage) ?? 0) != 0) ? 0 : 1;
        double warcryMore = Math.Max(0, 1 + (skill?.GetValueOrDefault(Stat.SupportOverexertionDamagePlusPctFinalPerWarcryExertingAction) ?? 0) / 100d * warcries);
        double exertedMore = Math.Max(0, 1 + (skill?.GetValueOrDefault(Stat.SupportOverexertionDamagePlusPctFinalIfExerted) ?? 0) / 100d);
        long globalMorePct = (long)(playerStats?.GetValueOrDefault(Stat.EmpoweredAttackDamagePlusPctFinal) ?? 0)
            + (skill?.GetValueOrDefault(Stat.EmpoweredAttackDamagePlusPctFinal) ?? 0);
        double globalMore = globalMorePct == 0 ? 1 : Math.Max(0, 1 + globalMorePct / 100d);
        long helmetPerCry = (long)(playerStats?.GetValueOrDefault(Stat.UniqueHelmetDamagePlusPctFinalPerWarcryExertingAction) ?? 0)
            + (skill?.GetValueOrDefault(Stat.UniqueHelmetDamagePlusPctFinalPerWarcryExertingAction) ?? 0);
        double helmetMore = helmetPerCry == 0 ? 1 : Math.Max(0, 1 + helmetPerCry / 100d * warcries);
        return warcryMore * exertedMore * globalMore * helmetMore * (1 + rallyingDamage.GetValueOrDefault() / 100d);
    }

    static DamagePacket WithExertedAttackDamage(DamagePacket hit, IReadOnlyDictionary<uint, int>? skill, bool exerted,
        int otherWarcries = 0, int? rallyingDamage = null, IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        double more = ExertedAttackMore(skill, exerted, otherWarcries, rallyingDamage, playerStats);
        return more == 1 ? hit : hit.Scale(more);
    }

    static bool IntimidatingCryPoolsChangedLocked(Player player)
    {
        int movement = IntimidatingCryMovement(player);
        var state = CryOf(player);
        if (movement == state.PresentedMovement) return false;
        state.PresentedMovement = movement;
        return true;
    }

    static byte[] IntimidatingCryBuffRecordsLocked(Player player)
    {
        IntimidatingCryMovement(player);
        var state = CryOf(player);
        double until = state.Gifts.Select(g => g.Until).DefaultIfEmpty().Max();
        if (until > AreaTime(player)) player.Area.Buffs.Keep(player.Id, CryBuff(player, until));
        else player.Area.Buffs.Clear(player.Id, IntimidatingCryBuffRow, player.Id);
        state.Buff = (ushort)(until > AreaTime(player) ? 1 : 0);
        return [];
    }

    // The latest gift's countdown: a new gift moves it.
    static Buff CryBuff(Player player, double until) =>
        new(IntimidatingCryBuffRow, player.Id, U68: Buff.Seconds(until - AreaTime(player)));

    static void CryGiftGained(Player player)
    {
        var state = CryOf(player);
        double until = state.Gifts.Select(g => g.Until).DefaultIfEmpty().Max();
        if (until > AreaTime(player)) player.Area.Buffs.Set(player.Id, CryBuff(player, until));
    }

    static async Task DrainCatCryLosses(Player player)
    {
        CatCryLoss[] losses;
        var state = CryOf(player); losses = [.. state.Pending]; state.Pending.Clear();
        foreach (var loss in losses)
        {
            bool accepted = false;
            await SendWorldRecord(player, player.Id, [], "0x01ce Cat Stealth loss triggers Intimidating Cry",
                atGate: () =>
                {
                    using (ProjectileMutation(player))
                    {
                        var packet = AdmitCatCryLossLocked(player, loss);
                        accepted = packet is not null;
                        return packet;
                    }
                });
            if (accepted)
            {
                await SendSkillCost(player, loss.LifeCost, "triggered Intimidating Cry cost");
                await SendChargeStats(player, "Intimidating Cry");
                var at = player.Walk.Position(AreaTime(player));
                await WarcryReleased(player, at, WarcryRadius, loss.Epoch);
            }
        }
        state = CryOf(player);
        if (losses.Length == 0 && state.Buff == 0 && state.Gifts.Count == 0 && state.Inbox.IsEmpty) return;
        await SendWorldRecord(player, player.Id, [], "Intimidating Cry buff",
            atGate: () =>
            {
                return !CurrentGameWork || player.Closed || player.LeavingArea ? null : IntimidatingCryBuffRecordsLocked(player);
            });
        if (losses.Length > 0) await SendNativeCooldownRefresh(player);
    }
}
