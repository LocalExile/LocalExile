using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class CurrencyTransferTests
{
    [Fact]
    public void Pickup_registration_is_disabled_by_default_and_disposal_restores_previous_options()
    {
        Assert.Equal(0, CurrencyPickupPolicy.Current.Radius);
        using var mod = new PickupModFixture();
        Assert.True(mod.Host.SetEnabled("petcurrency", true));
        mod.Host.Reload("petcurrency");
        Assert.Equal(12, CurrencyPickupPolicy.Current.Radius);
        using (CurrencyPickupPolicy.Use(new(4, 2, false)))
            Assert.Equal(4, CurrencyPickupPolicy.Current.Radius);
        Assert.Equal(12, CurrencyPickupPolicy.Current.Radius);
        Assert.True(mod.Host.SetEnabled("petcurrency", false));
        Assert.Equal(0, CurrencyPickupPolicy.Current.Radius);
        Assert.Throws<ArgumentOutOfRangeException>(() => CurrencyPickupPolicy.Use(new(-1)));
    }

    [Fact]
    public void Pet_pickup_requires_binding_respects_ownership_and_preserves_ground_on_full_or_failed_save()
    {
        string db = Path.Combine(Path.GetTempPath(), "LocalExile-pet-currency-" + Guid.NewGuid().ToString("N") + ".db");
        Saves.Open(db);
        var catalog = new ItemCatalog("data");
        string account = "PetCurrency" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, catalog);
        PlayerInventories.Shape? Shape(Item i) => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
        var currency = ItemCatalog.Create(catalog.Find("Orb of Alteration")!);
        var ring = ItemCatalog.Create(catalog.Find("Iron Ring")!);
        var ground = new InstanceGround();
        object owner = new();
        var drop = new WorldItemObject(10, 10, currency with { StackSize = 2 });
        uint id = ground.Add(drop, owner, shared: false);
        try
        {
            Assert.Null(CurrencyTransfers.PickUp(ground, id, drop, owner, stash, catalog, Shape));
            Assert.Same(drop, ground.Get(id));
            stash.SetAffinities(0, [(3, true)]);
            Assert.Null(CurrencyTransfers.PickUp(ground, id, drop, new object(), stash, catalog, Shape));
            int max = Shape(currency)!.Value.MaxStack;
            Assert.NotNull(stash.Place(0, 0, 0, new(1, 0, 0, currency with { StackSize = (ushort)(max - 1) }), Shape, out _));
            for (int y = 0; y < 12; y++)
                for (int x = 0; x < 12; x++)
                    if (x + y > 0) Assert.NotNull(stash.Place(0, x, y, new((uint)(2 + y * 12 + x), (byte)x, (byte)y, ring), Shape, out _));
            Assert.Null(CurrencyTransfers.PickUp(ground, id, drop, owner, stash, catalog, Shape));
            Assert.Equal(max - 1, stash.CountOf(currency.TypeHash));
            Assert.Same(drop, ground.Get(id));
            Assert.Equal((owner, false), ground.Origin(id));
            var freed = stash.Inventory(0).Items.First(i => i.X == 1 && i.Y == 0);
            stash.Take(0, freed.Id);
            Saves.FailWrite = key => key == account;
            Assert.Throws<IOException>(() => CurrencyTransfers.PickUp(ground, id, drop, owner, stash, catalog, Shape));
            Saves.FailWrite = null;
            Assert.Same(drop, ground.Get(id));
            Assert.Equal((owner, false), ground.Origin(id));
            AccountStash.Forget(account);
            stash = AccountStash.For(account, catalog);
            Assert.Equal(max - 1, stash.CountOf(currency.TypeHash));
            Assert.NotNull(CurrencyTransfers.PickUp(ground, id, drop, owner, stash, catalog, Shape));
            Assert.Null(ground.Get(id));
            Assert.Null(CurrencyTransfers.PickUp(ground, id, drop, owner, stash, catalog, Shape));
            AccountStash.Forget(account);
            Assert.Equal(max + 1, AccountStash.For(account, catalog).CountOf(currency.TypeHash));
        }
        finally
        {
            Saves.FailWrite = null;
            AccountStash.Forget(account);
            TestEnvironment.ReleaseSaves(db);
        }
    }


    sealed class PickupModFixture : IDisposable
    {
        readonly string _folder = Path.Combine(Path.GetTempPath(), "localexile-petcurrency-" + Guid.NewGuid().ToString("N"));
        internal ModHost Host { get; }
        internal PickupModFixture()
        {
            string mod = Path.Combine(_folder, "petcurrency");
            Directory.CreateDirectory(mod);
            File.WriteAllText(Path.Combine(mod, "mod.json"), """{"id":"petcurrency","enabled":false}""");
            File.WriteAllText(Path.Combine(mod, "main.cs"), """
                public class TestMod : IMod
                {
                    public void Load(IModContext mod) => CurrencyPickupPolicy.Use(mod.Manifest.Id, new(12, 8, true));
                }
                """);
            Host = new ModHost(_folder, _ => { });
            Host.Start(watch: false);
        }
        public void Dispose()
        {
            Host.Dispose();
            Directory.Delete(_folder, recursive: true);
        }
    }

}
