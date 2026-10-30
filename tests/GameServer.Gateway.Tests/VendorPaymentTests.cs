using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public class VendorPaymentTests : IDisposable
{
    const uint Wand = 0x90201001, A = 0x90201002, B = 0x90201003;
    readonly string _folder = TestSaves.Catalog(new TestSaves.Type(Wand, "Wand", 1, 3),
        new TestSaves.Type(A, "CurrencyA", StackSize: 40), new TestSaves.Type(B, "CurrencyB", StackSize: 40));
    readonly string _name = "Vendor-" + Guid.NewGuid().ToString("N");
    PlayerInventories Open() => PlayerInventories.For(_name, new ItemCatalog(_folder));
    PlayerInventories Reload() { PlayerInventories.Forget(_name); return Open(); }
    static Item Currency(uint hash, int n) => new(hash, ["Base", "Stack"], 1) { StackSize = (ushort)n };
    static Item Weapon() => new(Wand, ["Base"], 1);
    static PlayerInventories.Shape? Shape(Item i) => i.TypeHash == Wand ? new(1, 3, "Wand") : new(1, 1, "StackableCurrency", 40);
    public VendorPaymentTests() => TestSaves.Ensure();
    public void Dispose() { Saves.FailWrite = null; Directory.Delete(_folder, true); }

    [Fact]
    public void All_rewards_commit_once_and_merge_to_the_final_stack_without_duplicate_updates()
    {
        var inv = Open();
        var old = inv.Add(0, 0, 0, Currency(A, 35));
        var weapon = inv.Add(0, 1, 0, Weapon());
        inv.Offer(0, weapon.Id, 0, 0, Shape);
        var result = inv.Sell([Currency(A, 2), Currency(A, 3), Currency(B, 4)], Shape)!;
        Assert.Single(result.Changed);
        Assert.Equal(40, result.Changed[0].Item.StackSize);
        Assert.Single(result.Added);
        Assert.Equal(B, result.Added[0].Item.TypeHash);
        var after = Reload();
        Assert.Equal(40, after.Count(0, A));
        Assert.Equal(4, after.Count(0, B));
        Assert.DoesNotContain(after.Items(0), i => i.Id == weapon.Id);
    }

}
