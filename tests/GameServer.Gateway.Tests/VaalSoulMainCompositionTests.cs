using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class VaalSoulCompositionTests
{
    static Item AttributeRing(params (string Stat, int Value)[] stats) =>
        ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        {
            Mods = stats.Select(s =>
            {
                var mod = Program.Items.Mods.All.Where(m => m.Stats.Any(a => a.Id == s.Stat && !a.Local))
                    .OrderBy(m => m.Level).First();
                return new ItemMod(ModSection.Explicit, mod.Hash,
                    mod.Stats.Select(a => a.Id == s.Stat ? s.Value : 0).ToArray());
            }).ToArray()
        };

    static void UseCalculatedRequirements(ClientState conn)
    {
        foreach (string attr in new[] { "strength", "dexterity", "intelligence" })
            conn.PlayerStats.Remove(Key($"virtual_{attr}_for_requirements"));
    }

    static IReadOnlyList<GrantedSkill> CurrentSkills(ClientState conn) =>
        (IReadOnlyList<GrantedSkill>)Call("PlayerSkills", conn);

    static GrantedSkill Fireball(ClientState conn) => CurrentSkills(conn).Single(s =>
        s.EffectsPerLevelRow == Program.Skills.FindByRow(0, Program.Skills.EffectRow("Fireball"), 1)!.EffectsPerLevelRow);

    [Fact]
    public async Task Raw_attribute_replacements_preserve_mana_support_payment_and_Vaal_budget_publication()
    {
        using var f = new VaalSoulGatewayTests.Fixture(); using var w = new VaalSoulGatewayTests.Wire();
        UseCalculatedRequirements(f.Conn);
        var ordinaryFireballRow = Program.Skills.FindByRow(0, Program.Skills.EffectRow("Fireball"), 1)!.EffectsPerLevelRow;
        f.Gems("SkillGemVaalFireball", "SkillGemVaalGrace", "SupportGemFasterProjectiles");
        f.Links(3);
        f.Items.Add(6, 0, 0, AttributeRing(("strength_+%", 11), ("dexterity_+%", 500), ("intelligence_+%", 50)));
        f.Conn.MainAscendancy = new(Nodes: [38080]); // Mana source only; no class admission or gate change.
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        f.Conn.PlayerLife = 100; f.Conn.PlayerMana = 77;
        await Command(f, w, "/set_stat base_armour_applies_to_chaos_damage -1");
        await Command(f, w, "/set_stat base_your_auras_are_disabled 1");
        await Command(f, w, "/set_stat spell_skills_deal_no_damage -1");
        await Command(f, w, "/set_stat virtual_aura_vaal_skill_soul_requirement_multipler 100");
        Assert.Equal((16, 84, 48, 138u),
            (f.Conn.Pools.Strength, f.Conn.Pools.Dexterity, f.Conn.Pools.Intelligence, f.Conn.Pools.Mana));
        // Main's Mental Conditioning converts 80% of 690 Mana into 1104 Armour.
        Assert.True(f.Conn.Pools.Armour >= 1104);
        Assert.True(f.Conn.Pools.ArmourAppliesToChaosDamage);
        Assert.Single(Fireball(f.Conn).Supports);
        Assert.Equal(0, ((DamagePacket)Call("SupportedHit", f.Conn, Fireball(f.Conn).Id)).MaxTotal);
        for (int i = 0; i < 4; i++) Assert.True(f.Kill());
        Assert.Equal(new VaalSoulBudget(247, 100, 2), f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 247));

        await Command(f, w, "/set_stat dexterity_+% 0");
        // Vaal Grace and Faster Projectiles no longer meet dexterity, so the club they are socketed in goes inactive:
        // every skill in it leaves, the Vaal budgets with them (not only the unmet gems').
        Assert.Empty(f.Conn.VaalSouls.Read());
        Assert.DoesNotContain(CurrentSkills(f.Conn), s => s.EffectsPerLevelRow == ordinaryFireballRow);
        Assert.DoesNotContain(247u, f.Conn.SentVaalSouls.Keys);
        Assert.DoesNotContain(87u, f.Conn.SentVaalSouls.Keys);
        // The removal tuple follows the update count and any other changed budgets in the same Actor payload.
        await w.Through(InstanceEntities.PlayerSkillsChange(0x1000, [], [], [], [new(247, 0, 0)])[^12..]);
        await Command(f, w, "/set_stat dexterity_+% off");
        Assert.Equal(new VaalSoulBudget(247, 100, 0), f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 247));
        Assert.Single(Fireball(f.Conn).Supports);
        Assert.Equal(138u, f.Conn.Pools.Mana);
        Assert.Equal(100u, f.Conn.PlayerLife); Assert.Equal(77u, f.Conn.PlayerMana);
        await Command(f, w, "/set_stat spell_skills_deal_no_damage off");
        Assert.True(((DamagePacket)Call("SupportedHit", f.Conn, Fireball(f.Conn).Id)).MaxTotal > 0);

        var ordinary = Fireball(f.Conn);
        uint cost = (uint)ordinary.Stats!.Single(s => s.Key == Key("mana_cost")).Value;
        Assert.True(cost > 0);
        object?[] args = [f.Conn, ordinary.Id, 0u];
        Assert.True((bool)typeof(Program).GetMethod("PayMana",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, args)!);
        Assert.Equal(cost, (uint)args[2]!);
        Assert.Equal(77u - cost, f.Conn.PlayerMana);
        // Both Vaal skills left with the inactive club, so both came back without their stored souls.
        Assert.Equal(0u, f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 87).Current);
        Assert.Equal(0u, f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 247).Current);
    }

}
