using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using GameServer.Modules.Mercenary;

namespace GameServer.Gateway.Tests;

public sealed class MercenaryTests
{
    // The area's own Mercenaries chance (CoreLeagues row 28 is never rolled by LeagueChances.Count, so the module reads the
    // WorldAreaLeagueChances column itself): 8 in a map area, 0 in a town, and the map's stats force, add to, scale or remove it.
    [Fact]
    public void A_map_area_rolls_the_Mercenaries_column_and_the_maps_stats_change_it()
    {
        MercenaryModule.LoadData();
        Assert.Equal(8, LeagueChances.Current.ChanceOf("MapWorldsStrand", LeagueChances.Mercenaries).Chance);
        Assert.Equal(8, MercenaryModule.ChanceOf("MapWorldsStrand", false, new Dictionary<uint, int>()));
        Assert.Equal(0, MercenaryModule.ChanceOf("1_1_town", false, new Dictionary<uint, int>()));
        Assert.Equal(36, MercenaryModule.ChanceOf("MapWorldsStrand", false, new Dictionary<uint, int>
            { [Stat.MapMercenaryAdditionalChancePct] = 10, [Stat.MapMercenaryChancePlusPct] = 100 }));
        Assert.Equal(100, MercenaryModule.ChanceOf("MapWorldsStrand", false, new Dictionary<uint, int> { [Stat.MapMercenaryLeague] = 1 }));
        Assert.Equal(0, MercenaryModule.ChanceOf("MapWorldsStrand", false, new Dictionary<uint, int>
            { [Stat.MapMercenaryLeague] = 1, [Stat.MapDisableMercenary] = 1 }));
        Assert.Equal(0, MercenaryModule.ChanceOf("MapWorldsStrand", false, new Dictionary<uint, int>
            { [Stat.MapDisableLeagues] = 1 << LeagueChances.Mercenaries }));
        // The duel choice is an action of MercenaryEncounterRules, Rulesets row 0xa1: row, payload length, eight bytes.
        Assert.Equal((byte?)0xa1, MercenaryTables.EncounterRulesRow);
        var (row, payload) = InstanceRulesets.ReadAction(Convert.FromHexString("02CA" + "A1" + "08" + "0101010000000000"));
        Assert.Equal(0xa1, row);
        Assert.Equal(MercenaryWire.DuelChoice, payload);
    }

    // One mercenary of a new map instance, from the roll to the hire: placed once for the instance, given to a player who enters
    // later, pressed, chosen, replaced by its hostile duel opponent after the countdown, and left as the hired one when that dies.
    [GameFilesFact]
    public async Task A_mercenary_is_placed_for_the_instance_talked_to_duelled_and_hired()
    {
        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var mods = new MapMods(new Dictionary<uint, int> { [Stat.MapMercenaryLeague] = 1,
            [Stat.MapMercenariesAreInfamous] = 1, [Stat.MapMercanaryAccompaniedByParty] = 1, [Stat.MapMercenaryAllUniqueItems] = 1 });
        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), new HashSet<string>(), -1));
        uint seed = ServerLevels.PlayableSeed(area.Id, 0x7061, 70, stats);
        var layout = AreaLayouts.For(area.Hash16, seed);
        instance.OpenedWith(new("MercenaryOwner", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, mods));

        TestEnvironment.OpenOwnSaves("mercenary");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        var module = new ModuleLifetime("Mercenary");
        new MercenaryModule().Load(Context(module));
        await using var owner = await Open(instance, layout, seed);
        await using var peer = await Open(instance, layout, seed);
        instance.World.Runner = owner.Player;
        try
        {
            MercenaryModule.Run Read() => JsonSerializer.Deserialize<MercenaryModule.Run>(instance.ContentState["Mercenary.Encounter"])!;
            await GameEvents.RunAreaPopulated(new AreaPopulated(owner.Player, 70, (int)seed, []));
            var placed = Read();
            Assert.Equal(MercenaryModule.Stage.Met, placed.Stage);
            Assert.True(MercenaryTables.BuildOf(placed.Build)!.Infamous);
            Assert.All(placed.Equipment, item => Assert.Equal(ItemRarity.Unique, item.Item.Rarity));
            var met = owner.Player.Area.Monsters[placed.Body];
            Assert.False(met.Hostile);
            Assert.True(met.Scripted);
            Assert.Equal(MercenaryWire.MetStates, met.States!.SharedStates);
            // What a player who comes later is given is the instance's record of it, with the build and the team.
            var created = instance.World.MonsterOrigins[placed.Body].Object;
            Assert.Equal(MercenaryWire.MercenaryTeam, created.Team);
            Assert.NotEqual(0, created.MercenaryBuild);
            await GameEvents.RunPlayerEntered(peer.Player);
            Assert.True(peer.Player.Npcs.ContainsKey(placed.Body));
            Assert.Equal(Math.Clamp(met.Variety!.ObjectSize - 1, 0, 127), Game.NpcInteractionSize(peer.Player.Npcs[placed.Body]));

            // The press, then the panel's duel choice; the countdown runs on the area's frames.
            Assert.True(await GameEvents.AnyNpcTalk(peer.Player, placed.Body, peer.Player.Npcs[placed.Body]));
            Assert.Equal(MercenaryModule.Stage.Talk, Read().Stage);
            byte row = MercenaryTables.EncounterRulesRow!.Value;
            Assert.True(await GameEvents.AnyPacket(peer.Player, InstanceRulesets.ActionId,
                [0x02, 0xca, row, 0x08, .. MercenaryWire.DuelChoice]));
            Assert.Equal(MercenaryModule.Stage.Countdown, Read().Stage);
            Assert.Equal(MercenaryWire.CountdownStates, instance.World.MonsterOrigins[placed.Body].Object.StateMachine!.SharedStates);
            owner.Player.Clock.Start();
            var work = GameEvents.AreaTickWork(owner.Player);
            await GameEvents.RunAreaTick(work, owner.Player, 3.0);
            Assert.Equal(2, Read().Wilds.Count);
            Assert.All(Read().Wilds, wild => { Assert.True(owner.Player.Area.Monsters[wild.Body].Hostile); Assert.NotEmpty(wild.Equipment); Assert.All(wild.Equipment, item => Assert.Equal(ItemRarity.Unique, item.Item.Rarity)); });

            // The friendly one is gone and its hostile twin fights.
            var duel = Read();
            Assert.Equal(MercenaryModule.Stage.Duel, duel.Stage);
            Assert.NotEqual(placed.Body, duel.Body);
            Assert.DoesNotContain(placed.Body, owner.Player.Area.Monsters.Keys);
            var opponent = owner.Player.Area.Monsters[duel.Body];
            Assert.True(opponent.Hostile);
            Assert.False(opponent.Scripted);
            Assert.Equal(MercenaryWire.DuelStates, opponent.States!.SharedStates);
            Assert.NotEmpty(opponent.Kit);
            Assert.Null(opponent.Kit[0].Attack.Use);
            Assert.Equal(0, opponent.Kit[0].TriggerThreshold);
            var skills = Game.SkillsFor(owner.Player);
            Assert.DoesNotContain(opponent.Kit, k => BossKits.IsLeavePortal(skills, k.Effect));

            var kept = Assert.Single(SavedMonster.Save([opponent], Game.AreaTime(owner.Player),
                origin: m => instance.World.MonsterOrigins[m.ObjectId].Object));
            kept = JsonSerializer.Deserialize<SavedMonster>(JsonSerializer.Serialize(kept))!;
            Assert.Equal(opponent.Kit.Select(k => k.Effect), kept.OwnedSkillEffects);
            owner.Player.Area.Monsters.Remove(duel.Body);
            instance.World.MonsterOrigins.TryRemove(duel.Body, out _);
            uint restoredDuel = await Game.RestoreMonster(owner.Player, opponent.Variety!, kept);
            Assert.NotEqual(duel.Body, restoredDuel);
            var restoredKit = owner.Player.Area.Monsters[restoredDuel].Kit;
            Assert.Equal(opponent.Kit.Select(k => k.Effect), restoredKit.Select(k => k.Effect));
            Assert.Equal(opponent.Kit[0].Attack.Damage.AverageTotal, restoredKit[0].Attack.Damage.AverageTotal);
            await GameEvents.RunPlayerEntered(peer.Player);
            duel = Read();
            Assert.Equal(restoredDuel, duel.Body);
            opponent = owner.Player.Area.Monsters[restoredDuel];

            var wild = Read().Wilds[0];
            var wildBody = owner.Player.Area.Monsters[wild.Body];
            wildBody.Dead = true;
            var wildKill = new KillContext(owner.Player, wildBody, wildBody.Variety!.Id, (wild.X, wild.Y));
            int beforeWildLoot = instance.Ground.All().Count();
            await GameEvents.RunKill(wildKill);
            var won = Assert.Single(instance.Ground.All().Skip(beforeWildLoot));
            Assert.Equal(ItemRarity.Unique, won.Item.Item.Rarity);
            Assert.Contains(wild.Equipment, w => w.Item.TypeHash == won.Item.Item.TypeHash && w.Item.NameWords.SequenceEqual(won.Item.Item.NameWords));
            await GameEvents.RunKill(new KillContext(owner.Player, wildBody, wildBody.Variety.Id, (wild.X, wild.Y)));
            Assert.Equal(beforeWildLoot + 1, instance.Ground.All().Count());

            // Its death leaves the hired mercenary, in the hired states and its own team.
            opponent.Dead = true;
            await GameEvents.RunKill(new KillContext(owner.Player, opponent, opponent.Variety!.Id, (duel.X, duel.Y)));
            var hired = Read();
            Assert.Equal(MercenaryModule.Stage.Hired, hired.Stage);
            Assert.NotEqual(0u, hired.Hired);
            Assert.Equal(MercenaryWire.HiredStates, owner.Player.Area.Monsters[hired.Hired].States!.SharedStates);
            Assert.Equal(MercenaryWire.AlliedTeam, instance.World.MonsterOrigins[hired.Hired].Object.Team);
        }
        finally
        {
            PlayerInventories.Forget(owner.Player.Character.Name!);
            PlayerInventories.Forget(peer.Player.Character.Name!);
            module.Stop(ex => throw ex);
            GameEventOwners.Release("Mercenary", GameEventOwners.ModuleRank);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    static IModuleContext Context(IModuleLifetime lifetime)
    {
        var context = System.Reflection.DispatchProxy.Create<IModuleContext, Hook>();
        ((Hook)(object)context).Lifetime = lifetime;
        return context;
    }

    public class Hook : System.Reflection.DispatchProxy
    {
        public IModuleLifetime Lifetime = null!;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Lifetime" => Lifetime,
            "get_Manifest" => new ModManifest(Lifetime.Id, "0", ModKind.Feature, [], Lifetime.Id + ".dll"),
            "Log" => null,
            _ => throw new NotSupportedException(method.Name),
        };
    }

    // A connected player of a league that lists the Mercenary module; what is sent to it is read by nobody.
    static async Task<ScarabEncounterTests.Wire> Open(InstanceState instance, AreaLayout layout, uint seed)
    {
        var wire = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        wire.Player.LeagueModule = new(new GameServer.Core.Accounts.League("mercenary-test", "Standard", false, false),
            new HashSet<string>(["Game", "Mercenary"]));
        return wire;
    }
}
