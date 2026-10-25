namespace GameServer.Gateway.Tests;

public class BowSetupTests
{
    // The Ranger's start weapon: Crude Bow, ItemClasses row 13 (Bows, stance 5), ItemVisualIdentity 0xE8F2.
    static ItemCatalog.BaseType Bow() =>
        new(-1, "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1", "Crude Bow", 0, 2, 3, 0xe8f2, ["Base"], 0, "Bow", 5)
        { ClassRow = 13 };

    [Fact]
    public void A_bow_is_in_the_left_hand_an_arrow_in_the_right_and_the_quiver_on_the_back()
    {
        var entries = BowSetup.Entries(Bow(), arrowArt: 0xb2f6, quiverArt: 0x328b);

        // The record the live server sent (Frida 2026-10-25): three entries, the bow's own art in slot 1 — the left hand,
        // where the client holds a bow — and no weapon type there.
        var left = Assert.Single(entries, e => e.Slot == 1);
        Assert.Equal(0xe8f2, left.VisualIdentity);
        Assert.Equal(BowSetup.NoWeaponType, left.Type);
        Assert.Equal(BowSetup.NoStance, left.Stance);

        // Slot 0 is the right hand: an arrow's art, and the entry that carries the main hand's weapon type and stance
        // (the client checks skill weapon restrictions against slots 0 and 1).
        var right = Assert.Single(entries, e => e.Slot == 0);
        Assert.Equal(0xb2f6, right.VisualIdentity);
        Assert.Equal(14, right.Type);      // ItemClasses row 13 (Bows) + 1
        Assert.Equal(5, right.Stance);     // ItemStances row Bow

        // The quiver on the back: slot 7, not the left hand.
        var back = Assert.Single(entries, e => e.Slot == 7);
        Assert.Equal(0x328b, back.VisualIdentity);
        Assert.Equal(BowSetup.NoWeaponType, back.Type);
        Assert.Equal(3, entries.Length);
    }
}
