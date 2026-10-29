using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    // Three legal eight-point builds; the third takes the free Spirit of Kahuturoa effect of Ancestral Spirit.
    static readonly ushort[][] AncestralCommanderBuilds =
    [
        [9828, 61282, 65410, 40762, 11217, 37852, 26496, 32105],
        [41978, 48740, 29654, 3245, 24701, 23061, 8455, 848],
        [19258, 47491, 59500, 28454, 16317, 26496, 32105, 11217, 37852],
    ];
    static readonly ushort[] AncestralSpiritOptions = [59500, 17140, 17511, 16639, 62534];
    // Toughened Skin, Ancestral Endurance, Ancestral Defiance and five Armour and Life nodes.
    static readonly ushort[] AncestralCommanderDefences = [11217, 37852, 24701, 23061, 8455, 848, 26496, 9828];

    static GatewayCharacter AncestralCommander() => new(classRow: 0, baseClass: 1, choice: "Ancestral Commander");

    // A Plate Vest with a unique body armour's 20% reduced Movement Speed.
    static Item SlowPlateVest() => ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
    { Mods = [Program.Items.Mods.ById("MovementVelocityUniqueBodyStr5")!.With(ModSection.Explicit, [-20], Random.Shared)] };

    [Fact]
    public void Ancestral_Commander_checklist_is_the_actual_graph_and_legal_builds_cover_every_node()
    {
        if (!DataAvailable()) return;
        var definition = Program.Ascendancies.Find(1, AscendancyFamily.Phrecia, "Ancestral Commander")!;
        Assert.Equal("Juggernaut", definition.Identity.Id);
        Assert.Equal(new ushort[] { 17765, 11217, 9828, 19258, 26496, 65410, 41978, 40762, 61282, 16317, 47491, 32105,
            37852, 23061, 848, 59500, 17140, 17511, 16639, 62534, 48740, 8455, 24701, 28454, 29654, 3245 }.Order(),
            definition.Nodes.Keys.Order());
        Assert.All(definition.Nodes.Values, n => Assert.Equal(0, n.PointsGranted));
        Assert.True(definition.Nodes[47491].IsChoice);
        Assert.Equal(AncestralSpiritOptions.Order(),
            definition.Nodes[47491].Adjacent.Where(n => definition.Nodes[n].IsChoiceOption).Order());
        Assert.Equal(definition.Nodes.Keys.Where(n => n != definition.StartNode).Order(),
            AncestralCommanderBuilds.SelectMany(b => b).Concat(AncestralSpiritOptions).Distinct().Order());
        foreach (var build in AncestralCommanderBuilds.Append(AncestralCommanderDefences))
        {
            var progress = new AscendancyProgress(definition.Identity, build, AscendancyRules.AwardKeys);
            Assert.Same(definition, Program.AscendancyRules.Validate(1, progress));
            Assert.Equal(8, Program.AscendancyRules.SpentPoints(1, progress));
        }
    }

    [Fact]
    public async Task Ancestral_Commander_paid_choice_refund_reselection_and_reload()
    {
        if (!DataAvailable()) return;
        using var game = AncestralCommander();
        var build = AncestralCommanderBuilds[2];
        Assert.Null((await game.Change(build, true)).Error);
        Assert.Equal(build.Order(), game.State.MainAscendancy.Allocated.Order());
        Assert.NotNull((await game.Change([9828], true)).Error); // a ninth paid point
        Assert.NotNull((await game.Change([17511], true)).Error); // a second Ancestral Spirit effect
        Assert.Null((await game.Change([59500], false, refundPoints: true)).Error);
        Assert.Equal((ushort)15, game.State.RefundPoints); // changing the choice effect costs five
        Assert.Null((await game.Change([17140], true)).Error);
        var reloaded = game.Reload();
        Assert.Equal(build.Where(n => n != 59500).Append((ushort)17140).Order(), reloaded.MainAscendancy.Allocated.Order());
        Assert.Equal((ushort)15, reloaded.RefundPoints);
    }

    [Fact]
    public async Task Ancestral_Commander_defences_match_their_plain_stats()
    {
        if (!DataAvailable()) return;
        using var game = AncestralCommander();
        using var plain = AncestralCommander();
        foreach (var character in new[] { game, plain })
        {
            character.Items.Add(PlayerInventories.BodyArmour, 0, 0, SlowPlateVest());
            character.State.Pools = Program.PoolsFor(character.State, 1);
            lock (character.State.Monsters)
                Program.ApplyPlayerAilmentsLocked(character.State, DamagePacket.Of(DamageType.Cold, 1, 1),
                    DamagePacket.Of(DamageType.Cold, (int)character.State.PlayerMaxLife, (int)character.State.PlayerMaxLife),
                    false, false, character.State.Clock.Elapsed.TotalSeconds, Program.CharacterPlayerStats(character.State));
            Assert.Equal(.7, character.State.ElementalAilments.ActionFactor(character.State.Clock.Elapsed.TotalSeconds), 6);
        }
        var before = plain.State.Pools;
        Assert.Equal(-20, before.MovementVelocityPct);
        Assert.True(before.Armour > 0);

        // Allocation reconciles the active chill with Toughened Skin's action-speed floor.
        Assert.Null((await game.Change(AncestralCommanderDefences, true)).Error);
        var state = game.State;
        double now = state.Clock.Elapsed.TotalSeconds;
        Assert.Equal(30.0, state.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Chill, now));
        Assert.Equal(1.0, state.ElementalAilments.ActionFactor(now));
        Assert.Equal((uint)(before.Life * 120m / 100), state.Pools.Life); // five times 4% increased maximum Life
        Assert.Equal((int)(before.Armour * 2 * 140m / 100), state.Pools.Armour); // doubled, then five times 8%
        Assert.Equal(0, state.Pools.MovementVelocityPct);
        Assert.Equal(0, GamblerStat(state, "movement_velocity_+%"));
        Assert.Equal(Program.WalkSpeed, state.Walk.Speed, 6);
        Assert.Equal(5, GamblerStat(state, "virtual_maximum_endurance_charges"));
        Assert.Equal(5, Charges.LimitsOf(ChargeType.Endurance, n => GamblerStat(state, n)).Maximum);

        // The plain stats give the same results without the nodes.
        Assert.Equal(-20, GamblerStat(plain.State, "movement_velocity_+%"));
        Assert.Equal(3, GamblerStat(plain.State, "virtual_maximum_endurance_charges"));
        Assert.Equal(Program.WalkSpeed * 80 / 100 * .7, plain.State.Walk.Speed, 6);
        lock (plain.State.Monsters)
            foreach (var name in new[] { "action_speed_cannot_be_reduced_below_base", "movement_speed_cannot_be_reduced_below_base",
                "damage_reduction_rating_from_body_armour_doubled" })
                plain.State.PlayerStats[GamblerKey(name)] = 1;
        lock (plain.State.Monsters) plain.State.PlayerStats[GamblerKey("max_endurance_charges")] = 2;
        lock (plain.State.Monsters) ProgramValue<object?>("RecomputePoolsAndSpeed", plain.State);
        Assert.Equal(before.Armour * 2, plain.State.Pools.Armour);
        Assert.Equal(0, plain.State.Pools.MovementVelocityPct);
        Assert.Equal(1.0, plain.State.ElementalAilments.ActionFactor(plain.State.Clock.Elapsed.TotalSeconds));
        Assert.Equal(Program.WalkSpeed, plain.State.Walk.Speed, 6);
        Assert.Equal(5, GamblerStat(plain.State, "virtual_maximum_endurance_charges"));

        // Iron Reflexes: the body armour's converted evasion doubles too; without it, its evasion does not.
        var scaleVest = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStrDex1")!);
        Assert.NotNull(plain.Items.Remove(PlayerInventories.BodyArmour, plain.Items.Items(PlayerInventories.BodyArmour).Single().Id));
        plain.Items.Add(PlayerInventories.BodyArmour, 0, 0, scaleVest);
        (decimal Armour, decimal Evasion, int EnergyShield, decimal Ward) Defences(bool doubled, bool ironReflexes) =>
            ProgramValue<(decimal, decimal, int, decimal)>("EquippedBaseDefences", plain.State,
                (Func<string, int>)(n => doubled && n == "damage_reduction_rating_from_body_armour_doubled" ? 1 : 0), ironReflexes);
        var single = Defences(false, true);
        Assert.True(single.Evasion > 0);
        Assert.Equal((single.Armour * 2, single.Evasion * 2), (Defences(true, true).Armour, Defences(true, true).Evasion));
        Assert.Equal((single.Armour * 2, single.Evasion), (Defences(true, false).Armour, Defences(true, false).Evasion));

        // Ancestral Defiance: 10% of missing Life before a landed monster hit and a dev hit; /set_stat 0 removes it.
        uint max = state.PlayerMaxLife, half = max / 2, gain = (max - half) * 10 / 100;
        var hit = DamagePacket.Of(DamageType.Chaos, 20, 20);
        async Task<uint> LifeAfter(Func<Task> strike)
        {
            state.PlayerLife = half;
            await strike();
            return state.PlayerLife;
        }
        uint monster = await LifeAfter(() => game.GamblerMonsterHit(hit, critical: false));
        uint dev = await LifeAfter(() => game.Command("/hit chaos 20"));
        lock (state.Monsters) state.PlayerStats[GamblerKey("missing_unreserved_life_%_gained_as_life_before_hit")] = 0;
        Assert.Equal(monster - gain, await LifeAfter(() => game.GamblerMonsterHit(hit, critical: false)));
        Assert.Equal(dev - gain, await LifeAfter(() => game.Command("/hit chaos 20")));
        Assert.True(monster < half + gain);
    }
}
