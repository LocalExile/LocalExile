using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The three current tattoo rows of issue #372 whose consumers are new: the client's PassiveSkillOverrides carry the
// Herald buff effect, guard-skill duration and Fortify-on-stun stats. The consumers are the herald buff stats
// (AuraCatalog.RawStats), the guard duration (TriggeredGuards.GuardDuration, added into the skill-duration sum) and
// the damage-based Fortification gain on a successful melee stun (FortificationSession.FortifyFromStun).
public sealed class TattooConsumerTests
{
    static readonly (string Name, string Stat, int Value)[] Rows =
    [
        ("Tattoo of the Arohongui Shaman", "herald_effect_on_self_+%", 5),
        ("Tattoo of the Rongokurai Guard", "guard_skill_effect_duration_+%", 6),
        ("Tattoo of the Tukohama Warrior", "chance_to_fortify_on_melee_stun_%", 5),
    ];

    [Fact]
    public void Every_consumer_tattoo_maps_its_client_stats()
    {
        foreach (var (name, stat, value) in Rows)
        {
            var stats = TattooStats.For(name);
            Assert.NotNull(stats);
            Assert.Contains((Program.Stats.Key(stat)!.Value, value), stats);
        }
    }

    [Fact]
    public void The_new_consumers_read_the_herald_guard_and_fortify_stats()
    {
        // Herald buff effect: the same Herald of Ash buff stat at level 20 with 100% increased Effect of Herald Buffs.
        var herald = new AuraCatalog.Aura(1, Program.Skills.EffectRow("HeraldOfAsh"), "HeraldOfAsh", "Herald of Ash",
            1, 1, 0, 0, Level: 20);
        int HeraldValue(int heraldEffectPct) => AuraCatalog.RawStats([herald], 0, Program.Skills, Program.Stats,
            heraldEffectPct).Where(stat => Program.Stats.Name(stat.Key) == "physical_damage_%_to_add_as_fire")
            .Select(stat => stat.Value).DefaultIfEmpty(0).First();
        int plain = HeraldValue(0);
        Assert.True(plain > 0);
        Assert.Equal(plain * 2, HeraldValue(100));

        // Guard skills' duration: "Guard Skills have 6% increased Duration" adds into the same increased-duration sum
        // as skill_effect_duration_+%, it does not multiply the finished duration.
        int Get(string name) => name switch
        {
            "skill_effect_duration_+%" => 50, "guard_skill_effect_duration_+%" => 6, _ => 0,
        };
        Assert.Equal(15.6, Game.GuardDuration(10000, Get, _ => 0), 6);

        // Melee hits which stun: 100% chance to Fortify grants the gain model's stacks from the hit's raw damage and
        // the target's life and rarity; cannot_gain_fortification_from_hits blocks it.
        var values = new Dictionary<string, int>
        {
            [FortificationSession.ChanceOnStun] = 100, ["fortification_gained_from_hits_+%"] = 50,
        };
        var session = new FortificationSession();
        int expected = FortificationGainModel.Calculate(200m, 1000m, MonsterRarity.Normal, session.Value.Maximum,
            new FortificationGainModel.Modifiers(50, 0));
        Assert.True(expected > 0);
        Assert.True(session.FortifyFromStun(0, 200m, 1000m, MonsterRarity.Normal,
            name => values.GetValueOrDefault(name), new Random(1)));
        Assert.Equal(expected, session.Read(0).Uncapped);
        values[FortificationSession.CannotGain] = 1;
        Assert.False(session.FortifyFromStun(1, 200m, 1000m, MonsterRarity.Normal,
            name => values.GetValueOrDefault(name), new Random(1)));
        values.Remove(FortificationSession.CannotGain);
        values[FortificationSession.ChanceOnStun] = 0;
        Assert.False(session.FortifyFromStun(2, 200m, 1000m, MonsterRarity.Normal,
            name => values.GetValueOrDefault(name), new Random(1)));
    }
}
