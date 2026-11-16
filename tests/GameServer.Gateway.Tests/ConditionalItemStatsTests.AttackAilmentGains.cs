using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Attack_ailment_sources_grant_charges_only_after_accepted_effects(ClientState conn,
        Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn); var saved = inventory.EquipmentSnapshot();
        var session = Game.ChargesOf(conn);
        Dictionary<uint, int> stats = [];
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        void Read()
        {
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife;
            stats = Game.CharacterPlayerStats(conn);
        }
        void Step(double at) => session.Step(at, Get, () => Game.WeaponProfileFor(conn),
            alive: true, moving: false, inTown: true, new PhasingRoll(99));
        var ids = new List<uint>();
        try
        {
            foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
            wear("Astramentis", 5); wear("Le Heup of All", 6); wear("Le Heup of All", 7);
            foreach (bool ignite in new[] { true, false })
            {
                session.Die();
                string name = ignite ? "Oro's Sacrifice" : "Replica Oro's Sacrifice";
                string coefficient = ignite ? Game.FrenzyOnAttackIgnite : Game.EnduranceOnAttackFreeze;
                var type = ignite ? ChargeType.Frenzy : ChargeType.Endurance;
                var source = wear(name, PlayerInventories.Weapon1); Read();
                Assert.False(Game.IsInactive(conn, source.Id)); Assert.Equal(ignite ? 100 : 1, Get(coefficient));
                var captured = Game.AttackAilmentGains.Capture(stats, attack: true);
                Assert.Equal(ignite, captured.IgniteFrenzy); Assert.Equal(!ignite, captured.FreezeEndurance);
                uint next = (uint)(ignite ? 0x5040A0 : 0x5040B0);
                SpawnedMonster Target(bool hostile = true)
                {
                    var target = new SpawnedMonster("attack ailment grant", new(100, 100, 1), 1, 1, 1000)
                        { Hostile = hostile, Life = 1000 };
                    conn.Monsters[++next] = target; ids.Add(next); return target;
                }
                void Inflict(SpawnedMonster target, Game.AttackAilmentGains gains, bool owned = false, bool empty = false)
                {
                    if (ignite)
                    {
                        var settings = Game.DefaultAttackIgnite(conn, stats);
                        Assert.NotNull(Game.IgniteOnHitLocked(conn, next, target, settings, critical: true, 1,
                            playerIgnite: !owned, attackGains: gains));
                    }
                    else
                        Game.AilmentsOnHitLocked(conn, next, target, empty ? DamagePacket.Empty : DamagePacket.Of(DamageType.Cold, 200, 200),
                            spell: false, critical: true, 100, stats, null, 1, new PhasingRoll(99), attackGains: gains);
                }
                Inflict(Target(), Game.AttackAilmentGains.Capture(stats, attack: false));
                Step(1); Assert.Equal(0, session.Effective(type));
                if (ignite)
                {
                    Inflict(Target(), captured, owned: true); Step(1); Assert.Equal(0, session.Effective(type));
                    var immune = Target(); immune.KingGrace = true;
                    Assert.Null(Game.IgniteOnHitLocked(conn, next, immune, Game.DefaultAttackIgnite(conn, stats),
                        true, 1, attackGains: captured));
                    Step(1); Assert.Equal(0, session.Effective(type));
                }
                else
                {
                    Inflict(Target(), captured, empty: true); Step(1); Assert.Equal(0, session.Effective(type));
                }
                var friendly = Target(hostile: false);
                Inflict(friendly, captured); Step(1); Assert.Equal(0, session.Effective(type));
                conn.PlayerLife = 0;
                Inflict(Target(), captured); Step(1); Assert.Equal(0, session.Effective(type));
                conn.PlayerLife = conn.PlayerMaxLife;
                Inflict(Target(), captured);
                Assert.True(session.Pending);
                inventory.Remove(PlayerInventories.Weapon1, source.Id); Read();
                Assert.Equal(default, Game.AttackAilmentGains.Capture(stats, attack: true));
                Step(1); Assert.Equal(1, session.Effective(type)); // Already accepted grants survive a later source change.
                Step(100); Assert.Equal(0, session.Effective(type));
                var restored = inventory.Add(PlayerInventories.Weapon1, 0, 0, source.Item); Read();
                Assert.Equal(captured, Game.AttackAilmentGains.Capture(stats, attack: true));
                conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
                stats = Game.CharacterPlayerStats(conn);
                Assert.True(Game.IsInactive(conn, restored.Id));
                Assert.Equal(default, Game.AttackAilmentGains.Capture(stats, attack: true));
                conn.Experience = CharacterStats.ExperienceFor(100); Read();
                Assert.Equal(captured, Game.AttackAilmentGains.Capture(stats, attack: true));
                inventory.Remove(PlayerInventories.Weapon1, restored.Id); Read();
            }
        }
        finally
        {
            session.Die(); conn.Experience = CharacterStats.ExperienceFor(100);
            foreach (uint id in ids) conn.Monsters.Remove(id);
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
            Read();
        }
    }
}
