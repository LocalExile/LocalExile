/// <summary>Buff.Tag values: two buffs of one row from one source, which the client keeps as two instances.</summary>
public enum BuffTag : ushort
{
    None,
    /// <summary>Cat's Agility's Onslaught (row 215), beside the kill or flask Onslaught.</summary>
    CatOnslaught,
    /// <summary>A charge's permanent Onslaught (row 215), beside the kill one.</summary>
    ChargeOnslaught,
    /// <summary>A Vaal aura's two buffs (its row may be a reservation aura's too).</summary>
    VaalAura,
    /// <summary>Blinded (row 179) by the player's blind aura, Physical Aegis or a flask's ground, beside a hit's Blind.</summary>
    BlindAura,
    AegisBlind,
    FlaskBlind,
    RampageBlind,
    /// <summary>A totem's aura on the player (its row may be one of the player's own auras').</summary>
    TotemAura,
    /// <summary>A Survivors run's herald auras (their rows are the heralds').</summary>
    SurvivorsHerald,
    /// <summary>Blood and Sand stance buff (rows 1308/1309), beside Flesh and Stone.</summary>
    BloodAndSand,
}
