using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ConditionalCriticalGatewayTests
{
    sealed class Draws(params int[] values) : Random
    {
        public int Calls;
        public override int Next(int maxValue)
        {
            Assert.Equal(10000, maxValue);
            int index = Interlocked.Increment(ref Calls) - 1;
            Assert.True(index < values.Length, "Unexpected critical draw");
            return values[index];
        }
    }

    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    sealed class Contact : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(), reader;
        readonly ConnectionCiphers ciphers = new(new byte[64], isClient: false);
        readonly ConnectionCiphers received = new(new byte[64], isClient: true);
        readonly string character = "Critical" + Guid.NewGuid().ToString("N");
        public ClientState Conn { get; }
        public SpawnedMonster Target { get; }
        public ushort Skill = SkillCatalog.FirstSkillId;
        public SemaphoreSlim SendGate => Program.SendGates.GetValue(ciphers, _ => new(1, 1));

        public Contact(Draws random, string? gemName = null, string? weaponName = null, int distance = 2,
            uint targetLife = 100000)
        {
            TestSaves.Ensure();
            Conn = new(new(ushort.MaxValue, "CriticalContact", 200, 200, 32, 32, (_, _) => true, []), 1, 1)
            { Character = character, ClassRow = 1, PlayerSent = true, PlayerLife = 50, PlayerMana = 1000,
                CriticalRandom = random, Instance = new InstanceState() };
            var inventory = PlayerInventories.For(character, Program.Items);
            if (weaponName is not null)
                inventory.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find(weaponName)!));
            if (gemName is not null)
            {
                var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGem" + gemName)!);
                var support = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemIncreasedCriticalDamage")!);
                var host = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
                { Sockets = [new(SocketColour.White, gem), new(SocketColour.White, support)], Links = [2] };
                inventory.Add(PlayerInventories.BodyArmour, 0, 0, host);
                Skill = Program.GemSkillIdPool.Value[0];
            }
            Set(AttackBaseCriticalStrike.UnarmedBaseStat, 1000);
            Set(AttackBaseCriticalStrike.WeaponOverrideStat, 1000);
            Set("extra_critical_rolls_while_on_low_life", 1);
            Set("physical_damage_+%", -100);
            Set("attack_minimum_added_chaos_damage", 1000);
            Set("attack_maximum_added_chaos_damage", 1000);
            Set("spell_minimum_added_chaos_damage", 1000);
            Set("spell_maximum_added_chaos_damage", 1000);
            Set("attack_speed_+%", 200);
            Refresh();
            Target = new("critical target", new(32 + distance, 32, 30), 0, 2, targetLife);
            Conn.Monsters.Add(42, Target);
            Conn.Clock.Start();
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }

        public void Set(string name, int value) => Conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;
        public void Refresh() => Conn.Pools = Program.PoolsFor(Conn, 40) with
        { Life = 100, Mana = 1000, Accuracy = int.MaxValue, Strength = 1000, Dexterity = 1000, Intelligence = 1000 };
        public int Multiplier => Skill == SkillCatalog.FirstSkillId
            ? Program.AttackCriticalMultiplier(Conn, Program.CharacterPlayerStats(Conn))
            : (int)Method("SupportedCriticalMultiplier").Invoke(null, [Conn, Skill])!;
        public DamagePacket Hit => (DamagePacket)Method("SupportedHit").Invoke(null, [Conn, Skill])!;

        public Task Use()
        {
            var bytes = new LoginWriter().U16(0x24).U32(42).U16(Skill).U16(0).U16(0x409).ToArray();
            var use = InstanceTransfer.ReadUseSkillOnObject(bytes);
            return ((Task)Method("OnUseObject").Invoke(null,
                ["critical-contact-test", writer.GetStream(), ciphers, Conn, use, Conn.Clock.Elapsed.TotalSeconds])!)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task HitLands()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Target.Life == Target.MaxLife) await Task.Delay(10, timeout.Token);
        }

        public async Task Refund(ushort node)
        {
            var bytes = new LoginWriter().U16(0x36).U32(node).U8(0).U8(0).ToArray();
            await Program.OnClientMessages("critical-refund-test", writer.GetStream(), ciphers,
                [(InstanceEntities.RefundPassiveId, bytes)], [], Conn);
        }

        public Task RunPanelTick() => (Task)Method("RunPlayerTick").Invoke(null,
            ["critical-panel-test", writer.GetStream(), ciphers, Conn])!;

        public IReadOnlyList<GrantedSkill> CurrentSkills() =>
            ((IEnumerable<GrantedSkill>)Method("PlayerSkills").Invoke(null, [Conn])!).ToList();

        public Task RefreshStats(bool charges) => (Task)Method(charges ? "SendChargeStats" : "SendCharacterStats")
            .Invoke(null, ["critical-queued-stats-test", writer.GetStream(), ciphers, Conn, "critical context"])!;

        public async Task AssertNextPlayerStats()
        {
            var stats = ((IEnumerable<(uint, int)>)Method("FinalPlayerStats").Invoke(null, [Conn])!).ToList();
            await AssertNextPacket(InstanceEntities.StatsUpdate(Program.PlayerId, stats));
        }

        public Task AssertNextActorStats() => AssertNextPacket(
            InstanceEntities.PlayerSkillsChange(Program.PlayerId, CurrentSkills(), []));

        async Task AssertNextPacket(byte[] expected)
        {
            byte[] actual = new byte[expected.Length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await reader.GetStream().ReadExactlyAsync(actual, timeout.Token);
            received.Receive.Process(actual);
            Assert.Equal(expected, actual);
        }

        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop();
            PlayerInventories.Forget(character);
            Program.GemSkillIdsOf.TryRemove(character, out _);
        }
    }

    [Theory]
    [InlineData(50u, 0, 2, true)]
    public async Task Parsed_default_contact_uses_live_life_and_signed_luck(uint life, int ordinary, int calls, bool crit)
    {
        var random = new Draws(9000, 0);
        using var f = new Contact(random);
        f.Set("extra_critical_rolls", ordinary);
        f.Set(CriticalStrikePolicy.UnarmedMeleeMultiplierStat, 50);
        var use = f.Use();
        f.Conn.PlayerLife = life; // Action already accepted; contact is still waiting.
        await use;
        Assert.Equal(calls, random.Calls);
        Assert.Equal(crit ? 2000u : 1000u, f.Target.MaxLife - f.Target.Life);
    }

}
