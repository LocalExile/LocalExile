using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class AbyssGazeTests
{
    [Fact]
    public void Gaze_uses_only_matching_active_abyss_jewels_and_caps_at_five()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        var items = Game.ItemsOf(player);
        var catalog = Game.ItemsFor(player);
        var unique = Game.UniquesFor(player);
        var tree = Game.PassivesFor(player);
        var sockets = Enumerable.Range(0, 57).Where(i => tree.JewelNodeForSlot(i) is not null).Take(13).ToArray();
        player.Character.Passives = sockets.Select(i => tree.JewelNodeForSlot(i)!.Value).ToList();

        void Socket(int i, Item jewel) => items.Add(PlayerInventories.PassiveJewels, checked((byte)sockets[i]), 0, jewel);
        Item Gaze(string name) => Assert.IsType<Item>(unique.Create(name, new Random(7)));
        Item Eye(string kind) => ItemCatalog.Create(catalog.Find("Metadata/Items/Jewels/JewelAbyss" + kind)!);
        (int Surge, int Terrain) Effects()
        {
            var stats = Game.EquipmentStatSources(player, items.EquipmentSnapshot()).Sources;
            int Get(uint key) => stats.GetValueOrDefault(key);
            return (Get(Stat.ArcaneSurgeEffectPlusPct),
                GatewayProjectileModifiers.Resolve(new ProjectileProfile("gaze"), Get, attack: true).TerrainChainChance);
        }

        Socket(0, Gaze("Kurgal's Gaze"));
        Socket(1, Gaze("Ulaman's Gaze"));
        Socket(2, Eye("Summoner"));
        Assert.Equal((8, 4), Effects());

        var belt = ItemCatalog.Create(catalog.Find(AbyssJewels.StygianVise)!) with
        {
            Sockets = [new ItemSocket(SocketColour.Abyss, Eye("Caster"))],
        };
        var worn = items.Add(PlayerInventories.Belt, 0, 0, belt);
        Assert.Equal((16, 4), Effects());
        items.Remove(PlayerInventories.Belt, worn.Id);
        Assert.Equal((8, 4), Effects());

        for (int i = 3; i < 8; i++) Socket(i, Eye("Caster"));
        for (int i = 8; i < 13; i++) Socket(i, Eye("Ranged"));
        Assert.Equal((40, 20), Effects());

        Game.GainArcaneSurgeLocked(player, Game.AreaTime(player));
        var equipment = Game.EquipmentStatSources(player, items.EquipmentSnapshot()).Sources;
        var buff = Game.WithSupportGemBuffs(player, new Dictionary<uint, int>(equipment));
        Assert.Equal(Game.ArcaneSurgeCastSpeed * 140 / 100,
            buff[Stat.ArcaneSurgeCastSpeedPlusPct]);
    }
}
