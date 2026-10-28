using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public partial class PathOfBuildingServerTests
{
    const string Build = """
        <PathOfBuilding><Build level="20" className="Witch" ascendClassName="None"/>
        <Tree activeSpec="1"><Spec nodes="" classId="3" ascendClassId="0" treeVersion="3_29"/></Tree></PathOfBuilding>
        """;

    [Fact]
    public async Task Http_preview_authenticates_without_writing_or_creating_characters()
    {
        await using var fixture = await Fixture.Start();
        byte[] before = File.ReadAllBytes(fixture.Database);
        using var response = await fixture.Send("/api/pob/preview", "alice_import", fixture.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.True(result.GetProperty("preview").GetBoolean());
        Assert.Equal(20, result.GetProperty("level").GetInt32());
        Assert.Equal("Witch", result.GetProperty("class").GetString());
        Assert.Equal(before, File.ReadAllBytes(fixture.Database));
        using var db = new SaveDatabase(fixture.Database);
        Assert.Null(new SqliteAccountStore(db).FindCharacter("FromPoB"));
        Assert.Null(db.Get(SaveDatabase.Table.Characters, "FromPoB"));
    }

    [Fact]
    public async Task Http_creation_uses_configured_database_and_prevents_replacement()
    {
        await using var fixture = await Fixture.Start();
        string cwd = Directory.GetCurrentDirectory();
        using (var response = await fixture.Send("/api/pob/characters", "alice_import", fixture.Token))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var db = new SaveDatabase(fixture.Database);
        var store = new SqliteAccountStore(db);
        var character = store.FindCharacter("FromPoB");
        Assert.NotNull(character);
        Assert.Equal(store.FindAccount("alice_import")!.Id, character.AccountId);
        Assert.Equal(20, character.Level);
        Assert.Equal("1_1_town", character.LastTown);
        var save = JsonSerializer.Deserialize<CharacterSaves.Save>(db.Get(SaveDatabase.Table.Characters, "FromPoB")!);
        Assert.NotNull(save);
        Assert.Equal(123000u, save.Experience);
        Assert.Empty(save.Inventories);
        Assert.Null(save.Tutorial);
        string original = db.Get(SaveDatabase.Table.Characters, "FromPoB")!;
        using var duplicate = await fixture.Send("/api/pob/characters", "alice_import", fixture.Token);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(original, db.Get(SaveDatabase.Table.Characters, "FromPoB"));
        Assert.Equal(cwd, Directory.GetCurrentDirectory());
    }

    [Fact]
    public async Task Tokens_are_account_bound_rotatable_revocable_and_not_stored_as_plaintext()
    {
        await using var fixture = await Fixture.Start();
        using var unauthorized = await fixture.Send("/api/pob/characters", "bob_import", fixture.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        string rotated = PathOfBuildingServerTokens.Issue(fixture.Database, "alice_import");
        using var old = await fixture.Send("/api/pob/preview", "alice_import", fixture.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var valid = await fixture.Send("/api/pob/preview", "alice_import", rotated);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using (var db = new SaveDatabase(fixture.Database))
        {
            var account = new SqliteAccountStore(db).FindAccount("alice_import")!;
            string digest = db.GetMeta("pob_import_token/" + account.Id.ToString("N"))!;
            Assert.Equal(64, digest.Length);
            Assert.NotEqual(rotated, digest);
            Assert.Empty(new SqliteAccountStore(db).CharactersOf(account.Id));
        }
        PathOfBuildingServerTokens.Issue(fixture.Database, "alice_import", revoke: true);
        using var revoked = await fixture.Send("/api/pob/preview", "alice_import", rotated);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Missing_and_wrong_tokens_never_create_characters(string? token)
    {
        await using var fixture = await Fixture.Start();
        using var response = await fixture.Send("/api/pob/characters", "alice_import", token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var db = new SaveDatabase(fixture.Database);
        Assert.Null(new SqliteAccountStore(db).FindCharacter("FromPoB"));
    }

    [Fact]
    public async Task Protocol_rejects_large_malformed_and_path_injection_requests()
    {
        await using var fixture = await Fixture.Start();
        async Task<HttpResponseMessage> Raw(string body, string contentType = "application/json")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/pob/preview")
                { Content = new StringContent(body, Encoding.UTF8, contentType) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token);
            request.Headers.ExpectContinue = true;
            return await fixture.Client.SendAsync(request);
        }
        using var duplicate = await Raw("{\"account\":\"alice_import\",\"Account\":\"bob_import\",\"code\":\"x\"}");
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var folder = await Raw("{\"account\":\"alice_import\",\"code\":\"x\",\"databasePath\":\"someone-else.db\"}");
        Assert.Equal(HttpStatusCode.BadRequest, folder.StatusCode);
        using var malformed = await Raw("{");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var nonjson = await Raw("x", "text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, nonjson.StatusCode);
        using var large = await Raw(new string('A', PathOfBuildingServer.MaximumBodyBytes + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        using var get = await fixture.Client.GetAsync("/api/pob/preview");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using var unknown = await fixture.Client.GetAsync("/unknown");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Ascended_build_previews_without_writes_and_creates_an_unascended_character_with_warnings()
    {
        await using var fixture = await Fixture.Start();
        string build = Build.Replace("ascendClassName=\"None\"", "ascendClassName=\"Elementalist\"")
            .Replace("ascendClassId=\"0\"", "ascendClassId=\"1\"");
        byte[] before = File.ReadAllBytes(fixture.Database);
        using var preview = await fixture.Send("/api/pob/preview", "alice_import", fixture.Token, build);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var prepared = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(prepared.GetProperty("preview").GetBoolean());
        Assert.Contains(prepared.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("Elementalist"));
        Assert.Equal(before, File.ReadAllBytes(fixture.Database));
        using var response = await fixture.Send("/api/pob/characters", "alice_import", fixture.Token, build);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(prepared.GetProperty("warnings").GetRawText(), result.GetProperty("warnings").GetRawText());
        using var db = new SaveDatabase(fixture.Database);
        Assert.NotNull(new SqliteAccountStore(db).FindCharacter("FromPoB"));
        var save = JsonSerializer.Deserialize<CharacterSaves.Save>(db.Get(SaveDatabase.Table.Characters, "FromPoB")!)!;
        Assert.Null(save.MainAscendancy);
    }

    [Fact]
    public async Task Http_save_failure_rolls_back_the_account_character_entry()
    {
        await using var fixture = await Fixture.Start();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.Database};Pooling=False"))
        {
            connection.Open();
            using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER fail_pob_save BEFORE INSERT ON characters BEGIN SELECT RAISE(FAIL, 'intentional test failure'); END";
            trigger.ExecuteNonQuery();
        }
        using var response = await fixture.Send("/api/pob/characters", "alice_import", fixture.Token);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("intentional test failure", body);
        Assert.DoesNotContain(fixture.Database, body);
        string diagnostic = fixture.Diagnostics.ToString();
        Assert.Contains("phase=save", diagnostic);
        Assert.Contains("sqlite=", diagnostic);
        Assert.Contains("reference", body);
        Assert.DoesNotContain(fixture.Token, diagnostic);
        Assert.DoesNotContain("intentional test failure", diagnostic);
        Assert.DoesNotContain(fixture.Database, diagnostic);
        using var db = new SaveDatabase(fixture.Database);
        Assert.Null(new SqliteAccountStore(db).FindCharacter("FromPoB"));
        Assert.Null(db.Get(SaveDatabase.Table.Characters, "FromPoB"));
    }

    [Fact]
    public async Task A_recreated_account_cannot_inherit_the_old_accounts_token()
    {
        await using var fixture = await Fixture.Start();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.Database};Pooling=False"))
        {
            connection.Open();
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM accounts WHERE name='alice_import'";
            delete.ExecuteNonQuery();
        }
        using (var db = new SaveDatabase(fixture.Database))
            Assert.True(new AccountService(new SqliteAccountStore(db)).Register("alice_import", "different-test").Ok);
        using var response = await fixture.Send("/api/pob/characters", "alice_import", fixture.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var check = new SaveDatabase(fixture.Database);
        Assert.Null(new SqliteAccountStore(check).FindCharacter("FromPoB"));
    }

    [Fact]
    public void Issuing_tokens_requires_an_existing_account_and_database()
    {
        string folder = Directory.CreateTempSubdirectory("pob-token-").FullName;
        try
        {
            string file = Path.Combine(folder, "missing.db");
            Assert.Throws<FileNotFoundException>(() => PathOfBuildingServerTokens.Issue(file, "missing"));
            Assert.False(File.Exists(file));
            using (var db = new SaveDatabase(file)) { }
            Assert.Throws<ArgumentException>(() => PathOfBuildingServerTokens.Issue(file, "missing"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    sealed class Fixture : IAsyncDisposable
    {
        public string Folder { get; } = Directory.CreateTempSubdirectory("pob-http-").FullName;
        public string Database => Path.Combine(Folder, "db", "saves.db");
        public PathOfBuildingServer Server { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public string Token { get; private set; } = "";
        public StringWriter Diagnostics { get; } = new();

        public static async Task<Fixture> Start(bool realData = false)
        {
            var fixture = new Fixture();
            string data = Path.Combine(fixture.Folder, "custom-game-data");
            Directory.CreateDirectory(Path.Combine(data, "tables"));
            void Write(string table, object rows) => File.WriteAllText(Path.Combine(data, "tables", table + ".json"), JsonSerializer.Serialize(rows));
            Write("Characters", new[] { new { IntegerId = 3 } });
            Write("ExperienceLevels", new[] { new { Level = 20, Experience = 123000 } });
            Write("PassiveSkills", Array.Empty<object>());
            Write("BaseItemTypes", Array.Empty<object>());
            Write("ItemVisualIdentity", Array.Empty<object>());
            File.WriteAllText(Path.Combine(data, "item-types.json"), "{\"types\":[]}");
            using (var db = new SaveDatabase(fixture.Database))
            {
                var accounts = new AccountService(new SqliteAccountStore(db));
                Assert.True(accounts.Register("alice_import", "local-test").Ok);
                Assert.True(accounts.Register("bob_import", "local-test").Ok);
            }
            fixture.Token = PathOfBuildingServerTokens.Issue(fixture.Database, "alice_import");
            fixture.Server = new(new PathOfBuildingServerConfig { ListenUrl = "http://127.0.0.1:0", RepositoryRoot = fixture.Folder,
                DataFolder = realData ? "data" : data, DatabasePath = fixture.Database, ClientGgpk = Path.Combine(fixture.Folder, "absent.ggpk"),
                OozWorker = Path.Combine(fixture.Folder, "absent.mjs") }, fixture.Diagnostics);
            try
            {
                await fixture.Server.StartAsync();
                fixture.Client = new HttpClient { BaseAddress = new Uri(fixture.Server.Address), Timeout = TimeSpan.FromSeconds(30) };
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public Task<HttpResponseMessage> Send(string path, string account, string? token, string code = Build, string name = "FromPoB")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path)
                { Content = JsonContent.Create(new { account, code, name, league = "Standard" }) };
            if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (Server is not null) await Server.DisposeAsync();
            Directory.Delete(Folder, recursive: true);
        }
    }
}
