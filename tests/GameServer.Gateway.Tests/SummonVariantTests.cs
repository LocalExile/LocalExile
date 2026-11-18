using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class SummonVariantTests
{
    static ushort Equip(AnimatedAdmissionTests.Fixture f, string name, string weapon = "Rusted Sword", int sockets = 1)
    {
        var items = Game.ItemsOf(f.Conn);
        foreach (var p in items.Items(PlayerInventories.Weapon1)) items.Remove(PlayerInventories.Weapon1, p.Id);
        var type = Program.Items.FindGem(name)!.Value;
        var gem = ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index,
            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20) };
        var host = ItemCatalog.Create(Program.Items.Find(weapon)!) with
        {
            Sockets = Enumerable.Range(0, sockets).Select(i => new ItemSocket(SocketColour.White, i == 0 ? gem : null)).ToArray()
        };
        items.Add(PlayerInventories.Weapon1, 0, 0, host);
        return Game.GemSkillEffects(f.Conn).Single().Id;
    }

    [Fact]
    public async Task Self_reflection_copies_the_active_melee_weapon_and_never_consumes_ground_equipment()
    {
        using var f = new AnimatedAdmissionTests.Fixture();
        ushort source = Equip(f, "Animate Weapon of Self Reflection", sockets: 6);
        var equipped = Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single().Item;
        var definition = Game.SummonData.Find("AnimateWeaponAltX")!;
        Assert.True(Game.SupportedSkill(f.Conn, source).RequirementsMet);
        uint id = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, source, definition, (100, 100));
        Assert.NotEqual(0u, id);
        Assert.Same(equipped, f.Conn.Summons.Fighters[id].Equipment[2]);
        var ground = new WorldItemObject(100, 100, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        uint groundId = f.Conn.Instance.Ground.Add(ground, f.Conn);
        id = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, source, definition, (100, 100), groundId);
        Assert.NotEqual(0u, id); Assert.Same(ground, f.Conn.Instance.Ground.Get(groundId));
        Assert.Same(equipped, f.Conn.Summons.Fighters[id].Equipment[2]);
        Assert.Same(equipped, Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single().Item);
        int count = f.Conn.Summons.Fighters.Count;
        source = Equip(f, "Animate Weapon of Self Reflection", "Crude Bow");
        Assert.Equal(0u, await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, source, definition, (100, 100), groundId));
        Assert.Equal(count, f.Conn.Summons.Fighters.Count);
        Assert.Same(ground, f.Conn.Instance.Ground.Get(groundId));
    }

    [Fact]
    public async Task Holy_relic_variants_use_their_native_novas_and_their_respective_damage_centres()
    {
        foreach (bool conviction in new[] { false, true })
        {
            using var f = new AnimatedAdmissionTests.Fixture();
            ushort source = Equip(f, conviction ? "Summon Holy Relic of Conviction" : "Summon Holy Relic");
            uint id = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, source,
                Game.SummonData.Find(conviction ? "SummonHolyRelicAltX" : "SummonHolyRelic")!, (100, 100));
            var relic = f.Conn.Summons.Fighters[id];
            var nova = Assert.Single(relic.Abilities, a => a.Effect == (conviction ? 9928 : 9927));
            var target = new SpawnedMonster("distant enemy", new(160, 100, 0), 100, 3, 100000) { Hostile = true };
            var beside = new SpawnedMonster("beside relic", new(100, 100, 0), 100, 3, 100000) { Hostile = true };
            f.Conn.Monsters[999] = target; f.Conn.Monsters[998] = beside;
            var action = new GolemState.Action(nova, 999, target, (160, 100), 0, relic.Monster.ActionGeneration);
            typeof(Game).GetMethod("GolemAbilityContactLocked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, ["test", f.Stream, f.Ciphers, f.Conn, relic, action, 0d]);
            Assert.Equal(conviction, target.Life < 100000);
            Assert.Equal(!conviction, beside.Life < 100000);
            typeof(Game).GetMethod("HolyRelicsOnHitLocked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [f.Conn, 999u, target, 1d]);
            Assert.Equal(conviction ? 998u : 999u, relic.Pending!.Target);
        }
    }
}
