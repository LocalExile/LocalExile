using GameServer.Core.World;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// The corpse-consumption and Hinder tattoo rows of issue #372: their client stats, the spell-only Hinder roll with the
// client's four seconds and the additive Maim/Hinder walk reduction, and the killing blow's corpse consumption (which
// recovers 10% of maximum life).
public sealed class TattooCorpseHinderTests
{
    static readonly (string Name, string Stat, int Value)[] Rows =
    [
        ("Tattoo of the Kitava Heart Eater", "killing_blow_consumes_corpse_restore_10%_life_chance_%", 4),
        ("Tattoo of the Tasalio Shaman", "spells_chance_to_hinder_on_hit_%", 5),
    ];

    [Fact]
    public void Every_corpse_and_hinder_tattoo_maps_its_client_stats()
    {
        foreach (var (name, stat, value) in Rows)
        {
            var stats = TattooStats.For(name);
            Assert.NotNull(stats);
            Assert.Contains((Program.Stats.Key(stat)!.Value, value), stats);
        }
    }

    [Fact]
    public void The_hinder_roll_and_the_killing_blow_corpse_consumption_read_their_stats()
    {
        // Hinder rolls on spell hits only and lasts the client's ReminderTextHinder four seconds.
        Func<string, int> get = name => name == "spells_chance_to_hinder_on_hit_%" ? 100 : 0;
        var spell = new SpawnedMonster("hinder", new(105, 100, 0), 0, 0, 100);
        Game.HitDebuffsOnHitLocked(spell, get, spell: true, critical: false, now: 1);
        Assert.Equal(5, spell.HinderedUntil, 6);
        Assert.Equal(0.7, Game.TimedDebuffWalkFactor(spell, 1, 1), 6);
        var attack = new SpawnedMonster("attack", new(105, 100, 0), 0, 0, 100);
        Game.HitDebuffsOnHitLocked(attack, get, spell: false, critical: false, now: 1);
        Assert.Equal(double.NegativeInfinity, attack.HinderedUntil);
        // Maim and Hinder are both "reduced movement speed": together they take 30% + 30% (0.4), not 0.7 x 0.7 (0.49).
        var both = new SpawnedMonster("both", new(105, 100, 0), 0, 0, 100)
        {
            MaimedUntil = 10, HinderedUntil = 10,
        };
        Assert.Equal(0.4, Game.TimedDebuffWalkFactor(both, 1, 1), 6);
        Assert.True(Game.TimedWalkDebuff(1, both));
        Assert.False(Game.TimedWalkDebuff(11, both));

        // A killing blow with the tattoo's 4% chance made certain consumes the corpse and recovers 10% of maximum life.
        string path = Path.Combine(Path.GetTempPath(), $"localexile-tattoo-corpse-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "tattoo-corpse", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "TattooCorpseChar", PlayerLife = 500, PlayerMana = 1000, Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            conn.PlayerStats[Program.Stats.Key("killing_blow_consumes_corpse_restore_10%_life_chance_%")!.Value] = 100;
            var zombie = Game.Monsters.Find("Metadata/Monsters/Zombies/ZombieBite")
                ?? throw new InvalidOperationException("no zombie variety");
            Assert.True(zombie.LifeData.IsUsableAsCorpse(0));
            var corpse = new SpawnedMonster("corpse", new(0, 0, 0), 0, 0, 100) { Dead = true, Life = 0, Variety = zombie };
            conn.Monsters[1] = corpse;
            Assert.True(Game.ConsumeCorpseOnKillLocked(conn, 1, corpse, 0));
            Assert.Equal(600u, conn.PlayerLife);
            Assert.True(corpse.CorpseConsumed);
            Assert.False(conn.Monsters.ContainsKey(1));
            Assert.False(Game.ConsumeCorpseOnKillLocked(conn, 1, corpse, 0));
            // A monster whose life type is not usable as a corpse is not consumed (Warcries.IsCorpse's rule).
            var construct = new SpawnedMonster("construct", new(0, 0, 0), 0, 0, 100)
            {
                Dead = true,
                Life = 0,
                Variety = Game.Monsters.Find("Metadata/Monsters/Ancestor/KaruiSpiritMaceSlamOak")
                    ?? throw new InvalidOperationException("no construct variety"),
            };
            Assert.False(construct.Variety.LifeData.IsUsableAsCorpse(0));
            conn.Monsters[2] = construct;
            Assert.False(Game.ConsumeCorpseOnKillLocked(conn, 2, construct, 0));
            Assert.Equal(600u, conn.PlayerLife);
            Assert.False(construct.CorpseConsumed);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
