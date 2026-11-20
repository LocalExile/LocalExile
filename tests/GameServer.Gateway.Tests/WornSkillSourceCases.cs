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
        var conn = new Player(new(1, "worn-source", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "WornSource" + Guid.NewGuid().ToString("N"), Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 2000, 1000, 0, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            PlayerLife = 2000, PlayerMana = 1000,
            LeagueModule = new(new League("worn-test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        conn.Clock.Stop();
        // 1000 more of each attribute (and so for requirements): the socketed gems' requirements are met as well as the host's.
        conn.God = true;
        try
        {
            var supportType = catalog.FindGem("Arrogance Support")!.Value;
            var support = ItemCatalog.Create(supportType.Type) with { GemVariant = supportType.Variant.Index };
            int effect = supportType.Variant.Effect;
            var cleaveType = catalog.FindGem("Cleave")!.Value;
            var cleave = ItemCatalog.Create(cleaveType.Type) with { GemVariant = cleaveType.Variant.Index };
            var stanceType = catalog.FindGem("Blood and Sand")!.Value;
            var stanceGem = ItemCatalog.Create(stanceType.Type) with { GemVariant = stanceType.Variant.Index };
            var host = Game.ItemsOf(conn).Add(PlayerInventories.Boots, 0, 0, item with
            {
                Sockets = [new ItemSocket(SocketColour.White, support), new ItemSocket(SocketColour.White, cleave),
                    new ItemSocket(SocketColour.White, stanceGem)],
                Links = [3],
            });
            // A socketed melee area gem takes Blood and Sand's stance area once, through the stance owner's own stat.
            var melee = Assert.Single(Game.GemSkillEffects(conn), s => s.Effect == cleaveType.Variant.Effect);
            var stanceSkill = Assert.Single(Game.GemSkillEffects(conn), s => s.Effect == stanceType.Variant.Effect);
            uint areaKey = Stat.ActiveSkillAreaOfEffectPlusPctFinal;
            uint stanceAreaKey = Stat.BloodSandStanceMeleeSkillsAreaOfEffectPlusPctFinal;
            var plain = Game.SupportedSkill(conn, melee.Id).Stats;
            conn.Auras[stanceSkill.Id] = new AuraCatalog.Aura(stanceSkill.Id, stanceSkill.Effect, "BloodAndSand", "Blood and Sand", 1,
                AuraCatalog.BuffRow("BloodAndSand", "Sand"), 0, 0, Level: stanceSkill.Level, Stance: "Sand");
            var stance = Game.SupportedSkill(conn, stanceSkill.Id).Stats;
            int sand = stance.GetValueOrDefault(stanceAreaKey) + stance.GetValueOrDefault(Program.Stats.Key(
                "blood_sand_stance_melee_skills_area_of_effect_+%_final_in_sand_stance")!.Value);
            Assert.NotEqual(0, sand);
            var stanced = Game.SupportedSkill(conn, melee.Id).Stats;
            Assert.Equal(plain.GetValueOrDefault(areaKey), stanced.GetValueOrDefault(areaKey));
            Assert.Equal(sand, stanced.GetValueOrDefault(stanceAreaKey));
            int Get(IReadOnlyDictionary<uint, int> own, uint name) => own.GetValueOrDefault(name);
            Assert.Equal(SkillArea.Modifier(n => Get(plain, n), false) * (1 + sand / 100.0),
                SkillArea.Modifier(n => Get(stanced, n), false), 6);
            conn.Auras.Remove(stanceSkill.Id);
            // A saved Sand stance comes back under its runtime id after an area change.
            var back = Assert.Single(Game.RestoreRememberedAuras(conn, [StanceCatalog.Sand]));
            Assert.Equal((stanceSkill.Id, StanceCatalog.Sand, StanceCatalog.SandSelection), (back.SkillId, back.EffectId, conn.Stance));
            conn.Auras.Remove(stanceSkill.Id);
            Assert.NotEmpty(Game.AllWornGrantedSkillsOf(conn));
            Assert.Contains(Game.WornGrantedSkillsOf(conn), g => g.Effect == aura.Effect);
            var source = Assert.Single(Game.GemSkillEffects(conn), s => s.Effect == aura.Effect);
            var supported = Game.SupportedSkill(conn, source.Id);
            Assert.NotNull(supported.WornGrant);
            Assert.Equal(host.Id, supported.WornGrant.Value.Host);
            Assert.True(supported.RequirementsMet);
            Assert.Contains(supported.Supports, s => s.Effect.Row == effect);
            Assert.Null(Game.GemSkillInstanceDisabled(conn, source.Effect, source.Id));
            conn.God = false;
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
