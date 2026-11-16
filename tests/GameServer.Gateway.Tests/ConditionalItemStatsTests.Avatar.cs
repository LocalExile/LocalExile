using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_avatar_rotation_and_permanent_sources_reach_final_consumers(ClientState conn,
        Func<string, byte, InventoryItem> wear)
    {
        var rotation = new AvatarOfFireRotation(); var rules = new object();
        rotation.Reconcile(rules, 1, [1], 0);
        foreach (var (at, active) in new[] { (0d, false), (7.999, false), (8d, true), (11.999, true),
                     (12d, false), (16d, true), (20d, false), (8008d, true) })
            Assert.Equal(active, rotation.Read(at).Active);
        rotation.Reconcile(rules, 1, [1], 9); Assert.Equal(0, rotation.Started);
        rotation.Reconcile(rules, 1, [2], 9); Assert.False(rotation.Read(16.999).Active);
        Assert.True(rotation.Read(17).Active);
        rotation.Reconcile(rules, 2, [2], 18); Assert.False(rotation.Read(18).Active);
        rotation.Reconcile(new object(), 2, [2], 19); Assert.Equal(19, rotation.Started);
        rotation.Buff = 41; rotation.Reconcile(rules, 2, [], 20);
        Assert.Contains((ushort)41, rotation.Retired); Assert.False(rotation.Read(100).Active);
        Assert.Equal(new[] { 5333 }, ClientTables.Table("BuffDefinitions")[Game.AvatarRotationBuffRow].Ints("StatsKeys"));

        var inventory = Game.ItemsOf(conn);
        var stats = Game.CharacterPlayerStats(conn);
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        int armourBefore = Get("base_physical_damage_reduction_rating");
        var weapon = wear("Vulconus", PlayerInventories.Weapon1);
        stats = Game.CharacterPlayerStats(conn);
        int criticalBefore = Get("critical_strike_chance_+%"); // Includes this dagger's unconditional implicit.
        Assert.Equal(armourBefore + 2000, Get("base_physical_damage_reduction_rating"));
        Assert.Equal(0, Get(Game.AvatarKeystone));
        Vulconus_typed_target_increases_follow_conversion_and_ailments(conn, stats);
        var source = Game.AvatarRotationOf(conn);
        Assert.Equal(new[] { weapon.Id }, source.Hosts);
        double now = Game.ResourceEffectsTime(conn);
        void RestoreAge(double age)
        {
            // Exercise the production area-transfer restoration, using this exact qualified physical host.
            Game.RestoreAvatarOfFire(conn, new([weapon.Id], age), now);
            stats = Game.CharacterPlayerStats(conn);
        }
        RestoreAge(8);
        Assert.Equal(1, Get(Game.AvatarKeystone));
        Assert.Equal(armourBefore, Get("base_physical_damage_reduction_rating"));
        Assert.True(Get("critical_strike_chance_+%") - criticalBefore is >= 160 and <= 200);
        Assert.Equal(50, Get("physical_damage_%_to_convert_to_fire"));
        Assert.Equal(50, Get("base_physical_damage_%_to_convert_to_fire"));
        var hit = DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Lightning, new(100, 100))
            .Add(DamageType.Cold, new(100, 100)).Add(DamageType.Fire, new(100, 100)).Add(DamageType.Chaos, new(100, 100));
        foreach (bool spell in new[] { false, true })
        {
            var conversion = DamageStats.Conversion(stats, Program.Stats, attack: !spell);
            var raw = hit.Convert(conversion);
            Assert.Equal(300, raw[DamageType.Fire].Min);
            var final = PlayerHitDamage.Resolve(hit, Get, conversion, spell);
            Assert.True(final[DamageType.Fire].Min >= 300);
            foreach (var type in DamageTypes.ConversionOrder.Where(t => t != DamageType.Fire))
                Assert.Equal(0, final[type].Max);
        }
        var avatarPackets = Game.AvatarOfFirePresentation(conn)!; Assert.NotNull(avatarPackets); Assert.True(source.Buff > 0);
        var avatarPacket = Assert.Single(avatarPackets, p => p[0] == 1 && p[1] == 0xc1);
        Assert.Equal(31, avatarPacket.Length); // Row1012 is not IsRecovery: duration plus an empty additional-data list.
        var carried = Assert.IsType<Game.CarriedAvatar>(Game.CaptureAvatarOfFire(conn, now));
        Assert.Equal(8, carried.Age, 6);
        Game.RestoreAvatarOfFire(conn, carried, now + 100);
        Assert.True(source.Read(now + 100).Active); Assert.False(source.Read(now + 104).Active);
        RestoreAge(12);
        Assert.Equal(0, Get(Game.AvatarKeystone));
        Assert.Equal(armourBefore + 2000, Get("base_physical_damage_reduction_rating"));
        Assert.Equal(criticalBefore, Get("critical_strike_chance_+%"));
        Assert.Equal(0, Get("base_deal_no_physical_damage"));
        Assert.NotNull(Game.AvatarOfFirePresentation(conn)); Assert.Equal((ushort)0, source.Buff);

        var permanent = wear("Xoph's Blood", 5);
        stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(1, Get(Game.AvatarKeystone));
        Assert.Equal(armourBefore, Get("base_physical_damage_reduction_rating"));
        Assert.Equal(50, Get("physical_damage_%_to_convert_to_fire"));
        inventory.Remove(PlayerInventories.Weapon1, weapon.Id);
        stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(1, Get(Game.AvatarKeystone));
        Assert.Equal(50, Get("base_physical_damage_%_to_convert_to_fire"));
        Assert.Equal(0, Get("physical_damage_%_to_convert_to_fire"));
        Assert.Empty(source.Hosts);
        inventory.Remove(5, permanent.Id); stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(0, Get(Game.AvatarKeystone)); Assert.Equal(0, Get("base_deal_no_physical_damage"));

        // The retained legacy weapon-local row must stay on its hand; it is not a current Vulconus factory claim.
        var legacy = Mod("ChanceToBleedWithoutAvatarOfFireUnique__1");
        var diagnostic = new UniqueCatalog(Program.Items).Create("Vulconus", new Random(504))!;
        diagnostic = diagnostic with { Mods = [.. diagnostic.Mods.Where(m => m.Hash != Mod("LocalChanceToBleedUnique__1__").Hash),
            legacy.Roll(ModSection.Explicit, new Random(504))] };
        weapon = inventory.Add(PlayerInventories.Weapon1, 0, 0, diagnostic);
        conn.Pools = Game.PoolsFor(conn, 100); stats = Game.CharacterPlayerStats(conn);
        Assert.False(Game.IsInactive(conn, weapon.Id));
        Assert.Equal(50, Game.WeaponFor(conn).LocalSources!.GetValueOrDefault("local_chance_to_bleed_on_hit_%"));
        RestoreAge(8);
        Assert.Equal(0, Game.WeaponFor(conn).LocalSources!.GetValueOrDefault("local_chance_to_bleed_on_hit_%"));
        RestoreAge(12);
        Assert.Equal(50, Game.HandLocalStats(conn).Main.GetValueOrDefault("local_chance_to_bleed_on_hit_%"));
        conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
        stats = Game.CharacterPlayerStats(conn);
        Assert.True(Game.IsInactive(conn, weapon.Id)); Assert.Empty(source.Hosts);
        Assert.Equal(0, Get(Game.AvatarKeystone));
        conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
        stats = Game.CharacterPlayerStats(conn);
        Assert.False(Game.IsInactive(conn, weapon.Id)); Assert.Equal(0, Get(Game.AvatarKeystone));
        Assert.Equal(new[] { weapon.Id }, source.Hosts);
        inventory.Remove(PlayerInventories.Weapon1, weapon.Id); stats = Game.CharacterPlayerStats(conn);
        Assert.Empty(source.Hosts); Assert.Equal(0, Get(Game.AvatarKeystone));
        Game.RestoreAvatarOfFire(conn, carried, now); stats = Game.CharacterPlayerStats(conn);
        Assert.Empty(source.Hosts); Assert.Equal(0, Get(Game.AvatarKeystone)); // A carried stale host cannot reactivate.
        weapon = wear("Vulconus", PlayerInventories.Weapon1); RestoreAge(8);
        Assert.Equal(1, Get(Game.AvatarKeystone));
        Assert.NotNull(Game.AvatarOfFirePresentation(conn)); Assert.True(source.Buff > 0);
        ushort deathBuff = source.Buff;
        conn.PlayerLife = 0; stats = Game.CharacterPlayerStats(conn);
        Assert.Empty(source.Hosts); Assert.Equal(0, Get(Game.AvatarKeystone));
        var ownCleanup = Game.AvatarOfFirePresentation(conn) ?? [];
        byte[] cleanup = [.. ownCleanup.SelectMany(p => p), .. Game.BlindPresentation(conn) ?? []];
        Assert.True(cleanup.AsSpan().IndexOf(InstanceEntities.RemoveBuff(Program.PlayerId, deathBuff)) >= 0);
        Assert.Equal((ushort)0, source.Buff);
    }

    void Vulconus_typed_target_increases_follow_conversion_and_ailments(ClientState conn,
        IReadOnlyDictionary<uint, int> actual)
    {
        const string physical = "physical_damage_+%_vs_ignited_enemies", fire = "fire_damage_+%_vs_bleeding_enemies";
        int Actual(string name) => actual.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        Assert.InRange(Actual(physical), 70, 100); Assert.InRange(Actual(fire), 70, 100);
        var stats = new Dictionary<uint, int>
        {
            [Program.Stats.Key(physical)!.Value] = Actual(physical),
            [Program.Stats.Key(fire)!.Value] = Actual(fire),
            [Program.Stats.Key("damage_+%")!.Value] = 100,
        };
        var raw = DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Fire, new(100, 100));
        foreach (bool attack in new[] { false, true })
        foreach (bool secondary in new[] { false, true })
        foreach (bool convert in new[] { false, true })
        foreach (bool ignited in new[] { false, true })
        foreach (bool bleeding in new[] { false, true })
        {
            var conversion = convert ? new DamageConversion().Add(DamageType.Physical, DamageType.Fire, 50) : new();
            var source = new SpiderDamageSource(raw, conversion, stats, stats, attack, attack, true, false,
                [], new HashSet<int>(), 1, Secondary: secondary, Volatile: false);
            var profile = Game.SpiderDamageProfile(source, 0, false, ignited, bleeding);
            double p = ignited ? Actual(physical) : 0, f = bleeding ? Actual(fire) : 0;
            double expectedPhysical = (convert ? 50 : 100) * (200 + p) / 100;
            double expectedFire = 100 * (200 + f) / 100 + (convert ? 50 * (200 + p + f) / 100 : 0);
            Assert.Equal(expectedPhysical, profile.Hit[DamageType.Physical].Min, 6);
            Assert.Equal(expectedFire, profile.Hit[DamageType.Fire].Min, 6);
            Assert.InRange(profile.Ignite.Min, expectedFire * .9 - 1d / 60, expectedFire * .9 + 1d / 60);
            if (attack && !secondary)
                Assert.InRange(profile.Bleed.Min, expectedPhysical * .7 - 1d / 60, expectedPhysical * .7 + 1d / 60);
            var shot = new ProjectileOffense(DamagePacket.Empty, !attack, 0, 0, 100, "target control")
                { Spider = source, SpiderArea = source };
            Assert.Equal(profile.Hit.MinTotal, shot.Against(false, 0, ignited, bleeding).Hit.MinTotal, 6);
            Assert.Equal(profile.Hit.MinTotal, shot.Against(false, 0, ignited, bleeding).AreaHit!.MinTotal, 6);
            Assert.Equal(100, stats[Program.Stats.Key("damage_+%")!.Value]);
        }
        const uint targetId = 0x504AD;
        var target = new SpawnedMonster("Vulconus target", new(100, 100, 1), 100, 8, 1_000_000)
            { Hostile = true, Life = 1_000_000 };
        conn.Monsters[targetId] = target;
        var weapon = Game.WeaponFor(conn);
        var converted = new DamageConversion().Add(DamageType.Physical, DamageType.Fire, 50);
        foreach (bool ignited in new[] { false, true })
        foreach (bool bleeding in new[] { false, true })
        {
            target.Ignites.Clear(); target.Bleeds.Clear();
            if (ignited) Assert.True(target.Ignites.Add(1, 1, 10));
            if (bleeding) Assert.True(target.Bleeds.Add(1, 1, 10));
            var contact = Assert.IsType<Game.DefaultMeleePrehitProfile>(Game.DefaultMeleePrehitProfileLocked(
                conn, targetId, target, stats, weapon, converted, null, 1));
            var expected = new Dictionary<uint, int>(stats)
            {
                [Program.Stats.Key("physical_damage_+%")!.Value] = ignited ? Actual(physical) : 0,
                [Program.Stats.Key("fire_damage_+%")!.Value] = bleeding ? Actual(fire) : 0,
            };
            var expectedHit = Game.AttackHitConverted(weapon, expected, true, converted);
            Assert.Equal(expectedHit.MinTotal, contact.Hit.MinTotal, 6);
            Assert.Equal(expectedHit.MaxTotal, contact.Hit.MaxTotal, 6);
        }
        conn.Monsters.Remove(targetId);
        Assert.Null(Game.DefaultMeleePrehitProfileLocked(conn, targetId, target, stats, weapon, converted, null, 1));
    }
}
