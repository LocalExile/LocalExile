using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const int SpectreFireOffering = 5543, SpectreChaosOffering = 5545, JudgementSkeletonAura = 6860;
    const int SpectreAidRadius = 60;
    // ItemisedCorpse specifies the 20% exhaustion threshold and the share healed. Spell cost and recovery rate are
    // modelled estimates until the native AI values can be measured.
    const int SanguimancerSpellLifeCostPct = 10, SanguimancerExhaustedLifePct = 20, SanguimancerRecoveryPctPerSecond = 20;
    const ushort SanguimancerExhaustedBuff = 2388, SanguimancerSpellBuff = 2389;
    sealed class SanguimancerLifeState { public bool Exhausted, LowLifeAura; }
    static readonly RuleTable<GolemState.Golem, SanguimancerLifeState> SanguimancerLife = new();

    static bool IsSanguimancer(GolemState.Golem spectre) => spectre.Monster.Variety?.Row is 10214 or 10215 or 10216;

    static bool SpectreCanSpendLife(GolemState.Golem spectre, GolemState.Ability ability) =>
        !ability.Attack.Spell || !IsSanguimancer(spectre)
        || 100L * spectre.Monster.Life > SanguimancerExhaustedLifePct * (long)spectre.Monster.MaxLife
            && (!SanguimancerLife.TryGetValue(spectre, out var state) || !state.Exhausted);

    static bool TryPaySpectreLifeLocked(Player player, GolemState.Golem spectre, GolemState.Ability ability, double now)
    {
        if (!ability.Attack.Spell || !IsSanguimancer(spectre)) return true;
        if (!SpectreCanSpendLife(spectre, ability)) return false;
        uint cost = (uint)Math.Min(spectre.Monster.Life - 1L, Math.Max(1L, (long)spectre.Monster.MaxLife * SanguimancerSpellLifeCostPct / 100));
        spectre.Monster.Life -= cost;
        player.Minions.OutputQueue.Add(new([], "sanguimancer spell life cost", spectre.Id, spectre.Monster, PlayerMinions.OutputKind.Life));
        if (spectre.Monster.Variety?.Row is 10215 or 10216 && cost >= 10)
        {
            var at = spectre.Monster.Walk.Position(now);
            uint healing = cost / 10;
            Heal(player);
            foreach (var ally in player.Instance.PlayersExcept(player.Id))
                if (ReferenceEquals(ally.Area, player.Area)) Heal(ally);

            void Heal(Player recipient)
            {
                if (recipient.PlayerLife > 0 && recipient.PlayerLife < recipient.PlayerUnreservedLife
                    && Dist(recipient.Walk.Position(now), at) <= SpectreAidRadius)
                {
                    recipient.PlayerLife = (uint)Math.Min(recipient.PlayerUnreservedLife, (long)recipient.PlayerLife + healing);
                    recipient.Frames.Area?.Life.Resend(recipient);
                }
                foreach (var ally in UtilityFighters(recipient))
                {
                    var body = ally.Monster;
                    if (ReferenceEquals(body, spectre.Monster) || body.Life >= body.MaxLife
                        || !GolemCurrent(recipient, ally) || Dist(body.Walk.Position(now), at) > SpectreAidRadius) continue;
                    body.Life = (uint)Math.Min(body.MaxLife, (long)body.Life + healing);
                    recipient.Minions.OutputQueue.Add(new([], "sanguimancer ally recovery", ally.Id, body, PlayerMinions.OutputKind.Life));
                }
            }
        }
        _ = SpectreLifeRecoveryLocked(player, spectre, 0, now);
        return true;
    }

    static double SpectreLifeRecoveryLocked(Player player, GolemState.Golem spectre, double elapsed, double now)
    {
        if (!IsSanguimancer(spectre)) return 0;
        var body = spectre.Monster;
        var state = SanguimancerLife.GetOrCreateValue(spectre);
        bool exhausted = state.Exhausted ? body.Life < body.MaxLife
            : 100L * body.Life <= SanguimancerExhaustedLifePct * (long)body.MaxLife;
        if (exhausted != state.Exhausted)
        {
            state.Exhausted = exhausted;
            if (exhausted) player.Area.Buffs.Set(spectre.Id, new(SanguimancerExhaustedBuff, spectre.Id,
                Values: [(uint)(SanguimancerRecoveryPctPerSecond * 60)]));
            else player.Area.Buffs.Clear(spectre.Id, SanguimancerExhaustedBuff, spectre.Id);
        }
        bool low = spectre.Monster.Variety?.Row == 10216 && 2L * body.Life <= body.MaxLife;
        if (low != state.LowLifeAura)
        {
            state.LowLifeAura = low;
            var aura = NativeMinionAuraStates.GetOrCreateValue(spectre);
            var ability = spectre.Abilities.FirstOrDefault(a => a.Effect == 6865);
            aura.Auras = low && ability is not null ? [NativeMinionAura(ability.Id, SanguimancerSpellBuff, SpectreAidRadius,
                [(Stat.SpellDamagePlusPct, GolemStat(ability.Stats, Stat.SpellDamagePlusPct))])] : [];
            if (player.Minions.All.FirstOrDefault(m => m.Id == spectre.Id) is { } source)
                SyncSharedMinionAuras(player, source, aura.Auras, now, double.PositiveInfinity, registered: false);
        }
        return exhausted ? body.MaxLife * elapsed * SanguimancerRecoveryPctPerSecond / 100d : 0;
    }

    static void ReleaseSpectreOfferingLocked(Player player, GolemState.Golem spectre, GolemState.Action action, double now)
    {
        var ability = action.Ability;
        bool fire = ability.Effect == SpectreFireOffering;
        var type = fire ? DamageType.Fire : DamageType.Chaos;
        double seconds = GolemStat(ability.Stats, Stat.BaseSkillEffectDuration) / 1000d;
        uint term = fire ? Stat.BaseFireDamageToDealPerMinute : Stat.BaseChaosDamageToDealPerMinute;
        var (dps, basis, increase) = GolemHazardDamage(player, spectre, now, GolemStat(ability.Stats, term) / 60d, area: true);
        if (seconds <= 0 || dps <= 0) return;
        uint? art = null;
        var at = action.Aim;
        if (GroundKindOf(fire ? "FireOfferingOfJudgement" : "ChaosOfferingOfJudgement") is { } kind)
        {
            art = player.TakeObjectId();
            float scale = kind.ArtCells > 0 ? (float)(ability.Radius / kind.ArtCells) : 1;
            var ground = new GroundEffectObject(at.X, at.Y, kind.ArtRow, kind.Art, (float)seconds) { Scale = scale, Team = 0 };
            player.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(art.Value,
                InstanceEntities.ObjectTypeHash(GroundEffectObject.TypePath), ground.Blob()), null, WorldId: art, CreatesObject: true));
            player.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(art.Value), null, WorldId: art));
        }
        spectre.Hazards.Add(new(ability.Effect, at, now + seconds, ability.Radius, dps, type,
            UnitDamagePerSecond: basis, NormalDamageIncrease: increase) { LastAt = now, Object = art });
    }

    static readonly uint[] JudgementAttackDamage =
    [
        Stat.AttackMinimumAddedFireDamage, Stat.AttackMaximumAddedFireDamage,
        Stat.AttackMinimumAddedChaosDamage, Stat.AttackMaximumAddedChaosDamage,
    ];

    static void AddSpectreSkeletonDamage(Player player, GolemState.Golem recipient, Dictionary<uint, int> stats, double now)
    {
        if (recipient.Definition.Kind is not ("Skeleton" or "VaalSkeleton" or "SpectreSkeleton")) return;
        var at = recipient.Monster.Walk.Position(now);
        GolemState.Ability? strongest = null;
        int largest = 0;
        Include(player);
        foreach (var peer in player.Instance.PlayersExcept(player.Id))
            if (ReferenceEquals(peer.Area, player.Area)) Include(peer);
        if (strongest is null) return;
        foreach (var key in JudgementAttackDamage)
            stats[key] = ActorScale.Saturate((long)stats.GetValueOrDefault(key) + GolemStat(strongest.Stats, key));

        void Include(Player owner)
        {
            foreach (var spirit in owner.Summons.Fighters.Values)
            {
                if (spirit.Monster?.Variety?.Row is not (10241 or 10242) || spirit.Monster.Dead || spirit.Monster.Life == 0 || owner.PlayerLife == 0
                    || !GolemCurrent(owner, spirit) || Dist(spirit.Monster.Walk.Position(now), at) > SpectreAidRadius
                    || spirit.Abilities.FirstOrDefault(a => a.Effect == JudgementSkeletonAura) is not { } aura) continue;
                int strength = JudgementAttackDamage.Sum(key => GolemStat(aura.Stats, key));
                if (strength > largest) (strongest, largest) = (aura, strength);
            }
        }
    }
}
