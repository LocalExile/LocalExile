public static partial class Game
{
    // The five Flask Effect cosmetic slots (MicrotransactionSlot 26-30), one per flask-bar cell: their
    // flask_effect_variation belongs to that flask's buff, not to the character's stats.
    const int FirstFlaskEffectSlot = 26;

    public static bool IsFlaskEffectSlot(int slot) => slot is >= FirstFlaskEffectSlot and < FirstFlaskEffectSlot + 5;

    // The BuffVisuals override for a flask's buff (S→C 0x1c1 bit4) from the cosmetic worn on its cell: a variation's own
    // row (BuffVisualsArtVariations flask_effect), or row 0 (invisible, NoEffect.epk) for Invisible Flask Effect's -1, as
    // the auras' hidden effects use. Null keeps the buff's own art.
    public static ushort? FlaskEffectVisual(Player player, byte flaskSlot)
    {
        var worn = WornCosmetics(player).FirstOrDefault(w => w.Slot == FirstFlaskEffectSlot + flaskSlot);
        if (worn is null || MtxEffects.Of(worn.MtxTypeHash) is not { } effect) return null;
        int variation = effect.Stats.FirstOrDefault(s => s.Stat == Stat.FlaskEffectVariation).Value;
        return variation switch { 0 => null, < 0 => (ushort)0, _ => Art.BuffVisual("flask_effect", variation) };
    }
}
