using System.Reflection;
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

    [Fact]
    public async Task Ancestral_Commander_warcries_match_their_plain_stats()
    {
        if (!DataAvailable()) return;
        using var game = AncestralCommander();
        using var plain = AncestralCommander();
        using var none = AncestralCommander();
        Assert.Null((await game.Change(AncestralCommanderBuilds[1], true)).Error); // Call of the Ancestors, Deathrattle
        lock (plain.State.Monsters)
            foreach (var (name, value) in new[] { ("warcry_speed_+%", 200), ("warcry_cooldown_speed_+%", -30),
                (Program.CorpseExplosionStat, 12), (Program.OpenChestsStat, 1) })
                plain.State.PlayerStats[GamblerKey(name)] = value;

        // Call of the Ancestors: the cry takes a third of its 0.8 s and recovers at 70%; a spell's recovery is unchanged.
        int cry = Program.Skills.EffectRow("EnduringCry"), bomb = Program.Skills.EffectRow("FrostBomb");
        SingleUseCooldown? Cooldown(ClientState s, int effect) => SingleUseCooldown.ForSkill(Program.Skills, Program.Stats,
            effect, 1, Program.CharacterPlayerStats(s), WarcryOwn(effect));
        double Cast(ClientState s) => Program.WarcryCastSeconds(s, cry, Program.CharacterPlayerStats(s), WarcryOwn(cry));
        foreach (var s in new[] { game.State, plain.State })
        {
            Assert.Equal(.8 / 3, Cast(s), 6);
            Assert.Equal(new SingleUseCooldown(8000, -30), Cooldown(s, cry));
            Assert.Equal(new SingleUseCooldown(2500, 0), Cooldown(s, bomb));
        }
        Assert.Equal(new SingleUseCooldown(8000, 0), Cooldown(none.State, cry));
        Assert.Equal(.8, Cast(none.State), 6);
        lock (none.State.Monsters)
            Program.ApplyPlayerAilmentsLocked(none.State, DamagePacket.Of(DamageType.Cold, 1, 1),
                DamagePacket.Of(DamageType.Cold, (int)none.State.PlayerMaxLife, (int)none.State.PlayerMaxLife),
                false, false, none.State.Clock.Elapsed.TotalSeconds, Program.CharacterPlayerStats(none.State));
        Assert.Equal(.8 / .7, Cast(none.State), 6); // chilled: 30% less action speed

        // Deathrattle: at the cry's release, a corpse within its radius explodes for 12% of its maximum Life on the living
        // monster beside it, is consumed and removed, and a Rhoa Nest within the radius opens. The node's cry goes through
        // the real Enduring Cry release; the plain stats and no stats call the release consumer directly.
        var zombie = Program.Monsters.Find(TutorialObjects.Zombie)!;
        Assert.True(zombie.LifeData.IsUsableAsCorpse(0));
        foreach (var (character, works) in new[] { (game, true), (plain, true), (none, false) })
        {
            var state = character.State;
            double usedAt = state.Clock.Elapsed.TotalSeconds;
            var here = state.Walk.Position(usedAt);
            var corpse = new SpawnedMonster("corpse", new(here.X + 30, here.Y, 30), 0, 2, 1000) { Variety = zombie, Life = 0, Dead = true };
            var beside = new SpawnedMonster("beside", new(here.X + 40, here.Y, 30), 0, 2, 1000);
            var nest = new GlyphQuest.Nest(new(GlyphQuest.NestTypes[0], "", here.X - 20, here.Y, 0, null, 0), 0);
            lock (state.Monsters) { state.Monsters[50] = corpse; state.Monsters[51] = beside; }
            state.Glyphs = new([nest], null, null, []);
            lock (state.GlyphNests) state.GlyphNests[0x2e80] = nest;
            bool Opened() => state.Instance.ChestOpened(nest.At.X, nest.At.Y);
            var bytes = character == game ? (await game.EnduringCry(cry, Opened)).Bytes : (await character.WarcryRelease(here, 60)).Bytes;
            Assert.Equal(works ? 880u : 1000u, beside.Life);
            Assert.Equal(works, corpse.CorpseConsumed);
            Assert.Equal(works, Opened());
            if (character == game) Assert.True(state.Clock.Elapsed.TotalSeconds - usedAt >= .8 / 3);
            else Assert.Equal(works, bytes.AsSpan().IndexOf(InstanceProtocol.RemoveObject(50)) >= 0);
        }
    }

    [Fact]
    public void Ancestral_Commander_strike_targets_follow_the_client_scan()
    {
        if (!DataAvailable()) return;
        var skills = Program.Skills;
        int heavy = skills.EffectRow("HeavyStrike"), vaal = skills.EffectRow("VaalHeavyStrike");
        Assert.True(skills.IsStrike(heavy) && skills.IsStrike(skills.EffectRow("Melee"))); // the default attack too
        Assert.True(skills.IsStrike(vaal) && skills.IsVaal(vaal));
        Assert.False(skills.IsStrike(skills.EffectRow("GroundSlam")) || skills.IsVaal(heavy));
        int Reach(string name) => name == "melee_attack_number_of_spirit_strikes" ? 2 : 0;
        Assert.Equal(2, StrikeTargets.SpiritStrikes(Reach, strike: true, vaal: false, melee: true));
        Assert.Equal(0, StrikeTargets.SpiritStrikes(Reach, strike: true, vaal: true, melee: true));
        Assert.Equal(0, StrikeTargets.SpiritStrikes(Reach, strike: false, vaal: false, melee: true));
        Assert.Equal(0, StrikeTargets.SpiritStrikes(Reach, strike: true, vaal: false, melee: false));
        Assert.Equal((30, 45), (StrikeTargets.Range(0), StrikeTargets.Range(50)));

        // Around the player at (0, 0): the action's target (1), one within 10 cells (2), four at 25-44 cells, one at 60.
        StrikeTargets.Candidate At(uint id, int x) => new(id, (x, 0), 0);
        var crowd = new[] { At(1, 20), At(2, 5), At(3, 25), At(4, 40), At(5, 44), At(6, 60), At(7, 30) };
        List<uint> Pick(int range, int count) => StrikeTargets.Pick(crowd, (0, 0), range, count, 7, Program.PlayerId, new HashSet<uint> { 1 });
        Assert.Equal(2, Pick(45, 2).Count);
        Assert.Equal(Pick(45, 2), Pick(45, 2)); // seeded by the use's sequence: the same use picks the same
        Assert.Equal(new uint[] { 3, 4, 5, 7 }, Pick(45, 9).Order());
        Assert.Equal(new uint[] { 3, 7 }, Pick(30, 9).Order()); // 40 cells away is in with Ancestral Reach's 50% only
        Assert.Empty(Pick(45, 0));

        // Ancestral Echo's memory: the previous use's centre, unless the player stood within 5 cells of where it stands.
        var memory = new StrikeTargets.Memory();
        Assert.Null(memory.Use(1, (0, 0), (5, 0)));
        Assert.Null(memory.Use(1, (3, 0), (8, 0)));
        Assert.Equal((8, 0), memory.Use(1, (40, 0), (45, 0)));
        memory.Clear();
        Assert.Null(memory.Use(1, (90, 0), (95, 0)));
    }

    [Fact]
    public async Task Ancestral_Commander_strikes_match_their_plain_stats()
    {
        if (!DataAvailable()) return;
        using var game = AncestralCommander();
        using var plain = AncestralCommander();
        using var none = AncestralCommander();
        Assert.Null((await game.Change(AncestralCommanderBuilds[0], true)).Error); // Ancestral Reach, Ancestral Echo, Born under Fire
        lock (plain.State.Monsters)
            foreach (var (name, value) in new[] { ("melee_attack_number_of_spirit_strikes", 2), ("extra_target_targeting_distance_+%", 50),
                ("melee_strike_skill_strike_previous_location", 1) })
                plain.State.PlayerStats[GamblerKey(name)] = value;

        // Heavy Strike from (100, 100) on a monster at (105, 100), with three more 40 cells away; then from (160, 100), 55 cells
        // from the first target and out of every scan, so only Ancestral Echo reaches it.
        int heavy = Program.Skills.EffectRow("HeavyStrike");
        var struck = new List<uint[]>();
        foreach (var character in new[] { game, plain, none })
        {
            var state = character.State;
            character.Items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Club")!) with
            {
                Sockets = [new(SocketColour.White, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemHeavyStrike")!))],
                Links = [1],
            });
            var monsters = new Dictionary<uint, SpawnedMonster>();
            lock (state.Monsters)
                foreach (var (id, x, y) in new (uint, int, int)[] { (60, 105, 100), (61, 100, 140), (62, 60, 100), (63, 100, 60) })
                    state.Monsters[id] = monsters[id] = new($"strike {id}", new(x, y, 30), 0, 2, 1000);
            ushort skill = character.HeavyStrikeGem(heavy).Id, buff = state.NextBuff;
            var bytes = (await character.HeavyStrike(heavy, (100, 100), (105, 100), sequence: 1, target: 60)).Bytes;
            var memory = InstanceEntities.AddBuff(Program.PlayerId, buff, 1451, Program.PlayerId, skill,
                activeSkill: (skill, Program.Skills.ActionHash(heavy)!.Value));
            Assert.Equal(character != none, bytes.AsSpan().IndexOf(memory) >= 0); // skill_location_memory, as the client keeps it
            struck.Add([.. monsters.Where(kv => kv.Value.Life < 1000).Select(kv => kv.Key).Order()]);
            uint first = monsters[60].Life;
            await character.HeavyStrike(heavy, (160, 100), (165, 100), sequence: 2, target: 0);
            Assert.Equal(character != none, monsters[60].Life < first); // Ancestral Echo: the first centre again
        }
        Assert.Equal(3, struck[0].Length); // the target and two spirit strikes
        Assert.Equal(60u, struck[0][0]);
        Assert.Equal(struck[0], struck[1]);
        Assert.Equal(new uint[] { 60 }, struck[2]);

        // The default attack is a Strike too: its swing hits two spirit strike targets as well, and its next swing from 60
        // cells away also strikes the monster at its previous target's place.
        var attacker = game.State;
        double at = attacker.Clock.Elapsed.TotalSeconds;
        attacker.Walk.Teleport(60, 60, at);
        var targets = new Dictionary<uint, SpawnedMonster>();
        lock (attacker.Monsters)
        {
            attacker.Monsters.Clear();
            attacker.PlayerStats[GamblerKey("global_always_hit")] = 1;
            foreach (var (id, x, y) in new (uint, int, int)[] { (70, 63, 60), (71, 60, 100), (72, 20, 60), (73, 60, 20), (74, 123, 60) })
                attacker.Monsters[id] = targets[id] = new($"attack {id}", new(x, y, 30), 0, 2, 1000);
        }
        await game.DefaultAttack(70, sequence: 3);
        Assert.Equal(3, targets.Count(kv => kv.Value.Life < 1000));
        Assert.True(targets[74].Life == 1000);
        uint before = targets[70].Life;
        attacker.Walk.Teleport(120, 60, attacker.Clock.Elapsed.TotalSeconds);
        attacker.NextAttackAt = 0;
        await game.DefaultAttack(74, sequence: 4);
        Assert.True(targets[74].Life < 1000);
        Assert.True(targets[70].Life < before);
    }

    [Fact]
    public async Task Ancestral_Commander_born_under_fire_matches_a_converting_monster()
    {
        if (!DataAvailable()) return;
        using var game = AncestralCommander();
        using var plain = AncestralCommander();
        Assert.Null((await game.Change(AncestralCommanderBuilds[0], true)).Error);
        foreach (var character in new[] { game, plain })
            character.State.Pools = character.State.Pools with { FireResistance = 40 };
        async Task<uint> Loss(GatewayCharacter character, int distance, DamagePacket hit, bool critical = false)
        {
            character.State.PlayerLife = character.State.PlayerMaxLife;
            character.State.ElementalAilments.Clear();
            await character.SwingFrom(distance, hit, critical);
            return character.State.PlayerMaxLife - character.State.PlayerLife;
        }
        var physical = DamagePacket.Of(DamageType.Physical, 40, 40);
        var split = DamagePacket.Of(DamageType.Physical, 20, 20).Add(DamageType.Fire, new DamageRange(20, 20));
        // Within 60 cells half of the physical hit is fire, which the player's 40% fire resistance lowers; beyond, it is not.
        uint converted = await Loss(game, 30, physical), outside = await Loss(game, 70, physical);
        Assert.Equal(await Loss(plain, 30, split), converted);
        Assert.Equal(await Loss(plain, 70, physical), outside);
        Assert.True(converted < outside);
        // The converted fire ignites on a critical hit: the ailment's base converts too.
        await Loss(game, 30, physical, critical: true);
        Assert.True(game.State.ElementalAilments.Snapshot(game.State.Clock.Elapsed.TotalSeconds).Ignite);
        await Loss(game, 70, physical, critical: true);
        Assert.False(game.State.ElementalAilments.Snapshot(game.State.Clock.Elapsed.TotalSeconds).Ignite);

        // Only auras whose every stat the server applies act: not Overwhelm's crit multiplier, Void Beacon's resistances or
        // Kaom's Binding's from-enemy conversion.
        var born = Assert.Single(PassiveBuffAuras.Of([32105]));
        Assert.Equal((674, (ushort)2552, 60, true), (born.Template, born.BuffRow, born.Radius, born.Enemies));
        Assert.True(Program.EnemyAuraActs(born));
        Assert.All(PassiveBuffAuras.Of([17315, 27096, 33467]), a => Assert.False(Program.EnemyAuraActs(a)));

        // The aura's buff (BuffDefinitions 2552) goes on a hostile monster within 60 cells and comes off when it leaves.
        foreach (var character in new[] { game, plain })
        {
            var state = character.State;
            var here = state.Walk.Position(state.Clock.Elapsed.TotalSeconds);
            var near = new SpawnedMonster("aura target", new(here.X + 30, here.Y, 30), 0, 2, 1000);
            lock (state.Monsters) { state.Monsters.Clear(); state.Monsters[80] = near; }
            ushort buff = state.NextBuff;
            var add = InstanceEntities.AddBuff(80, buff, 2552, Program.PlayerId);
            Assert.Equal(character == game, (await character.AuraTick()).Bytes.AsSpan().IndexOf(add) >= 0);
            Assert.Empty((await character.AuraTick()).Bytes); // once
            near.Walk = new(here.X + 70, here.Y, 30);
            Assert.Equal(character == game, (await character.AuraTick()).Bytes.AsSpan().IndexOf(InstanceEntities.RemoveBuff(80, buff)) >= 0);
        }
    }

    static Dictionary<uint, int> WarcryOwn(int effect) => Program.Skills.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2);

    sealed partial class GatewayCharacter
    {
        public Task<(string? Error, byte[] Bytes)> WarcryRelease((int X, int Y) at, int radius) => Exchange(async (stream, cipher) =>
        {
            await Program.WarcryReleased("warcry-integration", stream, cipher, State, at, radius, State.ProjectileEpoch);
            return null;
        });

        static readonly MethodInfo GemSkills = typeof(Program).GetMethod("GemSkillEffects", BindingFlags.Static | BindingFlags.NonPublic,
            binder: null, types: [typeof(ClientState)], modifiers: null)!;

        public (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) HeavyStrikeGem(int effect) =>
            ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)GemSkills.Invoke(null, [State])!)
                .Single(g => g.Effect == effect);

        // A gem attack's hit from one cell at another, landing at once (AreaSkillHit, immediate).
        public Task<(string? Error, byte[] Bytes)> HeavyStrike(int effect, (int X, int Y) from, (int X, int Y) to, ushort sequence,
            uint target) => Exchange(async (stream, cipher) =>
        {
            var gem = HeavyStrikeGem(effect);
            await (Task)GatewayMethod("AreaSkillHit").Invoke(null, ["strike-integration", stream, cipher, State, gem.Id, gem,
                from, to, true, sequence, null, null, null, target])!;
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> DefaultAttack(uint target, ushort sequence) => Exchange(async (stream, cipher) =>
        {
            await Program.Attack("strike-integration", stream, cipher, State,
                new InstanceTransfer.UseSkillOnObject(target, SkillCatalog.FirstSkillId, sequence, 0), State.Monsters[target],
                State.Clock.Elapsed.TotalSeconds);
            return null;
        });

        // A monster's physical-only or split hit from a distance in cells, landing (no evasion) at once.
        public Task<(string? Error, byte[] Bytes)> SwingFrom(int distance, DamagePacket hit, bool critical) => Exchange(async (stream, cipher) =>
        {
            State.EvasionEntropy = 99; State.LastEvasionAt = State.Clock.Elapsed.TotalSeconds;
            var position = State.Walk.Position(State.Clock.Elapsed.TotalSeconds);
            var monster = new SpawnedMonster("aura contact", new(position.X + distance, position.Y, 30), 0, 2, 1000)
            {
                Attack = new(SkillCatalog.FirstSkillId, .002, 100, hit),
                Mods = critical ? new([], [], new(new Dictionary<string, int> { ["always_crit"] = 1 })) : MonsterModSet.None,
            };
            lock (State.Monsters) State.Monsters[43] = monster;
            await (Task)GatewayMethod("MonsterSwing").Invoke(null, ["aura-integration", stream, cipher,
                State, 43u, monster, (position.X + distance, position.Y), position, State.IncomingHitEpoch])!;
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> AuraTick() => Exchange(async (stream, cipher) =>
        {
            await Program.TickPassiveAuras("aura-integration", stream, cipher, State);
            return null;
        });

        // A use of Enduring Cry through its release continuation, waiting for its effect.
        public Task<(string? Error, byte[] Bytes)> EnduringCry(int effect, Func<bool> released) => Exchange(async (stream, cipher) =>
        {
            Assert.True(ProgramValue<bool>("UseChargeSkillCore", "warcry-integration", stream, cipher, State, effect, WarcryOwn(effect)));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!released()) await Task.Delay(10, timeout.Token);
            return null;
        });
    }
}
