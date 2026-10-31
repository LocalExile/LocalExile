using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{

    [Fact]
    public async Task Remembered_duplicate_aura_skips_only_the_gem_with_unmet_instance_requirements()
    {
        // The level 20 copy sits in its own helmet: a gem the character cannot meet switches its whole item off, so a copy
        // in the same item would go with it.
        using var f = new Fixture("Clarity");
        var gem = Program.Items.Find("Metadata/Items/Gems/SkillGemClarity")!;
        f.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
            { Sockets = [new(SocketColour.White, ItemCatalog.Create(gem) with { GemExperience = Program.Items.Gem(gem)!.ExperienceFor(20) })] });
        var copies = ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
            Call("GemSkillEffects", f.Conn)!).ToArray();
        ushort low = copies.Single(c => c.Level == 1).Id, high = copies.Single(c => c.Level == 20).Id;
        f.Conn.PlayerStats[Program.Stats.Key("virtual_intelligence_for_requirements")!.Value] = 100;
        int effect = f.Effect("Clarity");
        Assert.NotNull(Call("GemSkillInstanceDisabled", f.Conn, effect, high));
        Assert.Null(Call("GemSkillInstanceDisabled", f.Conn, effect, low));
        CharacterSaves.SetAuraActive(f.Conn.Character!, "Clarity", true);
        await f.Set(21148, 1);
        Assert.Empty(f.Conn.Auras);
        await f.Set(21148, 0);
        var restored = Assert.Single(f.Conn.Auras).Value;
        Assert.Equal(low, restored.SkillId);
        Assert.Equal(1, restored.Level);
    }

    static IEnumerable<(uint Current, uint Reserved)> ManaPackets(byte[] bytes)
    {
        byte[] header = InstanceProtocol.LifeUpdate(0x1000, 0, 0, pool: Program.ManaPool)[..12];
        for (int i = 0; i <= bytes.Length - 24; i++)
            if (bytes.AsSpan(i).StartsWith(header) && bytes[i + 20] == Program.ManaPool)
            {
                yield return (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 12)),
                    BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 16)));
                i += 23;
            }
    }
}
