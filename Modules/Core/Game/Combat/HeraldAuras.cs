static partial class Game
{
    /// <summary>"X% increased Effect of Herald Buffs on you" scales only the five herald auras. The client names
    /// them in the GrantedEffects id ("Herald of Ash" is HeraldOfAsh, ...); the rule stays here, in the game logic,
    /// and the started aura carries the answer to AuraCatalog.RawStats.</summary>
    internal static bool IsHeraldAura(string effectId) => effectId.StartsWith("Herald", StringComparison.Ordinal);

    static int ActiveHeraldCount(IEnumerable<AuraCatalog.Aura> auras) => auras
        .Where(a => a.IsHerald && a.AffectsSelf && a.ExternalEffectPct is null)
        .Select(a => a.EffectId).Distinct(StringComparer.Ordinal).Count();

    static IEnumerable<AuraCatalog.Aura> WithHeraldScaledOwnAuras(IEnumerable<AuraCatalog.Aura> auras,
        SkillCatalog skills, int perHerald, int heralds)
    {
        int effect = ActorScale.Saturate(Math.Min(40, (long)perHerald * heralds));
        return effect == 0 ? auras : auras.Select(a => a.AffectsSelf && !a.IsHerald && a.ExternalEffectPct is null
            && skills.IsAura(a.Effect) ? a with { SupportEffectPct = ActorScale.Saturate((long)a.SupportEffectPct + effect) } : a);
    }
}
