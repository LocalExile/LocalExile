using Xunit;

namespace GameServer.Gateway.Tests;

/// <summary>The mods a monster's own row carries (BossMods) decide two things the client's tables state outright: an act
/// boss that nothing shoves, and a boss's stage whose death gives no items or no experience.</summary>
public class BossModsTests
{
    static MonsterCatalog.Variety Variety(string id) =>
        Game.Monsters.Find(id) ?? throw new InvalidOperationException($"no variety {id}");

    [Fact]
    public void A_boss_row_says_whether_its_death_gives_items_and_experience()
    {
        // Malachai's first form and Shakari's and Piety's escape give neither: their rows name
        // MonsterNoDropsOrExperience, and the kill site reads it (Combat.cs, MinionOutcomes.cs).
        var malachai = Variety("Metadata/Monsters/Malachai/MalachaiBoss");
        Assert.True(BossMods.NoRewards(malachai));
        Assert.True(BossMods.GivesNoItems(malachai));

        // Abberath gives no items and still gives experience: a different mod, a different answer.
        var abberath = Variety("Metadata/Monsters/Abberath/Abberath");
        Assert.False(BossMods.NoRewards(abberath));
        Assert.True(BossMods.GivesNoItems(abberath));

        // The boss players actually farm keeps both.
        var brutus = Variety("Metadata/Monsters/Brute/BossBrute");
        Assert.False(BossMods.NoRewards(brutus));
        Assert.False(BossMods.GivesNoItems(brutus));

        // The repeat-stun immunity is a stat value on the mod, not a flag: every act boss row states 2000 ms after a stun
        // ends, which replaces the wiki's 4 s for uniques (HitReaction.TryFlinch).
        Assert.Equal(2000, BossMods.StunImmunityMilliseconds(malachai));
        Assert.Equal(2000, BossMods.StunImmunityMilliseconds(brutus));

        // And an ordinary monster carries neither list, so nothing changed for it.
        var zombie = Variety("Metadata/Monsters/Zombies/ZombieBite");
        Assert.False(BossMods.GivesNoItems(zombie));
        Assert.False(BossMods.KnockbackImmune(zombie));
        Assert.Null(BossMods.StunImmunityMilliseconds(zombie));
    }

    [Fact]
    public void An_act_boss_that_cannot_be_shoved_says_so_in_its_row()
    {
        // ImmuneToKnockback is a mod on these rows, so the client's component was never the answer: the component list
        // falls back to the eight base components and carries no such entry, which is why every one of these used to be
        // shoved (Knockback.KnockbackAllowed now asks BossMods).
        foreach (string id in (string[])
                 [
                     "Metadata/Monsters/Pope/Pope", "Metadata/Monsters/Malachai/MalachaiBoss",
                     "Metadata/Monsters/Malachai/MalachaiBoss2", "Metadata/Monsters/AvariusCasticus/AvariusCasticus",
                     "Metadata/Monsters/Vilenta/Vilenta", "Metadata/Monsters/Axis/PietyBeastBoss",
                 ])
            Assert.True(BossMods.KnockbackImmune(Variety(id)), id);

        // A campaign boss with no such mod is still shoved, and so is a plain monster.
        Assert.False(BossMods.KnockbackImmune(Variety("Metadata/Monsters/Brute/BossBrute")));
    }
}
