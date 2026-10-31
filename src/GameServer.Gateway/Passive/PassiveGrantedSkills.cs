using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

/// <summary>
/// Skills that allocated passives grant or trigger (Agents/docs/protocol/11-skills.md §10f): "Grants Level 30
/// Determination Skill", "Trigger level 20 Summon Spirit of Kahuturoa Skill when ...", Abberath's Fury on each footstep.
/// PassiveSkills.dat GrantedEffectsPerLevel by PassiveSkillGraphId, GrantedEffectsPerLevelHardmode in Ruthless. The row
/// fixes the skill and its level. A passive-granted skill is not a gem: it has no supports, no quality, no gem-level
/// bonuses and no requirements. Ordinary and ascendancy nodes both count. Only skills with an executor act: granted
/// self auras (Program.GrantedSkillsOf), Summon Elemental Relic (Program.RelicTriggerOf), the Spirit chieftains
/// (Program.TriggeredGrantsLocked) and the footstep skill <c>RepeatingShockwave</c> (Abberath's Fury, Polytheist's Devout
/// of Abberath at level 20: a spell triggered by each of the player's footsteps while running, Program.RunFootstepSkills).
/// Game data: data/tables/PassiveSkills.json, loaded on first use.
/// </summary>
sealed class PassiveGrantedSkills(SkillCatalog skills, string folder = "data", bool ruthless = false)
{
    /// <summary>One granted skill: the node, its GrantedEffectsPerLevel row, the row's GrantedEffects row (and Id) and
    /// level.</summary>
    public readonly record struct Grant(ushort Node, int EffectsPerLevelRow, int Effect, string EffectId, int Level)
    {
        /// <summary>The host GemSkillIds keys the skill by (socket 0), so its skill id stays stable like a socketed
        /// gem's.</summary>
        public uint Host => HostBase | Node;
    }

    /// <summary>Synthetic hosts: item ids count up from 1 and never reach this range.</summary>
    public const uint HostBase = 0xFFFF0000;

    readonly Lazy<Dictionary<ushort, Grant>> _grants = new(() => Load(skills, folder, ruthless));

    /// <summary>The skills these nodes grant, in node order, each node once.</summary>
    public IReadOnlyList<Grant> For(IEnumerable<ushort> nodes) =>
        [.. nodes.Distinct().Where(_grants.Value.ContainsKey).Select(n => _grants.Value[n])];

    static Dictionary<ushort, Grant> Load(SkillCatalog skills, string folder, bool ruthless)
    {
        var grants = new Dictionary<ushort, Grant>();
        string path = Path.Combine(folder, "tables", "PassiveSkills.json");
        if (!File.Exists(path)) return grants;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var r in d.RootElement.EnumerateArray())
        {
            int graphId = r.GetProperty("PassiveSkillGraphId").GetInt32();
            // Ruthless uses its own column (Determination's points at DeterminationHardMode), as its gems use their
            // HardMode effects; HasHardmodeAlternate only marks the stat columns.
            if (graphId is <= 0 or > ushort.MaxValue
                || !r.TryGetProperty(ruthless ? "GrantedEffectsPerLevelHardmode" : "GrantedEffectsPerLevel", out var v)
                || v.ValueKind != JsonValueKind.Number || skills.EffectsPerLevel(v.GetInt32()) is not { } level
                || skills.EffectId(level.Effect) is not { } effectId) continue;
            grants.TryAdd((ushort)graphId, new((ushort)graphId, v.GetInt32(), level.Effect, effectId, level.Level));
        }
        return grants;
    }

    /// <summary>A skill each footstep triggers (its trigger has a runtime: Program.RunFootstepSkills).</summary>
    internal static bool IsFootstepSkill(string effectId) => effectId == "RepeatingShockwave";

    // The Templar's run animations (Art/Models/CHARACTERS/Templar/rig.amd, all 18 run_* clips): an 800 ms loop with
    // footstepright at 233 ms and footstepleft at 633 ms.
    // shortcut: the Templar's timing stands for every class, because only the Templar has a passive that grants a footstep
    // skill (the other rigs have 800-833 ms loops with other offsets); read the class rig's run_* footstep events
    // (scripts/re/anim_contacts.py) when another class gets one.
    const double RunLoopSeconds = .8;
    static readonly double[] FootstepAt = [.233, .633];

    /// <summary>The footsteps the run animation passes from <paramref name="from"/> to <paramref name="to"/> (its own clock:
    /// seconds since the run began at the animation's base rate): every footstep event in (from, to].</summary>
    public static int Footsteps(double from, double to) => to <= from ? 0
        : FootstepAt.Sum(at => (int)(Math.Floor((to - at) / RunLoopSeconds) - Math.Floor((from - at) / RunLoopSeconds)));

    /// <summary>
    /// Abberath's Fury's base radius in cells. Its stats have no <c>active_skill_base_area_of_effect_radius</c>, and
    /// neither PoB nor poedb give one: the real radius is the game server's own. This is its explosion art's:
    /// MiscAnimated AbberathsHoovesImpact's mesh (Art/Microtransactions/Aura/Uniques/Disjunction/disjunction.sm, BoundingBox
    /// ±73.553 world units, 6.8 cells at 250/23 units per cell). Unverified against the real game.
    /// </summary>
    public const int FootstepExplosionRadius = 7;

    /// <summary>The explosion art: SkillArtVariations <c>repeating_shockwave</c>'s default (MiscAnimated 1173
    /// AbberathsHoovesImpact), which is not scaled to an area (MiscAnimated <c>__col4</c> 0).</summary>
    public const string ExplosionArtVariation = "repeating_shockwave";

    /// <summary>How long the explosion art plays: its one clip (disjunction.amd "animate", once, 666 ms).</summary>
    public const int ExplosionArtMs = 700;
}
