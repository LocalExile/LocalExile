using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class TectonicSlamMechanicsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Regular_slam_consumes_on_the_third_contact_including_repeats(bool multistrike)
    {
        using var trial = new Trial("Tectonic Slam", multistrike);
        trial.SetCharges(3);
        var target = trial.Target(901, (112, 100));
        if (multistrike)
        {
            Assert.True(Game.MeleeAttackRepeats(trial.Player, trial.SkillId) >= 2);
            Assert.True(await trial.Attack());
            Assert.Equal(2, trial.Charges);
        }
        else
        {
            Assert.True(await trial.Attack());
            Assert.Equal(3, trial.Charges);
            Assert.True(await trial.Attack());
            Assert.Equal(3, trial.Charges);
            trial.Player.PlayerMana = 0;
            Assert.False(await trial.Attack());
            Assert.Equal(3, trial.Charges);
            Assert.True(await trial.Attack());
            Assert.Equal(2, trial.Charges);
        }
        Assert.True(target.Life < target.MaxLife);
    }

    [Fact]
    public async Task Cataclysm_scales_with_the_charges_removed_and_preserves_the_minimum()
    {
        using var trial = new Trial("Tectonic Slam of Cataclysm", minimum: 1);
        trial.SetCharges(3);
        var target = trial.Target(901, (112, 100));
        Assert.True(await trial.Attack());
        Assert.Equal(1, trial.Charges);
        uint first = target.MaxLife - target.Life;
        Assert.True(await trial.Attack());
        uint second = target.MaxLife - target.Life - first;
        Assert.Equal(1, trial.Charges);
        Assert.True(second > 0);
        Assert.InRange(first / (double)second, 1.28, 1.32);
    }

    [Fact]
    public async Task Regular_fissure_hits_downrange_and_misses_the_enemies_behind_the_cast()
    {
        using var trial = new Trial("Tectonic Slam");
        var near = trial.Target(901, (108, 100));
        var downrange = trial.Target(902, (125, 100));
        var behind = trial.Target(903, (91, 100));
        Assert.True(await trial.Attack());
        Console.WriteLine($"[Tectonic geometry] near={near.MaxLife - near.Life}, forward={downrange.MaxLife - downrange.Life}, behind={behind.MaxLife - behind.Life}");
        Assert.True(near.Life < near.MaxLife);
        Assert.True(downrange.Life < downrange.MaxLife);
        Assert.Equal(behind.MaxLife, behind.Life);
    }

    [Fact]
    public void Branch_plan_matches_the_native_action_seed()
    {
        var paths = Game.TectonicFissures.Plan((336, 1325), (320, 1325), 32770, 536870916, 0, 30, false,
            area => MathF.Floor(100 * MathF.Sqrt(1 + area / 100f)) / 100);
        foreach (var path in paths)
            Console.WriteLine($"[Tectonic native seed] {path.Level}/{path.Detail} at {path.Starts:0.000}: {path.Origin} {path.End} {path.Length} {path.ScanWidth}");
        Assert.Equal(28, paths[0].Length);
        var first = paths[1];
        Assert.InRange(first.Origin.X, 334.30f - .03f, 334.30f + .03f);
        Assert.InRange(first.Origin.Y, 1322.17f - .03f, 1322.17f + .03f);
        Assert.InRange(first.End.X, 329.69f - .08f, 329.69f + .08f);
        Assert.InRange(first.End.Y, 1305.35f - .08f, 1305.35f + .08f);
        Assert.InRange(first.Length, 17.43f, 17.45f);
    }

    [Theory]
    [InlineData("Tectonic Slam", 30)]
    [InlineData("Tectonic Slam of Cataclysm", 0)]
    public async Task Branches_hit_outside_the_main_fissure_without_double_hitting(string name, int chance)
    {
        using var trial = new Trial(name);
        const ushort sequence = 0x8002;
        var paths = Game.TectonicFissures.Plan(Trial.Origin, Trial.Aim, sequence, trial.Player.Id, 0, chance, false,
            area => MathF.Floor(100 * MathF.Sqrt(1 + area / 100f)) / 100);
        var branch = paths.First(path => path.Level == 1);
        var branchOnly = branch.Origin + .8f * branch.Length * branch.Direction;
        Assert.True(MathF.Abs(branchOnly.Y - Trial.Origin.Y) > 8);
        var branchVictim = trial.Target(904, ((int)branchOnly.X, (int)branchOnly.Y));
        var centralVictim = trial.Target(905, (108, 100));
        var hits = new List<uint>();
        await trial.Hit(sequence, hits.Add);
        Assert.Single(hits, id => id == branchVictim.ObjectId);
        Assert.Single(hits, id => id == centralVictim.ObjectId);
    }

    sealed class Trial : IDisposable
    {
        internal static readonly (int X, int Y) Origin = (100, 100), Aim = (106, 100);
        readonly TriggeredSummonTests.Fixture fixture;
        readonly ViperStrikeMambaTests.AttackClock clock = new();
        readonly PlayerUpdatesState? previous;
        readonly IDisposable stats;
        readonly ChargeSession charges;

        public Player Player => fixture.Player;
        public ushort SkillId { get; }
        public int Charges => charges.Effective(ChargeType.Endurance);

        public Trial(string name, bool multistrike = false, int minimum = 0)
        {
            fixture = new(clock);
            Player.Use(new(1, "tectonic-test", 400, 400, Origin.X, Origin.Y, (_, _) => true, []) { Sight = (_, _) => 2 });
            Player.Walk.Teleport(Origin.X, Origin.Y, clock.At);
            Player.Connection.LockstepClient = false;
            Player.Connection.PlayerObjectCreated = true;
            Player.ProjectileRuntimeState = fixture.Session;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            var items = Game.ItemsOf(Player);
            var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
            items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
            if (minimum > 0)
            {
                var mod = Program.Items.Mods.ById("JunMasterMinimumEnduranceCharges1")!;
                var ring = ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
                    { Mods = [new ItemMod(ModSection.Explicit, mod.Hash, [minimum])] };
                items.Add(PlayerInventories.Ring1, 0, 0, ring);
            }
            stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, Player))
                {
                    values[Stat.GlobalCannotCrit] = values[Stat.GlobalAlwaysHit] = 1;
                    values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 10_000;

                }
                return values;
            });
            SkillId = Assert.Single(multistrike
                ? fixture.Equip((name, 1), ("Multistrike Support", 1))
                : fixture.Equip((name, 1)));
            previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            charges = Game.ChargesOf(Player);
            var characterStats = Game.CharacterPlayerStats(Player);
            charges.Observe(key => characterStats.GetValueOrDefault(key));
            Assert.Equal(minimum, charges.Limits(ChargeType.Endurance).Minimum);
            Player.PlayerMana = 100;
        }

        public void SetCharges(int count) =>
            charges.State.Set(ChargeType.Endurance, count, Game.AreaTime(Player), charges.Limits(ChargeType.Endurance));

        public SpawnedMonster Target(uint id, (int X, int Y) point)
        {
            var monster = new SpawnedMonster("Tectonic Slam witness", new(point.X, point.Y, 0), 0, 0, 1_000_000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            Player.Area.Monsters[id] = monster;
            Player.Area.Grid.Set(new(id), point);
            return monster;
        }

        public async Task<bool> Attack()
        {
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            bool admitted = await Game.CastGemSkill(Player, SkillId, Origin, Aim);
            Player.PlayerMana = 100;
            clock.At += 1;
            return admitted;
        }

        public Task Hit(ushort sequence, Action<uint> hit) =>
            Game.AreaSkillHit(Player, SkillId, Game.GemSkillEffects(Player).Single(gem => gem.Id == SkillId),
                Origin, Aim, sequence: sequence, onHit: hit);

        public void Dispose()
        {
            Game.AreaUpdateContext.Value = previous;
            stats.Dispose();
            fixture.Dispose();
        }
    }
}
