using GameServer.Core.World;

// Support gems whose more damage depends on the target or the player at the hit (poedb): Hypothermia "more Damage with
// Hits and Ailments against Chilled Enemies", Behead "against Enemies on Low Life" (half its maximum Life or less, PoB
// LowPoolThreshold), Damage on Full Life "Supported Attack Skills deal more Damage while on Full Life", Vile Toxins
// "5% more Damage with Hits for each Poison on the Enemy, up to a maximum of 8 Poisons", Bloodlust "more Melee Physical
// Damage against Bleeding Enemies" and Power Charge on Critical "more Damage per Power Charge". GemSupportDamage builds
// the skill's hit without them, once per cast; these scale each target's roll.
static partial class Game
{
    internal static double ConditionalSupportMore(IReadOnlyDictionary<uint, int>? skill, bool attack, bool chilled,
        bool lowLife, bool fullLife, bool lifetap, int poisonStacks = 0, bool bleeding = false, int powerCharges = 0)
    {
        int Get(string name) => skill is not null && Stats.Key(name) is uint k ? skill.GetValueOrDefault(k) : 0;
        double more = 1;
        void Apply(bool applies, string name) { if (applies) more *= Math.Max(0, 1 + Get(name) / 100.0); }
        Apply(chilled, "support_hypothermia_damage_+%_vs_chilled_enemies_final");
        Apply(lowLife, "support_executioner_damage_vs_enemies_on_low_life_+%_final");
        Apply(attack && fullLife, "support_damage_while_on_full_life_+%_final");
        Apply(lifetap, "support_lifetap_damage_+%_final_while_buffed");
        Apply(bleeding, "support_bloodlust_melee_physical_damage_+%_final_vs_bleeding_enemies");
        // The per-stack share applies to the stacks that count (PoB: capped by the support's maximum).
        int counted = Math.Clamp(poisonStacks, 0, Math.Max(0, Get("support_debilitate_hit_damage_max_poison_stacks")));
        if (counted > 0) more *= Math.Max(0, 1 + Get("support_debilitate_hit_damage_+%_final_per_poison_stack") * counted / 100.0);
        if (powerCharges > 0)
            more *= Math.Max(0, 1 + Get("support_power_charge_on_crit_damage_+%_final_per_power_charge") * powerCharges / 100.0);
        return more;
    }

    // Call under the Monsters lock, after the per-target outgoing damage hooks.
    internal static DamagePacket WithConditionalSupportDamageLocked(ClientState conn, IReadOnlyDictionary<uint, int>? skill,
        bool attack, SpawnedMonster target, DamagePacket rolled, double now)
    {
        double more = ConditionalSupportMore(skill, attack, target.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, now) > 0,
            FlaskRecovery.LowLife(target.Life, target.MaxLife), conn.PlayerLife >= conn.PlayerMaxLife,
            Lifetap.ActiveAt(conn, now), target.Poisons.Stacks(now), target.Bleeds.Burning,
            ChargesOf(conn).Effective(ChargeType.Power));
        return more == 1 ? rolled : rolled.Scale(more);
    }
}
