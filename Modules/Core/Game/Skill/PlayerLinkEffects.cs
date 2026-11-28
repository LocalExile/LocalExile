using GameServer.Core.World;

public static partial class Game
{
    readonly record struct PlayerLinkStats(LinkOffence Offence, int? AttackBlock = null, int BlockMax = 0,
        int RecoverOnBlock = 0, int StunThreshold = 0, int SoulTaken = 0, int Intercept = 0, int FullLifeDamage = 0);

    // Materialize the caster-dependent values in its frame. The recipient's stat graph never builds the caster's stats.
    sealed class PlayerLinkStatsState(PlayerLinkStats stats)
    {
        public PlayerLinkStats Stats = stats;
        public double LeechCarry;
    }
    static readonly RuleTable<LinkSkills.Link, PlayerLinkStatsState> PlayerLinkSnapshots = new();

    static void RefreshPlayerLinkStats(Player caster, Player peer, LinkSkills.Link link, IReadOnlyDictionary<uint, int> source)
    {
        long effect = Math.Max(0, 100L + source.GetValueOrDefault(Stat.LinkSkillBuffEffectPlusPct)
            + PlayerStatValues(peer).GetValueOrDefault(Stat.LinkBuffEffectOnSelfPlusPct));
        int Scale(uint key) => ScaledLinkStat(link, key, effect);
        int maxBlock = caster.Pools.BlockChanceMax;
        PlayerLinkStats snapshot = link.EffectId switch
        {
            "FlameLink" or "DestructiveLink" => new(LinkOffenceAt(caster, link, effect)),
            "ProtectiveLink" => new(default,
                AttackBlock: caster.Pools.AttackBlockAtMaximum ? maxBlock : caster.Pools.BlockChance,
                BlockMax: maxBlock, RecoverOnBlock: Scale(Stat.BulwarkLinkGrantsRecoverXLifeOnBlock),
                StunThreshold: Scale(Stat.BulwarkLinkGrantsStunThresholdPlusPct)),
            "SoulLink" => new(default, SoulTaken: Scale(Stat.SoulLinkGrantsDamageTakenPlusPctFinal),
                Intercept: Math.Clamp(Scale(Stat.SoulLinkGrantsTakePctOfHitDamageFromSoulLinkSourceEnergyShieldBeforeYou), 0, 100)),
            "VampiricLink" => new(default, FullLifeDamage: Scale(Stat.RemoraLinkGrantsDamagePlusPctWhenOnFullLife)),
            _ => default
        };
        if (PlayerLinkSnapshots.TryGetValue(link, out var previous))
        {
            if (previous.Stats == snapshot) return;
            previous.Stats = snapshot;
        }
        else
            PlayerLinkSnapshots.Add(link, new(snapshot));
        if (snapshot != default || previous is not null) PlayerStatBuffsChanged(peer);
    }

    static bool ActivePlayerLink(Player source, Player peer, LinkSkills.Link link, double now) =>
        source.PlayerLife > 0 && !source.Closed && !source.LeavingArea && source.PendingTransfer is null
        && peer.PlayerLife > 0 && LinkPeerPresent(source, peer)
        && ReferenceEquals(peer.Links.Incoming?.Link, link) && now < link.Until
        && (link.AwaySince is not double away || now - away < LinkSkills.BreakSeconds);

    static Dictionary<uint, int> WithPlayerLinkSources(Player peer, Dictionary<uint, int> sources)
    {
        if (peer.Links.Incoming is not { } incoming
            || !PlayerLinkSnapshots.TryGetValue(incoming.Link, out var snapshot)) return sources;
        var stats = snapshot.Stats;
        void Add(uint key, int value)
        {
            if (value != 0) sources[key] = ActorScale.Saturate((long)sources.GetValueOrDefault(key) + value);
        }
        Add(Stat.GlobalMinimumAddedFireDamage, stats.Offence.FireMin);
        Add(Stat.GlobalMaximumAddedFireDamage, stats.Offence.FireMax);
        Add(Stat.BaseChanceToIgnitePct, stats.Offence.Ignite);
        Add(Stat.BaseCriticalStrikeMultiplierPlus, stats.Offence.CritMultiplier);
        Add(Stat.AccuracyRatingPlusPct, stats.Offence.Accuracy);
        Add(Stat.RecoverXLifeOnBlock, stats.RecoverOnBlock);
        Add(Stat.StunThresholdPlusPct, stats.StunThreshold);
        Add(Stat.SoulLinkGrantsDamageTakenPlusPctFinal, stats.SoulTaken);
        Add(Stat.RemoraLinkGrantsDamagePlusPctWhenOnFullLife, stats.FullLifeDamage);
        if (stats.AttackBlock is int block)
        {
            sources[Stat.DisplayBulwarkLinkOverridesAttackBlockAndMaximumAttackBlock] = 1;
            sources[Stat.OverrideAttackBlockPctFromBulwarkLink] = block;
            sources[Stat.OverrideMaximumAttackBlockPctFromBulwarkLink] = stats.BlockMax;
        }
        if (stats.Offence.MainHandCrit is int crit)
        {
            sources[Stat.DisplayCriticalLinkOverridesMainHandCriticalStrikeChance] = 1;
            sources[Stat.OverrideMainHandCriticalStrikeChancePctFromCriticalLink] = crit;
        }
        return sources;
    }

    static Player? VampiricPlayerAt(Player source, LinkSkills.Link link, double now) =>
        link.EffectId == "VampiricLink" && link.PlayerTarget is { } peer && ActivePlayerLink(source, peer, link, now) ? peer : null;

    static void RecoverVampiricPlayer(Player peer, LinkSkills.Link link, double recovery, bool overTime)
    {
        var stats = PlayerStatValues(peer);
        if (stats.GetValueOrDefault(Stat.CannotRecoverLife) != 0
            || !PlayerLinkSnapshots.TryGetValue(link, out var state)) return;
        if (overTime) recovery *= Math.Max(0, 1 + stats.GetValueOrDefault(Stat.LifeRecoveryRatePlusPct) / 100d);
        uint before = peer.PlayerLife;
        peer.PlayerLife = PetrifiedBlood.RecoverLife(peer, before, recovery, ref state.LeechCarry, minimum: 1);
        if (peer.PlayerLife != before) TrimLeechLocked(peer);
    }

    static (int Physical, int Other, int Chaos) InterceptSoulLinkHit(Player peer, int physical, int other, int chaos, double now)
    {
        if (peer.Links.Incoming is not { } incoming || incoming.Link.EffectId != "SoulLink"
            || !ActivePlayerLink(incoming.Source, peer, incoming.Link, now)
            || incoming.Source.EnergyShield.Current == 0
            || !PlayerLinkSnapshots.TryGetValue(incoming.Link, out var snapshot) || snapshot.Stats.Intercept <= 0)
            return (physical, other, chaos);
        var shield = incoming.Source.EnergyShield;
        long carry = 0;
        int AfterIntercept(int value)
        {
            long share = carry + (long)value * snapshot.Stats.Intercept;
            int intercept = (int)(share / 100);
            carry = share % 100;
            return value - intercept + shield.Damage(intercept, bypass: false, now);
        }
        return (AfterIntercept(physical), AfterIntercept(other), AfterIntercept(chaos));
    }
}
