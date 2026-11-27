using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class TormentTests
{
    static MapDevice.Slot Slot(string leaf)
    {
        var type = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find("Metadata/Items/Scarabs/" + leaf));
        return new(type.Hash, type.Class, type.Name, type.Row, [], 0) { BaseId = type.Id };
    }

    [GameFilesFact]
    public async Task Tormented_spirits_roll_from_the_scarabs_touch_possess_pay_out_and_come_back_with_a_kept_instance()
    {
        Assert.Equal(22, TormentCatalog.Pool(70, unusual: false).Count);
        Assert.Equal(["Metadata/Monsters/Spirit/TormentedBetrayer", "Metadata/Monsters/Spirit/TormentedFelon",
            "Metadata/Monsters/Spirit/TormentedTrespasser"], TormentCatalog.Pool(70, unusual: true).Select(s => s.Variety).Order());
        Assert.Equal((0, 120, 880), (TormentCatalog.LifePct(55), TormentCatalog.LifePct(61), TormentCatalog.LifePct(83)));

        // Two Torment Scarabs (4 spirits each) and the Peculiarity's unusual spirits: every stat has a provider.
        var catalog = MapRunCatalog.Load("data");
        MapDevice.Slot[] slots = [Slot("ScarabTormentNew1"), Slot("ScarabTormentNew1"), Slot("ScarabTormentNew2")];
        var mods = catalog.Of(slots, Program.Items.Mods);
        Assert.Equal(8, mods.Get(Stat.MapSpawnExtraTormentSpirits));
        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var opening = new MapOpening(new(new(1, "torment", 100, 100, 10, 10, null, []), 1, 1), slots, false,
            new(new(1, area.Id, "Strand Map", 3) { Level = 70 }, mods), "");
        MapScarabContent.Describe(opening);
        MapTorment.Opening(opening);
        Assert.Contains("Torment", opening.HandledContent);
        Assert.Empty(opening.UnhandledContent);

        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), new HashSet<string>(), -1));
        uint seed = ServerLevels.PlayableSeed(area.Id, 0x7041, 70, stats);
        Assert.IsType<ServerLevels.Level>(ServerLevels.For(area.Id, seed, 70));
        var layout = AreaLayouts.For(area.Hash16, seed);
        instance.OpenedWith(new("TormentOwner", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, mods));

        TestEnvironment.OpenOwnSaves("torment");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        using var life = MonsterModEffects.HandOverMore([MapTorment.LifeStat], [], [MapTorment.TaperStat]);
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        await using var peer = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        instance.World.Runner = owner.Player;
        try
        {
            MapTorment.Spirit[] Spirits() => JsonSerializer.Deserialize<MapTorment.Spirit[]>(instance.ContentState["Torment/spirits"])!;
            await MapTorment.Enter(owner.Player);
            var spirits = Spirits();
            Assert.InRange(spirits.Length, 8, 10);
            Assert.All(spirits, s => Assert.True(TormentCatalog.ByRow(s.Row)!.Unusual));
            await MapTorment.Enter(peer.Player);
            Assert.Equal(spirits.Select(s => s.Monster), Spirits().Select(s => s.Monster));
            var spirit = spirits[0];
            var body = owner.Player.Area.Monsters[spirit.Monster];
            Assert.Equal((MonsterRarity.Unique, true), (body.Rarity, body.Scripted));
            Assert.Equal(-90, body.Effects[MapTorment.TaperStat]);
            Assert.Equal(0.1, body.Effects.DamageTaken(false), 6);

            // The peer is not the monster runner: standing on a spirit, its tick wakes nothing.
            peer.Player.Walk.Teleport(spirits[1].X, spirits[1].Y, 0);
            await MapTorment.Tick(peer.Player, 0.25);
            Assert.Equal(MapTorment.SpiritState.Waiting, Spirits()[1].State);

            // A normal and a rare monster where the spirit is, and the player close enough to wake it: in one tick it
            // touches the normal one and goes into the rare one.
            var member = Game.Packs.For(area.Id)!.Scattered.SelectMany(p => p.Pack.Entries)
                .Select(e => Game.Monsters.Find(e.Variety.ToString())).First(v => v is { Supported: true })!;
            uint normal = await Game.SpawnMonster(owner.Player, member, 70, MonsterRarity.Normal,
                spirit.X, spirit.Y, unrisen: false, placement: Game.MonsterPlacement.Established);
            uint rare = await Game.SpawnMonster(owner.Player, member, 70, MonsterRarity.Rare,
                spirit.X, spirit.Y, unrisen: false, placement: Game.MonsterPlacement.Established);
            owner.Player.Walk.Teleport(spirit.X + 10, spirit.Y, 0);
            await MapTorment.Tick(owner.Player, 0.25);
            Assert.Equal(MapTorment.SpiritState.Possessed, Spirits()[0].State);
            Assert.Equal(1, Spirits()[0].Touches);
            Assert.False(owner.Player.Area.Monsters.ContainsKey(spirit.Monster));
            var touched = owner.Player.Area.Monsters[normal];
            var possessed = owner.Player.Area.Monsters[rare];
            Assert.True(TormentCatalog.Has(touched.Mods, grip: false));
            Assert.True(TormentCatalog.Has(possessed.Mods, grip: true));
            Assert.Equal(2500, touched.Effects[Stat.FromLeagueItemQuantityPlusPctPermyriad]);
            Assert.True(possessed.Effects[Stat.FromLeagueItemRarityPlusPctPermyriad] >= 3000);
            Assert.Equal(touched.Mods.Mods, instance.World.MonsterOrigins[normal].Object.Mods);

            // A kill (any kind: GameEvents.Kill) claims the payout once; the kill's own drop roll still sees the mark,
            // and the next tick drops the possessed rare's two rares and a slain spirit's rare.
            int ground = instance.Ground.All().Count();
            var killed = new KillContext(owner.Player, possessed, possessed.Variety!.Id, (spirit.X, spirit.Y)) { PlayerKill = false };
            MapTorment.Kill(killed);
            MapTorment.Kill(killed);
            await MapTorment.Tick(owner.Player, 0.25);
            Assert.InRange(instance.Ground.All().Skip(ground).Count(g => g.Item.Item.Rarity is ItemRarity.Rare or ItemRarity.Unique), 2, 3);
            ground = instance.Ground.All().Count();
            var other = owner.Player.Area.Monsters[spirits[1].Monster];
            MapTorment.Kill(new(owner.Player, other, other.Variety!.Id, (spirits[1].X, spirits[1].Y)));
            await MapTorment.Tick(owner.Player, 0.25);
            Assert.Equal(MapTorment.SpiritState.Slain, Spirits()[1].State);
            Assert.Contains(instance.Ground.All().Skip(ground), g => g.Item.Item.Rarity is ItemRarity.Rare or ItemRarity.Unique);

            // A kept instance brings its monsters back under new ids and without their script: the spirits that are
            // still waiting are made again, the stray copy goes.
            uint waiting = spirits[2].Monster;
            uint stray, again;
            int touchedMods = touched.Mods.Mods.Count;
            var kept = owner.Player.Area.Monsters[waiting];
            owner.Player.Area.Monsters.Remove(waiting);
            stray = owner.Player.Instance.World.NewMonsterId();
            owner.Player.Area.Monsters[stray] = kept;
            owner.Player.Area.Monsters.Remove(normal);
            again = owner.Player.Instance.World.NewMonsterId();
            owner.Player.Area.Monsters[again] = touched;
            MapTorment.Leave(owner.Player);
            await MapTorment.Enter(owner.Player);
            var respawned = Spirits()[2];
            Assert.NotEqual(waiting, respawned.Monster);
            Assert.True(owner.Player.Area.Monsters[respawned.Monster].Scripted);
            Assert.False(owner.Player.Area.Monsters.ContainsKey(stray));
            // The touched monster came back with its Touch in its kept mods: marked again, not touched again.
            await MapTorment.Tick(owner.Player, 0.25);
            Assert.Contains(again, JsonSerializer.Deserialize<Dictionary<uint, JsonElement>>(instance.ContentState["Torment/marks"])!.Keys);
            Assert.Equal(touchedMods, touched.Mods.Mods.Count);
        }
        finally
        {
            PlayerInventories.Forget(owner.Player.Character.Name!);
            PlayerInventories.Forget(peer.Player.Character.Name!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    [GameFilesFact]
    public async Task Atlas_and_map_torment_stats_possess_rares_bosses_and_players_and_dig_up_a_monster()
    {
        // The Atlas tree's Seance and Speaker of the Dead (composed as the tree's stats), and map stats (the Idols' are
        // the same stats, added to the map's): an extra spirit, the Enthralled boss, spirit speed, Blessed possessed
        // monsters and the Torment Scarab's release and dig.
        var atlas = new Dictionary<uint, int>
        {
            [Stat.MapRareMonstersPossessedWithTouchedMinionsChancePct] = 100,
            [Stat.MapRareMonstersPossessedWithTouchedMinionsNum] = 1,
            [Stat.AtlasKeystoneSpeakerOfTheDead] = 1,
        };
        var mods = new MapMods(new Dictionary<uint, int>
        {
            [Stat.MapSpawnExtraTormentSpirits] = 1, [Stat.MapBossIsPossessed] = 1, [Stat.MapTormentedSpiritsMovementSpeedPlusPct] = 30,
            [Stat.MapMonsterDamageTakenPlusPctWhilePossessed] = 20, [Stat.MapTormentedSpiritReleaseAndDigPctChance] = 100,
        });
        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, atlas, new HashSet<string>(), -1));
        uint seed = ServerLevels.PlayableSeed(area.Id, 0x7042, 70, stats);
        Assert.IsType<ServerLevels.Level>(ServerLevels.For(area.Id, seed, 70));
        var layout = AreaLayouts.For(area.Hash16, seed);
        instance.OpenedWith(new("TormentAtlas", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, mods));

        TestEnvironment.OpenOwnSaves("torment-atlas");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        using var life = MonsterModEffects.HandOverMore([MapTorment.LifeStat], [], [MapTorment.TaperStat]);
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        owner.Player.Clock.Start();
        instance.World.Runner = owner.Player;
        try
        {
            await MapTorment.Enter(owner.Player);
            var spirit = JsonSerializer.Deserialize<MapTorment.Spirit[]>(instance.ContentState["Torment/spirits"])![0];
            Assert.Equal("1", instance.ContentState["Torment/seance"]);
            var body = owner.Player.Area.Monsters[spirit.Monster];
            Assert.True(body.Effects[Stat.BaseMovementVelocityPlusPct] >= 20); // the map's 30, less a spirit's own 10

            // Far from the spirit: a rare with a minion (Seance) and the map's boss (Enthralled).
            var packs = Game.Packs.For(area.Id)!;
            var member = packs.Scattered.SelectMany(p => p.Pack.Entries).Select(e => Game.Monsters.Find(e.Variety.ToString()))
                .First(v => v is { Supported: true })!;
            var bossVariety = packs.Bosses.Select(row => Game.Monsters.Find(row.ToString())).First(v => v is { Supported: true })!;
            var far = layout.Near((spirit.X > layout.GridW / 2 ? spirit.X - 150 : spirit.X + 150, spirit.Y));
            uint minion = await Game.SpawnMonster(owner.Player, member, 70, MonsterRarity.Normal,
                far.X + 3, far.Y, unrisen: false);
            uint rare = await Game.SpawnMonster(owner.Player, member, 70, MonsterRarity.Rare,
                far.X, far.Y, unrisen: false);
            uint boss = await Game.SpawnMonster(owner.Player, bossVariety, 70, MonsterRarity.Unique,
                far.X, far.Y + 20, unrisen: false);
            owner.Player.Walk.Teleport(spirit.X, spirit.Y, 0);
            await MapTorment.Tick(owner.Player, 0.25);
            Assert.True(TormentCatalog.Has(owner.Player.Area.Monsters[rare].Mods, grip: true));
            Assert.True(owner.Player.Area.Monsters[rare].Effects[Stat.BaseDamageTakenPlusPct] >= 20);
            Assert.True(TormentCatalog.Has(owner.Player.Area.Monsters[minion].Mods, grip: false));
            Assert.True(TormentCatalog.Has(owner.Player.Area.Monsters[boss].Mods, grip: true));
            Assert.Equal("0", instance.ContentState["Torment/seance"]);

            // Speaker of the Dead: the spirit the player stands on goes into the player, who gets its Possessed buff.
            var after = JsonSerializer.Deserialize<MapTorment.Spirit[]>(instance.ContentState["Torment/spirits"])![0];
            Assert.Equal(MapTorment.SpiritState.Possessed, after.State);
            Assert.False(owner.Player.Area.Monsters.ContainsKey(spirit.Monster));
            var player = MapTorment.PlayerStats(owner.Player, []);
            Assert.True(player.GetValueOrDefault(Stat.AttackSpeedPlusPct) >= 35);
            Assert.DoesNotContain(Stat.MonsterIsPossessed, player.Keys);
            MapTorment.Leave(owner.Player);
            Assert.Empty(MapTorment.PlayerStats(owner.Player, []));

            // The possessed rare dies: its spirit is set free and digs up another possessed monster.
            int spirits = JsonSerializer.Deserialize<MapTorment.Spirit[]>(instance.ContentState["Torment/spirits"])!.Length;
            var possessed = owner.Player.Area.Monsters[rare];
            possessed.Dead = true;
            MapTorment.Kill(new(owner.Player, possessed, possessed.Variety!.Id, (far.X, far.Y)));
            // The test client also runs while any additional natural spirit steers before this release.
            await MapTorment.Tick(owner.Player, 0.25);
            Assert.Equal(spirits + 1, JsonSerializer.Deserialize<MapTorment.Spirit[]>(instance.ContentState["Torment/spirits"])!.Length);
            Assert.Contains(owner.Player.Area.Monsters, p => p.Key > boss && !p.Value.Dead && p.Value.Rarity == MonsterRarity.Rare
                && TormentCatalog.Has(p.Value.Mods, grip: true));
        }
        finally
        {
            PlayerInventories.Forget(owner.Player.Character.Name!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }
}
