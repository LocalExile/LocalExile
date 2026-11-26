using System.Buffers.Binary;
using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>The native completed flag and the map run that owns it.</summary>
public sealed class MapPortalLabelTests
{
    // 0x008f: u16 id, u8 flags, u8 +0x2e, u8 +0x31, u32 BE device, u16 BE +0x2c, u8 +0x2f, u8 +0x30, u8 n, then each
    // entry's u32 BE portal object and u16 BE area HASH16. Bit 1 of the flags is the completed label (+0x33); bit 0 is
    // the "just opened" render path (+0x32) and is never set.
    [Fact]
    public void A_portals_list_carries_the_completed_flag_and_never_the_opened_bit()
    {
        InstanceMapDevice.Portal[] portals = [new(0x400, 0x2a5a, "Owner", Style: 1, Variation: 6)];
        byte[] plain = InstanceMapDevice.Portals(0x300, portals);
        byte[] completed = InstanceMapDevice.Portals(0x300, portals, completed: true);

        Assert.Equal(InstanceMapDevice.PortalsId, BinaryPrimitives.ReadUInt16BigEndian(plain));
        Assert.Equal((byte)0x00, plain[2]);
        Assert.Equal((byte)0x02, completed[2]);
        // The flag is all that moves: the device, the entry and the area the label is named after stay as they were.
        Assert.Equal(plain.Length, completed.Length);
        Assert.Equal(plain[..2], completed[..2]);
        Assert.Equal(plain[3..], completed[3..]);
        Assert.Equal(0x300u, BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(5)));
        Assert.Equal((byte)1, plain[13]);
        Assert.Equal(0x400u, BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(14)));
        Assert.Equal((ushort)0x2a5a, BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(18)));
        // A portal listed with area 0 closes, and its list is a plain one.
        Assert.Equal((byte)0x00, InstanceMapDevice.Portals(0x300, [new(0x400, 0)])[2]);
    }


    [Fact]
    public async Task Completion_follows_the_exact_run_and_requires_its_actual_final_bosses()
    {
        const string owner = "portal-label-owner", guest = "portal-label-guest";
        var area = new MapDevice.MapArea(0x2a5a, "MapWorldsStrand", "Strand", 1);
        Player Player(InstanceState instance, ulong number, MapDevice.MapArea map) =>
            new(new(map.Hash16, map.Id, 32, 32, 10, 10, null, []), 0, 1)
            { Character = { Name = guest, Account = guest }, Instance = instance, InstanceNumber = number, Connection = { PlayerSent = true }, PlayerLife = 100 };
        SpawnedMonster Boss(int row, bool dead = false) => new("boss", new(15, 15, 30), 60, 8, 100)
            { Variety = Game.Monsters.Find(row.ToString()), Rarity = MonsterRarity.Unique, Dead = dead, Life = dead ? 0u : 100u };
        try
        {
            var run = MapDevice.Activate(owner, area);
            MapDevice.Entered(run, 987654);
            var instance = new InstanceState();
            var player = Player(instance, 987654, area); // The guest can finish it with the owner absent.
            var rows = Game.Packs.For(area.Id)!.Bosses;
            Assert.Equal(2, rows.Count);
            player.Area.Monsters[1] = Boss(rows[0], dead: true);
            player.Area.Monsters[99] = new("unrelated unique", new(12, 12, 0), 60, 8, 100) { Rarity = MonsterRarity.Unique };
            await Game.TickMapRunBoss(player, force: true);
            Assert.False(Game.RunBossKilled(run)); // The second actual boss has not appeared yet.
            player.Area.Monsters.Remove(1); // Consuming the first corpse must not lose its kill.
            var last = Boss(rows[1]);
            player.Area.DormantMonsters[2] = last;
            await Game.TickMapRunBoss(player, force: true);
            Assert.False(Game.RunBossKilled(run));
            (last.Dead, last.Life) = (true, 0);
            await Game.TickMapRunBoss(player, force: true);
            Assert.True(Game.RunBossKilled(run)); // The unrelated unique is still alive.
            var returned = Player(new InstanceState(), 1234, area);
            returned.MapPortalRun = MapDevice.OpenOf(owner);
            Assert.True(Game.RunBossKilled(returned.MapPortalRun));
            Assert.False(Game.RunBossKilled(MapDevice.Activate(guest, area)));
            var replacement = MapDevice.Activate(owner, area);
            await Game.TickMapRunBoss(player, force: true);
            Assert.False(Game.RunBossKilled(replacement));
            Assert.True(Game.RunBossKilled(run));

            var core = new MapDevice.MapArea(0x1234, "MapWorldsCore", "Core", 2);
            var phased = MapDevice.Activate(owner, core);
            MapDevice.Entered(phased, 987655);
            var phases = Player(new InstanceState(), 987655, core);
            var forms = Game.Packs.For(core.Id)!.Bosses;
            Assert.Equal(2, forms.Count);
            phases.Area.Monsters[1] = Boss(forms[0]); // First form flees; it need not leave a dead corpse.
            await Game.TickMapRunBoss(phases, force: true);
            Assert.False(Game.RunBossKilled(phased));
            phases.Area.Monsters.Clear();
            phases.Area.Monsters[2] = Boss(forms[1]);
            phases.Area.Monsters[3] = Boss(forms[1], dead: true); // A dead twin cannot complete a living one's fight.
            await Game.TickMapRunBoss(phases, force: true);
            Assert.False(Game.RunBossKilled(phased));
            (phases.Area.Monsters[2].Dead, phases.Area.Monsters[2].Life) = (true, 0);
            await Game.TickMapRunBoss(phases, force: true);
            Assert.True(Game.RunBossKilled(phased));

            // A guest captures a real Reef soul while the owner's device and pack are full.
            var catalog = Program.Items;
            var device = MapDevice.Load("data", row => catalog.Row(row)?.Hash, catalog);
            MapDevice.Slot Slot(string id)
            {
                var type = Assert.IsType<ItemCatalog.BaseType>(catalog.Find(id));
                return new(type.Hash, type.Class, type.Name, type.Row, [], 0) { BaseId = type.Id, Components = type.Components };
            }
            var reef = Slot("Metadata/Items/Maps/MapWorldsReef");
            var vessel = Slot(ItemDrops.DivineVesselId);
            Assert.Null(device.Check([reef, vessel, vessel], owner, catalog.Mods, out _));
            var legacyVessel = Slot(PantheonCatalog.DivineVesselId);
            Assert.Null(device.Check([reef, vessel, legacyVessel], owner, catalog.Mods, out _));
            var legacyOpening = Assert.IsType<MapDevice.Opening>(device.Check([reef, legacyVessel], owner, catalog.Mods, out _));
            var opening = Assert.IsType<MapDevice.Opening>(device.Check([reef, vessel], owner, catalog.Mods, out _));
            Assert.Equal(legacyOpening.PantheonSoul, opening.PantheonSoul);
            Assert.Equal("MapWorldsReef", opening.Area.Id);
            var soul = Assert.IsType<PantheonCatalog.Soul>(opening.PantheonSoul);
            Assert.Equal("BrineKingUpgrade1", soul.UpgradeFlag);
            var ownerItems = PlayerInventories.For(owner, Program.Items);
            var filler = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring1")!);
            Saves.Atomically(() =>
            {
                foreach (byte row in new[] { PlayerInventories.MapDevice, PlayerInventories.MainInventory })
                    while (ownerItems.FreeCell(row, 1, 1, Game.ShapeOf) is { } at)
                        ownerItems.Add(row, at.X, at.Y, filler);
            });
            var reefRun = MapDevice.Activate(owner, opening.Area, opening.Mods, pantheonSoul: soul);
            MapDevice.Entered(reefRun, 987656);
            var reefPlayer = Player(new InstanceState(), 987656, opening.Area);
            var reefBosses = Game.Packs.For(opening.Area.Id)!.Bosses;
            Assert.NotEmpty(reefBosses);
            uint id = 1;
            foreach (int row in reefBosses) reefPlayer.Area.Monsters[id++] = Boss(row, dead: true);
            await Game.TickMapRunBoss(reefPlayer, force: true);
            Assert.True(Game.RunBossKilled(reefRun));
            Assert.Equal(soul.VesselItemId, CharacterSaves.ModuleData(owner, "pantheon.pendingSouls"));
            Assert.NotNull(ownerItems.ConsumeOne(PlayerInventories.MapDevice, ownerItems.Items(PlayerInventories.MapDevice).First().Id));
            await Game.DeliverCapturedSouls(reefPlayer, owner);
            await Game.DeliverCapturedSouls(reefPlayer, owner);
            Assert.True(string.IsNullOrEmpty(CharacterSaves.ModuleData(owner, "pantheon.pendingSouls")));
            Assert.Single(ownerItems.Items(PlayerInventories.MapDevice), item =>
                item.Item.TypeHash == InstanceEntities.ObjectTypeHash(soul.VesselItemId));
        }
        finally { MapDevice.Close(owner); MapDevice.Close(guest); }
    }
}
