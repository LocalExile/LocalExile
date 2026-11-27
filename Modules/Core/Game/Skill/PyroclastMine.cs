using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    public static bool IsPyroclastMine(string? effect) =>
        effect is "PyroclastMine" or "PyroclastMineAltX" or "PyroclastMineAltY";

    static bool IsStormblastMine(string? effect) =>
        effect is "StormblastMine" or "StormblastMineAltX" or "StormblastMineAltY";

    static ProjectileUse PyroclastMortars(Player player, PlacedSkills.Placed mine, ProjectileUse use)
    {
        var own = SkillAreaStats(player, mine.Own, mine.Effect);
        int spread = SkillArea.Radius(26, k => own.GetValueOrDefault(k), spell: true);
        int count = use.Profile.Count;
        var targets = new ProjectileVector[count];
        var aim = use.Aim - use.Origin;
        var random = new Random(unchecked((int)mine.Id));
        if (count > 0) targets[0] = use.Origin + aim.Unit * Math.Min(aim.Length, spread);
        for (int i = 1; i < count; i++)
        {
            double angle = random.NextDouble() * Math.Tau;
            double distance = Math.Sqrt(random.NextDouble()) * spread;
            targets[i] = use.Origin + new ProjectileVector(Math.Cos(angle), Math.Sin(angle)) * distance;
        }
        var profile = use.Profile with
        {
            SequentialDelay = ProjectileRuntime.Time(.06),
            ShareSequentialHits = false,
            ArcHeight = 90,
        };
        var shot = (ProjectileOffense)use.Payload!;
        var presentation = (shot.Presentation ?? new()) with
        {
            EventEffect = static e => e.Kind == ProjectileEventKind.Area
                ? "Metadata/Effects/Spells/barrage_mine/impact.ao" : null,
        };
        return use with { Profile = profile, ProjectileTargets = targets, Payload = shot with { Presentation = presentation } };
    }

    sealed record EnemyMineAura(GameArea Area, Player Owner, PlacedSkills.Placed Mine, Zone Zone, int Exposure, int DamageTaken);
    static readonly RuleTable<GameArea, Dictionary<uint, EnemyMineAura>> EnemyMineAuras = new();
    static readonly RuleTable<SpawnedMonster, Dictionary<uint, EnemyMineAura>> EnemyMineAuraRecipients = new();

    static void KeepEnemyMineAura(Player player, PlacedSkills.Placed mine, bool pyroclast)
    {
        var area = player.Area;
        if (EnemyMineAuras.TryGetValue(area, out var current) && current.ContainsKey(mine.Id)) return;
        int exposure = pyroclast ? mine.Own.GetValueOrDefault(Stat.PyroclastMineAuraFireExposurePctToApply) : 0;
        int damageTaken = pyroclast ? 0 : mine.Own.GetValueOrDefault(Stat.LightningExplosionMineAuraDamageTakenPlusPct);
        if (exposure >= 0 && damageTaken <= 0) return;
        var stats = CharacterPlayerStats(player);
        int Get(uint key) => GolemStat(stats, key) + GolemStat(mine.Own, key);
        double increased = (double)Get(Stat.AuraEffectPlusPct) + Get(Stat.NonCurseAuraEffectPlusPct)
            + Get(Stat.NonCurseAuraEffectPlusPctVsEnemies) + Get(Stat.MineAuraEffectPlusPct)
            + Get(Stat.StormblastIciclePyroclastMineAuraEffectPlusPct);
        if (pyroclast)
            exposure = InflictedExposure(Get, DamageType.Fire, (int)Math.Clamp(Math.Truncate(exposure
                * Math.Max(0, 1 + increased / 100)), int.MinValue, 0));
        else
            damageTaken = (int)Math.Clamp(Math.Truncate(damageTaken
                * Math.Max(0, 1 + (increased + Get(Stat.LightningExplosionMineAuraEffectPlusPct)) / 100)), 0, int.MaxValue);
        if (exposure >= 0 && damageTaken <= 0) return;

        var scaling = SkillAreaStats(player, mine.Own, mine.Effect, stats);
        int radius = SkillArea.Radius(35, key => scaling.GetValueOrDefault(key)
            + (key == SkillArea.Increase ? player.Pools.AuraAreaPct : 0), spell: true);
        EnemyMineAura? aura = null;
        var zone = new Zone
        {
            At = mine.At,
            Radius = () => radius,
            Reach = id => area.Monsters.GetValueOrDefault(id.Value) is
                { Hostile: true, Dead: false, Life: > 0, OwnerId: null } monster
                && (!pyroclast || monster.Mods.Effects[Stat.ImmuneToExposure] == 0) ? 0 : null,
            Entered = id =>
            {
                if (area.Monsters.GetValueOrDefault(id.Value) is { } target)
                    EnemyMineAuraRecipients.GetOrCreateValue(target)[mine.Id] = aura!;
            },
            Left = id => DropRecipient(area, mine.Id, id),
        };
        aura = new(area, player, mine, zone, exposure, damageTaken);
        (current ?? EnemyMineAuras.GetOrCreateValue(area)).Add(mine.Id, aura);
        area.Zones.Add(zone, CombatLifetime);
    }

    static void DropRecipient(GameArea area, uint mineId, EntityId id)
    {
        if (area.Monsters.GetValueOrDefault(id.Value) is { } target
            && EnemyMineAuraRecipients.TryGetValue(target, out var sources))
        {
            sources.Remove(mineId);
            if (sources.Count == 0) EnemyMineAuraRecipients.Remove(target);
        }
    }

    static void RemoveEnemyMineAura(GameArea area, uint mineId)
    {
        if (!EnemyMineAuras.TryGetValue(area, out var active) || !active.Remove(mineId, out var aura)) return;
        foreach (var id in aura.Zone.Inside) DropRecipient(area, mineId, id);
        area.Zones.Remove(aura.Zone);
        if (active.Count == 0) EnemyMineAuras.Remove(area);
    }

    public static void ClearPyroclastAuras(Player player) => ClearEnemyMineAuras(player, pyroclastOnly: true);

    internal static void ClearEnemyMineAuras(Player player, bool pyroclastOnly = false)
    {
        var area = player.Area;
        void Clear(GameArea oldArea)
        {
            if (!EnemyMineAuras.TryGetValue(oldArea, out var active)) return;
            foreach (var (id, _) in active.Where(pair => ReferenceEquals(pair.Value.Owner, player)
                && (!pyroclastOnly || pair.Value.Exposure < 0)).ToArray())
                RemoveEnemyMineAura(oldArea, id);
        }

        if (area.Driven && !area.Owned) area.Defer(Clear);
        else Clear(area);
    }

    static bool EnemyMineAuraActive(EnemyMineAura aura, double now) => aura.Mine.Armed <= now && aura.Mine.Until > now
        && aura.Owner is { PlayerLife: > 0, Closed: false, LeavingArea: false }
        && ReferenceEquals(aura.Owner.Area, aura.Area) && aura.Owner.Placed.All.GetValueOrDefault(aura.Mine.Id) == aura.Mine;

    static int PyroclastExposure(SpawnedMonster monster, double now)
    {
        if (!EnemyMineAuraRecipients.TryGetValue(monster, out var auras) || monster.Mods.Effects[Stat.ImmuneToExposure] != 0) return 0;
        int exposure = 0;
        foreach (var aura in auras.Values)
            if (aura.Exposure < exposure && EnemyMineAuraActive(aura, now)) exposure = aura.Exposure;
        return exposure;
    }

    static int StormblastDamageTaken(SpawnedMonster monster, double now)
    {
        if (double.IsNaN(now) || !EnemyMineAuraRecipients.TryGetValue(monster, out var auras)
            || monster is not { Hostile: true, Dead: false, Life: > 0, OwnerId: null }) return 0;
        int increase = 0;
        foreach (var aura in auras.Values)
            if (aura.DamageTaken > 0 && EnemyMineAuraActive(aura, now))
            {
                increase += Math.Min(150, aura.DamageTaken);
                if (increase >= 150) return 150;
            }
        return increase;
    }
}
