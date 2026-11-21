using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using Progression = GameServer.Core.World.Progression;
using Rarity = GameServer.Core.Protocol.Instance.MonsterRarity;

public static partial class Game
{
    /// <summary>
    /// One kill's two experience values: the character's (with the level penalty) and the gems' base before that penalty
    /// (they gain 10% of it in <see cref="CommitGemExperience"/>). "Socketed gems gain 10% of the experience your
    /// character earns, before any level penalties"; items that increase experience gain reach both.
    /// </summary>
    public static (uint Character, uint Gems) KillExperience(IPlayer player, MonsterInfo monster, int baseExperience,
        int multiplierPercent, Rarity rarity, int playerLevel, int monsterLevel, double partyMultiplier = 1)
    {
        uint character = Progression.MonsterExperience(baseExperience, multiplierPercent, rarity, playerLevel, monsterLevel,
            partyMultiplier: partyMultiplier);
        uint beforePenalty = Progression.MonsterExperience(baseExperience, multiplierPercent, rarity, playerLevel,
            monsterLevel, levelPenalty: false, partyMultiplier: partyMultiplier);
        return (GameEvents.RunExperience(PlayerOf(player), monster, character),
            GameEvents.RunExperience(PlayerOf(player), monster, beforePenalty));
    }

    public sealed class CharacterLevelUpdates
    {
        public readonly Lock Gate = new();
        public readonly SemaphoreSlim Auth = new(1, 1);
        public long Revision;
    }

    public static readonly ConcurrentDictionary<string, CharacterLevelUpdates> LevelUpdates = new(StringComparer.OrdinalIgnoreCase);
    public static CharacterLevelUpdates LevelUpdatesFor(Player player) => LevelUpdates.GetOrAdd(CharacterName(player), _ => new());

    public sealed record ExperienceChange(long Revision, uint Before, uint After, bool LevelChanged, bool Reset = false);

    // Call while holding the connection lock; an accepted death must not gain XP or publish a later level-up heal.
    public static bool HardcoreProgressBlocked(Player player) =>
        player.Character.Hardcore && (player.PlayerLife == 0 || player.Character.HardcoreDeathRecorded);

    // All production gains/resets serialize the shared XP ledger before publishing a connection copy.
    // Lock order is character XP -> connection -> database. No monitor is held across network waits.
    public static ExperienceChange? RecordExperienceGain(Player player, uint gain)
    {
        var updates = LevelUpdatesFor(player);
        lock (updates.Gate)
        {
            if (HardcoreProgressBlocked(player)) return null;
            var rules = LeagueFor(player).Rules;
            var (before, after) = CharacterSaves.AddExperience(CharacterName(player), player.Character.Experience,
                // An older area may have stale pools. Scale against the shared ledger's current level, not that copy.
                xp => Progression.AddExperience(xp, LeagueRules.Scale(gain,
                    rules.ExperienceMultiplier(Progression.LevelFor(xp, CharacterStats.LevelStarts))), CharacterStats.LevelStarts));
            if (after == before) return null;
            bool levelChanged = Progression.LevelFor(after, CharacterStats.LevelStarts) != player.Pools.Level;
            if (levelChanged) CharacterSaves.SaveExperience(CharacterName(player));
            player.Character.Experience = after;
            return new(++updates.Revision, before, after, levelChanged);
        }
    }

    public static uint CurrentExperience(Player player) =>
        CharacterSaves.Experience(CharacterName(player)) ?? player.Character.Experience;

    public static async Task<bool> PublishExperienceChange(Player player, ExperienceChange change, string why)
    {
        var updates = LevelUpdatesFor(player);
        await Send(player, [], $"0x02bc current player experience ({why})", atGate: () =>
        {
            lock (updates.Gate)
            {
                if (HardcoreProgressBlocked(player)) return null;
                player.Character.Experience = CurrentExperience(player);
                return InstanceEntities.PlayerExperienceUpdate(player.Id, player.Character.Experience);
            }
        });
        int before, level;
        lock (updates.Gate)
        {
            if (HardcoreProgressBlocked(player)) return false;
            // Old-area gains share XP, not runtime pools. Even an obsolete event must reconcile this connection
            // from current XP; rejecting it outright could leave the live connection at an older level.
            player.Character.Experience = CurrentExperience(player);
            level = Progression.LevelFor(player.Character.Experience, CharacterStats.LevelStarts);
            bool currentReset = change.Reset && change.Revision == updates.Revision;
            if (!currentReset && level == player.Pools.Level) return false;
            before = CommitExperienceLevel(player, level);
        }
        await PublishLevelChange(player, before, level);
        return true;
    }

    public static async Task CompleteExperienceGain(Player player, ExperienceChange change, string why, Func<ControlRequest, Task<ControlResponse>>? control = null)
    {
        bool applied = await PublishExperienceChange(player, change, why);
        if (!change.LevelChanged && !applied) return;
        if (HardcoreProgressBlocked(player)) return;
        var result = await SynchronizeAccountLevel(player, control);
        if (!result.Ok) Console.WriteLine($"[gateway] {player.Connection.Remote} current level not saved to the account: {result.Error}");
    }

    // Auth has unconditional set_level writes. Serialize every Gateway producer for this character, including resets
    // and login repair, and correct an in-flight request if XP changed before its answer. Answers never apply a level.
    public static async Task<ControlResponse> SynchronizeAccountLevel(Player player,
        Func<ControlRequest, Task<ControlResponse>>? control = null)
    {
        var updates = LevelUpdatesFor(player);
        await updates.Auth.WaitAsync();
        try
        {
            while (true)
            {
                int level;
                lock (updates.Gate)
                {
                    CharacterSaves.SaveExperience(CharacterName(player));
                    level = Progression.LevelFor(CurrentExperience(player), CharacterStats.LevelStarts);
                }
                var request = new ControlRequest { Op = "set_level", Ticket = player.Ticket, Level = level };
                var result = await (control is null ? InstanceControl.CallAsync(request) : control(request));
                lock (updates.Gate)
                if (level == Progression.LevelFor(CurrentExperience(player), CharacterStats.LevelStarts)) return result;
            }
        }
        finally { updates.Auth.Release(); }
    }
}
