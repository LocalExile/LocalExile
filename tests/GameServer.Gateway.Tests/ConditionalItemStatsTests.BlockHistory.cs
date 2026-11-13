using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Real_monster_blocks_keep_attack_and_spell_history_distinct(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn); var saved = inventory.EquipmentSnapshot();
        var random = conn.IncomingHitRandom; bool running = conn.Clock.IsRunning; conn.Clock.Stop();
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        try
        {
            foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
            wear("Astramentis", 5);
            var shield = wear("Daresso's Courage", PlayerInventories.Offhand1);
            conn.IncomingHitRandom = new PhasingRoll(0);
            var monster = new SpawnedMonster("typed block history", new(100, 100, 0), 0, 0, 1000);
            foreach (bool spell in new[] { true, false })
            {
                conn.RecentEvents.Clear(); Game.ChargesOf(conn).Die();
                conn.Pools = Game.PoolsFor(conn, 100);
                conn.PlayerLife = spell ? conn.PlayerMaxLife / 2 : conn.PlayerMaxLife;
                var before = Game.CharacterPlayerStats(conn);
                Assert.False(Game.IsInactive(conn, shield.Id));
                int attackBlock = conn.Pools.BlockChance, spellBlock = conn.Pools.SpellBlockChance;
                Assert.True(spell ? spellBlock > 0 : attackBlock > 0);
                conn.EvasionEntropy = 100;
                var attack = new MonsterAttack(0, 1, 100, DamagePacket.Of(DamageType.Fire, 100, 100)) { Spell = spell };
                double now = conn.Clock.Elapsed.TotalSeconds;
                conn.LastEvasionAt = now;
                var hit = Game.CommitMonsterHitLocked("typed-block-control", conn, 0x5048, monster, attack,
                    now, (100, 100), (100, 100), checkRange: false);
                Assert.NotNull(hit); Assert.True(hit.Blocked);
                Assert.True(conn.RecentEvents.Happened(StatConditions.Blocked, now));
                Assert.True(conn.RecentEvents.Happened(spell ? StatConditions.BlockedSpell : StatConditions.BlockedAttack, now));
                Assert.False(conn.RecentEvents.Happened(spell ? StatConditions.BlockedAttack : StatConditions.BlockedSpell, now));
                var after = Game.CharacterPlayerStats(conn);
                string final = spell ? "attack_block_%" : "spell_block_%";
                Assert.True(after.GetValueOrDefault(Key(final)) > before.GetValueOrDefault(Key(final)),
                    $"spell={spell}, final={final}, before={before.GetValueOrDefault(Key(final))}, after={after.GetValueOrDefault(Key(final))}; "
                    + string.Join(",", after.Where(p => Program.Stats.Name(p.Key)?.Contains("block") == true)
                        .Select(p => Program.Stats.Name(p.Key) + "=" + p.Value)));
                // Diagnostic coefficients isolate the typed event queue; the positive block itself uses the real shield.
                int Chance(string name) => name is "chance_to_gain_frenzy_charge_on_block_attack_%"
                    or "power_charge_on_spell_block_%_chance" ? 100 : 0;
                Game.ChargesOf(conn).Step(now, Chance, () => default, alive: true,
                    moving: false, inTown: true, new PhasingRoll(0));
                Assert.Equal(spell ? 1 : 0, Game.ChargesOf(conn).Effective(ChargeType.Power));
                Assert.Equal(spell ? 0 : 1, Game.ChargesOf(conn).Effective(ChargeType.Frenzy));
                conn.RecentEvents.Record(spell ? StatConditions.BlockedSpell : StatConditions.BlockedAttack, now - 4);
                var expired = Game.CharacterPlayerStats(conn);
                Assert.Equal(before.GetValueOrDefault(Key(final)), expired.GetValueOrDefault(Key(final)));
            }
        }
        finally
        {
            conn.IncomingHitRandom = random; conn.RecentEvents.Clear(); Game.ChargesOf(conn).Die();
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Put(row, item);
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife;
            if (running) conn.Clock.Start();
        }
    }
}
