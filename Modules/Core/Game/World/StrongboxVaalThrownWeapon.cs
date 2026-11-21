using GameServer.Core.Projectiles;
using GameServer.Core.World;
using static Game;
using static Program;

public static partial class MapStrongboxes
{
    const string ThrownWeaponEffect = "ChestVaalThrownWeapon";
    const string ThrownProjectile = "Metadata/Projectiles/ThrownWeapon";

    static VesselProjectileSkill ReadThrownWeapon(ClientState c, string daemon)
    {
        var catalog = SkillsFor(c);
        int effect = catalog.EffectRow(ThrownWeaponEffect);
        byte level = (byte)AreaLevel(c);
        var variety = Monsters.Find(daemon)!;
        var native = GolemData.Skill(catalog, effect, level);
        int Required(string id) => native.Stats[Stats.Key(id)!.Value];
        int Get(string id) => Stats.Key(id) is uint key ? native.Stats.GetValueOrDefault(key) : 0;

        int baseCount = Required("base_number_of_projectiles_in_spiral_nova");
        int count = baseCount + Required("number_of_additional_projectiles");
        int arms = Required("projectile_spiral_nova_both_directions") != 0 ? 2 : 1;
        double angleStep = Required("projectile_spiral_nova_angle") / (double)baseCount;
        int offset = Required("projectile_spiral_nova_starting_angle_offset");
        long interval = ProjectileRuntime.Time(Required("projectile_spiral_nova_time_ms") / (1000d * baseCount));
        var angles = new double[count * arms];
        // Each additional weapon extends both spirals at the same spacing as the base shots.
        for (int i = 0; i < count; i++)
        {
            double angle = (offset + angleStep * i) * Math.PI / 180;
            angles[i * arms] = angle;
            if (arms == 2) angles[i * arms + 1] = -angle;
        }

        var row = catalog.Projectile(ThrownProjectile)!.Value;
        double speed = ProjectileSpeed.CellsPerSecond(row.Speed, _ => 0, Get);
        int contact = catalog.ProjectileContactCells(ThrownProjectile)!.Value;
        var profile = GatewayProjectileSkillProfiles.Resolve("SpectralThrow", ThrownProjectile, Get, speed, attack: true) with
        {
            Count = angles.Length, NativeVolley = new() { Angles = angles },
            SequentialDelay = interval, SequentialBatchSize = arms, ShareSequentialHits = true,
            Legs = _ => new SpectralThrowLegs(speed), LegLeash = SpectralThrowLegs.Leash(speed),
            ReturnOvershoot = 0, ReturnSpeedMultiplier = SpectralThrowLegs.ReturnSpeedMultiplier,
            HitRule = new(ProjectileHitScope.Emission, ProjectileRepeat.ContactInterval,
                ProjectileRuntime.Time(Required("spectral_throw_forget_hit_list_time_override") / 1000d)),
            ContactCells = contact, StopsAtTerrain = true
        };
        // ThrownWeaponProjectile draws the equipped weapon. Its native count describes each mirrored arm.
        native.Stats[Stats.Key("total_number_of_projectiles_to_fire")!.Value] = count;
        native.Stats[Stats.Key("virtual_total_number_of_projectiles")!.Value] = count;
        var projectile = new SkillCatalog.MonsterProjectileInfo(ThrownProjectile, row.Speed, false, contact) { AnimationKey = "SpectralThrow" };
        var (min, max) = Monsters.BaseDamage(variety, level)!.Value;
        var damage = DamagePacket.Of(DamageType.Physical, min, max).Scale(native.Multiplier);
        ushort action = (ushort)(catalog.ActionHash(effect)!.Value & 0x3fff);
        double seconds = variety.AttackMs / (1000d * (1 + catalog.AttackSpeedMultiplier(effect, native.Level) / 100d));
        var attack = new MonsterAttack(action, seconds, profile.Range, damage)
            { Ranged = true, Projectile = projectile, CannotBeEvaded = Get("global_always_hit") != 0, Ailments = IncomingAilmentSettings(Get) };
        var skill = new VesselSkill(effect, action, level, native.Stats, attack, native.Crit);
        // This daemon has no decoded SpectralThrow contact. Use the ordinary monster attack midpoint in that case.
        double release = MonsterReleaseFor(c, variety, effect, level, projectile)?.Fraction ?? .5;
        long lifetime = (count - 1) * interval + ProjectileRuntime.Time(new SpectralThrowLegs(speed).Lifetime);
        return new(skill, new(attack, damage), profile, TimeSpan.FromSeconds(seconds * release).Ticks, lifetime);
    }
}
