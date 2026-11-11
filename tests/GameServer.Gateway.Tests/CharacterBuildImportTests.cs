using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;
using System.Text.Json;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class CharacterBuildImportTests
{
    [Fact]
    public async Task Public_mod_imports_validate_identity_expiry_lifetime_and_single_use_confirmation()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mod-build-api-{Guid.NewGuid():N}.db");
        string mods = Path.Combine(Path.GetTempPath(), $"mod-build-probe-{Guid.NewGuid():N}");
        Saves.Open(path);
        var record = new Character(Guid.NewGuid(), Guid.NewGuid(), "ModBuild", CharacterClass.Witch, 1,
            "Standard", "1_1_town", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var conn = new ClientState(new(1, "mod-build", 1000, 1000, 100, 100, null, []), 1, 1)
        { CharacterId = record.Id, Character = record.Name, Account = record.AccountId.ToString(), PlayerLife = 1000 };
        var player = new ImportPlayer(conn);
        var lifetime = new ModuleLifetime("probe");
        var clock = new Clock();
        var api = new CharacterBuildApi(lifetime, clock);
        string Payload(Character character, int version = 1) => JsonSerializer.Serialize(new
        {
            Version = version, Record = character, Save = new CharacterSaves.Save([]), Summary = "Prepared", Warnings = Array.Empty<string>(),
        });
        // The probe's /capture resolves the player through Program.PlayerOf, which needs a game logic: install one for
        // this test instead of inheriting whatever another test in the same process left behind (the suite is one
        // process, so without this the test passes or fails by class order).
        IGameLogic? previousLogic = Program.GameLogicOrNull;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            new SqliteAccountStore(Saves.Db).AddCharacter(record);
            Assert.Equal(record, JsonSerializer.Deserialize<Character>(api.Capture(player)));
            foreach (var invalid in new[] { record with { Id = Guid.NewGuid() }, record with { AccountId = Guid.NewGuid() },
                record with { Name = "Other" }, record with { League = "Other" }, record with { Level = 101 } })
                Assert.Throws<InvalidDataException>(() => api.Prepare(player, Payload(invalid)));
            Assert.Throws<InvalidDataException>(() => api.Prepare(player, Payload(record, 2)));
            Assert.Throws<InvalidDataException>(() => api.Prepare(player, new string(' ', CharacterBuildImports.MaxJsonLength + 1)));
            using (var cancelled = api.Prepare(player, Payload(record)))
            { cancelled.Dispose(); Assert.False(cancelled.Confirm()); }
            using (var expired = api.Prepare(player, Payload(record)))
            { clock.Now = 300; Assert.False(expired.Confirm()); }
            using (var dead = api.Prepare(player, Payload(record)))
            { conn.PlayerLife = 0; Assert.False(dead.Confirm()); conn.PlayerLife = 1000; }
            using (var moved = api.Prepare(player, Payload(record)))
            { conn.LeavingArea = true; Assert.False(moved.Confirm()); conn.LeavingArea = false; }
            using (var disconnected = api.Prepare(player, Payload(record)))
            { conn.Closed = true; Assert.False(disconnected.Confirm()); conn.Closed = false; }
            var stopped = api.Prepare(player, Payload(record));
            lifetime.Stop(e => throw e);
            Assert.False(stopped.Confirm()); stopped.Dispose();
            Assert.Throws<InvalidOperationException>(() => api.Capture(player));
            Assert.Null(conn.BuildImport.Confirmed);

            // The probe's commands, as a connection builds them; PlayerOf(conn) is this test's player.
            Program.ModPlayers.AddOrUpdate(conn, player);
            ChatCommands Commands()
            {
                var commands = new ChatCommands { Send = (_, _) => Task.CompletedTask, Reply = player.Tell };
                foreach (var build in GameEvents.CommandsBuilding.All) commands = build(commands, "", null!, null!, conn);
                return commands;
            }
            Directory.CreateDirectory(Path.Combine(mods, "probe"));
            File.WriteAllText(Path.Combine(mods, "probe", "probe.cs"), """
                public sealed class Probe : IMod
                {
                    public void Load(IModContext mod) => GameEvents.Command(mod.Manifest.Id, "capture", "/capture",
                        (conn, _) => Program.PlayerOf(conn).Tell(CharacterBuildApi.For(mod).Capture(Program.PlayerOf(conn))));
                }
                """);
            using (var host = new ModHost(mods, _ => { }))
            {
                host.Reload();
                Assert.True(host.IsLoaded("probe"), string.Join("; ", host.Mods().Select(m => m.Error)));
                Assert.True(await Commands().RunAsync("/capture"));
                Assert.Contains(player.Messages, m => JsonSerializer.Deserialize<Character>(m) == record);
                Assert.True(host.SetEnabled("probe", false));
                Assert.False(host.IsLoaded("probe"));
                // Unloading released its subscription: nobody answers /capture any more.
                Assert.False(await Commands().RunAsync("/capture"));
            }
            var acceptedLifetime = new ModuleLifetime("accepted");
            using var accepted = new CharacterBuildApi(acceptedLifetime, clock).Prepare(player, Payload(record with { Level = 90 }));
            Assert.Equal("Prepared", accepted.Summary); Assert.Empty(accepted.Warnings);
            Assert.True(accepted.Confirm()); Assert.False(accepted.Confirm());
            Assert.True(conn.Closed); Assert.True(CharacterBuildImports.IsApplying(record.Id));
            Assert.Equal(90, conn.BuildImport.Confirmed!.Level);
            acceptedLifetime.Stop(e => throw e); accepted.Dispose();
            Assert.NotNull(conn.BuildImport.Confirmed); // Acceptance belongs to the server's old-session drain.
        }
        finally
        {
            Program.SetGameLogic(previousLogic!);
            CharacterBuildImports.Release(conn);
            TestEnvironment.ReleaseSaves(path);
            if (Directory.Exists(mods)) Directory.Delete(mods, recursive: true);
        }
    }

    sealed class ImportPlayer(ClientState conn) : IConnectionPlayer
    {
        public ClientState Conn => conn;
        public List<string> Messages { get; } = [];
        public string Character => conn.Character!;
        public string? Account => conn.Account;
        public string League => "Standard";
        public string LeagueId => "Standard";
        public bool Hardcore => false;
        public bool SoloSelfFound => false;
        public bool Uses(string module) => true;
        public int Level => 1;
        public string AreaId => "1_1_town";
        public uint Seed => 1;
        public (int X, int Y) Position => (1, 1);
        public bool Closed => conn.Closed;
        public Task Tell(string text) { Messages.Add(text); return Task.CompletedTask; }
        public Task Send(byte[] bytes, string? log = null) => Task.CompletedTask;
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
