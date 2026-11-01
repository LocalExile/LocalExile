namespace GameServer.Core.Accounts;

public interface IAccountStore
{
    Account? FindAccount(string name);
    Account? GetAccount(Guid id);
    /// <summary>Every account, by name.</summary>
    IReadOnlyList<Account> Accounts();
    void AddAccount(Account account);
    void UpdateAccount(Account account);
    IReadOnlyList<Character> CharactersOf(Guid accountId);
    Character? FindCharacter(string name);
    Character? GetCharacter(Guid id);
    void AddCharacter(Character character);
    void UpdateCharacter(Character character);
    void DeleteCharacter(Guid id);
}
