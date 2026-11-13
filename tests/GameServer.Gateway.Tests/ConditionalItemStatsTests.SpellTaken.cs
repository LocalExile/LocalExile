using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_low_mana_spell_damage_taken_reaches_incoming_hits(ClientState conn,
        Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn);
        var saved = inventory.EquipmentSnapshot();
        var random = conn.IncomingHitRandom;
        var priorMana = conn.PlayerMana;
        try
        {
            // Retain only the two legal attribute rings; no other damage-taken or avoidance source participates.
            foreach (var (row, item) in saved.Where(e => e.Row is not (6 or 7))) inventory.Remove(row, item.Id);
            wear("Astramentis", 5);
            var source = wear("Infernal Mantle", PlayerInventories.BodyArmour);
            uint key = Program.Stats.Key("base_spell_damage_taken_+%")!.Value;
            uint condition = Program.Stats.Key("spell_damage_taken_+%_when_on_low_mana")!.Value;
            var raw = ItemModStats.Sum([source.Item], Program.Items.Mods);
            int coefficient = raw["spell_damage_taken_+%_when_on_low_mana"];
            Assert.Equal(100, coefficient);
            Dictionary<uint, int> At(bool low)
            {
                conn.PlayerMana = conn.Pools.Mana / 2 + (low ? 0u : 1u);
                return Game.CharacterPlayerStats(conn);
            }
            var high = At(false);
            Assert.Equal(0, high.GetValueOrDefault(key));
            var low = At(true);
            Assert.Equal(coefficient, low.GetValueOrDefault(key));
            Assert.Equal(0, low.GetValueOrDefault(condition));
            foreach (var type in Enum.GetValues<DamageType>())
            {
                int Base(bool spell, bool attack) => Game.MitigateTakenWithArmour(conn, type, 100, 0, 75, 0,
                    high, attack: attack, ranged: false, spell: spell);
                int Active(bool spell, bool attack) => Game.MitigateTakenWithArmour(conn, type, 100, 0, 75, 0,
                    low, attack: attack, ranged: false, spell: spell);
                Assert.Equal(2 * Base(true, false), Active(true, false));
                Assert.Equal(Base(false, true), Active(false, true));
                Assert.Equal(Base(false, false), Active(false, false)); // A non-attack secondary hit is not a spell.
                double carry = 0;
                Assert.Equal(100, IncomingResourceDefences.TakenOverTime(100,
                    name => Program.Stats.Key(name) is uint stat ? low.GetValueOrDefault(stat) : 0, ref carry));
            }
            conn.Clock.Start(); conn.IncomingHitRandom = new PhasingRoll(99);
            int Synthetic(bool lowMana, bool spell)
            {
                At(lowMana); conn.PlayerLife = conn.PlayerMaxLife;
                Assert.True(Game.TrySyntheticEnemyHit(conn, DamageType.Fire, 100, block: false, spell: spell,
                    suppress: false, out var hit));
                Assert.False(hit.Blocked); Assert.False(hit.Suppressed);
                return hit.Damage;
            }
            int ordinary = Synthetic(false, true);
            Assert.True(ordinary > 0);
            Assert.Equal(2 * ordinary, Synthetic(true, true));
            Assert.Equal(ordinary, Synthetic(true, false));
            Assert.Equal(ordinary, Synthetic(false, true));

            var monster = new SpawnedMonster("Infernal Mantle contact control", new(100, 100, 0), 0, 0, 1000);
            int Contact(bool lowMana, bool spell)
            {
                At(lowMana); conn.PlayerLife = conn.PlayerMaxLife; conn.EvasionEntropy = 100;
                var attack = new MonsterAttack(0, 1, 100, DamagePacket.Of(DamageType.Fire, 100, 100)) { Spell = spell };
                var hit = Game.CommitMonsterHitLocked("infernal-control", conn, 0x5047, monster, attack,
                    conn.Clock.Elapsed.TotalSeconds, (100, 100), (100, 100), checkRange: false);
                Assert.NotNull(hit); return hit.Damage;
            }
            int ordinaryContact = Contact(false, true);
            Assert.True(ordinaryContact > 0);
            Assert.Equal(2 * ordinaryContact, Contact(true, true));
            Assert.Equal(ordinaryContact, Contact(true, false));

            // Removing just the real row keeps every other property of the actual host fixed.
            Assert.NotNull(inventory.Change(PlayerInventories.BodyArmour, source.Id, item => item with
            {
                Mods = item.Mods.Where(m => Program.Items.Mods.ByHash(m.Hash)?.Stats
                    .All(s => s.Id != "spell_damage_taken_+%_when_on_low_mana") != false).ToArray()
            }));
            Assert.Equal(0, At(true).GetValueOrDefault(key));
            Assert.Equal(ordinary, Synthetic(true, true));
            inventory.Remove(PlayerInventories.BodyArmour, source.Id);
            source = inventory.Add(PlayerInventories.BodyArmour, source.X, source.Y, source.Item);
            Assert.Equal(coefficient, At(true).GetValueOrDefault(key));
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
            Assert.True(Game.IsInactive(conn, source.Id)); Assert.Equal(0, At(true).GetValueOrDefault(key));
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            Assert.Equal(coefficient, At(true).GetValueOrDefault(key));
            inventory.Remove(PlayerInventories.BodyArmour, source.Id);
            Assert.Equal(0, At(true).GetValueOrDefault(key));
            Assert.Empty(conn.PlayerStats);
        }
        finally
        {
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.PlayerMaxLife; conn.PlayerMana = Math.Min(priorMana, conn.Pools.Mana);
            conn.IncomingHitRandom = random;
        }
    }
}
