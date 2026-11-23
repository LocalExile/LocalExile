using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Abyss;

namespace GameServer.Gateway.Tests;

/// <summary>The Abyss encounter: its client-table data, its create blobs against the captures' bodies, the plan, and the
/// whole flow driven by its own tick with game seconds (no waiting).</summary>
public sealed class AbyssEncounterTests
{
    [Fact]
    public void Tables_Blobs_And_Plan_MatchTheClientAndTheCaptures()
    {
        AbyssTables.LoadGameData();
        Assert.Equal(1724u, AbyssTables.AnimRow(AbyssTables.StraightOrthogonal));
        Assert.Equal(1722u, AbyssTables.AnimRow(AbyssTables.StartOrthogonal));
        Assert.Equal(1731u, AbyssTables.AnimRow("AbyssPitThreeOrthogonal"));
        Assert.Equal(694, AbyssTables.ChestIconRow);
        // The reward chest is the captured Chests row; its mods give 300% more items (the capture dropped 4).
        Assert.True(AbyssTables.HasRewardChest);
        Assert.Equal(300, AbyssTables.ChestQuantityPct);
        Assert.Equal(25000, AbyssTables.ChestRarityPct);
        Assert.Equal(new[] { "Pit", "Lightless" }, AbyssTables.Families.Select(f => f.Name).Distinct());
        // The captured families' monsters: the level 45 area's Pit types, the level 61 area's Lightless ones.
        Assert.Contains("Metadata/Monsters/AbyssTwoLeague/PrimordialMonster02", AbyssTables.Families.First(f => f.Name == "Pit").Regular);
        Assert.Contains("Metadata/Monsters/AbyssTwoLeague/PaleWalker01", AbyssTables.Families.First(f => f.Name == "Lightless").Regular);

        // The roll: the chance of CoreLeagues Abyss (a test's base chance) as the map's stats change it, and what switches it off.
        var none = new Dictionary<uint, int>();
        Assert.True(AbyssEncounter.Rolled(none, "MapAtziri", new Random(1), forced: 100));
        Assert.False(AbyssEncounter.Rolled(none, "MapAtziri", new Random(1), forced: 0));
        Assert.False(AbyssEncounter.Rolled(new Dictionary<uint, int> { [Stat.MapDisableBreachAbyss] = 1 }, "MapAtziri", new Random(1), forced: 100));
        Assert.False(AbyssEncounter.Rolled(new Dictionary<uint, int> { [Stat.AtlasKeystoneNoExtraContent] = 1 }, "MapAtziri", new Random(1), forced: 100));
        Assert.False(AbyssEncounter.Rolled(new Dictionary<uint, int> { [Stat.MapDisableLeagues] = 1 << AbyssTables.LeagueRow }, "MapAtziri", new Random(1), forced: 100));
        Assert.False(AbyssEncounter.Rolled(new Dictionary<uint, int> { [Stat.MapExtraContentHasNoBaseChanceToSpawn] = 1 }, "MapAtziri", new Random(1), forced: 100));
        Assert.True(AbyssEncounter.Rolled(new Dictionary<uint, int> { [Stat.MapExtraContentHasNoBaseChanceToSpawn] = 1, [Stat.MapSpawnAbysses] = 1 }, "MapAtziri", new Random(1)));
        Assert.True(AbyssEncounter.Rolled(new Dictionary<uint, int> { [Stat.MapAbyssChancePct] = 100 }, "MapAtziri", new Random(1), forced: 0));
        Assert.Equal(100, AbyssEncounter.ForcedChance("Anarchy=50,Abyss=100"));

        // The captures' bodies for cell (100, 200), world (1150, 2300), rotation pi/4 and the pit id 0x1234.
        Assert.Equal("020064000000c8000000db0f493f0000410000c08f4400c00f4503bc060000880000",
            Convert.ToHexString(AbyssWire.Crack((100, 200), (1150f, 2300f), MathF.PI / 4, 1724, 1, AbyssTables.CrackOpen)).ToLowerInvariant());
        Assert.Equal("020064000000c8000000db0f493f0000400000c08f4400c00f4503ba0600000101000000",
            Convert.ToHexString(AbyssWire.Pit((100, 200), (1150f, 2300f), MathF.PI / 4, 1722)).ToLowerInvariant());
        Assert.Equal("020064000000c8000000db0f493f0000020000002d00000000000000004040010800000000000100",
            Convert.ToHexString(AbyssWire.Chest((100, 200), MathF.PI / 4, 45)).ToLowerInvariant());
        var plinth = AbyssWire.Plinth((100, 198), MathF.PI / 4, 0x1234, AbyssTables.PlinthAo);
        Assert.StartsWith("02029cfcf04000010134120000", Convert.ToHexString(plinth).ToLowerInvariant());
        Assert.Contains(Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(AbyssTables.PlinthAo)), Convert.ToHexString(plinth));
        // The click carries the standard order (0, 0), so the frame keeps it ahead of the open.
        Assert.Equal("01bd000002a600000000000001000000000227", Convert.ToHexString(AbyssWire.ChestClick(0x2a6, 0x227)).ToLowerInvariant());

        // The pit stands on a lattice point at most SiteRadius steps from the spot's (cell (471, 471) is point (41, 41)), on
        // that point's cell, each arm's first piece two lattice steps (23 cells) out, every piece inside the map, and the
        // plan holds the captures' 25 arm monsters.
        Assert.Equal((471, 471), AbyssEncounter.LatCell((41, 41)));
        var plan = AbyssEncounter.MakePlan(Flat(1200), (471, 471), new Random(7))!;
        Assert.InRange(plan.PitLattice.X, 41 - AbyssTables.SiteRadius, 41 + AbyssTables.SiteRadius);
        Assert.InRange(plan.PitLattice.Y, 41 - AbyssTables.SiteRadius, 41 + AbyssTables.SiteRadius);
        var (px, py) = plan.PitLattice;
        Assert.InRange(plan.Arms.Count, 1, AbyssTables.MaxArms);
        (int X, int Y)[] dirs = [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)];
        foreach (var arm in plan.Arms)
        {
            Assert.InRange(arm.Cells.Count, AbyssTables.ArmMin, AbyssTables.ArmLenMax);
            var (dx, dy) = dirs[arm.Dir];
            Assert.Equal(AbyssEncounter.LatCell((px + 2 * dx, py + 2 * dy)), arm.Cells[0]);
            Assert.All(arm.Cells, c => Assert.True(c.X >= 0 && c.Y >= 0 && c.X < 1200 && c.Y < 1200, $"{c} is inside the map"));
        }
        Assert.Equal(AbyssTables.ArmMonsters, plan.Slots.Count);
        Assert.All(plan.Slots, s => Assert.InRange(s.Closing, 0, plan.Arms[s.Arm].Cells.Count - 2));
        Assert.InRange(plan.Wave.Count, AbyssTables.WaveMin, AbyssTables.WaveMax);
        Assert.Equal(AbyssTables.RewardChest, plan.Chest);
        // An arrival that reaches nothing leaves no site.
        Assert.Null(AbyssEncounter.MakePlan(Flat(1200), (471, 471), new Random(7), _ => false));
    }

    [Fact]
    public async Task The_encounter_runs_from_the_reveal_to_the_chest_drop()
    {
        AbyssTables.LoadGameData();
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        Game.CombatLifetime = new ModuleLifetime("Game");
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        using var wire = new PartyFollowerTests.Wire();
        var player = new Player(Flat(1200), 1, 3316637604u) { Instance = new() };
        try
        {
            player.Connection.Session = new("abyss", wire.Stream, wire.Ciphers);
            player.Connection.PlayerSent = true;
            player.PlayerLife = 1000;
            var encounter = new AbyssEncounter();
            var rng = new Random(11);
            var state = encounter.Begin(player, AbyssEncounter.MakePlan(player.Layout, (300, 300), rng)!, 45, rng);
            // The encounter is the instance's: another player of it reads the same one.
            var second = new Player(Flat(1200), 2, 3316637604u) { Instance = player.Instance };
            Assert.Same(state, encounter.Of(second));
            double Now() => Game.AreaTime(player);
            double Distance((int X, int Y) a, (int X, int Y) b) => double.Hypot(a.X - b.X, a.Y - b.Y);
            void Kill(IEnumerable<uint> ids) { foreach (uint id in ids) if (player.Area.Monsters.TryGetValue(id, out var m)) m.Life = 0; }
            // What was sent since a mark, from the connection's packet history: id, length and the first 48 bytes.
            var history = Program.HistoryFor(wire.Ciphers);
            List<(ushort Id, int Length, byte[] Head)> Since(long mark) => [.. history.Lines((int)Math.Min(PacketHistory.Capacity, history.Count - mark))
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Select(p => (Convert.ToUInt16(p[2][2..], 16), int.Parse(p[3]), Convert.FromHexString(p[5].TrimEnd('\u2026'))))];
            static uint Entity(byte[] head) => BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(2));
            long mark;

            // Far away nothing is created; near, the pit and the pieces within reach of the player are (0x2bb), open, and the
            // rest follow as the player walks up. The pit's create carries its cell and its lattice point's world position.
            player.Walk.Teleport(1190, 1190, Now());
            await encounter.Advance(player, 0.25);
            Assert.Equal(0u, state.Pit);
            player.Walk.Teleport(state.PitCell.X, state.PitCell.Y, Now());
            mark = history.Count;
            await encounter.Advance(player, 0.25);
            Assert.Equal(AbyssEncounter.Stage.Arms, state.Stage);
            var creates = Since(mark).Where(m => m.Id == 0x2bb).ToList();
            Assert.Equal((state.Pit != 0 ? 1 : 0) + state.Arms.Sum(a => a.Pieces.Count(p => p.Id != 0)), creates.Count);
            Assert.NotEqual(0u, state.Pit);
            var (_, pitLength, pit) = creates.Single(m => Entity(m.Head) == state.Pit);
            Assert.Equal(18 + 36, pitLength);
            Assert.Equal(state.PitCell, (BinaryPrimitives.ReadInt32LittleEndian(pit.AsSpan(20)), BinaryPrimitives.ReadInt32LittleEndian(pit.AsSpan(24))));
            Assert.Equal(state.Plan.PitLattice.X * 125f, BinaryPrimitives.ReadSingleLittleEndian(pit.AsSpan(36)));

            // Standing at the pit starts nothing; a kill beside the first arm starts it, and with it closing, the others
            // the player is near. Each closing piece brings its arm monsters 3-8 cells from it, all 25 of the family.
            for (int tick = 0; tick < 4; tick++) await encounter.Advance(player, 1.0);
            Assert.All(state.Arms, arm => Assert.Null(arm.StartedAt));
            encounter.Killed(new KillContext(player, null!, "", state.Arms[0].Cells[^1]));
            await encounter.Advance(player, 0.25);
            Assert.NotNull(state.Arms[0].StartedAt);
            for (int tick = 0; tick < 24; tick++) await encounter.Advance(player, 1.0);
            Assert.All(state.Arms, arm => Assert.NotNull(arm.StartedAt));
            Assert.All(state.Arms, arm => Assert.All(arm.Pieces.Skip(1), p => Assert.Equal(AbyssTables.CrackClosing3, p.State)));
            Assert.All(state.Slots, slot => Assert.True(slot.Spawned));
            Assert.Equal(AbyssTables.ArmMonsters, state.ArmMonsters.Count);
            var family = AbyssTables.Families.First(f => f.Name == state.Plan.Family);
            Assert.All(state.ArmMonsters, id => Assert.Contains(player.Area.Monsters[id].Variety!.Id, family.Regular));
            Assert.All(state.ArmMonsters, id => Assert.InRange(state.Arms.SelectMany(a => a.Cells)
                .Min(c => Distance(player.Area.Monsters[id].Walk.Position(Now()), c)), 0, AbyssTables.ArmMonsterDistanceMax + 1));

            // They die: the pit opens (Transitionable 2), its side pieces close a frame later, the wave comes 13-26 cells
            // from the pit.
            Kill(state.ArmMonsters);
            mark = history.Count;
            await encounter.Advance(player, 0.25);
            Assert.Equal(AbyssEncounter.Stage.Wave, state.Stage);
            // 0x2bc: the header, the blob's length, the component (Transitionable, 3) and its state.
            Assert.Contains(Since(mark), m => m.Id == 0x2bc && Entity(m.Head) == state.Pit && m.Head[^1] == AbyssTables.PitOpening);
            for (int tick = 0; tick < 4; tick++) await encounter.Advance(player, 1.0);
            Assert.All(state.Arms, arm => Assert.True(arm.Pieces[0].State >= AbyssTables.CrackClosing1));
            Assert.Equal(state.Plan.Wave.Count, state.WaveSpawned);
            Assert.InRange(state.WaveMonsters.Count, 1, AbyssTables.WaveMax);
            Assert.All(state.WaveMonsters, id => Assert.InRange(Distance(player.Area.Monsters[id].Walk.Position(Now()), state.PitCell),
                AbyssTables.WaveDistanceMin - 1, AbyssTables.WaveDistanceMax + 1));

            // The wave dies: the plinth and the chest at the pit; 91 frames on the chest is ready.
            Kill(state.WaveMonsters);
            mark = history.Count;
            await encounter.Advance(player, 0.25);
            Assert.Equal(AbyssEncounter.Stage.Reward, state.Stage);
            var rewardCreates = Since(mark).Where(m => m.Id == 0x2bb).ToList();
            Assert.Contains(rewardCreates, m => Entity(m.Head) == state.Plinth);
            var chest = rewardCreates.Single(m => Entity(m.Head) == state.Chest).Head;
            Assert.Equal(state.PitCell, (BinaryPrimitives.ReadInt32LittleEndian(chest.AsSpan(20)), BinaryPrimitives.ReadInt32LittleEndian(chest.AsSpan(24))));
            var use = new InstanceTransfer.UseSkillOnObject(state.Chest, 0x0266, 0, 0x0408) { Started = true };
            // Not ready yet: the click opens nothing.
            Assert.True(await encounter.OnUse(player, use, Now()));
            Assert.False(state.ChestOpened);
            for (int tick = 0; tick < 4; tick++) await encounter.Advance(player, 1.0);
            Assert.True(state.ChestReady);

            // The click at the chest: the 0x1bd click, 05 and Transitionable 3, the reward on the floor; then it is over.
            mark = history.Count;
            Assert.True(await encounter.OnUse(player, use, Now()));
            Assert.True(state.ChestOpened);
            var opened = Since(mark);
            Assert.Single(opened, m => m.Id == 0x1bd && Entity(m.Head) == state.Chest);
            Assert.Contains(opened, m => m.Id == 0x2bc && Entity(m.Head) == state.Chest && m.Head[^1] == AbyssTables.ChestOpenedState);
            uint item = InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath);
            Assert.Equal(4, opened.Count(m => m.Id == 0x2bb && BinaryPrimitives.ReadUInt32BigEndian(m.Head.AsSpan(12)) == item));
            await encounter.Advance(player, 0.25);
            Assert.Equal(AbyssEncounter.Stage.Over, state.Stage);
        }
        finally
        {
            player.Closed = true;
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
        }
    }

    // A flat area where every cell is floor.
    static AreaLayout Flat(int size) => new(0x1234, "MapWorldsReef", size, size, size / 2, size / 2, (_, _) => true, []);
}
