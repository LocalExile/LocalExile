using GameServer.Core.Protocol.Instance;
using GameServer.Modules.CosmeticsAndPets;

namespace GameServer.Gateway.Tests;

public sealed class ModLootTests
{
    [Fact]
    public async Task Loot_api_hides_private_items_and_retires_control_without_clearing_a_replacement()
    {
        using var f = new Fixture();
        var type = Program.Items.Find("Metadata/Items/Currency/CurrencyRerollRare")!;
        var visible = new WorldItemObject(100, 100, ItemCatalog.Create(type));
        uint item = f.Conn.Instance.Ground.Add(visible);
        uint hidden = f.Conn.Instance.Ground.Add(visible, new object(), shared: false);
        uint hand = f.Conn.Instance.Ground.Add(visible with { PlayerDropped = true });
        Assert.Contains(f.Loot.Nearby(80), i => i.Id == item && i.Metadata == type.Id);
        Assert.DoesNotContain(f.Loot.Nearby(80), i => i.Id == hidden);
        Assert.True(f.Loot.Nearby(80).Single(i => i.Id == hand).PlayerDropped);
        var quest = Program.Items.QuestItemTypes.First();
        uint questId = f.Conn.Instance.Ground.Add(new(100, 100, ItemCatalog.Create(quest)));
        Assert.True(f.Loot.Nearby(80).Single(i => i.Id == questId).Quest);
        Assert.False(await f.Loot.PickUp(questId, f.Mod));
        Assert.Empty(f.Loot.Nearby(double.NaN));
        Assert.False(await f.Loot.PickUp(hidden, f.Mod));
        Assert.False(await f.Loot.PickUp(hand, f.Mod));
        int searches = 0;
        f.Conn.Pets[1] = new(new GameServer.Core.World.WalkEstimator(100, 100, 10,
            (x, y) => { searches++; return true; }, 300, 300), 1);
        Assert.True(await f.Pets.MovePet(1, 110, 100, f.Mod));
        int firstSearch = searches;
        Assert.True(firstSearch > 0);
        for (int tick = 0; tick < 20; tick++) Assert.True(await f.Pets.MovePet(1, 110, 100, f.Mod));
        Assert.Equal(firstSearch, searches);
        f.Conn.Pets[1].ControlUntil = f.Conn.Clock.Elapsed.TotalSeconds - 1;
        Assert.True(await f.Pets.MovePet(1, 110, 100, f.Mod));
        Assert.True(searches > firstSearch);
        Assert.False(await f.Pets.MovePet(1, 100, 100, f.Next));
        Assert.False(await f.Pets.MovePet(1, 500, 500, f.Mod));
        f.Mod.Stop(_ => { });
        Assert.Null(f.Conn.Pets[1].ControlOwner);
        Assert.Null(f.Conn.Pets[1].Target);
        Assert.True(f.Conn.Pets[1].ReturnToOwner);
        Assert.False(await f.Loot.PickUp(item, f.Mod, 1));
        Assert.True(await f.Pets.MovePet(1, 100, 100, f.Next));
        f.Pets.ReleasePet(1, f.Mod);
        Assert.Same(f.Next, f.Conn.Pets[1].ControlOwner);
        f.Conn.Clock.Stop();
        Assert.False(await f.Loot.PickUp(item, f.Next, 1));
        Assert.False(await f.Pets.MovePet(1, 100, 100, f.Next));
        f.Conn.Clock.Start(); f.Conn.LeavingArea = true;
        Assert.False(await f.Loot.PickUp(item, f.Next, 1));
        f.Conn.LeavingArea = false; f.Conn.PlayerLife = 0;
        Assert.False(await f.Loot.PickUp(item, f.Next, 1));
        f.Conn.PlayerLife = 300;
        using var lootProvider = GameEvents.GroundLoot.Add("Game", _ => f.Rules.IsActive ? f.Loot : null);
        Assert.Same(f.Loot, GameEvents.GroundLootFor(f.Conn));
        Assert.Null(GameEvents.WornPetsFor(f.Conn)); // no provider in this connection's league
        using (GameEvents.PetControls.Add("Game", _ => f.Pets)) Assert.Same(f.Pets, GameEvents.WornPetsFor(f.Conn));
        Assert.Null(GameEvents.WornPetsFor(f.Conn));
        f.Cosmetics.Stop(_ => { });
        Assert.Null(f.Conn.Pets[1].ControlOwner);
        Assert.False(await f.Pets.MovePet(1, 100, 100, f.Next));
        var replacement = new ModuleLifetime("Cosmetics");
        try
        {
            var pets = new CosmeticPetControls(f.Conn, replacement);
            Assert.True(await pets.MovePet(1, 100, 100, f.Next));
            f.Pets.ReleasePet(1, f.Next);
            Assert.Same(replacement, f.Conn.Pets[1].ControlProvider);
            Assert.Same(f.Next, f.Conn.Pets[1].ControlOwner);
        }
        finally { replacement.Stop(_ => { }); }
        f.Rules.Stop(_ => { });
        Assert.Null(GameEvents.GroundLootFor(f.Conn));
        Assert.False(await f.Loot.PickUp(item, f.Next, 1));
        Assert.Same(visible, f.Conn.Instance.Ground.Get(item));
    }

    [Fact]
    public async Task Pickup_requires_a_nearby_collector_saves_once_and_leaves_items_when_inventory_is_full()
    {
        using var f = new Fixture();
        var chaos = Program.Items.Find("Metadata/Items/Currency/CurrencyRerollRare")!;
        var ground = new WorldItemObject(100, 100, ItemCatalog.Create(chaos) with { StackSize = 3 });
        uint id = f.Conn.Instance.Ground.Add(ground);
        Assert.False(await f.Loot.PickUp(id, f.Mod, 1)); // pet is not controlled yet
        Assert.True(await f.Pets.MovePet(1, 100, 100, f.Mod));
        var inventory = Game.ItemsOf(f.Conn);
        int Before() => inventory.Items(PlayerInventories.MainInventory).Where(i => i.Item.TypeHash == chaos.Hash).Sum(i => i.Item.StackSize);
        int before = Before();
        Assert.True(await f.Loot.PickUp(id, f.Mod, 1));
        Assert.Equal(before + 3, Before());
        Assert.Null(f.Conn.Instance.Ground.Get(id));
        Assert.False(await f.Loot.PickUp(id, f.Mod, 1));
        Assert.Equal(before + 3, Before());
        Assert.Equal(before + 3, CharacterSaves.Load(f.Conn.Character!)!.Inventories[PlayerInventories.MainInventory]
            .Where(i => i.Item.Type == chaos.Hash).Sum(i => i.Item.StackSize));

        var distant = ground with { GridX = 130 };
        uint far = f.Conn.Instance.Ground.Add(distant);
        Assert.False(await f.Loot.PickUp(far, f.Mod, 1));
        Assert.False(await f.Loot.PickUp(far, f.Mod));
        Assert.Same(distant, f.Conn.Instance.Ground.Get(far));
        inventory.AddStacked(PlayerInventories.MainInventory, ItemCatalog.Create(chaos) with { StackSize = 30000 }, Game.ShapeOf);
        uint full = f.Conn.Instance.Ground.Add(ground);
        Assert.False(await f.Loot.PickUp(full, f.Mod, 1));
        Assert.Same(ground, f.Conn.Instance.Ground.Get(full));
        var gold = new WorldItemObject(100, 100, ItemCatalog.Create(Program.Items.Find(ItemDrops.GoldId)!) with { StackSize = 7 });
        uint goldId = f.Conn.Instance.Ground.Add(gold);
        Assert.True(await f.Loot.PickUp(goldId, f.Mod, 1)); // gold does not need an inventory cell
        Assert.False(await f.Loot.PickUp(goldId, f.Mod, 1));
        Assert.Null(f.Conn.Instance.Ground.Get(goldId));
        using var wire = new PartyFollowerTests.Wire();
        foreach (bool manual in new[] { true, false })
        {
            uint failed = f.Conn.Instance.Ground.Add(gold);
            uint beforeGold = Game.PersonalStashOf(f.Conn).Gold;
            Saves.FailWrite = _ => true;
            try
            {
                if (manual)
                    await Assert.ThrowsAsync<IOException>(() => Game.PickUpGold("test", wire.Stream, wire.Ciphers, f.Conn, failed, gold, "test"));
                else
                    await Assert.ThrowsAsync<IOException>(() => f.Loot.PickUp(failed, f.Mod, 1));
                Assert.Same(gold, f.Conn.Instance.Ground.Get(failed));
                Assert.Equal(beforeGold, Game.PersonalStashOf(f.Conn).Gold);
            }
            finally { Saves.FailWrite = null; }
        }
        PetControl.Clear(f.Conn);
        Assert.Null(f.Conn.Pets[1].ControlOwner);
    }

    sealed class Fixture : IDisposable
    {
        readonly GameServer.Modding.IModuleLifetime? previousRules = Game.CombatLifetime;
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly string saves = Path.Combine(Path.GetTempPath(), $"pet-loot-api-{Guid.NewGuid():N}.db");
        internal readonly ModuleLifetime Rules = new("Game"), Mod = new("petloot"), Next = new("petloot"), Cosmetics = new("Cosmetics");
        readonly IDisposable collector;
        internal readonly CosmeticPetControls Pets;
        internal readonly ClientState Conn = new(new(1, "pet-api", 300, 300, 100, 100, null, []), 1, 1)
        { PlayerSent = true, PlayerLife = 300, Instance = new(), Character = "PetApi" + Guid.NewGuid().ToString("N") };
        internal readonly Game.ModLoot Loot;
        internal Fixture()
        {
            Saves.Open(saves);
            Conn.Clock.Start();
            Conn.Pets[1] = new(Conn.NewWalk(100, 100, 10), 1);
            Game.CombatLifetime = Rules;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(Rules));
            Loot = new(Conn, Rules);
            Pets = new(Conn, Cosmetics);
            // Use a Game subscriber to isolate the provider hand-over from optional league composition.
            collector = GameEvents.PetCollector.Add("Game", (conn, id, owner) =>
                ReferenceEquals(conn, Conn) ? Pets.Collector(id, owner) : null);
        }
        public void Dispose()
        {
            collector.Dispose(); Cosmetics.Stop(_ => { });
            PetControl.Clear(Conn); Mod.Stop(_ => { }); Next.Stop(_ => { }); Rules.Stop(_ => { });
            Game.CombatLifetime = previousRules; Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(saves);
        }
    }
}
