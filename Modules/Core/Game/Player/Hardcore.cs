using GameServer.Core.Accounts;

public static partial class Game
{
    /// <summary>Left in the character's saved data from the moment of a Hardcore death until it has moved, so that a crash in between
    /// cannot bring the character back.</summary>
    public const string HardcoreDeathMark = "hardcore.dead";

    // Called synchronously inside the lethal damage mutation, before its first network write. The player sees the death screen and
    // stays connected; the character (only: stashes stay) moves to the parent league once they have left (CommitHardcoreDeath).
    public static void RecordHardcoreDeath(Player player)
    {
        if (!player.Character.Hardcore || player.PlayerLife != 0 || player.Character.HardcoreDeathRecorded) return;
        string name = CharacterName(player);
        ItemsOf(player).SaveUnsaved();
        // Loading the save includes the pending XP total, so this saves it without consuming its retry buffer.
        CharacterSaves.ClearActiveAuras(name);
        CharacterSaves.ChangeModuleData(name, HardcoreDeathMark, _ => "1");
        player.Character.HardcoreDeathRecorded = true;
        player.PendingTransfer = null;
        Console.WriteLine($"[gateway] {name}: Hardcore death in {player.Character.League}: moves to the parent league when the player leaves");
    }

    // The connection has ended (the death screen's Exit, or any other way out).
    public static void CommitHardcoreDeath(Player player)
    {
        if (!player.Character.HardcoreDeathRecorded) return;
        string name = CharacterName(player);
        var source = LeagueCatalog.Startup.ResolveSavedLeague(player.Character.League)
            ?? throw new InvalidOperationException("Cannot resolve the dying character's Hardcore league");
        // A league's rules can make the death permanent (Ruthless's hardcoreDeathToVoid setting).
        if (LeagueFor(player).Rules.HardcoreDeathToVoid) source = source with { PermanentDeath = true };
        var dead = HardcoreDeathPersistence.Commit(Saves.Db, name, source, () => ItemsOf(player).SaveUnsaved(), expectedCharacterId: player.Character.Id);
        CharacterSaves.ChangeModuleData(name, HardcoreDeathMark, _ => "");
        Console.WriteLine($"[gateway] {name}: Hardcore death committed: {source.Name} → {dead.League}" +
            (dead.HardcoreDeath?.PermanentlyDead == true ? " (permanently dead)" : " (carried items retained)"));
    }

    // A connection of a character that died and whose move a crash interrupted: it moves now, and this connection does not go on.
    public static void RefuseInterruptedHardcoreDeath(Player player)
    {
        if (!player.Character.Hardcore || CharacterSaves.ModuleData(CharacterName(player), HardcoreDeathMark) is not { Length: > 0 }) return;
        player.Character.HardcoreDeathRecorded = true;
        CommitHardcoreDeath(player);
        throw new InvalidOperationException($"{CharacterName(player)} died in {player.Character.League}: moved to the parent league");
    }
}
