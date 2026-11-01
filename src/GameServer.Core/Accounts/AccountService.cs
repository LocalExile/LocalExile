using System.Security.Cryptography;
using System.Text;

namespace GameServer.Core.Accounts;

public enum AuthError { None, NameTaken, InvalidName, WeakPassword, BadCredentials, CharacterNameTaken, InvalidCharacterName, LimitReached, NotFound, SessionInvalid, InvalidClass, InvalidLevel }

public readonly record struct Result<T>(T? Value, AuthError Error)
{
    public bool Ok => Error == AuthError.None;
    public static Result<T> Success(T v) => new(v, AuthError.None);
    public static Result<T> Fail(AuthError e) => new(default, e);
}

/// <summary>Registration, login (session tokens) and character management. Transport-agnostic.</summary>
public sealed class AccountService(IAccountStore store, TimeProvider? clock = null, int maxCharacters = 24, string league = "Standard")
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly object _gate = new();

    // The Twilight Strand: new characters wash up there and reach Lioneye's Watch once Hillock is dead (a1q1).
    public static string StartingArea => "1_1_1";

    /// <summary>How many characters an account may have.</summary>
    public int MaxCharacters => maxCharacters;

    public Result<Account> Register(string name, string password)
    {
        if (!ValidName(name, 3, 32)) return Result<Account>.Fail(AuthError.InvalidName);
        if (password is null || password.Length < 4) return Result<Account>.Fail(AuthError.WeakPassword);
        lock (_gate)
        {
            if (store.FindAccount(name) is not null) return Result<Account>.Fail(AuthError.NameTaken);
            var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var acc = new Account(Guid.NewGuid(), name, Hash(password, salt), salt, _clock.GetUtcNow(),
                DigestHash(ClientPasswordDigest(password), salt));
            store.AddAccount(acc);
            return Result<Account>.Success(acc);
        }
    }

    /// <summary>Sets an account's password (dev tool: no minimum length, so a local test account can use any text). Both
    /// stored hashes change with a new salt: the one for <see cref="Login"/> and the one for the official client's digest.</summary>
    public Result<Account> SetPassword(string name, string password)
    {
        if (password is null || password.Length == 0) return Result<Account>.Fail(AuthError.WeakPassword);
        lock (_gate)
        {
            if (store.FindAccount(name) is not { } acc) return Result<Account>.Fail(AuthError.NotFound);
            var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var changed = acc with
            {
                PasswordHash = Hash(password, salt), PasswordSalt = salt,
                ClientDigestHash = DigestHash(ClientPasswordDigest(password), salt),
            };
            store.UpdateAccount(changed);
            return Result<Account>.Success(changed);
        }
    }

    public Result<Session> Login(string name, string password)
    {
        var acc = store.FindAccount(name);
        if (acc is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(acc.PasswordHash), Encoding.UTF8.GetBytes(Hash(password ?? "", acc.PasswordSalt))))
            return Result<Session>.Fail(AuthError.BadCredentials);
        if (acc.ClientDigestHash is null)
            store.UpdateAccount(acc with { ClientDigestHash = DigestHash(ClientPasswordDigest(password!), acc.PasswordSalt) });
        return Result<Session>.Success(NewSession(acc));
    }

    /// <summary>Login as the official client does it: it sends <see cref="ClientPasswordDigest"/>, never the password.</summary>
    public Result<Session> LoginWithClientDigest(string name, ReadOnlySpan<byte> digest)
    {
        var acc = store.FindAccount(name);
        if (acc is null) return Result<Session>.Fail(AuthError.NotFound);
        if (acc.ClientDigestHash is null || digest.Length != 32 || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(acc.ClientDigestHash), Encoding.UTF8.GetBytes(DigestHash(digest, acc.PasswordSalt))))
            return Result<Session>.Fail(AuthError.BadCredentials);
        return Result<Session>.Success(NewSession(acc));
    }

    /// <summary>What the official client sends for a password: SHA-256 over its UTF-16LE text (client 0x140d05f70 → 0x140e23290).</summary>
    public static byte[] ClientPasswordDigest(string password) => SHA256.HashData(Encoding.Unicode.GetBytes(password));

    /// <summary>Persistent device-bound credential for the client's Remember Password path.
    /// Derived from the stored password verifier: survives restart, changes when the password changes.</summary>
    public byte[] SavedClientCredential(Guid accountId, ReadOnlySpan<byte> machineA, ReadOnlySpan<byte> machineB)
    {
        var acc = store.GetAccount(accountId) ?? throw new InvalidOperationException("Account no longer exists");
        if (machineA.Length != 32 || machineB.Length != 32) throw new ArgumentException("Expected 32-byte machine identifiers");
        byte[] binding = [.. Encoding.UTF8.GetBytes("LocalExile saved login v1"), .. acc.Id.ToByteArray(), .. machineA, .. machineB];
        return HMACSHA256.HashData(Convert.FromBase64String(acc.PasswordHash), binding);
    }

    public Result<Session> LoginWithSavedClientCredential(string name, ReadOnlySpan<byte> credential,
        ReadOnlySpan<byte> machineA, ReadOnlySpan<byte> machineB)
    {
        var acc = store.FindAccount(name);
        if (acc is null) return Result<Session>.Fail(AuthError.NotFound);
        if (credential.Length != 32 || machineA.Length != 32 || machineB.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(credential, SavedClientCredential(acc.Id, machineA, machineB)))
            return Result<Session>.Fail(AuthError.BadCredentials);
        return Result<Session>.Success(NewSession(acc));
    }

    private Session NewSession(Account acc)
    {
        var now = _clock.GetUtcNow();
        var s = new Session(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), acc.Id, now, now.AddHours(12));
        lock (_gate) _sessions[s.Token] = s;
        return s;
    }

    public Result<Session> Resume(string token)
    {
        lock (_gate)
        {
            if (token is not null && _sessions.TryGetValue(token, out var s) && s.ExpiresUtc > _clock.GetUtcNow())
                return Result<Session>.Success(s);
        }
        return Result<Session>.Fail(AuthError.SessionInvalid);
    }

    public Account? GetAccount(Guid accountId) => store.GetAccount(accountId);

    public IReadOnlyList<Character> Characters(Guid accountId) => store.CharactersOf(accountId);

    /// <summary>Every account with its characters (the dev protocol's "accounts").</summary>
    public IReadOnlyList<(Account Account, IReadOnlyList<Character> Characters)> AccountsWithCharacters() =>
        [.. store.Accounts().Select(a => (a, store.CharactersOf(a.Id)))];

    public Character? FindCharacter(string name) => store.FindCharacter(name);

    /// <summary>Creates a level 1 character in <paramref name="inLeague"/> (default: the service's league).</summary>
    public Result<Character> CreateCharacter(Guid accountId, string name, CharacterClass cls, string? inLeague = null)
    {
        if (!ValidName(name, 3, 23)) return Result<Character>.Fail(AuthError.InvalidCharacterName);
        if (!Enum.IsDefined(cls)) return Result<Character>.Fail(AuthError.InvalidClass);
        lock (_gate)
        {
            if (store.GetAccount(accountId) is null) return Result<Character>.Fail(AuthError.NotFound);
            if (store.CharactersOf(accountId).Count >= maxCharacters) return Result<Character>.Fail(AuthError.LimitReached);
            if (store.FindCharacter(name) is not null) return Result<Character>.Fail(AuthError.CharacterNameTaken);
            var now = _clock.GetUtcNow();
            var c = new Character(Guid.NewGuid(), accountId, name, cls, 1, inLeague ?? league, StartingArea, now, now);
            store.AddCharacter(c);
            return Result<Character>.Success(c);
        }
    }

    public Result<bool> DeleteCharacter(Guid accountId, Guid characterId)
    {
        var c = store.GetCharacter(characterId);
        if (c is null || c.AccountId != accountId) return Result<bool>.Fail(AuthError.NotFound);
        store.DeleteCharacter(characterId);
        return Result<bool>.Success(true);
    }

    /// <summary>Sets a character's level (1 to 100), found by name.</summary>
    public Result<Character> SetLevel(string characterName, int level)
    {
        if (level is < 1 or > 100) return Result<Character>.Fail(AuthError.InvalidLevel);
        // Read and write under one lock: SetLastTown changes the same record and must not undo this change.
        lock (_gate)
        {
            var c = store.FindCharacter(characterName);
            if (c is null) return Result<Character>.Fail(AuthError.NotFound);
            var u = c with { Level = level };
            store.UpdateCharacter(u);
            return Result<Character>.Success(u);
        }
    }

    /// <summary>Records the character's worn portrait variation (0 = the class's own), found by name.</summary>
    public Result<Character> SetPortrait(string characterName, ushort portrait)
    {
        lock (_gate)
        {
            var c = store.FindCharacter(characterName);
            if (c is null) return Result<Character>.Fail(AuthError.NotFound);
            if (c.Portrait == portrait) return Result<Character>.Success(c);
            var u = c with { Portrait = portrait };
            store.UpdateCharacter(u);
            return Result<Character>.Success(u);
        }
    }

    /// <summary>Records the character's worn portrait frame (0 = the default), found by name.</summary>
    public Result<Character> SetPortraitFrame(string characterName, ushort frame)
    {
        lock (_gate)
        {
            var c = store.FindCharacter(characterName);
            if (c is null) return Result<Character>.Fail(AuthError.NotFound);
            if (c.PortraitFrame == frame) return Result<Character>.Success(c);
            var u = c with { PortraitFrame = frame };
            store.UpdateCharacter(u);
            return Result<Character>.Success(u);
        }
    }

    /// <summary>Records the last town a character entered (a WorldAreas Id), found by name.</summary>
    public Result<Character> SetLastTown(string characterName, string townId)
    {
        lock (_gate)
        {
            var c = store.FindCharacter(characterName);
            if (c is null) return Result<Character>.Fail(AuthError.NotFound);
            if (c.LastTown == townId) return Result<Character>.Success(c);
            var u = c with { LastTown = townId };
            store.UpdateCharacter(u);
            return Result<Character>.Success(u);
        }
    }

    public Result<Character> Touch(Guid accountId, Guid characterId, string areaId)
    {
        var c = store.GetCharacter(characterId);
        if (c is null || c.AccountId != accountId) return Result<Character>.Fail(AuthError.NotFound);
        var u = c with { AreaId = areaId, LastPlayedUtc = _clock.GetUtcNow() };
        store.UpdateCharacter(u);
        return Result<Character>.Success(u);
    }

    private static bool ValidName(string? n, int min, int max) =>
        n is not null && n.Length >= min && n.Length <= max && n.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-');

    private static string DigestHash(ReadOnlySpan<byte> digest, string salt) => Hash(Convert.ToHexString(digest), salt);

    private static string Hash(string pw, string salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(pw, Convert.FromBase64String(salt), 50_000, HashAlgorithmName.SHA256, 32));
}
