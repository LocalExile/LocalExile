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

    // A pick in a Flask Effect slot shows at once, not at the next area: the buffs of that cell's flask (its running
    // utility effect with its remaining time, the one Mageblood keeps) are set again with the cell's visual now. Phasing
    // follows with the stat presentations (SyncPhasing), which a pick does not otherwise run: the slot's variation is no
    // character stat.
    public static void RefreshFlaskEffectCosmetic(Player player, int cosmeticSlot)
    {
        if (!IsFlaskEffectSlot(cosmeticSlot) || !CurrentGameWork || player.Closed || player.LeavingArea) return;
        PresentStats(player);
        if (player.PlayerLife == 0) return;
        byte cell = (byte)(cosmeticSlot - FirstFlaskEffectSlot);
        ushort? visual = FlaskEffectVisual(player, cell);
        double now = AreaTime(player);
        foreach (var flask in ItemsOf(player).Items(PlayerInventories.Flask).Where(f => f.X == cell))
        {
            if (player.UtilityFlaskEffects.Of(flask.Id) is { } effect && effect.Slot == cell && effect.Ends > now)
                player.Area.Buffs.Set(player.Id, Flasks.Buff(player.Id, effect.Buff, effect.Flask.BuffRow, cell,
                    (effect.Ends - now) * effect.ExpiryRate, visual: visual));
            if (player.MagebloodBuffs.TryGetValue(flask.Id, out var mageblood) && mageblood.Slot == cell)
                player.Area.Buffs.Set(player.Id, Flasks.Buff(player.Id, mageblood.Buff, mageblood.Row, cell, seconds: null, visual: visual));
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} flask cell {cell}: flask effect visual now {visual?.ToString() ?? "the buff's own"}");
    }
}
