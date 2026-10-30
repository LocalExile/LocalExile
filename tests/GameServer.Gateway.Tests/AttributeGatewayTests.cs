using System.Reflection;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class AttributeGatewayTests : IDisposable
{
    readonly string name = "Attributes" + Guid.NewGuid().ToString("N");

    ClientState Character(int classRow = SkillCatalog.WitchRow)
    {
        TestSaves.Ensure();
        return new(new(ushort.MaxValue, "AttributeTest", 128, 128, 32, 32, null, []), 0, 1)
            { Character = name, ClassRow = classRow };
    }

    static Item Gear(string type, params (string Stat, int Value)[] stats) => ItemCatalog.Create(Program.Items.Find(type)!) with
    {
        Mods = stats.Select(s =>
        {
            var mod = Program.Items.Mods.All.Where(m => m.Stats.Any(a => a.Id == s.Stat && !a.Local))
                .OrderBy(m => m.Level).First();
            return new ItemMod(ModSection.Explicit, mod.Hash,
                mod.Stats.Select(a => a.Id == s.Stat ? s.Value : 0).ToArray());
        }).ToArray()
    };

    PlayerInventories Inventory => PlayerInventories.For(name, Program.Items);
    static uint Key(string stat) => Program.Stats.Key(stat)!.Value;
    static void Set(ClientState conn, string stat, int value) => conn.PlayerStats[Key(stat)] = value;
    static CharacterStats.Pools Rebuild(ClientState conn) => conn.Pools = Program.PoolsFor(conn, 100);
    static Dictionary<uint, int> Publish(ClientState conn) =>
        ((IEnumerable<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!).ToDictionary(s => s.Item1, s => s.Item2);

    static async Task WithCommands(ClientState conn, Func<ChatCommands, Task> scenario)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        var commands = (ChatCommands)typeof(Program).GetMethod("Commands", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["attribute-boundary-test", writer.GetStream(), new ConnectionCiphers(new byte[64], false), conn])!;
        await scenario(commands).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Complete_class_passive_and_equipment_bucket_drives_every_attribute_bonus_once()
    {
        var conn = Character();
        conn.Passives.Add(49772); // Utmost Might: +40 strength, 8% increased strength, from the extracted tree.
        Inventory.Add(6, 0, 0, Gear("Iron Ring", ("additional_strength", 20), ("additional_all_attributes", 30),
            ("strength_+%", 20), ("dexterity_+%", 25), ("intelligence_+%", 50), ("all_attributes_+%", 10)));
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Gear("Simple Robe"));
        var pools = Rebuild(conn);
        Assert.Equal((144, 59, 99), (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal(1310u, pools.Life);
        Assert.Equal(683u, pools.Mana);
        Assert.Equal(318, pools.Accuracy);
        Assert.Equal(11, pools.EvasionPct);
        Assert.Equal(17, pools.Evasion); // Native final defence rounding: 15 * 1.11 = 16.65 -> 17.
        Assert.Equal(9, pools.EnergyShieldPct); // Complete final Intelligence groups of ten.
        Assert.True(pools.EnergyShield > 0);
        Assert.Equal(28, pools.MeleePhysicalDamagePct);
        var shown = Program.CharacterPlayerStats(conn);
        foreach (var (stat, value) in new[] { ("strength", 144), ("dexterity", 59), ("intelligence", 99),
            ("virtual_strength_for_requirements", 144), ("virtual_dexterity_for_requirements", 59),
            ("virtual_intelligence_for_requirements", 99) })
            Assert.Equal(value, shown[Key(stat)]);
        Assert.Equal(pools, Rebuild(conn));
    }

    // A gem's requirements are its item's (live 2026-10-29, lost in PR #175): a level 20 Fireball (155 intelligence)
    // the Witch cannot meet switches the robe off, so neither its gems nor its mods count, until the attributes come.
    [Fact]
    public void Socketed_gem_requirements_disable_the_worn_item_and_its_skills()
    {
        var conn = Character();
        var fireball = Program.Items.FindGem("Fireball")!.Value;
        var gem = ItemCatalog.Create(fireball.Type) with
            { GemExperience = Program.Items.Gem(fireball.Type)!.ExperienceFor(20), GemVariant = fireball.Variant.Index };
        var robe = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Gear("Simple Robe", ("additional_dexterity", 10))
            with { Sockets = [new(SocketColour.Blue, gem)] });
        IEnumerable<(ushort, int, string, int, int, uint)> Skills() =>
            (IEnumerable<(ushort, int, string, int, int, uint)>)typeof(Program).GetMethod("GemSkillEffects",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!;

        var pools = Rebuild(conn);
        Assert.True(pools.Intelligence < 155);
        Assert.Contains(robe.Id, Program.InactiveItems[name]);
        Assert.Empty(Skills());
        Assert.Equal(Program.SkillsFor(conn).ClassFor(conn.ClassRow).Dexterity, pools.Dexterity);

        Inventory.Add(6, 0, 0, Gear("Iron Ring", ("additional_intelligence", 155)));
        Rebuild(conn);
        Assert.DoesNotContain(robe.Id, Program.InactiveItems[name]);
        Assert.Single(Skills());
    }

    public void Dispose()
    {
        PlayerInventories.Forget(name);
        Program.InactiveItems.TryRemove(name, out _);
    }
}
