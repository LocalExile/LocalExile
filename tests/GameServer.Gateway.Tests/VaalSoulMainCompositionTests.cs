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
        Assert.Equal(new VaalSoulBudget(87, 30, 2), f.Budget);
        Assert.Empty(Fireball(f.Conn).Supports);
        Assert.DoesNotContain(247u, f.Conn.SentVaalSouls.Keys);
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
        Assert.Equal(2u, f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 87).Current);
        Assert.Equal(0u, f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 247).Current);
    }

    [Fact]
    public void Current_Vaal_requirements_keep_wide_raw_buckets_and_never_rebuild_or_refill_cached_pools()
    {
        using var f = new VaalSoulGatewayTests.Fixture();
        UseCalculatedRequirements(f.Conn);
        f.Gems("SkillGemVaalGrace");
        f.Items.Add(6, 0, 0, AttributeRing(("additional_dexterity", int.MaxValue),
            ("additional_dexterity", int.MaxValue)));
        f.Conn.PlayerStats[Key("base_dexterity")] = -int.MaxValue;
        f.Conn.PlayerStats[Key("dexterity_+%")] = -50;
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        Assert.Equal(1073741831, f.Conn.Pools.Dexterity);
        f.Conn.PlayerLife = 100; f.Conn.PlayerMana = 77;
        var cached = f.Conn.Pools;
        Assert.True(f.Kill()); Assert.Equal(new VaalSoulBudget(247, 50, 1), f.Budget);
        f.Conn.PlayerStats[Key("additional_dexterity")] = 0; // Replaces both affixes, before scaling.
        Program.RefreshVaalSouls(f.Conn);
        Assert.Empty(f.Conn.VaalSouls.Read());
        Assert.Same(cached, f.Conn.Pools);
        f.Conn.PlayerStats.Remove(Key("additional_dexterity"));
        Program.RefreshVaalSouls(f.Conn);
        Assert.Equal(new VaalSoulBudget(247, 50, 0), f.Budget);
        Assert.True(f.Kill()); Assert.Equal(1u, f.Budget.Current);
        Assert.Same(cached, f.Conn.Pools);
        Assert.Equal(100u, f.Conn.PlayerLife); Assert.Equal(77u, f.Conn.PlayerMana);
    }
}
