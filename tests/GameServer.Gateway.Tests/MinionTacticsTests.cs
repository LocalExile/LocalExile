using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MinionTacticsTests
{
    sealed class Clock : IPlayerUpdateSchedule
    {
        internal double Time;
        public double Now(Player player) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping)
        { stopping.ThrowIfCancellationRequested(); Time += milliseconds / 1000d; return Task.CompletedTask; }
    }
    sealed class ZeroRandom : Random { public override int Next(int maxValue) => 0; }
    static object? Invoke(string name, params object[] args) =>
        typeof(Game).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    static SpawnedMonster Enemy(MinionUtilityTests.Fixture f, uint id, int x) => f.Player.Area.Monsters[id] =
        new("target", new(x, 100, 0), 100, 3, 1000000) { Hostile = true };

    static ushort Source(MinionUtilityTests.Fixture f, string effect) =>
        Game.GemSkillEffects(f.Player).Single(g => Program.Skills.EffectId(g.Effect) == effect).Id;
    static void Link(MinionUtilityTests.Fixture f, params byte[] links)
    {
        var item = Game.ItemsOf(f.Player).Items(PlayerInventories.Weapon1).Single();
        Game.ItemsOf(f.Player).Change(PlayerInventories.Weapon1, item.Id, i => i with { Links = links });
    }
    static Task<uint> Skeleton(MinionUtilityTests.Fixture f) =>
        Game.SummonFighter(f.Player, Source(f, "SummonSkeletons"),
            Game.SummonData.Find("SummonSkeletons")!, (100, 100));
    static bool Hit(MinionUtilityTests.Fixture f, GolemState.Golem fighter, uint id, SpawnedMonster target, double now)
    {
        Invoke("RefreshGolemLocked", f.Player, fighter, Game.CharacterPlayerStats(f.Player), (double?)now);
        var a = fighter.Abilities.First() with { Attack = new(0x4000, 1, 20,
            DamagePacket.Of(DamageType.Physical, 100, 100)) { Spell = true }, Crit = 0 };
        fighter.Abilities = [a];
        return (bool)Invoke("GolemHitLocked", f.Player, fighter, a, id, target, now)!;
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
            uint id = await Game.SummonFighter(f.Player, source, definition, (100, 100));
            var reaper = f.Player.Summons.Fighters[id];
            f.Player.Walk.Teleport(100, 100, 0);
            reaper.Monster.Walk.Teleport(100, 100, 0);
            var target = Enemy(f, 900, 105);

            Game.GolemStepLocked(f.Player, reaper, clock.Time);
            Assert.Null(reaper.Pending);
            Assert.Equal(0u, reaper.Target);
            Assert.True(await Game.CastSummon(f.Player, source, definition,
                (105, 100), 900, f.Player.ActionInterruptionGeneration, () => true));
            Assert.Same(reaper, Assert.Single(f.Player.Summons.Fighters).Value);
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
            f.Player.Connection.LockstepClient = lockstep;
            f.Equip(("Summon Skeletons", 20), ("Meat Shield Support", 20)); Link(f, 2);
            var fighter = f.Player.Summons.Fighters[await Skeleton(f)];
            f.Player.Minions.Random = new ZeroRandom();
            var near = Enemy(f, 900, 105);
            Assert.True(Hit(f, fighter, 900, near, clock.Time));
            Assert.Equal(20, fighter.Own[Program.Stats.Key("minion_chance_to_taunt_on_hit_%")!.Value]);
            Assert.Equal(4d, fighter.Taunts[900]);
            var targetAt = near.Walk.Position(clock.Time);
            near.Attack = new(0x3188, 2, 8, DamagePacket.Empty);
            var peer = new Player(f.Player.Layout, 2, 1)
                { Instance = f.Player.Instance, PlayerLife = 1000, Connection = { LockstepClient = lockstep } };
            peer.Walk.Teleport(targetAt.X + 1, targetAt.Y, 0);
            f.Player.Walk.Teleport(targetAt.X + 10, targetAt.Y, 0);
            near.Target = peer;
            Assert.True(Game.MonsterTargetsLocked(f.Player, 900, near, targetAt, 10, clock.Time));
            Assert.Same(f.Player, near.Target);
            Assert.True((bool)Invoke("GolemEnemyOrderLocked", f.Player, 900u, near, clock.Time, 10d)!);
            Assert.Same(fighter, Assert.Single(f.Player.Golems.IncomingHits).Target);
            // Even a distant summoner retains the monster while their taunting skeleton is in reach.
            f.Player.Walk.Teleport(targetAt.X + 40, targetAt.Y, 0);
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
            Assert.Equal(new[] { "SummonSkeletons", "SignalPrey" }, Game.GemSkillEffects(f.Player)
                .Select(g => Program.Skills.EffectId(g.Effect)));
            var fighter = f.Player.Summons.Fighters[await Skeleton(f)];
            f.Player.Walk.Teleport(100, 100, 0); fighter.Monster.Walk.Teleport(100, 100, 0);
            ushort signal = Source(f, "SignalPrey"); var gem = Game.SupportedSkill(f.Player, signal);
            Assert.Equal(800, Program.Skills.CooldownMs(gem.Effect, gem.Level));
            Assert.Equal(8000, gem.Stats[Stat.BaseSkillEffectDuration]);
            var near = Enemy(f, 900, 105); var prey = Enemy(f, 901, 110);
            uint before = near.Life;
            Assert.True(Hit(f, fighter, 900, near, clock.Time));
            Assert.Equal(112u, before - near.Life);
            Assert.True(Game.SignalPreyLocked(f.Player, signal, gem, (110, 100), 901, clock.Time));
            before = prey.Life;
            Assert.True(Hit(f, fighter, 901, prey, clock.Time));
            Assert.InRange(before - prey.Life, 138u, 139u);
            uint focusKey = Stat.SupportMinionUseFocussedTarget;
            int focus = fighter.Own[focusKey]; fighter.Own.Remove(focusKey);
            before = prey.Life; Assert.True(Hit(f, fighter, 901, prey, clock.Time));
            Assert.Equal(112u, before - prey.Life);
            fighter.Own[focusKey] = focus;
            Game.GolemStepLocked(f.Player, fighter, clock.Time);
            Assert.Equal(901u, fighter.Target);
            Assert.True(f.Player.Area.Buffs.Has(901, 1376, f.Player.Id));
            Assert.True(Game.SignalPreyLocked(f.Player, signal, gem, (105, 100), 900, clock.Time));
            Assert.True(f.Player.Area.Buffs.Has(900, 1376, f.Player.Id));
            Assert.False(f.Player.Area.Buffs.Has(901, 1376, f.Player.Id));
            clock.Time = 9;
            Game.MinionUtilityTick(f.Player, clock.Time); Assert.Null(f.Player.Summons.SignalledPrey);
            Assert.True(Game.SignalPreyLocked(f.Player, signal, gem, (110, 100), 901, clock.Time));
            prey.Dead = true; prey.Life = 0; Game.MinionUtilityTick(f.Player, clock.Time);
            Assert.Null(f.Player.Summons.SignalledPrey);
            Assert.False(Game.SignalPreyLocked(f.Player, signal, gem, (200, 200), 901, clock.Time));
            Assert.True(Game.SignalPreyLocked(f.Player, signal, gem, (105, 100), 900, clock.Time));
            Enemy(f, 900, 105); f.Player.Minions.OutputQueue.Clear();
            Game.MinionUtilityTick(f.Player, clock.Time); Assert.Null(f.Player.Summons.SignalledPrey);
            Assert.True(Game.SignalPreyLocked(f.Player, signal, gem, (105, 100), 900, clock.Time));
            f.Equip(("Summon Skeletons", 20)); Game.MinionUtilityTick(f.Player, clock.Time);
            Assert.Null(f.Player.Summons.SignalledPrey);
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
            var fighter = f.Player.Summons.Fighters[await Skeleton(f)];
            ushort relicSource = Source(f, "SummonHolyRelic");
            uint relicId = await Game.SummonFighter(f.Player, relicSource,
                Game.SummonData.Find("SummonHolyRelic")!, (100, 100));
            var relic = f.Player.Summons.Fighters[relicId];
            var target = Enemy(f, 900, 105);
            f.Player.Minions.Random = new ZeroRandom();
            Assert.True(Hit(f, relic, 900, target, clock.Time)); Assert.False(f.Player.Area.Buffs.HasRow(f.Player.Id, 1372));
            Assert.True(Hit(f, fighter, 900, target, clock.Time));
            Assert.Equal(6.9, f.Player.Summons.FeedingFrenzyUntil, 5);
            Assert.True(f.Player.Area.Buffs.HasRow(f.Player.Id, 1372));
            int Stat(uint name) => Game.CharacterPlayerStats(f.Player).GetValueOrDefault(name);
            Assert.Equal(1, Stat(global::Stat.HasPuppetMaster));
            Assert.Equal(10, relic.Monster.Mods.Effects[global::Stat.AttackSpeedPlusPct]);
            Assert.Equal(10, relic.Monster.Mods.Effects[global::Stat.CastSpeedPlusPct]);
            Assert.Equal(10, relic.Monster.Mods.Effects[global::Stat.Of("movement_speed_+%")]);
            uint before = target.Life; Assert.True(Hit(f, relic, 900, target, clock.Time));
            Assert.InRange(before - target.Life, 110u, 111u);
            clock.Time = 2; Game.FeedingFrenzyOnHitLocked(f.Player, fighter, clock.Time);
            Assert.True(f.Player.Area.Buffs.Has(f.Player.Id, 1372, f.Player.Id));
            Assert.Equal(7.9, f.Player.Summons.FeedingFrenzyUntil, 5);
            Assert.Equal(10, relic.Monster.Mods.Effects[global::Stat.AttackSpeedPlusPct]);
            clock.Time = 7.9; Game.MinionUtilityTick(f.Player, clock.Time);
            Assert.False(f.Player.Area.Buffs.HasRow(f.Player.Id, 1372)); Assert.Equal(0, Stat(global::Stat.HasPuppetMaster));
            Assert.Equal(0, relic.Monster.Mods.Effects[global::Stat.AttackSpeedPlusPct]);
            Assert.False(f.Player.Area.Buffs.Has(f.Player.Id, 1372, f.Player.Id));
            before = target.Life; Assert.True(Hit(f, relic, 900, target, clock.Time)); Assert.Equal(100u, before - target.Life);
            Game.FeedingFrenzyOnHitLocked(f.Player, fighter, clock.Time);
            f.Player.PlayerLife = 0; Game.MinionUtilityTick(f.Player, clock.Time);
            Assert.False(f.Player.Area.Buffs.HasRow(f.Player.Id, 1372));
        }
        finally { Game.AreaUpdateContext.Value = outer; }
    }

    [Fact]
    public async Task Minion_stances_change_skeleton_targeting_and_cancel_each_other()
    {
        foreach (var (feeding, meatShield, target, dash) in new[]
            { (false, false, 900u, false), (true, false, 900u, true), (false, true, 0u, false), (true, true, 900u, false) })
        {
            using var f = new MinionUtilityTests.Fixture();
            var gems = new List<(string, int)> { ("Summon Skeletons", 20) };
            if (feeding) gems.Add(("Feeding Frenzy Support", 20));
            if (meatShield) gems.Add(("Meat Shield Support", 20));
            f.Equip([.. gems]); Link(f, (byte)gems.Count);
            var fighter = f.Player.Summons.Fighters[await Skeleton(f)];
            f.Player.Walk.Teleport(100, 100, 0);
            fighter.Monster.Walk.Teleport(100, 100, 0);
            var monster = Enemy(f, 900, 143);
            Game.GolemStepLocked(f.Player, fighter, 1);
            Assert.Equal(target, fighter.Target);
            Assert.Equal(dash, fighter.Pending?.Ability.Effect == 10844);
            if (dash)
            {
                var start = f.Player.Minions.OutputQueue.Last(o => o.Kind == PlayerMinions.OutputKind.GolemAction);
                var records = (byte[][])Invoke("GolemActionRecords", f.Player, start)!;
                var movement = Assert.Single(records);
                Assert.Equal(0x2909, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(movement.AsSpan(^4)));
                var action = fighter.Pending!;
                uint life = monster.Life;
                Game.GolemAbilityContactLocked(f.Player, fighter, action, 1);
                Assert.Equal(action.Aim, fighter.Monster.Walk.Position(1));
                Assert.Equal(life, monster.Life);
            }

            // Chaos Golems have a 50-cell native scan range. Feeding Frenzy raises it to 80.
            using var golems = new MinionUtilityTests.Fixture();
            gems[0] = ("Summon Chaos Golem", 20);
            golems.Equip([.. gems]); Link(golems, (byte)gems.Count);
            var definition = Game.GolemData.Find("SummonChaosGolem")!;
            var id = await Game.SummonGolem(golems.Player,
                Source(golems, definition.Id), definition, (100, 100));
            var golem = golems.Player.Golems.All[id];
            golems.Player.Walk.Teleport(100, 100, 0);
            golem.Monster.Walk.Teleport(100, 100, 0);
            Enemy(golems, 900, 180);
            Game.GolemStepLocked(golems.Player, golem, 1);
            Assert.Equal(feeding && !meatShield ? 900u : 0u, golem.Target);
        }
    }

    [Fact]
    public async Task Meat_shield_bonus_depends_on_distance_to_the_owner()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Summon Skeletons", 20), ("Meat Shield Support", 20)); Link(f, 2);
        var fighter = f.Player.Summons.Fighters[await Skeleton(f)];
        f.Player.Walk.Teleport(100, 100, 0);
        fighter.Monster.Walk.Teleport(145, 100, 0);
        var near = Enemy(f, 900, 105);
        var far = Enemy(f, 901, 145);
        Assert.True(Hit(f, fighter, 900, near, 1));
        Assert.True(Hit(f, fighter, 901, far, 1));
        Assert.Equal(130u, near.MaxLife - near.Life);
        Assert.Equal(100u, far.MaxLife - far.Life);

        fighter.Hazards.Add(new(4336, (125, 100), 2, 25, 100, DamageType.Chaos) { LastAt = 1 });
        Game.GolemHazardsLocked(f.Player, fighter, 2);
        Assert.Equal(260u, near.MaxLife - near.Life);
        Assert.Equal(200u, far.MaxLife - far.Life);
    }
}
