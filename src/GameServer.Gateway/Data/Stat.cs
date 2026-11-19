/// <summary>What the client does with some stats (its reads, from client code), and the groups the rules read together.
/// Keys are Stats.dat row + 1, not the row (the generated constants in Stat.g.cs).</summary>
public static partial class Stat
{
    // movement_velocity_+%: walk speed = base × (1 + value / 100), at least −100; movement_velocity_cap caps it when
    // positive (0x141e827a0). maximum_life is the final maximum the life bars and orbs read.
    // hide_mini_life_bar: non-zero hides the object's overhead life bar (listener 0x140220fd0).
    // skill_is_non_pathing: the action starts where the actor stands instead of walking into range first.
    // A projectile action's release (0x141ba7c30) fires nothing without number_of_projectiles_override or a count from
    // 0x141ba8b00, which reads virtual_total_number_of_projectiles, or virtual_total_number_of_arrows for an action that
    // fires arrows; the mortar attack's constructor (0x141b6c390) takes its MonsterMortar row from
    // skill_behaviour_variation, row 0 (MortarMolten) without it.
    // A spell action's cast-duration function reads virtual_base_spell_cast_time_ms: a spell skill without it
    // dereferenced a missing value (0xc0000005 at 0x19e37bf, live 2026-10-31).
    // main_hand_weapon_type is the client's native weapon enum: a bow is 9, while its ItemClasses row + 1 is 14
    // (13-items.md "Client weapon-type stats"). skill_uses_main_hand gates the character panel's MH_* damage rows.

    /// <summary>The raw "increased attack speed" inputs of an unarmed melee attack (attack time = base / (1 + sum / 100))
    /// that /set_stat can add; the passives' conditional ones are summed into CharacterStats.Pools.AttackSpeedPct by
    /// PassiveBonus.</summary>
    public static readonly uint[] AttackSpeedInputs = [AttackSpeedPlusPct, MeleeAttackSpeedPlusPct, MainHandAttackSpeedPlusPct, UnarmedMeleeAttackSpeedPlusPct];

    /// <summary>The id of a stat name made at run time (a family prefix, a name from data), or a server-only stat the
    /// client's table does not have (StatCatalog.Id). Resolve it once and keep the id, not per read.</summary>
    public static uint Of(string name) => Program.Stats.Id(name);
}
