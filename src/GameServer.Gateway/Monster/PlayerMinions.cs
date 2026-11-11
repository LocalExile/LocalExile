using GameServer.Core.Protocol.Instance;
using DamageType = GameServer.Core.World.DamageType;

/// <summary>
/// The monsters the player owns: relics, Spirit chieftains and gem golems (whose lifecycle is in GolemState).
/// A minion is
/// a monster on his team (Positioned team 0) that the client knows as his by its own stats is_player_minion and
/// is_semantic_minion (the link-target filter 0x141bf8490 reads 0x48a and 0x3cf8). It follows him (the monster AI walks
/// friendly monsters into the player's surround; one with a teleport jumps back to him from beyond its aggro range),
/// may fight his enemies, cast an aura and explode when it ends. Timed relics and chieftains leave with the connection;
/// golems keep their activation separately across areas and can take hostile damage. Never kept by the instance.
/// Guarded by the connection's Monsters lock.
/// </summary>
sealed class PlayerMinions
{
    /// <summary>One minion: its object id and monster, the skill that made it (an effect id), its kind (a relic's element),
    /// until when it lives (game seconds), its aura on the owner and its death explosion.</summary>
    internal sealed class Minion(uint id, SpawnedMonster monster, string skill, string kind)
    {
        public uint Id => id;
        public SpawnedMonster Monster => monster;
        public string Skill => skill;
        public string Kind => kind;
        public double Until;
        public Aura? OwnerAura { get; init; }
        public Explosion? Explodes { get; init; }
        /// <summary>It has a teleport to its owner (the relics' RelicTeleport).</summary>
        public bool Teleports { get; init; }
        /// <summary>The aura's received buff on the owner, 0 while it is not on him.</summary>
        public ushort OwnerBuff;
        /// <summary>It attacks the player's enemies near it (Game.MinionAttacks).</summary>
        public bool Fights { get; init; }
        /// <summary>It belongs to SpiritChieftains, whose cleanup must not retire unrelated fighting minions.</summary>
        public bool SpiritChieftain { get; init; }
    }

    /// <summary>An aura a minion casts on its owner: the skill, its BuffDefinitions row, its radius (cells) and its stats.
    /// The owner's aura effect does not scale it: it is the minion's skill, not his.</summary>
    internal sealed record Aura(string EffectId, ushort BuffRow, int Radius, IReadOnlyList<(uint Key, int Value)> Stats);

    /// <summary>A death explosion: this percent of the minion's maximum life as this type of damage to the hostile monsters
    /// within the radius (cells).</summary>
    internal sealed record Explosion(DamageType Type, int LifePct, int Radius);

    // A reserved contact is host-owned data, so the replacement area owner can finish the same
    // attack at its accepted deadline without keeping an old Game callback alive.
    internal sealed record Attack(uint Id, SpawnedMonster Monster, (int X, int Y) From, uint TargetId,
        SpawnedMonster Target, (int X, int Y) To, double ContactAt)
    {
        internal bool Started;
    }
    internal readonly Dictionary<uint, Attack> Attacks = [];
    // Accepted gameplay finishes under Monsters; only neutral output/projection data crosses Game replacement.
    internal enum OutputKind { Packet, Life, EnergyShield, Experience, Flinch, Remove, Vaal, QuestFlags, QuestKill, QuestTransition, QuestRoomRemove, PlayerKill, Charges, Passives, GemInventory, MonsterType, GolemAction, GolemIncoming }
    internal sealed record Output(byte[] Packet, string? Log, uint? TargetId = null, SpawnedMonster? Target = null,
        OutputKind Kind = OutputKind.Packet, double Until = 0, int Action = 0,
        AreaTransitionObject? Transition = null, GameServer.Modding.MonsterInfo? Kill = null, IReadOnlyList<AcceptedCharges>? Charges = null, byte? InventoryRow = null, IReadOnlyList<uint>? ItemIds = null, (int X, int Y)? At = null, (ushort Row, byte Level)? MonsterType = null);
    internal sealed record AcceptedCharges(bool KillEvent, bool Critical, MonsterRarity Rarity, string? Effect,
        IReadOnlyDictionary<uint, int>? Skill, bool Primary, double At, bool Killed = false, bool Frozen = false);
    internal sealed record FlaskReward(MonsterRarity Rarity, IReadOnlyDictionary<uint, double>? CritGains = null);
    internal readonly List<Output> OutputQueue = [];
    internal readonly List<FlaskReward> FlaskRewards = [];
    internal readonly HashSet<SpawnedMonster> Rewarded = [];
    internal long StatRevision, PresentedStatRevision, AccountRevision, PresentedAccountRevision;
    readonly Dictionary<uint, Minion> minions = [];

    public IReadOnlyCollection<Minion> All => minions.Values;
    public bool Owns(uint id) => minions.ContainsKey(id);
    public bool Owns(uint id, SpawnedMonster monster) =>
        minions.TryGetValue(id, out var owned) && ReferenceEquals(owned.Monster, monster);
    public bool Fights(uint id) => minions.TryGetValue(id, out var m) && m.Fights;
    public void Add(Minion minion) => minions[minion.Id] = minion;
    public void Remove(uint id) { minions.Remove(id); Attacks.Remove(id); }

    /// <summary>Ended minions' bodies still in the area and when they go (Game.PlayerMinionsTick).</summary>
    public readonly List<(uint Id, SpawnedMonster Monster, double At)> Bodies = [];

    /// <summary>Relic summons that triggers queued (Program.TriggerElementalRelicLocked); the next minion step makes them.</summary>
    public int PendingRelics;

    /// <summary>The trigger and element rolls (Next(int) only; the tests script it).</summary>
    public Random Random = Random.Shared;

    /// <summary>
    /// The stats an aura skill grants when a minion of this level casts it: between the two gem levels whose
    /// PlayerLevelReq brackets the minion's level, in proportion, rounded down. A level-85 relic's Anger 182–259, Hatred
    /// 43% and Wrath 26–415 with 23% (GGG, forum thread 3408278, quoted by the wiki) are levels 27 (84) and 28 (86) halfway;
    /// Path of Building takes the lower level. Below the first requirement the level-1 stats.
    /// </summary>
    internal static IReadOnlyList<(uint Key, int Value)> AuraStatsAt(SkillCatalog skills, StatCatalog stats, int effect,
        string effectId, int minionLevel)
    {
        int low = skills.MaxLevelAt(effect, minionLevel);
        int lowReq = skills.PlayerLevelReq(effect, low), highReq = skills.PlayerLevelReq(effect, low + 1);
        double share = highReq > lowReq ? Math.Clamp((minionLevel - lowReq) / (double)(highReq - lowReq), 0, 1) : 0;
        Dictionary<uint, int> At(int level) => AuraCatalog.RawStats([new(0, effect, effectId, effectId, 0, 0, 0, 0, level)], 0,
            skills, stats).ToDictionary(s => s.Key, s => s.Value);
        var (a, b) = (At(low), At(low + 1));
        return [.. a.Keys.Union(b.Keys).Select(k =>
            (k, (int)Math.Floor(a.GetValueOrDefault(k) + (b.GetValueOrDefault(k) - a.GetValueOrDefault(k)) * share)))];
    }
}

sealed partial class ClientState
{
    /// <summary>The player's minions (PlayerMinions), under the Monsters lock.</summary>
    public readonly PlayerMinions Minions = new();
}
