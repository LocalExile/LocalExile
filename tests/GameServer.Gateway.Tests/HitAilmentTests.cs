using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// A melee or area skill's hit ignites from the skill's ignite rows, as a projectile's does (MonsterIgnites), and an
// attack bleeds from its bleeding rows (MonsterBleeds) and poisons with its weapon's own chance (PoisonOnHitLocked).
public sealed class HitAilmentTests
{
    [Fact]
    public void A_melee_hit_ignites_from_its_skills_ignite_rows() => WithClubCharacter(conn =>
    {
        ushort skill = Game.GemSkillEffects(conn).Single(g => g.Name == "Infernal Blow").Id;
        MonsterIgnites.Settings Ignite() => Game.SupportedIgnite(conn, skill, spell: false, Game.CharacterPlayerStats(conn));
        var settings = Ignite();
        // Infernal Blow's fire: the main hand's ignite rows, 4 s, and no chance without a source.
        Assert.Equal(0, settings.Chance);
        Assert.True(settings.Max > 0 && settings.CriticalMax >= settings.Max);
        Assert.Equal(4, settings.Seconds, 6);

        var target = Monster();
        conn.Monsters[1] = target;
        lock (conn.Monsters)
        {
            Assert.Null(Game.IgniteOnHitLocked(conn, 1, target, settings, critical: false, now: 0));
            // A critical strike always ignites: one buff, its damage in the critical range.
            var packets = Game.IgniteOnHitLocked(conn, 1, target, settings, critical: true, now: 0);
            Assert.Single(packets!);
            Assert.InRange(target.Ignites.PerSecond(0), settings.CriticalMin, settings.CriticalMax);
            Assert.Equal(4, target.Ignites.Until, 6);
            // The next ignite sends the buff again, the old instance off first.
            Assert.Equal(2, Game.IgniteOnHitLocked(conn, 1, target, settings, critical: true, now: 1)!.Count);
            // A monster that cannot be ignited is not.
            var immune = Monster(("cannot_be_ignited", 1));
            conn.Monsters[2] = immune;
            Assert.Null(Game.IgniteOnHitLocked(conn, 2, immune, settings, critical: true, now: 0));
        }

        // 100% chance: a non-critical hit ignites too, in the non-critical range.
        conn.PlayerStats[Program.Stats.Key("base_chance_to_ignite_%")!.Value] = 100;
        settings = Ignite();
        Assert.Equal(100, settings.Chance);
        var other = Monster();
        conn.Monsters[3] = other;
        lock (conn.Monsters) Assert.NotNull(Game.IgniteOnHitLocked(conn, 3, other, settings, critical: false, now: 0));
        Assert.InRange(other.Ignites.PerSecond(0), settings.Min, settings.Max);

        // The default attack reads the weapon's rows: the club has no fire until the attack gets added fire.
        Assert.Equal(0, Game.DefaultAttackIgnite(conn, Game.CharacterPlayerStats(conn)).Max);
        conn.PlayerStats[Program.Stats.Key("attack_minimum_added_fire_damage")!.Value] = 10;
        conn.PlayerStats[Program.Stats.Key("attack_maximum_added_fire_damage")!.Value] = 20;
        var attack = Game.DefaultAttackIgnite(conn, Game.CharacterPlayerStats(conn));
        Assert.True(attack.Max > 0 && attack.Chance == 100);
    });

    [Fact]
    public void An_attack_bleeds_from_its_bleeding_rows_and_a_moving_monster_takes_triple() => WithClubCharacter(conn =>
    {
        MonsterIgnites.Settings Bleed() => Game.DefaultAttackBleed(conn, Game.CharacterPlayerStats(conn));
        var settings = Bleed();
        // The club's physical damage: the main hand's bleeding rows for 5 s, and no chance without a source.
        Assert.Equal(0, settings.Chance);
        Assert.True(settings.Max > 0);
        // Character.ot's +50% damage over time multiplier for ailments from critical strikes.
        Assert.Equal(1.5, settings.CriticalMax / settings.Max, 2);
        Assert.Equal(5, settings.Seconds, 6);
        // Its poison reads the same rows: 30% of the physical damage a second, against the bleed's 70%.
        Assert.Equal(3.0 / 7, Game.DefaultAttackPoison(conn, Game.CharacterPlayerStats(conn)).Max / settings.Max, 2);
        ushort skill = Game.GemSkillEffects(conn).Single(g => g.Name == "Infernal Blow").Id;
        Assert.Equal(MonsterIgnites.Settings.None, Game.SupportedBleed(conn, skill, spell: true, Game.CharacterPlayerStats(conn)));

        var target = Monster();
        conn.Monsters[1] = target;
        lock (conn.Monsters)
        {
            // Unlike ignite, a critical strike rolls the chance too.
            Assert.Null(Game.BleedOnHitLocked(conn, 1, target, settings, critical: true, now: 0));
            // The "on Critical Strike" chances count for critical strikes only.
            conn.PlayerStats[Program.Stats.Key("bleed_on_crit_%_with_attacks")!.Value] = 100;
            Assert.Equal((0, 100), (Bleed().Chance, Bleed().CriticalChance));
            Assert.Null(Game.BleedOnHitLocked(conn, 1, target, Bleed(), critical: false, now: 0));
            // A weapon's fixed chances: "25% chance to cause Bleeding on Hit", "50% ... on Critical Strike".
            var fixedChances = Game.BleedSettings([], "main_hand", _ => 0, new Dictionary<string, int>
                { ["local_chance_to_bleed_on_hit_25%"] = 1, ["local_chance_to_bleed_on_crit_50%"] = 1 }, melee: false, bow: false);
            Assert.Equal((25, 75), (fixedChances.Chance, fixedChances.CriticalChance));
            conn.PlayerStats[Program.Stats.Key("bleed_on_hit_with_attacks_%")!.Value] = 100;
            settings = Bleed();
            Assert.Equal(100, settings.Chance);
            Assert.Single(Game.BleedOnHitLocked(conn, 1, target, settings, critical: false, now: 0)!);
            Assert.InRange(target.Bleeds.PerSecond(0), settings.Min, settings.Max);
            Assert.Equal(5, target.Bleeds.Until, 6);
            Assert.False(target.Ignites.Burning);
            // A monster immune to bleeding is not.
            var immune = Monster(("immune_to_bleeding", 1));
            conn.Monsters[2] = immune;
            Assert.Null(Game.BleedOnHitLocked(conn, 2, immune, settings, critical: false, now: 0));
        }
        // Standing still it takes the bleed through its physical damage taken; moving, three times that.
        double still = Game.BleedTaken(target, 0);
        Assert.True(still > 0);
        target.Walk = new(105, 100, 30);
        target.Walk.Order(155, 100, 0);
        Assert.True(target.Walk.Moving(0.1));
        Assert.Equal(3 * still, Game.BleedTaken(target, 0.1), 6);
        // Modifiers to hits never reach it (wiki Bleeding); a bleed support's more multiplier does, and not the poison.
        double poison = Game.DefaultAttackPoison(conn, Game.CharacterPlayerStats(conn)).Max;
        conn.PlayerStats[Program.Stats.Key("melee_damage_+%")!.Value] = 100;
        conn.PlayerStats[Program.Stats.Key("attack_damage_+%")!.Value] = 100;
        Assert.Equal(settings.Max, Bleed().Max, 6);
        conn.PlayerStats[Program.Stats.Key("support_chance_to_bleed_bleeding_damage_+%_final")!.Value] = 50;
        Assert.Equal(1.5, Bleed().Max / settings.Max, 2);
        Assert.Equal(poison, Game.DefaultAttackPoison(conn, Game.CharacterPlayerStats(conn)).Max, 6);
        double supported = Bleed().Max;
        conn.PlayerStats[Program.Stats.Key("bleeding_dot_multiplier_+")!.Value] = 50;
        Assert.Equal(1.5, Bleed().Max / supported, 2);

        // Aggravated (wiki Bleeding): on a standing monster the strongest aggravated bleed adds the moving bonus to the
        // strongest bleed's damage; a moving one already takes it.
        Assert.Equal((2.0, 0.0), (Game.AggravatedExtra(Monster(), 0), Game.AggravatedExtra(target, 0.1)));
        var cuts = new MonsterIgnites();
        cuts.Add(0, 4, 5);
        cuts.Aggravate();
        cuts.Add(0, 10, 5);
        Assert.Equal(10 + 2 * 4, cuts.Advance(1, 1, aggravatedExtra: 2));
        conn.PlayerStats[Program.Stats.Key("base_aggravate_bleeding_on_attack_hit_chance_%")!.Value] = 10;
        conn.PlayerStats[Program.Stats.Key("aggravate_inflicted_bleeding")!.Value] = 1;
        Assert.Equal((10, 10, true), (Bleed().Aggravate, Bleed().CriticalAggravate, Bleed().Aggravated));
        // Crimson Dance: the 8 strongest deal damage at 50% less, never aggravated nor more while it moves.
        double single = Bleed().Max;
        conn.PlayerStats[Program.Stats.Key("keystone_crimson_dance")!.Value] = 1;
        var dance = Bleed();
        Assert.Equal((8, 0, false), (dance.Stacks, dance.Aggravate, dance.Aggravated));
        Assert.Equal(.5, dance.Max / single, 6);
        cuts.Stacks = 8;
        Assert.Equal(14, cuts.Advance(2, 1));
        target.Bleeds.Stacks = 8;
        Assert.Equal((still, 0.0), (Game.BleedTaken(target, 0.1), Game.AggravatedExtra(target, 0)));
        conn.PlayerStats[Program.Stats.Key("cannot_cause_bleeding")!.Value] = 1;
        Assert.Equal(MonsterIgnites.Settings.None, Bleed());
        VerifyRuptureClocks(conn);

        // The club's own "chance to Poison on Hit", which the panel counts, poisons an attack's hit and not a spell's.
        var player = Game.CharacterPlayerStats(conn);
        var hit = DamagePacket.Of(DamageType.Physical, 100, 100);
        var poisoned = Monster();
        lock (conn.Monsters)
        {
            Game.PoisonOnHitLocked(conn, poisoned, hit, spell: true, critical: false, player, null,
                Game.DefaultAttackPoison(conn, player), now: 0, new Random(1));
            Assert.False(poisoned.Poisons.Poisoned);
            Game.PoisonOnHitLocked(conn, poisoned, hit, spell: false, critical: false, player, null,
                Game.DefaultAttackPoison(conn, player), now: 0, new Random(1));
            Assert.True(poisoned.Poisons.Poisoned);
        }
    });

    static void VerifyRuptureClocks(ClientState conn)
    {
        var bleed = new MonsterIgnites.Settings(100, 100, 100, 100, 100, 10, CriticalChance: 100, RupturePercent: 25);
        var target = Monster();
        target.Bleeds.Add(0, 200, 10); // A stronger bleed applied before the first Rupture.
        var first = Game.BleedOnHitLocked(conn, 10, target, bleed, critical: true, now: 1)!;
        var visible = Assert.Single(first, IsRupture);
        Assert.Equal(31, visible.Length); // IsRecovery=false: no inline values, extra word or stack field.
        Assert.Equal(2, visible[22]);
        Assert.Equal(3f, BitConverter.UInt32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(visible.AsSpan(23))));
        Assert.Empty(Game.PresentRupturesLocked(conn, 10, target, 2)); // Ticking must not restart the duration.
        var observer = new ClientState(conn.Layout, 2, 1) { Instance = conn.Instance };
        var observed = Assert.Single(Game.PresentRupturesLocked(observer, 10, target, 2), IsRupture);
        Assert.Equal(2f, BitConverter.UInt32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(observed.AsSpan(23))));
        Assert.NotEqual(visible[12..14], observed[12..14]);
        Assert.Equal(9, target.Bleeds.Until, 8); // The new ten-second bleed immediately runs at 1.25x.
        Assert.Equal(250, target.Bleeds.Advance(2, Game.BleedTaken(target, 2)) - 200); // One unruptured second retained.
        Game.SettleBleeding(target, 5); // Crosses the stack's t=4 expiry with no intermediate tick.
        Assert.Equal(700, target.Bleeds.Advance(5, 1)); // 2 seconds at 250, then one at 200.
        Assert.Equal(10.25, target.Bleeds.Until, 8); // New bleed: 10 - 3*1.25 base seconds remain at t=4.
        Assert.Equal(1, Game.BleedingExpiryRate(target, 5));
        var ended = Game.PresentRupturesLocked(conn, 10, target, 5);
        Assert.Single(ended, p => p[0] == 1 && p[1] == 0xc2);
        Assert.Contains(ended, p => p.SequenceEqual(InstanceEntities.StatsUpdate(10,
            [(Program.Stats.Key("rupture_debuff_bleeding_time_passed_+%_final")!.Value, 0)])));

        var staggered = Monster();
        var shown = new HashSet<ushort>();
        for (int i = 0; i < 5; i++)
        {
            var hit = Game.BleedOnHitLocked(conn, 11, staggered, bleed with { Seconds = 20 }, critical: true, now: i * .5)!;
            var add = Assert.Single(hit, IsRupture);
            ushort instance = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(add.AsSpan(12));
            Assert.DoesNotContain(instance, shown);
            int removed = hit.Count(p => p[0] == 1 && p[1] == 0xc2
                && shown.Remove(System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(12))));
            Assert.Equal(i == 4 ? 1 : 0, removed);
            shown.Add(instance);
            Assert.Equal(Math.Min(i + 1, 4), shown.Count);
        }
        Assert.Equal(4, staggered.Ruptures.Stacks(2));
        Game.SettleBleeding(staggered, 6); // Replacement and every distinct expiry, in one advance.
        Assert.Equal(0, staggered.Ruptures.Stacks(6));
        Assert.Equal(1, Game.BleedingExpiryRate(staggered, 6));
        Assert.True(staggered.Bleeds.Advance(6, 1) > 600);
        Assert.Equal(4, Game.PresentRupturesLocked(conn, 11, staggered, 6).Count(p => p[0] == 1 && p[1] == 0xc2));

        var slowed = Monster(("buff_time_passed_+%_other_than_temporal_chains", -50));
        slowed.RecipientDebuffRate = .5;
        slowed.Bleeds.SetExpiryRate(0, .5);
        var slowHit = Game.BleedOnHitLocked(conn, 12, slowed, bleed, critical: true, now: 0)!;
        uint clockKey = Program.Stats.Key("buff_time_passed_+%_other_than_temporal_chains")!.Value;
        Assert.Contains(slowHit, p => p.SequenceEqual(InstanceEntities.StatsUpdate(12, [(clockKey, -50)])));
        var slowView = Game.PresentRupturesLocked(observer, 12, slowed, 1);
        Assert.Contains(slowView, p => p.SequenceEqual(InstanceEntities.StatsUpdate(12, [(clockKey, -50)])));
        Assert.Equal(2.5f, BitConverter.UInt32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
            Assert.Single(slowView, IsRupture).AsSpan(23)))); // Remaining base time, with the observer's native clock slowed.
        Assert.Empty(Game.PresentRupturesLocked(observer, 12, slowed, 2));
        Assert.Equal(.625, Game.BleedingExpiryRate(slowed, 0));
        Assert.Equal(16, slowed.Bleeds.Until, 8);
        Assert.Equal(6, slowed.Ruptures.NextExpiry); // Rupture is itself a debuff; Temporal Chains slows it too.
        Game.SettleBleeding(slowed, 4);
        Assert.Equal(16, slowed.Bleeds.Until, 8);
        Assert.Equal(500, slowed.Bleeds.Advance(4, 1));
        Assert.Equal(100, slowed.Bleeds.PerSecond(4)); // Expiry never scales the base DPS.
        Game.SettleBleeding(slowed, 7);
        Assert.Equal(18.5, slowed.Bleeds.Until, 8);
        Assert.Equal(350, slowed.Bleeds.Advance(7, 1));
        slowed.Mods = Monster().Mods;
        var restoredView = Game.PresentRupturesLocked(observer, 12, slowed, 7);
        Assert.Contains(restoredView, p => p.SequenceEqual(InstanceEntities.StatsUpdate(12, [(clockKey, 0)])));

        var changed = Monster();
        Game.BleedOnHitLocked(conn, 14, changed, bleed, critical: true, now: 0);
        Game.SettleBleeding(changed, 1);
        changed.Mods = Monster(("buff_time_passed_+%_other_than_temporal_chains", -50)).Mods;
        changed.RecipientDebuffRate = .5;
        changed.Ruptures.SetExpiryRate(1, .5);
        changed.Bleeds.SetExpiryRate(1, Game.BleedingExpiryRate(changed, 1));
        Assert.Equal(InstanceEntities.StatsUpdate(14, [(clockKey, -50)]),
            Assert.Single(Game.PresentRupturesLocked(conn, 14, changed, 1))); // Retimes the live native instance, no new buff.
        Assert.Equal(5, changed.Ruptures.NextExpiry);
        Game.SettleBleeding(changed, 6);
        Assert.Equal(17.5, changed.Bleeds.Until, 8);
        Assert.Equal(725, changed.Bleeds.Advance(6, 1));
        // Speeding a live timer back up preserves elapsed progress, and never revives an expired stack.
        var restored = new MonsterRuptures(); restored.Add(0, 25);
        restored.SetExpiryRate(1, .5); restored.SetExpiryRate(2, 1);
        Assert.Equal(3.5, restored.NextExpiry);
        restored.SetExpiryRate(4, .5);
        Assert.Equal(0, restored.Stacks(4));

        var shortBleed = Monster();
        Game.BleedOnHitLocked(conn, 13, shortBleed, bleed with { Seconds = 1 }, critical: true, now: 0);
        Game.SettleBleeding(shortBleed, 4);
        Assert.False(shortBleed.Bleeds.Burning);
        Assert.Equal(100, shortBleed.Bleeds.Advance(4, 1)); // 100 DPS * 1.25 taken * 0.8 seconds.
        Assert.Equal(0, shortBleed.Bleeds.Advance(5, 1));

        var dying = Monster();
        Game.BleedOnHitLocked(conn, 15, dying, bleed, critical: true, now: 0);
        dying.Dead = true;
        Assert.Single(Game.PresentRupturesLocked(conn, 15, dying, .5), p => p[0] == 1 && p[1] == 0xc2);
        Assert.Empty(dying.Ruptures.Active);
        Assert.Empty(Game.PresentRupturesLocked(conn, 15, dying, 1));

        static bool IsRupture(byte[] p) => p.Length >= 16 && p[0] == 1 && p[1] == 0xc1
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(14)) == Game.RuptureBuffRow;
    }

    static SpawnedMonster Monster(params (string, int)[] stats) => new("target", new(105, 100, 0), 0, 0, 100)
        { Mods = new([], [], new(stats.ToDictionary(s => s.Item1, s => s.Item2))) };

    // A level 100 character with a Driftwood Club socketed with Infernal Blow, its ChanceToPoison1 rolled 100% here.
    static void WithClubCharacter(Action<ClientState> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-hit-ailment-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "hit-ailment", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "HitAilmentChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var club = ItemCatalog.Create(Program.Items.Find("Driftwood Club")!);
            var blow = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemInfernalBlow")!);
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, club with
            {
                Sockets = [new ItemSocket(SocketColour.White, blow)], Links = [1],
                Mods = [new ItemMod(ModSection.Explicit, Program.Items.Mods.ById("ChanceToPoison1")!.Hash, [100])],
            });
            test(conn);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
    }
}
