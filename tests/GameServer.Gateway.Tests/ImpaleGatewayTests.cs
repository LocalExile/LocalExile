using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Theory]
    [InlineData("closed")]
    [InlineData("leaving")]
    [InlineData("paused")]
    [InlineData("owner-dead")]
    [InlineData("target-dead")]
    [InlineData("replaced-action")]
    public async Task Rejected_contacts_neither_consume_nor_apply_impales(string rejected)
    {
        using var f = new Contact(new Draws());
        f.Target.Impale.Apply(100, now: f.Conn.Clock.Elapsed.TotalSeconds);
        switch (rejected)
        {
            case "closed": f.Conn.Closed = true; break;
            case "leaving": f.Conn.LeavingArea = true; break;
            case "paused": f.Conn.Clock.Stop(); break;
            case "owner-dead": f.Conn.PlayerLife = 0; break;
            case "target-dead": f.Target.Dead = true; break;
        }
        int calls = 0;
        bool accepted = await f.ContactDamage(() =>
        {
            calls++;
            f.Target.Impale.OnHit(100, f.Conn.Clock.Elapsed.TotalSeconds, new(100), new Random(1));
            return 10;
        }, rejected == "replaced-action" ? int.MaxValue : null);
        Assert.False(accepted);
        Assert.Equal(0, calls);
        Assert.Equal(1, f.Target.Impale.Stacks);
        for (int i = 0; i < 4; i++) f.Target.Impale.AfterHit();
        Assert.Equal(10, f.Target.Impale.Bonus);
    }

    [Theory]
    [InlineData(null, "Rusted Sword")]
    [InlineData(null, "Crude Bow")]
    [InlineData("Cyclone", "Rusted Sword")]
    [InlineData("PoisonArrow", "Crude Bow")]
    [InlineData("Fireball", "Rusted Sword")]
    [InlineData("IceNova", "Rusted Sword")]
    public async Task Target_impale_avoidance_prevents_new_instances_but_does_not_discard_existing_ones(string? gem, string weapon)
    {
        var mods = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<string, int> { ["avoid_impale_%"] = 100 }));
        using var f = new Contact(new Draws(), gem, weapon, distance: weapon == "Crude Bow" ? 60 : 2, targetMods: mods);
        f.Set("global_cannot_crit", 1);
        f.Set("physical_damage_+%", 0);
        f.Set("attack_minimum_added_physical_damage", 1000);
        f.Set("attack_maximum_added_physical_damage", 1000);
        f.Set("spell_minimum_added_physical_damage", 1000);
        f.Set("spell_maximum_added_physical_damage", 1000);
        f.Set("impale_on_hit_%_chance", 100);
        f.Refresh();
        f.Target.Impale.Apply(100, now: f.Conn.Clock.Elapsed.TotalSeconds);
        await f.Use();
        await f.HitLands();
        Assert.Equal(1, f.Target.Impale.Stacks);
        Assert.Equal(10, f.Target.Impale.Bonus);
        for (int i = 0; i < 4; i++) f.Target.Impale.AfterHit();
        Assert.Equal(0, f.Target.Impale.Stacks);
    }

    [Theory]
    [InlineData(null, "Rusted Sword", false)]
    [InlineData(null, "Crude Bow", false)]
    [InlineData("Cyclone", "Rusted Sword", false)]
    [InlineData("PoisonArrow", "Crude Bow", false)]
    [InlineData("Fireball", "Rusted Sword", true)]
    [InlineData("IceNova", "Rusted Sword", true)]
    public async Task Parsed_contacts_apply_impale_from_their_own_physical_hit(string? gem, string weapon, bool spell)
    {
        using var f = new Contact(new Draws(), gem, weapon, distance: weapon == "Crude Bow" ? 60 : 2);
        f.Set("global_cannot_crit", 1);
        f.Set("physical_damage_+%", 0);
        foreach (string kind in new[] { "attack", "spell" })
            foreach (string bound in new[] { "minimum", "maximum" })
            {
                f.Set($"{kind}_{bound}_added_chaos_damage", 0);
                f.Set($"{kind}_{bound}_added_physical_damage", 1000);
            }
        f.Set(spell ? "spells_impale_on_hit_%_chance" : "attacks_impale_on_hit_%_chance", gem is null || spell ? 100 : 40);
        f.Set("impale_debuff_effect_+%", 50);
        if (gem is not null && !spell)
        {
            var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
            var host = Assert.Single(inventory.Items(PlayerInventories.BodyArmour));
            var support = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemImpale")!);
            Assert.NotNull(inventory.Change(PlayerInventories.BodyArmour, host.Id,
                item => item with { Sockets = [item.Sockets[0], new(SocketColour.White, support)], Links = [2] }));
        }
        f.Refresh();
        DamagePacket packet;
        if (gem is null)
        {
            var main = (SkillCatalog.Unarmed)Method("WeaponFor").Invoke(null, [f.Conn])!;
            packet = Program.AttackHitConverted(main, Program.CharacterPlayerStats(f.Conn), weapon != "Crude Bow", null);
        }
        else packet = f.Hit;
        var physical = packet[DamageType.Physical];
        Assert.True(physical.Min > 0);
        if (gem is not null && !spell)
        {
            var selected = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill);
            Assert.Contains((Program.Stats.Key("virtual_total_main_hand_impale_on_hit_%_chance")!.Value, 100), selected.Stats!);
            Assert.Single(selected.Supports);
        }
        await f.Use();
        await f.HitLands();
        Assert.Equal(1, f.Target.Impale.Stacks);
        Assert.InRange(f.Target.Impale.Bonus, Math.Floor(physical.Min) * .15, Math.Floor(physical.Max) * .15 + 1e-8);
    }

    [Theory]
    [InlineData("Cyclone", "Rusted Sword")]
    [InlineData("PoisonArrow", "Crude Bow")]
    [InlineData("Fireball", "Rusted Sword")]
    [InlineData("IceNova", "Rusted Sword")]
    public async Task Accepted_zero_damage_skill_hits_consume_reflected_impales_without_spell_scaling(string gem, string weapon)
    {
        var mods = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<string, int>
        { ["base_additional_physical_damage_reduction_%"] = 50, ["base_damage_taken_+%"] = 20,
            ["rare_monster_mod_spell_damage_taken_+%_final"] = -90 }));
        using var f = new Contact(new Draws(), gem, weapon, distance: weapon == "Crude Bow" ? 60 : 2, targetMods: mods);
        f.Set("global_cannot_crit", 1);
        foreach (string type in new[] { "physical", "fire", "cold", "lightning", "chaos" }) f.Set($"{type}_damage_+%", -10000);
        f.Refresh();
        Assert.True(f.Hit.IsZero);
        f.Target.Impale.Apply(100, now: f.Conn.Clock.Elapsed.TotalSeconds);
        await f.Use();
        await f.HitLands();
        Assert.Equal(6u, f.Target.MaxLife - f.Target.Life); // stored 10 * 50% physical reduction * 120% taken
        for (int i = 0; i < 4; i++) f.Target.Impale.AfterHit();
        Assert.Equal(0, f.Target.Impale.Stacks); // the actual contact consumed the first remaining hit
    }
}
