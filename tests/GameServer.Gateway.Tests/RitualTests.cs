using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using GameServer.Modules.Ritual;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

/// <summary>The Ritual module against the official captures: the objects' bodies and the messages byte for byte, the
/// client's tables it reads, and an area's altars from its league roll through the guards' deaths, the click, the waves and
/// the end.</summary>
public class RitualTests
{
    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>The bodies and messages as the captures show them (cell (512, 446), rotation pi/4), and the tables.</summary>
    [Fact]
    public void The_bodies_the_messages_and_the_tables_match_the_captures()
    {
        var cell = (512, 446);
        float rot = MathF.PI / 4;
        string altar = Hex(RitualWire.Interactable(cell, rot, 62, 0, 0, 0));
        Assert.Equal(320, altar.Length);
        Assert.StartsWith("000246a37a020001083e00000034610e460001090000000000", altar);
        Assert.EndsWith("0317140000000200000000000000", altar);
        // The light (32 bytes, 44 with a scale), the pool (32) and the blocker (34): their heads and lengths.
        string light = Hex(RitualWire.Light(cell, rot, 0, null)), scaled = Hex(RitualWire.Light(cell, rot, 2, (float)(1 + 8.46 * 0.5)));
        Assert.Equal(64, light.Length);
        Assert.Equal(88, scaled.Length);
        Assert.StartsWith("000000020000be010000db0f493f0000", light);
        Assert.StartsWith("000000020000be010000db0f493f00002400", scaled);
        string pool = Hex(RitualWire.Pool(cell, rot, 0x1234)), blocker = Hex(RitualWire.Blocker(cell, 87));
        Assert.Equal(64, pool.Length);
        Assert.StartsWith("02011cd9231700010134120000", pool);
        Assert.Equal(68, blocker.Length);
        Assert.StartsWith("020000020000be0100000000000000002000", blocker);
        var states = new BlobWriter();
        new StateMachineData([3, 1], 0).Write(states);
        Assert.Equal("020003000100", Hex(states.ToArray()));
        Assert.Equal("01650000123400000200000001be02", Hex(RitualWire.AltarState(0x1234, cell, 2)));
        Assert.Equal("016700000128", Hex(RitualWire.Tribute(296)));
        // The favours window at level 77: 20 rows, wave 2 is priced once two altars are done.
        var favours = RitualWire.Favours(0, RitualTables.FavoursRows(0), 3, 0);
        Assert.Equal(2 + 4 + 2 + 20 * 9 + 2, favours.Length);
        Assert.Equal(2 + 4 + 2 + 20 * 9 + 2, RitualWire.Favours(0, RitualTables.FavoursRows(2), 3, 2).Length);
        // The environment on and off as the captures send them (the off value's bits are 0xb3d6bf95).
        Assert.Equal("00df0104ff0000000000", Hex(InstanceEnvironments.Data((1279, 0))));
        Assert.Equal("00df0104ff00b3d6bf95", Hex(InstanceEnvironments.Data((1279, RitualTables.EndEnvironmentSeconds))));

        // The client's tables: 3..3 altars below level 46 and 3..4 from it, the circle 87 cells, the tribute of the 1st to
        // 4th revive of each rarity (100 %, 60 %, 20 %, 0 %).
        Assert.Equal((3, 3), RitualTables.AltarRange(45));
        Assert.Equal((3, 4), RitualTables.AltarRange(46));
        Assert.Equal(87, RitualTables.RadiusCells);
        Assert.Equal(new[] { 10, 6, 2, 0, 26, 15, 5, 0, 135, 81, 27, 0, 400, 240, 80, 0 },
            new[] { MonsterRarity.Normal, MonsterRarity.Magic, MonsterRarity.Rare, MonsterRarity.Unique }
                .SelectMany(r => Enumerable.Range(1, 4).Select(n => RitualTables.TributeOf(r, n))));
        // Every rune carries a buff row or mods; Fire1's buff is ritual_rune_fire1 (25% ignite chance, +20% physical as
        // fire) with the drop-penalty mod, and the Charged runes' effect is their three charge mods.
        Assert.Equal(82, RitualTables.Runes.Count);
        Assert.All(RitualTables.Runes, r => Assert.True(r.BuffRow >= 0 || r.ModRows.Count > 0));
        var fire = RitualTables.Runes.Single(r => r.Id == "Fire1");
        Assert.Equal([(uint)614, (uint)450], fire.BuffStats.Select(s => s.Stat));
        Assert.Equal([25, 20], fire.BuffStats.Select(s => s.Value));
        Assert.Equal([37430], fire.ModRows);
        var charged = RitualTables.Runes.Single(r => r.Id == "GenericCharges1");
        Assert.Empty(charged.BuffStats);
        Assert.Equal(3, RitualTables.RuneModSet(charged, 77)!.Mods.Count);
        // A daemon variety is a monster only when its type extends a monster base.
        Assert.True(RitualTables.IsMonsterType(Game.Monsters.Find("PhysicalBleedTotem")!));
        Assert.False(RitualTables.IsMonsterType(Game.Monsters.Find("GoldenCoinDaemon")!));
    }

    /// <summary>A map whose stats make Ritual certain places its altars; the guards show when the player comes near, and
    /// killing them readies the altar, the click starts it, the killed monsters come back 60 frames on, each revived kill
    /// adds tribute (10 for a normal monster), and the last one ends it.</summary>
    [GameFilesFact]
    public async Task An_altar_is_ready_when_its_guards_die_and_its_start_revives_them_for_tribute()
    {
        var mods = new MapMods(new Dictionary<uint, int> { [Stat.MapAreaRitualAdditionalChancePct] = 100 });
        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), new HashSet<string>(), -1));
        uint seed = ServerLevels.PlayableSeed(area.Id, 0x7269, 70, stats);
        Assert.IsType<ServerLevels.Level>(ServerLevels.For(area.Id, seed, 70));
        var layout = AreaLayouts.For(area.Hash16, seed);
        instance.OpenedWith(new("RitualMap", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, mods));

        TestEnvironment.OpenOwnSaves("ritual");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        instance.World.Runner = owner.Player;
        try
        {
            var player = owner.Player;
            var encounter = new RitualEncounter();
            Assert.Null(encounter.Populate(new AreaPopulated(player, 70, 1234, [])));
            await encounter.Placed(player);
            var state = encounter.Of(player)!;
            // The area's spawns share one mod set, resolved once at placement.
            var placed = RitualTables.RuneModSet(state.Altars[0].Rune, state.Level);
            Assert.Equal(placed is null, state.ModSet is null);
            Assert.Equal(placed?.Mods.Count, state.ModSet?.Mods.Count);
            // The Ritual is the instance's: a second player of it reads the same one, and is told the entry again.
            var second = new Player(layout, 2, seed) { Instance = player.Instance };
            Assert.Same(state, encounter.Of(second));
            Assert.Equal(2, encounter.EnterMessages(second).Count());
            Assert.NotNull(encounter.Populate(new AreaPopulated(player, 70, 1234, [])));   // placed once per instance
            var (lo, hi) = RitualTables.AltarRange(70);
            Assert.InRange(state.Altars.Count, lo, hi);
            Assert.All(state.Altars, a => Assert.True(a.Cell.X >= 0 && a.Cell.Y >= 0 && a.Cell.X < layout.GridW && a.Cell.Y < layout.GridH));
            Assert.Single(state.Altars.Select(a => a.Rune.Row).Distinct());

            // The instance's clock runs: a paused instance does not step.
            player.Clock.Start();
            // The player stands beside the first altar: it shows with its guards tied to it.
            var altar = state.Altars[0];
            altar.Cell = layout.Near((layout.SpawnX + 10, layout.SpawnY));
            await encounter.Advance(player, 0.25);
            Assert.True(altar.Shown);
            Assert.NotEqual(0u, altar.Interactable);
            int guards = altar.Tied.Count;
            Assert.True(guards > 0);
            Assert.All(altar.Tied, id => Assert.True(player.Area.Monsters.TryGetValue(id, out var m) && m.Life > 0));
            Assert.Equal(RitualEncounter.Inactive, altar.State);

            foreach (uint id in altar.Tied) player.Area.Monsters[id].Life = 0;
            await encounter.Advance(player, 0.25);
            Assert.Equal(RitualEncounter.Ready, altar.State);
            Assert.Equal(guards, altar.Credit.Count);
            Assert.NotEmpty(altar.Pools);

            var use = new InstanceTransfer.UseSkillOnObject(altar.Interactable, 0x0266, 0, 0x0408) { Started = true };
            player.Walk.Teleport(altar.Cell.X, altar.Cell.Y, Game.AreaTime(player));
            Assert.True(await encounter.OnUse(player, use, Game.AreaTime(player)));
            Assert.Equal(RitualEncounter.Running, altar.State);
            var run = state.Run!;
            Assert.Equal(guards, run.Monsters.Count);
            // A second click while it runs, and a client entering again, change nothing.
            Assert.True(await encounter.OnUse(player, use, Game.AreaTime(player)));
            Assert.Same(run, state.Run);
            await encounter.Entered(second);
            await encounter.Entered(player);

            // Nothing comes back before the 60th frame; then the wave spawns one every 2.5 frames.
            for (int f = 0; f < 59; f++) await encounter.Advance(player, 1 / 30.0);
            Assert.Equal(0, run.Next);
            for (int f = 0; f < 60 + 3 * guards; f++) await encounter.Advance(player, 1 / 30.0);
            Assert.Equal(guards, run.Next);
            Assert.NotEmpty(run.Alive);

            int revived = run.Alive.Count;
            foreach (uint id in run.Alive.Keys) player.Area.Monsters[id].Life = 0;
            await encounter.Advance(player, 1 / 30.0);
            Assert.Equal(10 * revived, state.Tribute);
            Assert.Equal(RitualEncounter.Done, altar.State);
            Assert.Null(state.Run);
            Assert.Equal(1, state.Completed);
        }
        finally
        {
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

}
