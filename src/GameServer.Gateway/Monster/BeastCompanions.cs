/// <summary>
/// Source-owned beast companions. Pending and active records retain the worn host identity, skill source, module
/// lifetime and area so a summon can be validated after an await without borrowing another minion's ownership.
/// </summary>
sealed class BeastCompanions
{
    /// <summary>
    /// One continuous lifetime of one real worn grant. The owner survives ordinary stat and skill publications
    /// unchanged; source removal retires it, so re-equipping the same host starts a new generation.
    /// </summary>
    internal sealed class SourceGeneration(WornGrantedSkills.Grant grant)
    {
        internal WornGrantedSkills.Grant Grant { get; } = grant;
        internal bool Retired;
    }

    internal sealed record Source(SourceGeneration Owner)
    {
        internal WornGrantedSkills.Grant Grant => Owner.Grant;
        internal uint Host => Grant.Host;
        internal int Effect => Grant.Effect;
        internal string EffectId => Grant.EffectId;
        internal int Level => Grant.Level;
    }

    internal sealed record PendingSummon(Source Source, ushort SkillId, string Skill, string Variety, string MinionKind,
        byte MinionLevel, int MaximumLifeIncreasePct, int BeastLimit, object? Lifetime, object? Area, int? ActionToken);

    internal sealed record ActiveBeast(uint Id, SpawnedMonster Monster, Source Source, string Skill, string Variety,
        string MinionKind, object? Lifetime, object? Area);

    public PendingSummon? Pending;
    public ActiveBeast? Active;
    internal readonly Dictionary<WornGrantedSkills.Grant, SourceGeneration> SourceGenerations = [];
}

sealed partial class ClientState
{
    /// <summary>The player's source-owned beasts, under the Monsters lock.</summary>
    public readonly BeastCompanions Beasts = new();
}
