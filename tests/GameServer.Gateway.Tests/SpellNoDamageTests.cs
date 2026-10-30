using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class SpellNoDamageTests
{
    const string Flag = "spell_skills_deal_no_damage";
    static MethodInfo Method(string name) =>
        typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    sealed class Fixture : IDisposable
    {
        readonly string name = "SpellNoDamage" + Guid.NewGuid().ToString("N");
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        public readonly TcpClient Reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public readonly ClientState Conn;
        public readonly SpawnedMonster Target;
        public readonly int Effect;
        public ushort Skill => Program.GemSkillIdPool.Value[0];
        public NetworkStream Stream => writer.GetStream();
        public PlayerInventories Items => PlayerInventories.For(name, Program.Items);

        public Fixture(string? effect = null, string? weapon = null, string? support = null, int targetX = 38)
        {
            TestSaves.Ensure();
            Conn = new(new(ushort.MaxValue, "SpellNoDamage", 300, 300, 32, 32, null, []), 0, 1)
            { Character = name, ClassRow = 1, PlayerSent = true };
            Set("global_cannot_crit", 1);
            Set("base_cast_speed_+%", 1000);
            Set("attack_speed_+%", 1000);
            // Use an ordinary equipment stat source for requirements; dev attributes are not pool inputs on main.
            var attributes = Program.Items.Mods.All.First(m => m.GenerationType is 1 or 2
                && m.Stats.Count == 1 && m.Stats[0].Id == "additional_all_attributes");
            var amulet = ItemCatalog.Create(Program.Items.Find("Paua Amulet")!) with
                { Rarity = ItemRarity.Rare, Mods = [new(ModSection.Explicit, attributes.Hash, [1000])] };
            PlayerInventories.For(name, Program.Items).Add(5, 0, 0, amulet);
            if (weapon is not null)
                PlayerInventories.For(name, Program.Items).Add(PlayerInventories.Weapon1, 0, 0,
                    ItemCatalog.Create(Program.Items.Find(weapon)!));
            Effect = effect is null ? -1 : Program.Skills.EffectRow(effect);
            if (effect is not null)
            {
                string gemId = effect switch
                {
                    "CausticArrow" => "PoisonArrow",
                    "ScorchingRay" => "FireBeam",
                    "KineticBlastAltX" => "KineticBlast",
                    _ => effect,
                };
                var gem = ItemCatalog.Create(Program.Items.Find($"Metadata/Items/Gems/SkillGem{gemId}")!);
                if (effect == "KineticBlastAltX")
                    gem = gem with { GemVariant = Program.Items.FindGem("Kinetic Blast of Clustering")!.Value.Variant.Index };
                var body = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
                    { Sockets = [new(SocketColour.White, gem)] };
                if (support is not null)
                    body = body with { Links = [2], Sockets = [new(SocketColour.White, gem),
                        new(SocketColour.White, ItemCatalog.Create(Program.Items.Find(support)!))] };
                PlayerInventories.For(name, Program.Items).Add(PlayerInventories.BodyArmour, 0, 0, body);
            }
            Conn.Pools = Program.PoolsFor(Conn, 100) with { Accuracy = int.MaxValue };
            Conn.PlayerLife = Conn.PlayerMaxLife;
            Conn.PlayerMana = (uint)Conn.Pools.Mana;
            Target = new("Spell suppression target", new(targetX, 32, 30), 0, 2, 100000)
                { Hostile = true, Level = 1 };
            Conn.Monsters.Add(42, Target);
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
        }

        public void Set(string stat, int value) => Conn.PlayerStats[Program.Stats.Key(stat)!.Value] = value;
        public DamagePacket Hit() => (DamagePacket)Method("SupportedHit").Invoke(null, [Conn, Skill])!;
        public Dictionary<uint, int> Panel() => ((IReadOnlyList<GrantedSkill>)Method("PlayerSkills")
            .Invoke(null, [Conn])!).Single(s => s.Id == Skill).Stats!.ToDictionary(s => s.Item1, s => s.Item2);

        public Task Invoke(string name, params object?[] args) => (Task)Method(name).Invoke(null,
            new object?[] { "spell-no-damage-test", Stream, Ciphers, Conn }.Concat(args).ToArray())!;

        public async Task Command(string line)
        {
            Conn.Commands ??= (ChatCommands)Method("Commands").Invoke(null,
                ["spell-no-damage-test", Stream, Ciphers, Conn])!;
            var input = new LoginWriter().U16(InstanceChat.ChatId).WStr(line).U8(0).ToArray();
            var messages = InstanceProtocol.SplitClientMessages(input, out var partial, out var unknown);
            Assert.Empty(partial); Assert.Empty(unknown); Assert.Single(messages);
            await Program.OnClientMessages("spell-no-damage-test", Stream, Ciphers, messages, [], Conn)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        public Task Dispatch(bool attack = false, bool instant = false)
        {
            ushort id = instant ? (ushort)0x191 : (ushort)0x24;
            var w = new LoginWriter().U16(id).U32(instant ? 0x1000u : 42u)
                .U16(attack ? SkillCatalog.FirstSkillId : Skill);
            if (!instant) w.U16(7).U16(1);
            return Program.OnClientMessages("spell-no-damage-test", Stream, Ciphers,
                [(id, w.ToArray())], [], Conn);
        }

        public void Dispose()
        {
            Conn.Closed = true; Conn.ProjectileRuntime?.Invalidate();
            writer.Dispose(); Reader.Dispose(); listener.Stop(); PlayerInventories.Forget(name);
        }
    }

    static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    static int Value(IReadOnlyDictionary<uint, int> stats, string name) =>
        stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);

    [Fact]
    public void Rampager_supplies_the_raw_key_without_admitting_the_class_or_allocating_its_node()
    {
        var node = new PassiveTree().Find(49937);
        Assert.NotNull(node);
        Assert.Equal("Rampager", node.Name);
        Assert.Equal(5341u, Program.Stats.Key(Flag));
        Assert.Equal(5341u, StatKeys.SpellSkillsDealNoDamage);
        Assert.Contains((5341u, 1), node.Stats);
    }

}
