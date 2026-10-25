/// <summary>Up-front mana and life costs. Reservation and damage from a skill are separate from paying its cost.</summary>
readonly record struct SkillCost(uint Mana, uint Life)
{
    public static SkillCost Calculate(int baseMana, int baseLife, Func<string, int> get)
    {
        decimal mana = Math.Max(0, baseMana + get("base_mana_cost_+"));
        decimal life = Math.Max(0, baseLife + get("base_life_cost_+"));
        if (get("base_skill_cost_life_instead_of_mana") != 0) { life += mana; mana = 0; }
        else life += mana * get("base_skill_gain_life_cost_%_of_mana_cost") / 100;
        decimal common = Math.Max(0, 100 + get("base_cost_+%")) / 100m;
        // The native stat name retains "-%", but its stored value is signed (Phrecia's no-cost node stores -100).
        mana *= common * Math.Max(0, 100 + get("mana_cost_+%") + get("base_mana_cost_-%")) / 100;
        life *= common * Math.Max(0, 100 + get("life_cost_+%") + get("base_life_cost_+%")) / 100;
        if (get("base_no_life_cost") != 0) life = 0;
        return new((uint)Math.Clamp(decimal.Truncate(mana), 0, uint.MaxValue),
            (uint)Math.Clamp(decimal.Truncate(life), 0, uint.MaxValue));
    }

    // Spending life cannot kill the player. Check both pools before changing either one.
    public bool Pay(ref uint mana, ref uint life)
    {
        if (mana < Mana || life <= Life) return false;
        mana -= Mana;
        life -= Life;
        return true;
    }
}
