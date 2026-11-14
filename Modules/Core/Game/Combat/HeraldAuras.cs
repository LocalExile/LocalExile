static partial class Game
{
    /// <summary>"X% increased Effect of Herald Buffs on you" scales only the five herald auras. The client names
    /// them in the GrantedEffects id ("Herald of Ash" is HeraldOfAsh, ...); the rule stays here, in the game logic,
    /// and the started aura carries the answer to AuraCatalog.RawStats.</summary>
    internal static bool IsHeraldAura(string effectId) => effectId.StartsWith("Herald", StringComparison.Ordinal);
}
