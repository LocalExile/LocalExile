using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

/// <summary>The fifteen per-aura effect stats scale only their own aura.</summary>
public class PerAuraEffectTests
{
    [Fact]
    public void Per_aura_effect_scales_only_that_aura()
    {
        var skills = Program.Skills;
        var haste = new AuraCatalog.Aura(1, skills.EffectRow("Haste"), "Haste", "Haste", 1, 152, 0, 5000, Level: 20);
        var grace = new AuraCatalog.Aura(2, skills.EffectRow("Grace"), "Grace", "Grace", 1, 143, 0, 5000, Level: 20);
        var plain = AuraCatalog.Bonus([haste, grace], 0, skills, Program.Stats);
        Assert.True(plain.AttackSpeedPct > 0);
        Assert.True(plain.MovementVelocityPct > 0);
        Assert.True(plain.Evasion > 0);

        // +100% effect of Haste only: Haste's own bonuses double, Grace's do not move.
        var stronger = haste with { PerAuraEffectPct = 100 };
        var scaled = AuraCatalog.Bonus([stronger, grace], 0, skills, Program.Stats);
        Assert.Equal(2 * plain.AttackSpeedPct, scaled.AttackSpeedPct);
        Assert.Equal(2 * plain.MovementVelocityPct, scaled.MovementVelocityPct);
        Assert.Equal(plain.Evasion, scaled.Evasion);

        // Live ReconcileOwnAuras restamp path: changing a per-aura effect stat in raw sources restamps the active
        // aura, marks AuraStatsDirty (firing AuraStatsDirtied), and rebuilds Pools; temporary auras stamp on start.
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        string character = "PerAura" + Guid.NewGuid().ToString("N");
        var player = runtime.PlayerOf(new(), wire, character);
        int hasteEffectPct = 0;
        IDisposable? requirements = null, statHook = null;
        try
        {
            player.Character.Experience = CharacterStats.ExperienceFor(100);
            player.Pools = new(100, 2000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000);
            player.PlayerLife = 2000;
            player.PlayerMana = 1000;
            player.LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"]));
            requirements = TestRequirements.Waive(player);
            statHook = GameEvents.PlayerStatSources.Add("Game", (p, sources) =>
            {
                if (!ReferenceEquals(p, player) || hasteEffectPct == 0) return sources;
                var copy = new Dictionary<uint, int>(sources);
                copy[Stat.HasteAuraEffectPlusPct] = copy.GetValueOrDefault(Stat.HasteAuraEffectPlusPct) + hasteEffectPct;
                return copy;
            });

            var gemType = Program.Items.FindGem("Haste")!.Value;
            var gem = ItemCatalog.Create(gemType.Type) with
            {
                GemVariant = gemType.Variant.Index,
                GemExperience = Program.Items.Gem(gemType.Type)!.ExperienceFor(20),
            };
            var boots = ItemCatalog.Create(Program.Items.Find("Iron Greaves")!) with
            {
                Sockets = [new(SocketColour.White, gem)],
                Links = [1],
            };
            Game.ItemsOf(player).Add(PlayerInventories.Boots, 0, 0, boots);
            var hasteSkill = Assert.Single(Game.GemSkillEffects(player));
            player.Auras[hasteSkill.Id] = new AuraCatalog.Aura(hasteSkill.Id, hasteSkill.Effect, "Haste", "Haste",
                player.NextBuff++, AuraCatalog.BuffRow("Haste"), 0, 5000, Level: 20, SourceBuff: player.NextBuff++);
            Game.ReconcileOwnAuras(player);
            player.Pools = Game.PoolsFor(player, 100);
            player.AuraStatsDirty = false;
            int moveBefore = player.Pools.MovementVelocityPct;

            bool dirtied = false;
            player.AuraStatsDirtied = () => dirtied = true;
            hasteEffectPct = 100;
            Game.ReconcileOwnAuras(player);
            Assert.Equal(100, player.Auras[hasteSkill.Id].PerAuraEffectPct);
            Assert.True(player.AuraStatsDirty);
            Assert.True(dirtied);
            Assert.Equal(moveBefore + plain.MovementVelocityPct, player.Pools.MovementVelocityPct);

            // Reconciling again with unchanged sources does not re-dirty.
            player.AuraStatsDirty = false;
            dirtied = false;
            Game.ReconcileOwnAuras(player);
            Assert.False(player.AuraStatsDirty);
            Assert.False(dirtied);

            // Temporary auras (e.g. Blessing) also stamp PerAuraEffectPct immediately on activation.
            var temp = Game.StartTemporaryAuraLocked(player, hasteSkill.Id, "Haste", AuraCatalog.BuffRow("Haste"), 10, true);
            Assert.Equal(100, temp.PerAuraEffectPct);
        }
        finally
        {
            player.PlayerLife = 0;
            Game.ExpireTemporaryAuras(player);
            player.Auras.Clear();
            statHook?.Dispose();
            requirements?.Dispose();
            PlayerInventories.Forget(character);
        }
    }

    [Fact]
    public void Per_aura_effect_scales_Pride_and_the_mapping_covers_the_fifteen_auras()
    {
        var skills = Program.Skills;
        int effect = skills.EffectRow("Pride");
        Assert.True(effect >= 0);
        var aura = new AuraCatalog.Aura(0x999, effect, "Pride", "Pride", 1, AuraCatalog.BuffRow("Pride"),
            0, 0, Level: 20, Quality: 0);
        Assert.Equal((19, 39), AuraCatalog.PridePhysicalDamageTaken(aura, 0, skills, Program.Stats));
        Assert.Equal((38, 78), AuraCatalog.PridePhysicalDamageTaken(aura with { PerAuraEffectPct = 100 }, 0, skills, Program.Stats));

        // The mapping covers exactly the fifteen aura-body rows, and every named stat exists in the client's
        // Stats table. Malevolence is the spelling mismatch: its stat is named for Delirium.
        Assert.Equal(15, AuraCatalog.PerAuraEffectStats.Count);
        Assert.Equal(Stat.DeliriumAuraEffectPlusPct, AuraCatalog.PerAuraEffectStats["Malevolence"]);
        foreach (uint key in AuraCatalog.PerAuraEffectStats.Values)
            Assert.True(Program.Stats.Name(key) is not null);
    }
}
