using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

static partial class Sirus
{
    static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    internal const string Area = "AtlasExilesBoss5", Root = "Metadata/Monsters/AtlasExiles/",
        Body = Root + "AtlasExile5", Throne = Root + "AtlasExile5Throne", Apparition = Root + "AtlasExile5ApparitionDetached",
        Storm = "Metadata/Monsters/InvisibleFire/InvisibleFireOrionDeathZone",
        StationaryStorm = "Metadata/Monsters/InvisibleFire/InvisibleFireOrionDeathZoneStationary";
    const string Art = "Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/";
    internal static bool IsActor(MonsterCatalog.Variety? v) => v?.Id is Body or Throne or Apparition or Storm or StationaryStorm or Kirac or Zana;
    internal static LifePhaseGate? Phases(MonsterCatalog.Variety v) => v.Id == Body ? new([75, 50, 25]) : null;
    internal static StateMachineData? StartStates(MonsterCatalog.Variety v) => !IsActor(v) ? null
        : v.StateMachine?.Set("boss_life_bar", 0).Set("disable_life_bar", 1);
    static readonly int[] BodyEffects = [7008, 7012, 10948, 10949, 6809, 8098, 7019, 7020, 7021, 7022, 7023,
        7024, 7025, 7026, 7027, 7028, 7029, 7030, 7031, 9851, 10147, 8100, 10950, 10951, 10952, 10953,
        10973, 9690, 10141, 10945, 8099, 10991];
    static readonly int[] ApparitionEffects = [10967, 10968, 10969, 10954, 9849, 7032, 7033, 9685, 9687, 10956, 10946, 6832, 3629];
    static readonly ConditionalWeakTable<SkillCatalog, ConcurrentDictionary<(int, byte), (int Level, Dictionary<uint, int> Stats, double Multiplier, int Crit)>> Native = new();
    static (int Level, Dictionary<uint, int> Stats, double Multiplier, int Crit) Data(ClientState c, int effect, byte level) =>
        Native.GetValue(Game.SkillsFor(c), _ => new()).GetOrAdd((effect, level), p => GolemData.Skill(Game.SkillsFor(c), p.Item1, p.Item2));
    static int Stat(ClientState c, int effect, byte level, string name) => Stats.Key(name) is uint key ? Data(c, effect, level).Stats.GetValueOrDefault(key) : 0;
    static int[] Effects(MonsterCatalog.Variety v) => v.Id == Body ? BodyEffects : v.Id == Apparition ? ApparitionEffects : [];
    static ushort Skill(MonsterCatalog.Variety v, int effect) => (ushort)(0x4000 + Array.IndexOf(Effects(v), effect));
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState c, MonsterCatalog.Variety v, byte level) =>
        !IsActor(v) || Effects(v).Length == 0 ? null : [.. Effects(v).Select(e => Game.SkillsFor(c).FindByRow(Skill(v, e), e,
            Data(c, e, level).Level, WithBehaviourVariation(c, e, Attack(c, v, level, e).InPlaceSkillStats()))).OfType<GrantedSkill>()];
    internal static MonsterAttack Attack(ClientState c, MonsterCatalog.Variety v, byte level, int effect)
    {
        var damage = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            int min = Stat(c, effect, level, $"spell_minimum_base_{slug}_damage"), max = Stat(c, effect, level, $"spell_maximum_base_{slug}_damage");
            if (max == 0) { min = Stat(c, effect, level, $"minimum_base_{slug}_damage"); max = Stat(c, effect, level, $"maximum_base_{slug}_damage"); }
            if (max > 0) damage = damage.Add(type, new(min, max));
        }
        var physical = damage[DamageType.Physical];
        double remaining = 1;
        var converted = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder.Where(t => t != DamageType.Physical))
        {
            string slug = type.ToString().ToLowerInvariant();
            double fraction = Math.Min(remaining, Math.Max(0, Stat(c, effect, level, "skill_physical_damage_%_to_convert_to_" + slug) / 100d));
            converted = converted.Add(type, damage[type]).Add(type, physical.Scale(fraction)); remaining -= fraction;
        }
        damage = converted.Add(DamageType.Physical, physical.Scale(remaining));
        return new(Skill(v, effect), Math.Max(.1, Game.SkillsFor(c).CastTimeMs(effect) / 1000d), 200, damage) { Spell = true, Scripted = true };
    }

    // The monster set, not the throne or the invulnerable projection, owns the encounter reward.
    internal static IEnumerable<ItemDrops.Drop> Rewards(ClientState c, SpawnedMonster m)
    {
        if (c.Layout.Id != Area || m.Variety?.Id != Body || !m.Dead || m.LifePhases?.Completed != 3
            || !c.Instance.SetState(Flag("Rewards"))) yield break;
        // Server weights are deliberately explicit: GGG does not publish the unique drop weights.
        int roll = Random.Shared.Next(100);
        string name = roll < 40 ? "Hands of the High Templar" : roll < 75 ? "Crown of the Inward Eye"
            : roll < 95 ? "The Burden of Truth" : "Thread of Hope";
        var unique = UniquesFor(c).Find(name);
        if (unique is not null && UniquesFor(c).Create(unique, Random.Shared) is { } item && ItemsFor(c).ByHash(item.TypeHash) is { } type)
            yield return new(item with { ItemLevel = m.Level }, type);
        foreach (var (query, chance) in new[] { ("Awakener's Orb", 15), ("Orb of Dominance", 10), ("A Fate Worse Than Death", 4) })
            if (Random.Shared.Next(100) < chance && ModDrop(c, query, m.Level) is { } drop) yield return drop;
    }
}
