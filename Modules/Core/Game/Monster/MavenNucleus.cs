using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    /// <summary>
    /// Maven's Writ (Absence of Mercy): The Maven's first granted effects are teleports, Nucleus starts un-emerged
    /// without a boss bar, and neither fights at 15-cell melee. Live 2026-11-01: The Maven died in ~2 s, Nucleus formed
    /// at mapboss2 with <c>emerged</c> 0, and the arena never spawned orbs or volatile cores.
    /// </summary>
    public static class MavenNucleus
    {
        public const string Area = "MavenBoss";
        public const string Maven = "Metadata/Monsters/MavenBoss/TheMaven";
        public const string Nucleus = "Metadata/Monsters/MavenBoss/MavenBrainBoss";
        public const int HitCapPct = 8;
        public const int Phase1Radius = 22;
        public const int Phase2Radius = 12;

        public static readonly string[] Phase1Orbs =
        [
            "Metadata/Monsters/MavenBoss/TheMavenMapVolatileOrb",
            "Metadata/Monsters/MavenBoss/TheMavenMapChillOrb",
            "Metadata/Monsters/MavenBoss/TheMavenMapDegenOrb",
            "Metadata/Monsters/MavenBoss/TheMavenMapBeamOrb",
        ];

        public static readonly string[] Phase2Cores =
        [
            "Metadata/Monsters/MavenBoss/MavenBrainOrbitDaemon",
            "Metadata/Monsters/MavenBoss/MavenBrainOrbitDaemon",
            "Metadata/Monsters/MavenBoss/MavenBrainOrbitDaemon",
            "Metadata/Monsters/MavenBoss/MavenBrainOrbitDaemon",
        ];

        public static bool Is(string? varietyId) => varietyId is Maven or Nucleus;

        /// <summary>A hit on The Maven or Nucleus takes at most this much of maximum life, so the 100M% damage boost
        /// cannot skip the phase and the orbs.</summary>
        public static int CapHit(string? varietyId, uint maxLife, int damage)
        {
            if (!Is(varietyId) || damage <= 0) return damage;
            int cap = Math.Max(1, (int)(maxLife * (long)HitCapPct / 100));
            return Math.Min(damage, cap);
        }

        /// <summary>Nucleus's .ot starts <c>emerged</c> at 0: without flipping it the brain never draws.</summary>
        public static StateMachineData? StartStates(MonsterCatalog.Variety variety)
        {
            var states = variety.StateMachine;
            if (states is null) return null;
            return variety.Id == Nucleus ? states.Set("emerged", 1) : states;
        }

        /// <summary>The first granted effect the AI can actually use: skip teleports, daemons and the memory game, then
        /// prefer a projectile the server can fly.</summary>
        public static int CombatEffect(SkillCatalog skills, MonsterCatalog.Variety variety)
        {
            if (variety.GrantedEffects is not { Count: > 0 } effects) return -1;
            int? fallback = null;
            foreach (int effect in effects)
            {
                if (skills.IsMovement(effect)) continue;
                string? id = skills.EffectId(effect);
                if (id is not null && (id.Contains("Teleport", StringComparison.Ordinal)
                    || id.Contains("Daemon", StringComparison.Ordinal)
                    || id.Contains("MemoryGame", StringComparison.Ordinal)
                    // A bond, mark or soul skill links the monster to a second object the server does not create: Dominus'
                    // first effect, DominusChillingBond, as his plain attack crashed the client at 0x1401297ca when the
                    // client linked it to entity 0 (0x141a2bbb0, live 2026-11-05). His DominusLightningSlam is the swing.
                    || id.Contains("Bond", StringComparison.Ordinal)
                    || id.Contains("Mark", StringComparison.Ordinal)
                    || id.Contains("Souls", StringComparison.Ordinal))) continue;
                if (!(skills.IsAttack(effect) || skills.IsSpell(effect) || skills.IsProjectile(effect))) continue;
                if (Shot(skills, effect) is not null) return effect;
                fallback ??= effect;
            }
            // The leap's damage row asks Goatman2.act for GroundSlam. Use the monster's
            // ordinary swing when it has one; keep the damage row for payload-only actors.
            if (fallback is int chosen && skills.EffectId(chosen) == "GAGoatmanLeapSlamDamage")
                foreach (int effect in effects)
                    if (skills.EffectId(effect)?.StartsWith("MeleeAtAnimationSpeed", StringComparison.Ordinal) == true)
                        return effect;
            return fallback ?? effects[0];
        }

        public static bool AttackIsRanged(SkillCatalog skills, int effect) =>
            effect >= 0 && (skills.IsRangedAttack(effect)
                || skills.IsProjectile(effect) && !skills.IsMelee(effect)
                || skills.IsSpell(effect) && !skills.IsMelee(effect));

        public static SkillCatalog.MonsterProjectileInfo? Shot(SkillCatalog skills, int effect)
        {
            if (skills.MonsterProjectile(effect) is { } mortar) return mortar;
            return skills.EffectId(effect) switch
            {
                "MavenProjectile" => Named("MavenProjectile", 2250),
                "MavenBrainProjectileBlastMPS" => Named("MavenBrainProjectileBlast", 750),
                "MavenBrainLargeProjectileBarrageMPS" => Named("MavenBrainLargeProjectileBarrage", 300),
                "MavenBrainSmallProjectileBarrage" => Named("MavenBrainSmallProjectileBarrage", 750),
                _ => null,
            };
        }

        static SkillCatalog.MonsterProjectileInfo Named(string id, int speed) =>
            new($"Metadata/Projectiles/{id}", speed, false, 1) { AnimationKey = "SpellProjectile" };

        public static IEnumerable<(string Id, int X, int Y)> Around(IReadOnlyList<string> ids, (int X, int Y) centre, int radius)
        {
            int n = ids.Count;
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                yield return (ids[i], centre.X + (int)Math.Round(Math.Cos(a) * radius),
                    centre.Y + (int)Math.Round(Math.Sin(a) * radius));
            }
        }
    }
}
