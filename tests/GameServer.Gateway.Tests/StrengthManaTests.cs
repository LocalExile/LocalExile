using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class StrengthManaTests
{
    [Theory]
    [InlineData(3, 8, 0L)]
    public void Complete_nonnegative_Strength_groups_keep_the_signed_raw_value(int strength, int raw, long expected)
        => Assert.Equal(expected, StrengthMana.Flat(strength, raw));

    static byte[] NativeBytes(CharacterStats.Pools pools)
    {
        var attributes = (IEnumerable<(uint, int)>)Method("ClassAttributes").Invoke(null, [1, pools])!;
        return InstanceEntities.StatsUpdate(1, attributes.ToArray());
    }

    static byte[] Mana(uint current, uint reserved) =>
        InstanceProtocol.LifeUpdate(Program.PlayerId, current, reserved, pool: Program.ManaPool);

    static List<byte[]> ManaPackets(byte[] bytes)
    {
        var header = Mana(0, 0).AsSpan(0, 12).ToArray();
        var packets = new List<byte[]>();
        for (int i = 0; i <= bytes.Length - 24; i++)
            if (bytes.AsSpan(i).StartsWith(header) && bytes[i + 20] == Program.ManaPool)
            {
                packets.Add(bytes.AsSpan(i, 24).ToArray());
                i += 23;
            }
        return packets;
    }

    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    static ClientState Conn()
    {
        TestSaves.Ensure();
        string character = "StrengthMana" + Guid.NewGuid().ToString("N");
        CharacterSaves.SetExperience(character, 0); // An empty saved inventory avoids implicit starter modifiers.
        return new(new(ushort.MaxValue, "StrengthMana", 128, 128, 32, 32, null, []), 0, 1)
        { Character = character, ClassRow = 1 };
    }
}
