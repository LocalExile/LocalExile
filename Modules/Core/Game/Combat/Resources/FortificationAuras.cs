using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    public const int ChampionFortitudeRadius = 40;
    public const int KingmakerAuraRadius = 30;

    public readonly record struct KingmakerAura(int Fortify, bool Culling, int CritMulti, int Iir, int Radius = KingmakerAuraRadius)
    {
        public bool Active => Fortify > 0 || Culling || CritMulti > 0 || Iir > 0;
    }

    public readonly record struct AllyFortificationBonus(int ChampionFortification, int KingmakerFortify,
        bool KingmakerCulling, int KingmakerCritMulti, int KingmakerIir);

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FortificationSession, PermanentPresentation> PresentedPermanent = new();

    sealed class PermanentPresentation
    {
        public int Current;
        public int Maximum;
        public bool HasExpiry;
    }

    public static KingmakerAura ReadKingmaker(IEnumerable<Item> items, ModCatalog mods)
    {
        int fortify = 0, critMulti = 0, iir = 0;
        bool culling = false;
        foreach (var item in items)
            foreach (var mod in item.Mods)
            {
                if (mods.ByHash(mod.Hash) is not { } row) continue;
                for (int s = 0; s < row.Stats.Count && s < mod.Values.Count; s++)
                    switch (Stats.Name(row.Stats[s].Id))
                    {
                        case "local_display_nearby_allies_have_fortify":
                            fortify = Math.Max(fortify, mod.Values[s]);
                            break;
                        case "local_display_aura_allies_have_culling_strike":
                            if (mod.Values[s] != 0) culling = true;
                            break;
                        case "local_display_nearby_allies_critical_strike_multiplier_+":
                            critMulti = Math.Max(critMulti, mod.Values[s]);
                            break;
                        case "local_display_aura_allies_have_increased_item_rarity_+%":
                            iir = Math.Max(iir, mod.Values[s]);
                            break;
                    }
            }
        return new(fortify, culling, critMulti, iir);
    }

    static KingmakerAura MergeKingmaker(KingmakerAura a, KingmakerAura b) => new(
        Math.Max(a.Fortify, b.Fortify),
        a.Culling || b.Culling,
        Math.Max(a.CritMulti, b.CritMulti),
        Math.Max(a.Iir, b.Iir));

    static (GolemState.Golem? Guardian, KingmakerAura Aura) LiveAnimatedGuardianKingmakerLocked(Player player)
    {
        var ag = player.Summons.Fighters.Values.FirstOrDefault(g =>
            g.Definition.Kind == "AnimatedGuardian" && !g.Monster.Dead && g.Monster.Life > 0);
        if (ag is null || player.Summons.GuardianEquipment.Count == 0) return default;
        return (ag, ReadKingmaker(player.Summons.GuardianEquipment.Values, ItemsFor(player).Mods));
    }

    static KingmakerAura OwnEquippedKingmakerLocked(Player player)
    {
        var active = EquipmentForStats(player)
            .Where(e => !IsInactive(player, e.Item.Id))
            .Select(e => e.Item.Item);
        return ReadKingmaker(active, ItemsFor(player).Mods);
    }

    static bool HasGuardianOrPeerFortificationAura(Player player)
    {
        if (player.PlayerLife == 0) return false;
        if (Stat.YouHaveYourMaximumFortification is uint key
            && (player.StatSourcesCache?.Sources.GetValueOrDefault(key) ?? 0) != 0)
            return true;
        if (InTown(player)) return false;
        if (player.Summons.GuardianEquipment.Count > 0 && player.Summons.Fighters.Values.Any(g =>
            g.Definition.Kind == "AnimatedGuardian" && !g.Monster.Dead && g.Monster.Life > 0))
            return true;
        if (player.Instance is { } instance && instance.PlayersExcept(player.Id).Any(p => p.PlayerLife > 0 && p.Fortification is not null))
            return true;
        return false;
    }

    public static AllyFortificationBonus SelfAllyFortificationBonusLocked(Player player, double now)
    {
        if (player.PlayerLife == 0 || InTown(player)) return default;
        var here = player.Walk.Position(now);
        int championFort = 0;
        KingmakerAura km = default;

        var (ag, agKm) = LiveAnimatedGuardianKingmakerLocked(player);
        if (ag is not null && agKm.Active && Dist(here, ag.Monster.Walk.Position(now)) <= agKm.Radius)
            km = MergeKingmaker(km, agKm);

        if (player.Instance is { } instance)
            foreach (var peer in instance.PlayersExcept(player.Id))
            {
                if (peer.PlayerLife == 0 || peer.Fortification is not { } share) continue;
                var peerPos = peer.Walk.Position(now);
                if (share.ChampionFortification > 0 && Dist(here, peerPos) <= share.ChampionRadius)
                    championFort = Math.Max(championFort, share.ChampionFortification);
                bool peerKm = share.KingmakerFortify > 0 || share.KingmakerCulling || share.KingmakerCritMulti > 0 || share.KingmakerIir > 0;
                if (!peerKm) continue;
                bool inRange = Dist(here, peerPos) <= share.KingmakerRadius
                    || share.AnimatedGuardianAt is { } peerAg && Dist(here, peerAg) <= share.KingmakerRadius;
                if (inRange)
                    km = MergeKingmaker(km, new(share.KingmakerFortify, share.KingmakerCulling, share.KingmakerCritMulti, share.KingmakerIir));
            }

        return new(championFort, km.Fortify, km.Culling, km.CritMulti, km.Iir);
    }

    public static (FortificationState.Value Value, int Above20AttackSpeedPct, int Above20DefenceMorePct, int SuppressionPct, int MinionDoubleDamagePct)
        EvaluateFortificationLocked(Player player, double now, IReadOnlyDictionary<uint, int>? stats = null,
            bool skipPeerShare = false)
    {
        var session = FortificationOf(player);
        int GetStat(uint key) => (stats ?? player.StatSourcesCache?.Sources)?.GetValueOrDefault(key) ?? 0;
        int addedMax = stats is not null ? GetStat(FortificationState.MaximumStat) : session.AddedMaximum;
        int maximum = FortificationState.Maximum(addedMax);
        bool hasChampionFortitude = GetStat(Stat.YouHaveYourMaximumFortification) != 0;
        var ally = skipPeerShare ? default : SelfAllyFortificationBonusLocked(player, now);
        int minimumOverride = Math.Max(hasChampionFortitude ? maximum : 0, ally.ChampionFortification);
        var value = session.Read(AreaTime(player), addedMax, ally.KingmakerFortify, minimumOverride);
        int above20 = Math.Max(0, value.Current - 20);
        int speed = above20 * GetStat(Stat.AttackSpeedPlusPctPerFortificationAbove20);
        int defenceMore = above20 * GetStat(Stat.ArmourEvasionRatingPlusPctFinalPerFortificationAbove20);
        int suppression = value.Current * GetStat(Stat.SpellSuppressionChancePctPerFortification);
        int minionDouble = value.Current * GetStat(Stat.MinionChanceToDealDoubleDamagePctPerFortification);
        return (value, speed, defenceMore, suppression, minionDouble);
    }

    public static int FortificationDamageTakenLocked(Player player, int damage, double now, IReadOnlyDictionary<uint, int>? stats = null)
    {
        if (damage <= 0) return damage;
        int alternate = 0;
        if (Stat.ShouldUseAlternateFortify is uint altKey)
            alternate = (stats ?? player.StatSourcesCache?.Sources)?.GetValueOrDefault(altKey) ?? 0;
        if (alternate != 0) return damage;
        var value = EvaluateFortificationLocked(player, now, stats).Value;
        return value.Current == 0 ? damage : (int)Math.Round(damage * value.HitDamageFactor);
    }

    public static FortificationShare? OwnFortificationShareLocked(Player player, double now)
    {
        if (player.PlayerLife == 0 || InTown(player)) return null;
        int championFort = 0;
        if (Stat.ChampionAscendancyNearbyAlliesFortificationIsEqualToYours is uint shareKey
            && (player.StatSourcesCache?.Sources.GetValueOrDefault(shareKey) ?? 0) != 0)
        {
            championFort = EvaluateFortificationLocked(player, now, skipPeerShare: true).Value.Current;
        }
        var ownKm = OwnEquippedKingmakerLocked(player);
        var (ag, agKm) = LiveAnimatedGuardianKingmakerLocked(player);
        var combined = ag is not null && agKm.Active ? MergeKingmaker(ownKm, agKm) : ownKm;
        (int X, int Y)? agPos = ag is not null && agKm.Active ? ag.Monster.Walk.Position(now) : null;
        if (championFort <= 0 && !combined.Active) return null;
        return new(championFort, ChampionFortitudeRadius, combined.Fortify, combined.Culling,
            combined.CritMulti, combined.Iir, KingmakerAuraRadius, agPos);
    }

    public static Dictionary<uint, int> FortificationAllyPlayerStats(Player player, Dictionary<uint, int> stats)
    {
        double now = AreaTime(player);
        var ally = SelfAllyFortificationBonusLocked(player, now);
        var session = FortificationOf(player);
        var sessionValue = session.Read(now);
        int GetStat(uint key) => stats.GetValueOrDefault(key);
        var eval = EvaluateFortificationLocked(player, now, stats);
        int deltaStacks = eval.Value.Current - sessionValue.Current;
        bool countsChanged = eval.Value != sessionValue;
        int extraAttackSpeed = deltaStacks * session.SpeedPerStack + eval.Above20AttackSpeedPct;
        int suppression = eval.SuppressionPct;
        int minionLifeShare = GetStat(Stat.AdditiveMinionMaximumLifeModifiersApplyToYouAtPctValue);
        int minionLifePct = minionLifeShare != 0
            ? (int)Math.Clamp((long)GetStat(Stat.MinionMaximumLifePlusPct) * minionLifeShare / 100, int.MinValue, int.MaxValue)
            : 0;

        if (!countsChanged && !ally.KingmakerCulling && ally.KingmakerCritMulti == 0 && ally.KingmakerIir == 0
            && extraAttackSpeed == 0 && suppression == 0 && minionLifePct == 0)
            return stats;

        var next = new Dictionary<uint, int>(stats);
        if (countsChanged) AddFortificationCounts(new PlainStatMap(next), eval.Value);
        if (ally.KingmakerCulling)
            next[Stat.KillEnemyOnHitIfUnder10PctLife] = Math.Max(next.GetValueOrDefault(Stat.KillEnemyOnHitIfUnder10PctLife), 1);
        if (ally.KingmakerCritMulti != 0)
            next[Stat.BaseCriticalStrikeMultiplierPlus] = next.GetValueOrDefault(Stat.BaseCriticalStrikeMultiplierPlus) + ally.KingmakerCritMulti;
        if (ally.KingmakerIir != 0)
            next[Stat.BaseItemFoundRarityPlusPct] = next.GetValueOrDefault(Stat.BaseItemFoundRarityPlusPct) + ally.KingmakerIir;
        if (extraAttackSpeed != 0)
            next[Stat.AttackSpeedPlusPct] = (int)Math.Clamp((long)next.GetValueOrDefault(Stat.AttackSpeedPlusPct) + extraAttackSpeed, int.MinValue, int.MaxValue);
        if (suppression != 0)
            next[Stat.BaseSpellSuppressionChancePct] = (int)Math.Clamp((long)next.GetValueOrDefault(Stat.BaseSpellSuppressionChancePct) + suppression, int.MinValue, int.MaxValue);
        if (minionLifePct != 0)
            next[Stat.MaximumLifePlusPct] = (int)Math.Clamp((long)next.GetValueOrDefault(Stat.MaximumLifePlusPct) + minionLifePct, int.MinValue, int.MaxValue);
        return next;
    }

    public static Cull MinionCullLocked(Player player, GolemState.Golem? golem, (int X, int Y) minionAt, double now) =>
        MinionAllyFortificationLocked(player, golem, minionAt, now).KingmakerCulling ? new Cull(10, 0) : default;

    public static AllyFortificationBonus MinionAllyFortificationLocked(Player player, GolemState.Golem? golem,
        (int X, int Y) minionAt, double now)
    {
        if (player.PlayerLife == 0 || InTown(player)) return default;
        var ownerAt = player.Walk.Position(now);
        int championFort = 0;
        KingmakerAura km = default;

        // Owner's Champion Fortitude ("Nearby Allies count as having Fortification equal to yours").
        if (Stat.ChampionAscendancyNearbyAlliesFortificationIsEqualToYours is uint shareKey
            && (player.StatSourcesCache?.Sources.GetValueOrDefault(shareKey) ?? 0) != 0
            && Dist(minionAt, ownerAt) <= ChampionFortitudeRadius)
        {
            championFort = EvaluateFortificationLocked(player, now).Value.Current;
        }

        // Owner wielding Kingmaker directly buffs nearby minions within 30.
        var ownKm = OwnEquippedKingmakerLocked(player);
        if (ownKm.Active && Dist(minionAt, ownerAt) <= ownKm.Radius)
            km = MergeKingmaker(km, ownKm);

        // Living Animated Guardian wielding Kingmaker buffs OTHER nearby allies (not itself).
        var (ag, agKm) = LiveAnimatedGuardianKingmakerLocked(player);
        if (ag is not null && !ReferenceEquals(ag, golem) && agKm.Active
            && Dist(minionAt, ag.Monster.Walk.Position(now)) <= agKm.Radius)
            km = MergeKingmaker(km, agKm);

        // Nearby party members and their Animated Guardians.
        if (player.Instance is { } instance)
            foreach (var peer in instance.PlayersExcept(player.Id))
            {
                if (peer.PlayerLife == 0 || peer.Fortification is not { } share) continue;
                var peerPos = peer.Walk.Position(now);
                if (share.ChampionFortification > 0 && Dist(minionAt, peerPos) <= share.ChampionRadius)
                    championFort = Math.Max(championFort, share.ChampionFortification);
                bool peerKm = share.KingmakerFortify > 0 || share.KingmakerCulling || share.KingmakerCritMulti > 0 || share.KingmakerIir > 0;
                if (peerKm && (Dist(minionAt, peerPos) <= share.KingmakerRadius
                    || share.AnimatedGuardianAt is { } peerAg && Dist(minionAt, peerAg) <= share.KingmakerRadius))
                    km = MergeKingmaker(km, new(share.KingmakerFortify, share.KingmakerCulling, share.KingmakerCritMulti, share.KingmakerIir));
            }

        return new(championFort, km.Fortify, km.Culling, km.CritMulti, km.Iir);
    }

    public static int MinionFortificationStacksLocked(Player player, GolemState.Golem? golem, (int X, int Y) minionAt, double now)
    {
        var ally = MinionAllyFortificationLocked(player, golem, minionAt, now);
        int kingmaker = Math.Min(FortificationState.BaseMaximum, ally.KingmakerFortify);
        return Math.Max(kingmaker, ally.ChampionFortification);
    }

    public static int MinionDoubleDamageChanceLocked(Player player, GolemState.Golem g, bool minionFullLife, double now)
    {
        int total = 0;
        if (minionFullLife)
            total += GolemStat(g.Player, Stat.Of("minion_chance_to_deal_double_damage_while_on_full_life_%"));
        total += g.Monster.Effects[Stat.MinionChanceToDealDoubleDamagePct];
        int perFort = GolemStat(g.Player, Stat.MinionChanceToDealDoubleDamagePctPerFortification);
        if (perFort > 0)
            total += EvaluateFortificationLocked(player, now, g.Player).Value.Current * perFort;
        return Math.Clamp(total, 0, 100);
    }

    public static IReadOnlyDictionary<uint, int>? WithStrikeFortifySkillStat(IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int>? skillStats, bool strike)
    {
        if (!strike || GolemStat(playerStats, Stat.StrikeSkillsFortifyOnHit) == 0
            || (skillStats is not null && GolemStat(skillStats, FortificationSession.OnMeleeHit) != 0))
            return skillStats;
        var copy = skillStats is not null ? new Dictionary<uint, int>(skillStats) : new Dictionary<uint, int>();
        copy[FortificationSession.OnMeleeHit] = 1;
        return copy;
    }

    public static void AfterFortificationGainLocked(Player player, IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        int loseChance = 0;
        if (Stat.LoseAnEnduranceChargeOnFortifyGainPctChance is uint key)
            loseChance = (playerStats ?? player.StatSourcesCache?.Sources)?.GetValueOrDefault(key) ?? 0;
        if (loseChance > 0 && RandomRolls.Chance(loseChance, 100, AreaRng(player)))
            ChargesOf(player).Spend(ChargeType.Endurance);
    }

    static bool FortificationPresentationDirtyLocked(Player player, FortificationSession session, double now)
    {
        var value = EvaluateFortificationLocked(player, now).Value;
        var state = PresentedPermanent.GetOrCreateValue(session);
        return state.Current != value.Current || state.Maximum != value.Maximum || state.HasExpiry != value.LatestExpiry.HasValue;
    }

    static void MarkFortificationPresentedLocked(Player player, FortificationSession session, double now)
    {
        var value = EvaluateFortificationLocked(player, now).Value;
        var state = PresentedPermanent.GetOrCreateValue(session);
        state.Current = value.Current;
        state.Maximum = value.Maximum;
        state.HasExpiry = value.LatestExpiry.HasValue;
    }
}
