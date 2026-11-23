using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class IncomingHitEventsTests
{
    sealed class LowRolls : Random
    {
        public override int Next(int maxValue) => 0;
        public override double NextDouble() => 0;
    }

    [Fact]
    public async Task Real_and_synthetic_hits_record_the_same_block_suppression_and_recovery_events()
    {
        string path = Path.Combine(Path.GetTempPath(), $"incoming-hit-events-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            using var wire = new PartyFollowerTests.Wire();
            var catalog = Program.Items;
            var flaskBase = Assert.IsType<ItemCatalog.BaseType>(catalog.Find("Quicksilver Flask"));
            var hitMod = catalog.Mods.All.First(m => m.GenerationType is 1 or 2 && m.WeightFor(flaskBase.Tags) > 0
                && m.Stats.Count == 1 && m.Stats[0].Id == Stat.LocalFlaskGainXChargesWhenHit && m.RangeAt(0).Max > 0);
            int chargesPerHit = Assert.Single(hitMod.MaxValues());
            Assert.InRange(chargesPerHit, 1, flaskBase.MaxCharges - 1);
            var rechargeFlask = ItemCatalog.Create(flaskBase) with
            {
                Charges = 0, Rarity = ItemRarity.Magic, Identified = true,
                Mods = [hitMod.With(ModSection.Explicit, hitMod.MaxValues(), new Random(1))],
            };
            foreach (bool synthetic in new[] { false, true })
            foreach (bool spell in new[] { false, true })
            foreach (bool block in new[] { false, true })
            {
                var player = new Player(new(1, "incoming-hit-events", 1000, 1000, 100, 100, null, []), 1, 1)
                {
                    Character = { Name = $"HitEvents{synthetic}{spell}{block}" },
                    PlayerLife = 1000,
                    PlayerMana = 100,
                    Instance = new(),
                    EvasionEntropy = 100,
                    IncomingHitRandom = new LowRolls(),
                    Pools = new(100, 1000, 100, 0, 0, BlockChance: block ? 100 : 0, SpellBlockChance: block ? 100 : 0,
                        SpellSuppressionChance: 100)
                };
                using var hitWire = new PartyFollowerTests.Wire();
                player.Connection.Session = new("incoming-hit-test", hitWire.Stream, hitWire.Ciphers);
                if (!synthetic) player.Clock.Stop();
                double now = player.Clock.Elapsed.TotalSeconds;
                try
                {
                    Game.MonsterHitOutcome? realHit = null;
                    var flask = !synthetic ? Game.ItemsOf(player).Add(PlayerInventories.Flask, 0, 0, rechargeFlask) : null;
                    var monster = new SpawnedMonster("hitter", new(0, 0, 0), 0, 0, 1000) { Level = 10 };
                    var attack = new MonsterAttack(0, 1, 10, DamagePacket.Of(DamageType.Physical, 400, 400)) { Spell = spell };
                    if (synthetic)
                    {
                        Assert.True(Game.TrySyntheticEnemyHit(player, DamageType.Physical, 400, block, spell,
                            suppress: spell, out var hit));
                        Assert.Equal(block, hit.Blocked);
                        Assert.Equal(spell, hit.Suppressed);
                        now = player.Clock.Elapsed.TotalSeconds;
                    }
                    else
                    {
                        // A missed contact must not create any accepted-hit bookkeeping.
                        Assert.Null(Game.CommitMonsterHitLocked(player, 1, monster, attack, now,
                            (1000, 1000), (0, 0), checkRange: true));
                        Assert.Empty(player.RecentEvents.Snapshot(now));
                        Assert.Equal(0, player.RecentEnemyHits.Count(now));
                        Assert.False(Game.ChargesOf(player).Pending);
                        realHit = Game.CommitMonsterHitLocked(player, 1, monster, attack, now,
                            (0, 0), (0, 0), checkRange: false);
                        Assert.NotNull(realHit);
                        Assert.Equal(block, realHit.Blocked);
                    }
                    Assert.Equal(1, player.RecentEnemyHits.Count(now));
                    Assert.True(player.RecentEvents.Happened(StatConditions.BeenHit, now));
                    Assert.Equal(block, player.RecentEvents.Happened(StatConditions.Blocked, now));
                    Assert.Equal(block && spell, player.RecentEvents.Happened(StatConditions.BlockedSpell, now));
                    Assert.Equal(block && !spell, player.RecentEvents.Happened(StatConditions.BlockedAttack, now));
                    Assert.Equal(spell, player.RecentEvents.Happened(StatConditions.SuppressedSpell, now));
                    Assert.Equal(!block, player.RecentEvents.Happened(StatConditions.TakenSavageHit, now));
                    Assert.Equal(new[] { block ? FlaskEnchantments.Trigger.Block : FlaskEnchantments.Trigger.SavageHit },
                        player.FlaskAutomation.Events.Select(e => e.Trigger));

                    // Both charge consumers are present: only the matching block type may grant its charge.
                    int Stat(uint name) => name is global::Stat.ChanceToGainEnduranceChargeWhenHitPct
                        or global::Stat.ChanceToGainFrenzyChargeOnBlockAttackPct or global::Stat.PowerChargeOnSpellBlockPctChance ? 100 : 0;
                    var charges = Game.ChargesOf(player);
                    charges.Step(now, Stat, () => default, alive: true, moving: false, inTown: false, new LowRolls());
                    Assert.Equal(1, charges.Effective(ChargeType.Endurance));
                    Assert.Equal(block && !spell ? 1 : 0, charges.Effective(ChargeType.Frenzy));
                    Assert.Equal(block && spell ? 1 : 0, charges.Effective(ChargeType.Power));
                    if (realHit is not null)
                    {
                        // Native recharge stays on the asynchronous flask transaction path. Blocking retains this
                        // defender's when-hit benefit, and presenting the hit must not enqueue its triggers twice.
                        Assert.NotNull(flask);
                        Assert.Equal(0, Game.ItemsOf(player).Items(PlayerInventories.Flask).Single(f => f.Id == flask.Id).Item.Charges);
                        var triggers = player.FlaskAutomation.Events.Select(e => e.Trigger).ToArray();
                        await Game.PresentMonsterHit(player, 1, realHit);
                        Assert.Equal(chargesPerHit,
                            Game.ItemsOf(player).Items(PlayerInventories.Flask).Single(f => f.Id == flask.Id).Item.Charges);
                        Assert.Equal(triggers, player.FlaskAutomation.Events.Select(e => e.Trigger));
                    }
                }
                finally
                {
                    PlayerInventories.Forget(player.Character.Name!);
                    Game.ChargeSessions.Remove(player);
                }
            }
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
