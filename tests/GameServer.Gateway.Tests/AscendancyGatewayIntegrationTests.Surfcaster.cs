using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    // Shadow (classRow 5, base class 6). No node grants points, so each build spends the class's eight.
    // Together they reach the 18 non-start nodes; A also holds both fishing-rod notables and Sea Legs.
    internal static readonly ushort[][] SurfcasterBuilds =
    [
        [48926, 64012, 33031, 37082, 36782, 37553, 42290, 61696],
        [41643, 60505, 34767, 61246, 53922, 6252, 37534, 52944],
        [41643, 60505, 56184, 52264, 36782, 37553, 42290, 61696],
    ];

    static int SurfcasterStat(ClientState state, string name) =>
        Program.CharacterPlayerStats(state).GetValueOrDefault(Program.Stats.Key(name)!.Value);

    static GatewayCharacter Surfcaster() => new(classRow: 5, baseClass: 6, choice: "Surfcaster", refundPoints: 100, level: 60);

    static void SetDev(ClientState state, params (string Name, int Value)[] stats)
    {
        lock (state.Monsters)
            foreach (var (name, value) in stats)
                if (value == 0) state.PlayerStats.Remove(Program.Stats.Key(name)!.Value);
                else state.PlayerStats[Program.Stats.Key(name)!.Value] = value;
        state.Pools = Program.PoolsFor(state, 60);
    }

    [Fact]
    public void Surfcaster_checklist_is_the_actual_graph_and_three_builds_cover_every_node()
    {
        if (!DataAvailable()) return;
        var definition = Program.Ascendancies.Find(6, AscendancyFamily.Phrecia, "Surfcaster")!;
        Assert.Equal(new ushort[] { 18635, 48926, 37082, 53922, 64012, 41643, 60505, 34767, 6252, 33031, 52944, 36782,
            37553, 37534, 52264, 56184, 61696, 42290, 61246 }.Order(), definition.Nodes.Keys.Order());
        Assert.Equal((ushort)18635, definition.StartNode);
        Assert.All(definition.Nodes.Values, n => Assert.Equal(0, n.PointsGranted));
        Assert.Equal(definition.Nodes.Keys.Where(n => n != definition.StartNode).Order(),
            SurfcasterBuilds.SelectMany(b => b).Distinct().Order());
        // The default Phrecia gate stays closed without Game_VERIFIED_SURFCASTER.
        Assert.Throws<AscendancyRuleException>(() => AscendancyClientCompatibility.RequirePlayable(definition.Identity));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Surfcaster_legal_build_paid_full_refund_reallocation_and_reload(int index)
    {
        if (!DataAvailable()) return;
        using var game = Surfcaster();
        var nodes = SurfcasterBuilds[index];
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(8, game.State.MainAscendancy.Allocated.Count);
        // Four small nodes at 4% each, and Ghost of the Deep's 23% in builds A and C.
        Assert.Equal(nodes.Contains((ushort)37553) ? 39 : 16, SurfcasterStat(game.State, "cast_speed_+%"));
        Assert.Equal(nodes.Order(), game.Reload().MainAscendancy.Allocated.Order());
        Assert.Null((await game.Change(nodes.Reverse().ToArray(), false, refundPoints: true)).Error);
        Assert.Empty(game.State.MainAscendancy.Allocated);
        Assert.Equal((ushort)60, game.State.RefundPoints);
        Assert.Equal(0, SurfcasterStat(game.State, "cast_speed_+%"));
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(nodes.Order(), game.Reload().MainAscendancy.Allocated.Order());
        Assert.Equal((ushort)60, game.Reload().RefundPoints);
    }

    [Fact]
    public async Task Surfcaster_rod_notables_and_Sea_Legs_match_plain_equivalents()
    {
        if (!DataAvailable()) return;
        using var game = Surfcaster();
        using var plain = Surfcaster();
        Assert.Null((await game.Change(SurfcasterBuilds[0], true)).Error);

        // Sea Legs (61696) against plain +40% cold and lightning resistance with both overcap flags.
        (string, int)[] overcapped = [("base_cold_damage_resistance_%", 100), ("base_lightning_damage_resistance_%", 100)];
        SetDev(game.State, overcapped);
        SetDev(plain.State, [.. overcapped, (ResistanceOvercap.ColdAndLightningResistance, 40),
            (ResistanceOvercap.ColdEvasionFlag, 1), (ResistanceOvercap.LightningCritFlag, 1)]);
        Assert.True(game.State.Pools.ColdOvercapEvasionPct > 0);
        Assert.Equal(plain.State.Pools.ColdOvercapEvasionPct, game.State.Pools.ColdOvercapEvasionPct);
        Assert.Equal(plain.State.Pools.LightningOvercapCritPct, game.State.Pools.LightningOvercapCritPct);
        Assert.Equal(plain.State.Pools.ColdResistance, game.State.Pools.ColdResistance);
        Assert.Equal(plain.State.Pools.LightningResistance, game.State.Pools.LightningResistance);
        Assert.Equal(plain.State.Pools.Evasion, game.State.Pools.Evasion);
        Assert.Equal(plain.State.Pools.SpellCriticalStrikeChance, game.State.Pools.SpellCriticalStrikeChance);

        // Screaming Reels (64012) and Baited Breath (37082) hold only with a Fishing Rod in the active main hand.
        foreach (var character in new[] { game, plain })
            EquipFamilyGem(character, ItemCatalog.Create(Program.Items.Find("Fireball")!));
        var (skill, effect, _, _, _, _) = ProgramValue<IEnumerable<(ushort, int, string, int, int, uint)>>(
            "GemSkillEffects", game.State).Single();
        Assert.Equal(750, Program.Skills.CastMs(effect));
        int? CastMs(ClientState state) => ProgramValue<int?>("CastMs", state, effect, skill);
        (int Speed, int Rate, int Crit) Record(ClientState state)
        {
            var stats = FamilySkills(state).Single(s => s.Id == skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
            int Get(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
            return (Get("cast_speed_+%"), Get("hundred_times_casts_per_second"), Get("spell_critical_strike_chance"));
        }
        Assert.Equal(540, CastMs(game.State)); // 750 / 1.39 (small nodes and Ghost of the Deep): no rod, no multiplier
        Assert.False(Program.RollCriticalStrike(game.State, 0));

        var rod = Program.Items.Find("FishingRod1")!;
        Assert.Equal("FishingRod", rod.Class);
        foreach (var character in new[] { game, plain })
            character.Items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(rod));
        // round(139 × 1.6 − 100) = 122% (client calculator 0x142709610); plain: the same rod with 122% increased.
        SetDev(game.State);
        SetDev(plain.State, ("base_cast_speed_+%", 122));
        Assert.Equal(338, CastMs(game.State)); // 750 / 2.22
        Assert.Equal(CastMs(plain.State), CastMs(game.State));
        Assert.Equal((122, 296, 10_000), Record(game.State));
        Assert.Equal((122, 296), (Record(plain.State).Speed, Record(plain.State).Rate));
        Assert.NotEqual(10_000, Record(plain.State).Crit);

        // Every hit crits without a roll, even from a 0% base; the panel shows 100% where the base is not 0.
        Assert.Equal(10_000, game.State.Pools.AttackCriticalStrikeChance);
        Assert.Equal(10_000, game.State.Pools.SpellCriticalStrikeChance);
        Assert.True(plain.State.Pools.AttackCriticalStrikeChance is > 0 and < 10_000);
        Assert.True(Program.RollCriticalStrike(game.State, 0, attack: false));
        Assert.False(Program.RollCriticalStrike(plain.State, 0, attack: false));
        // Never deal critical strikes wins.
        SetDev(game.State, ("global_cannot_crit", 1));
        Assert.False(Program.RollCriticalStrike(game.State, 10_000));
        Assert.Equal(0, game.State.Pools.AttackCriticalStrikeChance);
        SetDev(game.State, ("global_cannot_crit", 0));

        // Off the rod, both effects end.
        var held = Assert.Single(game.Items.Items(PlayerInventories.Weapon1));
        Assert.NotNull(game.Items.Remove(PlayerInventories.Weapon1, held.Id));
        SetDev(game.State);
        Assert.Equal(540, CastMs(game.State));
        Assert.False(Program.RollCriticalStrike(game.State, 0));
        Assert.Equal(39, Record(game.State).Speed);
    }

    // Ghost of the Deep (37553): BuffTemplates 671's values on BuffDefinitions 2550's stats join the passives' sum, so the
    // existing consumers read them like any other source; the buff is published on the player and goes with the refund.
    [Fact]
    public async Task Surfcaster_Ghost_of_the_Deep_buff_matches_plain_stats_and_is_published()
    {
        if (!DataAvailable()) return;
        using var game = Surfcaster();
        using var plain = Surfcaster();
        game.State.PlayerSent = true;
        var buff = Assert.Single(PassiveBuffAuras.For(Program.PassivesFor(game.State), [37553]));
        Assert.Equal((ushort)2550, buff.Definition);
        Assert.Equal([("base_movement_velocity_+%", 23), ("attack_speed_+%", 23), ("base_cast_speed_+%", 23),
            ("damage_+%", 23), ("cant_touch_this", 1), ("monster_is_touched_by_ghost", 1)],
            buff.Stats.Select(s => (Program.Stats.Name(s.Key), s.Value)));
        int baseDamage = SurfcasterStat(plain.State, "main_hand_maximum_total_damage");
        double baseWalk = plain.State.Walk.Speed;

        var (error, bytes) = await game.Change([36782, 37553], true);
        Assert.Null(error);
        ushort instance = game.State.PassiveBuffs[2550];
        Assert.True(bytes.AsSpan().IndexOf(InstanceEntities.AddBuff(Program.PlayerId, instance, 2550, Program.PlayerId)) >= 0);
        // Plain: the small node's 4% cast speed plus the buff's damage as /set_stat values.
        int baseAttackSpeed = SurfcasterStat(plain.State, "main_hand_attack_speed_+%");
        SetDev(plain.State, ("base_cast_speed_+%", 27), ("damage_+%", 23));
        foreach (string stat in new[] { "cast_speed_+%", "main_hand_maximum_total_damage" })
            Assert.Equal(SurfcasterStat(plain.State, stat), SurfcasterStat(game.State, stat));
        // The pools give the unarmed swing 10% more attack speed: 100 × 1.1 − 100 = 10 without, 123 × 1.1 − 100 with.
        Assert.Equal((10, 35), (baseAttackSpeed, SurfcasterStat(game.State, "main_hand_attack_speed_+%")));
        Assert.Equal(27, SurfcasterStat(game.State, "cast_speed_+%"));
        Assert.True(SurfcasterStat(game.State, "main_hand_maximum_total_damage") > baseDamage);
        Assert.Equal(23, game.State.Pools.MovementVelocityPct);
        Assert.Equal(baseWalk * 1.23, game.State.Walk.Speed, 6);
        Assert.Null(PassiveBuffAuras.Presentation(game.State)); // already published

        // A reload (a new area's connection) has the stats and publishes the buff again.
        var reloaded = game.Reload();
        reloaded.PlayerSent = true;
        Assert.Equal(23, reloaded.Pools.MovementVelocityPct);
        ushort next = reloaded.NextBuff;
        Assert.Equal(InstanceEntities.AddBuff(Program.PlayerId, next, 2550, Program.PlayerId), PassiveBuffAuras.Presentation(reloaded));

        (error, bytes) = await game.Change([37553, 36782], false, refundPoints: true);
        Assert.Null(error);
        Assert.True(bytes.AsSpan().IndexOf(InstanceEntities.RemoveBuff(Program.PlayerId, instance)) >= 0);
        Assert.Empty(game.State.PassiveBuffs);
        Assert.Equal(0, game.State.Pools.MovementVelocityPct);
        Assert.Equal(baseWalk, game.State.Walk.Speed, 6);
        Assert.Equal(0, SurfcasterStat(game.State, "cast_speed_+%"));
    }

    // Changing Seasons (52944): any hit can chill the player, and the chill speeds up instead of slowing.
    [Fact]
    public async Task Surfcaster_Changing_Seasons_chills_from_any_hit_and_reverses_the_chill()
    {
        if (!DataAvailable()) return;
        using var game = Surfcaster();
        using var plain = Surfcaster();
        Assert.Null((await game.Change([41643, 60505, 37534, 52944], true)).Error);
        Assert.Null((await plain.Change([41643, 60505, 37534], true)).Error);
        double walk = plain.State.Walk.Speed;
        (double Factor, int Virtual, int Reduced, int Combined, double Walk) Hit(ClientState state, DamageType type)
        {
            lock (state.Monsters)
            {
                var hit = DamagePacket.Of(type, (int)state.PlayerMaxLife, (int)state.PlayerMaxLife);
                Program.ApplyPlayerAilmentsLocked(state, hit, hit, false, false, state.Clock.Elapsed.TotalSeconds,
                    Program.CharacterPlayerStats(state));
            }
            return (Math.Round(Program.PlayerActionFactor(state), 6), SurfcasterStat(state, "virtual_action_speed_+%"),
                SurfcasterStat(state, "action_speed_-%"), SurfcasterStat(state, "intermediary_combined_action_speed_+%"),
                Math.Round(state.Walk.Speed / walk, 6));
        }
        // A full-life physical hit: no chill without the node; with it a 30% chill that is 30% more action speed.
        Assert.Equal((1.0, 0, 0, 0, 1.0), Hit(plain.State, DamageType.Physical));
        Assert.Equal((1.3, 30, -30, 30, 1.3), Hit(game.State, DamageType.Physical));
        // A plain cold hit slows by the same chill.
        Assert.Equal((.7, -30, 30, -30, .7), Hit(plain.State, DamageType.Cold));
        plain.State.ElementalAilments.Clear();
        SetDev(plain.State, ("chill_effect_is_reversed", 1), ("all_damage_taken_can_chill", 1));
        Assert.Equal(Hit(game.State, DamageType.Physical), Hit(plain.State, DamageType.Physical));
    }

    [Fact]
    public void Surfcaster_reversed_chill_leaves_freeze_and_unaffected_action_speed()
    {
        static int Reversed(string name) => name == "chill_effect_is_reversed" ? 1 : 0;
        var cold = DamagePacket.Of(DamageType.Cold, 200, 200);
        var reversed = new PlayerElementalAilments();
        var plain = new PlayerElementalAilments();
        // A critical 20%-of-life cold hit: floor(50 × 0.2^0.4) = 26% chill for 2 s, frozen for 6 × 0.2 = 1.2 s.
        reversed.Apply(cold, cold, true, 1000, 0, Reversed, new Random(1));
        plain.Apply(cold, cold, true, 1000, 0, _ => 0, new Random(1));
        Assert.Equal(0, reversed.ActionFactor(1));
        Assert.Equal(1.26, reversed.ActionFactor(1.5), 6);
        Assert.Equal(.74, plain.ActionFactor(1.5), 6);
        reversed.Reconcile(name => Reversed(name) | (name == "not_affected_by_action_speed_modifiers" ? 1 : 0));
        Assert.Equal(1, reversed.ActionFactor(1.5));
    }
}
