using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string ExplosiveConcoctionEffect = "ExplosiveConcoction";
    const string DestructionConcoctionEffect = "ExplosiveConcoctionAltX";
    const string PoisonousConcoctionEffect = "PoisonousConcoction";
    const string BouncingConcoctionEffect = "PoisonousConcoctionAltX";

    static bool IsConcoction(string? effect) => effect is ExplosiveConcoctionEffect or DestructionConcoctionEffect
        or PoisonousConcoctionEffect or BouncingConcoctionEffect;

    static readonly ProjectileSkillDefinition ExplosiveConcoctionProjectile = ConcoctionProjectile(poisonous: false, bouncing: false);
    static readonly ProjectileSkillDefinition PoisonousConcoctionProjectile = ConcoctionProjectile(poisonous: true, bouncing: false);
    static readonly ProjectileSkillDefinition BouncingConcoctionProjectile = ConcoctionProjectile(poisonous: true, bouncing: true);

    static ProjectileSkillDefinition? ConcoctionDefinition(string effect) => effect switch
    {
        ExplosiveConcoctionEffect or DestructionConcoctionEffect => ExplosiveConcoctionProjectile,
        PoisonousConcoctionEffect => PoisonousConcoctionProjectile,
        BouncingConcoctionEffect => BouncingConcoctionProjectile,
        _ => null
    };

    static ProjectileSkillDefinition ConcoctionProjectile(bool poisonous, bool bouncing)
    {
        string metadata = poisonous ? "Metadata/Projectiles/PoisonousConcoction" : "Metadata/Projectiles/IgnitingFlaskProjectile";
        return new(metadata, new((get, speed, _) =>
        {
            var profile = GatewayProjectileDefaults.Create(metadata, get, speed) with
            {
                Count = bouncing ? 1 : Math.Max(1, get(Stat.BaseNumberOfProjectiles)),
                CountConsumer = bouncing ? ProjectileCountConsumer.GroundChains : ProjectileCountConsumer.Primary,
                Lobbed = true, DirectDamage = false, RandomGroundBounceDirection = bouncing,
                CannotPierce = true, CannotFork = true, CannotSplit = true, CannotReturn = true
            };
            var resolved = GatewayProjectileModifiers.Resolve(profile, get, attack: true);
            return resolved with
            {
                Chain = 0, CannotChain = true,
                GroundChains = bouncing && !resolved.CannotChain ? Math.Clamp(resolved.GroundChains, 0, 256) : 0
            };
        }))
        {
            CancelWindupOnNewAction = true,
            SpecializeRelease = (context, use) =>
            {
                var shot = (ProjectileOffense)use.Payload!;
                string impact = poisonous ? "Metadata/Effects/Spells/flask_throw_poison/explosion_01.ao"
                    : "Metadata/Effects/Spells/flask_throw/explode_base.ao";
                var radius = SkillRadius(context.Player, shot.SkillStats, spell: false, shot.Effect);
                return use with
                {
                    Profile = use.Profile with
                    {
                        EndArea = new(poisonous ? "concoction-poison" : "concoction-explosion", radius),
                        EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing, ProjectileEndReason.Terrain }
                    },
                    Payload = shot with { AreaVisual = Art.AnimatedInPlaceOf(impact, context.Variation) }
                };
            }
        };
    }

    enum ConcoctionFlaskFamily { None, Ruby, Sapphire, Topaz, Life }

    readonly record struct ConcoctionFlaskBonus(Dictionary<uint, int>? Modifiers, int CriticalMultiplier);

    static ConcoctionFlaskBonus SpendConcoctionFlasks(ProjectileSession session, string effect, int count, Func<uint, int> get)
    {
        bool poisonous = effect is PoisonousConcoctionEffect or BouncingConcoctionEffect;
        long baseCost = get(Stat.FlaskThrowBaseChargesUsed) + (long)get(Stat.FlaskThrowChargesUsedPerProjectile) * count;
        if (baseCost <= 0) return default;
        long increased = (long)get(Stat.FlaskChargesUsedPlusPct) + get(poisonous
            ? Stat.PoisonousConcoctionFlaskChargesConsumedPlusPct : Stat.ExplosiveConcoctionFlaskChargesConsumedPlusPct);
        int cost = (int)Math.Clamp(Math.Floor(baseCost * (1 + increased / 100d)), 1, int.MaxValue);
        var player = session.Player;
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        var spent = new HashSet<ConcoctionFlaskFamily>();
        var changed = new List<InventoryItem>(poisonous ? 1 : 3);
        var modifiers = new Dictionary<uint, int>();
        int criticalMultiplier = 0;
        foreach (var flask in items.Items(PlayerInventories.Flask).OrderBy(f => f.X))
        {
            if (flask.Item.Charges < cost || catalog.ByHash(flask.Item.TypeHash) is not { } type) continue;
            var charges = catalog.FlaskChargesOf(type);
            var family = poisonous ? charges is { LifePerUse: > 0, ManaPerUse: 0 }
                ? ConcoctionFlaskFamily.Life : ConcoctionFlaskFamily.None
                : NamedFlaskRecent(type.Id) switch
                {
                    StatConditions.UsedRubyFlask => ConcoctionFlaskFamily.Ruby,
                    StatConditions.UsedSapphireFlask => ConcoctionFlaskFamily.Sapphire,
                    StatConditions.UsedTopazFlask => ConcoctionFlaskFamily.Topaz,
                    _ => ConcoctionFlaskFamily.None
                };
            if (family == ConcoctionFlaskFamily.None || spent.Contains(family) || charges is null) continue;
            Item? committed = null;
            var after = items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, current =>
            {
                if (current.TypeHash != flask.Item.TypeHash || current.Charges < cost) return current;
                committed = current;
                return Flasks.Spend(current, charges with { ChargesPerUse = cost })!;
            });
            if (committed is null || after is null) continue;
            changed.Add(after);
            spent.Add(family);
            if (poisonous)
            {
                var recovery = FlaskRecovery.For(charges, committed, FlaskLocalStats(player, committed), _ => 0,
                    life: player.PlayerMaxLife, maxLife: player.PlayerMaxLife);
                int extra = (int)Math.Clamp(Math.Floor((recovery.Life + recovery.InstantLife)
                    * get(Stat.FlaskThrowAddedChaosDamagePctOfFlaskLifeToRecover) / 100), 0, int.MaxValue);
                modifiers[Stat.AttackMinimumAddedChaosDamage] = modifiers[Stat.AttackMaximumAddedChaosDamage] = extra;
                break;
            }
            switch (family)
            {
                case ConcoctionFlaskFamily.Ruby:
                    modifiers[Stat.IgniteDotMultiplierPlus] = get(Stat.FlaskThrowRubyFlaskIgniteDotMultiplierPlus);
                    criticalMultiplier = get(Stat.FlaskThrowRubyFlaskCriticalStrikeMultiplierPlus);
                    break;
                case ConcoctionFlaskFamily.Sapphire:
                    modifiers[Stat.AttackMinimumAddedColdDamage] = get(Stat.FlaskThrowMinimumColdDamageIfUsedSapphireFlask);
                    modifiers[Stat.AttackMaximumAddedColdDamage] = get(Stat.FlaskThrowMaximumColdDamageIfUsedSapphireFlask);
                    break;
                case ConcoctionFlaskFamily.Topaz:
                    modifiers[Stat.AttackMinimumAddedLightningDamage] = get(Stat.FlaskThrowMinimumLightningDamageIfUsedTopazFlask);
                    modifiers[Stat.AttackMaximumAddedLightningDamage] = get(Stat.FlaskThrowMaximumLightningDamageIfUsedTopazFlask);
                    break;
            }
        }
        if (changed.Count == 0) return default;
        session.FlaskBarChanged(changed);
        if (ServerTrace.Skills)
            Console.WriteLine($"[gateway] {player.Connection.Remote} concoction {effect} bottles={count} spent={cost} "
                + $"flasks={string.Join(',', changed.Select(item => item.X))} crit={criticalMultiplier} "
                + $"bonus={string.Join(',', modifiers.Select(pair => $"{pair.Key}:{pair.Value}"))}");
        return new(modifiers, criticalMultiplier);
    }
}
