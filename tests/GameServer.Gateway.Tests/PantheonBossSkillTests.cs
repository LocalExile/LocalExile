using GameServer.Core.World;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// The Pantheon's campaign bosses (Tukohama, Abberath, Ryslatha, Greust, Gruthkul, Arakaali, Yugul, Solaris,
// Lunaris, Shakari, Garukhan) played one plain swing each: their varieties list whole kits in
// GrantedEffectsKeys but nothing took them (PantheonBosses.cs, after the Atlas bosses' 838bdcc6). The kit
// lists here are the varieties' own rows — an effect outside its row would send an action the client's actor
// has no clip for.
public class PantheonBossSkillTests
{
    static readonly (string Id, int[] Effects)[] Kits =
    [
        (PantheonBosses.Tukohama, PantheonBosses.TukohamaEffects),
        (PantheonBosses.Abberath, PantheonBosses.AbberathEffects),
        (PantheonBosses.Abberath2, PantheonBosses.Abberath2Effects),
        (PantheonBosses.Ryslatha, PantheonBosses.RyslathaEffects),
        (PantheonBosses.Greust, PantheonBosses.GreustEffects),
        (PantheonBosses.Gruthkul, PantheonBosses.GruthkulEffects),
        (PantheonBosses.Arakaali, PantheonBosses.ArakaaliEffects),
        (PantheonBosses.Yugul, PantheonBosses.YugulEffects),
        (PantheonBosses.Solaris, PantheonBosses.SolarisEffects),
        (PantheonBosses.Lunaris, PantheonBosses.LunarisEffects),
        (PantheonBosses.Shakari, PantheonBosses.ShakariEffects),
        (PantheonBosses.Shakari2, PantheonBosses.Shakari2Effects),
        (PantheonBosses.Garukhan, PantheonBosses.GarukhanEffects),
    ];

    [Fact]
    public void Every_pantheon_boss_kit_is_what_its_variety_granted()
    {
        var monsters = new MonsterCatalog();
        foreach (var (id, effects) in Kits)
        {
            var variety = monsters.Find(id);
            Assert.NotNull(variety);
            Assert.True(PantheonBosses.IsBoss(variety), id);
            // The kit is the row's own list, in its order, and every effect is one the skill catalog resolves.
            Assert.Equal(variety!.GrantedEffects, effects);
            foreach (int effect in effects)
                Assert.Contains(effect, variety.GrantedEffects!);
            // The basic skill (the record's own swing) is the kit's first effect, and its id sits in the boss's
            // own 0x4030 range, never in the player's gem range (the King in the Mists' crash).
            Assert.Equal(effects[0], PantheonBosses.BasicEffect(variety));
            foreach (int effect in effects)
            {
                ushort skill = PantheonBosses.Skill(variety, effect);
                // Each id is one per effect in the boss's own 0x4030 range, never the player's gem range.
                Assert.True(skill >= 0x4030 && skill < 0x4030 + effects.Length + 2, $"{id} effect {effect}");
            }
        }
        // The Brine King stays out (BrineKing.cs owns him); ordinary monsters stay out too.
        Assert.False(PantheonBosses.IsBoss(monsters.Find(PantheonBosses.BrineKingId)));
        Assert.False(PantheonBosses.IsBoss(monsters.Find("Metadata/Monsters/Zombie/Zombie")));
        // The monsters the fights raise are real varieties too: a summon the catalog cannot create is a skill that
        // silently does nothing (MonsterSummon logs exactly that).
        foreach (string raised in new[] { PantheonBosses.TotemVariety, PantheonBosses.EggVariety,
            PantheonBosses.SpiderHatch, PantheonBosses.GarukhanMinion })
        {
            var variety = monsters.Find(raised);
            Assert.NotNull(variety);
            Assert.True(variety!.Supported, raised);
            // None of them is a boss of its own: the AI drives them, the controllers do not.
            Assert.False(PantheonBosses.IsBoss(variety), raised);
        }
    }

    [Fact]
    public void The_melee_keeps_the_variety_s_own_time_and_a_marker_carries_no_hit()
    {
        var conn = new ClientState(new(1, "pantheon", 300, 300, 100, 100, null, []), 1, 1)
        {
            Character = $"pantheon-{Guid.NewGuid():N}", PlayerLife = 100, PlayerMana = 80,
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        };
        var monsters = new MonsterCatalog();
        var tukohama = monsters.Find(PantheonBosses.Tukohama)!;
        var swing = tukohama.AttackMs / 1000.0;
        var melee = PantheonBosses.Attack(conn, tukohama, 68, PantheonBosses.MeleeEffect);
        Assert.Equal(PantheonBosses.Skill(tukohama, PantheonBosses.MeleeEffect), melee.SkillId);
        Assert.Equal(swing, melee.Seconds, 2);
        Assert.True(melee.Damage.MaxTotal > 0);
        // The slam hits harder than the plain swing, at the clip's own cast time (BrineKing.cs reads the same way).
        var slam = PantheonBosses.Attack(conn, tukohama, 68, 6665);
        Assert.True(slam.Damage.MaxTotal > melee.Damage.MaxTotal, "the slam hits harder than the melee");
        Assert.True(slam.Seconds > 2, "the slam's clip keeps him busy");
        // A marker (his totem summon, a burrow, a dodge) deals no hit at all.
        var totem = PantheonBosses.Attack(conn, tukohama, 68, 5373);
        Assert.Equal(0, totem.Damage.MaxTotal);
        // The stationary one (Arakaali, MovementSpeed 0) is not walked toward the player by her controller.
        Assert.Equal(0, monsters.Find(PantheonBosses.Arakaali)!.MovementSpeed);
    }
}
