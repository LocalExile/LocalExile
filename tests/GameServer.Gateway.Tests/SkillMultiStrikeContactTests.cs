namespace GameServer.Gateway.Tests;

// A skill that strikes more than once in one use (Double Strike's base_skill_number_of_additional_hits = 1) lands each
// strike on its own clip's next contact keyframe, which differs per class rig: doublestrike_sword contacts at 300 and 666
// of 1000 ms on the Str rig but 299 and 899 of 1133 on the Dex one, so AnimationContacts.SkillAttackContactAt reads the
// frame instead of a constant. Data extracted before the "contacts" key existed has only the earliest frame, and the
// caller then models the gap (Game.GemAttackStrikeGap) — that fallback must stay reachable, not throw.
public sealed class SkillMultiStrikeContactTests
{
    const string Str = "Metadata/Characters/Str/Str.ao";
    const string Dex = "Metadata/Characters/Dex/Dex.ao";
    const string Old = "Metadata/Characters/Old/Old.ao";

    // A one-hand sword, the weapon Double Strike's own doublestrike_sword clip is named for.
    static WeaponProfile Sword => new(Shield: false, DualWield: false, Staff: false, Bow: false, DualClaws: false,
        TwoHand: false, Melee: true, TwoHandMelee: false, Sword: true);

    [Fact]
    public void Only_double_strike_gem_variants_replay_the_base_additional_hit_stat()
    {
        Assert.Equal(2, SkillMultiStrike.StrikeCount("DoubleStrike", 1, immediate: false, channelled: false, spell: false, wave: false));
        Assert.Equal(2, SkillMultiStrike.StrikeCount("DoubleStrikeAltX", 1, immediate: false, channelled: false, spell: false, wave: false));
        Assert.Equal(2, SkillMultiStrike.StrikeCount("DoubleStrikeAltY", 1, immediate: false, channelled: false, spell: false, wave: false));
        Assert.Equal(2, SkillMultiStrike.StrikeCount("VaalDoubleStrike", 1, immediate: false, channelled: false, spell: false, wave: false));
        Assert.Equal(1, SkillMultiStrike.StrikeCount("Cyclone", 1, immediate: false, channelled: true, spell: false, wave: false));
        Assert.Equal(1, SkillMultiStrike.StrikeCount("LightningTendrilsOld", 3, immediate: false, channelled: false, spell: true, wave: false));
        Assert.Equal(1, SkillMultiStrike.StrikeCount("Cleave", 0, immediate: false, channelled: false, spell: false, wave: false));
        Assert.Equal(1, SkillMultiStrike.StrikeCount("DoubleStrike", 1, immediate: true, channelled: false, spell: false, wave: false));
        // StatSet 280: only base additional hits = 1 and bleed-on-hit = 25. Load the real row rather than injecting test-only stats.
        var skills = new SkillCatalog();
        int effect = skills.EffectRow("DoubleStrike");
        var stats = skills.EffectStats(effect, 1).ToDictionary(entry => entry.Item1, entry => entry.Item2);
        uint additionalHits = Program.Stats.Key("base_skill_number_of_additional_hits")!.Value;
        uint bleedOnHit = Program.Stats.Key("bleed_on_hit_with_attacks_%")!.Value;
        Assert.Equal(1, stats[additionalHits]);
        Assert.Equal(25, stats[bleedOnHit]);
        Assert.Equal(2, SkillMultiStrike.StrikeCount("DoubleStrike", stats[additionalHits],
            immediate: false, channelled: false, spell: false, wave: false));

        // First-strike victims are added to the caller's live set, but are not part of this snapshot; second-strike
        // filtering allows the same target and still excludes monsters already hit by earlier path sweeps.
        HashSet<uint> once = [10, 20];
        var prior = SkillMultiStrike.SnapshotPriorTargets(once, strikeCount: 2);
        once.Add(30);
        Assert.True(SkillMultiStrike.WasHitBeforeUse(prior, 10));
        Assert.False(SkillMultiStrike.WasHitBeforeUse(prior, 30));
        Assert.False(SkillMultiStrike.WasHitBeforeUse(prior, 40));
        Assert.Null(SkillMultiStrike.SnapshotPriorTargets(once, strikeCount: 1));
    }

    [Fact]
    public void Each_strike_reads_its_own_keyframe_and_an_older_extract_falls_back_to_null()
    {
        // As data/animation-contacts.json has it: two contact frames per clip, the second one later on the Dex rig.
        var contacts = AnimationContactsTables.From("""
            { "Metadata/Characters/Str/Str.ao": { "animations": { "doublestrike_sword": [1000, 300] },
                "contacts": { "doublestrike_sword": [[300, null], [666, null]] } },
              "Metadata/Characters/Dex/Dex.ao": { "animations": { "doublestrike_sword": [1133, 299] },
                "contacts": { "doublestrike_sword": [[299, null], [899, null]] } },
              "Metadata/Characters/Old/Old.ao": { "animations": { "doublestrike_sword": [1000, 300] } } }
            """);

        // The first strike is the clip's earliest contact, the second its next frame — 0.366 later on the Str rig.
        var first = contacts.SkillAttackContactAt(Str, Sword, "doublestrike", 0);
        var second = contacts.SkillAttackContactAt(Str, Sword, "doublestrike", 1);
        Assert.True(first.HasValue && second.HasValue, "both of the Str rig's doublestrike_sword keyframes should answer");
        Assert.Equal("doublestrike_sword", second.Value.Name);
        Assert.Equal(300.0 / 1000, first.Value.Contact, 6);
        Assert.Equal(666.0 / 1000, second.Value.Contact, 6);
        Assert.Equal(666.0 / 1000 - 300.0 / 1000, second.Value.Contact - first.Value.Contact, 6);
        // The same skill on another class lands its second strike far later: a constant would be wrong here.
        Assert.Equal(899.0 / 1133, contacts.SkillAttackContactAt(Dex, Sword, "doublestrike", 1)!.Value.Contact, 6);
        // A clip with a single contact frame has no second strike, and neither does an index past the data.
        Assert.Null(contacts.SkillAttackContactAt(Str, Sword, "doublestrike", 2));
        Assert.Null(contacts.SkillAttackContactAt(Str, Sword, "icescrash", 1));
        Assert.Null(contacts.SkillAttackContactAt("Metadata/None.ao", Sword, "doublestrike", 1));
        // Data extracted before the key existed keeps its earliest contact only: null, never an exception, so the caller
        // models the gap instead (Game.GemAttackStrikeGap's half-attack fallback).
        Assert.True(contacts.SkillAttackContact(Old, Sword, "doublestrike").HasValue);
        Assert.Null(contacts.SkillAttackContactAt(Old, Sword, "doublestrike", 1));
    }
}
