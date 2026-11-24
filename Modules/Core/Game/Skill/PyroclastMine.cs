using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    public static bool IsPyroclastMine(string? effect) =>
        effect is "PyroclastMine" or "PyroclastMineAltX" or "PyroclastMineAltY";

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

    sealed record PyroclastAura(GameArea Area, Player Owner, PlacedSkills.Placed Mine, Zone Zone, int Exposure);
    static readonly RuleTable<GameArea, Dictionary<uint, PyroclastAura>> PyroclastAuras = new();
    static readonly RuleTable<SpawnedMonster, Dictionary<uint, PyroclastAura>> PyroclastRecipients = new();

    static void KeepPyroclastAura(Player player, PlacedSkills.Placed mine)
    {
        var area = player.Area;
        if (PyroclastAuras.TryGetValue(area, out var current) && current.ContainsKey(mine.Id)) return;
        int exposure = mine.Own.GetValueOrDefault(Stat.PyroclastMineAuraFireExposurePctToApply);
        if (exposure >= 0) return;
        var stats = CharacterPlayerStats(player);
        int Get(uint key) => GolemStat(stats, key) + GolemStat(mine.Own, key);
        double increased = (double)Get(Stat.AuraEffectPlusPct) + Get(Stat.NonCurseAuraEffectPlusPct)
            + Get(Stat.NonCurseAuraEffectPlusPctVsEnemies) + Get(Stat.MineAuraEffectPlusPct)
            + Get(Stat.StormblastIciclePyroclastMineAuraEffectPlusPct);
        exposure = InflictedExposure(Get, DamageType.Fire, (int)Math.Clamp(Math.Truncate(exposure
            * Math.Max(0, 1 + increased / 100)), int.MinValue, 0));
        if (exposure >= 0) return;

        var scaling = SkillAreaStats(player, mine.Own, mine.Effect, stats);
        int radius = SkillArea.Radius(35, key => scaling.GetValueOrDefault(key)
            + (key == SkillArea.Increase ? player.Pools.AuraAreaPct : 0), spell: true);
        PyroclastAura? aura = null;
        var zone = new Zone
        {
            At = mine.At,
            Radius = () => radius,
            Reach = id => area.Monsters.GetValueOrDefault(id.Value) is
                { Hostile: true, Dead: false, Life: > 0, OwnerId: null } monster && monster.Mods.Effects[Stat.ImmuneToExposure] == 0
                ? 0 : null,
            Entered = id =>
            {
                if (area.Monsters.GetValueOrDefault(id.Value) is { } target)
                    PyroclastRecipients.GetOrCreateValue(target)[mine.Id] = aura!;
            },
            Left = id => DropRecipient(area, mine.Id, id),
        };
        aura = new(area, player, mine, zone, exposure);
        (current ?? PyroclastAuras.GetOrCreateValue(area)).Add(mine.Id, aura);
        area.Zones.Add(zone, CombatLifetime);
    }

    static void DropRecipient(GameArea area, uint mineId, EntityId id)
    {
        if (area.Monsters.GetValueOrDefault(id.Value) is { } target
            && PyroclastRecipients.TryGetValue(target, out var sources))
        {
            sources.Remove(mineId);
            if (sources.Count == 0) PyroclastRecipients.Remove(target);
        }
    }

    static void RemovePyroclastAura(GameArea area, uint mineId)
    {
        if (!PyroclastAuras.TryGetValue(area, out var active) || !active.Remove(mineId, out var aura)) return;
        foreach (var id in aura.Zone.Inside) DropRecipient(area, mineId, id);
        area.Zones.Remove(aura.Zone);
        if (active.Count == 0) PyroclastAuras.Remove(area);
    }

    public static void ClearPyroclastAuras(Player player)
    {
        var area = player.Area;
        void Clear(GameArea oldArea)
        {
            if (!PyroclastAuras.TryGetValue(oldArea, out var active)) return;
            foreach (var (id, _) in active.Where(pair => ReferenceEquals(pair.Value.Owner, player)).ToArray())
                RemovePyroclastAura(oldArea, id);
        }

        if (area.Driven && !area.Owned) area.Defer(Clear);
        else Clear(area);
    }

    static int PyroclastExposure(SpawnedMonster monster, double now)
    {
        if (!PyroclastRecipients.TryGetValue(monster, out var auras)) return 0;
        int exposure = 0;
        foreach (var aura in auras.Values)
            if (aura.Mine.Until > now && aura.Owner is { PlayerLife: > 0, Closed: false, LeavingArea: false }
                && ReferenceEquals(aura.Owner.Area, aura.Area)
                && aura.Owner.Placed.All.GetValueOrDefault(aura.Mine.Id) == aura.Mine)
                exposure = Math.Min(exposure, aura.Exposure);
        return exposure;
    }
}
