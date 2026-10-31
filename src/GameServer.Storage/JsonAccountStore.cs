using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Saves;

namespace GameServer.Storage;

/// <summary>Durable store: whole state in one JSON file (SaveFiles: replaced whole, the previous version kept as .bak).
/// A file that can't be read stops the store (UnreadableSaveException) instead of starting it empty, which the next change
/// would write over every account and character. Pass null path for memory-only.</summary>
public sealed class JsonAccountStore : IAccountStore
{
    private sealed record State(List<Account> Accounts, List<Character> Characters);
    private readonly string? _path;
    private readonly object _gate = new();
    private State _s = new([], []);
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public JsonAccountStore(string? path = null)
    {
        _path = path;
        if (path is not null) _s = SaveFiles.Load<State>(path, Opts) ?? _s;
    }

    private void Save()
    {
        if (_path is not null) SaveFiles.Write(_path, JsonSerializer.Serialize(_s, Opts));
    }

    /// <summary>Every account and character (the import into the save database, <see cref="SqliteAccountStore.ImportJson"/>).</summary>
    public (IReadOnlyList<Account> Accounts, IReadOnlyList<Character> Characters) All() { lock (_gate) return ([.. _s.Accounts], [.. _s.Characters]); }

    public Account? FindAccount(string name) { lock (_gate) return _s.Accounts.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }
    public Account? GetAccount(Guid id) { lock (_gate) return _s.Accounts.FirstOrDefault(a => a.Id == id); }
    public IReadOnlyList<Account> Accounts() { lock (_gate) return [.. _s.Accounts.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)]; }
    public void AddAccount(Account a) { lock (_gate) { _s.Accounts.Add(a); Save(); } }
    public void UpdateAccount(Account a) { lock (_gate) { var i = _s.Accounts.FindIndex(x => x.Id == a.Id); if (i >= 0) { _s.Accounts[i] = a; Save(); } } }
    public IReadOnlyList<Character> CharactersOf(Guid id) { lock (_gate) return _s.Characters.Where(c => c.AccountId == id).OrderBy(c => c.CreatedUtc).ToList(); }
    public Character? FindCharacter(string n) { lock (_gate) return _s.Characters.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); }
    public Character? GetCharacter(Guid id) { lock (_gate) return _s.Characters.FirstOrDefault(c => c.Id == id); }
    public void AddCharacter(Character c) { lock (_gate) { _s.Characters.Add(c); Save(); } }
    public void UpdateCharacter(Character c) { lock (_gate) { var i = _s.Characters.FindIndex(x => x.Id == c.Id); if (i >= 0) { _s.Characters[i] = HardcoreDeathRules.PreserveCommittedDeath(c, _s.Characters[i]); Save(); } } }
    public void DeleteCharacter(Guid id) { lock (_gate) { _s.Characters.RemoveAll(c => c.Id == id); Save(); } }
}
