using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;
using System.Text.Json;

namespace GameServer.Gateway.Tests;

public sealed class PobImportTests
{
    sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Now;
    }

    static CharacterBuildImports.Prepared Plan(Character record, CharacterSaves.Save save) =>
        new(record.Id, record.AccountId, record.League, CharacterClass.Templar, 90, save, "test", []);

    [Fact]
    public void Confirmation_is_single_use_expires_and_cannot_reuse_a_cancelled_or_replaced_build()
    {
        var clock = new Clock();
        var session = new CharacterBuildImports.Session(clock);
        var record = new Character(Guid.NewGuid(), Guid.NewGuid(), "PobConfirm", CharacterClass.Witch, 1,
            "Standard", "1_1_town", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var plan = Plan(record, new([]));
        Assert.Null(session.Confirm());
        session.Stage(plan);
        Assert.Same(plan, session.Confirm());
        Assert.Null(session.Confirm());
        session.Stage(plan);
        clock.Now = 300;
        Assert.Null(session.Confirm());
        session.Stage(plan);
        session.Clear();
        Assert.Null(session.Confirm());
        session.Stage(plan);
        var replacement = plan with { Level = 95 };
        session.Stage(replacement);
        Assert.Same(replacement, session.Confirm());
        Assert.Null(session.Confirmed); // Completing the countdown, not Y alone, authorizes the commit.
        session.Accept(replacement);
        Assert.Same(replacement, session.Confirmed);
        Transformed_component_embeds_an_unframed_item_or_a_null_type_hash();
        Energy_blade_save_round_trip_retains_damage_gems_abyss_jewel_and_original();
    }

    [Fact]
    public void Apply_is_atomic_merges_latest_unrelated_progress_and_retires_old_inventory_writers()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pob-import-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var record = new Character(Guid.NewGuid(), Guid.NewGuid(), "PobAtomic", CharacterClass.Witch, 10,
            "Standard", "1_1_town", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        try
        {
            var accounts = new SqliteAccountStore(Saves.Db);
            accounts.AddCharacter(record);
            var catalog = new ItemCatalog("data");
            var item = ItemCatalog.Create(catalog.All.First(t => t.Name == "Driftwood Wand"));
            var old = CharacterSaves.ToSaved(item, catalog.Mods);
            var latest = new CharacterSaves.Save(new()
            {
                [0] = [new(901, 1, 1, old)], [2] = [new(902, 0, 0, old)],
            }, Experience: 42, QuestFlags: ["KeepMe"], PortalCosmetic: 3, ModuleData: new() { ["keep"] = "new" });
            CharacterSaves.Write(record.Name, latest);
            var cached = PlayerInventories.For(record.Name, catalog);
            var proposed = new CharacterSaves.Save(new() { [2] = [new(1, 0, 0, old)] }, Experience: 1234,
                Passives: [10, 20]);
            var plan = Plan(record, proposed);
            string before = JsonSerializer.Serialize(CharacterSaves.Load(record.Name));
            Saves.FailWrite = key => key == record.Name;
            Assert.Throws<IOException>(() => CharacterBuildImports.Apply(plan));
            Assert.Equal(before, JsonSerializer.Serialize(CharacterSaves.Load(record.Name)));
            Assert.Equal(record, accounts.GetCharacter(record.Id));
            Saves.FailWrite = null;
            CharacterBuildImports.Apply(plan);
            var saved = CharacterSaves.Load(record.Name)!;
            Assert.Equal([901u], saved.Inventories[0].Select(i => i.Id));
            Assert.Equal([902u], saved.Inventories[2].Select(i => i.Id));
            Assert.Equal(latest.QuestFlags, saved.QuestFlags);
            Assert.Equal("new", saved.ModuleData!["keep"]);
            Assert.Equal(latest.PortalCosmetic, saved.PortalCosmetic);
            Assert.Equal(1234u, saved.Experience);
            Assert.Equal(CharacterClass.Templar, accounts.GetCharacter(record.Id)!.Class);
            Assert.Equal(90, accounts.GetCharacter(record.Id)!.Level);
            Assert.NotSame(cached, PlayerInventories.For(record.Name, catalog));
            cached.ChangeUnsaved(2, 902, i => i with { Quality = 17 });
            cached.SaveUnsaved();
            Assert.Equal(0, CharacterSaves.Load(record.Name)!.Inventories[2][0].Item.Quality);
            Assert.Throws<InvalidDataException>(() => CharacterBuildImports.Apply(plan with { AccountId = Guid.NewGuid() }));
        }
        finally
        {
            Saves.FailWrite = null;
            PlayerInventories.Forget(record.Name);
            TestEnvironment.ReleaseSaves(path);
        }
    }
    static void Transformed_component_embeds_an_unframed_item_or_a_null_type_hash()
    {
        var blade = new Item(0x11223344, ["Quality", "Transformed", "RenderItem"], 0) { Quality = 11 };
        Assert.Equal(Convert.FromHexString("4433221100000B00000000"), blade.Payload());
        var original = new Item(0x55667788, ["Quality"], 0) { Quality = 33 };
        Assert.Equal(Convert.FromHexString("4433221100000B88776655000021"), (blade with { OriginalItem = original }).Payload());
    }

    static void Energy_blade_save_round_trip_retains_damage_gems_abyss_jewel_and_original()
    {
        var items = new ItemCatalog("data");
        var type = items.All.Single(t => t.Id.EndsWith("/StormBladeOneHand", StringComparison.Ordinal));
        Assert.True(type.Supported);
        var lightning = items.Mods.ById("StormBladeImplicitLocalLightningDamage_1")!;
        var gem = ItemCatalog.Create(items.All.First(t => t.Name == "Fireball"));
        var jewel = ItemCatalog.Create(items.All.First(t => t.Name == "Hypnotic Eye Jewel"));
        var original = ItemCatalog.Create(items.All.First(t => t.Class == "One Hand Sword" && !t.Components.Contains("Transformed")))
            with { ItemLevel = 83, Quality = 20 };
        var blade = ItemCatalog.Create(type, defenceBaseRoll: 0) with
        {
            ItemLevel = 83, OriginalItem = original,
            Mods = [new(ModSection.Implicit, lightning.Hash, [78, 1558])],
            Sockets = [new(SocketColour.White, gem), new(SocketColour.White), new(SocketColour.Abyss, jewel)], Links = [2, 1],
        };
        var saved = CharacterSaves.ToSaved(blade, items.Mods);
        var restored = CharacterSaves.FromSaved(JsonSerializer.Deserialize<CharacterSaves.SavedItem>(JsonSerializer.Serialize(saved))!, items);
        Assert.Equal(blade.Payload(), restored.Payload());
        Assert.Equal(original.TypeHash, restored.OriginalItem!.TypeHash);
        Assert.Equal(gem.TypeHash, restored.Sockets[0].Gem!.TypeHash);
        Assert.Equal(jewel.TypeHash, restored.Sockets[2].Gem!.TypeHash);
        Assert.Equal(new[] { 78, 1558 }, restored.Mods.Single().Values);
        // A retained hybrid gem must not publish its support-only half into Actor skills.
        var automation = ItemCatalog.Create(items.All.First(t => t.Name == "Automation"));
        var variant = items.Gem(items.ByHash(automation.TypeHash)!)!.Variant(automation.GemVariant);
        Assert.Equal(new[] { variant.Effect }, items.GemEffects(automation));
        Assert.True(variant.Effect2 >= 0);
    }
}
