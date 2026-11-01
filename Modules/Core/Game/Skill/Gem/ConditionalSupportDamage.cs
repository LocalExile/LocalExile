using GameServer.Core.World;

// Support gems whose more damage depends on the target or the player at the hit (poedb): Hypothermia "more Damage with
// Hits and Ailments against Chilled Enemies", Behead "against Enemies on Low Life" (half its maximum Life or less, PoB
// LowPoolThreshold), Damage on Full Life "Supported Attack Skills deal more Damage while on Full Life". GemSupportDamage
// builds the skill's hit without them, once per cast; these scale each target's roll.
static partial class Game
{
    internal static double ConditionalSupportMore(IReadOnlyDictionary<uint, int>? skill, bool attack, bool chilled,
        bool lowLife, bool fullLife)
    {
        int Get(string name) => skill is not null && Stats.Key(name) is uint k ? skill.GetValueOrDefault(k) : 0;
        double more = 1;
        void Apply(bool applies, string name) { if (applies) more *= Math.Max(0, 1 + Get(name) / 100.0); }
        Apply(chilled, "support_hypothermia_damage_+%_vs_chilled_enemies_final");
        Apply(lowLife, "support_executioner_damage_vs_enemies_on_low_life_+%_final");
        Apply(attack && fullLife, "support_damage_while_on_full_life_+%_final");
        return more;
    }

    // Call under the Monsters lock, after the per-target outgoing damage hooks.
    internal static DamagePacket WithConditionalSupportDamageLocked(ClientState conn, IReadOnlyDictionary<uint, int>? skill,
        bool attack, SpawnedMonster target, DamagePacket rolled, double now)
    {
        double more = ConditionalSupportMore(skill, attack, target.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, now) > 0,
            FlaskRecovery.LowLife(target.Life, target.MaxLife), conn.PlayerLife >= conn.PlayerMaxLife);
        return more == 1 ? rolled : rolled.Scale(more);
    }
}
