using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    static async Task MadnessFixture(Player template, ModuleLifetime lifetime)
    {
        string character = "MadnessRuntime" + Guid.NewGuid().ToString("N");
        PlayerInventories.SetClass(character, 1);
        CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
            Experience: CharacterStats.ExperienceFor(100)));
        var schedule = new CatClockSchedule { At = 100 };
        var updates = new PlayerUpdatesState(lifetime, schedule);
        var previousContext = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = updates;
        using var wire = new Wire();
        var player = new Player(template.Layout, 0xBAD0, 1)
        {
            Character = { Name = character, Id = Guid.NewGuid(), Account = "madness-runtime", League = "Standard", LeagueForm = "Standard", ClassRow = 1, Experience = CharacterStats.ExperienceFor(100) },
            Instance = new(),
            Connection = { PlayerSent = true, PlayerObjectCreated = true, Session = new("madness", wire.Stream, wire.Ciphers) },
            PlayerUpdatesState = updates,
            LeagueModule = template.LeagueModule,
            Pools = new(100, 1, 1, 0, 0),
            PlayerLife = 1
        };
        try
        {
            var inventory = Game.ItemsOf(player);
            foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1,
                         PlayerInventories.Helmet, PlayerInventories.BodyArmour, PlayerInventories.Boots,
                         PlayerInventories.Gloves, 5, 6, 7, 10 })
                foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);
            var uniques = new UniqueCatalog(Program.Items);
            foreach (var (variant, baseId, resistance) in new[]
            {
                ("Chaos", "BootsAtlas1", "ColdAndLightningResistImplicitBoots1"),
                ("Physical", "BootsAtlas2", "FireAndColdResistImplicitBoots1_"),
                ("Elemental", "BootsAtlas3", "FireAndLightningResistImplicitBoots1"),
            })
            {
                var created = Assert.IsType<Item>(uniques.Create($"Beacon of Madness ({variant})", new Random(504)));
                Assert.Equal("Metadata/Items/Armours/Boots/" + baseId, Program.Items.ByHash(created.TypeHash)!.Id);
                Assert.Contains(created.Mods, m => m.Section == ModSection.Implicit && Program.Items.Mods.ByHash(m.Hash)?.Id == resistance);
            }
            foreach (var definition in uniques.All.Where(d => d.Name == "Beacon of Madness"))
            {
                var created = Assert.IsType<Item>(uniques.Create(definition, new Random(504)));
                Assert.Equal(definition.BaseTypeId, Program.Items.ByHash(created.TypeHash)!.Id);
                Assert.Contains(created.Mods, m => Program.Items.Mods.ByHash(m.Hash)?.Id == "AllDamageCanPoisonGloriousMadnessUnique___1");
            }
            inventory.Add(5, 0, 0, uniques.Create("Astramentis", new Random(504))!);
            inventory.Add(6, 0, 0, uniques.Create("Le Heup of All", new Random(504))!);
            inventory.Add(7, 0, 0, uniques.Create("Le Heup of All", new Random(505))!);
            player.Pools = Game.PoolsFor(player, 100);
            player.PlayerLife = player.Pools.Life;
            player.PlayerMana = player.Pools.Mana;
            int Stat(uint name) => Game.CharacterPlayerStats(player).GetValueOrDefault(name);
            async Task<bool> Toggle()
            {
                var skill = Assert.Single(Game.GemSkillEffects(player), g => Program.Skills.EffectId(g.Effect) == "EmbraceMadness");
                return await Game.ToggleMadness(player, skill.Id, skill.Effect, () => true);
            }
            void Reconcile() { Game.ReconcileOwnAuras(player, validateSkills: true); Game.WithOwnAuraChanges(player, () => []); }
            var boots = inventory.Add(PlayerInventories.Boots, 0, 0,
                uniques.Create("Beacon of Madness (Chaos)", new Random(504))!);
            Reconcile();
            Assert.False(Game.IsInactive(player, boots.Id));
            Assert.Equal(0, Stat(global::Stat.AllDamageCanPoison));
            Assert.True(await Toggle());
            Assert.True(player.Madness.Active);
            Assert.True(player.Area.Buffs.HasRow(player.Id, 1535));
            Assert.Empty(player.Auras);
            Assert.Equal(1, Stat(global::Stat.AllDamageCanPoison));
            Assert.Equal(100, Stat(global::Stat.BaseChanceToPoisonOnHitPct));
            var source = Assert.Single(Game.GemSkillEffects(player), g => Program.Skills.EffectId(g.Effect) == "EmbraceMadness");
            Assert.Equal(9000, Game.CooldownFor(player, source.Effect, 1, 0)!.Value.BaseMilliseconds);
            Assert.False(await Toggle());
            player.Madness.Random = new AspectRoll(0);
            schedule.At = 101.999;
            Reconcile();
            Assert.Equal(0, player.Madness.Touches.Sum());
            schedule.At = 102;
            Reconcile();
            Assert.Equal(1, player.Madness.Touches[0]);
            Assert.Equal(-9, Stat(global::Stat.LifeRecoveryRatePlusPct));
            Assert.Equal(-9, Stat(global::Stat.EnergyShieldRecoveryRatePlusPct));
            player.Madness.Random = new AspectRoll(1);
            schedule.At = 104;
            Reconcile();
            Assert.Equal(6, Stat(global::Stat.ActionSpeedMinusPct));
            player.Madness.Random = new AspectRoll(2);
            schedule.At = 106;
            Reconcile();
            Assert.Equal(6, Stat(global::Stat.BaseDamageTakenPlusPct));
            player.Madness.Random = new AspectRoll(3);
            schedule.At = 108;
            Reconcile();
            Assert.Equal(-9, Stat(global::Stat.FlaskEffectPlusPct));
            Assert.Equal(-9, Stat(global::Stat.ChargesGainedPlusPct));
            schedule.At = 122;
            Reconcile();
            Assert.Equal(0, player.Madness.Touches[0]);
            Assert.Equal(0, Stat(global::Stat.LifeRecoveryRatePlusPct));
            schedule.At = 144;
            Reconcile();
            Assert.Equal(10, player.Madness.Touches[3]);
            Assert.Equal(-90, Stat(global::Stat.FlaskEffectPlusPct));
            schedule.At += 1000000;
            Reconcile();
            Assert.Equal(10, player.Madness.Touches[3]);
            Assert.Equal(schedule.At + 20, player.Madness.Until[3], 6);

            var playerStats = Game.CharacterPlayerStats(player);
            int Get(uint name) => playerStats.GetValueOrDefault(name);
            var raw = DamagePacket.Of(DamageType.Lightning, 100, 100);
            var conversion = DamageStats.Conversion(playerStats, Program.Stats, attack: null);
            var poison = MonsterPoisons.Settings.From(AilmentPanelStats.Compute(new("secondary", false), raw,
                conversion, Get), "secondary", Get);
            Assert.Equal(100, poison.Chance);
            Assert.True(poison.Min >= 30);
            var target = new SpawnedMonster("Madness poison", new(100, 100, 1), 100, 8, 100000)
                { Hostile = true };
            Assert.True(Game.TryPoisonOnHitLocked(player, target, raw, false, false, playerStats, null, poison,
                schedule.At, new AspectRoll(0), default(SkillPoisonStats), out double dps, out double seconds));
            // The lethal mutation earns a source snapshot. Publication may follow a toggle or item removal.
            var corpse = new SpawnedMonster("Madness corpse", new(100, 100, 1), 100, 8, 1000)
                { Variety = new MonsterCatalog().Find("Metadata/Monsters/Zombies/Zombie") };
            var nearby = new SpawnedMonster("Madness nearby", new(101, 100, 1), 100, 8, 10000);
            var outside = new SpawnedMonster("Madness outside", new(200, 100, 1), 100, 8, 10000);
            player.Area.Monsters[0xBAD1] = corpse;
            player.Area.Monsters[0xBAD2] = nearby;
            player.Area.Monsters[0xBAD3] = outside;
            Assert.Equal(0u, Game.ApplyMonsterHitLocked(player, corpse, 1000)!.Value.Life);
            Assert.NotNull(corpse.MadnessExplosion);
            var earned = corpse.MadnessExplosion!;
            Game.QueueMadnessExplosionLocked(player, 0xBAD1, corpse);
            Game.QueueMadnessExplosionLocked(player, 0xBAD1, corpse);
            Assert.Single(player.Madness.Explosions);
            Assert.True(await Toggle());
            Assert.False(player.Madness.Active);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 1535));
            Assert.All(Enumerable.Range(1496, 4), row => Assert.False(player.Area.Buffs.HasRow(player.Id, (ushort)row)));
            Assert.Equal(0, Stat(global::Stat.AllDamageCanPoison));
            Assert.Equal(0, player.Madness.Touches.Sum());
            Assert.Equal(schedule.At + 9, player.Madness.ReadyAt, 6);
            Assert.Equal(1000, Game.CooldownFor(player, source.Effect, 1, 0)!.Value.BaseMilliseconds);
            Assert.Equal(dps, target.Poisons.TotalDps(schedule.At + seconds / 2), 6);
            Game.MadnessExplosionsLocked(player, schedule.At);
            Assert.Empty(player.Madness.Explosions);
            Assert.True(corpse.CorpseConsumed);
            Assert.False(player.Area.Monsters.ContainsKey(0xBAD1));
            Assert.InRange(nearby.Life, 1u, 9999u);
            Assert.True(nearby.Poisons.TotalDps(schedule.At) > 0);
            Assert.Equal(10000u, outside.Life);
            uint afterExplosion = nearby.Life;
            Game.MadnessExplosionsLocked(player, schedule.At);
            Assert.Equal(afterExplosion, nearby.Life);
            schedule.At += 8.999;
            Assert.False(await Toggle());
            schedule.At += .001;
            Assert.True(await Toggle());
            inventory.Remove(PlayerInventories.Boots, boots.Id);
            Reconcile();
            Assert.False(player.Madness.Active);
            Assert.Null(player.Madness.Source);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 1535));

            foreach (string variant in new[] { "Physical", "Elemental" })
            {
                boots = inventory.Add(PlayerInventories.Boots, 0, 0,
                    uniques.Create($"Beacon of Madness ({variant})", new Random(504))!);
                Reconcile();
                Assert.False(player.Madness.Active);
                schedule.At += 10;
                Assert.True(await Toggle());
                Assert.Equal(0, Stat(global::Stat.AllDamageCanPoison));
                if (variant == "Physical")
                {
                    Assert.Equal(20, Stat(global::Stat.ChanceToDealDoubleDamagePct));
                    Assert.Equal(15, Stat(global::Stat.BaseMaxFortification));
                    Assert.Equal(0, Stat(global::Stat.AllDamageCanIgnite));
                }
                else
                {
                    Assert.Equal(0, Stat(global::Stat.ChanceToDealDoubleDamagePct));
                    Assert.Equal(1, Stat(global::Stat.AllDamageCanIgnite));
                    Assert.Equal(1, Stat(global::Stat.AllDamageCanChill));
                    Assert.Equal(1, Stat(global::Stat.AllDamageCanShock));
                    Assert.Equal(100, Stat(global::Stat.BaseChanceToIgnitePct));
                    foreach (string ailment in new[] { "ignite", "freeze", "chill", "shock" })
                        Assert.Equal(1, Stat(global::Stat.Of($"base_immune_to_{ailment}")));
                    Assert.Equal(1, Stat(global::Stat.ImmuneToScorch));
                    Assert.Equal(1, Stat(global::Stat.ImmuneToSap));
                    Assert.Equal(1, Stat(global::Stat.CannotGainBrittle));
                    playerStats = Game.CharacterPlayerStats(player);
                    var ignite = MonsterIgnites.Settings.From(AilmentPanelStats.Compute(new("secondary", false),
                        raw, DamageStats.Conversion(playerStats, Program.Stats, attack: null), Get), "secondary", Get);
                    Assert.True(ignite.Min >= 90);
                }
                player.PlayerLife = 0;
                Reconcile();
                Assert.False(player.Madness.Active);
                Assert.False(player.Area.Buffs.HasRow(player.Id, 1535));
                player.PlayerLife = player.Pools.Life;
                Reconcile();
                Assert.False(player.Madness.Active);
                inventory.Remove(PlayerInventories.Boots, boots.Id);
                Reconcile();
            }
        }
        finally
        {
            player.Closed = true;
            Game.AreaUpdateContext.Value = previousContext;
            PlayerInventories.Forget(character);
        }
    }
}
