using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class IceCrashContactTests
{
    [Fact]
    public async Task Regular_crash_samples_the_later_rings_and_hits_each_enemy_once()
    {
        using var trial = new Trial("Ice Crash");
        var inner = trial.Target(901, 104);
        var middle = trial.Target(902, 115);
        var outer = trial.Target(903, 126);
        var enters = trial.Target(904, 136);
        var outside = trial.Target(905, 136);
        var hits = new List<(uint Id, double At)>();
        await trial.Hit(0x8002, id =>
        {
            hits.Add((id, trial.Clock.At));
            if (id == inner.ObjectId) trial.Move(inner, 128);
            if (id == middle.ObjectId) trial.Move(enters, 126);
        });
        Console.WriteLine($"[Ice Crash] initial contacts: {string.Join(", ", hits.Select(hit => hit.Id))}");
        Assert.Equal([inner.ObjectId, middle.ObjectId, outer.ObjectId, enters.ObjectId], hits.Select(hit => hit.Id));
        Assert.InRange(hits[1].At - hits[0].At, .39, .42);
        Assert.InRange(hits[2].At - hits[0].At, .79, .82);
        Assert.InRange((double)Damage(middle) / Damage(inner), .84, .86);
        Assert.InRange((double)Damage(outer) / Damage(inner), .69, .71);
        Assert.Equal(0u, Damage(outside));
    }

    [Fact]
    public async Task Cadence_uses_the_swinging_hands_radius_and_requires_two_eligible_weapons()
    {
        using var trial = new Trial("Ice Crash of Cadence");
        var offhand = trial.EquipOffhand("Driftwood Club");
        Assert.Null(Game.WeaponRestricted(trial.Player, trial.Effect));
        var inner = trial.Target(901, 104);
        var outer = trial.Target(902, 126);
        var outside = trial.Target(903, 136);
        await trial.Hit(0x8002);
        uint mainDamage = Damage(inner);
        Assert.True(mainDamage > 0);
        Assert.Equal(0u, Damage(outer));

        await trial.Hit(0x8003);
        uint offDamage = Damage(inner) - mainDamage;
        Console.WriteLine($"[Ice Crash of Cadence] main={mainDamage}, off={offDamage}, off outer={Damage(outer)}");
        Assert.True(Damage(outer) > 0);
        Assert.Equal(0u, Damage(outside));
        Assert.InRange((double)mainDamage / offDamage, 1.78, 1.82);
        Assert.InRange((double)Damage(outer) / offDamage, .99, 1.01);

        Game.ItemsOf(trial.Player).Remove(PlayerInventories.Offhand1, offhand);
        trial.Player.PlayerMana = 100;
        uint mana = trial.Player.PlayerMana;
        Assert.NotNull(Game.WeaponRestricted(trial.Player, trial.Effect));
        Assert.False(await Game.CastGemSkill(trial.Player, trial.SkillId, Trial.Origin, Trial.Impact));
        Assert.Equal(mana, trial.Player.PlayerMana);
        trial.EquipOffhand("Glass Shank");
        Assert.NotNull(Game.WeaponRestricted(trial.Player, trial.Effect));
        Assert.False(await Game.CastGemSkill(trial.Player, trial.SkillId, Trial.Origin, Trial.Impact));
        Assert.Equal(mana, trial.Player.PlayerMana);
    }

    static uint Damage(SpawnedMonster monster) => monster.MaxLife - monster.Life;

    sealed class Trial : IDisposable
    {
        internal static readonly (int X, int Y) Origin = (100, 100), Impact = (110, 100);
        internal readonly ViperStrikeMambaTests.AttackClock Clock = new();
        readonly TriggeredSummonTests.Fixture fixture;
        readonly IDisposable stats;
        readonly PlayerUpdatesState? previous;
        readonly (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem;
        public Player Player => fixture.Player;
        public ushort SkillId => gem.Id;
        public int Effect => gem.Effect;

        public Trial(string name)
        {
            fixture = new(Clock);
            previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, Clock);
            Player.Use(new(1, "ice-crash-test", 400, 400, Origin.X, Origin.Y, (_, _) => true, []) { Sight = (_, _) => 2 });
            Player.Walk.Teleport(Origin.X, Origin.Y, Clock.At);
            Player.ProjectileRuntimeState = fixture.Session;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(Clock.At));

            var items = Game.ItemsOf(Player);
            var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
            items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
            ushort id = Assert.Single(fixture.Equip((name, 1)));
            gem = Game.GemSkillEffects(Player).Single(item => item.Id == id);
            stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, Player))
                {
                    values[Stat.GlobalCannotCrit] = values[Stat.GlobalAlwaysHit] = 1;
                    values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 10_000;
                }
                return values;
            });
        }

        public uint EquipOffhand(string name) => Game.ItemsOf(Player)
            .Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find(name)!)).Id;

        public SpawnedMonster Target(uint id, int y)
        {
            var monster = new SpawnedMonster("Ice Crash witness", new(Impact.X, y, 0), 0, 0, 1_000_000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            Player.Area.Monsters[id] = monster;
            Move(monster, y);
            return monster;
        }

        public void Move(SpawnedMonster monster, int y)
        {
            monster.Walk.Teleport(Impact.X, y, Clock.At);
            Player.Area.Grid.Set(new(monster.ObjectId), (Impact.X, y));
        }

        public Task Hit(ushort sequence, Action<uint>? onHit = null) =>
            Game.AreaSkillHit(Player, gem.Id, gem, Origin, Impact, sequence: sequence, onHit: onHit);

        public void Dispose()
        {
            stats.Dispose();
            Game.AreaUpdateContext.Value = previous;
            fixture.Dispose();
        }
    }
}
