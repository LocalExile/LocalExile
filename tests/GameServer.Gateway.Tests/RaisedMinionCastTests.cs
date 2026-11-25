using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// An owned minion's create data, and the removal of the corpse it was raised from (once).
public sealed class RaisedMinionCastTests
{
    static string Le(uint v) => Convert.ToHexString(BitConverter.GetBytes(v));

    [Fact]
    public void An_owned_create_carries_flag_0x02_and_base_data()
    {
        var blob = new MonsterObject(100, 100, 100, MonsterObject.BaseComponents)
        {
            ObjectFlags = 0x02,
            BaseData = [(MonsterObject.OwnerKey, 0, 0x1000), (MonsterObject.SourceKey, 0, 0x800abcd), (MonsterObject.SelfKey, 1, 0x800abce)],
        }.Blob();
        Assert.Equal("0203" + "9CFCF040000101" + Le(0x1000) + "79C80077000101" + Le(0x800abcd) + "3AAC50B3010101" + Le(0x800abce),
            Convert.ToHexString(blob.AsSpan(0, 2 + 3 * 11)));
    }

    [Fact]
    public async Task A_raise_with_no_corpse_in_reach_sends_nothing_and_costs_nothing()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        var source = Assert.Single(f.Equip(("Raise Zombie", 20)));
        uint mana = player.PlayerMana;
        var sent = new List<byte[]>(); var previous = Program.Outgoing;
        Program.Outgoing = (_, bytes) => { lock (sent) sent.Add((byte[])bytes.Clone()); };
        try { Assert.False(await Game.CastGemSkill(player, source, (100, 100), (100, 100))); }
        finally { Program.Outgoing = previous; }
        Assert.Equal(mana, player.PlayerMana);
        Assert.Empty(player.Summons.Fighters);
        lock (sent) Assert.DoesNotContain(sent, b => Convert.ToHexString(b).Contains("0203", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_raised_zombie_is_created_with_its_corpse_named_and_the_corpse_is_removed_once()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        Assert.Single(f.Equip(("Raise Zombie", 20)));
        const uint corpseId = 0x800abcd;
        var corpse = new SpawnedMonster("corpse", new(100, 100, 0), 0, 0, 1000)
            { Dead = true, Life = 0, Hostile = true, Variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!, Level = 70, ObjectId = corpseId };
        player.Area.Monsters[corpseId] = corpse;
        var sent = new List<byte[]>(); var previous = Program.Outgoing;
        Program.Outgoing = (_, bytes) => { lock (sent) sent.Add((byte[])bytes.Clone()); };
        uint id;
        try { id = await f.Summon("RaiseZombie", corpseId); }
        finally { Program.Outgoing = previous; }
        Assert.NotEqual(0u, id);

        // The create names its owner, the corpse it rose from and itself, with flag 0x02.
        string baseData = "9CFCF040000101" + Le(player.Id) + "79C80077000101" + Le(corpseId) + "3AAC50B3010101" + Le(id);
        lock (sent) Assert.Contains(sent, b => Convert.ToHexString(b).Contains("0203" + baseData, StringComparison.Ordinal));
        // Spectres and the other families the official servers write differently keep the plain create (flag 0, no base data).
        Assert.True(Game.OfficialOwnedCreate("Zombie", "RaiseZombie"));
        Assert.All(new[] { "Spectre", "AnimatedWeapon", "Spider", "Golem" }, family => Assert.False(Game.OfficialOwnedCreate(family, family)));
        // The corpse is consumed and its removal is sent (a corpse that was created as one would not be).
        Assert.True(corpse.CorpseConsumed);
        Assert.Single(player.Minions.OutputQueue, o => o.RemovesObject && o.WorldId == corpseId
            && o.Packet.AsSpan().SequenceEqual(InstanceProtocol.RemoveObject(corpseId)));
    }
}
