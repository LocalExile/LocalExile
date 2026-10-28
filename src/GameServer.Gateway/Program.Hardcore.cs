using GameServer.Core.Accounts;

partial class Program
{
    // Called synchronously inside the lethal damage mutation, before its first network write. A disconnect or a failed
    // socket write therefore cannot skip the death. The account record is separate from normal progress snapshots.
    internal static void RecordHardcoreDeath(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!conn.Hardcore || conn.PlayerLife != 0 || conn.HardcoreDeathRecorded) return;
            string name = CharacterName(conn);
            var source = LeagueCatalog.FromEnvironment().ResolveSavedLeague(conn.League)
                ?? throw new InvalidOperationException("Cannot resolve the dying character's Hardcore league");
            var dead = HardcoreDeathPersistence.Commit(Saves.Db, name, source, () =>
            {
                ItemsOf(conn).SaveUnsaved();
                // Loading the save includes the pending XP total, so this saves it without consuming its retry buffer.
                CharacterSaves.ClearActiveAuras(name);
            }, expectedCharacterId: conn.CharacterId);
            conn.HardcoreDeathRecorded = true;
            conn.PendingTransfer = null;
            conn.ProjectileEpoch++;
            Console.WriteLine($"[gateway] {name}: Hardcore death committed: {source.Name} → {dead.League}" +
                (dead.HardcoreDeath?.PermanentlyDead == true ? " (permanently dead)" : " (carried items retained)"));
        }
    }
}
