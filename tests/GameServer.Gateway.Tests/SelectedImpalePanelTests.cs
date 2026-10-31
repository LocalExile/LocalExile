using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static SelectedMiscCase ImpaleSpec => new("SkillGemHeavyStrike", "SupportGemImpale", "SupportImpale",
        "attacks_impale_on_hit_%_chance", "attacks_impale_on_hit_%_chance",
        "virtual_total_main_hand_impale_on_hit_%_chance", 10, 60, 0);

    [Fact]
    public void Selected_impale_support_follows_links_sources_and_final_overrides_without_leaking_into_spells()
    {
        var spec = ImpaleSpec;
        using var game = new SelectedMiscCharacter(spec);
        void Check(int selected, int actor, bool supported)
        {
            var view = game.Read();
            Assert.Equal(selected, view.Get(spec.Alias));
            Assert.Equal(0, view.Get("virtual_total_spell_impale_on_hit_%_chance"));
            Assert.Equal(0, view.Stats.GetValueOrDefault(SelectedMiscKey("impale_on_hit_%_chance")));
            Assert.Equal(selected, game.Read().Get(spec.Alias));
            if (supported) game.AssertSupported(view);
            else Assert.Empty(view.Applied);
            game.AssertControls(actor);
        }
        Check(10, 10, false);
        game.Socket();
        Check(70, 10, true);
        game.SetActor(50);
        Check(100, 50, true);
        game.SetActor(-60);
        Check(0, 0, true);
        game.SetActor(10);
        foreach (int replacement in new[] { 0, 7 })
        {
            game.State.PlayerStats[SelectedMiscKey(spec.Alias)] = replacement;
            game.Refresh();
            Check(replacement, replacement, true);
        }
        Assert.True(game.State.PlayerStats.Remove(SelectedMiscKey(spec.Alias)));
        game.Refresh();
        Check(70, 10, true);
        game.Socket(linked: false);
        Check(10, 10, false);
        game.Socket();
        Check(70, 10, true);
        game.Socket(support: false);
        game.RemoveActor();
        Check(0, 0, false);
    }

    [Fact]
    public void Selected_impale_cannot_support_a_spell()
    {
        using var game = new SelectedMiscCharacter(ImpaleSpec with { Active = "SkillGemFireball" });
        game.Socket();
        game.AssertQualified();
        var view = game.Read();
        Assert.Empty(view.Applied);
        Assert.Empty(view.Skill.Supports);
        Assert.Equal(0, view.Own.GetValueOrDefault(SelectedMiscKey(ImpaleSpec.OwnSource)));
        Assert.Equal(0, view.Get("virtual_total_spell_impale_on_hit_%_chance"));
        game.AssertControls(10);
    }

    [Fact]
    public void Selected_impale_uses_each_qualified_active_weapons_local_chance()
    {
        using var game = new SelectedMiscCharacter(ImpaleSpec);
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        var main = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        ItemMod Local(string id, int chance)
        {
            var mod = Program.Items.Mods.ById(id)!;
            Assert.Contains(mod.Stats, s => s.Id == "local_attacks_impale_on_hit_%_chance" && s.Local);
            return new(ModSection.Explicit, mod.Hash,
                mod.Stats.Select(s => s.Id == "local_attacks_impale_on_hit_%_chance" ? chance : s.Min).ToArray());
        }
        var mainMod = Local("JunMaster2LocalIncreasedPhysicalDamageAndImpaleCrafted1", 15);
        var offMod = Local("JunMaster2LocalIncreasedPhysicalDamageAndImpaleCrafted2", 17);
        Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, main.Id, item => item with { Mods = [mainMod] }));
        var off = inventory.Add(3, 0, 0, LevelPanelCarrier(Program.Items, "Rusted Sword", offMod));
        inventory.Add(14, 0, 0, LevelPanelCarrier(Program.Items, "Rusted Sword", offMod));
        game.Socket();
        void Check(int mainChance, int offChance)
        {
            var view = game.Read();
            game.AssertSupported(view);
            Assert.Equal(mainChance, view.Get(ImpaleSpec.Alias));
            Assert.Equal(offChance, view.Get("virtual_total_off_hand_impale_on_hit_%_chance"));
            Assert.Equal(0, view.Get("virtual_total_spell_impale_on_hit_%_chance"));
            var weapon = ProgramValue<SkillCatalog.Unarmed>("WeaponFor", game.State);
            var hit = ImpaleStats.From(Program.CharacterPlayerStats(game.State), Program.Stats, true, view.Own, weapon.LocalSources);
            Assert.Equal(mainChance, hit.Chance);
        }
        Check(85, 87);
        Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, main.Id, item => item with { Mods = [] }));
        game.Refresh();
        Check(70, 87);
        Assert.NotNull(inventory.Remove(3, off.Id));
        game.Refresh();
        Check(70, 70);
    }
}
