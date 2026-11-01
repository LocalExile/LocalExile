using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DivinationCardTests
{
    static ItemCatalog Items => Program.Items;
    static UniqueCatalog Uniques => Game.Uniques;
    static ItemRolls Rolls => Program.Rolls;
    static readonly Random Rng = new(1);

    static IReadOnlyList<Item> Rewards(string card) =>
        DivinationCards.Create(DivinationCards.Of(card) ?? throw new InvalidOperationException(card), Items, Uniques, Rolls, Rng)
        ?? throw new InvalidOperationException($"{card}: no reward");

    [Fact]
    public void Table_has_official_sets_and_stack_sizes()
    {
        Assert.True(DivinationCards.All.Count > 400, $"only {DivinationCards.All.Count} cards");
        Assert.Equal(8, DivinationCards.Of("The Doctor")!.Stack);
        Assert.Equal(5, DivinationCards.Of("The Apothecary")!.Stack);
        Assert.Equal(3, DivinationCards.Of("The Scholar")!.Stack);
        Assert.Equal((ushort)8, DivinationCards.StackOf("The Doctor"));
        Assert.Equal("unique", DivinationCards.Of("The Doctor")!.Kind);
        Assert.Equal("Headhunter", DivinationCards.Of("The Doctor")!.Item);
        Assert.Equal("Mageblood", DivinationCards.Of("The Apothecary")!.Item);
        Assert.Equal(40, DivinationCards.Of("The Scholar")!.Count);
        Assert.Equal("void", DivinationCards.Of("The Void")!.Kind);
        Assert.Equal("random_card", DivinationCards.Of("The Gambler")!.Kind);
    }

    [Fact]
    public void ShapeOf_lets_a_complete_set_share_one_cell()
    {
        var doctor = Items.Find("The Doctor") ?? throw new InvalidOperationException(Items.Error ?? "The Doctor");
        Assert.Equal("DivinationCard", doctor.Class);
        var shape = Game.ShapeOf(ItemCatalog.Create(doctor));
        Assert.NotNull(shape);
        Assert.Equal((ushort)8, shape!.Value.MaxStack);
    }

    [Fact]
    public void Doctor_turns_into_an_identified_headhunter()
    {
        var hunter = Uniques.Create("Headhunter", Rng) ?? throw new InvalidOperationException("Headhunter");
        var rewards = Rewards("The Doctor");
        Assert.Single(rewards);
        Assert.Equal(hunter.TypeHash, rewards[0].TypeHash);
        Assert.Equal(ItemRarity.Unique, rewards[0].Rarity);
        Assert.True(rewards[0].Identified);
        Assert.Equal(hunter.NameWords, rewards[0].NameWords);
        Assert.False(rewards[0].Corrupted);
    }

    [Fact]
    public void Demon_is_two_implicit_corrupted_headhunter()
    {
        Assert.True(DivinationCards.Of("The Demon")!.Corrupted);
        Assert.True(DivinationCards.Of("The Demon")!.TwoImplicit);
        var rewards = Rewards("The Demon");
        Assert.Single(rewards);
        Assert.True(rewards[0].Corrupted);
        Assert.True(rewards[0].Mods.Count(m => m.Section == ModSection.Implicit) >= 2);
    }

    [Fact]
    public void Price_of_devotion_is_quality_two_implicit_mageblood()
    {
        var reward = DivinationCards.Of("The Price of Devotion")!;
        Assert.True(reward.Corrupted);
        Assert.True(reward.TwoImplicit);
        Assert.Equal(20, reward.Quality);
        var item = Rewards("The Price of Devotion").Single();
        Assert.True(item.Corrupted);
        Assert.Equal(20, item.Quality);
        Assert.True(item.Mods.Count(m => m.Section == ModSection.Implicit) >= 2);
    }

    [Fact]
    public void Fiend_is_corrupted_headhunter()
    {
        Assert.True(DivinationCards.Of("The Fiend")!.Corrupted);
        Assert.False(DivinationCards.Of("The Fiend")!.TwoImplicit);
        var item = Rewards("The Fiend").Single();
        Assert.True(item.Corrupted);
    }

    [Fact]
    public void Scholar_turns_into_forty_wisdom_scrolls()
    {
        var wisdom = Items.Find("Scroll of Wisdom") ?? throw new InvalidOperationException("Scroll of Wisdom");
        var rewards = Rewards("The Scholar");
        Assert.Single(rewards);
        Assert.Equal(wisdom.Hash, rewards[0].TypeHash);
        Assert.Equal(40, rewards[0].StackSize);
    }

    [Fact]
    public void Nurse_turns_into_a_doctor_card()
    {
        var doctor = Items.Find("The Doctor") ?? throw new InvalidOperationException("The Doctor");
        var rewards = Rewards("The Nurse");
        Assert.Single(rewards);
        Assert.Equal(doctor.Hash, rewards[0].TypeHash);
    }

    [Fact]
    public void Ethereal_is_a_six_link_vaal_regalia()
    {
        var baseType = Items.Find("Vaal Regalia") ?? throw new InvalidOperationException("Vaal Regalia");
        var rewards = Rewards("The Ethereal");
        Assert.Single(rewards);
        Assert.Equal(baseType.Hash, rewards[0].TypeHash);
        Assert.Equal(6, rewards[0].Sockets.Count);
        Assert.Equal(new byte[] { 6 }, rewards[0].Links);
    }

    [Fact]
    public void Complete_set_in_inventory_is_consumed_and_the_reward_fits()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-divcard-{Guid.NewGuid():N}.db");
        string character = "DivCard" + Guid.NewGuid().ToString("N");
        Saves.Open(path);
        try
        {
            var doctor = Items.Find("The Doctor")!;
            var items = PlayerInventories.For(character, Items);
            var stacked = items.AddStacked(PlayerInventories.MainInventory,
                ItemCatalog.Create(doctor) with { StackSize = 8 }, Game.ShapeOf);
            Assert.Equal(0, stacked.Left);
            Assert.Equal(8, items.Count(PlayerInventories.MainInventory, doctor.Hash));
            var card = stacked.AddedItems.Single();
            var rewards = Rewards("The Doctor");
            Assert.True(items.CanExchange(rewards, Game.ShapeOf));
            items.SetStackSize(PlayerInventories.MainInventory, card.Id, 0);
            var given = items.AddStacked(PlayerInventories.MainInventory, rewards[0], Game.ShapeOf);
            Assert.Equal(0, given.Left);
            Assert.Equal(0, items.Count(PlayerInventories.MainInventory, doctor.Hash));
            Assert.Contains(items.Items(PlayerInventories.MainInventory), i => i.Item.TypeHash == rewards[0].TypeHash
                && i.Item.Rarity == ItemRarity.Unique);
        }
        finally
        {
            PlayerInventories.Forget(character);
        }
    }
}
