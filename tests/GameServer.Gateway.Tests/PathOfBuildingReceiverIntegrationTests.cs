using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public partial class PathOfBuildingServerTests
{
    [Fact]
    public void Warning_bounds_keep_missing_gear_visible_and_report_the_full_warning_count()
    {
        var build = GameServer.Core.Imports.PathOfBuilding.Parse(Build);
        var progress = new PathOfBuildingCharacters.Progress(build.Class, build.Level, 0, [], [], 0, []);
        var warnings = Enumerable.Range(0, 100).Select(i => "Modifier " + i + new string('\u754c', 2000))
            .Append("Skipped item in Helmet: unsupported base.").ToArray();
        var equipment = new PathOfBuildingItems.Result([], warnings, []);
        var summary = PathOfBuildingImport.Describe(build, progress, equipment);
        Assert.StartsWith("Skipped item in Helmet", summary.Warnings[0]);
        Assert.Equal(101, summary.WarningCount);
        Assert.True(summary.WarningsTruncated);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(new PathOfBuildingImport.Prepared(build, progress, equipment, summary)
            .Response(true, "Test", "Standard")).Length < 64 * 1024);
    }

    [Fact]
    public async Task Listener_releases_its_port_on_shutdown_and_reports_address_collisions()
    {
        await using var fixture = await Fixture.Start();
        var config = new PathOfBuildingServerConfig { ListenUrl = fixture.Server.Address, DatabasePath = fixture.Database };
        await using (var collision = new PathOfBuildingServer(config))
            await Assert.ThrowsAnyAsync<IOException>(() => collision.StartAsync());
        await fixture.Server.DisposeAsync();
        await using var restarted = new PathOfBuildingServer(config);
        await restarted.StartAsync();
        Assert.Equal(config.ListenUrl, restarted.Address);
    }

    [Fact]
    public void Config_uses_normal_server_defaults_and_rejects_independent_storage_settings()
    {
        string folder = Directory.CreateTempSubdirectory("pob-config-").FullName;
        try
        {
            string path = Path.Combine(folder, "receiver.json");
            var absent = PathOfBuildingServerConfig.Load(path, optional: true);
            Assert.False(absent.Enabled);
            Assert.Equal(SaveDatabase.DefaultPath, absent.DatabasePath);
            Assert.Equal("data", absent.DataFolder);
            Assert.Equal(LeagueCatalog.FromEnvironment().All, absent.Leagues.All);
            File.WriteAllText(path, "{\"enabled\":true}");
            var config = PathOfBuildingServerConfig.Load(path);
            Assert.True(config.Enabled);
            Assert.Equal("http://127.0.0.1:20496", config.ListenUrl);
            foreach (string field in new[] { "repositoryRoot", "dataFolder", "databasePath", "clientGgpk", "leagues", "unknownSetting" })
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string> { [field] = "obsolete" }));
                Assert.Throws<JsonException>(() => PathOfBuildingServerConfig.Load(path));
            }
            File.WriteAllText(path, "{\"listenUrl\":\"http://localhost:20496/path\"}");
            Assert.Throws<ArgumentException>(() => PathOfBuildingServerConfig.Load(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Import_leagues_are_a_subset_of_the_game_catalog_and_Ruthless_stays_blocked()
    {
        var config = new PathOfBuildingServerConfig { Leagues = new LeagueCatalog(["Custom"]), AllowedLeagues = ["HC SSF Custom"] };
        config.Validate();
        Assert.Equal("HC SSF Custom", config.SelectLeague("HC SSF Custom").Name);
        Assert.Throws<ArgumentException>(() => config.SelectLeague("Custom"));
        Assert.Throws<ArgumentException>(() => config.SelectLeague("Standard"));
        Assert.Throws<ArgumentException>(() => (config with { AllowedLeagues = ["Standard"] }).Validate());
        Assert.Throws<ArgumentException>(() => (config with { AllowedLeagues = null }).SelectLeague("Ruthless Custom"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Large_non_ASCII_warnings_are_bounded_before_creation_and_report_omissions(bool create)
    {
        await using var fixture = await Fixture.Start();
        string build = Build.Replace("ascendClassName=\"None\"", "ascendClassName=\"" + new string('\u754c', 140000) + "\"");
        using var response = await fixture.Send(create ? "/api/pob/characters" : "/api/pob/preview", "alice_import", fixture.Token, build);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length < 64 * 1024);
        var result = JsonSerializer.Deserialize<JsonElement>(bytes);
        Assert.True(result.GetProperty("partial").GetBoolean());
        Assert.True(result.GetProperty("warningsTruncated").GetBoolean());
        Assert.Contains(result.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.StartsWith("Skipped ascendancy"));
        using var db = new SaveDatabase(fixture.Database);
        Assert.Equal(create, new SqliteAccountStore(db).FindCharacter("FromPoB") is not null);
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("x")]
    [InlineData("name_taken")]
    public async Task Preview_checks_the_name_without_writing(string name)
    {
        await using var fixture = await Fixture.Start();
        using (var db = new SaveDatabase(fixture.Database))
        {
            var store = new SqliteAccountStore(db);
            Assert.True(new AccountService(store).CreateCharacter(store.FindAccount("bob_import")!.Id, "Name_Taken", CharacterClass.Witch).Ok);
        }
        byte[] before = File.ReadAllBytes(fixture.Database);
        using var response = await fixture.Send("/api/pob/preview", "alice_import", fixture.Token, name: name);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(fixture.Database));
    }

    [Fact]
    public async Task Preview_refuses_a_full_account_without_writing()
    {
        await using var fixture = await Fixture.Start();
        using (var db = new SaveDatabase(fixture.Database))
        {
            var store = new SqliteAccountStore(db);
            var service = new AccountService(store);
            for (int i = 0; i < service.MaxCharacters; i++)
                Assert.True(service.CreateCharacter(store.FindAccount("alice_import")!.Id, "FullSlot" + i, CharacterClass.Witch).Ok);
        }
        byte[] before = File.ReadAllBytes(fixture.Database);
        using var response = await fixture.Send("/api/pob/preview", "alice_import", fixture.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("character limit", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, File.ReadAllBytes(fixture.Database));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Counts_distinguish_active_and_inactive_weapons_and_omitted_items_and_gems(bool secondSet)
    {
        await using var fixture = await Fixture.Start(realData: true);
        string build = Build.Replace("</PathOfBuilding>", $$"""
            <Items useSecondWeaponSet="{{secondSet.ToString().ToLowerInvariant()}}">
            <Item id="1">Rarity: NORMAL
            Driftwood Wand
            Sockets: B
            Implicits: 0</Item>
            <Item id="2">Rarity: NORMAL
            Driftwood Wand
            Sockets: B
            Implicits: 0</Item>
            <Item id="3">Rarity: NORMAL
            Unavailable Made Up Base
            Implicits: 0</Item>
            <Slot name="Weapon 1" itemId="1"/><Slot name="Weapon 1 Swap" itemId="2"/><Slot name="Helmet" itemId="3"/>
            </Items><Skills>
            <Skill enabled="true" slot="Weapon 1"><Gem nameSpec="Fireball" level="1" quality="0" enabled="true"/></Skill>
            <Skill enabled="true" slot="Weapon 1 Swap"><Gem nameSpec="Fireball" level="1" quality="0" enabled="true"/></Skill>
            <Skill enabled="true" slot="Helmet"><Gem nameSpec="Fireball" level="1" quality="0" enabled="true"/></Skill>
            </Skills></PathOfBuilding>
            """);
        using var response = await fixture.Send("/api/pob/preview", "alice_import", fixture.Token, build);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (string field in new[] { "activeItems", "inactiveWeaponItems", "skippedItems", "activeGems", "inactiveWeaponGems", "skippedGems" })
            Assert.Equal(1, result.GetProperty(field).GetInt32());
        Assert.True(result.GetProperty("partial").GetBoolean());
    }
}
