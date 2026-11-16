using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    static void ReviewWeaponProjectileScope()
    {
        var active = ClientTables.Table("ActiveSkills");
        int checkedEffects = 0;
        foreach (var effect in ClientTables.Table("GrantedEffects"))
        {
            if (effect.IntOrNull("ActiveSkill") is not int row
                || active[row].Text("Id") != "monster_projectile_weapon") continue;
            Assert.Null(Program.Skills.MonsterProjectile(effect.Index));
            if (Program.Skills.MonsterProjectile(effect.Index, includeWeaponAttacks: true) is not null)
                checkedEffects++;
        }
        Assert.True(checkedEffects > 0);
        int mortar = Program.Skills.EffectRow("MMASandSpitterProjectile");
        Assert.Equal(Program.Skills.MonsterProjectile(mortar),
            Program.Skills.MonsterProjectile(mortar, includeWeaponAttacks: true));
    }

    static async Task ReviewRingSocketRefresh(Wire wire)
    {
        string character = "RingSocketReview" + Guid.NewGuid().ToString("N");
        var conn = new ClientState(new(1, "ring-socket", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = character, PlayerLife = 10000, PlayerMana = 10000,
            Experience = CharacterStats.ExperienceFor(100), Pools = new(100, 10000, 10000, 2, 0),
            LeagueModule = new(new League("ring-socket", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        try
        {
            foreach (string stat in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                         "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
            conn.PlayerStats[Program.Stats.Key("enable_ring_slot_3")!.Value] = 1;
            var items = Game.ItemsOf(conn);
            var ring = items.Add(102, 0, 0, ItemCatalog.Create(Program.Items.Find("Unset Ring")!) with
            { Sockets = [new ItemSocket(SocketColour.White)] });
            var gem = items.Add(PlayerInventories.Cursor, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!));
            conn.Pools = Game.PoolsFor(conn, 100);
            await Game.SocketGem("ring-socket", wire.Stream, wire.Ciphers, conn, new(103, ring.Id, 0));
            ushort fireball = Assert.Single(Game.GemSkillEffects(conn), s => Program.Skills.EffectId(s.Effect) == "Fireball").Id;
            Assert.Contains(fireball, conn.ClientSkillIds);
            Assert.Empty(items.Items(PlayerInventories.Cursor));
            var qualified = conn.StatSourcesCache;
            var pools = conn.Pools;
            for (int read = 0; read < 100; read++)
            {
                Game.ReservedMana(conn);
                Game.LifeCap(conn);
            }
            Assert.Same(qualified, conn.StatSourcesCache);
            Assert.Equal(pools, conn.Pools);
            await Game.UnsocketGem("ring-socket", wire.Stream, wire.Ciphers, conn, new(103, ring.Id, 0));
            Assert.DoesNotContain(fireball, conn.ClientSkillIds);
            Assert.DoesNotContain(Game.GemSkillEffects(conn), s => Program.Skills.EffectId(s.Effect) == "Fireball");
            Assert.Equal(gem.Item.TypeHash, Assert.Single(items.Items(PlayerInventories.Cursor)).Item.TypeHash);
        }
        finally
        {
            conn.Closed = true;
            PlayerInventories.Forget(character);
            Game.InactiveItems.TryRemove(character, out _);
        }
    }
}
