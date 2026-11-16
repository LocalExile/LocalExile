using GameServer.Core.Protocol.Instance;
using GameServer.Core.Accounts;

namespace GameServer.Gateway.Tests;

static class WornSkillSourceCases
{
    public static void Worn_grants_use_real_rows_distinct_hosts_and_supported_current_sources()
    {
        var catalog = Program.Items;
        var item = new UniqueCatalog(catalog).Create("Doryani's Delusion", new Random(1))!;
        var reader = new WornGrantedSkills(Program.Skills, catalog.Mods);
        var first = reader.For([(PlayerInventories.Boots, new InventoryItem(100, 0, 0, item))]);
        Assert.NotEmpty(first);
        var aura = Assert.Single(first, g => g.ManuallyCast && AuraCatalog.Supported(g.EffectId));
        Assert.Equal((aura.Effect, aura.Level), Program.Skills.EffectsPerLevel(aura.EffectsPerLevelRow));
        var second = reader.For([(PlayerInventories.Boots, new InventoryItem(200, 0, 0, item))]);
        Assert.NotEqual(aura.Identity, Assert.Single(second, g => g.Effect == aura.Effect).Identity);
        Assert.True(reader.StillWorn(aura, [(PlayerInventories.Boots, new InventoryItem(100, 0, 0, item))]));
        Assert.False(reader.StillWorn(aura, [(PlayerInventories.Boots, new InventoryItem(200, 0, 0, item))]));

        var previous = Program.GameLogicOrNull;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "worn-source", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "WornSource" + Guid.NewGuid().ToString("N"), Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 2000, 1000, 0, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            PlayerLife = 2000, PlayerMana = 1000,
            LeagueModule = new(new League("worn-test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        conn.Clock.Stop();
        try
        {
            foreach (string attribute in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                conn.PlayerStats[Program.Stats.Key(attribute)!.Value] = 1000;
            var supportType = catalog.FindGem("Arrogance Support")!.Value;
            var support = ItemCatalog.Create(supportType.Type) with { GemVariant = supportType.Variant.Index };
            int effect = supportType.Variant.Effect;
            var host = Game.ItemsOf(conn).Add(PlayerInventories.Boots, 0, 0,
                item with { Sockets = [new ItemSocket(SocketColour.White, support)], Links = [1] });
            Assert.NotEmpty(Game.AllWornGrantedSkillsOf(conn));
            Assert.Contains(Game.WornGrantedSkillsOf(conn), g => g.Effect == aura.Effect);
            var source = Assert.Single(Game.GemSkillEffects(conn), s => s.Effect == aura.Effect);
            var supported = Game.SupportedSkill(conn, source.Id);
            Assert.NotNull(supported.WornGrant);
            Assert.Equal(host.Id, supported.WornGrant.Value.Host);
            Assert.True(supported.RequirementsMet);
            Assert.Contains(supported.Supports, s => s.Effect.Row == effect);
            Assert.Null(Game.GemSkillInstanceDisabled(conn, source.Effect, source.Id));
            foreach (string attribute in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                conn.PlayerStats.Remove(Program.Stats.Key(attribute)!.Value);
            conn.Experience = CharacterStats.ExperienceFor(1);
            conn.Pools = conn.Pools with { Level = 1, Strength = 1, Dexterity = 1, Intelligence = 1 };
            Assert.False(Game.SupportedSkill(conn, source.Id).RequirementsMet);
            Assert.Equal("the granting item is inactive", Game.GemSkillInstanceDisabled(conn, source.Effect, source.Id));
        }
        finally
        {
            conn.Closed = true;
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previous!);
        }
    }
}
