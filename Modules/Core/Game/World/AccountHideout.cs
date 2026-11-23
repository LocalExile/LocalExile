using System.Text.Json;
using GameServer.Storage;

public static partial class Game
{
    // Account-wide module data lives under the account's unqualified stash key, independently of league stash contents.
    const string AccountHideoutKey = "game.account-hideout.v1";
    public sealed record AccountHideoutProfile(string Area, string Founder,
        Dictionary<string, Dictionary<string, HideoutPlacement>> Placements,
        Dictionary<string, List<CharacterSaves.SavedMonsterStatue>> Statues);
    public sealed class HideoutMigrationSettings
    {
        public string? LegacySourceCharacter { get; set; }
    }

    // Auth assigns the login destination before the first Gateway connection. Migrate existing homes when
    // the Game module loads, so that first login also reads the shared selection through its existing fields.
    public static void InitializeAccountHideouts(string? preferredSource = null)
    {
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
}
