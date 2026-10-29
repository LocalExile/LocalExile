namespace GameServer.Core.Protocol.Instance;

/// <summary>Actor 0x40 entry. Identity is the zero-based ActiveSkills row, not a skill instance or GEPL row.</summary>
public readonly record struct VaalSoulBudget(uint ActiveSkillsRow, uint Capacity, uint Current);

/// <summary>Actor 0x20 seed: creates/replaces a row with zero current souls.</summary>
public readonly record struct VaalSoulSeed(uint ActiveSkillsRow, uint Capacity);

/// <summary>Actor 0x20 update. Native code clamps Current against the OLD capacity before assigning NewCapacity.</summary>
public readonly record struct VaalSoulUpdate(uint ActiveSkillsRow, uint Current, uint NewCapacity);
