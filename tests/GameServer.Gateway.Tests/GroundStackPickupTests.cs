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
        uint id = f.Player.Instance.Ground.Add(item, f.Player, shared: false);
        var other = new Player(f.Player.Layout, 2, 1) { Connection = { LockstepClient = false }, Instance = f.Player.Instance };
        Assert.Null(Game.TakeGroundStack(other, id, item));
        Assert.Null(Game.TakeGroundStack(f.Player, id, item with { GridX = 101 }));
        int Count() => Game.ItemsOf(f.Player).Items(PlayerInventories.MainInventory)
            .Where(i => i.Item.TypeHash == chaos.Hash).Sum(i => i.Item.StackSize);
        int before = Count();
        Saves.FailWrite = _ => true;
        try { Assert.Throws<IOException>(() => Game.TakeGroundStack(f.Player, id, item)); }
        finally { Saves.FailWrite = null; }
        Assert.Same(item, f.Player.Instance.Ground.Get(id));
        Assert.Equal(((object?)f.Player, false), f.Player.Instance.Ground.Origin(id));
        Assert.Equal(before, Count());
        Assert.NotNull(Game.TakeGroundStack(f.Player, id, item));
        Assert.Null(Game.TakeGroundStack(f.Player, id, item));
        Assert.Equal(before + 3, Count());
        Assert.Equal(before + 3, CharacterSaves.Load(f.Player.Character.Name!)!.Inventories[PlayerInventories.MainInventory]
            .Where(i => i.Item.Type == chaos.Hash).Sum(i => i.Item.StackSize));

        Game.ItemsOf(f.Player).AddStacked(PlayerInventories.MainInventory,
            ItemCatalog.Create(chaos) with { StackSize = 30000 }, Game.ShapeOf);
        uint full = f.Player.Instance.Ground.Add(item);
        Assert.Null(Game.TakeGroundStack(f.Player, full, item));
        Assert.Same(item, f.Player.Instance.Ground.Get(full));
    }

    [Fact]
    public async Task Gold_auto_loot_uses_the_same_saved_commit_and_rechecks_reach()
    {
        using var f = new Fixture();
        using var wire = new PartyFollowerTests.Wire();
        f.Player.Connection.Session = new("gold-test", wire.Stream, wire.Ciphers);
        var gold = new WorldItemObject(100, 100, ItemCatalog.Create(Program.Items.Find(ItemDrops.GoldId)!) with { StackSize = 7 });
        uint id = f.Player.Instance.Ground.Add(gold, f.Player, shared: false);
        uint before = Game.PersonalStashOf(f.Player).Gold;
        Saves.FailWrite = _ => true;
        try { await Assert.ThrowsAsync<IOException>(() => Game.PickUpGold(f.Player, id, gold, "auto-looted")); }
        finally { Saves.FailWrite = null; }
        Assert.Same(gold, f.Player.Instance.Ground.Get(id));
        Assert.Equal(((object?)f.Player, false), f.Player.Instance.Ground.Origin(id));
        Assert.Equal(before, Game.PersonalStashOf(f.Player).Gold);
        f.Player.Walk.Teleport(800, 800, 0);
        await Game.PickUpGold(f.Player, id, gold, "auto-looted");
        Assert.Same(gold, f.Player.Instance.Ground.Get(id));
        f.Player.Walk.Teleport(100, 100, 0);
        await Game.PickUpGold(f.Player, id, gold, "auto-looted");
        Assert.Null(Game.TakeGroundStack(f.Player, id, gold));
        Assert.Equal(before + 7, Game.PersonalStashOf(f.Player).Gold);
    }

    [Fact]
    public async Task Lockstep_gold_pickup_removes_the_published_object_in_a_world_frame()
    {
        using var f = new Fixture();
        using var wire = new PartyFollowerTests.Wire();
        var player = f.Player;
        player.Connection.LockstepClient = true;
        player.Connection.Session = new("lockstep-gold", wire.Stream, wire.Ciphers);
        await Program.BeginWorldFrames(player, .033);
        var gold = new WorldItemObject(100, 100, ItemCatalog.Create(Program.Items.Find(ItemDrops.GoldId)!) with { StackSize = 7 });
        uint id = player.Instance.Ground.Add(gold, player, shared: false);
        var creation = Game.SendGroundItem(player, id, gold, "create test gold");
        await Program.EndWorldFrame(player, (.033, .066));
        await creation;
        Assert.True(player.Connection.WorldOutput.Has(id));

        uint before = Game.PersonalStashOf(player).Gold;
        var pickup = Game.PickUpGold(player, id, gold, "auto-looted");
        await Program.EndWorldFrame(player, (.066, .099));
        await pickup;
        Assert.Equal(before + 7, Game.PersonalStashOf(player).Gold);
        Assert.False(player.Connection.WorldOutput.Has(id));
        Assert.Contains(Program.HistoryFor(wire.Ciphers).Lines(),
            line => line.Contains($"02BD{id:X8}FFFFFFFF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Currency_cards_and_scarabs_in_range_are_auto_looted_but_not_gear_flasks_or_far_items_and_off_takes_nothing()
    {
        using var f = new Fixture();
        using var wire = new PartyFollowerTests.Wire();
        var player = f.Player;
        player.Connection.Session = new("currency-autoloot", wire.Stream, wire.Ciphers);
        var at = player.Walk.Position(Game.AreaTime(player));
        uint Drop(Func<ItemCatalog.BaseType, bool> pick, int dx) => player.Instance.Ground.Add(
            new WorldItemObject(at.X + dx, at.Y, ItemCatalog.Create(Program.Items.All.First(pick))), player, shared: false);
        uint chaos = Drop(t => t.Id == "Metadata/Items/Currency/CurrencyRerollRare", 3);
        uint card = Drop(t => t.Class == "DivinationCard", 4);
        uint scarab = Drop(t => t.Class == "MapFragment" && t.Id.Contains("/Scarabs/"), 5);
        uint flask = Drop(t => t.Class == "LifeFlask", 3);
        uint ring = Drop(t => t.Class == "Ring", 3);
        uint far = Drop(t => t.Id == "Metadata/Items/Currency/CurrencyRerollRare", 60);
        double previous = Game.CurrencyAutoLootRadius;
        try
        {
            Game.CurrencyAutoLootRadius = 0;
            await Game.PickUpNearbyCurrency(player);
            Assert.NotNull(player.Instance.Ground.Get(chaos));
            Game.CurrencyAutoLootRadius = 20;
            await Game.PickUpNearbyCurrency(player);
            Assert.All([chaos, card, scarab], id => Assert.Null(player.Instance.Ground.Get(id)));
            Assert.All([flask, ring, far], id => Assert.NotNull(player.Instance.Ground.Get(id)));
        }
        finally { Game.CurrencyAutoLootRadius = previous; }
    }

    sealed class Fixture : IDisposable
    {
        readonly GameServer.Modding.IModuleLifetime? previousRules = Game.CombatLifetime;
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly string saves = Path.Combine(Path.GetTempPath(), $"ground-stack-{Guid.NewGuid():N}.db");
        readonly ModuleLifetime rules = new("Game");
        internal readonly Player Player = new(new(1, "pickup", 300, 300, 100, 100, null, []), 1, 1)
        { Connection = { LockstepClient = false, PlayerSent = true }, PlayerLife = 300, Instance = new(), Character = { Name = "Pickup" + Guid.NewGuid().ToString("N") } };
        internal Fixture()
        {
            Saves.Open(saves);
            Player.Clock.Start();
            Game.CombatLifetime = rules;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(rules));
        }
        public void Dispose()
        {
            rules.Stop(_ => { }); Game.CombatLifetime = previousRules; Program.SetGameLogic(previousLogic!);
            PlayerInventories.Forget(Player.Character.Name!); TestEnvironment.ReleaseSaves(saves);
        }
    }
}
