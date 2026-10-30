using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class VaalSoulGatewayTests
{
    static object Call(string name, params object?[] args)
    {
        var method = typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        return method.Invoke(null, args.Concat(method.GetParameters().Skip(args.Length)
            .Select(p => p.DefaultValue)).ToArray())!;
    }
    static void Set(ClientState conn, string name, int value) => conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;

    internal sealed class Fixture : IDisposable
    {
        public readonly ClientState Conn;
        public readonly PlayerInventories Items;
        readonly uint host;
        uint nextMonster = 42;
        public Fixture(int copies = 1, uint? ticket = null, string? character = null, ushort area = 1, bool noCharacter = false)
        {
            TestSaves.Ensure();
            Conn = new(new AreaLayout(area, "vaal-test", 1000, 1000, 100, 100, null, []),
                ticket ?? (uint)Random.Shared.Next(1, int.MaxValue), 1)
            {
                Character = noCharacter ? null : character ?? $"Vaal{Guid.NewGuid():N}", PlayerLife = 1000, PlayerMana = 1000,
                PlayerSent = true, Instance = new(), Experience = CharacterStats.ExperienceFor(100),
                Pools = new(100, 1000, 1000, int.MaxValue, 0,
                    Strength: 1000, Dexterity: 1000, Intelligence: 1000)
            };
            Items = PlayerInventories.For(Conn.Character ?? $"UnusedVaal{Guid.NewGuid():N}", Program.Items);
            foreach (var item in Items.Items(PlayerInventories.Weapon1).ToArray()) Items.Remove(PlayerInventories.Weapon1, item.Id);
            host = Items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Club")!)).Id;
            Copies(copies);
            Set(Conn, "global_cannot_crit", 1);
            Set(Conn, "attack_minimum_added_chaos_damage", 100);
            Set(Conn, "attack_maximum_added_chaos_damage", 100);
            // These test characters intentionally meet gem requirements through real raw developer replacements.
            foreach (string attribute in new[] { "strength", "dexterity", "intelligence" })
                Set(Conn, $"virtual_{attribute}_for_requirements", 1000);
        }
        public void Copies(int count) => GemLevels(Enumerable.Repeat(1, count).ToArray());
        public void Gems(params string[] names) => Items.Change(PlayerInventories.Weapon1, host, item => item with
        {
            Sockets = names.Select(name => new ItemSocket(SocketColour.White,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)!))).ToArray(),
            Links = names.Select(_ => (byte)1).ToArray()
        });
        public void Links(params byte[] groups) => Items.Change(PlayerInventories.Weapon1, host,
            item => item with { Links = groups });
        public void GemLevels(params int[] levels) => Items.Change(PlayerInventories.Weapon1, host, item => item with
        {
            Sockets = levels.Select(level =>
            {
                var type = Program.Items.Find("Metadata/Items/Gems/SkillGemVaalFireball")!;
                return new ItemSocket(SocketColour.White, ItemCatalog.Create(type) with
                    { GemExperience = Program.Items.Gem(type)!.ExperienceFor(level) });
            }).ToArray(),
            Links = levels.Length == 0 ? [] : [(byte)levels.Length]
        });
        public SpawnedMonster Monster(bool corpse = true, int distance = 5, bool hostile = true,
            MonsterRarity rarity = MonsterRarity.Normal)
        {
            var monster = new SpawnedMonster("soul target", new(100 + distance, 100, 10, width: 1000, height: 1000), 0, 2, 1)
            {
                Life = corpse ? 0u : 1u, Dead = corpse, Hostile = hostile, Rarity = rarity,
                Variety = new(0, "soul-test-monster", "soul target", "Metadata/Monsters/Monster", null, null),
            };
            Conn.Monsters.Add(nextMonster++, monster);
            return monster;
        }
        public uint Id(SpawnedMonster monster) => Conn.Monsters.Single(kv => ReferenceEquals(kv.Value, monster)).Key;
        public bool Kill(int distance = 5) { var m = Monster(distance: distance); return Program.CollectVaalSoulOnKill(Conn, Id(m), m); }
        public VaalSoulBudget Budget => Assert.Single(Conn.VaalSouls.Read());
        public void Dispose() { Conn.Closed = true; if (Conn.Character is not null) PlayerInventories.Forget(Conn.Character); }
    }

    // Same encrypted in-memory dispatcher transport as existing Rage/cooldown tests, no game server or client.
    internal sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(), reader;
        readonly ConnectionCiphers receive = new(new byte[64], isClient: true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public NetworkStream Stream => writer.GetStream();
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public Wire()
        {
            listener.Start(); var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }
        public async Task<byte[]> Read(int count)
        {
            byte[] bytes = new byte[count]; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await reader.GetStream().ReadExactlyAsync(bytes, timeout.Token); receive.Receive.Process(bytes); return bytes;
        }
        public async Task<byte[]> Through(byte[] expected)
        {
            var bytes = new List<byte>(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            byte[] chunk = new byte[65536];
            while (bytes.ToArray().AsSpan().IndexOf(expected) < 0)
            {
                int count = await reader.GetStream().ReadAsync(chunk, timeout.Token); Assert.True(count > 0);
                receive.Receive.Process(chunk.AsSpan(0, count)); bytes.AddRange(chunk.Take(count));
            }
            return [.. bytes];
        }
        public Task Dispatch(ClientState conn, uint target, ushort skill)
        {
            ushort id = InstanceTransfer.UseSkillOnObjectId;
            var bytes = new LoginWriter().U16(id).U32(target).U16(skill).U16(1).U16(1).ToArray();
            return Program.OnClientMessages("soul-dispatch-test", Stream, Ciphers, [(id, bytes)], [], conn);
        }
        public void Dispose() { writer.Dispose(); reader.Dispose(); listener.Stop(); }
    }

    static async Task<Task> HoldLethalNotifications(Fixture f, Wire wire, SpawnedMonster monster, string path)
    {
        uint id = f.Id(monster);
        if (path == "default")
        {
            Task attack;
            lock (f.Conn.Monsters)
            {
                attack = Program.Attack("soul-held-default", wire.Stream, wire.Ciphers, f.Conn,
                    new(id, SkillCatalog.FirstSkillId, 1, 1), monster, f.Conn.Clock.Elapsed.TotalSeconds);
                // Initial input was accepted unpaused. Freeze contact until its action echo has passed the gate.
                f.Conn.Clock.Stop();
            }
            try
            {
                await wire.Through(InstanceProtocol.ActionStarted(0x1000, 100, 100, 105, 100,
                    SkillCatalog.FirstSkillId, 1, 1, targetObjectId: id));
                await wire.Gate.WaitAsync();
            }
            finally { f.Conn.Clock.Start(); }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                lock (f.Conn.Monsters) if (monster.Dead) break;
                await Task.Delay(5, timeout.Token);
            }
            return attack;
        }
        await wire.Gate.WaitAsync();
        if (path == "shared")
            return (Task)Call("HitMonster", "soul-held-hit", wire.Stream, wire.Ciphers, f.Conn, id, monster,
                10, "soul test", false, false, (ushort)0, null, null, null);
        var rf = new RighteousFireState(1, 2, null, SkillCatalog.FirstSkillId, 6000, 0, 1000);
        f.Conn.RighteousFire = rf;
        return (Task)Call("BurnAround", "soul-held-rf", wire.Stream, wire.Ciphers, f.Conn, rf, 1.0);
    }

    public static IEnumerable<object[]> NotificationChanges()
    {
        foreach (string path in new[] { "shared", "rf", "default" })
            foreach (string change in new[] { "prevention-clear", "equip", "prevention-start", "remove-readd", "death", "transfer" })
                yield return [path, change];
        foreach (string path in new[] { "shared", "rf" })
            foreach (string change in new[] { "move-in", "move-out" }) yield return [path, change];
    }

    [Fact]
    public async Task A_stale_death_notification_cannot_collect_souls_without_a_lethal_mutation()
    {
        using var f = new Fixture(); using var wire = new Wire(); var corpse = f.Monster();
        await (Task)Call("MonsterKilled", "stale-soul-death", wire.Stream, wire.Ciphers, f.Conn, f.Id(corpse), corpse);
        Assert.Equal(0u, f.Budget.Current);
    }

    static GrantedSkill[] GemRecords(ClientState conn) => ((IReadOnlyList<GrantedSkill>)Call("PlayerSkills", conn))
        .Where(s => s.Id != SkillCatalog.FirstSkillId).ToArray();
    static int AuraSelector(GrantedSkill skill) => skill.Stats!.Single(s => s.Key == Program.Stats.Key(VaalSoulCost.Aura)).Value;
    // Reflection lets this regression file compile against the original commit during the recorded RED run.
    static bool CatalogAura(int effect) => (bool)typeof(SkillCatalog).GetMethod("IsAura")!
        .Invoke(Program.Skills, [effect, null])!;

}
