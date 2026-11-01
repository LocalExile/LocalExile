/// <summary>An NPC a quest decides in an instance (Agents/docs/game/campaign-quests.md; Quests.NpcRule, ExistsRule, AppearsRule):
/// where it stands, which rules decide whether it stands there (its area and type, or the rule that made it appear), the
/// state it starts in, and its object id while created (0: not). Only data: the Game module's QuestNpcs.cs creates,
/// removes and transitions them as the character's flags change, asking <see cref="Present"/> with the player's tables,
/// so a reload of the rules leaves nothing of the old ones here.</summary>
sealed class QuestNpc(NpcCatalog.Npc npc, int x, int y, float angle, string area, string npcType, Quests.AppearsRule? appears, bool roomState)
{
    public NpcCatalog.Npc Npc { get; } = npc;
    public int X { get; } = x;
    public int Y { get; } = y;
    public float Angle { get; } = angle;
    public string Area { get; } = area;
    /// <summary>The NPCs.dat Id the rules name.</summary>
    public string NpcType { get; } = npcType;
    /// <summary>The rule that made it appear where a monster died (Quests.AppearsRule), or null for a placed NPC.</summary>
    public Quests.AppearsRule? Appears { get; } = appears;
    /// <summary>A room NPC outside a town starts in Transitionable state 1 (Fairgraves on his ship); a town NPC in its
    /// room line's.</summary>
    public bool RoomState { get; } = roomState;
    public uint Id { get; set; }

    /// <summary>Whether it stands there for a character with these flags, by these tables: an NPC that appeared until its
    /// GoneWith (and while its type exists), a placed one by its area's NpcRules and its type's ExistsRules.</summary>
    public bool Present(Quests.RuleSet rules, Func<string, bool> has) => Appears is { } rule
        ? rules.Exists(rule.NpcType, has) && (rule.GoneWith is null || !has(rule.GoneWith))
        : rules.NpcPresent(Area, NpcType, has);
}
