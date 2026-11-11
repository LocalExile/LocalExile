/// <summary>
/// Source-owned beast companions. Pending and active records retain the worn host identity, skill source, module
/// lifetime and area so a summon can be validated after an await without borrowing another minion's ownership.
/// </summary>
public sealed class BeastCompanions
{
    /// <summary>
    /// One continuous lifetime of one real worn grant. The owner survives ordinary stat and skill publications
    /// unchanged; source removal retires it, so re-equipping the same host starts a new generation.
    /// </summary>
    public sealed class SourceGeneration(WornGrantedSkills.Grant grant)
    {
        public WornGrantedSkills.Grant Grant { get; } = grant;
        public bool Retired;
    }

    public sealed record Source(SourceGeneration Owner)
    {
        public WornGrantedSkills.Grant Grant => Owner.Grant;
        public uint Host => Grant.Host;
        public int Effect => Grant.Effect;
        public string EffectId => Grant.EffectId;
        public int Level => Grant.Level;
    }

    public sealed record PendingSummon(Source Source, ushort SkillId, string Skill, string Variety, string MinionKind,
        byte MinionLevel, int MaximumLifeIncreasePct, int BeastLimit, object? Lifetime, object? Area, int? ActionToken);

    public sealed record ActiveBeast(uint Id, SpawnedMonster Monster, Source Source, string Skill, string Variety,
        string MinionKind, object? Lifetime, object? Area);

    public PendingSummon? Pending;
    public ActiveBeast? Active;
    public readonly Dictionary<WornGrantedSkills.Grant, SourceGeneration> SourceGenerations = [];
}

public sealed partial class ClientState
{
    /// <summary>The player's source-owned beasts, under the Monsters lock.</summary>
    public readonly BeastCompanions Beasts = new();
}
