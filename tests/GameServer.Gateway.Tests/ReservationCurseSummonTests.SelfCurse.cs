using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    static async Task SelfVulnerabilityFixture(ClientState template, ModuleLifetime lifetime)
    {
        string character = "SelfCurse" + Guid.NewGuid().ToString("N");
        PlayerInventories.SetClass(character, 1);
        CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
            Experience: CharacterStats.ExperienceFor(100)));
        var schedule = new CatClockSchedule { At = 100 };
        var updates = new PlayerUpdatesState(lifetime, schedule);
        var previousContext = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = updates;
        using var wire = new Wire();
        var conn = new ClientState(template.Layout, 0xC025, 1)
        {
            Character = character, CharacterId = Guid.NewGuid(), Account = "self-curse-runtime",
            League = "Standard", LeagueForm = "Standard", ClassRow = 1, Instance = new(),
            PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
            Experience = CharacterStats.ExperienceFor(100), LeagueModule = template.LeagueModule,
            Wire = ("self-curse", wire.Stream, wire.Ciphers), Pools = new(100, 1, 1, 0, 0), PlayerLife = 1,
        };
        try
        {
            var inventory = Game.ItemsOf(conn);
            foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1, PlayerInventories.Helmet,
                         PlayerInventories.BodyArmour, PlayerInventories.Boots, PlayerInventories.Gloves, 5, 6, 7, 10 })
                foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);
            var uniques = new UniqueCatalog(Program.Items);
            inventory.Add(5, 0, 0, uniques.Create("Astramentis", new Random(504))!);
            inventory.Add(6, 0, 0, uniques.Create("Le Heup of All", new Random(504))!);
            inventory.Add(7, 0, 0, uniques.Create("Le Heup of All", new Random(505))!);
            conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life;
            int Stat(string name) => Program.Stats.Key(name) is uint key
                ? Game.CharacterPlayerStats(conn).GetValueOrDefault(key) : 0;
            var mirror = inventory.Add(PlayerInventories.Offhand1, 0, 0, uniques.Create("Atziri's Mirror", new Random(504))!);
            conn.Pools = Game.PoolsFor(conn, 100);
            Assert.Equal(10, Stat("additional_block_%"));
            Assert.Equal(0, Stat("additional_spell_block_%"));
            int uncursedBlock = conn.Pools.BlockChance;
            var belt = inventory.Add(10, 0, 0, uniques.Create("Coward's Legacy", new Random(504))!);
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            Game.WithOwnAuraChanges(conn, () => []);
            conn.Pools = Game.PoolsFor(conn, 100);
            Assert.Equal(0, Stat("additional_block_%"));
            Assert.Equal(20, Stat("additional_spell_block_%"));
            Assert.Equal(uncursedBlock - 10, conn.Pools.BlockChance);
            Assert.Equal(20, conn.Pools.SpellBlockChance);
            Assert.NotEqual(0, conn.Reservations.SelfVulnerabilityBuff);
            Assert.Equal(1, Stat("cursed_by_vulnerability"));
            Assert.Equal(1, Stat("you_are_cursed_with_vulnerability"));
            Assert.Equal(1, Stat("virtual_on_low_life"));
            var raw = Game.CharacterPlayerStats(conn);
            Assert.True(Game.CriticalOnLowLife(conn, raw));
            Assert.True(Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)["_while_on_low_life"]);
            int level = Stat("self_cursed_with_level_x_vulnerability");
            Assert.Equal(10, level);
            Assert.Equal(33, Stat("physical_damage_taken_+%"));
            Assert.Equal(37, Stat("receive_bleeding_chance_%_when_hit_by_attack"));
            var immune = new Dictionary<uint, int>(raw) { [Program.Stats.Key("immune_to_curses")!.Value] = 1 };
            Assert.Empty(Game.SelfVulnerabilityStats(conn, immune));
            var hexproof = new Dictionary<uint, int>(raw) { [Program.Stats.Key("hexproof")!.Value] = 1 };
            var hexproofCurse = Game.SelfVulnerabilityStats(conn, hexproof).ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(1, hexproofCurse[Program.Stats.Key("cursed_by_vulnerability")!.Value]);
            Assert.Equal(0, hexproofCurse[Program.Stats.Key("physical_damage_taken_+%")!.Value]);
            int Get(string name) => Program.Stats.Key(name) is uint key ? raw.GetValueOrDefault(key) : 0;
            Assert.Equal(33, PlayerDamageTaken.IncreasedPct(DamageType.Physical, false, Get));
            conn.IncomingHitRandom = new AspectRoll(0);
            var enemy = new SpawnedMonster("self-curse-attacker", new(100, 100, 1), 100, 8, 1000) { Hostile = true };
            Assert.Equal(0, enemy.Mods.Effects["bleed_on_hit_with_attacks_%"]);
            Game.ApplyMonsterDamagingAilmentsLocked(conn, 0xA025, enemy,
                DamagePacket.Of(DamageType.Physical, 100, 100), false, schedule.At);
            Assert.True(conn.IncomingAilments.Bleeding(schedule.At));
            conn.IncomingAilments.Clear();
            Game.ApplyMonsterDamagingAilmentsLocked(conn, 0xA025, enemy,
                DamagePacket.Of(DamageType.Physical, 100, 100), true, schedule.At);
            Assert.False(conn.IncomingAilments.Bleeding(schedule.At));
            // Atziri's Reflection removes the penalty but preserves the cursed predicate and its low-Life benefit.
            inventory.Remove(PlayerInventories.Offhand1, mirror.Id);
            var shield = inventory.Add(PlayerInventories.Offhand1, 0, 0, uniques.Create("Atziri's Reflection", new Random(504))!);
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            Assert.False(Game.IsInactive(conn, shield.Id));
            Assert.Equal(0, Stat("physical_damage_taken_+%"));
            Assert.Equal(0, Stat("receive_bleeding_chance_%_when_hit_by_attack"));
            Assert.Equal(1, Stat("virtual_on_low_life"));
            Assert.Equal(1, Stat("cursed_by_vulnerability"));
            inventory.Remove(PlayerInventories.Offhand1, shield.Id);
            inventory.Remove(10, belt.Id);
            var chains = inventory.Add(10, 0, 0, uniques.Create("Coward's Chains", new Random(504))!);
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            conn.PlayerLife = conn.PlayerMaxLife * 3 / 4;
            raw = Game.CharacterPlayerStats(conn);
            Assert.True(Game.CountsAsFullLife(conn.PlayerLife, conn.PlayerMaxLife, raw));
            Assert.False(Game.CriticalOnLowLife(conn, raw));
            Assert.True(Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)["_while_on_full_life"]);
            Assert.Equal(20, Stat("self_cursed_with_level_x_vulnerability"));
            Assert.Equal(30, Stat("physical_damage_taken_+%"));
            Assert.Equal(-1, Stat("base_enemy_extra_damage_rolls"));
            Assert.Equal(25, Stat("receive_bleeding_chance_%_when_hit_by_attack"));
            conn.Pools = Game.PoolsFor(conn, 1);
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            Game.WithOwnAuraChanges(conn, () => []);
            Assert.True(Game.IsInactive(conn, chains.Id));
            Assert.Equal(0, Stat("cursed_by_vulnerability"));
            Assert.Equal(0, conn.Reservations.SelfVulnerabilityBuff);
            conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life;
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            Game.WithOwnAuraChanges(conn, () => []);
            Assert.False(Game.IsInactive(conn, chains.Id));
            Assert.Equal(1, Stat("cursed_by_vulnerability"));
            conn.PlayerLife = 0;
            await Game.EndAurasOnDeath("self-curse", wire.Stream, wire.Ciphers, conn);
            Assert.Equal(0, conn.Reservations.SelfVulnerabilityBuff);
            Assert.Equal(0, Stat("cursed_by_vulnerability"));
            conn.PlayerLife = conn.PlayerMaxLife;
            Game.WithOwnAuraChanges(conn, () => []);
            Assert.NotEqual(0, conn.Reservations.SelfVulnerabilityBuff);
            inventory.Remove(10, chains.Id);
            conn.PlayerLife = conn.PlayerMaxLife * 3 / 4;
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            Game.WithOwnAuraChanges(conn, () => []);
            Assert.Equal(0, conn.Reservations.SelfVulnerabilityBuff);
            Assert.Equal(0, Stat("cursed_by_vulnerability"));
            Assert.Equal(0, Stat("physical_damage_taken_+%"));
            Assert.False(Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)["_while_on_full_life"]);
            Assert.Empty(conn.PlayerStats);
            Assert.Empty(conn.Auras);
        }
        finally
        {
            conn.Closed = true;
            Game.AreaUpdateContext.Value = previousContext;
            PlayerInventories.Forget(character);
        }
    }
}
