using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;
using System.Text.Json;

namespace GameServer.Gateway.Tests;

public sealed class PobImportTests
{
    [Fact]
    public async Task External_bridge_rejects_other_identities_versions_and_unbounded_output_before_staging()
    {
        var record = new Character(Guid.NewGuid(), Guid.NewGuid(), "PobBridge", CharacterClass.Witch, 1,
            "Standard", "1_1_town", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        string Payload(Character character, int version = 1) => JsonSerializer.Serialize(new
        {
            Version = version, Record = character, Save = new CharacterSaves.Save([]), Summary = "Prepared", Warnings = Array.Empty<string>(),
        });
        var plan = PobBridge.Validate(Payload(record with { Level = 90 }), record);
        Assert.Equal(90, plan.Level);
        Assert.Equal(record.Id, plan.CharacterId);
        Assert.Throws<InvalidDataException>(() => PobBridge.Validate(Payload(record, 2), record));
        Assert.Throws<InvalidDataException>(() => PobBridge.Validate(Payload(record with { Id = Guid.NewGuid() }), record));
        Assert.Throws<InvalidDataException>(() => PobBridge.Validate(Payload(record with { AccountId = Guid.NewGuid() }), record));
        Assert.Throws<InvalidDataException>(() => PobBridge.Validate(Payload(record with { Name = "Other" }), record));
        Assert.Throws<InvalidDataException>(() => PobBridge.Validate(Payload(record with { League = "Other" }), record));
        Assert.Throws<InvalidDataException>(() => PobBridge.Validate(Payload(record with { Level = 101 }), record));
        Assert.Equal("1234", await PobBridge.ReadLimited(new StringReader("1234"), 4, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => PobBridge.ReadLimited(new StringReader("12345"), 4, CancellationToken.None));
        var previous = PobBridge.Settings;
        try
        {
            PobBridge.Settings = new();
            await Assert.ThrowsAsync<InvalidDataException>(() => PobBridge.Convert("code", record, null, CancellationToken.None));
        }
        finally { PobBridge.Settings = previous; }
    }

    sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Now;
    }

    static CharacterBuildImports.Prepared Plan(Character record, CharacterSaves.Save save) =>
        new(record.Id, record.AccountId, record.League, CharacterClass.Templar, 90, save, "test", []);

    void Confirmation_is_single_use_expires_and_cannot_reuse_a_cancelled_or_replaced_build()
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
        Confirmation_is_single_use_expires_and_cannot_reuse_a_cancelled_or_replaced_build();
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
            }, Experience: 42, QuestFlags: ["KeepMe"], PortalCosmetic: 3, PetrifiedBlood: true,
                ModuleData: new() { ["keep"] = "new", ["Game.Golems"] = "old source" });
            CharacterSaves.Write(record.Name, latest);
            var cached = PlayerInventories.For(record.Name, catalog);
            var proposed = new CharacterSaves.Save(new() { [2] = [new(1, 0, 0, old)] }, Experience: 1234,
                Passives: [10, 20]);
            var plan = Plan(record, proposed);
            CharacterEquipmentState.Inactive[record.Name] = [902];
            var oldGemIds = GemSkillIds.ByCharacter.GetOrAdd(record.Name, _ => new());
            oldGemIds.Assign([new(902, 0, 12)], [0x4000, 0x4001]);
            string before = JsonSerializer.Serialize(CharacterSaves.Load(record.Name));
            Saves.FailWrite = key => key == record.Name;
            Assert.Throws<IOException>(() => CharacterBuildImports.Apply(plan));
            Assert.Equal(before, JsonSerializer.Serialize(CharacterSaves.Load(record.Name)));
            Assert.Equal(record, accounts.GetCharacter(record.Id));
            Assert.Contains(902u, CharacterEquipmentState.Inactive[record.Name]);
            Assert.Same(oldGemIds, GemSkillIds.ByCharacter[record.Name]);
            Saves.FailWrite = null;
            CharacterBuildImports.Apply(plan);
            Assert.False(CharacterEquipmentState.Inactive.ContainsKey(record.Name));
            Assert.False(GemSkillIds.ByCharacter.ContainsKey(record.Name));
            Assert.Equal((ushort?)0x4000, GemSkillIds.ByCharacter.GetOrAdd(record.Name, _ => new())
                .Assign([new(902, 0, 13)], [0x4000, 0x4001]).Single());
            var saved = CharacterSaves.Load(record.Name)!;
            Assert.Equal([901u], saved.Inventories[0].Select(i => i.Id));
            Assert.Equal([902u], saved.Inventories[2].Select(i => i.Id));
            Assert.Equal(latest.QuestFlags, saved.QuestFlags);
            Assert.Equal("new", saved.ModuleData!["keep"]);
            Assert.False(saved.ModuleData.ContainsKey("Game.Golems"));
            Assert.Null(saved.PetrifiedBlood);
            Assert.Equal(latest.PortalCosmetic, saved.PortalCosmetic);
            Assert.Equal(1234u, saved.Experience);
            Assert.Equal(CharacterClass.Templar, accounts.GetCharacter(record.Id)!.Class);
            Assert.Equal(90, accounts.GetCharacter(record.Id)!.Level);
            Assert.NotSame(cached, PlayerInventories.For(record.Name, catalog));
            cached.ChangeUnsaved(2, 902, i => i with { Quality = 17 });
            cached.SaveUnsaved();
            Assert.Equal(0, CharacterSaves.Load(record.Name)!.Inventories[2][0].Item.Quality);
            Assert.Throws<InvalidDataException>(() => CharacterBuildImports.Apply(plan with { AccountId = Guid.NewGuid() }));

            // An imported item may legitimately sustain its own requirement once equipped. Reusing the id of an
            // inactive old item must not require allocating a temporary Strength node to wake the new build up.
            var glovesType = catalog.All.First(t => t.Class == "Gloves" && t.Supported && t.DropLevel < 80
                && catalog.Requirements(t) is { Str: > 14 and < 40, Dex: 0, Int: 0 });
            var strength = catalog.Mods.All.First(m => m.Domain == 1 && m.GenerationType == 2 && m.Level < 80
                && m.Stats.Count == 1 && m.Stats[0].Id == "additional_strength" && m.RangeAt(0).Max >= 40);
            int addedStrength = strength.RangeAt(0).Max;
            var fireball = ItemCatalog.Create(catalog.All.First(t => t.Name == "Fireball"));
            var gloves = ItemCatalog.Create(glovesType) with { Rarity = ItemRarity.Rare,
                Mods = [new(ModSection.Explicit, strength.Hash, [addedStrength])],
                Sockets = [new(SocketColour.White, fireball)], Links = [1] };
            var ready = new CharacterSaves.Save(new() { [8] = [new(1, 0, 0, CharacterSaves.ToSaved(gloves, catalog.Mods))] },
                Experience: CharacterStats.ExperienceFor(90), Passives: []);
            CharacterEquipmentState.Inactive[record.Name] = [902];
            CharacterBuildImports.Apply(Plan(record, ready) with { Class = CharacterClass.Witch });
            int row = Program.Skills.RowOfClass((int)CharacterClass.Witch);
            var admitted = new ClientState(new(1, "pob-readiness", 1000, 1000, 100, 100, null, []), 1, 1)
            { Character = record.Name, CharacterId = record.Id, ClassRow = row,
                Pools = CharacterStats.For(Program.Skills.ClassFor(row), 90) };
            Game.PrepareCharacterForArea("test", admitted, 90);
            Assert.Empty(admitted.PlayerStats); // No developer attribute overrides or extra passive allocation.
            Assert.Empty(CharacterEquipmentState.Inactive[record.Name]);
            Assert.Equal(Program.Skills.ClassFor(row).Strength + addedStrength, (int)admitted.Pools.Strength);
            var importedSkill = Game.GemSkillEffects(admitted).Single(g => g.Name == "Fireball");
            Assert.True(Game.SupportedSkill(admitted, importedSkill.Id).RequirementsMet);
            Assert.True(Game.SupportedHitFor(admitted, importedSkill.Id, suppress: false).MaxTotal > 0);
        }
        finally
        {
            Saves.FailWrite = null;
            PlayerInventories.Forget(record.Name);
            CharacterEquipmentState.Replaced(record.Name);
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
