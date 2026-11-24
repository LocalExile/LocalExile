using GameServer.Core.World;

public static partial class Game
{
    const string FrozenCannibal = "Metadata/Monsters/LeagueAzmeri/SpecialCorpses/Hailrake/";
    const string FieryCannibal = "Metadata/Monsters/LeagueAzmeri/SpecialCorpses/Firefury/";
    const int CannibalGlacialHammer = 1016, CannibalGlacialCascade = 3342, CannibalIceCrash = 4823, CannibalIceNova = 7601;
    const int CannibalMoltenStrike = 3756, CannibalCremation = 8817;
    const string SerpentWarrior = "Metadata/Monsters/LeagueAzmeri/SpecialCorpses/Basalisk";
    const int SerpentSword = 417, SerpentShroud = 6863;
    const int NavalCannonFire = 10349, NavalCannonImpact = 7603;
    const int CannibalPartnerRadius = 60;

    static bool SpectreShoulderMortar(int effect) => effect is 9992 or 9994;

    static bool SpectreUtilityAction(int effect) => effect is NavalCannonFire or SpectreFireOffering or SpectreChaosOffering
        or AstralMineCast or PustuleCast or AstralProjectileCast;

    static bool SpectreVarietyStartsWith(GolemState.Golem fighter, string family) =>
        fighter.Monster.Variety?.Id.StartsWith(family, StringComparison.Ordinal) == true;

    static bool CannibalEmpowered(Player player, GolemState.Golem fighter, double now)
    {
        string? partner = SpectreVarietyStartsWith(fighter, FrozenCannibal) ? FieryCannibal
            : SpectreVarietyStartsWith(fighter, FieryCannibal) ? FrozenCannibal : null;
        if (partner is null) return false;
        var at = fighter.Monster.Walk.Position(now);
        return HasPartner(player) || player.Instance.PlayersExcept(player.Id).Any(
            ally => ReferenceEquals(player.Area, ally.Area) && HasPartner(ally));

        bool HasPartner(Player owner) => owner.PlayerLife > 0 && owner.Summons.Fighters.Values.Any(other =>
            other.Definition.Kind == "Spectre" && SpectreVarietyStartsWith(other, partner)
            && !other.Monster.Dead && other.Monster.Life > 0 && GolemCurrent(owner, other)
            && Dist(other.Monster.Walk.Position(now), at) <= CannibalPartnerRadius);
    }

    static bool SpectreActionAllowed(GolemState.Golem fighter, GolemState.Ability ability, bool defensive,
        bool pairedCannibal, double distance)
    {
        if (ability.Effect is CannibalIceCrash or CannibalIceNova && SpectreVarietyStartsWith(fighter, FrozenCannibal)) return false;
        if (ability.Effect is CannibalMoltenStrike or CannibalCremation && SpectreVarietyStartsWith(fighter, FieryCannibal))
            return pairedCannibal;
        if (SpectreVarietyStartsWith(fighter, SerpentWarrior))
            return defensive ? SpectreShoulderMortar(ability.Effect)
                && distance >= GolemStat(ability.Stats, Stat.ProjectileMinimumRange) : ability.Effect == SerpentSword;
        return ability.Effect is not (SerpentShroud or NavalCannonImpact or AstralProjectileImpact or AstralMineImpact or PustuleImpact);
    }

    static bool ReleaseSpectreRoleLocked(Player player, GolemState.Golem fighter, GolemState.Action action, double now)
    {
        if (SpectreVarietyStartsWith(fighter, SerpentWarrior) && (action.Ability.Effect == SerpentSword || SpectreShoulderMortar(action.Ability.Effect)))
        {
            var source = SupportedSkill(player, fighter.Source);
            var (_, defensive) = MinionStance(source.Stats, fighter.Player, false, now - fighter.BlockedAt is >= 0 and <= 4);
            if (!source.RequirementsMet || (defensive && !HearsWhispers(fighter, now)) != SpectreShoulderMortar(action.Ability.Effect)) return true;
        }
        if (!TryPaySpectreLifeLocked(player, fighter, action.Ability, now)) return true;
        if (ReleaseSpectreTimedPayloadLocked(player, fighter, action, now)) return true;
        if (action.Ability.Effect is SpectreFireOffering or SpectreChaosOffering)
        {
            ReleaseSpectreOfferingLocked(player, fighter, action, now);
            return true;
        }
        if (action.Ability.Effect is CannibalMoltenStrike or CannibalCremation && SpectreVarietyStartsWith(fighter, FieryCannibal))
            return !CannibalEmpowered(player, fighter, now);
        if (action.Ability.Effect == TurtleBoon)
        {
            GrantTurtleBoonLocked(player, fighter, action.Ability, now);
            return true;
        }
        if (action.Ability.Effect != NavalCannonFire) return false;
        var catalog = SkillsFor(player);
        if (fighter.Abilities.FirstOrDefault(a => a.Effect == NavalCannonImpact) is { } impact
            && catalog.Projectile("Metadata/Projectiles/GhostCannonball") is { } projectile)
        {
            var payload = new SkillCatalog.MonsterProjectileInfo(projectile.Id, projectile.Speed, true,
                catalog.ProjectileContactCells(projectile.Id) ?? 2);
            GolemLaunch(player, fighter, impact with { Projectile = payload }, action.Aim, now);
        }
        return true;
    }

    static void SpectreRoleOnHitLocked(Player player, GolemState.Golem fighter, GolemState.Ability hit, uint id,
        SpawnedMonster victim, double now)
    {
        var at = victim.Walk.Position(now);
        if (hit.Effect is CannibalGlacialHammer or CannibalGlacialCascade && SpectreVarietyStartsWith(fighter, FrozenCannibal)
            && CannibalEmpowered(player, fighter, now)
            && fighter.Abilities.FirstOrDefault(a => a.Effect == (hit.Effect == CannibalGlacialHammer ? CannibalIceCrash : CannibalIceNova)) is { } burst)
        {
            foreach (var (enemyId, enemy) in player.Area.MonstersNear(at, burst.Radius + 8).ToArray())
                if (Dist(enemy.Walk.Position(now), at) <= burst.Radius + LivingCollision.Size(enemy))
                    GolemHitLocked(player, fighter, burst, enemyId, enemy, now);
            if (!victim.Dead && Dist(victim.Walk.Position(now), at) <= burst.Radius && !player.Area.MonstersNear(at, burst.Radius + 8)
                .Any(e => e.Key == id)) GolemHitLocked(player, fighter, burst, id, victim, now);
        }
        if (hit.Attack.Spell || victim.Poisons.Stacks(now) == 0
            || fighter.Abilities.FirstOrDefault(a => a.Effect == SerpentShroud) is not { } shroud
            || !GolemReady(fighter, shroud, now) || fighter.Hazards.Any(h => h.Effect == SerpentShroud && h.Until > now)) return;
        double duration = GolemStat(shroud.Stats, Stat.BaseSkillEffectDuration) / 1000d;
        var (dps, basis, increase) = GolemHazardDamage(player, fighter, now,
            GolemStat(shroud.Stats, Stat.BaseChaosDamageTakenPerMinute) / 60d, area: true);
        if (duration <= 0 || dps <= 0) return;
        GolemStartCooldown(fighter, shroud, now);
        fighter.Hazards.Add(new(SerpentShroud, fighter.Monster.Walk.Position(now), now + duration, shroud.Radius, dps,
            DamageType.Chaos, Follows: true, UnitDamagePerSecond: basis, NormalDamageIncrease: increase) { LastAt = now });
    }
}
