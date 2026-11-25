using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core;
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

    // A mercenary fights with the skills its MercenaryBuilds row lists (MercenarySkills' GrantedEffect, or the EncounterGrantedEffect
    // where there is one), not with its variety's default attack, which for most of the varieties is the effect it leaves by.
    [GameFilesFact]
    public void A_mercenary_fights_with_the_skills_of_its_build_and_never_leaves_by_portal()
    {
        MercenaryModule.LoadData();
        const byte level = 70;
        InGame(player =>
        {
            var catalog = Game.SkillsFor(player);
            foreach (var kind in MercenaryTables.Eligible())
            {
                var variety = Game.Monsters.Find(kind.Path)!;
                int portal = (variety.GrantedEffects ?? []).FirstOrDefault(e => BossKits.IsLeavePortal(catalog, e), -1);
                if (portal < 0) continue;
                // Nor is it the variety's default attack or one of its usable effects.
                Assert.NotEqual(portal, Game.MavenNucleus.CombatEffect(catalog, variety));
                Assert.DoesNotContain(portal, BossKits.Effects(catalog, variety));
                foreach (var build in MercenaryTables.BuildsOf(kind.Row))
                {
                    var listed = ListedEffects(build.Row, level);
                    var kit = BossKits.KitFromEffects(player, variety, level,
                        MercenaryTables.SkillsOf(build, level, new Random(1)));
                    if (kit.Count == 0) continue;
                    // Only skills the build lists, none of the variety's own, and the way out is nowhere in the kit.
                    Assert.All(kit, entry => Assert.Contains(entry.Effect, listed));
                    Assert.DoesNotContain(kit, entry => entry.Effect == portal);
                    Assert.DoesNotContain(kit, entry => (variety.GrantedEffects ?? []).Contains(entry.Effect));
                    Assert.Equal(Enumerable.Range(0, kit.Count).Select(i => (ushort)(0x4000 + i)), kit.Select(entry => entry.Attack.SkillId));
                    return;
                }
            }
            Assert.Fail("no mercenary build of a variety that leaves by portal has a skill the AI can play");
        });
    }

    // MercenarySkills' RequiredLevel gates a skill: a mercenary below it does not have it, one that reaches it does.
    [GameFilesFact]
    public void A_skill_that_needs_a_higher_level_than_the_mercenary_is_left_out()
    {
        MercenaryModule.LoadData();
        var skills = ClientTables.Table("MercenarySkills");
        var builds = ClientTables.Table("MercenaryBuilds");
        InGame(player =>
        {
            var catalog = Game.SkillsFor(player);
            foreach (var build in MercenaryTables.Builds)
            {
                var listed = builds.First(r => r.Index == build.Row);
                foreach (int row in listed.Ints("Skills1"))
                {
                    var skill = skills.First(r => r.Index == row);
                    int required = skill.IntOrNull("RequiredLevel") ?? 0;
                    int effect = EffectOf(skill);
                    if (required <= 1 || effect < 0) continue;
                    // No other row of the build gives this effect at a lower level, so it is the level that decides.
                    if (listed.Ints("Skills1").Concat(listed.Ints("Skills2")).Concat(listed.Ints("Skills3")).Any(other =>
                        other != row && skills.First(r => r.Index == other) is var o && EffectOf(o) == effect
                        && (o.IntOrNull("RequiredLevel") ?? 0) < required)) continue;
                    var below = MercenaryTables.SkillsOf(build, required - 1, new Random(1));
                    Assert.DoesNotContain(effect, below);
                    Assert.Contains(effect, MercenaryTables.SkillsOf(build, required, new Random(1)));
                    // What it leaves in the kit is only what the level reaches, and the way out is never in it.
                    var variety = Game.Monsters.Find(MercenaryTables.ClassOf(build.ClassRow)!.Path)!;
                    var kit = BossKits.KitFromEffects(player, variety, (byte)(required - 1), below);
                    Assert.DoesNotContain(kit, entry => entry.Effect == effect || BossKits.IsLeavePortal(catalog, entry.Effect));
                    return;
                }
            }
            Assert.Fail("no build lists a skill above level 1 in Skills1");
        });
    }

    static int EffectOf(ClientRow skill) =>
        skill.IntOrNull("EncounterGrantedEffect") is int encounter and >= 0 ? encounter : skill.IntOrNull("GrantedEffect") ?? -1;

    // Every effect the client rows list for the build (Skills1, Skills2 and Skills3) at the level: what its kit can hold.
    static HashSet<int> ListedEffects(int buildRow, int level)
    {
        var build = ClientTables.Table("MercenaryBuilds").First(r => r.Index == buildRow);
        var skills = ClientTables.Table("MercenarySkills");
        HashSet<int> effects = [];
        foreach (int row in build.Ints("Skills1").Concat(build.Ints("Skills2")).Concat(build.Ints("Skills3")))
        {
            var skill = skills.First(r => r.Index == row);
            if ((skill.IntOrNull("RequiredLevel") ?? 0) <= level) effects.Add(EffectOf(skill));
        }
        return effects;
    }

    static void InGame(Action<Player> body)
    {
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            body(new Player(new(1, "kit", 100, 100, 10, 10, null, []), 1, 1));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            if (previousLogic is not null) Program.SetGameLogic(previousLogic);
        }
    }

    // One mercenary of a new map instance, from the roll to the hire: placed once for the instance, given to a player who enters
    // later, pressed, chosen, replaced by its hostile duel opponent after the countdown, and left as the hired one when that dies.
    [GameFilesFact]
    public async Task A_mercenary_is_placed_for_the_instance_talked_to_duelled_and_hired()
    {
        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var mods = new MapMods(new Dictionary<uint, int> { [Stat.MapMercenaryLeague] = 1 });
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

            // The friendly one is gone and its hostile twin fights.
            var duel = Read();
            Assert.Equal(MercenaryModule.Stage.Duel, duel.Stage);
            Assert.NotEqual(placed.Body, duel.Body);
            Assert.DoesNotContain(placed.Body, owner.Player.Area.Monsters.Keys);
            var opponent = owner.Player.Area.Monsters[duel.Body];
            Assert.True(opponent.Hostile);
            Assert.False(opponent.Scripted);
            Assert.Equal(MercenaryWire.DuelStates, opponent.States!.SharedStates);
            // It fights with the skills its build lists, and never leaves by the portal its variety's default attack may be.
            var listed = ListedEffects(duel.Build, duel.Level);
            Assert.All(opponent.Kit, entry => Assert.Contains(entry.Effect, listed));
            var skillsOfOwner = Game.SkillsFor(owner.Player);
            Assert.DoesNotContain(opponent.Kit, entry => BossKits.IsLeavePortal(skillsOfOwner, entry.Effect));
            Assert.False(opponent.Attack?.Effect is int swing && BossKits.IsLeavePortal(skillsOfOwner, swing));

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
