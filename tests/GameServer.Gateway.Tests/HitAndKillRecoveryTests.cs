using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class HitAndKillRecoveryTests
{
    [Fact]
    public async Task Watchers_Eye_recovers_life_and_shield_on_paid_spell_and_attack_hits()
    {
        var jewel = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Watcher's Eye", new Random(17)));
        var lifeMod = Program.Items.Mods.ById("VitalityLifeGainPerHit")!;
        var shieldMod = Program.Items.Mods.ById("DisciplineEnergyShieldPerHit")!;
        var cases = new (bool Equipped, bool Vitality, bool Discipline)[]
        {
            (false, true, true), (true, false, false), (true, true, true),
            (true, true, false), (true, false, true),
        };
        foreach (var (equipped, vitality, discipline) in cases)
        {
            var clock = new KillClock();
            using var fixture = new TriggeredSummonTests.Fixture(clock);
            var player = fixture.Player;
            player.ProjectileRuntimeState = fixture.Session;
            player.Walk.Teleport(100, 100, clock.Now(player));
            var items = Game.ItemsOf(player);
            uint main = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
            fixture.Equip(("Ice Nova", 1), ("Faster Casting Support", 1), ("Arc", 1),
                ("Heavy Strike", 1), ("Vitality", 1), ("Discipline", 1));
            var sockets = Assert.Single(items.Items(PlayerInventories.Weapon1)).Item.Sockets.Take(6).ToArray();
            items.Change(PlayerInventories.Weapon1, main, _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!) with
                { Sockets = [sockets[3]], Links = [1] });
            items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
                { Sockets = [sockets[0], sockets[1], sockets[2], sockets[4], sockets[5]], Links = [2, 1, 1, 1] });
            var gems = Game.GemSkillEffects(player).ToDictionary(skill => skill.Name);
            const byte slot = 1;
            player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(slot)!.Value);
            Game.ItemsOf(player).Add(PlayerInventories.PassiveJewels, slot, 0, jewel with
            {
                Mods = equipped ? [new(ModSection.Explicit, lifeMod.Hash, [26]), new(ModSection.Explicit, shieldMod.Hash, [21])] : [],
            });
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, player)) values[Stat.GlobalAlwaysHit] = values[Stat.GlobalCannotCrit] = 1;
                return values;
            });
            var previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            try
            {
                foreach (var (name, active) in new[] { ("Vitality", vitality), ("Discipline", discipline) })
                {
                    var aura = gems[name];
                    if (active)
                        Assert.True(await Game.ToggleAura(player, aura.Id, aura.Effect, aura.Name,
                            Game.SupportedReservationCost(player, aura.Id)!.Value, aura.Level, aura.Quality));
                    Assert.Equal(active, Game.AffectingAuras(player).Any(a => a.SkillId == aura.Id));
                }
                Game.RecomputePoolsAndSpeed(player);
                var expected = (equipped && vitality ? 26u : 0u, equipped && discipline ? 21u : 0u);
                foreach (string name in vitality == discipline ? new[] { "Ice Nova", "Arc", "Heavy Strike" } : new[] { "Arc" })
                    Assert.Equal(expected, await Hit(name));
                if (equipped && (vitality || discipline))
                    Assert.Equal((vitality ? 5u : 0u, discipline ? 7u : 0u), await Hit("Arc", nearCap: true));
            }
            finally { Game.AreaUpdateContext.Value = previous; }

            async Task<(uint Life, uint EnergyShield)> Hit(string name, bool nearCap = false)
            {
                await clock.Wait(1000, CancellationToken.None);
                const uint id = 900;
                var target = new SpawnedMonster("recovery witness", new(104, 100, 0), 0, 0, 10000)
                    { ObjectId = id, Hostile = true };
                player.Area.Monsters[id] = target;
                player.Area.Grid.Set(new(id), (104, 100));
                player.EnergyShield.SetMaximum(82);
                player.EnergyShield.Empty();
                if (nearCap) player.EnergyShield.Recover(75);
                uint shield = player.EnergyShield.Current;
                uint life = player.PlayerLife = nearCap ? player.PlayerMaxLife - 5 : player.PlayerMaxLife / 2;
                uint mana = player.PlayerMana;
                try
                {
                    Assert.True(await Game.CastGemSkill(player, gems[name].Id, (100, 100), (104, 100), targetId: id));
                    Assert.True(player.PlayerMana < mana);
                    Assert.InRange(target.Life, 1u, target.MaxLife - 1);
                    return (player.PlayerLife - life, player.EnergyShield.Current - shield);
                }
                finally { player.Area.Monsters.Remove(id); }
            }
        }
    }

    sealed class KillClock : IPlayerUpdateSchedule
    {
        double now = 1;
        public double Now(Player player) => now;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            now += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }

    static Dictionary<uint, int> With(params (string Name, int Value)[] stats) =>
        stats.ToDictionary(s => Stat.Of(s.Name), s => s.Value);

    static readonly Dictionary<uint, int> GainRaw = new()
    {
        [Stat.BaseLifeGainPerTarget] = 44, [Stat.ManaGainPerTarget] = 3, [Stat.EnergyShieldGainPerTarget] = 2,
    };
    static readonly Dictionary<uint, int> GainWeapon = new()
    {
        [Stat.LocalLifeGainPerTarget] = 5, [Stat.LocalManaGainPerTarget] = 1, [Stat.LocalLifeAndManaGainPerTarget] = 10,
    };

    // Culling Strike, gain on hit and Life Recoup: what each source gives, and the hit that applies it.
    [Fact]
    public void Hits_cull_gain_and_recoup_by_their_stats()
    {
        // Culling Strike: the threshold comes from the character and the skill.
        Assert.Equal(default, Game.Cull.Of(null, null, critical: true, spell: true));
        // The support: its implicit stat and its quality's life recovery.
        Assert.Equal(new Game.Cull(10, 2), Game.Cull.Of(null,
            With(("kill_enemy_on_hit_if_under_10%_life", 1), ("recover_%_maximum_life_on_cull", 2)), false, false));
        var crits = With(("crits_have_culling_strike", 1));
        Assert.Equal(0, Game.Cull.Of(crits, null, critical: false, spell: false).Threshold);
        Assert.Equal(10, Game.Cull.Of(crits, null, critical: true, spell: false).Threshold);
        var spells = With(("spells_have_culling_strike", 1));
        Assert.Equal(0, Game.Cull.Of(spells, null, critical: false, spell: false).Threshold);
        Assert.Equal(10, Game.Cull.Of(spells, null, critical: false, spell: true).Threshold);
        Assert.Equal(20, Game.Cull.Of(With(("kill_enemy_on_hit_if_under_20%_life", 1)),
            With(("kill_enemy_on_hit_if_under_10%_life", 1)), false, false).Threshold);

        // A hit that leaves the enemy at the threshold kills it.
        {
            var player = new Player(new(1, "cull", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(1, 1000, 100, 2, 0), PlayerLife = 500,
            };
            var cull = new Game.Cull(10, 2);
            SpawnedMonster Monster(MonsterModSet? mods = null) => new("cull target", new(100, 100, 0), 0, 0, 1000)
                { Mods = mods ?? MonsterModSet.None };

            var above = Monster();
            Assert.Equal(101u, Game.TakeOwnerHitLocked(player, above, 899, cull).Life);
            Assert.Equal(500u, player.PlayerLife);

            var at = Monster();
            Assert.Equal(0u, Game.TakeOwnerHitLocked(player, at, 900, cull).Life);
            Assert.Equal(520u, player.PlayerLife);   // 2% of 1000 maximum Life

            var immune = Monster(new([], [], new(new Dictionary<uint, int> { [Stat.ImmuneToCullingStrike] = 1 })));
            Assert.Equal(100u, Game.TakeOwnerHitLocked(player, immune, 900, cull).Life);
            Assert.Equal(100u, Game.TakeOwnerHitLocked(player, Monster(), 900, default).Life);

            // The default melee producer calls this shared hit commit directly; generic area/projectile consumers
            // additionally call ApplyMonsterHitLocked. Both must record only the direct hit's excess Life damage.
            var genericOverkill = Monster();
            Assert.NotNull(Game.ApplyMonsterHitLocked(player, genericOverkill, 1500));
            Assert.Equal(500, genericOverkill.Overkill);
            var meleeOverkill = Monster();
            Assert.Equal(0u, Game.TakeOwnerHitLocked(player, meleeOverkill, 1500, default).Life);
            Console.WriteLine($"#504 shared hit overkill generic={genericOverkill.Overkill} defaultMelee={meleeOverkill.Overkill} expected=500");
            Assert.Equal(500, meleeOverkill.Overkill);
            var exactKill = Monster();
            Assert.Equal(0u, Game.TakeOwnerHitLocked(player, exactKill, 1000, default).Life);
            Assert.Equal(0, exactKill.Overkill);
            var culled = Monster();
            Assert.Equal(0u, Game.TakeOwnerHitLocked(player, culled, 900, cull).Life);
            Assert.Equal(0, culled.Overkill);
            var pacifist = Monster();
            Assert.Equal(1u, Game.TakeOwnerHitLocked(player, pacifist, 5000, new(0, 0, CannotKill: true)).Life);
            Assert.Equal(0, pacifist.Overkill);
            var protectedTarget = Monster();
            protectedTarget.KingGrace = true;
            Assert.Equal(1000u, Game.TakeOwnerHitLocked(player, protectedTarget, 5000, default).Life);
            Assert.Equal(0, protectedTarget.Overkill);
        }

        // Gain per enemy hit comes from attacks and their weapon.
        Assert.Equal((59, 14, 2), LeechSources.From(GainRaw.GetValueOrDefault, attack: true, GainWeapon.GetValueOrDefault).GainOnHit);
        Assert.Equal((44, 3, 2), LeechSources.From(GainRaw.GetValueOrDefault, attack: true).GainOnHit);
        Assert.Equal(default, LeechSources.From(GainRaw.GetValueOrDefault, attack: false, GainWeapon.GetValueOrDefault).GainOnHit);

        // Every enemy hit grants its gain at once.
        {
            var player = new Player(new(1, "gain", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(1, 1000, 100, 2, 0), PlayerLife = 900, PlayerMana = 50,
            };
            var sources = LeechSources.From(GainRaw.GetValueOrDefault, attack: true, GainWeapon.GetValueOrDefault);
            var first = new SpawnedMonster("first", new(100, 100, 0), 0, 0, 100);
            var second = new SpawnedMonster("second", new(100, 100, 0), 0, 0, 100);
            // A hit that deals no damage still hits: the gain does not scale with damage.
            Game.LeechOnHitLocked(player, first, DamagePacket.Empty, 0, sources);
            Game.LeechOnHitLocked(player, second, DamagePacket.Empty, 0, sources);
            Assert.Equal(1000u, player.PlayerLife);   // 900 + 2 × 59, capped at maximum Life
            Assert.Equal(78u, player.PlayerMana);
            Assert.True(Game.LeechOf(player).InstantLifeChanged && Game.LeechOf(player).InstantManaChanged);
            Game.LeechOnHitLocked(player, new("dead", new(100, 100, 0), 0, 0, 100) { Dead = true }, DamagePacket.Empty, 0, sources);
            Assert.Equal(78u, player.PlayerMana);
        }

        // Life Recoup: a share of each hit's damage taken, raised by the Life recovery rate, recovered evenly over the
        // 4 s after the hit (3 s with Nine Lives); overlapping hits keep their own ends.
        {
            static Func<uint, int> Get(params (string Name, int Value)[] stats) =>
                name => stats.FirstOrDefault(s => Stat.Of(s.Name) == name).Value;
            var hit = DamagePacket.Of(DamageType.Fire, 100, 100).Add(DamageType.Physical, new(50, 50));
            const string All = "damage_taken_goes_to_life_over_4_seconds_%";
            Assert.Equal(40, Game.LifeRecoupOf(Get((All, 10), ("fire_damage_taken_goes_to_life_over_4_seconds_%", 20),
                ("physical_damage_taken_recouped_as_life_%", 10)), hit), 9);
            Assert.Equal(0, Game.LifeRecoupOf(Get(), hit), 9);
            Assert.Equal(22.5, Game.LifeRecoupOf(Get((All, 10), ("life_recovery_rate_+%", 50)), hit), 9);
            Assert.Equal(0, Game.LifeRecoupOf(Get((All, 10), ("cannot_recover_life", 1)), hit), 9);
            Assert.Equal(4, Game.LifeRecoupSeconds(Get()));
            Assert.Equal(3, Game.LifeRecoupSeconds(Get(("recoup_effects_apply_over_3_seconds_instead", 1))));
            Assert.Equal(3, Game.LifeRecoupSeconds(Get(("recoup_life_effects_apply_over_3_seconds_instead", 1))));

            var life = new RecoupRecovery();
            life.Add(10, 40, 4);
            life.Add(12, 20, 4);
            Assert.Equal(10, life.Take(11), 9);
            Assert.Equal(25, life.Take(13), 9);
            Assert.Equal(25, life.Take(20), 9);
            Assert.Equal(0, life.Take(21), 9);
            life.Add(30, 8, 4);
            Assert.Equal(0, life.Take(29), 9);
            Assert.Equal(8, life.Take(34), 9);
        }
    }

    // Recovery on kill (with the corpse's ailments) and Onslaught on kill: what each source gives, and the kill that
    // applies it.
    [Fact]
    public void Kills_recover_and_grant_onslaught_by_their_stats()
    {
        // Kill recovery comes from flat, per-level and percent stats, and from the corpse's ailments.
        var raw = new Dictionary<uint, int>
        {
            [Stat.BaseLifeGainedOnEnemyDeath] = 10, [Stat.LifeGainedOnEnemyDeathPerLevel] = 1,
            [Stat.RecoverPctMaximumLifeOnKill] = 3, [Stat.BaseManaGainedOnEnemyDeath] = 4,
            [Stat.RecoverPctMaximumManaOnKill] = 2, [Stat.MaximumManaPctGainedOnKill] = 1,
            [Stat.BaseEnergyShieldGainedOnEnemyDeath] = 7, [Stat.RecoverEnergyShieldPctOnKill] = 5,
            [Stat.LifeGainedOnKillingIgnitedEnemies] = 20, [Stat.RecoverPctMaximumLifeOnKillingChilledEnemy] = 1,
            [Stat.GainXEnergyShieldOnKillingShockedEnemy] = 9, [Stat.RecoverXManaOnKillingFrozenEnemy] = 6,
        };
        var k = KillRecovery.From(raw.GetValueOrDefault, level: 20);
        Assert.Equal(new KillRecovery(30, 3, 4, 3, 7, 5), k.For(false, false, false, false));
        Assert.Equal(new KillRecovery(50, 4, 10, 3, 16, 5), k.For(true, true, true, true));
        Assert.Equal(new KillRecovery(30, 3, 10, 3, 7, 5), k.For(false, false, false, frozen: true));
        raw[Stat.CannotRecoverLife] = 1;
        raw[Stat.CannotRecoverEnergyShield] = 1;
        Assert.Equal(new KillRecovery(0, 0, 10, 3, 0, 0), KillRecovery.From(raw.GetValueOrDefault, level: 20).For(true, true, true, true));

        // A kill recovers at once by the corpse's ailments, and a dead player gains nothing.
        {
            var player = new Player(new(1, "kill", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(10, 1000, 200, 2, 0), PlayerLife = 500, PlayerMana = 50,
            };
            Game.LeechOf(player).OnKill = new KillRecovery(10, 2, 5, 3, 0, 0) { When = new(IgnitedLife: 7, 0, 0, FrozenMana: 4) };
            var corpse = new SpawnedMonster("kill", new(100, 100, 0), 0, 0, 100);
            Game.RecoverOnKill(player, corpse);
            Assert.Equal(530u, player.PlayerLife);   // 10 + 2% of 1000
            Assert.Equal(61u, player.PlayerMana);    // floor(5 + 3% of 200); it was not frozen
            Assert.True(Game.LeechOf(player).InstantLifeChanged && Game.LeechOf(player).InstantManaChanged);
            corpse.Ailments.ShockOnKill(0, _ => 0, _ => 0);   // Within the Tempest shocks the corpse after the death
            Assert.True(corpse.Ailments.Magnitude(MonsterElementalAilments.Kind.Shock, 1) > 0);
            Assert.False(corpse.Ailments.ShockedAtDeath(1));
            corpse.Ailments.Shatter();             // a shattered kill was of a frozen enemy
            corpse.Ignites.Clear(burnt: true);     // and its ignite's last tick killed it
            Game.RecoverOnKill(player, corpse);
            Assert.Equal(567u, player.PlayerLife);   // 530 + 10 + 7 + 2% of 1000
            Assert.Equal(76u, player.PlayerMana);    // floor(5 + 4 + 3% of 200)
            player.PlayerLife = 0;
            Game.RecoverOnKill(player, corpse);
            Assert.Equal(0u, player.PlayerLife);
            Assert.Equal(76u, player.PlayerMana);
        }

        // Each Onslaught-on-kill stat rolls its own duration, and a gain never shortens a longer one.
        {
            var rolls = new Dictionary<uint, int>
            {
                [Stat.ChanceToGainOnslaughtOnKillPct] = 10, [Stat.ChanceToGainOnslaughtOnKillFor10SecondsPct] = 100,
            };
            Assert.Equal([(10, 4.0), (100, 10.0)], KillOnslaught.From(rolls.GetValueOrDefault));
            rolls[Stat.OnslaughtTimeGrantedOnKillMs] = 6000;
            rolls[Stat.OnslaughtBuffDurationOnKillMs] = 8000;       // Maraketh: always, for 8 s
            Assert.Equal([(10, 6.0), (100, 8.0), (100, 10.0)], KillOnslaught.From(rolls.GetValueOrDefault));

            var o = new KillOnslaught { Rolls = [(100, 4.0)] };
            Assert.True(o.Kill(0, new Random(1)));
            Assert.Equal(4, o.Until);
            Assert.True(o.Kill(3, new Random(1)));       // 1 s left: a new 4 s gain lengthens it
            Assert.Equal(7, o.Until);
            o.Rolls = [(100, 4.0)];
            o.Until = 20;
            Assert.False(o.Kill(10, new Random(1)));     // 10 s left: a 4 s gain does not shorten it
            Assert.Equal(20, o.Until);
            Assert.False(new KillOnslaught { Rolls = [] }.Kill(0, new Random(1)));
        }

        // A kill's Onslaught applies once the client is told, and counts once with a flask's.
        {
            var player = new Player(new(1, "onslaught", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(10, 1000, 200, 2, 0), PlayerLife = 500,
            };
            Game.KillOnslaughtOf(player).Rolls = [(100, 4.0)];
            Assert.True(Game.OnslaughtOnKill(player));
            Assert.Empty(Game.KillOnslaughtSources(player, _ => 0));    // not until the client has the buff
            var o = Game.KillOnslaughtOf(player);
            double now = o.Until - 4;                                // granted for 4 s at the kill
            Assert.Equal(((ushort)0, (double?)4, true), o.Present(now, alive: true, hidden: false));
            o.Buff = 9;
            Assert.Null(o.Present(now + 1, alive: true, hidden: false));               // nothing new to send
            Assert.Equal(((ushort)9, (double?)null, false), o.Present(now + 1, alive: true, hidden: true)); // a flask's shows
            var kill = Game.KillOnslaughtSources(player, name => name == Stat.OnslaughtEffectPlusPct ? 50 : 0).ToList();
            Assert.Equal(["attack_speed_+%", "base_cast_speed_+%", "base_movement_velocity_+%"], kill.Select(s => Program.Stats.Name(s.Stat)));
            Assert.All(kill, s => Assert.Equal(30, s.Value));      // 20% at 50% increased effect
            // A Silver Flask's Onslaught at 80% more flask effect is the same buff: the stronger counts, once.
            var flask = UtilityFlasks.OnslaughtSources(Program.Items.Onslaught!, 1.8);
            Assert.All(UtilityFlasks.Strongest(kill.Concat(flask)), s => Assert.Equal(36, s.Value));
            Assert.Equal(3, UtilityFlasks.Strongest(kill.Concat(flask)).Count);
            Assert.Equal(((ushort)0, (double?)null, true), o.Present(now + 2, alive: false, hidden: true)); // death ends it
            Assert.False(o.Applied);
            Assert.Empty(Game.KillOnslaughtSources(player, _ => 0));
        }
    }
}
