using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    static async Task OwnedCountItemFixture(ClientState template, ModuleLifetime lifetime)
    {
        string character = "OwnedCounts" + Guid.NewGuid().ToString("N");
        PlayerInventories.SetClass(character, 1);
        CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
            Experience: CharacterStats.ExperienceFor(100)));
        using var wire = new Wire();
        var schedule = new CatClockSchedule { At = 100 };
        var updates = new PlayerUpdatesState(lifetime, schedule);
        var prior = Game.AreaUpdateContext.Value; Game.AreaUpdateContext.Value = updates;
        var conn = new ClientState(template.Layout, 1, 1)
        {
            Character = character, CharacterId = Guid.NewGuid(), Account = "owned-counts", ClassRow = 1,
            League = "Standard", LeagueForm = "Standard", Instance = new(), PlayerLife = 1,
            PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
            Experience = CharacterStats.ExperienceFor(100), LeagueModule = template.LeagueModule,
            Wire = ("owned-counts", wire.Stream, wire.Ciphers), Pools = new(100, 1, 1, 0, 0),
        };
        conn.Summons.Restored = conn.Golems.Restored = true;
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        var inventory = Game.ItemsOf(conn); var uniques = new UniqueCatalog(Program.Items);
        InventoryItem Wear(string name, byte slot) => inventory.Add(slot, 0, 0, uniques.Create(name, new Random(504))!);
        Item Gem(string name) => ItemCatalog.Create(Program.Items.FindGem(name)!.Value.Type);
        Dictionary<uint, int> Sources() => Game.CharacterStatSources(conn).Stats;
        int Stat(string name) => Sources().GetValueOrDefault(Key(name));
        int Final(string name) => Game.CharacterPlayerStats(conn).GetValueOrDefault(Key(name));
        void Rebuild()
        {
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife; conn.PlayerMana = conn.Pools.Mana;
        }
        try
        {
            Wear("Astramentis", 5); Wear("Perandus Blazon", PlayerInventories.Belt);
            Wear("Le Heup of All", 6); Wear("Le Heup of All", 7);
            var shield = Wear("Tukohama's Fortress", PlayerInventories.Offhand1);
            inventory.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
            { Sockets = [new(SocketColour.White, Gem("Rejuvenation Totem"))], Links = [1] });
            Rebuild(); Assert.False(Game.IsInactive(conn, shield.Id));
            const string armour = "base_physical_damage_reduction_rating";
            Assert.Equal(500, Stat("X_armour_per_active_totem")); int baseArmour = Stat(armour), basePool = conn.Pools.Armour;
            var totemGem = Game.GemSkillEffects(conn).Single();
            var variety = Game.TotemVariety(conn, totemGem.Effect)!; Assert.NotNull(variety);
            async Task<TotemState.Totem> Totem()
            {
                uint? id = await Game.CreateTotem("owned-counts", wire.Stream, wire.Ciphers, conn, totemGem.Id, totemGem.Effect,
                    totemGem.Level, variety, (100, 100));
                Assert.NotNull(id); return conn.Totems.All.Single(t => t.Id == id);
            }
            var one = await Totem(); Game.EnsureConditionalItemPools(conn);
            Assert.Equal(baseArmour + 500, Stat(armour)); Assert.True(conn.Pools.Armour > basePool);
            var two = await Totem(); Game.EnsureConditionalItemPools(conn); Assert.Equal(baseArmour + 1000, Stat(armour));
            var three = await Totem(); Assert.NotNull(one.DiedAt); Assert.Equal(baseArmour + 1000, Stat(armour));
            two.Life = 0; Game.EnsureConditionalItemPools(conn); Assert.Equal(baseArmour + 500, Stat(armour));
            three.Until = 0; Game.EnsureConditionalItemPools(conn); Assert.Equal(baseArmour, Stat(armour)); Assert.Equal(basePool, conn.Pools.Armour);
            three.Until = double.PositiveInfinity; Game.EnsureConditionalItemPools(conn);
            conn.PlayerStats[Key(armour)] = 123; Rebuild(); Assert.Equal(123, Final(armour)); conn.PlayerStats.Remove(Key(armour));
            conn.PlayerStats[Key("X_armour_per_active_totem")] = 7; Rebuild(); Assert.Equal(baseArmour + 7, Stat(armour));
            conn.PlayerStats.Remove(Key("X_armour_per_active_totem"));
            inventory.Remove(PlayerInventories.Offhand1, shield.Id); Rebuild(); Assert.Equal(0, Stat("X_armour_per_active_totem"));
            var restoredShield = inventory.Add(PlayerInventories.Offhand1, 0, 0, shield.Item); Rebuild(); Assert.Equal(baseArmour + 500, Stat(armour));
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
            Assert.True(Game.IsInactive(conn, restoredShield.Id)); Assert.Equal(0, Stat("X_armour_per_active_totem"));
            conn.Experience = CharacterStats.ExperienceFor(100); Rebuild(); Assert.Equal(baseArmour + 500, Stat(armour));
            conn.Totems.KillAll(0);
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);

            Wear("Astramentis", 5); Wear("Perandus Blazon", PlayerInventories.Belt);
            Wear("Le Heup of All", 6); Wear("Le Heup of All", 7);
            var femurs = Wear("Femurs of the Saints", PlayerInventories.Weapon1);
            Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, femurs.Id, item => item with
            {
                Sockets = [new(SocketColour.White, Gem("Summon Skeletons")), new(SocketColour.White, Gem("Summon Raging Spirit")),
                    new(SocketColour.White, Gem("Raise Zombie")), new(SocketColour.White, Gem("Raise Spectre"))], Links = [1, 1, 1, 1],
            }));
            Rebuild(); Assert.False(Game.IsInactive(conn, femurs.Id));
            var catalog = new SummonCatalog(); var monsters = new MonsterCatalog();
            var baseline = Sources(); int attackSpeed = conn.Pools.AttackSpeedPct;
            int lifeRegen = Final("total_life_recovery_per_minute_from_regeneration"), manaRegen = Final("total_mana_recovery_per_minute_from_regeneration");
            ushort Source(string effect) => Game.GemSkillEffects(conn).Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == effect).Id;
            ushort spirit = Source("SummonRagingSpirit"); int? baseCast = Game.CastMs(conn, Game.SupportedSkill(conn, spirit).Effect, spirit);
            uint nextCorpse = 0x81000000;
            async Task<GolemState.Golem> Summon(string effect)
            {
                var definition = catalog.Find(effect)!; uint target = 0;
                if (definition.Corpse)
                {
                    target = nextCorpse++;
                    conn.Monsters[target] = new("owned-count corpse", new(100, 100, 0), 0, 0, 1000)
                    { Variety = monsters.Find("Metadata/Monsters/Zombies/Zombie"), Level = 70, Dead = true, Life = 0 };
                }
                uint id = await Game.SummonFighter("owned-counts", wire.Stream, wire.Ciphers, conn, Source(effect), definition, (100, 100), target);
                Assert.NotEqual(0u, id); return conn.Summons.Fighters[id];
            }
            var skeleton = await Summon("SummonSkeletons"); await Summon("SummonSkeletons");
            Game.EnsureConditionalItemPools(conn);
            Assert.Equal(baseline.GetValueOrDefault(Key("additional_block_%")) + 2, Stat("additional_block_%"));
            Assert.True(conn.Pools.BlockChanceUncapped >= 2);
            var srs = await Summon("SummonRagingSpirit"); await Summon("SummonRagingSpirit");
            Game.EnsureConditionalItemPools(conn);
            Assert.Equal(baseline.GetValueOrDefault(Key("attack_speed_+%")) + 4, Stat("attack_speed_+%"));
            Assert.Equal(baseline.GetValueOrDefault(Key("base_cast_speed_+%")) + 4, Stat("base_cast_speed_+%"));
            Assert.Equal(attackSpeed + 4, conn.Pools.AttackSpeedPct); Assert.True(Game.CastMs(conn, Game.SupportedSkill(conn, spirit).Effect, spirit) < baseCast);
            await Summon("RaiseZombie"); Game.EnsureConditionalItemPools(conn);
            Assert.Equal(baseline.GetValueOrDefault(Key("life_regeneration_rate_per_minute_%")) + 36, Stat("life_regeneration_rate_per_minute_%"));
            Assert.True(Final("total_life_recovery_per_minute_from_regeneration") > lifeRegen);
            await Summon("RaiseSpectre"); Game.EnsureConditionalItemPools(conn);
            Assert.Equal(baseline.GetValueOrDefault(Key("mana_regeneration_rate_+%")) + 30, Stat("mana_regeneration_rate_+%"));
            Assert.True(Final("total_mana_recovery_per_minute_from_regeneration") > manaRegen);

            // One capture remains coherent if another owner ends a minion before the rest of the calculation.
            var captured = Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot(), null, null,
                () => skeleton.Monster.Life = 0).Sources;
            Assert.Equal(baseline.GetValueOrDefault(Key("additional_block_%")) + 2, captured.GetValueOrDefault(Key("additional_block_%")));
            Game.EnsureConditionalItemPools(conn); Assert.Equal(baseline.GetValueOrDefault(Key("additional_block_%")) + 1, Stat("additional_block_%"));
            schedule.At = conn.Minions.All.Single(m => m.Id == srs.Id).Until + .01;
            Game.EnsureConditionalItemPools(conn); Assert.Equal(attackSpeed, conn.Pools.AttackSpeedPct);
            Assert.Equal(baseline.GetValueOrDefault(Key("base_cast_speed_+%")), Stat("base_cast_speed_+%"));
            var stale = new SpawnedMonster("old identity", new(100, 100, 0), 0, 0, 1000);
            conn.Minions.Add(new(0x82000000, stale, "SummonSkeletons", "Skeleton") { Until = double.PositiveInfinity });
            conn.Monsters[0x82000000] = new("replacement identity", new(100, 100, 0), 0, 0, 1000);
            Assert.Equal(baseline.GetValueOrDefault(Key("additional_block_%")) + 1, Stat("additional_block_%"));
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
            Assert.True(Game.IsInactive(conn, femurs.Id));
            foreach (var rule in OwnedCountItemStats.Rules.Where(r => r.Count != OwnedCountItemStats.Totems)) Assert.Equal(0, Stat(rule.Input));
            conn.Experience = CharacterStats.ExperienceFor(100); Rebuild();
            Assert.Equal(baseline.GetValueOrDefault(Key("additional_block_%")) + 1, Stat("additional_block_%"));
            uint aliveLife = conn.PlayerLife; conn.PlayerLife = 0;
            Assert.Equal(baseline.GetValueOrDefault(Key("additional_block_%")), Stat("additional_block_%"));
            Assert.Equal(baseline.GetValueOrDefault(Key("life_regeneration_rate_per_minute_%")), Stat("life_regeneration_rate_per_minute_%"));
            conn.PlayerLife = aliveLife; Game.EnsureConditionalItemPools(conn);
            Assert.Equal(baseline.GetValueOrDefault(Key("additional_block_%")) + 1, Stat("additional_block_%"));
            inventory.Remove(PlayerInventories.Weapon1, femurs.Id);
            inventory.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!)); Rebuild();
            foreach (var rule in OwnedCountItemStats.Rules.Where(r => r.Count != OwnedCountItemStats.Totems)) Assert.Equal(0, Stat(rule.Input));
            Assert.Equal(attackSpeed, conn.Pools.AttackSpeedPct);
            Assert.Equal(lifeRegen, Final("total_life_recovery_per_minute_from_regeneration"));
            Assert.Equal(manaRegen, Final("total_mana_recovery_per_minute_from_regeneration"));
            Assert.Empty(conn.PlayerStats);
        }
        finally
        {
            conn.Closed = true; updates.Clear(); Game.AreaUpdateContext.Value = prior; PlayerInventories.Forget(character);
        }
    }
}
