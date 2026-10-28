using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public sealed class HardcoreBackgroundProgressTests
{
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    sealed class Character : IDisposable
    {
        public readonly ClientState Conn;
        public readonly AccountStash Source;
        public readonly string SourceKey;
        public readonly SqliteAccountStore Accounts;

        public Character(string league, AreaLayout? layout = null)
        {
            TestSaves.Ensure();
            string name = "HardcoreBackground" + Guid.NewGuid().ToString("N");
            string account = "BackgroundAccount" + Guid.NewGuid().ToString("N");
            var definition = LeagueCatalog.Default.Find(league)!;
            var id = Guid.NewGuid();
            Accounts = new(Saves.Db);
            Accounts.AddCharacter(new(id, Guid.NewGuid(), name, CharacterClass.Witch, 1, league, "1_1_2",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            CharacterSaves.Write(name, new([]));
            Conn = new(layout ?? new(ushort.MaxValue, "HardcoreBackground", 128, 128, 32, 32, null, []), 0, 1)
            {
                Character = name, CharacterId = id, Account = account, League = league,
                Hardcore = definition.Hardcore, Ssf = definition.Ssf, Ruthless = definition.Ruthless,
                ClassRow = SkillCatalog.WitchRow, PresentedRecentEnemyHits = (0, 0),
                Instance = new(),
            };
            Conn.Pools = Program.PoolsFor(Conn, 1);
            Conn.PlayerLife = Conn.PlayerMaxLife;
            Conn.PlayerMana = Conn.Pools.Mana;
            SourceKey = AccountStash.Key(account, league);
            Source = AccountStash.For(SourceKey, Program.Items);
            Source.AddGold(0); // Make the initial stash before the observation hook is installed.
        }

        public void Die()
        {
            lock (Conn.Monsters)
            {
                Conn.PlayerLife = 0;
                Program.RecordHardcoreDeath(Conn);
            }
        }

        public void Dispose()
        {
            Conn.Closed = true;
            PlayerInventories.Forget(Conn.Character!);
            AccountStash.Forget(SourceKey);
        }
    }

    sealed class Wire(TcpClient writer, TcpClient reader) : IDisposable
    {
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public Task Invoke(string name, ClientState conn, params object?[] tail) =>
            (Task)Method(name).Invoke(null, ["hardcore-background-test", Stream, Ciphers, conn, .. tail])!;

        public static async Task<Wire> Open()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var writer = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync();
            await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            return new(writer, await accepting);
        }

        public void Dispose() { writer.Dispose(); reader.Dispose(); }
    }

    [Theory]
    [InlineData("Hardcore", "Standard")]
    [InlineData("SSF Hardcore", "SSF Standard")]
    [InlineData("Hardcore Ruthless", "Void")]
    [InlineData("SSF Hardcore Ruthless", "Void")]
    [InlineData("Standard", "Standard")]
    public async Task Player_tick_cannot_credit_another_drop_after_a_queued_gold_notification_and_hardcore_death(
        string league, string destination)
    {
        using var character = new Character(league);
        using var wire = await Wire.Open();
        var conn = character.Conn;
        var gold = ItemCatalog.Create(Program.Items.Find(ItemDrops.GoldId)!);
        uint first = conn.Instance.Ground.Add(new(32, 32, gold with { StackSize = 5 }));
        uint second = conn.Instance.Ground.Add(new(32, 32, gold with { StackSize = 7 }));
        var credited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int intercepted = 0;
        bool held = false;
        Task? tick = null;
        var previous = Saves.FailWrite;
        try
        {
            // Observe the first real stash save without failing it, then hold its following socket notification.
            // Holding the gate before RunPlayerTick starts would stop an earlier panel update instead of gold.
            Saves.FailWrite = key =>
            {
                if (key == character.SourceKey && Interlocked.CompareExchange(ref intercepted, 1, 0) == 0)
                {
                    held = wire.Gate.Wait(0);
                    if (held) credited.TrySetResult();
                    else credited.TrySetException(new InvalidOperationException("The first gold save must precede its notification"));
                }
                return previous?.Invoke(key) ?? false;
            };
            tick = wire.Invoke("RunPlayerTick", conn);
            await credited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (conn.Monsters)
            {
                Assert.Equal(5u, character.Source.Gold);
                Assert.Null(conn.Instance.Ground.Get(first));
                Assert.NotNull(conn.Instance.Ground.Get(second));
                if (conn.Hardcore) character.Die();
                // Finish this production loop after its currently suspended iteration, without a timed race.
                conn.Closed = true;
            }
            wire.Gate.Release();
            held = false;
            await tick.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(conn.Hardcore ? 5u : 12u, character.Source.Gold);
            Assert.Equal(conn.Hardcore, conn.Instance.Ground.Get(second) is not null);
            var saved = character.Accounts.FindCharacter(conn.Character!)!;
            Assert.Equal(destination, saved.League);
            Assert.Equal(conn.Hardcore, saved.HardcoreDeath is not null);
            if (conn.Hardcore)
            {
                Assert.True(conn.HardcoreDeathRecorded);
                string targetKey = AccountStash.Key(conn.Account!, destination);
                Assert.Null(Saves.Get(SaveDatabase.Table.Stashes, targetKey));
            }
        }
        finally
        {
            Saves.FailWrite = previous;
            conn.Closed = true;
            if (held) wire.Gate.Release();
            if (tick is not null) await tick.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("Hardcore")]
    [InlineData("Standard")]
    public async Task Quest_progress_queued_before_death_cannot_change_the_migrated_character(string league)
    {
        using var character = new Character(league);
        using var wire = await Wire.Open();
        var conn = character.Conn;
        const string flag = "a1q1-KilledHillock";
        await conn.QuestGate.WaitAsync();
        Task queued;
        try
        {
            queued = wire.Invoke("SetQuestFlags", conn, new[] { flag }, true);
            Assert.False(queued.IsCompleted);
            character.Die();
        }
        finally { conn.QuestGate.Release(); }
        await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(!conn.Hardcore, conn.QuestFlags.Contains(flag));
        Assert.Equal(!conn.Hardcore, CharacterSaves.Load(conn.Character!)!.QuestFlags?.Contains(flag) == true);
    }

    [Theory]
    [InlineData("SyncQuestItems", "Hardcore")]
    [InlineData("SyncQuestItems", "Standard")]
    [InlineData("HandInQuestItems", "Hardcore")]
    [InlineData("HandInQuestItems", "Standard")]
    public async Task Quest_inventory_work_queued_before_death_keeps_the_migrated_carried_items(string operation, string league)
    {
        using var character = new Character(league);
        using var wire = await Wire.Open();
        var conn = character.Conn;
        var items = PlayerInventories.For(conn.Character!, Program.Items);
        var chest = items.Add(PlayerInventories.MainInventory, 0, 0,
            ItemCatalog.Create(Program.Items.Find(MercyMission.MedicineChest)!));
        string[] initial = operation == "SyncQuestItems"
            ? [MercyMission.HaveMedicineChest, MercyMission.NessaCongratulated] : [MercyMission.HaveMedicineChest];
        conn.QuestFlags.UnionWith(initial);
        CharacterSaves.ChangeQuestFlags(conn.Character!, initial, true);
        await conn.QuestGate.WaitAsync();
        Task queued;
        try
        {
            if (operation == "SyncQuestItems") queued = wire.Invoke(operation, conn, true);
            else queued = wire.Invoke(operation, conn, new NpcCatalog.Talk(0, 0, 0, "The Medicine Chest", "Quests", "", [])
                { StartFlag = MercyMission.NessaCongratulated });
            Assert.False(queued.IsCompleted);
            character.Die();
        }
        finally { conn.QuestGate.Release(); }
        await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(conn.Hardcore, items.Items(PlayerInventories.MainInventory).Any(i => i.Id == chest.Id));
        var save = CharacterSaves.Load(conn.Character!)!;
        Assert.Equal(conn.Hardcore, save.Inventories.GetValueOrDefault(PlayerInventories.MainInventory)?.Any() == true);
        Assert.Equal(conn.Hardcore, save.QuestFlags?.Contains(MercyMission.HaveMedicineChest) == true);
        if (operation == "HandInQuestItems")
            Assert.Equal(!conn.Hardcore, save.QuestFlags?.Contains(MercyMission.NessaCongratulated) == true);
    }

    [Theory]
    [InlineData("Hardcore")]
    [InlineData("Standard")]
    public async Task Delayed_glyph_placement_cannot_consume_the_migrated_characters_glyphs(string league)
    {
        using var character = new Character(league);
        using var wire = await Wire.Open();
        var conn = character.Conn;
        var items = PlayerInventories.For(conn.Character!, Program.Items);
        for (byte i = 0; i < GlyphQuest.Glyphs.Length; i++)
            items.Add(PlayerInventories.MainInventory, i, 0, ItemCatalog.Create(Program.Items.Find(GlyphQuest.Glyphs[i])!));
        character.Die();
        await wire.Invoke("PlaceGlyphs", conn).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(conn.Hardcore ? 3 : 0, items.Items(PlayerInventories.MainInventory).Count);
        Assert.Equal(!conn.Hardcore, CharacterSaves.Load(conn.Character!)!.QuestFlags?.Contains(GlyphQuest.PlacedGlyphs) == true);
    }

    [Theory]
    [InlineData("ActivateWaypoint", "Hardcore")]
    [InlineData("ActivateWaypoint", "Standard")]
    [InlineData("SendWaypoints", "Hardcore")]
    [InlineData("SendWaypoints", "Standard")]
    [InlineData("RecordVisit", "Hardcore")]
    [InlineData("RecordVisit", "Standard")]
    public async Task Progress_waiting_for_the_death_lock_cannot_change_the_migrated_character(string operation, string league)
    {
        var area = WaypointCatalog.Areas.Entries.First(e => e.IsTown && WaypointCatalog.Visits.For(e.Id).Count > 0);
        using var character = new Character(league, new(area.Hash16, area.Id, 128, 128, 32, 32, null, []));
        using var wire = await Wire.Open();
        using var started = new ManualResetEventSlim();
        var conn = character.Conn;
        Task queued;
        lock (conn.Monsters)
        {
            queued = Task.Run(async () =>
            {
                started.Set();
                if (operation == "RecordVisit") Method(operation).Invoke(null, [conn]);
                else if (operation == "ActivateWaypoint") await wire.Invoke(operation, conn, "queued before death");
                else await wire.Invoke(operation, conn);
            });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            character.Die();
        }
        await queued.WaitAsync(TimeSpan.FromSeconds(5));
        bool visit = operation == "RecordVisit";
        Assert.Equal(!conn.Hardcore, (visit ? conn.Visited : conn.Waypoints).Contains(area.Id));
        var save = CharacterSaves.Load(conn.Character!)!;
        Assert.Equal(!conn.Hardcore, (visit ? save.Visited : save.Waypoints)?.Contains(area.Id) == true);
    }
}
