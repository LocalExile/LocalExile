using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class SummonVariantTests
{
    [Fact]
    public async Task Skeleton_equipment_stats_change_their_own_timing_and_leave_other_summons_unchanged()
    {
        async Task<(double Action, double Move, double Duration)> Sample(string name, bool modifiers)
        {
            using var fixture = new TriggeredSummonTests.Fixture();
            var player = fixture.Player;
            ushort skill = Assert.Single(fixture.Equip((name, 1)));
            var gem = Assert.Single(Game.GemSkillEffects(player));
            using var input = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (modifiers && ReferenceEquals(owner, player))
                {
                    values[Stat.SkeletonAttackSpeedPlusPct] = 25;
                    values[Stat.SkeletonCastSpeedPlusPct] = 40;
                    values[Stat.SkeletonMovementSpeedPlusPct] = 20;
                    values[Stat.SkeletonDurationPlusPct] = 100;
                }
                return values;
            });
            string effect = Assert.IsType<string>(Program.Skills.EffectId(gem.Effect));
            var definition = Assert.IsType<SummonCatalog.Definition>(Game.SummonData.Find(effect));
            uint id = await Game.SummonFighter(player, skill, definition, (100, 100));
            Assert.NotEqual(0u, id);
            var fighter = player.Summons.Fighters[id];
            double action = fighter.Abilities.First(a => a.Attack.Spell == (name == "Summon Skeletons of Mages")
                && a.Attack.Damage.MaxTotal > 0).Attack.Seconds;
            return (action, fighter.Monster.Walk.Speed, player.Summons.Until[id] - fighter.SummonedAt);
        }

        var warrior = await Sample("Summon Skeletons", modifiers: false);
        var fasterWarrior = await Sample("Summon Skeletons", modifiers: true);
        Assert.True(fasterWarrior.Action < warrior.Action);
        Assert.True(fasterWarrior.Move > warrior.Move);
        Assert.Equal(warrior.Duration * 2, fasterWarrior.Duration, precision: 6);

        var mage = await Sample("Summon Skeletons of Mages", modifiers: false);
        var fasterMage = await Sample("Summon Skeletons of Mages", modifiers: true);
        Assert.True(fasterMage.Action < mage.Action);
        var spirit = await Sample("Summon Raging Spirit", modifiers: false);
        var spiritWithSkeletonModifiers = await Sample("Summon Raging Spirit", modifiers: true);
        Assert.Equal(spirit.Action, spiritWithSkeletonModifiers.Action, precision: 6);
        Assert.Equal(spirit.Move, spiritWithSkeletonModifiers.Move, precision: 6);
        Assert.Equal(spirit.Duration, spiritWithSkeletonModifiers.Duration, precision: 6);

        // Regression #1: number_of_skeletons_allowed_per_2_old stores 2 for +1 skeleton.
        using (var capFixture = new TriggeredSummonTests.Fixture())
        {
            var player = capFixture.Player;
            ushort skill = Assert.Single(capFixture.Equip(("Summon Skeletons", 1)));
            var definition = Assert.IsType<SummonCatalog.Definition>(Game.SummonData.Find("SummonSkeletons"));
            int baseLimit = Game.SummonLimit(player, definition, Game.SupportedSkill(player, skill));
            using var perTwo = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, player)) values[Stat.NumberOfSkeletonsAllowedPer2Old] = 2;
                return values;
            });
            Assert.Equal(baseLimit + 1, Game.SummonLimit(player, definition, Game.SupportedSkill(player, skill)));
        }

        // Regression #4: minion restrictions keep Blink/Mirror Arrow castable while blocking clone spawns.
        using (var cloneFixture = new TriggeredSummonTests.Fixture())
        {
            var player = cloneFixture.Player;
            ushort[] skills = cloneFixture.Equip(("Blink Arrow", 1), ("Mirror Arrow", 1));
            bool monarch = false;
            using var restrict = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, player))
                    values[monarch ? Stat.MaximumNumberOfXMinionTypeDoubled : Stat.YouCannotHaveNonGolemMinions] = 1;
                return values;
            });
            foreach (bool useMonarch in new[] { false, true })
            {
                monarch = useMonarch;
                foreach (ushort skill in skills)
                {
                    var supported = Game.SupportedSkill(player, skill);
                    string effectId = Assert.IsType<string>(Program.Skills.EffectId(supported.Effect));
                    Assert.Null(Game.ActorSkillDisabled(player, supported.Effect));
                    var definition = Assert.IsType<SummonCatalog.Definition>(Game.SummonData.Find(effectId));
                    Assert.Equal(0u, await Game.SummonFighter(player, skill, definition, (100, 100)));
                    if (effectId == "BlinkArrow")
                    {
                        var origin = player.Walk.Position(Game.AreaTime(player));
                        var aim = (origin.X + 6, origin.Y);
                        Assert.True(await Game.CastSummon(player, skill, definition, aim, 0, 0, () => true, triggered: true));
                        Assert.Equal(aim, player.Walk.Position(Game.AreaTime(player)));
                        Assert.Empty(player.Summons.Fighters);
                    }
                }
            }
        }

        // Regression #8: dropping a flat zombie cap bonus trims excess zombies without negative percentage scaling.
        using (var zombieFixture = new TriggeredSummonTests.Fixture())
        {
            var player = zombieFixture.Player;
            ushort skill = Assert.Single(zombieFixture.Equip(("Raise Zombie", 1)));
            var definition = Assert.IsType<SummonCatalog.Definition>(Game.SummonData.Find("RaiseZombie"));
            bool bonus = true;
            using var zombieStats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, player))
                {
                    values[Stat.RaiseZombieDoesNotUseCorpses] = 1;
                    if (bonus) values[Stat.NumberOfZombiesAllowed] = 2;
                }
                return values;
            });
            int boostedLimit = Game.SummonLimit(player, definition, Game.SupportedSkill(player, skill));
            for (int i = 0; i < boostedLimit; i++)
                Assert.NotEqual(0u, await Game.SummonFighter(player, skill, definition, (100, 100)));
            Assert.Equal(boostedLimit, player.Summons.Fighters.Count);
            bonus = false;
            int reducedLimit = Game.SummonLimit(player, definition, Game.SupportedSkill(player, skill));
            Assert.Equal(boostedLimit - 2, reducedLimit);
            player.Summons.RefreshAt = 0;
            await Game.SummonsTick(player, Game.AreaTime(player) + 1);
            Assert.Equal(reducedLimit, player.Summons.Fighters.Count);
        }
    }

    static ushort Equip(AnimatedAdmissionTests.Fixture f, string name, string weapon = "Rusted Sword", int sockets = 1)
    {
        var items = Game.ItemsOf(f.Player);
        foreach (var p in items.Items(PlayerInventories.Weapon1)) items.Remove(PlayerInventories.Weapon1, p.Id);
        var type = Program.Items.FindGem(name)!.Value;
        var gem = ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index,
            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20) };
        var host = ItemCatalog.Create(Program.Items.Find(weapon)!) with
        {
            Sockets = Enumerable.Range(0, sockets).Select(i => new ItemSocket(SocketColour.White, i == 0 ? gem : null)).ToArray()
        };
        items.Add(PlayerInventories.Weapon1, 0, 0, host);
        return Game.GemSkillEffects(f.Player).Single().Id;
    }

    [Fact]
    public async Task Self_reflection_copies_the_active_melee_weapon_and_never_consumes_ground_equipment()
    {
        using var f = new AnimatedAdmissionTests.Fixture();
        ushort source = Equip(f, "Animate Weapon of Self Reflection", sockets: 6);
        var equipped = Game.ItemsOf(f.Player).Items(PlayerInventories.Weapon1).Single().Item;
        var definition = Game.SummonData.Find("AnimateWeaponAltX")!;
        Assert.True(Game.SupportedSkill(f.Player, source).RequirementsMet);
        uint id = await Game.SummonFighter(f.Player, source, definition, (100, 100));
        Assert.NotEqual(0u, id);
        Assert.Same(equipped, f.Player.Summons.Fighters[id].Equipment[2]);
        var ground = new WorldItemObject(100, 100, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        uint groundId = f.Player.Instance.Ground.Add(ground, f.Player);
        id = await Game.SummonFighter(f.Player, source, definition, (100, 100), groundId);
        Assert.NotEqual(0u, id); Assert.Same(ground, f.Player.Instance.Ground.Get(groundId));
        Assert.Same(equipped, f.Player.Summons.Fighters[id].Equipment[2]);
        Assert.Same(equipped, Game.ItemsOf(f.Player).Items(PlayerInventories.Weapon1).Single().Item);
        int count = f.Player.Summons.Fighters.Count;
        source = Equip(f, "Animate Weapon of Self Reflection", "Crude Bow");
        Assert.Equal(0u, await Game.SummonFighter(f.Player, source, definition, (100, 100), groundId));
        Assert.Equal(count, f.Player.Summons.Fighters.Count);
        Assert.Same(ground, f.Player.Instance.Ground.Get(groundId));
    }

    [Fact]
    public async Task Holy_relic_variants_use_their_native_novas_and_their_respective_damage_centres()
    {
        foreach (bool conviction in new[] { false, true })
        {
            using var f = new AnimatedAdmissionTests.Fixture();
            ushort source = Equip(f, conviction ? "Summon Holy Relic of Conviction" : "Summon Holy Relic");
            uint id = await Game.SummonFighter(f.Player, source,
                Game.SummonData.Find(conviction ? "SummonHolyRelicAltX" : "SummonHolyRelic")!, (100, 100));
            var relic = f.Player.Summons.Fighters[id];
            var nova = Assert.Single(relic.Abilities, a => a.Effect == (conviction ? 9928 : 9927));
            var target = new SpawnedMonster("distant enemy", new(160, 100, 0), 100, 3, 100000) { Hostile = true };
            var beside = new SpawnedMonster("beside relic", new(100, 100, 0), 100, 3, 100000) { Hostile = true };
            f.Player.Area.Monsters[999] = target; f.Player.Area.Monsters[998] = beside;
            var action = new GolemState.Action(nova, 999, target, (160, 100), 0, relic.Monster.ActionGeneration);
            typeof(Game).GetMethod("GolemAbilityContactLocked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [f.Player, relic, action, 0d]);
            Assert.Equal(conviction, target.Life < 100000);
            Assert.Equal(!conviction, beside.Life < 100000);
            typeof(Game).GetMethod("HolyRelicsOnHitLocked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [f.Player, 999u, target, 1d]);
            Assert.Equal(conviction ? 998u : 999u, relic.Pending!.Target);
        }
    }
}
