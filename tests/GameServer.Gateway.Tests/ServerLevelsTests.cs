namespace GameServer.Gateway.Tests;

public class ServerLevelsTests
{
    // A tier-16 Chateau (0x1d level 83) the client generated at seed 1174023763: tile hash 3728131682 in its
    // Client.txt and generation trace (2026-11-01). At its WorldAreas level (71) the topology pick differs
    // (chateau_1.dgr, not chateau_2.dgr) and so does the whole layout.
    [Fact]
    public void MapsAndMinesAreGeneratedAtTheLevelTheyArePlayedAt()
    {
        if (!File.Exists("data/game-files.zip")) return;
        foreach (var (seed, level, hex, hash) in new[] {
            (13u, (byte)34, "2000000000000000120007200400010780800000", 3805635301u),
            (12u, (byte)34, "2000000000000000210007100100010740200000", 2409549327u),
            (1145791572u, (byte)36, "2000000006000000210007100200010C40100000", 3697733175u),
            (1099130544u, (byte)36, "20000000050000003100071001000111502100001340200000", 3436672513u) })
        {
            try
            {
                var scene = ServerLevels.PrepareDelve(seed, level, Convert.FromHexString(hex));
                Assert.NotNull(scene);
                Assert.Equal(hash, scene.TileHash);
                Assert.Same(scene, ServerLevels.For("Delve_Main", seed));
                string prefix = $"data/walkability/Delve_Main_seed{seed}";
                if (File.Exists(prefix + ".bin")) Assert.Equal(File.ReadAllBytes(prefix + ".bin"), scene.Walk);
                if (File.Exists(prefix + ".projectile.bin")) Assert.Equal(File.ReadAllBytes(prefix + ".projectile.bin"), scene.Projectile);
            }
            finally { ServerLevels.ForgetDelve(seed); }
        }

        const string chateau = "MapWorldsChateau";
        const uint chateauSeed = 1174023763;
        if (ServerLevels.For(chateau, chateauSeed) is not { } atAreaLevel) return; // needs the game files (data/game-files.zip)
        Assert.Equal(2796439874u, atAreaLevel.TileHash);

        Assert.Equal(3728131682u, ServerLevels.For(chateau, chateauSeed, 83)?.TileHash);
        Assert.Equal(chateauSeed, ServerLevels.PlayableSeed(chateau, chateauSeed, 83));
        // The lookups without a level (monster packs, heights, projectiles) get the level it is played at.
        Assert.Equal(3728131682u, ServerLevels.For(chateau, chateauSeed)?.TileHash);
    }
}
