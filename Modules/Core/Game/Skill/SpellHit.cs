using GameServer.Core.World;


/// <summary>The supported spell hit before critical strikes and target mitigation, shared by combat and the panel.</summary>
static class SpellHit
{
    public static DamagePacket Resolve(SkillHitPlan plan, StatCatalog catalog,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own,
        DamageConversion conversion, double addedEffectiveness = 1)
    {
        // Added damage does not create a hit for a beam, aura or another spell without extracted hit damage.
        if (plan.Kind != SkillHitKind.Spell || !plan.CanHit) return DamagePacket.Empty;
        int Get(string name) => catalog.Key(name) is uint key
            ? own.TryGetValue(key, out int value) ? value : player.GetValueOrDefault(key) : 0;
        var hit = SpellDamage.Packet(plan.SpellBase, Get, addedEffectiveness, conversion);
        return new SkillDamageContext(plan.Kind, player).Hit(hit);
    }
}
