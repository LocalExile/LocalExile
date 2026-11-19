using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using System.Reflection;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class DivinationCardTests
{
    [Fact]
    public void Named_rewards_match_current_card_sets_and_never_substitute_unrelated_items()
    {
        var items = Program.Items;
        var uniques = new UniqueCatalog(items);
        var rolls = new ItemRolls(items);
        Assert.Equal(470, DivinationRewards.All.Count);
        Assert.Equal(456, DivinationRewards.All.Values.Count(r => r.Enabled));
        Assert.All(DivinationRewards.All.Values, r => Assert.True(r.Enabled || r.Kind == "disabled", r.Card));
        var doctor = DivinationRewards.Find(items.Find("The Doctor")!.Id)!;
        Assert.Equal(8, doctor.Stack);
        var belt = DivinationRewards.Create(doctor, items, uniques, rolls, new Random(499));
        Assert.NotNull(belt);
        Assert.Equal(uniques.Create("Headhunter", new Random(1))!.NameWords, belt.NameWords);
        Assert.Equal(ItemRarity.Unique, belt.Rarity);
        var scholar = DivinationRewards.Find(items.Find("The Scholar")!.Id)!;
        var wisdom = DivinationRewards.Create(scholar, items, uniques, rolls, new Random(1))!;
        Assert.Equal(items.Find("Scroll of Wisdom")!.Hash, wisdom.TypeHash);
        Assert.Equal(40, wisdom.StackSize);
        var justice = DivinationRewards.Find(items.Find("Divine Justice")!.Id)!;
        string[] spectrumBases = ["Metadata/Items/Jewels/JewelStr", "Metadata/Items/Jewels/JewelDex", "Metadata/Items/Jewels/JewelInt"];
        Assert.Equal(spectrumBases.Order(), justice.Members.Select(m => m.Base).Order());
        foreach (var member in justice.Members)
        {
            Assert.Equal("unique", member.Kind);
            Assert.Equal("Grand Spectrum", member.Id);
            var spectrum = DivinationRewards.Create(justice with { Members = [member] }, items, uniques, rolls, new Random(974))!;
            Assert.NotNull(spectrum);
            Assert.Equal(member.Base, items.ByHash(spectrum.TypeHash)!.Id);
            Assert.Equal(ItemRarity.Unique, spectrum.Rarity);
            Assert.True(spectrum.Corrupted);
        }
        var rolledSpectrumBases = Enumerable.Range(0, 32).Select(seed => items.ByHash(
            DivinationRewards.Create(justice, items, uniques, rolls, new Random(seed))!.TypeHash)!.Id).ToHashSet();
        Assert.Equal(spectrumBases.Order(), rolledSpectrumBases.Order());
        Assert.Null(DivinationRewards.Create(doctor with { Name = "No such unique" }, items, uniques, rolls, new Random(1)));
        Assert.Null(DivinationRewards.Create(doctor with { Enabled = false }, items, uniques, rolls, new Random(1)));
        Assert.Null(DivinationRewards.Create(doctor with { Kind = "void" }, items, uniques, rolls, new Random(1)));
        // Two corruption implicits replace ordinary base implicits, rather than keeping one and adding one.
        var doubleImplicit = doctor with { Name = "Astramentis", TwoImplicit = true, Corrupted = true, ItemLevel = 100 };
        var amulet = DivinationRewards.Create(doubleImplicit, items, uniques, rolls, new Random(1))!;
        Assert.True(amulet.Corrupted);
        var implicits = amulet.Mods.Where(m => m.Section == ModSection.Implicit).ToArray();
        Assert.Equal(2, implicits.Length);
        Assert.All(implicits, m => Assert.Equal(5, items.Mods.ByHash(m.Hash)!.GenerationType));
        // A plain "Corrupted" equipment reward has one corruption implicit in place of the base's own.
        var plain = DivinationRewards.Create(doubleImplicit with { TwoImplicit = false }, items, uniques, rolls, new Random(1))!;
        Assert.True(plain.Corrupted);
        Assert.Equal(5, items.Mods.ByHash(Assert.Single(plain.Mods, m => m.Section == ModSection.Implicit).Hash)!.GenerationType);
        var humble = DivinationRewards.Find(items.Find("Humility")!.Id)!;
        var robe = DivinationRewards.Create(humble, items, uniques, rolls, new Random(1))!;
        Assert.Equal(6, robe.Sockets.Count);
        Assert.Equal(6, Assert.Single(robe.Links!));
        Assert.All(robe.Sockets, s => Assert.Equal(SocketColour.White, s.Colour));
        var atlas = new AtlasCatalog(items);
        foreach (var (name, tier) in new[] { ("Cartographer's Delight", 5), ("Rain Tempter", 6),
            ("The Arena Champion", 12), ("The Surveyor", 14), ("Lost Worlds", 15), ("The Trial", 15) })
        {
            var definition = DivinationRewards.Find(items.Find(name)!.Id)!;
            var map = DivinationRewards.Create(definition, items, uniques, rolls, new Random(499))!;
            Assert.Equal(tier, atlas.TierOf(map.TypeHash));
            Assert.Equal(name == "The Trial" ? ItemRarity.Rare : ItemRarity.Normal, map.Rarity);
            Assert.Equal(name == "The Trial", map.Corrupted);
        }
        Assert.Equal(14, DivinationRewards.All.Values.Count(r => r.Kind == "disabled" && !r.Enabled));
        foreach (var (name, level, corrupted) in new[] { ("The Artist", 4, true), ("The Dragon's Heart", 4, true),
            ("The Enlightened", 3, false), ("Wealth and Power", 4, true) })
        {
            var definition = DivinationRewards.Find(items.Find(name)!.Id)!;
            var gem = DivinationRewards.Create(definition, items, uniques, rolls, new Random(499))!;
            Assert.Equal(level, items.GemLevel(gem));
            Assert.Equal(corrupted, gem.Corrupted);
            Assert.EndsWith(" Support", items.GemName(gem));
        }
        foreach (var reward in DivinationRewards.All.Values)
        {
            var card = items.Find(reward.Card)!;
            Assert.Equal("DivinationCard", card.Class);
            Assert.Equal(card.MaxStack, reward.Stack);
        }
        var taggedCards = new Dictionary<string, string> { ["Deathly Designs"] = "trap", ["The Bitter Blossom"] = "chaos",
            ["The Cataclysm"] = "spell", ["The Rite of Elements"] = "golem", ["The Summoner"] = "minion",
            ["The Wilted Rose"] = "aura", ["Terrible Secret of Space"] = "golem", ["Volatile Power"] = "vaal" };
        var gemRows = GameServer.Core.ClientTables.Table("SkillGems");
        var effects = GameServer.Core.ClientTables.Table("GemEffects");
        var tags = GameServer.Core.ClientTables.Table("GemTags");
        foreach (var reward in DivinationRewards.All.Values.Where(r => r.Kind == "gem_pool" && r.Enabled
            || r.Name == "Portal" && r.QualityMax > 0))
        for (int seed = 0; seed < 16; seed++)
        {
            var gem = DivinationRewards.Create(reward, items, uniques, rolls, new Random(seed))!;
            Assert.NotNull(gem);
            var type = items.ByHash(gem.TypeHash)!;
            var details = items.Gem(type)!;
            Assert.True(details.Variant(gem.GemVariant).Creatable);
            Assert.Equal(reward.GemLevel > 0 ? reward.GemLevel : 1, items.GemLevel(gem));
            Assert.Equal(reward.Corrupted, gem.Corrupted);
            Assert.InRange((int)gem.Quality, reward.Quality, reward.QualityMax > 0 ? reward.QualityMax : reward.Quality);
            var cardName = items.Find(reward.Card)!.Name;
            if (taggedCards.TryGetValue(cardName, out string? tag))
            {
                var source = gemRows.First(r => r.IntOrNull("BaseItemTypesKey") == type.Row);
                int effect = source.Ints("GemVariants").ElementAt(gem.GemVariant);
                Assert.Contains(effects[effect].Ints("GemTags").Select(t => tags[t].Text("Id")), actual => actual == tag);
            }
            if (reward.Name.StartsWith("Transfigured", StringComparison.Ordinal))
            {
                Assert.True(gem.GemVariant > 0);
                Assert.False(details.IsSupport || details.IsVaalOrAwakened);
            }
            if (reward.Name is "Exceptional Support Gem" or "Awakened Exceptional Gem")
            {
                Assert.True(details.IsSupport && details.IsExceptional);
                Assert.Equal(reward.Name == "Awakened Exceptional Gem", details.IsAwakened);
            }
            if (reward.Name == "Empower, Enhance or Enlighten")
                Assert.Contains(type.Name, new[] { "Empower Support", "Enhance Support", "Enlighten Support" });
        }
        Assert.Null(DivinationRewards.Create(doctor with { Kind = "gem_pool", Name = "Unknown gem pool" }, items, uniques, rolls, new Random(1)));
        var failures = new List<string>();
        foreach (var reward in DivinationRewards.All.Values.Where(r => r.Enabled && r.Equipment is not null))
        {
            foreach (var member in reward.Members.Where(m => DivinationEquipmentRewards.Eligible(m, reward.Equipment, reward.Count, items, rolls)))
                if (DivinationRewards.Create(reward with { Members = [member] }, items, uniques, rolls, new Random(499)) is null)
                    failures.Add(reward.Card + " member=" + member.Id + " base=" + member.Base);
            foreach (var member in reward.Members.Where(m => m.Kind == "base"))
            {
                var type = items.Find(member.Id)!;
                Assert.True(type.Supported && type.Name.Length > 0, member.Id);
                Assert.True(type.MaxStack >= reward.Count, member.Id);
            }
            for (int seed = 0; seed < 16; seed++)
            {
                var result = DivinationRewards.Create(reward, items, uniques, rolls, new Random(seed));
                if (result is null) { failures.Add(reward.Card + " seed=" + seed); continue; }
                if (reward.Kind == "set_pool")
                {
                    Assert.Equal(result.Payload(), CharacterSaves.FromSaved(CharacterSaves.ToSaved(result, items.Mods), items).Payload());
                    continue;
                }
                var rule = reward.Equipment!;
                Assert.Equal(rule.Influences, result.Influences & rule.Influences);
                if (rule.RandomInfluences > 0)
                    Assert.True(System.Numerics.BitOperations.PopCount((uint)result.Influences) >= rule.RandomInfluences);
                if (rule.Fractured) Assert.Equal(1, result.FracturedCount);
                if (rule.Modifiers > 0) Assert.Equal(rule.Modifiers, result.Mods.Count(m => m.Section == ModSection.Explicit));
                if (rule.Links > 0) Assert.Contains((byte)rule.Links, result.Links!);
                if (rule.Special == "vestigial")
                {
                    Assert.True(result.Vestigial);
                    Assert.Null(rolls.Reforge(result, new Random(seed)));
                }
                if (rule.Special == "synthesised")
                {
                    Assert.True(result.Synthesised);
                    Assert.Equal(3, result.Mods.Count(m => m.Section == ModSection.Implicit));
                    Assert.All(result.Mods.Where(m => m.Section == ModSection.Implicit), m =>
                        Assert.StartsWith("SynthesisImplicit", items.Mods.ByHash(m.Hash)!.Id));
                }
                if (rule.Special == "foulborn")
                    Assert.Contains(result.Mods, m => items.Mods.ByHash(m.Hash)!.Id == "MutatedUniqueBelt43MagicUtilityFlasksAlwaysApplyRightmost");
                if (rule.Special == "perfect_eldritch")
                    Assert.Contains(result.Mods, m => items.Mods.ByHash(m.Hash) is { GenerationType: 28 or 29 } mod
                        && mod.Id.EndsWith("EldritchImplicit6", StringComparison.Ordinal));
                if (rule.Special == "corrupted_blood")
                    Assert.Contains(result.Mods, m => m.Section == ModSection.Implicit
                        && items.Mods.ByHash(m.Hash)!.Id.Contains("CorruptedBloodImmunity", StringComparison.Ordinal));
                if (rule.Special == "double_veiled")
                    Assert.Equal(2, result.Mods.Count(m => items.Mods.ByHash(m.Hash)!.Domain == 26));
                if (rule.Special == "blueprint")
                {
                    var blueprint = result.HeistBlueprint!;
                    Assert.NotNull(blueprint);
                    Assert.Equal(83, blueprint.AreaLevel);
                    Assert.InRange(blueprint.Wings.Count, 3, 4);
                    Assert.All(blueprint.Wings, w =>
                    {
                        Assert.True(w.Revealed);
                        Assert.Equal(3, w.Jobs.Count);
                        Assert.All(w.Rooms, r => Assert.True(r.Revealed));
                        Assert.Contains(w.Rooms, r => GameServer.Core.ClientTables.Table("HeistChestRewardTypes")[r.Row].Text("Id") == "EscapeRoom");
                    });
                }
                Assert.Equal(!rule.Unidentified, result.Identified);
                Assert.NotEmpty(result.Payload());
                var restored = CharacterSaves.FromSaved(CharacterSaves.ToSaved(result, items.Mods), items);
                Assert.Equal(result.Payload(), restored.Payload());
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        var unresolved = DivinationRewards.All.Values.Where(r => r.Enabled
            && DivinationRewards.Create(r, items, uniques, rolls, new Random(499)) is null).Select(r => r.Card).ToArray();
        Assert.True(unresolved.Length == 0, string.Join("\n", unresolved));
    }

    [Fact]
    public void Corrupted_rewards_replace_equipment_implicits_without_changing_special_rewards()
    {
        var items = Program.Items;
        var uniques = new UniqueCatalog(items);
        var rolls = new ItemRolls(items);
        Item Reward(string name) => DivinationRewards.Create(DivinationRewards.Find(items.Find(name)!.Id)!,
            items, uniques, rolls, new Random(974))!;
        foreach (var name in new[] { "Apocalypse", "Divine Justice", "A Mother's Parting Gift", "The Warden" })
        {
            var reward = Reward(name);
            Assert.NotNull(reward);
            Assert.True(reward.Corrupted);
            var implicitMod = Assert.Single(reward.Mods, m => m.Section == ModSection.Implicit);
            Assert.Equal(5, items.Mods.ByHash(implicitMod.Hash)!.GenerationType);
            if (name == "Apocalypse")
            {
                var definition = uniques.All.First(d => d.Name == "Eyes of the Greatwolf");
                Assert.NotEmpty(definition.Mods);
                Assert.All(definition.Mods, id => Assert.Contains(reward.Mods,
                    m => m.Section == ModSection.Explicit && items.Mods.ByHash(m.Hash)!.Id == id));
            }
        }
        var immunity = Reward("Lachrymal Necrosis");
        var immunityMod = Assert.Single(immunity.Mods, m => m.Section == ModSection.Implicit);
        Assert.Contains("CorruptedBloodImmunity", items.Mods.ByHash(immunityMod.Hash)!.Id);
        var map = Reward("The Trial");
        Assert.True(map.Corrupted);
        Assert.DoesNotContain(map.Mods, m => m.Section == ModSection.Implicit && items.Mods.ByHash(m.Hash)!.GenerationType == 5);
        var gem = Reward("The Artist");
        Assert.True(gem.Corrupted);
        Assert.DoesNotContain(gem.Mods, m => m.Section == ModSection.Implicit);
        var talisman = Reward("Call to the First Ones");
        Assert.True(talisman.Corrupted);
        Assert.Contains(talisman.Mods, m => m.Section == ModSection.Implicit);
        Assert.DoesNotContain(talisman.Mods, m => m.Section == ModSection.Implicit && items.Mods.ByHash(m.Hash)!.GenerationType == 5);
    }

    public class HookContext : DispatchProxy
    {
        public IModuleLifetime Lifetime = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Lifetime" => Lifetime,
            "get_Manifest" => new ModManifest(Lifetime.Id, "0", ModKind.Feature, [], Lifetime.Id + ".dll"),
            _ => throw new NotSupportedException(method.Name),
        };
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        public NetworkStream Stream => writer.GetStream();
        public ConnectionCiphers Ciphers { get; } = new(new byte[64], false);
        public Player Conn { get; } = new(new(0, "1_1_town", 32, 32, 10, 10, null, []), 0, 1)
            { Instance = new(), PlayerLife = 50, Character = "Cards" + Guid.NewGuid().ToString("N"),
              LeagueModule = new(new League("cards", "Standard", false, false), new HashSet<string>(["Game", "divination-test"])) };
        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }
        public void Dispose() { Conn.Closed = true; reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
