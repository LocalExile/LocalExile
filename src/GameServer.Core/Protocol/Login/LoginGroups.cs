using System.Numerics;

namespace GameServer.Core.Protocol.Login;

/// <summary>
/// Diffie-Hellman groups hard-coded in the client's login crypto object ("Rhoa", ctor 0x140e00a10; see Agents/docs/re/008).
/// Both are safe primes p = 2q + 1; g generates the order-q subgroup. Static keys use the 3072-bit group,
/// per-connection (ephemeral) keys the 1024-bit group, combined like Crypto++ DH2.
/// </summary>
public static class LoginGroups
{
    public static readonly DhGroup Static = new(
        Hex(
        "cd8fd4b7415dee60366c437dc8b43cfb01e35540cdd79b22f60b6c6ad4c77571efc441a88c33e8bacaa6bcca3e5099d5" +
        "8a8415b35217a5119b4eb3893f1472b9ed168230c3ca982f32202658f88959881c1bdd98423af79caca5d517544f0921" +
        "4e4ff3d2e6108fa21776749af68282771799575269507bc69b20b66eb74d075e57a5b96c13180262b2a96ea3c42e128b" +
        "cc064f5b4e7a451f12f074bd2e64d433ab380494ed53d1c45db97cc4b3da288bf3533499a5f607b55175cc1d7ef91745" +
        "9e8f77658638d918e23753cf29429b846dcd410e541a855ebb4d04d065627b0025bb37a2a75733c65bf9d0a9e4aeabcb" +
        "07eea223e6aa9c084b86e1c100c83af11bdba33ac34e80afaee202ff8511f4451b48e91490d773816eff332e8db21bbe" +
        "3e8bf30d0b4408bbe32b5db2695b413e1f87101ebc547446f01ec77105de81db454ad676e3401add8471a432da342518" +
        "e0497df6db7e59cb09bf3accb685617695d07cff6a875de4bec17368509d02ade555a92dd4abdaf6c44300536cadddab"),
        Hex(
        "66c7ea5ba0aef7301b3621bee45a1e7d80f1aaa066ebcd917b05b6356a63bab8f7e220d44619f45d65535e651f284cea" +
        "c5420ad9a90bd288cda759c49f8a395cf68b411861e54c179910132c7c44acc40e0deecc211d7bce5652ea8baa278490" +
        "a727f9e9730847d10bbb3a4d7b41413b8bccaba934a83de34d905b375ba683af2bd2dcb6098c01315954b751e2170945" +
        "e60327ada73d228f89783a5e97326a19d59c024a76a9e8e22edcbe6259ed1445f9a99a4cd2fb03daa8bae60ebf7c8ba2" +
        "cf47bbb2c31c6c8c711ba9e794a14dc236e6a0872a0d42af5da6826832b13d8012dd9bd153ab99e32dfce854f25755e5" +
        "83f75111f3554e0425c370e080641d788dedd19d61a74057d771017fc288fa228da4748a486bb9c0b77f999746d90ddf" +
        "1f45f98685a2045df195aed934ada09f0fc3880f5e2a3a23780f63b882ef40eda2a56b3b71a00d6ec238d2196d1a128c" +
        "7024befb6dbf2ce584df9d665b42b0bb4ae83e7fb543aef25f60b9b4284e8156f2aad496ea55ed7b62218029b656eed5"),
        3);

    public static readonly DhGroup Ephemeral = new(
        Hex(
        "c5d1fff6e1e0b5b5a4220a369a4f504d59c7482724053c0d4b05426328031633bc79249c1c58c91b32e6802f20a1e762" +
        "6859da201e7faad8406c702796cbdf3208a6cccb77baa29bec763a9a1fb868d79182f00957e890d762806b443e7fd2f7" +
        "5ef2eed5f56e92e5939ec15533a642b2212504b62ba72ca8e6c7fe28bbc8f687"),
        Hex(
        "62e8fffb70f05adad211051b4d27a826ace3a41392029e06a582a13194018b19de3c924e0e2c648d997340179050f3b1" +
        "342ced100f3fd56c20363813cb65ef9904536665bbdd514df63b1d4d0fdc346bc8c17804abf4486bb14035a21f3fe97b" +
        "af79776afab74972c9cf60aa99d321591092825b15d396547363ff145de47b43"),
        2);

    private static BigInteger Hex(string hex) => BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);
}
