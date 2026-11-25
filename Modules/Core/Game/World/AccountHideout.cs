using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

public static partial class Game
{
    // Account-wide module data lives under the account's unqualified stash key, independently of league stash contents.
    const string AccountHideoutKey = "game.account-hideout.v1";
    // Display-case items are strictly league-scoped in PersonalStashOf(player) so characters in different leagues
    // (Standard / Hardcore / SSF / Ruthless / Challenge) can never transfer items through a shared hideout.
    const string LeagueDisplayCasesKey = "game.hideout-display-cases.v1";
    public const int MaxHideoutDecorations = 750;
    public sealed record SavedHideoutDecoration(string Key, uint Hash, int X, int Y, float Rotation,
        byte Variation = 0, bool Mirrored = false);
    public sealed record AccountHideoutProfile(string Area, string Founder,
        Dictionary<string, Dictionary<string, HideoutPlacement>> Placements,
        Dictionary<string, List<CharacterSaves.SavedMonsterStatue>> Statues,
        Dictionary<string, List<SavedHideoutDecoration>>? Decorations = null,
        Dictionary<string, ushort>? Music = null,
        Dictionary<string, SavedHideoutDecoration>? MapDevices = null);
    public sealed class HideoutMigrationSettings
    {
        public string? LegacySourceCharacter { get; set; }
    }

    // Auth assigns the login destination before the first Gateway connection. Migrate existing homes when
    // the Game module loads, so that first login also reads the shared selection through its existing fields.
    public static void InitializeAccountHideouts(string? preferredSource = null)
    {
        if (!Saves.IsOpen) return;
        if (!string.IsNullOrWhiteSpace(preferredSource)) InitializeAccountHideout(preferredSource);
        var store = new SqliteAccountStore(Saves.Db);
        foreach (var account in store.Accounts())
            Saves.Atomically(() =>
            {
                if (Saves.LoadStash(account.Name)?.ModuleData?.ContainsKey(AccountHideoutKey) == true) return;
                var source = store.CharactersOf(account.Id)
                    .FirstOrDefault(c => CharacterHome.SelectedHideout(Saves.Db, c.Name) is not null);
                if (source is not null) InitializeAccountHideout(source.Name);
            });
    }

    // The shared profile and Auth compatibility fields must commit or roll back together.
    public static void InitializeAccountHideout(string character) => Saves.Atomically(() =>
    {
        var store = new SqliteAccountStore(Saves.Db);
        if (store.FindCharacter(character) is not { } owner || store.GetAccount(owner.AccountId) is not { } account
            || CharacterSaves.Load(owner.Name) is not { SelectedHideout: not null } save) return;
        var stash = AccountStash.For(account.Name, Items);
        if (stash.ModuleData(AccountHideoutKey) is not null) return;
        stash.ChangeModuleData(AccountHideoutKey, prior => prior ?? JsonSerializer.Serialize(FromLegacyHideout(owner.Name, save)));
        SynchronizeHomeSelection(account.Name, save.SelectedHideout);
        Console.WriteLine($"[hideout] initialized shared account home from {owner.Name}: {save.SelectedHideout}");
    });

    static string? HideoutAccount(Player player, bool instanceOwner)
    {
        string character = instanceOwner ? player.InstanceOwner ?? CharacterName(player) : CharacterName(player);
        if (string.Equals(character, CharacterName(player), StringComparison.OrdinalIgnoreCase)) return player.Character.Account;
        var store = new SqliteAccountStore(Saves.Db);
        return store.FindCharacter(character) is { } owner ? store.GetAccount(owner.AccountId)?.Name : null;
    }

    static AccountHideoutProfile FromLegacyHideout(string character, CharacterSaves.Save save, string? area = null) =>
        new(area ?? save.SelectedHideout ?? HideoutCatalog.DefaultArea, character,
            save.HideoutPlacements is null ? [] : new(save.HideoutPlacements),
            save.MonsterStatues is null ? [] : new(save.MonsterStatues));

    static AccountHideoutProfile? SharedHideout(Player player, bool instanceOwner = false, bool create = false) => Saves.Atomically<AccountHideoutProfile?>(() =>
    {
        if (HideoutAccount(player, instanceOwner) is not { } account) return null;
        var stash = AccountStash.For(account, ItemsFor(player));
        string? text = stash.ModuleData(AccountHideoutKey);
        if (text is null)
        {
            var store = new SqliteAccountStore(Saves.Db);
            var identity = store.FindAccount(account);
            (string Name, CharacterSaves.Save? Save)? first = identity is null ? null : store.CharactersOf(identity.Id)
                .Select(c => (c.Name, Save: CharacterSaves.Load(c.Name)))
                .FirstOrDefault(c => c.Save?.SelectedHideout is not null);
            AccountHideoutProfile? initial = first is { Save: { } legacy }
                ? FromLegacyHideout(first.Value.Name, legacy)
                : create ? FromLegacyHideout(CharacterName(player), CharacterSaves.Load(CharacterName(player)) ?? new([])) : null;
            if (initial is null) return null;
            stash.ChangeModuleData(AccountHideoutKey, prior => prior ?? JsonSerializer.Serialize(initial));
            text = stash.ModuleData(AccountHideoutKey);
            SynchronizeHomeSelection(account, initial.Area);
        }
        var profile = JsonSerializer.Deserialize<AccountHideoutProfile>(text!)
            ?? throw new InvalidDataException("The account hideout profile is empty.");
        if (!instanceOwner && CharacterSaves.Load(CharacterName(player))?.SelectedHideout != profile.Area)
            CharacterSaves.SetSelectedHideout(CharacterName(player), profile.Area);
        return profile;
    });

    static void SynchronizeHomeSelection(string account, string area)
    {
        var store = new SqliteAccountStore(Saves.Db);
        if (store.FindAccount(account) is not { } identity) return;
        // Auth's existing home lookup remains compatible, including logins before a Game connection exists.
        foreach (var character in store.CharactersOf(identity.Id))
            if (CharacterSaves.Load(character.Name)?.SelectedHideout != area)
                CharacterSaves.SetSelectedHideout(character.Name, area);
        foreach (var player in InGame.Keys.Where(c => string.Equals(c.Character.Account, account, StringComparison.OrdinalIgnoreCase)))
            player.Furniture = null;
    }

    static void ChangeSharedHideout(Player player, Func<AccountHideoutProfile, AccountHideoutProfile> change) => Saves.Atomically(() =>
    {
        var profile = SharedHideout(player, create: true);
        if (profile is null || player.Character.Account is not { } account) return;
        AccountStash.For(account, ItemsFor(player)).ChangeModuleData(AccountHideoutKey, text =>
            JsonSerializer.Serialize(change(text is null ? profile : JsonSerializer.Deserialize<AccountHideoutProfile>(text)!)));
        foreach (var peer in InGame.Keys.Where(c => string.Equals(c.Character.Account, account, StringComparison.OrdinalIgnoreCase)))
            peer.Furniture = null;
    });

    public static void SelectAccountHideout(Player player, string area) => Saves.Atomically(() =>
    {
        if (player.Character.Account is null) { CharacterSaves.SetSelectedHideout(CharacterName(player), area); return; }
        ChangeSharedHideout(player, p => p with { Area = area });
        SynchronizeHomeSelection(player.Character.Account, area);
    });

    public static void SetAccountHideoutPlacement(Player player, string area, string key, HideoutPlacement position)
    {
        if (player.Character.Account is null) { CharacterSaves.SetHideoutPlacement(CharacterName(player), area, key, position); return; }
        ChangeSharedHideout(player, p =>
        {
            var areas = new Dictionary<string, Dictionary<string, HideoutPlacement>>(p.Placements);
            var positions = areas.TryGetValue(area, out var old) ? new Dictionary<string, HideoutPlacement>(old) : [];
            positions[key] = position; areas[area] = positions;
            return p with { Placements = areas };
        });
    }

    public static IReadOnlyList<CharacterSaves.SavedMonsterStatue> AccountMonsterStatues(Player player) =>
        SharedHideout(player, instanceOwner: true) is { } shared
            ? shared.Statues.GetValueOrDefault(player.Layout.Id) ?? []
            : CharacterSaves.MonsterStatues(player.InstanceOwner ?? CharacterName(player), player.Layout.Id);

    public static void AddAccountMonsterStatue(Player player, CharacterSaves.SavedMonsterStatue statue)
    {
        if (player.Character.Account is null) { CharacterSaves.AddMonsterStatue(CharacterName(player), player.Layout.Id, statue); return; }
        ChangeSharedHideout(player, p =>
        {
            var areas = new Dictionary<string, List<CharacterSaves.SavedMonsterStatue>>(p.Statues);
            var list = areas.TryGetValue(player.Layout.Id, out var old) ? new List<CharacterSaves.SavedMonsterStatue>(old) : [];
            list.Add(statue); areas[player.Layout.Id] = list;
            return p with { Statues = areas };
        });
    }

    public static IReadOnlyList<SavedHideoutDecoration> AccountHideoutDecorations(Player player) =>
        SharedHideout(player, instanceOwner: true)?.Decorations?.GetValueOrDefault(player.Layout.Id) ?? [];

    public static bool AddAccountHideoutDecoration(Player player, string area, SavedHideoutDecoration decoration)
    {
        if (player.Character.Account is null) return false;
        bool added = false;
        ChangeSharedHideout(player, p =>
        {
            var areas = p.Decorations is null ? new Dictionary<string, List<SavedHideoutDecoration>>() : new(p.Decorations);
            var list = areas.TryGetValue(area, out var old) ? new List<SavedHideoutDecoration>(old) : [];
            if (list.Count >= MaxHideoutDecorations) return p;
            list.Add(decoration);
            areas[area] = list;
            added = true;
            return p with { Decorations = areas };
        });
        return added;
    }

    public static void UpdateAccountHideoutDecoration(Player player, string area, string key, HideoutPlacement position,
        byte variation, bool mirrored)
    {
        if (player.Character.Account is null) return;
        ChangeSharedHideout(player, p =>
        {
            if (p.Decorations is null || !p.Decorations.TryGetValue(area, out var old)) return p;
            int idx = old.FindIndex(d => d.Key == key);
            if (idx < 0) return p;
            var areas = new Dictionary<string, List<SavedHideoutDecoration>>(p.Decorations);
            var list = new List<SavedHideoutDecoration>(old);
            list[idx] = list[idx] with { X = position.X, Y = position.Y, Rotation = position.Rotation, Variation = variation, Mirrored = mirrored };
            areas[area] = list;
            return p with { Decorations = areas };
        });
    }

    public static bool RemoveAccountHideoutDecoration(Player player, string area, string key)
    {
        if (player.Character.Account is null || DisplayCaseHasStoredItem(player, area, key)) return false;
        bool removed = false;
        ChangeSharedHideout(player, p =>
        {
            if (p.Decorations is null || !p.Decorations.TryGetValue(area, out var old)) return p;
            var list = old.Where(d => d.Key != key).ToList();
            if (list.Count == old.Count) return p;
            removed = true;
            var areas = new Dictionary<string, List<SavedHideoutDecoration>>(p.Decorations) { [area] = list };
            return p with { Decorations = areas };
        });
        return removed;
    }

    public static bool ClearAccountHideoutDecorations(Player player, string area, bool resetPlacements)
    {
        if (player.Character.Account is null || HideoutHasStoredDisplayCaseItems(player, area)) return false;
        ChangeSharedHideout(player, p =>
        {
            var decorations = p.Decorations is null ? new Dictionary<string, List<SavedHideoutDecoration>>() : new(p.Decorations);
            decorations.Remove(area);
            var placements = p.Placements;
            var devices = p.MapDevices;
            if (resetPlacements)
            {
                placements = new Dictionary<string, Dictionary<string, HideoutPlacement>>(p.Placements);
                placements.Remove(area);
                devices = devices is null ? null : new(devices);
                devices?.Remove(area);
            }
            return p with { Placements = placements, Decorations = decorations, MapDevices = devices };
        });
        return true;
    }

    public const string MapDeviceKey = "map-device";

    /// <summary>The map device variant placed in this hideout instead of its template's device, or null.</summary>
    public static SavedHideoutDecoration? AccountMapDevice(Player player) =>
        SharedHideout(player, instanceOwner: true)?.MapDevices?.GetValueOrDefault(player.Layout.Id);

    /// <summary>Saves the hideout's map device variant (null: back to the template's) and where the device stands.</summary>
    public static void SetAccountMapDevice(Player player, SavedHideoutDecoration? device, HideoutPlacement? at)
    {
        if (player.Character.Account is null) return;
        ChangeSharedHideout(player, p =>
        {
            var devices = p.MapDevices is null ? new Dictionary<string, SavedHideoutDecoration>() : new(p.MapDevices);
            if (device is null) devices.Remove(player.Layout.Id); else devices[player.Layout.Id] = device;
            var placements = new Dictionary<string, Dictionary<string, HideoutPlacement>>(p.Placements);
            if (at is not null)
                placements[player.Layout.Id] = new(placements.GetValueOrDefault(player.Layout.Id) ?? []) { [MapDeviceKey] = at };
            return p with { MapDevices = devices, Placements = placements };
        });
    }

    public static ushort? AccountHideoutMusic(Player player, string? area = null)
    {
        string key = area ?? player.Layout.Id;
        var map = SharedHideout(player, instanceOwner: true)?.Music;
        return map is not null && map.TryGetValue(key, out ushort row) ? row : null;
    }

    public static void SetAccountHideoutMusic(Player player, string area, ushort musicRow)
    {
        if (player.Character.Account is null) return;
        ChangeSharedHideout(player, p =>
        {
            var music = p.Music is null ? new Dictionary<string, ushort>() : new(p.Music);
            if (musicRow == InstanceHideoutEditing.DefaultMusicRow) music.Remove(area);
            else music[area] = musicRow;
            return p with { Music = music };
        });
    }

    /// <summary>Atomically replaces an area's saved custom decorations, functional placements and optional music row
    /// for C→S 0x0213 template import. Refuses if any Item Display Case in this area holds a stored item.</summary>
    public static bool ReplaceAccountHideoutLayout(Player player, string area,
        IReadOnlyDictionary<string, HideoutPlacement> placements,
        IReadOnlyList<SavedHideoutDecoration> decorations, ushort? musicRow) =>
        ReplaceAccountHideoutLayout(player, area, placements, decorations, musicRow, null);

    public static bool ReplaceAccountHideoutLayout(Player player, string area,
        IReadOnlyDictionary<string, HideoutPlacement> placements,
        IReadOnlyList<SavedHideoutDecoration> decorations, ushort? musicRow, SavedHideoutDecoration? device) => Saves.Atomically(() =>
    {
        if (player.Character.Account is null || HideoutHasStoredDisplayCaseItems(player, area)) return false;
        if (decorations.Count > MaxHideoutDecorations) return false;
        ChangeSharedHideout(player, p =>
        {
            var nextPlacements = new Dictionary<string, Dictionary<string, HideoutPlacement>>(p.Placements);
            if (placements.Count > 0)
            {
                var merged = nextPlacements.TryGetValue(area, out var old)
                    ? new Dictionary<string, HideoutPlacement>(old) : [];
                foreach (var (k, v) in placements) merged[k] = v;
                nextPlacements[area] = merged;
            }
            var nextDecorations = p.Decorations is null
                ? new Dictionary<string, List<SavedHideoutDecoration>>() : new(p.Decorations);
            if (decorations.Count > 0) nextDecorations[area] = [.. decorations];
            else nextDecorations.Remove(area);
            var nextMusic = p.Music is null ? new Dictionary<string, ushort>() : new(p.Music);
            if (musicRow is ushort row)
            {
                if (row == InstanceHideoutEditing.DefaultMusicRow) nextMusic.Remove(area);
                else nextMusic[area] = row;
            }
            var devices = p.MapDevices;
            if (device is not null)
                devices = new Dictionary<string, SavedHideoutDecoration>(devices ?? []) { [area] = device };
            return p with { Placements = nextPlacements, Decorations = nextDecorations, Music = nextMusic, MapDevices = devices };
        });
        return true;
    });

    static Dictionary<string, Dictionary<string, CharacterSaves.SavedItem>>? ReadDisplayCaseMap(AccountStash stash) =>
        stash.ModuleData(LeagueDisplayCasesKey) is { } text
            ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, CharacterSaves.SavedItem>>>(text)
            : null;

    static IEnumerable<AccountStash> AccountLeagueStashes(Player player)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = PersonalStashOf(player);
        string currentKey = AccountStash.Key(player.Character.Account ?? CharacterName(player), player.Character.League);
        seen.Add(currentKey);
        yield return current;
        if (player.Character.Account is not { } account || !Saves.IsOpen) yield break;
        var store = new SqliteAccountStore(Saves.Db);
        if (store.FindAccount(account) is not { } identity) yield break;
        foreach (var ch in store.CharactersOf(identity.Id))
        {
            string key = AccountStash.Key(account, ch.League);
            if (seen.Add(key) && Saves.LoadStash(key) is not null)
                yield return AccountStash.For(key, ItemsFor(player));
        }
    }

    public static bool DisplayCaseHasStoredItem(Player player, string area, string key)
    {
        if (player.Character.Account is null) return false;
        foreach (var stash in AccountLeagueStashes(player))
            if (ReadDisplayCaseMap(stash)?.GetValueOrDefault(area)?.ContainsKey(key) == true) return true;
        return false;
    }

    public static bool HideoutHasStoredDisplayCaseItems(Player player, string area)
    {
        if (player.Character.Account is null) return false;
        foreach (var stash in AccountLeagueStashes(player))
            if (ReadDisplayCaseMap(stash)?.GetValueOrDefault(area) is { Count: > 0 }) return true;
        return false;
    }

    public static CharacterSaves.SavedItem? AccountDisplayCaseItem(Player player, string area, string key) =>
        player.Character.Account is null ? null
            : ReadDisplayCaseMap(PersonalStashOf(player))?.GetValueOrDefault(area)?.GetValueOrDefault(key);

    public static bool SetAccountDisplayCaseItem(Player player, string area, string key, CharacterSaves.SavedItem? item)
    {
        if (player.Character.Account is null) return false;
        return PersonalStashOf(player).ChangeModuleData(LeagueDisplayCasesKey, text =>
        {
            var cases = text is null
                ? new Dictionary<string, Dictionary<string, CharacterSaves.SavedItem>>()
                : JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, CharacterSaves.SavedItem>>>(text) ?? [];
            var areaCases = cases.TryGetValue(area, out var old) ? new Dictionary<string, CharacterSaves.SavedItem>(old) : [];
            if (item is null) areaCases.Remove(key); else areaCases[key] = item;
            if (areaCases.Count == 0) cases.Remove(area); else cases[area] = areaCases;
            return JsonSerializer.Serialize(cases);
        });
    }

    public static (string AreaName, int Placements, int Decorations, int Statues, ushort? Music) HideoutLayoutSummary(Player player)
    {
        var hideout = HideoutCatalog.ByArea(player.Layout.Hash);
        string name = hideout?.Name ?? player.Layout.Id;
        var profile = SharedHideout(player, instanceOwner: true, create: OwnHideout(player));
        int placements = profile?.Placements.GetValueOrDefault(player.Layout.Id)?.Count ?? 0;
        int decorations = profile?.Decorations?.GetValueOrDefault(player.Layout.Id)?.Count ?? 0;
        int statues = profile?.Statues.GetValueOrDefault(player.Layout.Id)?.Count ?? 0;
        ushort? music = profile?.Music is { } m && m.TryGetValue(player.Layout.Id, out ushort row) ? row : null;
        return (name, placements, decorations, statues, music);
    }
}
