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
            Assert.True(Hit(f, relic, 900, target, clock.Time)); Assert.Equal(0, f.Player.Summons.FeedingFrenzyBuff);
            Assert.True(Hit(f, fighter, 900, target, clock.Time));
            Assert.Equal(6.9, f.Player.Summons.FeedingFrenzyUntil, 5);
            ushort buff = f.Player.Summons.FeedingFrenzyBuff; Assert.NotEqual(0, buff);
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
            Assert.Equal(0, f.Player.Summons.FeedingFrenzyBuff); Assert.Equal(0, Stat(global::Stat.HasPuppetMaster));
            Assert.Equal(0, relic.Monster.Mods.Effects[global::Stat.AttackSpeedPlusPct]);
            Assert.False(f.Player.Area.Buffs.Has(f.Player.Id, 1372, f.Player.Id));
            before = target.Life; Assert.True(Hit(f, relic, 900, target, clock.Time)); Assert.Equal(100u, before - target.Life);
            Game.FeedingFrenzyOnHitLocked(f.Player, fighter, clock.Time);
            f.Player.PlayerLife = 0; Game.MinionUtilityTick(f.Player, clock.Time);
            Assert.Equal(0, f.Player.Summons.FeedingFrenzyBuff);
        }
        finally { Game.AreaUpdateContext.Value = outer; }
    }
}
