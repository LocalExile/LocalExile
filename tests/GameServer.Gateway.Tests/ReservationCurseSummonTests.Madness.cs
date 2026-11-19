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
        var conn = new Player(template.Layout, 0xBAD0, 1)
        {
            Character = character, CharacterId = Guid.NewGuid(), Account = "madness-runtime",
            League = "Standard", LeagueForm = "Standard", ClassRow = 1, Instance = new(),
            PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
            Experience = CharacterStats.ExperienceFor(100), LeagueModule = template.LeagueModule,
            Session = new("madness", wire.Stream, wire.Ciphers), Pools = new(100, 1, 1, 0, 0), PlayerLife = 1,
        };
        try
        {
            var inventory = Game.ItemsOf(conn);
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
            conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = conn.Pools.Mana;
            int Stat(uint name) => Game.CharacterPlayerStats(conn).GetValueOrDefault(name);
            async Task<bool> Toggle()
            {
                var skill = Assert.Single(Game.GemSkillEffects(conn), g => Program.Skills.EffectId(g.Effect) == "EmbraceMadness");
                return await Game.ToggleMadness(conn, skill.Id, skill.Effect, () => true);
            }
            void Reconcile() { Game.ReconcileOwnAuras(conn, validateSkills: true); Game.WithOwnAuraChanges(conn, () => []); }
            var boots = inventory.Add(PlayerInventories.Boots, 0, 0,
                uniques.Create("Beacon of Madness (Chaos)", new Random(504))!);
            Reconcile();
            Assert.False(Game.IsInactive(conn, boots.Id));
            Assert.Equal(0, Stat(global::Stat.AllDamageCanPoison));
            Assert.True(await Toggle());
            Assert.True(conn.Madness.Active);
            Assert.NotEqual(0, conn.Madness.PresentedBuff);
            Assert.Empty(conn.Auras);
            Assert.Equal(1, Stat(global::Stat.AllDamageCanPoison));
            Assert.Equal(100, Stat(global::Stat.BaseChanceToPoisonOnHitPct));
            var source = Assert.Single(Game.GemSkillEffects(conn), g => Program.Skills.EffectId(g.Effect) == "EmbraceMadness");
            Assert.Equal(9000, Game.CooldownFor(conn, source.Effect, 1, 0)!.Value.BaseMilliseconds);
            Assert.False(await Toggle());
            conn.Madness.Random = new AspectRoll(0);
            schedule.At = 101.999;
            Reconcile();
            Assert.Equal(0, conn.Madness.Touches.Sum());
            schedule.At = 102;
            Reconcile();
            Assert.Equal(1, conn.Madness.Touches[0]);
            Assert.Equal(-9, Stat(global::Stat.LifeRecoveryRatePlusPct));
            Assert.Equal(-9, Stat(global::Stat.EnergyShieldRecoveryRatePlusPct));
            conn.Madness.Random = new AspectRoll(1);
            schedule.At = 104;
            Reconcile();
            Assert.Equal(6, Stat(global::Stat.ActionSpeedMinusPct));
            conn.Madness.Random = new AspectRoll(2);
            schedule.At = 106;
            Reconcile();
            Assert.Equal(6, Stat(global::Stat.BaseDamageTakenPlusPct));
            conn.Madness.Random = new AspectRoll(3);
            schedule.At = 108;
            Reconcile();
            Assert.Equal(-9, Stat(global::Stat.FlaskEffectPlusPct));
            Assert.Equal(-9, Stat(global::Stat.ChargesGainedPlusPct));
            schedule.At = 122;
            Reconcile();
            Assert.Equal(0, conn.Madness.Touches[0]);
            Assert.Equal(0, Stat(global::Stat.LifeRecoveryRatePlusPct));
            schedule.At = 144;
            Reconcile();
            Assert.Equal(10, conn.Madness.Touches[3]);
            Assert.Equal(-90, Stat(global::Stat.FlaskEffectPlusPct));
            schedule.At += 1000000;
            Reconcile();
            Assert.Equal(10, conn.Madness.Touches[3]);
            Assert.Equal(schedule.At + 20, conn.Madness.Until[3], 6);

            var player = Game.CharacterPlayerStats(conn);
            int Get(uint name) => player.GetValueOrDefault(name);
            var raw = DamagePacket.Of(DamageType.Lightning, 100, 100);
            var conversion = DamageStats.Conversion(player, Program.Stats, attack: null);
            var poison = MonsterPoisons.Settings.From(AilmentPanelStats.Compute(new("secondary", false), raw,
                conversion, Get), "secondary", Get);
            Assert.Equal(100, poison.Chance);
            Assert.True(poison.Min >= 30);
            var target = new SpawnedMonster("Madness poison", new(100, 100, 1), 100, 8, 100000)
                { Hostile = true };
            Assert.True(Game.TryPoisonOnHitLocked(conn, target, raw, false, false, player, null, poison,
                schedule.At, new AspectRoll(0), out double dps, out double seconds));
            // The lethal mutation earns a source snapshot. Publication may follow a toggle or item removal.
            var corpse = new SpawnedMonster("Madness corpse", new(100, 100, 1), 100, 8, 1000)
                { Variety = new MonsterCatalog().Find("Metadata/Monsters/Zombies/Zombie") };
            var nearby = new SpawnedMonster("Madness nearby", new(101, 100, 1), 100, 8, 10000);
            var outside = new SpawnedMonster("Madness outside", new(200, 100, 1), 100, 8, 10000);
            conn.Monsters[0xBAD1] = corpse;
            conn.Monsters[0xBAD2] = nearby;
            conn.Monsters[0xBAD3] = outside;
            Assert.Equal(0u, Game.ApplyMonsterHitLocked(conn, corpse, 1000)!.Value.Life);
            Assert.NotNull(corpse.MadnessExplosion);
            var earned = corpse.MadnessExplosion!;
            Game.QueueMadnessExplosionLocked(conn, 0xBAD1, corpse);
            Game.QueueMadnessExplosionLocked(conn, 0xBAD1, corpse);
            Assert.Single(conn.Madness.Explosions);
            Assert.True(await Toggle());
            Assert.False(conn.Madness.Active);
            Assert.Equal(0, conn.Madness.PresentedBuff);
            Assert.All(conn.Madness.PresentedTouches, buff => Assert.Equal(0, buff));
            Assert.Equal(0, Stat(global::Stat.AllDamageCanPoison));
            Assert.Equal(0, conn.Madness.Touches.Sum());
            Assert.Equal(schedule.At + 9, conn.Madness.ReadyAt, 6);
            Assert.Equal(1000, Game.CooldownFor(conn, source.Effect, 1, 0)!.Value.BaseMilliseconds);
            Assert.Equal(dps, target.Poisons.TotalDps(schedule.At + seconds / 2), 6);
            Game.MadnessExplosionsLocked(conn, schedule.At);
            Assert.Empty(conn.Madness.Explosions);
            Assert.True(corpse.CorpseConsumed);
            Assert.False(conn.Monsters.ContainsKey(0xBAD1));
            Assert.InRange(nearby.Life, 1u, 9999u);
            Assert.True(nearby.Poisons.TotalDps(schedule.At) > 0);
            Assert.Equal(10000u, outside.Life);
            uint afterExplosion = nearby.Life;
            Game.MadnessExplosionsLocked(conn, schedule.At);
            Assert.Equal(afterExplosion, nearby.Life);
            schedule.At += 8.999;
            Assert.False(await Toggle());
            schedule.At += .001;
            Assert.True(await Toggle());
            inventory.Remove(PlayerInventories.Boots, boots.Id);
            Reconcile();
            Assert.False(conn.Madness.Active);
            Assert.Null(conn.Madness.Source);
            Assert.Equal(0, conn.Madness.PresentedBuff);

            foreach (string variant in new[] { "Physical", "Elemental" })
            {
                boots = inventory.Add(PlayerInventories.Boots, 0, 0,
                    uniques.Create($"Beacon of Madness ({variant})", new Random(504))!);
                Reconcile();
                Assert.False(conn.Madness.Active);
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
                    player = Game.CharacterPlayerStats(conn);
                    var ignite = MonsterIgnites.Settings.From(AilmentPanelStats.Compute(new("secondary", false),
                        raw, DamageStats.Conversion(player, Program.Stats, attack: null), Get), "secondary", Get);
                    Assert.True(ignite.Min >= 90);
                }
                conn.PlayerLife = 0;
                Reconcile();
                Assert.False(conn.Madness.Active);
                Assert.Equal(0, conn.Madness.PresentedBuff);
                conn.PlayerLife = conn.Pools.Life;
                Reconcile();
                Assert.False(conn.Madness.Active);
                inventory.Remove(PlayerInventories.Boots, boots.Id);
                Reconcile();
            }
        }
        finally
        {
            conn.Closed = true;
            Game.AreaUpdateContext.Value = previousContext;
            PlayerInventories.Forget(character);
        }
    }
}
