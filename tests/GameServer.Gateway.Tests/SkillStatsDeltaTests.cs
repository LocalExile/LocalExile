using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;
// The 0x1b4 skill stats update: the refresh path through a real connection (a flask lowering the Artillery Ballista's
// mana_cost), and the update's bytes pinned and read back with this file's own decoder.
public sealed class SkillStatsDeltaTests
{
    // One 0x01b4 read back, for the log line: entity header, u16 skill, u8 stat set, compact count, compact pairs.
    static (ushort Skill, byte Set, List<(uint Stat, int Value)> Pairs) Decode(byte[] record)
    {
        var read = Decode(record, 0, out int end);
        Assert.Equal(record.Length, end);
        return read;
    }

    // The same, for the record that starts at `start` of a write that carries several; `end` is where the next begins.
    static (ushort Skill, byte Set, List<(uint Stat, int Value)> Pairs) Decode(byte[] record, int start, out int end)
    {
        Assert.Equal(InstanceEntities.SkillStatsUpdateId, (ushort)(record[start] << 8 | record[start + 1]));
        int at = start + 12;
        ushort skill = (ushort)(record[at] << 8 | record[at + 1]);
        at += 2;
        byte set = record[at++];
        uint count = Unsigned(record, ref at);
        var pairs = new List<(uint Stat, int Value)>();
        for (uint i = 0; i < count; i++)
        {
            uint key = Unsigned(record, ref at);
            pairs.Add((key, Signed(record, ref at)));
        }
        end = at;
        return (skill, set, pairs);
    }

    // InstanceProtocol.CompactUInt / CompactInt, read back.
    static uint Unsigned(byte[] bytes, ref int at)
    {
        byte first = bytes[at];
        int length = first switch { < 0x80 => 1, < 0xc0 => 2, < 0xe0 => 3, < 0xf0 => 4, _ => 5 };
        uint value = length == 5 ? 0u : (uint)(first & (0x7f >> (length - 1)));
        for (int i = 1; i < length; i++) value = value << 8 | bytes[at + i];
        at += length;
        return value;
    }

    static int Signed(byte[] bytes, ref int at)
    {
        byte first = bytes[at];
        if (first >= 0xf0)
        {
            at += 5;
            return (bytes[at - 4] << 24) | (bytes[at - 3] << 16) | (bytes[at - 2] << 8) | bytes[at - 1];
        }
        int length = first switch { < 0x80 => 1, < 0xc0 => 2, < 0xe0 => 3, _ => 4 };
        int bits = 7 - (length - 1) + 8 * (length - 1);
        long value = first & (0x7f >> (length - 1));
        for (int i = 1; i < length; i++) value = value << 8 | bytes[at + i];
        at += length;
        long sign = 1L << (bits - 1);
        return (int)((value & sign) != 0 ? value - (sign << 1) : value);
    }

    // The flask fixture both tests share: a player with a Crude Bow socketed with Artillery Ballista, and stat-source
    // overrides the test drives (the Topaz's base_mana_cost_-% among them). Nothing here waits on real time.
    sealed class FlaskFixture : IDisposable
    {
        public Player Player { get; }
        public Dictionary<uint, int> Overrides { get; } = new();
        readonly string _path;
        readonly IGameLogic? _previousLogic;
        readonly IDisposable _exemptions;
        readonly IDisposable _statSources;
        readonly GameServer.Modding.IModuleLifetime? _previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime _lifetime = new("Game");
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly TcpClient _writer = new();
        readonly TcpClient _reader;
        readonly Task _drain;
        readonly ConnectionCiphers _ciphers = new(new byte[64], false);
        readonly Action<Player, byte[]>? _previousOutgoing = Program.Outgoing;
        // Everything the server wrote for the player, one entry per write.
        public List<byte[]> Packets { get; } = [];

        public FlaskFixture()
        {
            _path = Path.Combine(Path.GetTempPath(), $"localexile-skilldelta-{Guid.NewGuid():N}.db");
            _previousLogic = Program.GameLogicOrNull;
            Player player = null!;
            _exemptions = GameEvents.RequirementExemptions.Add("Game",
                p => ReferenceEquals(p, player)
                    ? [.. Game.ItemsOf(p).EquipmentSnapshot().Select(e => e.Item.Id)]
                    : []);
            _statSources = GameEvents.PlayerStatSources.Add("Game", (p, s) =>
            {
                if (!ReferenceEquals(p, player) || Overrides.Count == 0) return s;
                var merged = new Dictionary<uint, int>(s);
                foreach (var (k, v) in Overrides) merged[k] = v;
                return merged;
            });
            Saves.Open(_path);
            Game.CombatLifetime = _lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(_lifetime));
            player = new Player(new(1, "SkillDeltaChar", 1000, 1000, 100, 100, null, []), 1, 1)
            {
                Character = { Name = "SkillDeltaChar", Experience = CharacterStats.ExperienceFor(100) },
                PlayerLife = 1000, PlayerMana = 1000, Instance = new(), Connection = { PlayerSent = true },
                Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
            };
            Player = player;
            // A real connection to a socket nobody reads, so the send gate runs as in game; Program.Outgoing records
            // what was written.
            _listener.Start();
            var accept = _listener.AcceptTcpClientAsync();
            _writer.Connect(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
            _reader = accept.GetAwaiter().GetResult();
            player.Connection.Session = new("test", _writer.GetStream(), _ciphers);
            _drain = Task.Run(async () =>
            {
                try { var sink = new byte[65536]; while (await _reader.GetStream().ReadAsync(sink) > 0) { } }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            });
            Program.Outgoing = (p, bytes) => { if (ReferenceEquals(p, player)) Packets.Add(bytes.ToArray()); };
            var bow = ItemCatalog.Create(Program.Items.Find("Crude Bow")!);
            var gemType = Program.Items.FindGem("Artillery Ballista")!.Value;
            var ballista = ItemCatalog.Create(gemType.Type) with { GemVariant = gemType.Variant.Index };
            Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, bow with
            {
                Sockets = [new ItemSocket(SocketColour.Green, ballista)], Links = [1],
            });
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) Overrides[Program.Stats.Key(name)!.Value] = 1000;
        }

        // The 0x1b4 records the server wrote since the last Packets.Clear(), read back; a write that starts with one carries
        // nothing but them.
        public List<(ushort Skill, byte Set, List<(uint Stat, int Value)> Pairs)> SentDeltas()
        {
            var found = new List<(ushort, byte, List<(uint, int)>)>();
            foreach (var packet in Packets.ToArray())
            {
                int at = 0;
                while (at + 2 <= packet.Length && (packet[at] << 8 | packet[at + 1]) == InstanceEntities.SkillStatsUpdateId)
                    found.Add(Decode(packet, at, out at));
                if (at > 0) Assert.Equal(packet.Length, at);
            }
            return found;
        }

        public bool SentFullSkillList() => Packets.ToArray().Any(p => p.Length >= 2 && (p[0] << 8 | p[1]) == 0x02bc);

        public IReadOnlyList<GrantedSkill> Skills()
        {
            Player.PlayerSkillsCache = null;
            return Game.PlayerSkills(Player);
        }

        public GrantedSkill Ballista(IReadOnlyList<GrantedSkill> skills) =>
            skills.Single(s => Game.SkillEffectId(Player, s.Id) == "ArtilleryBallista");

        public void Dispose()
        {
            Player.Closed = true;
            _lifetime.Stop(_ => { });
            try { PlayerInventories.Forget(Player.Character.Name!); } catch (Exception) { }
            _statSources.Dispose();
            _exemptions.Dispose();
            Program.Outgoing = _previousOutgoing;
            Game.CombatLifetime = _previousLifetime;
            Program.SetGameLogic(_previousLogic!);
            _reader.Dispose(); _writer.Dispose(); _listener.Stop(); _drain.GetAwaiter().GetResult();
            TestEnvironment.ReleaseSaves(_path);
        }
    }

    // Test 1: the real path. The Topaz's "25% reduced Mana Cost of Skills during Effect" reaching the character as
    // base_mana_cost_-% lowers the Artillery Ballista's mana_cost. PublishStatValues must put exactly one 0x1b4 on the wire
    // (the Ballista's own stats, the new cost) and no 0x2bc; nothing more when nothing changed again; and a skill that
    // went still takes the whole list.
    [Fact]
    public async Task A_stat_value_change_sends_one_skill_stats_update_and_a_skill_set_change_the_whole_list()
    {
        using var fixture = new FlaskFixture();
        var player = fixture.Player;
        uint manaCost = Program.Stats.Key("mana_cost")!.Value;
        uint reduction = Program.Stats.Key("base_mana_cost_-%")!.Value;

        // What the client was given at entry: the whole list, recorded as the full refresh records it.
        var before = fixture.Skills();
        Game.RecordPlayerSkills(player, before);
        player.ClientSkillIds = [.. before.Select(s => s.Id)];
        int costBefore = fixture.Ballista(before).Stats!.Single(s => s.Key == manaCost).Value;

        fixture.Overrides[reduction] = 20;
        var after = fixture.Skills();
        var ballista = fixture.Ballista(after);
        int costAfter = ballista.Stats!.Single(s => s.Key == manaCost).Value;
        Assert.True(costAfter < costBefore, $"the flask must lower the cost: {costBefore} -> {costAfter}");

        Assert.True(Game.CurrentGameWork, "the Game module's rules must be current, or the refresh returns at once");
        fixture.Packets.Clear();
        await Game.PublishStatValues(player);
        var sent = fixture.SentDeltas();
        // One 0x1b4 for each skill whose own stats moved, and for no other.
        var moved = after.Where(s => !before.Single(b => b.Id == s.Id).Stats!.SequenceEqual(s.Stats!)).Select(s => s.Id).Order();
        Assert.Equal(moved, sent.Select(d => d.Skill).Order());
        Assert.All(sent, d => Assert.Equal(0, d.Set));
        var update = Assert.Single(sent, d => d.Skill == ballista.Id);
        Assert.Equal(costAfter, Assert.Single(update.Pairs, p => p.Stat == manaCost).Value);
        Assert.False(fixture.SentFullSkillList(), "a stat value change must not resend the whole skill list");
        Assert.Equal(costAfter, Game.SentSkillStat(player, ballista.Id, manaCost));

        // Nothing changed again: nothing for the skills.
        fixture.Packets.Clear();
        await Game.PublishStatValues(player);
        Assert.Empty(fixture.SentDeltas());
        Assert.False(fixture.SentFullSkillList());

        bool IsWhole(byte[][] batches) => batches.Length > 0 && (batches[0][0] << 8 | batches[0][1]) == 0x02bc;

        // (a) A held guard: a value-only change takes the whole list, not a 0x1b4.
        Game.TriggeredGuards.AddOrUpdate(player, new Game.TriggeredGuard(player.ProjectileEpoch, Game.CombatLifetime));
        try
        {
            fixture.Overrides[reduction] = 10;
            Assert.True(IsWhole(Game.PlayerSkillsBatchesIfChanged(player, fixture.Skills(), out bool heldWhole)));
            Assert.True(heldWhole);
        }
        finally { Game.TriggeredGuards.Remove(player); }

        // (b) A shape change (the Ballista's cooldown): the whole list.
        fixture.Overrides[reduction] = 5;
        var reshaped = fixture.Skills().Select(s => s.Id == ballista.Id ? s with { CooldownMs = s.CooldownMs + 100 } : s).ToList();
        Assert.True(IsWhole(Game.PlayerSkillsBatchesIfChanged(player, reshaped, out bool shapeWhole)));
        Assert.True(shapeWhole);

        // (c) SendSkills with nothing changed still resyncs the client with the whole list. First put the real list on record
        // (the shape differs from (b)'s), then a value change as a 0x1b4, then the SendSkills.
        fixture.Overrides[reduction] = 10;
        await Game.PublishStatValues(player);
        fixture.Overrides[reduction] = 15;
        fixture.Packets.Clear();
        await Game.PublishStatValues(player);
        Assert.NotEmpty(fixture.SentDeltas());
        Assert.False(fixture.SentFullSkillList());
        fixture.Packets.Clear();
        await Game.SendSkills(player);
        Assert.True(fixture.SentFullSkillList(), "SendSkills with nothing changed must still send the whole list");
        Assert.Empty(fixture.SentDeltas());

        // A skill that went: the whole list, a 0x2bc Actor update, and it is recorded.
        var fewer = after.Where(s => s.Id != ballista.Id).ToList();
        byte[][] whole = Game.PlayerSkillsBatchesIfChanged(player, fewer);
        Assert.NotEmpty(whole);
        Assert.Equal(0x02bc, (ushort)(whole[0][0] << 8 | whole[0][1]));
        Assert.Empty(Game.PlayerSkillsIfChanged(player, fewer));
        Assert.Null(Game.SentSkillStat(player, ballista.Id, manaCost));
    }

    // Test 2: the 0x1b4 bytes, pinned. Skill 0x0102, additional stat set 1, pairs (100, 12) and (300, -5): header (id,
    // entity, order key, checkpoint), u16 skill, u8 set, compact count, then compact key and compact value of each pair.
    [Fact]
    public void A_skill_stats_update_carries_its_header_skill_set_and_compact_pairs()
    {
        byte[] bytes = InstanceEntities.SkillStatsUpdate(0x01020304, 0x0102, 1, [(100, 12), (300, -5)]);
        Assert.Equal([0x01, 0xb4, 0x01, 0x02, 0x03, 0x04, 0, 0, 0, 0, 0, 0, 0x01, 0x02, 0x01, 0x02,
            0x64, 0x0c, 0x81, 0x2c, 0x7b], bytes);
        var read = Decode(bytes);
        Assert.Equal((0x0102, (byte)1), (read.Skill, read.Set));
        Assert.Equal([(100u, 12), (300u, -5)], read.Pairs);
    }
}
