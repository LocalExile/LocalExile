using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class GroundStackPickupTests
{
    [Fact]
    public void Stack_pickup_checks_visibility_capacity_and_identity_and_commits_once()
    {
        using var f = new Fixture();
        var chaos = Program.Items.Find("Metadata/Items/Currency/CurrencyRerollRare")!;
        var item = new WorldItemObject(100, 100, ItemCatalog.Create(chaos) with { StackSize = 3 });
        uint id = f.Conn.Instance.Ground.Add(item, f.Conn, shared: false);
        var other = new ClientState(f.Conn.Layout, 2, 1) { Instance = f.Conn.Instance };
        Assert.Null(Game.TakeGroundStack(other, id, item));
        Assert.Null(Game.TakeGroundStack(f.Conn, id, item with { GridX = 101 }));
        int Count() => Game.ItemsOf(f.Conn).Items(PlayerInventories.MainInventory)
            .Where(i => i.Item.TypeHash == chaos.Hash).Sum(i => i.Item.StackSize);
        int before = Count();
        Saves.FailWrite = _ => true;
        try { Assert.Throws<IOException>(() => Game.TakeGroundStack(f.Conn, id, item)); }
        finally { Saves.FailWrite = null; }
        Assert.Same(item, f.Conn.Instance.Ground.Get(id));
        Assert.Equal(((object?)f.Conn, false), f.Conn.Instance.Ground.Origin(id));
        Assert.Equal(before, Count());
        Assert.NotNull(Game.TakeGroundStack(f.Conn, id, item));
        Assert.Null(Game.TakeGroundStack(f.Conn, id, item));
        Assert.Equal(before + 3, Count());
        Assert.Equal(before + 3, CharacterSaves.Load(f.Conn.Character!)!.Inventories[PlayerInventories.MainInventory]
            .Where(i => i.Item.Type == chaos.Hash).Sum(i => i.Item.StackSize));

        Game.ItemsOf(f.Conn).AddStacked(PlayerInventories.MainInventory,
            ItemCatalog.Create(chaos) with { StackSize = 30000 }, Game.ShapeOf);
        uint full = f.Conn.Instance.Ground.Add(item);
        Assert.Null(Game.TakeGroundStack(f.Conn, full, item));
        Assert.Same(item, f.Conn.Instance.Ground.Get(full));
    }

    [Fact]
    public async Task Gold_auto_loot_uses_the_same_saved_commit_and_rechecks_reach()
    {
        using var f = new Fixture();
        using var wire = new PartyFollowerTests.Wire();
        var gold = new WorldItemObject(100, 100, ItemCatalog.Create(Program.Items.Find(ItemDrops.GoldId)!) with { StackSize = 7 });
        uint id = f.Conn.Instance.Ground.Add(gold, f.Conn, shared: false);
        uint before = Game.PersonalStashOf(f.Conn).Gold;
        Saves.FailWrite = _ => true;
        try { await Assert.ThrowsAsync<IOException>(() => Game.PickUpGold("test", wire.Stream, wire.Ciphers, f.Conn, id, gold, "auto-looted")); }
        finally { Saves.FailWrite = null; }
        Assert.Same(gold, f.Conn.Instance.Ground.Get(id));
        Assert.Equal(((object?)f.Conn, false), f.Conn.Instance.Ground.Origin(id));
        Assert.Equal(before, Game.PersonalStashOf(f.Conn).Gold);
        f.Conn.Walk.Teleport(800, 800, 0);
        await Game.PickUpGold("test", wire.Stream, wire.Ciphers, f.Conn, id, gold, "auto-looted");
        Assert.Same(gold, f.Conn.Instance.Ground.Get(id));
        f.Conn.Walk.Teleport(100, 100, 0);
        await Game.PickUpGold("test", wire.Stream, wire.Ciphers, f.Conn, id, gold, "auto-looted");
        Assert.Null(Game.TakeGroundStack(f.Conn, id, gold));
        Assert.Equal(before + 7, Game.PersonalStashOf(f.Conn).Gold);
    }

    sealed class Fixture : IDisposable
    {
        readonly GameServer.Modding.IModuleLifetime? previousRules = Game.CombatLifetime;
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly string saves = Path.Combine(Path.GetTempPath(), $"ground-stack-{Guid.NewGuid():N}.db");
        readonly ModuleLifetime rules = new("Game");
        internal readonly ClientState Conn = new(new(1, "pickup", 300, 300, 100, 100, null, []), 1, 1)
        { PlayerSent = true, PlayerLife = 300, Instance = new(), Character = "Pickup" + Guid.NewGuid().ToString("N") };
        internal Fixture()
        {
            Saves.Open(saves);
            Conn.Clock.Start();
            Game.CombatLifetime = rules;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(rules));
        }
        public void Dispose()
        {
            rules.Stop(_ => { }); Game.CombatLifetime = previousRules; Program.SetGameLogic(previousLogic!);
            PlayerInventories.Forget(Conn.Character!); TestEnvironment.ReleaseSaves(saves);
        }
    }
}
