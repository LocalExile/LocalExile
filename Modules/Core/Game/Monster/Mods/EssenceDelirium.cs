using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Delirium's piercing chaos projectile (<c>EssenceChaosProjectileWithGroundEffects</c>): fires
/// <c>OfferingOfJudgementProjectileChaos</c> (<c>always_pierce</c>, +50% projectile speed) and drops caustic ground
/// clouds (<c>caustic_02.ao</c>) along its path. Its summoned Explosive Crawlers (<c>EssenceInsectWildExplosive</c>) stay
/// in the kit and detonate via <c>EssenceDeathEffects.cs</c>.</summary>
public static partial class MapEssences
{
    const string DeliriumChaosProjectile = "Metadata/Projectiles/OfferingOfJudgementProjectileChaos";
    const string DeliriumCausticGroundArt = "Metadata/Effects/Spells/ground_effects/caustic/caustic_02.ao";

    static Task CastDeliriumChaosProjectile(Player conn, SpawnedMonster monster, Player target, SpecialSpell spell)
    {
        if (monster.Dead || monster.Life == 0 || monster.Variety is null || !Areas.TryGetValue(conn.Instance, out var area))
            return Task.CompletedTask;
        var catalog = SkillsFor(conn);
        var damage = EssenceSkillDamage(conn, spell.Pulse, monster.Level);
        var dot = EssenceSkillDot(conn, spell.Pulse, monster.Level);
        var native = GolemData.Skill(catalog, spell.Pulse, monster.Level);
        int Get(string name) => Stats.Key(name) is uint key ? native.Stats.GetValueOrDefault(key) : 0;
        int baseSpeed = catalog.Projectile(DeliriumChaosProjectile)?.Speed ?? 1000;
        int speed = Math.Max(100, (int)Math.Round(baseSpeed * Math.Max(0.2, 1 + Get("base_projectile_speed_+%") / 100d)));
        var projectile = new SkillCatalog.MonsterProjectileInfo(DeliriumChaosProjectile, speed, false, 1)
        {
            Count = 1,
            AnimationKey = "SpellProjectile",
        };
        var attack = new MonsterAttack(0x4000, 1, 75, monster.Mods.Effects.Hit(damage))
        {
            Spell = true,
            Ranged = true,
            Projectile = projectile,
        };
        var from = monster.Walk.Position(AreaTime(conn));
        var aim = target.Walk.Position(AreaTime(target));
        var session = ProjectilesFor(conn);
        var use = MonsterProjectileUse(session, monster.ObjectId, monster, attack, projectile, monster.ActionGeneration, orderedAim: aim);
        use = use with { Profile = use.Profile with { Pierce = -1, Range = 75 } };
        session.Schedule(ProjectileRuntime.Time(ProjectileWorldTime(conn)),
            () => !monster.Dead && monster.Life > 0 && !conn.Closed, () => use);
        if (!dot.IsZero)
        {
            double radius = Math.Max(6, Get("projectile_ground_caustic_cloud_radius"));
            double duration = Math.Max(2, Get("base_projectile_ground_effect_duration") / 1000.0);
            double dx = aim.X - from.X, dy = aim.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
            double ux = len < 1 ? 1 : dx / len, uy = len < 1 ? 0 : dy / len;
            double cps = ProjectileSpeed.CellsPerSecond(speed, _ => 0, _ => 0);
            _ = DropDeliriumCausticTrail(conn, area, from, ux, uy, radius, duration, dot, cps);
        }
        Console.WriteLine($"[map-content] essence delirium chaos projectile from 0x{monster.ObjectId:x} toward ({aim.X}, {aim.Y})");
        return Task.CompletedTask;
    }

    static async Task DropDeliriumCausticTrail(Player conn, Area area, (int X, int Y) from, double ux, double uy,
        double radius, double duration, DamagePacket dot, double cellsPerSecond)
    {
        double prevDelay = 0;
        for (double d = 10; d <= 58; d += 12)
        {
            double delay = d / Math.Max(10, cellsPerSecond);
            await AreaDelay(conn, Math.Max(0.05, delay - prevDelay));
            prevDelay = delay;
            if (conn.Closed || conn.LeavingArea) return;
            (int X, int Y) spot = (from.X + (int)Math.Round(ux * d), from.Y + (int)Math.Round(uy * d));
            if (!conn.Layout.WalkableFor(spot.X, spot.Y, 0)) return;
            AddGroundZone(conn, area, spot, DeliriumCausticGroundArt, radius, duration, dot);
        }
    }
}