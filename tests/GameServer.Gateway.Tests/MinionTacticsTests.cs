using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MinionTacticsTests
{
    sealed class Clock : IPlayerUpdateSchedule
    {
        internal double Time;
        public double Now(ClientState conn) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping)
        { stopping.ThrowIfCancellationRequested(); Time += milliseconds / 1000d; return Task.CompletedTask; }
    }
    sealed class ZeroRandom : Random { public override int Next(int maxValue) => 0; }
    static object? Invoke(string name, params object[] args) =>
        typeof(Game).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    static SpawnedMonster Enemy(MinionUtilityTests.Fixture f, uint id, int x) => f.Conn.Monsters[id] =
        new("target", new(x, 100, 0), 100, 3, 1000000) { Hostile = true };

    static ushort Source(MinionUtilityTests.Fixture f, string effect) =>
        Game.GemSkillEffects(f.Conn).Single(g => Program.Skills.EffectId(g.Effect) == effect).Id;
    static void Link(MinionUtilityTests.Fixture f, params byte[] links)
    {
        var item = Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single();
        Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1, item.Id, i => i with { Links = links });
    }
    static Task<uint> Skeleton(MinionUtilityTests.Fixture f) =>
        Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, Source(f, "SummonSkeletons"),
            Game.SummonData.Find("SummonSkeletons")!, (100, 100));
    static bool Hit(MinionUtilityTests.Fixture f, GolemState.Golem fighter, uint id, SpawnedMonster target, double now)
    {
        Invoke("RefreshGolemLocked", f.Conn, fighter, Game.CharacterPlayerStats(f.Conn), (double?)now);
        var a = fighter.Abilities.First() with { Attack = new(0x4000, 1, 20,
            DamagePacket.Of(DamageType.Physical, 100, 100)) { Spell = true }, Crit = 0 };
        fighter.Abilities = [a];
        return (bool)Invoke("GolemHitLocked", "test", f.Stream, f.Ciphers, f.Conn, fighter, a, id, target, now)!;
    }

    [Fact]
    public async Task Eviscerating_reaper_follows_until_the_owner_requests_its_dash()
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time = 1 };
        var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            ushort source = f.Equip(("Summon Reaper of Eviscerating", 20)).Single();
            var definition = Game.SummonData.Find("SummonReaperAltY")!;
            uint id = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, source, definition, (100, 100));
            var reaper = f.Conn.Summons.Fighters[id];
            f.Conn.Walk.Teleport(100, 100, 0);
            reaper.Monster.Walk.Teleport(100, 100, 0);
            var target = Enemy(f, 900, 105);

            Game.GolemStepLocked("test", f.Stream, f.Ciphers, f.Conn, reaper, clock.Time);
            Assert.Null(reaper.Pending);
            Assert.Equal(0u, reaper.Target);
            Assert.True(await Game.CastSummon("test", f.Stream, f.Ciphers, f.Conn, source, definition,
                (105, 100), 900, f.Conn.ActionInterruptionGeneration, () => true));
            Assert.Same(reaper, Assert.Single(f.Conn.Summons.Fighters).Value);
            var command = Assert.IsType<GolemState.Action>(reaper.Pending);
            Assert.Equal(10584, command.Ability.Effect);
            Assert.Same(target, command.Victim);
        }
        finally { Game.AreaUpdateContext.Value = outer; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Meat_shield_taunts_after_an_accepted_hit_and_drops_the_roll_when_unlinked(bool lockstep)
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time = 1 };
        var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            f.Conn.LockstepClient = lockstep;
            f.Equip(("Summon Skeletons", 20), ("Meat Shield Support", 20)); Link(f, 2);
            var fighter = f.Conn.Summons.Fighters[await Skeleton(f)];
            f.Conn.Minions.Random = new ZeroRandom();
            var near = Enemy(f, 900, 105);
            Assert.True(Hit(f, fighter, 900, near, clock.Time));
            Assert.Equal(20, fighter.Own[Program.Stats.Key("minion_chance_to_taunt_on_hit_%")!.Value]);
            Assert.Equal(4d, fighter.Taunts[900]);
            var targetAt = near.Walk.Position(clock.Time);
            near.Attack = new(0x3188, 2, 8, DamagePacket.Empty);
            var peer = new ClientState(f.Conn.Layout, 2, 1)
                { Instance = f.Conn.Instance, PlayerLife = 1000, LockstepClient = lockstep };
            peer.Walk.Teleport(targetAt.X + 1, targetAt.Y, 0);
            f.Conn.Walk.Teleport(targetAt.X + 10, targetAt.Y, 0);
            peer.MonsterViews[900] = f.Conn.MonsterViews[900] = new(targetAt, targetAt, near.Life);
            near.Target = peer;
            Assert.True(Game.MonsterTargetsLocked(f.Conn, 900, near, targetAt, 10, clock.Time));
            Assert.Same(f.Conn, near.Target);
            Assert.True((bool)Invoke("GolemEnemyOrderLocked", f.Conn, 900u, near, clock.Time, 10d)!);
            Assert.Same(fighter, Assert.Single(f.Conn.Golems.IncomingHits).Target);
            // Even a distant summoner retains the monster while their taunting skeleton is in reach.
            f.Conn.Walk.Teleport(targetAt.X + 40, targetAt.Y, 0);
            fighter.Monster.Walk.Teleport(targetAt.X + 25, targetAt.Y, 0);
            Assert.False(Game.MonsterTargetsLocked(peer, 900, near, targetAt, 1, clock.Time));

            Link(f, 1, 1);
            clock.Time = 2;
            Assert.True(Hit(f, fighter, 900, near, clock.Time));
            Assert.Equal(4d, fighter.Taunts[900]);
            clock.Time = 4;
            Assert.True(Game.MonsterTargetsLocked(peer, 900, near, targetAt, 1, clock.Time));
            Assert.Same(peer, near.Target);
            Assert.True(double.IsFinite(near.BusyUntil));
        }
        finally { Game.AreaUpdateContext.Value = outer; }
    }

    [Fact]
    public async Task Predator_grants_only_its_active_companion_and_prioritizes_one_living_prey_until_expiry_or_source_loss()
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time = 1 }; var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            f.Equip(("Summon Skeletons", 20), ("Predator Support", 20)); Link(f, 2);
            Assert.Equal(new[] { "SummonSkeletons", "SignalPrey" }, Game.GemSkillEffects(f.Conn)
                .Select(g => Program.Skills.EffectId(g.Effect)));
            var fighter = f.Conn.Summons.Fighters[await Skeleton(f)];
            f.Conn.Walk.Teleport(100, 100, 0); fighter.Monster.Walk.Teleport(100, 100, 0);
            ushort signal = Source(f, "SignalPrey"); var gem = Game.SupportedSkill(f.Conn, signal);
            Assert.Equal(800, Program.Skills.CooldownMs(gem.Effect, gem.Level));
            Assert.Equal(8000, gem.Stats[Program.Stats.Key("base_skill_effect_duration")!.Value]);
            var near = Enemy(f, 900, 105); var prey = Enemy(f, 901, 110);
            uint before = near.Life;
            Assert.True(Hit(f, fighter, 900, near, clock.Time));
            Assert.Equal(112u, before - near.Life);
            Assert.True(Game.SignalPreyLocked(f.Conn, signal, gem, (110, 100), 901, clock.Time));
            before = prey.Life;
            Assert.True(Hit(f, fighter, 901, prey, clock.Time));
            Assert.InRange(before - prey.Life, 138u, 139u);
            uint focusKey = Program.Stats.Key("support_minion_use_focussed_target")!.Value;
            int focus = fighter.Own[focusKey]; fighter.Own.Remove(focusKey);
            before = prey.Life; Assert.True(Hit(f, fighter, 901, prey, clock.Time));
            Assert.Equal(112u, before - prey.Life);
            fighter.Own[focusKey] = focus;
            Game.GolemStepLocked("test", f.Stream, f.Ciphers, f.Conn, fighter, clock.Time);
            Assert.Equal(901u, fighter.Target);
            Assert.Contains(f.Conn.Minions.OutputQueue, p => p.Log == "Signal Prey" && p.TargetId == 901);
            ushort old = f.Conn.Summons.SignalledPrey!.Buff;
            Assert.True(Game.SignalPreyLocked(f.Conn, signal, gem, (105, 100), 900, clock.Time));
            Assert.NotEqual(old, f.Conn.Summons.SignalledPrey!.Buff);
            Assert.Contains(f.Conn.Minions.OutputQueue, p => p.Log == "Signal Prey ended" && p.TargetId == 901);
            clock.Time = 9;
            Game.MinionUtilityTick(f.Conn, clock.Time); Assert.Null(f.Conn.Summons.SignalledPrey);
            Assert.True(Game.SignalPreyLocked(f.Conn, signal, gem, (110, 100), 901, clock.Time));
            prey.Dead = true; prey.Life = 0; Game.MinionUtilityTick(f.Conn, clock.Time);
            Assert.Null(f.Conn.Summons.SignalledPrey);
            Assert.False(Game.SignalPreyLocked(f.Conn, signal, gem, (200, 200), 901, clock.Time));
            Assert.True(Game.SignalPreyLocked(f.Conn, signal, gem, (105, 100), 900, clock.Time));
            Enemy(f, 900, 105); f.Conn.Minions.OutputQueue.Clear();
            Game.MinionUtilityTick(f.Conn, clock.Time); Assert.Null(f.Conn.Summons.SignalledPrey);
            Assert.DoesNotContain(f.Conn.Minions.OutputQueue, p => p.Log == "Signal Prey ended");
            Assert.True(Game.SignalPreyLocked(f.Conn, signal, gem, (105, 100), 900, clock.Time));
            f.Equip(("Summon Skeletons", 20)); Game.MinionUtilityTick(f.Conn, clock.Time);
            Assert.Null(f.Conn.Summons.SignalledPrey);
        }
        finally { Game.AreaUpdateContext.Value = outer; }
    }

    [Fact]
    public async Task Feeding_frenzy_procs_on_supported_hits_shares_stats_and_refreshes_without_stacking_then_expires()
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time = 1 }; var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            f.Equip(("Summon Skeletons", 20), ("Feeding Frenzy Support", 20), ("Summon Holy Relic", 20)); Link(f, 2, 1);
            var fighter = f.Conn.Summons.Fighters[await Skeleton(f)];
            ushort relicSource = Source(f, "SummonHolyRelic");
            uint relicId = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, relicSource,
                Game.SummonData.Find("SummonHolyRelic")!, (100, 100));
            var relic = f.Conn.Summons.Fighters[relicId];
            var target = Enemy(f, 900, 105);
            f.Conn.Minions.Random = new ZeroRandom();
            Assert.True(Hit(f, relic, 900, target, clock.Time)); Assert.Equal(0, f.Conn.Summons.FeedingFrenzyBuff);
            Assert.True(Hit(f, fighter, 900, target, clock.Time));
            Assert.Equal(6.9, f.Conn.Summons.FeedingFrenzyUntil, 5);
            ushort buff = f.Conn.Summons.FeedingFrenzyBuff; Assert.NotEqual(0, buff);
            int Stat(string name) => Game.CharacterPlayerStats(f.Conn).GetValueOrDefault(Program.Stats.Key(name)!.Value);
            Assert.Equal(1, Stat("has_puppet_master"));
            Assert.Equal(10, relic.Monster.Mods.Effects["attack_speed_+%"]);
            Assert.Equal(10, relic.Monster.Mods.Effects["cast_speed_+%"]);
            Assert.Equal(10, relic.Monster.Mods.Effects["movement_speed_+%"]);
            uint before = target.Life; Assert.True(Hit(f, relic, 900, target, clock.Time));
            Assert.InRange(before - target.Life, 110u, 111u);
            clock.Time = 2; Game.FeedingFrenzyOnHitLocked(f.Conn, fighter, clock.Time);
            Assert.NotEqual(buff, f.Conn.Summons.FeedingFrenzyBuff);
            Assert.Equal(7.9, f.Conn.Summons.FeedingFrenzyUntil, 5);
            Assert.Equal(10, relic.Monster.Mods.Effects["attack_speed_+%"]);
            clock.Time = 7.9; Game.MinionUtilityTick(f.Conn, clock.Time);
            Assert.Equal(0, f.Conn.Summons.FeedingFrenzyBuff); Assert.Equal(0, Stat("has_puppet_master"));
            Assert.Equal(0, relic.Monster.Mods.Effects["attack_speed_+%"]);
            Assert.Contains(f.Conn.Minions.OutputQueue, p => p.Log == "Feeding Frenzy ended");
            before = target.Life; Assert.True(Hit(f, relic, 900, target, clock.Time)); Assert.Equal(100u, before - target.Life);
            Game.FeedingFrenzyOnHitLocked(f.Conn, fighter, clock.Time);
            f.Conn.PlayerLife = 0; Game.MinionUtilityTick(f.Conn, clock.Time);
            Assert.Equal(0, f.Conn.Summons.FeedingFrenzyBuff);
        }
        finally { Game.AreaUpdateContext.Value = outer; }
    }
}
