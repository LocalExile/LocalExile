using System.Buffers.Binary;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

/// <summary>League challenge sets: which set a league sees, and that their items persist and sync through 0x126.</summary>
public sealed class ChallengeProgressTests
{
    static readonly LeagueRuntime Allflame = new(
        new League("Allflame", "Allflame", false, false) { LaunchCategory = 59 },
        new HashSet<string>(["Game"], StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Challenge_sets_follow_the_league_launch_category()
    {
        Assert.Equal((byte?)79, Game.ChallengeSetFor(59, hardMode: false));
        Assert.Equal((byte?)80, Game.ChallengeSetFor(59, hardMode: true));
        Assert.Null(Game.ChallengeSetFor(60, hardMode: false));

        using var fixture = new MinionUtilityTests.Fixture();
        var player = fixture.Player;
        player.LeagueModule = Allflame;
        player.Character.League = "Allflame";
        Assert.Equal([InstanceAchievements.PermanentAchievementSetId, (byte)79], Game.VisibleAchievementSets(player));

        player.LeagueModule = new LeagueRuntime(
            new League("Standard", "Standard", false, false), new HashSet<string>(["Game"], StringComparer.OrdinalIgnoreCase));
        player.Character.League = "Standard";
        Assert.Equal([InstanceAchievements.PermanentAchievementSetId], Game.VisibleAchievementSets(player));
    }

    [Fact]
    public async Task Challenge_items_persist_and_sync_in_their_set()
    {
        using var fixture = new MinionUtilityTests.Fixture();
        Game.ResetAchievementCache();
        var outbox = new List<byte[]>();
        var player = new Player(fixture.Player.Layout, 1, 1)
        {
            Character =
            {
                Account = "challenge-acct-" + Guid.NewGuid().ToString("N"),
                Name = "Challenge" + Guid.NewGuid().ToString("N")[..8],
                League = "Allflame",
            },
            Instance = new(),
            PlayerLife = 1000,
            Connection =
            {
                PlayerSent = true,
                Outbox = (bytes, _) => { outbox.Add([.. bytes]); return Task.CompletedTask; },
            },
            LeagueModule = Allflame,
        };
        player.Clock.Start();

        // DeepwaterBeginnerChallenge1_1 ("Defeat Hillock") is set 79's first item, bit 0 of its bitset.
        // A Standard character cannot earn the Allflame challenges, so the award is refused before persistence.
        var standard = new Player(fixture.Player.Layout, 1, 1)
        {
            Character = { Account = "standard-challenge-" + Guid.NewGuid().ToString("N"), Name = "Standard" + Guid.NewGuid().ToString("N")[..8], League = "Standard" },
            Instance = new(),
            PlayerLife = 1000,
            Connection = { PlayerSent = true },
            LeagueModule = new LeagueRuntime(new League("Standard", "Standard", false, false), new HashSet<string>(["Game"], StringComparer.OrdinalIgnoreCase)),
        };
        standard.Clock.Start();
        Assert.False(await Game.AwardAchievementItem(standard, "DeepwaterBeginnerChallenge1_1"));

        Assert.True(await Game.AwardAchievementItem(player, "DeepwaterBeginnerChallenge1_1"));
        Assert.False(await Game.AwardAchievementItem(player, "DeepwaterBeginnerChallenge1_1"));
        Assert.Contains("DeepwaterBeginnerChallenge1_1", Game.CompletedAchievementItems(player));
        byte[] progress = Assert.Single(outbox, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceAchievements.AchievementProgressId);
        Assert.Equal((byte)79, progress[2]);
        Assert.Equal(1, progress[7] & 1);

        // The initial set sync covers every visible set: permanent achievements and the league's challenges.
        var enter = Game.AchievementEnterMessages(player).Select(m => m.Bytes).ToList();
        Assert.Contains(enter, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceAchievements.AchievementProgressId && p[2] == 79);
        Assert.Contains(enter, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceAchievements.AchievementProgressId && p[2] == 2);
    }
}
