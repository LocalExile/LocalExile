using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public sealed class GameModeMergeIntegrationTests : IDisposable
{
    readonly List<string> characters = [];
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name,
        BindingFlags.Static | BindingFlags.NonPublic)!;

    ClientState Connection(bool ruthless = false, InstanceState? instance = null, bool ssf = false, bool hardcore = false)
    {
        TestSaves.Ensure();
        string name = "ModeMerge" + Guid.NewGuid().ToString("N");
        characters.Add(name);
        var conn = new ClientState(new(ushort.MaxValue, "mode-merge", 300, 300, 10, 10, null, []), 1, 1)
        {
            Character = name, ClassRow = SkillCatalog.WitchRow, Ruthless = ruthless, Ssf = ssf, Hardcore = hardcore,
            League = ruthless ? "Ruthless" : "Standard", Instance = instance ?? new(),
            PlayerSent = true, PlayerLife = 100, PlayerMana = 100,
        };
        CharacterSaves.SetExperience(name, CharacterStats.ExperienceFor(40));
        conn.Experience = CharacterStats.ExperienceFor(40);
        conn.Pools = Program.PoolsFor(conn, 40);
        conn.PlayerLife = conn.PlayerMaxLife;
        conn.PlayerMana = conn.Pools.Mana;
        return conn;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Projectile_kill_commits_mode_rewards_once_while_output_is_held(bool ruthless)
    {
        var conn = Connection(ruthless);
        var catalog = Program.ItemsFor(conn);
        var host = PlayerInventories.For(conn.Character!, catalog).Add(PlayerInventories.Weapon1, 0, 0,
            ItemCatalog.Create(catalog.Find("Driftwood Wand")!) with
            { Sockets = [new(SocketColour.White, ItemCatalog.Create(catalog.Find("Fireball")!))] });
        var variety = Program.Monsters.Find("Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal")!;
        var target = new SpawnedMonster("merge reward target", new(20, 10, 0), 0, 2, 1)
        { Variety = variety, Level = 40, Rarity = MonsterRarity.Rare };
        conn.Monsters.Add(42, target);
        uint xp = Progression.MonsterExperience(Program.Monsters.BaseExperience(variety, 40)!.Value,
            variety.ExperienceMultiplier, target.Rarity, 40, 40);
        uint before = conn.Experience;
        await using var runtime = new ProjectileRuntimeFixture(conn);
        await runtime.HoldOutput();
        // Match the emitted flight's independent reward stream; normal and Ruthless use their own public drop rules.
        runtime.Release(runtime.Shot(offense: new(DamagePacket.Of(DamageType.Physical, 1000, 1000),
            true, int.MaxValue, 0, 150, "mode merge")));
        var flight = Assert.Single(runtime.Session.Runtime.Projectiles);
        var random = new Random(unchecked(flight.Use.Seed * 397 ^ (int)flight.Id) ^ 0x5e418ac3);
        var balance = ruthless ? Program.RuthlessSettings.Current : null;
        var expected = Program.DropsFor(conn).Roll(40, target.Rarity, random, 0,
            balance?.ItemDropMultiplier ?? 1, balance?.EquipmentRarityMultiplier ?? 1).ToList();
        if (Program.DropsFor(conn).Gold(40, target.Rarity, random, balance?.GoldMultiplier ?? 1) is { } gold)
            expected.Add(gold);

        runtime.Advance(2);

        Assert.True(target.Dead);
        Assert.Equal(before + xp, conn.Experience);
        double carry = 0;
        int gemXp = ruthless ? RuthlessGameplay.GemExperience(xp, balance!.GemExperienceMultiplier, ref carry)
            : (int)(xp / 10);
        var inventory = PlayerInventories.For(conn.Character!, catalog);
        Assert.Equal(gemXp, Assert.Single(inventory.Items(PlayerInventories.Weapon1), i => i.Id == host.Id)
            .Item.Sockets[0].Gem!.GemExperience);
        var drops = conn.Instance.Ground.All();
        Assert.Equal(expected.Select(d => (d.Item.TypeHash, d.Item.StackSize)),
            drops.Select(d => (d.Item.Item.TypeHash, d.Item.Item.StackSize)));
        Assert.Equal(0, runtime.Available);
        runtime.Advance(3);
        Assert.Equal(before + xp, conn.Experience);
        Assert.Equal(drops.Count, conn.Instance.Ground.All().Count);
        Assert.Equal(gemXp, inventory.Items(PlayerInventories.Weapon1).Single(i => i.Id == host.Id)
            .Item.Sockets[0].Gem!.GemExperience);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task Shared_town_presence_does_not_allow_SSF_trade_requests_or_acceptance(bool ssfA, bool ssfB, bool allowed)
    {
        var instance = new InstanceState();
        var a = Connection(instance: instance, ssf: ssfA);
        var b = Connection(instance: instance, ssf: ssfB);
        var sent = new List<ushort>();
        a.Outbox = b.Outbox = (bytes, _) =>
        { sent.Add((ushort)((bytes[0] << 8) | bytes[1])); return Task.CompletedTask; };
        instance.PublishPlayer(new(b.PeerId, 0, null!, 10, 10, b.PlayerLife, b));
        instance.PublishPlayer(new(a.PeerId, 0, null!, 10, 10, a.PlayerLife, a));
        Assert.Equal(allowed, Program.PlayerTradeAllowed(a, b));
        await (Task)Method("RequestTrade").Invoke(null, ["mode-merge", null, null, a, b.PeerId])!;
        Assert.Equal(allowed ? b.PeerId : 0u, a.TradeRequestTo);
        Assert.Equal(allowed, sent.Count > 0);
        // A forged acceptance must apply the same authoritative rule as the request.
        a.TradeRequestTo = b.PeerId;
        await (Task)Method("AcceptTradeRequest").Invoke(null, ["mode-merge", b, a.PeerId])!;
        Assert.Equal(allowed, a.Trade is not null);
        Assert.Equal(allowed, b.Trade is not null);
    }

    [Theory]
    [InlineData("Hardcore")]
    [InlineData("Ruthless")]
    [InlineData("leaving")]
    [InlineData("dead")]
    [InlineData("different instance")]
    public void Trading_refuses_different_modes_and_terminal_or_departing_owners(string reason)
    {
        var a = Connection();
        var b = Connection(ruthless: reason == "Ruthless",
            instance: reason == "different instance" ? new() : a.Instance, hardcore: reason == "Hardcore");
        switch (reason)
        {
            case "leaving": b.LeavingArea = true; break;
            case "dead": b.HardcoreDeathRecorded = true; break;
        }
        Assert.False(Program.PlayerTradeAllowed(a, b));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Projectile_flask_gain_obeys_the_active_Ruthless_utility_rule(bool ruthless)
    {
        var conn = Connection(ruthless);
        var catalog = Program.ItemsFor(conn);
        var type = catalog.Find("Quicksilver Flask")!;
        var flask = PlayerInventories.For(conn.Character!, catalog).Add(PlayerInventories.Flask, 0, 0,
            ItemCatalog.Create(type) with { Charges = 0 });
        conn.UtilityFlaskEffects.Start(new(flask.Id, 0, "Quicksilver", catalog.UtilityFlaskOf(type)!, 1, null,
            [], 0, 1000));
        await using var runtime = new ProjectileRuntimeFixture(conn);
        Func<InventoryItem, IReadOnlyDictionary<string, int>, double> gain = (_, _) => 10;
        Method("CommitProjectileFlaskGain").Invoke(null, [runtime.Session, gain]);
        Assert.Equal(ruthless ? 0 : 10, PlayerInventories.For(conn.Character!, catalog)
            .Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges);
    }

    static PlayerInventories.Shape? Shape(Item item) => Program.Items.ByHash(item.TypeHash) is { } type
        ? new(type.Width, type.Height, type.Class, type.MaxStack) : null;

    static PlayerTrade OfferTrade(ClientState a, ClientState b)
    {
        var first = PlayerInventories.For(a.Character!, Program.ItemsFor(a));
        var second = PlayerInventories.For(b.Character!, Program.ItemsFor(b));
        var ring = first.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Ring")!));
        var amulet = second.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(Program.Items.Find("Paua Amulet")!));
        first.Offer(PlayerInventories.MainInventory, ring.Id, 0, 0, Shape);
        second.Offer(PlayerInventories.MainInventory, amulet.Id, 0, 0, Shape);
        var trade = new PlayerTrade(a, b);
        a.Trade = b.Trade = trade;
        return trade;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Trade_completion_preserves_each_item_once_and_refuses_a_newly_dead_owner(bool ruthless, bool dead)
    {
        var a = Connection(ruthless);
        var b = Connection(ruthless, a.Instance);
        var trade = OfferTrade(a, b);
        b.HardcoreDeathRecorded = dead;
        await (Task)Method("CompletePlayerTrade").Invoke(null, [trade])!;
        Assert.True(trade.Over);
        foreach (var conn in new[] { a, b })
        {
            PlayerInventories.Forget(conn.Character!);
            var items = PlayerInventories.For(conn.Character!, Program.ItemsFor(conn));
            Assert.Empty(items.Offered());
            uint ring = Program.Items.Find("Iron Ring")!.Hash;
            uint amulet = Program.Items.Find("Paua Amulet")!.Hash;
            bool ownsRing = dead ? ReferenceEquals(conn, a) : ReferenceEquals(conn, b);
            Assert.Equal(ownsRing ? 1 : 0, items.Items(PlayerInventories.MainInventory).Count(i => i.Item.TypeHash == ring));
            Assert.Equal(ownsRing ? 0 : 1, items.Items(PlayerInventories.MainInventory).Count(i => i.Item.TypeHash == amulet));
        }
    }

    [Fact]
    public async Task A_failed_second_trade_save_rolls_back_both_inventories()
    {
        var a = Connection(); var b = Connection(instance: a.Instance);
        var trade = OfferTrade(a, b);
        string beforeA = Saves.Get(GameServer.Storage.SaveDatabase.Table.Characters, a.Character!)!;
        string beforeB = Saves.Get(GameServer.Storage.SaveDatabase.Table.Characters, b.Character!)!;
        var previous = Saves.FailWrite;
        try
        {
            Saves.FailWrite = key => key == b.Character || (previous?.Invoke(key) ?? false);
            await Assert.ThrowsAsync<IOException>(() => (Task)Method("CompletePlayerTrade").Invoke(null, [trade])!);
        }
        finally { Saves.FailWrite = previous; }
        Assert.False(trade.Over);
        Assert.Equal(beforeA, Saves.Get(GameServer.Storage.SaveDatabase.Table.Characters, a.Character!));
        Assert.Equal(beforeB, Saves.Get(GameServer.Storage.SaveDatabase.Table.Characters, b.Character!));
        PlayerInventories.Forget(a.Character!); PlayerInventories.Forget(b.Character!);
        Assert.Single(PlayerInventories.For(a.Character!, Program.Items).Items(PlayerInventories.MainInventory),
            i => i.Item.TypeHash == Program.Items.Find("Iron Ring")!.Hash);
        Assert.Single(PlayerInventories.For(b.Character!, Program.Items).Items(PlayerInventories.MainInventory),
            i => i.Item.TypeHash == Program.Items.Find("Paua Amulet")!.Hash);
    }

    [Fact]
    public void Synchronous_gem_and_quest_commits_cannot_write_after_Hardcore_death()
    {
        var conn = Connection(hardcore: true);
        conn.HardcoreDeathRecorded = true;
        string before = Saves.Get(GameServer.Storage.SaveDatabase.Table.Characters, conn.Character!)!;
        var result = ((List<byte[]> Packets, bool Leveled))Method("CommitGemExperience").Invoke(null, [conn, 1000u])!;
        Assert.Empty(result.Packets);
        Assert.False(result.Leveled);
        Assert.Null(Method("CommitQuestFlags").Invoke(null, [conn, new[] { "a1q1-KilledHillock" }, true]));
        Assert.DoesNotContain("a1q1-KilledHillock", conn.QuestFlagsSnapshot());
        Assert.Equal(before, Saves.Get(GameServer.Storage.SaveDatabase.Table.Characters, conn.Character!));
    }

    public void Dispose()
    {
        foreach (string name in characters) PlayerInventories.Forget(name);
    }
}
