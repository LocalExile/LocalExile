using System.Reflection;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class UpstreamModeIntegrationTests
{
    [Theory]
    [InlineData("Hardcore", "Standard", false, false)]
    [InlineData("SSF Hardcore", "SSF Standard", true, false)]
    [InlineData("Hardcore Ruthless", "Void", false, true)]
    [InlineData("SSF Hardcore Ruthless", "Void", true, true)]
    public void A_lethal_development_contact_commits_the_mode_death_before_returning(
        string source, string destination, bool ssf, bool ruthless)
    {
        TestSaves.Ensure();
        var now = DateTimeOffset.UtcNow;
        string name = "DevContactMode" + Guid.NewGuid().ToString("N");
        var character = new Character(Guid.NewGuid(), Guid.NewGuid(), name, CharacterClass.Witch, 1,
            source, "1_1_2", now, now);
        var store = new SqliteAccountStore(Saves.Db);
        store.AddCharacter(character);
        var conn = new ClientState(new(ushort.MaxValue, "ModeIntegration", 128, 128, 32, 32, null, []), 0, 1)
        {
            Character = name, CharacterId = character.Id, ClassRow = 1, League = source,
            Hardcore = true, Ssf = ssf, Ruthless = ruthless, PlayerLife = 1, PlayerMana = 100,
        };
        conn.Pools = Program.PoolsFor(conn, 1);
        conn.PlayerLife = 1;
        conn.RecentEnemyHits.Record(conn.Clock.Elapsed.TotalSeconds);
        object?[] args = [conn, DamageType.Physical, 100_000, false, false, false, null, false];
        try
        {
            bool accepted = (bool)typeof(Program).GetMethod("TryDevEnemyHit",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;

            Assert.True(accepted);
            Assert.Equal(0u, conn.PlayerLife);
            Assert.True(conn.HardcoreDeathRecorded);
            Assert.Equal(0, conn.RecentEnemyHits.Count(conn.Clock.Elapsed.TotalSeconds));
            var saved = store.FindCharacter(name)!;
            Assert.Equal(destination, saved.League);
            Assert.NotNull(saved.HardcoreDeath);
            Assert.Equal(source, saved.HardcoreDeath.SourceLeague);
            Assert.Equal(ruthless, saved.HardcoreDeath.PermanentlyDead);
            // Another command reaches admission after the durable terminal outcome and cannot mutate the old owner.
            Assert.False((bool)typeof(Program).GetMethod("TryDevEnemyHit",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!);
            Assert.Equal(saved, store.FindCharacter(name));
        }
        finally { PlayerInventories.Forget(name); }
    }

    [Fact]
    public void Runtime_Harvest_uses_a_separate_mode_catalog_and_keeps_ordinary_crafting_stable()
    {
        var ordinary = Connection("HarvestModeOrdinary", false);
        var ruthless = Connection("HarvestModeRuthless", true);
        Assert.Same(Program.Harvest, Program.HarvestFor(ordinary));
        Assert.NotSame(Program.HarvestFor(ordinary), Program.HarvestFor(ruthless));

        Item Target(ClientState conn) => ItemCatalog.Create(Program.ItemsFor(conn).Find("Two-Stone Ring")!) with
            { ItemLevel = 100, Rarity = ItemRarity.Rare, Identified = true };
        var ordinaryTarget = Target(ordinary);
        var before = Program.HarvestFor(ordinary).Apply(0xa3e5, ordinaryTarget, new Random(27));
        var alternate = Program.HarvestFor(ruthless).Apply(0xa3e5, Target(ruthless), new Random(27));
        var after = Program.HarvestFor(ordinary).Apply(0xa3e5, ordinaryTarget, new Random(27));

        Assert.NotNull(before);
        Assert.NotNull(alternate);
        Assert.NotNull(after);
        Assert.Contains(alternate.Item.Mods, mod => Program.ItemsFor(ruthless).Mods.ByHash(mod.Hash)?.Tags.Contains("fire") == true);
        foreach (var mod in alternate.Item.Mods)
        {
            var definition = Program.ItemsFor(ruthless).Mods.ByHash(mod.Hash)!;
            Assert.Equal(definition.Stats.Count, mod.Values.Count);
            foreach (var (stat, value) in definition.Stats.Zip(mod.Values))
                Assert.InRange(value, Math.Min(stat.Min, stat.Max), Math.Max(stat.Min, stat.Max));
        }
        Assert.Equal(before.Item.Mods.Select(m => (m.Hash, string.Join(',', m.Values))),
            after.Item.Mods.Select(m => (m.Hash, string.Join(',', m.Values))));
    }

    static ClientState Connection(string character, bool ruthless) =>
        new(new(ushort.MaxValue, "ModeIntegration", 128, 128, 32, 32, null, []), 0, 1)
        { Character = character, ClassRow = 1, Ruthless = ruthless, PlayerLife = 100, PlayerMana = 100 };
}
