using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class WeaponPassiveTreeTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    [Fact]
    public void Weapon_passive_tree_round_trips_through_character_and_stash_saves()
    {
        TestEnvironment.OpenOwnSaves("weapon-passive-tree");
        var catalog = Program.Items;
        var bowType = catalog.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!;
        var physMod = catalog.Mods.ById("WeaponTreeAddedPhysical1")!;
        var notableMod = catalog.Mods.ById("WeaponTreeNotableBowAvatarOfTheHunt")!;

        int b0 = WeaponPassiveTreeState.BitIndex(0, 0);
        int b1 = WeaponPassiveTreeState.BitIndex(1, 0);
        ushort layout = (ushort)((1 << b0) | (1 << b1));
        var revealedTree = new WeaponPassiveTreeState(
            BalanceItemLevel: 84, Experience: 12345, HasTree: true,
            Layout: layout, Allocation: (ushort)(1 << b0),
            LeftParentConnections: 0, RightParentConnections: (ushort)(1 << b1),
            PendingCol1: 0,
            Nodes:
            [
                new(physMod.Hash, [2, 6]),
                new(notableMod.Hash, [-28849]),
            ]);
        var unrevealedTree = new WeaponPassiveTreeState(BalanceItemLevel: 68, Experience: 500, HasTree: false);

        var revealedBow = ItemCatalog.Create(bowType) with { ItemLevel = 84, WeaponPassiveTree = revealedTree };
        var unrevealedBow = ItemCatalog.Create(bowType) with { ItemLevel = 68, WeaponPassiveTree = unrevealedTree };

        // Character save round-trip (equipment + inventory).
        string character = "CrucibleSave" + Guid.NewGuid().ToString("N");
        var saved = new CharacterSaves.Save(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>
        {
            [PlayerInventories.Weapon1] = [new(1, 0, 0, CharacterSaves.ToSaved(revealedBow, catalog.Mods))],
            [PlayerInventories.MainInventory] = [new(2, 0, 0, CharacterSaves.ToSaved(unrevealedBow, catalog.Mods))],
        });
        CharacterSaves.Write(character, saved);
        var loaded = CharacterSaves.Load(character)!;
        var loadedRevealed = CharacterSaves.FromSaved(loaded.Inventories![PlayerInventories.Weapon1][0].Item, catalog);
        var loadedUnrevealed = CharacterSaves.FromSaved(loaded.Inventories![PlayerInventories.MainInventory][0].Item, catalog);

        Assert.Equal(revealedTree, loadedRevealed.WeaponPassiveTree);
        Assert.Equal(revealedBow.Payload(), loadedRevealed.Payload());
        Assert.Equal(unrevealedTree, loadedUnrevealed.WeaponPassiveTree);
        Assert.Equal(unrevealedBow.Payload(), loadedUnrevealed.Payload());

        // Account stash round-trip.
        string account = "CrucibleStash" + Guid.NewGuid().ToString("N");
        PlayerInventories.Shape? Shape(Item i) => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
        var stash = AccountStash.For(account, catalog);
        Assert.Equal(2, stash.FillCells(0, [revealedBow, unrevealedBow], i => i.WeaponPassiveTree!.HasTree.ToString(), Shape, out _)!.Added.Count);
        AccountStash.Forget(account);
        var reloadedItems = AccountStash.For(account, catalog).Inventory(0).Items.Select(p => p.Item).ToArray();
        Assert.Contains(reloadedItems, i => Equals(i.WeaponPassiveTree, revealedTree));
        Assert.Contains(reloadedItems, i => Equals(i.WeaponPassiveTree, unrevealedTree));
        AccountStash.Forget(account);
    }

    [Fact]
    public void Allocated_weapon_passive_nodes_apply_stats_granted_passives_and_framework_hooks()
    {
        TestEnvironment.OpenOwnSaves("weapon-passive-stats");
        var catalog = Program.Items;
        var bowType = catalog.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!;
        var physMod = catalog.Mods.ById("WeaponTreeAddedPhysical1")!;
        var notableMod = catalog.Mods.ById("WeaponTreeNotableBowAvatarOfTheHunt")!;

        int b0 = WeaponPassiveTreeState.BitIndex(0, 0);
        int b1 = WeaponPassiveTreeState.BitIndex(1, 0);
        ushort layout = (ushort)((1 << b0) | (1 << b1));
        var tree = new WeaponPassiveTreeState(
            BalanceItemLevel: 84, Experience: 50000, HasTree: true,
            Layout: layout, Allocation: layout,
            RightParentConnections: (ushort)(1 << b1),
            Nodes:
            [
                new(physMod.Hash, [2, 6]),
                new(notableMod.Hash, [-28849]),
            ]);

        var bow = ItemCatalog.Create(bowType) with { ItemLevel = 84, WeaponPassiveTree = tree };
        var unallocatedBow = bow with { WeaponPassiveTree = tree with { Allocation = 0 } };

        // Allocated local weapon stats apply; unallocated nodes do not.
        var localAlloc = ItemModStats.Local(bow, catalog.Mods);
        var localUnalloc = ItemModStats.Local(unallocatedBow, catalog.Mods);
        Assert.Equal(2, localAlloc.GetValueOrDefault("local_minimum_added_physical_damage"));
        Assert.Equal(6, localAlloc.GetValueOrDefault("local_maximum_added_physical_damage"));
        Assert.Equal(0, localUnalloc.GetValueOrDefault("local_minimum_added_physical_damage"));

        // Granted passive hash resolves signed int16 (-28849) -> ushort 36687 (Avatar of the Hunt).
        ushort avatarOfTheHunt = unchecked((ushort)-28849);
        Assert.Equal([avatarOfTheHunt], ItemModStats.GrantedPassives([bow], catalog.Mods).ToArray());
        Assert.Empty(ItemModStats.GrantedPassives([unallocatedBow], catalog.Mods));

        var conn = new ClientState(new(1, "crucible", 100, 100, 100, 100, null, []), 1, 1)
        {
            Character = "CrucibleStats" + Guid.NewGuid().ToString("N"),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
        };
        try
        {
            var passiveTree = Game.PassivesFor(conn);
            var grantedNode = passiveTree.Find(avatarOfTheHunt)!;
            var (statKey, statVal) = grantedNode.Stats[0];

            int baseline = Game.CharacterStatSources(conn).Stats.GetValueOrDefault(statKey);
            Game.ItemsOf(conn).Put(PlayerInventories.Weapon1, new(50, 0, 0, bow));
            Assert.Equal(baseline + statVal, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(statKey));

            // Already allocating the same passive on the character's tree must not double-count it.
            conn.Passives.Add(avatarOfTheHunt);
            Assert.Equal(baseline + statVal, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(statKey));

            // Removing the weapon passive allocation removes the granted notable once not allocated on the tree.
            conn.Passives.Remove(avatarOfTheHunt);
            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, 50);
            Game.ItemsOf(conn).Put(PlayerInventories.Weapon1, new(50, 0, 0, unallocatedBow));
            Assert.Equal(baseline, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(statKey));

            // Generic framework hooks: GameEvents.VendorQuote and GameEvents.CurrencyApply.
            string owner = Program.LeagueFor(conn).Modules.First();
            var rewards = new List<Item>();
            var recipes = new List<string>();
            using (GameEvents.VendorQuote.Add(owner, q => { q.Rewards.Add(unallocatedBow); q.Recipes.Add("test-sell-bonus"); }))
            {
                GameEvents.RunVendorQuote(new VendorSaleQuote(conn, [new(50, 0, 0, bow)], rewards, recipes, basicSale: true));
            }
            Assert.Single(rewards);
            Assert.Equal(["test-sell-bonus"], recipes);

            var orbType = catalog.Find("Metadata/Items/Currency/CurrencyRerollRare")!;
            Item? applied;
            using (GameEvents.CurrencyApply.Add(owner, (_, target, _, _) => target with { Quality = 17 }))
            {
                applied = GameEvents.RunCurrencyApply(conn, bow, orbType, new Random(1));
            }
            Assert.NotNull(applied);
            Assert.Equal(17, applied!.Quality);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
        }
    }
}
