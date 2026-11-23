using GameServer.Core.Protocol.Instance;

/// <summary>
/// The character: what carries across areas and is kept in its save (CharacterSaves). Its player entity (Player) is made
/// anew in each area and reads and changes this.
/// </summary>
public sealed partial class Character
{
    /// <summary>The hand-over for a change of what the player's stats are built from (the player sets it).</summary>
    public Action<StatInput>? StatsChanged;
    public string? Name { get; set; }
    public Guid? Id { get; set; }
    /// <summary>The account the character belongs to (Auth's resolve): its stash (AccountStash) is shared by all of them.</summary>
    public string? Account { get; set; }
    public string? League { get; set; }
    public bool Hardcore { get; set; }
    public bool Ssf { get; set; }
    public string? LeagueForm { get; set; }
    public double GemExperienceCarry;
    public bool HardcoreDeathRecorded;
    /// <summary>The character's last town (HASH16, from Auth's resolve): Resurrect in Town goes there. 0 before the
    /// character has entered a town (the death screen then has no Resurrect in Town).</summary>
    public ushort LastTown { get; set; }
    // The character's class: its Characters.dat row (the Witch: 1).
    public int ClassRow { get; set; } = SkillCatalog.WitchRow;
    /// <summary>The portal cosmetic: the MicrotransactionPortalVariations row every portal is listed with (0x008f +0x26),
    /// 0 for the base effect. Applied to portals already standing as well as to new ones.</summary>
    public ushort CosmeticPortalVariation;
    /// <summary>The portrait frame the character logged in with (Auth's ticket): what the client draws and other players
    /// are told. A pick reaches it at the next login, as the pick's message says.</summary>
    public ushort PortraitFrame { get; set; }
    /// <summary>The worn cosmetics as 0x1f3 lists them (WornCosmetics), read from the save on first use; null after a
    /// pick, so the next use reads it again.</summary>
    public List<InstanceMicrotransactions.Equipped>? WornCosmetics;
    /// <summary>The Cosmetics tab's last published skill targets, built inside the send gate with 0x1f3.
    /// A key lives only while its full target remains present; retired keys are never reassigned on this connection.</summary>
    public Dictionary<uint, InstanceMicrotransactions.SkillSlot> SkillSlots = [];
    public uint NextSkillCosmeticKey = InstanceMicrotransactions.SlotCount;
    // The character's experience (the Player component's; the level follows from it) and its allocated passives
    // (PassiveSkillGraphId), both kept in its save (CharacterSaves). Changed in the area's frame.
    public uint Experience;
    /// <summary>A change in place (Add, Remove) is the changer's to hand over (StatsChanged with StatInput.Passives).</summary>
    public List<ushort> Passives
    {
        get { StatGraph.ReadInput((int)StatInput.Passives); return passives; }
        set { passives = value; StatsChanged?.Invoke(StatInput.Passives); }
    }
    List<ushort> passives = [];
    /// <summary>The three Atlas passive presets (PassiveSkillGraphId) and the points the character has; kept in its save.</summary>
    public List<ushort>[] AtlasPassives = [[], [], []];
    public ushort AtlasPassivePoints;
    /// <summary>The Atlas preset the player last changed (the client's active one, Atlas tree +0xc2f0); -1 until a change
    /// this session, when the fullest preset counts as active.</summary>
    public int AtlasPreset = -1;
    /// <summary>The cluster jewel nodes allocated in the tree's expansion cells: the client's own small ids, sent with
    /// the expansion flag (C->S 0x34/0x36) and carried in 0x33's second list. Empty until a cluster jewel is socketed.
    /// </summary>
    public HashSet<ushort> ClusterPassives = [];
    public AscendancyProgress MainAscendancy = new();
    // The Descendancy.dat specialisation row the client shows as a second ascendancy layer (0: none). 0x33 carries it in
    // its spare byte; Affliction's Wildwood classes and the 3.29 Bloodline classes are rows of that table.
    public byte DescendancyRow;
    // The descendancy layer's allocated nodes (PassiveSkills rows with that DescendancyKey); sent in 0x33 with the rest.
    public HashSet<ushort> DescendancyNodes = [];
    // The Descendancy row the server last offered with 0x7b kind 0x4c, and the area it offered it in. C->S 0x3a is
    // accepted only for that row in that area, so a client cannot pick a row the server did not present.
    public byte OfferedDescendancyRow;
    public ushort OfferedDescendancyArea;
    public CharacterSaves.PassiveProgressSnapshot? SavedPassiveProgress;
    // Selected mastery effects: mastery node (PassiveSkillGraphId) -> effect HASH16. Saved with the character.
    public Dictionary<ushort, ushort> MasteryEffects = [];
    // Applied tattoos: passive node (PassiveSkillGraphId) -> tattoo name. The tattoo stays on the node when the
    // passive is unallocated, so this map is independent of Passives. Saved with the character.
    public Dictionary<ushort, string> Tattoos = [];
    // Journey Tattoo of Makanui's chosen replacement keystone: passive node -> keystone PassiveSkillGraphId.
    public Dictionary<ushort, ushort> TattooKeystones = [];
    // Changes whenever Tattoos/TattooKeystones changes; part of the stat-source cache key.
    public int TattooRevision;
    // Extra passive points from /add_passive_points (the 0x33 extra_skill_points field), saved with the character.
    public ushort ExtraPassivePoints;
    // Refund points (0x33's first u16; Orbs of Regret), saved with the character; /add_refund_points grants them for now.
    public ushort RefundPoints;
    // The monster varieties that died in this instance, for NPCs a death frees (Quests.NpcRule.FreedBy); under QuestNpcs' lock.
    public readonly HashSet<string> KilledVarieties = new(StringComparer.Ordinal);
    /// <summary>The character's quest flags (QuestFlags.dat Ids), saved in data/saves/.</summary>
    public HashSet<string> QuestFlags = [];
    /// <summary>Of <see cref="Quests.EveryCharacter"/> (given on login, in <see cref="QuestFlags"/> but for
    /// <see cref="Quests.EndgameOnly"/>), those the character's save does not have: granted, not earned. A quest event that
    /// sets one (Kitava's death: EndGameAvailable) saves it and takes it out of here; what is earned only through the quest
    /// (Kitava's Act 10 penalty, /state's flags) reads this, and Game.PlayerQuestFlags sends an unearned endgame flag only
    /// in a hideout or a map area.</summary>
    public HashSet<string> UnearnedQuestFlags = [];
    /// <summary>The chosen Pantheon gods (PantheonPanelLayout Ids, null = none), from the save (CharacterSaves.SetPantheon);
    /// the Pantheon module changes them in the area's frame (campaign/pantheon.md).</summary>
    public string? PantheonMajor, PantheonMinor;
    // QuestFlags is changed in the area's frame (SetQuestFlagsHeld); code that may run beside it (the player tick)
    // reads it through these, under the same lock.
    public HashSet<string> QuestFlagsSnapshot() { return [.. QuestFlags]; }
    public bool HasQuestFlag(string flag) { return QuestFlags.Contains(flag); }
    /// <summary>Whether the character earned the flag: it has it, and not only as one every character is given at login
    /// (<see cref="UnearnedQuestFlags"/>: EndGameAvailable before Kitava's Act 10 death).</summary>
    public bool EarnedQuestFlag(string flag) { return QuestFlags.Contains(flag) && !UnearnedQuestFlags.Contains(flag); }
    // One quest-flag change at a time per connection (SetQuestFlags, the quest item sync): each reads, changes and sends the whole
    // set, so two at once could send an older set last.
    public readonly SemaphoreSlim QuestGate = new(1, 1);

    /// <summary>The character's league quest flags (client ids, Quests.League): every character's, and at login those the
    /// account saved in its league (AccountStash.LeagueQuestFlags, Game.SetLeagueQuestFlags).</summary>
    public HashSet<int> LeagueQuestFlags = [.. Quests.League.EveryCharacter, .. Program.Bench.UnlockFlags];
    // The character's activated waypoints (WorldAreas Ids, kept in its save) and whether this connection was reached
    // by waypoint travel (the player then appears next to the area's waypoint).
    public readonly HashSet<string> Waypoints = [];
    // The areas the character has entered (WorldAreas Ids, kept in its save): their visit quest flags.
    public readonly HashSet<string> Visited = [];
}
