using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Torment's Ball Lightning (<c>EssenceDaemonSummonOrbOfStormsDaemon</c> → <c>EssenceDaemonBallLightning</c>):
/// flashes the summon lightning strike (<c>SummonedSpecificMonsters[61].SummonEffect = 460</c>,
/// <c>lightning_strike_herald.ao</c>) and launches slow-moving piercing <c>Metadata/Projectiles/BallLightning</c> orbs
/// (<c>base_projectile_speed_+% = -80</c>) toward the player.</summary>
public static partial class MapEssences
{
    const string TormentSummonArt = "Metadata/Effects/Spells/herald/lightning/lightning_strike_herald.ao";
    const string TormentBallLightningProjectile = "Metadata/Projectiles/BallLightning";

    static Task CastTormentBallLightning(ClientState conn, SpawnedMonster monster, ClientState target, SpecialSpell spell)
    {
        if (monster.Dead || monster.Life == 0 || monster.Variety is null) return Task.CompletedTask;
        var catalog = SkillsFor(conn);
        int effect = catalog.EffectRow("EssenceDaemonBallLightning");
        var damage = EssenceSkillDamage(conn, effect, monster.Level);
        if (damage.IsZero) return Task.CompletedTask;
        int count = ScaledSpellCount(spell.Pulse, spell.Tier, 1);
        var from = monster.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        uint flash = SpawnEffect(conn, from, TormentSummonArt);
        _ = RemovePulseLater(conn, flash, 0.8);
        var native = GolemData.Skill(catalog, effect, monster.Level);
        int Get(string name) => Stats.Key(name) is uint key ? native.Stats.GetValueOrDefault(key) : 0;
        int baseSpeed = catalog.Projectile(TormentBallLightningProjectile)?.Speed ?? 300;
        int speed = Math.Max(40, (int)Math.Round(baseSpeed * Math.Max(0.2, 1 + Get("base_projectile_speed_+%") / 100d)));
        var projectile = new SkillCatalog.MonsterProjectileInfo(TormentBallLightningProjectile, speed, false, 1)
        {
            Count = count,
            SpreadRadians = count > 1 ? 0.5 : 0,
            CollisionRadius = 4,
            AnimationKey = "SpellProjectile",
        };
        var attack = new MonsterAttack(0x4000, 1, 80, monster.Mods.Effects.Hit(damage))
        {
            Spell = true,
            Ranged = true,
            Ailments = IncomingAilmentSettings(Get),
            Projectile = projectile,
        };
        var session = ProjectilesFor(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn);
        var aim = target.Walk.Position(target.Clock.Elapsed.TotalSeconds);
        var use = MonsterProjectileUse(session, monster.ObjectId, monster, attack, projectile, monster.ActionGeneration, orderedAim: aim);
        use = use with { Profile = use.Profile with { Pierce = -1, Range = 75 } };
        session.Schedule(ProjectileRuntime.Time(ProjectileWorldTime(conn)),
            () => !monster.Dead && monster.Life > 0 && !conn.Closed, () => use);
        Console.WriteLine($"[map-content] essence torment ball lightning: {count} orb(s) from 0x{monster.ObjectId:x} at ({from.X}, {from.Y})");
        return Task.CompletedTask;
    }
}
