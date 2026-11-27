using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Login;

/// <summary>
/// Minions a monster's skill brings, for any monster: what a SummonSpecificMonsters skill names
/// (SkillCatalog.MonsterSummon), or, for a minion skill the player also has, the player's minion of that skill
/// (SummonCatalog.ForActiveSkill) with the monster skill's own count, limit and duration. Monsters.MonsterSummon brings
/// them; this retires them when their duration ends or a newer use takes their place.
/// </summary>
public static partial class Game
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadMonsterMinionsData()
    {
        TotemSkins = LoadTotemSkins();
    }

    /// <summary>What a monster's skill summons at a level, or null for a skill that brings none the server can name.
    /// <para>A player minion skill's monster version brings the player's minion only when its stats name no
    /// <c>alternate_minion</c>: ExileSummonSkeletons' stat set has none, as the gem's has none. Its monster versions that
    /// set one (ExileRaiseZombie, ExileSummonRagingSpirit, ExileSummonIceGolem: 1) pick a variant the client never
    /// resolves (outside the SummonSpecificMonsters classes the value names no data row; the server alone creates the
    /// minion), so they bring nothing. A skill that raises a corpse needs one (MonsterSkills.OnCorpse).</para></summary>
    public static SkillCatalog.MonsterSummonInfo? MonsterSummonOf(SkillCatalog catalog, int effect, byte level)
    {
        if (catalog.MonsterSummon(effect, level) is { } specific) return specific;
        if (MonsterTotemOf(catalog, effect, level) is { } totem) return totem;
        if (SummonData.ForActiveSkill(effect) is not { Corpse: false } definition) return null;
        var stats = GolemData.Skill(catalog, effect, level).Stats;
        int Get(uint key) => stats.GetValueOrDefault(key);
        if (Get(Stat.AlternateMinion) != 0 || Monsters.Find(definition.Variety) is not { Supported: true } variety) return null;
        int ms = Get(Stat.BaseSkillEffectDuration);
        return new(variety.Row, SummonCount(definition, Get), 0, false, catalog.CastTimeMs(effect) / 1000.0,
            catalog.CooldownMs(effect, catalog.MaxLevelAt(effect, level)) / 1000.0)
        {
            Limit = definition.LimitStat == 0 ? 0 : Math.Max(0, Get(definition.LimitStat)),
            Lifetime = definition.Persistent || ms <= 0 ? double.PositiveInfinity : ms / 1000.0,
            AtTarget = true,
        };
    }

    /// <summary>Metadata/Monsters/Monster.ot: base_number_of_totems_allowed 1 (traps 3, remote mines 5), every monster's.</summary>
    public const int MonsterTotemsAllowed = 1;

    // SkillTotemVariations: (SkillTotemsKey, TotemSkinId) → MonsterVarieties row.
    static Dictionary<(int Key, int Skin), int> TotemSkins = default!;

    static Dictionary<(int Key, int Skin), int> LoadTotemSkins()
    {
        var result = new Dictionary<(int, int), int>();
        foreach (var v in ClientTables.TableOrEmpty("SkillTotemVariations"))
            if (v.IntOrNull("MonsterVarietiesKey") is int row) result.TryAdd((v.Int("SkillTotemsKey"), v.Int("TotemSkinId")), row);
        return result;
    }

    /// <summary>A monster's totem skill (stats is_totem, or the SummonsTotem type: Siege Ballista) as a summon of its
    /// body, which then uses the skill (MonsterSummonInfo.Effect).
    /// <para>The body is SkillTotemVariations' row for the skill's ActiveSkills.SkillTotemId (12 for Siege Ballista's
    /// Snipe totems) or, for a skill that names none of its keys (22, as Fireball's, Wither's and Ensnaring Arrow's do),
    /// the player's generic totem of its kind (spell 3, ranged attack 2: Totems.Rules: TotemVariety), at the skin <c>totem_art_variation</c> names (the Rogue
    /// Exiles' 5: ZigguratTotem, SnipeZigguratTotem). It stands base_totem_duration (with totem_duration_+%), as many
    /// come at once as number_of_totems_summoned_in_formation, and the summoner keeps Monster.ot's one plus the skill's
    /// number_of_additional_totems_allowed. It rises where the summoner stands.</para></summary>
    public static SkillCatalog.MonsterSummonInfo? MonsterTotemOf(SkillCatalog catalog, int effect, byte level)
    {
        var stats = GolemData.Skill(catalog, effect, level).Stats;
        int Get(uint key) => stats.GetValueOrDefault(key);
        // Non-totem monsters can share an ActiveSkill row with a player totem (e.g. Voidbearer FlamebearerFlameBlue
        // uses totem_fire_spray which has ActiveSkillType SummonsTotem even though is_totem == 0 and base_totem_duration == 0).
        bool isTotemSkill = Get(Stat.IsTotem) != 0 || Get(Stat.BaseSkillIsTotemified) != 0
            || (catalog.HasType(effect, "SummonsTotem") && Get(Stat.BaseTotemDuration) > 0);
        if (!isTotemSkill) return null;
        int? skillKey = ClientTables.TableOrNull("GrantedEffects")?.Row(effect)?.IntOrNull("ActiveSkill") is int active
            ? ClientTables.TableOrNull("ActiveSkills")?.Row(active)?.IntOrNull("SkillTotemId") : null;
        int skin = Get(Stat.TotemArtVariation);
        // As the player's (Totems.Rules: TotemVariety): a spell totem is the intelligence one (3), a ranged attack
        // totem (is_ranged_attack_totem: Ensnaring Arrow's ballista) the dexterity one (2).
        int key = skillKey is int k && TotemSkins.ContainsKey((k, skin)) ? k
            : catalog.IsSpell(effect) ? 3 : Get(Stat.IsRangedAttackTotem) != 0 ? 2 : -1;
        if (!TotemSkins.TryGetValue((key, skin), out int body) || Monsters.Find(body.ToString()) is not { Supported: true }) return null;
        double seconds = Get(Stat.BaseTotemDuration) / 1000.0 * Math.Max(0, 100 + Get(Stat.TotemDurationPlusPct)) / 100;
        return new(body, Math.Max(1, Get(Stat.NumberOfTotemsSummonedInFormation)), 0, true, catalog.CastTimeMs(effect) / 1000.0,
            catalog.CooldownMs(effect, catalog.MaxLevelAt(effect, level)) / 1000.0)
        {
            Limit = MonsterTotemsAllowed + Get(Stat.NumberOfAdditionalTotemsAllowed),
            Lifetime = seconds > 0 ? seconds : double.PositiveInfinity,
            Effect = effect,
        };
    }

    /// <summary>In the area's frame: the summoner's living minions of a variety beyond its limit, oldest (lowest
    /// object id) first, to make room for <paramref name="incoming"/> more.</summary>
    /// <summary>How many of a summon's monsters its summoner has standing. In the area's frame.</summary>
    public static int LivingSummonsLocked(Player player, uint summoner, SkillCatalog.MonsterSummonInfo summon) =>
        LivingSummonsLocked(player, summoner, summon, cachedAreaLists: false);

    internal static int LivingSummonsLocked(Player player, uint summoner, SkillCatalog.MonsterSummonInfo summon, bool cachedAreaLists)
    {
        if (!cachedAreaLists) player.Area.SyncLivingMonsters();
        var summons = player.Area.LivingSummons;
        int count = 0;
        for (int i = 0; i < summons.Count; i++)
        {
            var m = summons[i].Monster;
            if (m.SummonerId == summoner && !m.Dead && m.Life > 0 && m.Variety?.Row == summon.Variety) count++;
        }
        return count;
    }

    static List<uint> SurplusMinionsLocked(Player player, uint summoner, SkillCatalog.MonsterSummonInfo summon, int incoming)
    {
        if (summon.Limit <= 0) return [];
        var living = player.Area.Monsters.Where(p => p.Value.SummonerId == summoner && !p.Value.Dead && p.Value.Life > 0
            && p.Value.Variety?.Row == summon.Variety).Select(p => p.Key).Order().ToList();
        int surplus = living.Count + Math.Min(incoming, summon.Limit) - summon.Limit;
        return surplus > 0 ? living.GetRange(0, Math.Min(surplus, living.Count)) : [];
    }

    /// <summary>Takes summoned monsters out of the area (as a player's expired minion is: Golems.RemoveGolemLocked).</summary>
    static async Task RetireSummonedMonsters(Player player,
        IReadOnlyCollection<uint> ids, string why)
    {
        List<uint> gone = [];
        foreach (uint id in ids)
            if (player.Area.Monsters.Remove(id, out var m))
            {
                m.Dead = true; m.Life = 0; m.ActionGeneration++;
                player.Instance.World.MonsterOrigins.TryRemove(id, out _);
                gone.Add(id);
            }
        foreach (uint id in gone)
            await SendWorldRemoval(player, id, $"0x0013 summoned monster 0x{id:x} {why}");
    }

    /// <summary>The summoned monsters whose duration has run out leave (MonsterSummonInfo.Lifetime).</summary>
    static Task ExpireSummonedMonsters(Player player)
    {
        uint[] expired;
        double now = AreaTime(player);
        expired = [.. player.Area.Monsters.Where(p => p.Value.ExpiresAt <= now && !p.Value.Dead).Select(p => p.Key)];
        return expired.Length == 0 ? Task.CompletedTask
            : RetireSummonedMonsters(player, expired, "expires");
    }
}
