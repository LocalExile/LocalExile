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
            state.Exerts = state.SeismicExerts = state.AncestralExerts = 0;
            if (initialized) while (state.Inbox.TryDequeue(out _)) { }
        }
        return state;
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
        ApplyIntimidatingCryLocked(player, gem.Stats, CharacterPlayerStats(player), now);
        Console.WriteLine($"[gateway] Cat Stealth loss triggers Intimidating Cry skill=0x{source.Id:x4} source={grant.Host} t={now:0.000000}");
        var at = player.Walk.Position(now);
        return InstanceProtocol.InstantAction(player.Id, source.Id, at.X, at.Y,
            sequence: player.Connection.LockstepClient ? NextLockstepSequence(player) : (ushort)0xffff,
            flags: player.Connection.LockstepClient ? (ushort)0x101 : (ushort)0, triggered: true);
    }

    public static void ApplyIntimidatingCryLocked(Player player, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats, double now) =>
        ApplyIntimidatingCryLocked(player, own, playerStats, now, applyMovementBuff: true);

    static int ApplyIntimidatingCryLocked(Player player, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats, double now, bool applyMovementBuff, int? radiusOverride = null)
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
        double taunt = TauntSeconds(Player);
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
        int attacks = Math.Max(0, Own(Stat.SkillEmpowersNextXMeleeAttacks));
        CryOf(player).Exerts = Player(Stat.WarcriesExertTwiceAsManyAttacks) != 0
            ? ActorScale.Saturate(2L * attacks) : attacks;
        player.RecentEvents.Record(StatConditions.UsedWarcry, now);
        player.AuraStatsDirty = true;
        Console.WriteLine($"[gateway] Intimidating Cry: power={power} taunted={taunted} movement={movement} "
            + $"duration={duration:0.###} exerts={CryOf(player).Exerts}");
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
        { state.Gifts.Clear(); state.Exerts = state.SeismicExerts = state.AncestralExerts = 0; }
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
        CryOf(player).Exerts--;
        return true;
    }

    internal static void GrantSeismicCryExertsLocked(Player player, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats)
    {
        var cry = CryOf(player);
        cry.SeismicExerts = WarcryExertionCount(own.GetValueOrDefault(Stat.SkillEmpowersNextXMeleeAttacks), playerStats);
        cry.SeismicArea = own.GetValueOrDefault(Stat.SeismicCryBaseSlamSkillAreaPlusPctFinal);
    }

    internal static void GrantAncestralCryExertsLocked(Player player, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats)
    {
        var cry = CryOf(player);
        long count = (long)own.GetValueOrDefault(Stat.SkillEmpowersNextXMeleeAttacks)
            + playerStats.GetValueOrDefault(Stat.AncestralCryAttacksExertedPlus);
        cry.AncestralExerts = WarcryExertionCount(count, playerStats);
        cry.AncestralTargets = Math.Max(0, own.GetValueOrDefault(Stat.AncestralCryEmpoweredAttacksStrikeXAdditionalEnemies));
    }

    static int WarcryExertionCount(long count, IReadOnlyDictionary<uint, int> stats) =>
        Math.Max(0, ActorScale.Saturate(count * (stats.GetValueOrDefault(Stat.WarcriesExertTwiceAsManyAttacks) != 0 ? 2 : 1)));

    static int ConsumeAncestralCry(CryRuntime cry)
    {
        if (cry.AncestralExerts <= 0) return 0;
        cry.AncestralExerts--;
        return cry.AncestralTargets;
    }

    static (bool Intimidating, int? SeismicArea, int AncestralTargets) ConsumeMeleeWarcryExertsLocked(
        Player player, ushort skill, int effect, bool eligible)
    {
        if (!eligible || !WarcryCanExert(player, skill, effect)) return default;
        var cry = CryOf(player);
        IntimidatingCryMovementLocked(player);
        bool intimidating = cry.Exerts > 0;
        if (intimidating) cry.Exerts--;
        var skills = SkillsFor(player);
        int ancestralTargets = cry.AncestralExerts > 0 && skills.IsStrike(effect) ? ConsumeAncestralCry(cry) : 0;
        if (cry.SeismicExerts <= 0 || !skills.HasType(effect, "Slam"))
            return (intimidating, null, ancestralTargets);
        cry.SeismicExerts--;
        return (intimidating, cry.SeismicArea, ancestralTargets);
    }

    static bool WarcryCanExert(Player player, ushort skill, int effect) =>
        SkillsFor(player).IsAttack(effect) && SkillsFor(player).IsMelee(effect) && !SkillsFor(player).IsVaal(effect)
        && !SkillsFor(player).IsChannelled(effect) && !SkillsFor(player).IsRetaliation(effect)
        && MeleeAttackRepeats(player, skill) == 0
        && !IsTotemSkill(player, skill) && PlacedKind(SupportedSkill(player, skill)) is { Trap: false, Mine: false }
        && SupportedSkill(player, skill).Stats.GetValueOrDefault(Stat.SkillCannotBeExerted) == 0
        && (!SkillsFor(player).IsTravel(effect) || CharacterPlayerStats(player).GetValueOrDefault(Stat.TravelSkillsCannotBeExerted) == 0);

    public static HitRollModifiers WithIntimidatingCryExert(HitRollModifiers modifiers, bool exerted) =>
        exerted ? modifiers with { DoubleDamageChance = Math.Max(100, modifiers.DoubleDamageChance) } : modifiers;

    static double OverexertionMore(IReadOnlyDictionary<uint, int> skill, bool exerted, int otherWarcries = 0)
    {
        int warcries = (exerted ? 1 : 0) + otherWarcries;
        if (warcries == 0) return 1;
        double warcryMore = Math.Max(0, 1 + skill.GetValueOrDefault(Stat.SupportOverexertionDamagePlusPctFinalPerWarcryExertingAction) / 100d * warcries);
        double exertedMore = Math.Max(0, 1 + skill.GetValueOrDefault(Stat.SupportOverexertionDamagePlusPctFinalIfExerted) / 100d);
        return warcryMore * exertedMore;
    }

    static DamagePacket WithOverexertion(DamagePacket hit, IReadOnlyDictionary<uint, int> skill, bool exerted,
        int otherWarcries = 0)
    {
        double more = OverexertionMore(skill, exerted, otherWarcries);
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
