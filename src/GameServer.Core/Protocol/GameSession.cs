using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Accounts;
using GameServer.Core.World;

namespace GameServer.Core.Protocol;

/// <summary>
/// Dev protocol (our own, not the official client's): one JSON object per line, request {"op":..., ...} -> reply {"ok":bool,...}.
/// ops: register, login, resume, characters, create_character, delete_character, enter_world, enter_area, who.
/// One instance per connection; holds that connection's login/world state.
/// </summary>
public sealed class GameSession(AccountService accounts, InstanceManager instances)
{
    private Session? _session;
    private Character? _active;

    public Guid? ActiveCharacterId => _active?.Id;

    /// <summary>Call when the connection drops so the character leaves its instance.</summary>
    public void Disconnect()
    {
        if (_active is not null) instances.Leave(_active.Id);
        _active = null;
    }

    public string Handle(string line)
    {
        JsonObject req;
        try { req = JsonNode.Parse(line)?.AsObject() ?? throw new JsonException(); }
        catch (JsonException) { return Err("bad_request"); }

        string op = req["op"]?.GetValue<string>() ?? "";
        try
        {
            return op switch
            {
                "register" => Register(req),
                "login" => Login(req),
                "resume" => Resume(req),
                "characters" => Characters(),
                "accounts" => Accounts(),
                "create_character" => CreateCharacter(req),
                "delete_character" => DeleteCharacter(req),
                "enter_world" => EnterWorld(req),
                "enter_area" => EnterArea(req),
                "who" => Who(),
                _ => Err("unknown_op"),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            return Err("bad_request");
        }
    }

    private string Register(JsonObject r)
    {
        var res = accounts.Register(Str(r, "name"), Str(r, "password"));
        return res.Ok ? Ok(("account", res.Value!.Name)) : Err(res.Error);
    }

    private string Login(JsonObject r)
    {
        var res = accounts.Login(Str(r, "name"), Str(r, "password"));
        if (!res.Ok) return Err(res.Error);
        Disconnect();
        _session = res.Value;
        return Ok(("token", res.Value!.Token));
    }

    private string Resume(JsonObject r)
    {
        var res = accounts.Resume(Str(r, "token"));
        if (!res.Ok) return Err(res.Error);
        Disconnect();
        _session = res.Value;
        return Ok(("token", res.Value!.Token));
    }

    private string Characters()
    {
        if (_session is null) return Err("not_logged_in");
        var arr = new JsonArray();
        foreach (var c in accounts.Characters(_session.AccountId)) arr.Add(Describe(c));
        return Ok(("characters", arr));
    }

    private string CreateCharacter(JsonObject r)
    {
        if (_session is null) return Err("not_logged_in");
        if (!Enum.TryParse<CharacterClass>(Str(r, "class"), true, out var cls)) return Err("bad_class");
        var res = accounts.CreateCharacter(_session.AccountId, Str(r, "name"), cls);
        return res.Ok ? Ok(("character", Describe(res.Value!))) : Err(res.Error);
    }

    private string DeleteCharacter(JsonObject r)
    {
        if (_session is null) return Err("not_logged_in");
        var chars = accounts.Characters(_session.AccountId);
        var c = chars.FirstOrDefault(x => x.Name.Equals(Str(r, "name"), StringComparison.OrdinalIgnoreCase));
        if (c is null) return Err(AuthError.NotFound);
        if (_active?.Id == c.Id) Disconnect();
        var res = accounts.DeleteCharacter(_session.AccountId, c.Id);
        return res.Ok ? Ok() : Err(res.Error);
    }

    private string EnterWorld(JsonObject r)
    {
        if (_session is null) return Err("not_logged_in");
        var c = accounts.Characters(_session.AccountId)
            .FirstOrDefault(x => x.Name.Equals(Str(r, "name"), StringComparison.OrdinalIgnoreCase));
        if (c is null) return Err(AuthError.NotFound);
        c = accounts.FindCharacter(c.Name);
        if (c is null || c.AccountId != _session.AccountId) return Err(AuthError.NotFound);
        if (!HardcoreDeathRules.CanEnter(c)) return Err("character_permanently_dead");
        Disconnect();
        _active = c;
        var inst = instances.Enter(c.Id, c.AreaId, league: c.League);
        return Ok(("instance", Describe(inst)));
    }

    private string EnterArea(JsonObject r)
    {
        if (_active is null || _session is null) return Err("not_in_world");
        if (accounts.FindCharacter(_active.Name) is not { } current || current.Id != _active.Id
            || current.AccountId != _session.AccountId || !HardcoreDeathRules.CanEnter(current)
            || !string.Equals(current.League, _active.League, StringComparison.OrdinalIgnoreCase))
        {
            Disconnect();
            return Err("not_in_world");
        }
        _active = current;
        string area = Str(r, "area");
        if (area.Length is 0 or > 64) return Err("bad_area");
        var inst = instances.Enter(_active.Id, area, r["monster_level"]?.GetValue<int>() ?? 1, _active.League);
        var upd = accounts.Touch(_session.AccountId, _active.Id, area);
        if (upd.Ok) _active = upd.Value;
        return Ok(("instance", Describe(inst)));
    }

    private string Who()
    {
        if (_active is null) return Err("not_in_world");
        var inst = instances.Current(_active.Id);
        return inst is null ? Err("not_in_world")
            : Ok(("character", _active.Name), ("instance", Describe(inst)));
    }

    /// <summary>Every account and its characters, without logging in: a local dev tool's overview (the desktop launcher's
    /// account list). Names, dates and character summaries only; no hashes, tokens or saves.</summary>
    private string Accounts()
    {
        var list = new JsonArray();
        foreach (var (account, characters) in accounts.AccountsWithCharacters())
        {
            var chars = new JsonArray();
            foreach (var c in characters)
            {
                var d = Describe(c);
                d["last_town"] = c.LastTown;
                d["last_played"] = c.LastPlayedUtc.ToString("O");
                d["dead"] = c.HardcoreDeath is { PermanentlyDead: true } ? "permanent" : c.HardcoreDeath?.SourceLeague;
                chars.Add(d);
            }
            list.Add(new JsonObject { ["name"] = account.Name, ["created"] = account.CreatedUtc.ToString("O"), ["characters"] = chars });
        }
        return Ok(("accounts", list));
    }

    private static JsonObject Describe(Character c) => new()
    {
        ["name"] = c.Name, ["class"] = c.Class.ToString(), ["level"] = c.Level,
        ["league"] = c.League, ["area"] = c.AreaId,
    };

    private static JsonObject Describe(AreaInstance i) => new()
    {
        ["id"] = i.Id, ["area"] = i.AreaId, ["seed"] = i.Seed, ["monster_level"] = i.MonsterLevel,
        ["players"] = i.Players.Count,
    };

    private static string Str(JsonObject r, string key) => r[key]?.GetValue<string>() ?? "";

    private static string Ok(params (string, JsonNode?)[] fields)
    {
        var o = new JsonObject { ["ok"] = true };
        foreach (var (k, v) in fields) o[k] = v;
        return o.ToJsonString();
    }

    private static string Err(AuthError e) => Err(e.ToString());
    private static string Err(string code) => new JsonObject { ["ok"] = false, ["error"] = code }.ToJsonString();
}
