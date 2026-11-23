using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Shared ground-effect zones and level-scaled spell helpers for the essence spells (Sorrow's Vortex, Spite's
/// shocked ground, Hysteria's burning ground, Horror's Maelström chilled ground and Delirium's caustic ground). Non-stacking
/// per damage type, ticking through <see cref="ArenaHazards.Ground"/> and the shared player ailment path.</summary>
public static partial class MapEssences
{
    const int MaxGroundZonesPerArea = 64;

    sealed record GroundZone((int X, int Y) At, double Radius, double Until, uint Art, DamagePacket Dot,
        PlayerElementalAilments.Kind? Ailment = null, int AilmentMagnitude = 0);

    /// <summary>The area's shared essence ground zones, per instance: what the old area state kept, read by every
    /// player's tick.</summary>
    static readonly Store<InstanceState, List<GroundZone>> GroundZones = new();

    static readonly RuleTable<Player, ArenaHazards.Exposure> GroundExposures = new();

    /// <summary>Level-scaled spell hit damage for a monster granted effect, reading <see cref="GolemCatalog.Skill"/> so
    /// single-level monster skills scale by actor level rather than staying at level 1.</summary>
    static DamagePacket EssenceSkillDamage(Player conn, int effect, byte level)
    {
        if (effect < 0) return DamagePacket.Empty;
        var native = GolemData.Skill(SkillsFor(conn), effect, level);
        int Get(string name) => Stats.Key(name) is uint key ? native.Stats.GetValueOrDefault(key) : 0;
        var damage = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            int min = Get($"spell_minimum_base_{slug}_damage"), max = Get($"spell_maximum_base_{slug}_damage");
            if (max > 0) damage = damage.Add(type, new(min, max));
        }
        return damage;
    }

    /// <summary>Level-scaled spell damage over time (per second) for a monster ground-effect skill.</summary>
    static DamagePacket EssenceSkillDot(Player conn, int effect, byte level)
    {
        if (effect < 0) return DamagePacket.Empty;
        var native = GolemData.Skill(SkillsFor(conn), effect, level);
        int Get(string name) => Stats.Key(name) is uint key ? native.Stats.GetValueOrDefault(key) : 0;
        var dot = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            double perSec = Get($"base_{slug}_damage_to_deal_per_minute") / 60d;
            if (perSec > 0) dot = dot.Add(type, new(perSec, perSec));
        }
        double caustic = Get("base_projectile_ground_caustic_cloud_damage_per_minute") / 60d;
        if (caustic > 0) dot = dot.Add(DamageType.Chaos, new(caustic, caustic));
        return dot;
    }

    /// <summary>Lands a spell area hit from <paramref name="monster"/> through <see cref="ArenaHazards.Hit"/> so players
    /// and minions use their full ordinary defences, ailments and poison rules.</summary>
    static async Task EssenceSpellHit(Player conn, SpawnedMonster monster, int effect, DamagePacket rawDamage,
        double radius, (int X, int Y) spot, int poisonChance = 0, double poisonSeconds = 0)
    {
        if (rawDamage.IsZero || conn.Closed || conn.LeavingArea) return;
        var nativeStats = effect >= 0 ? GolemData.Skill(SkillsFor(conn), effect, monster.Level).Stats : null;
        int Get(uint key) => nativeStats is not null ? nativeStats.GetValueOrDefault(key) : 0;
        // A skill's own poison (Desolation's boulder: 100%) goes on the hit beside the monster's mods.
        var attack = new MonsterAttack(0x4000, 1, radius, monster.Mods.Effects.Hit(rawDamage))
        {
            Spell = true,
            Ailments = IncomingAilmentSettings(Get),
            PoisonChance = poisonChance,
            PoisonSeconds = poisonSeconds,
        };
        var instance = conn.Instance;
        foreach (var member in Members(conn).ToArray())
            await ArenaHazards.Hit(member, monster.ObjectId, monster, attack,
                at => Dist(at, spot) <= radius,
                () => CurrentGameWork && ReferenceEquals(member.Instance, instance));
    }

    static void AddGroundZone(Player conn, Area area, (int X, int Y) at, string art, double radius, double duration,
        DamagePacket dot, PlayerElementalAilments.Kind? ailment = null, int ailmentMagnitude = 0, float scale = 1f)
    {
        if (conn.Closed || conn.LeavingArea || duration <= 0) return;
        var spot = conn.Layout.Near(at);
        uint id = conn.TakeObjectId();
        _ = SendWorldCreate(conn, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(spot.X, spot.Y, art) { Flags0 = EffectObject.FixedRotation, Scale = scale }.Blob()), null,
            afterCreation: () => [InstanceProtocol.EnterWakeZone(id)]);
        var zones = GroundZones.GetOrCreateValue(conn.Instance);
        uint evicted = 0;
        lock (zones)
        {
            if (zones.Count >= MaxGroundZonesPerArea)
            {
                evicted = zones[0].Art;
                zones.RemoveAt(0);
            }
            zones.Add(new(spot, radius, AreaTime(conn) + duration, id, dot, ailment, ailmentMagnitude));
        }
        if (evicted != 0) RemoveEffect(conn, evicted);
    }

    static DamagePacket GroundZonesDotAt(GroundZone[] zones, (int X, int Y) pos)
    {
        double phys = 0, fire = 0, cold = 0, light = 0, chaos = 0;
        foreach (var z in zones)
        {
            if (z.Dot.IsZero || Dist(pos, z.At) > z.Radius) continue;
            phys = Math.Max(phys, z.Dot[DamageType.Physical].Min);
            fire = Math.Max(fire, z.Dot[DamageType.Fire].Min);
            cold = Math.Max(cold, z.Dot[DamageType.Cold].Min);
            light = Math.Max(light, z.Dot[DamageType.Lightning].Min);
            chaos = Math.Max(chaos, z.Dot[DamageType.Chaos].Min);
        }
        var total = DamagePacket.Empty;
        if (phys > 0) total = total.Add(DamageType.Physical, new(phys, phys));
        if (fire > 0) total = total.Add(DamageType.Fire, new(fire, fire));
        if (cold > 0) total = total.Add(DamageType.Cold, new(cold, cold));
        if (light > 0) total = total.Add(DamageType.Lightning, new(light, light));
        if (chaos > 0) total = total.Add(DamageType.Chaos, new(chaos, chaos));
        return total;
    }

    static async Task TickGroundZones(Player conn)
    {
        if (conn.Closed || conn.LeavingArea) return;
        double now = AreaTime(conn);
        var ground = GroundZones.GetOrCreateValue(conn.Instance);
        GroundZone[] zones;
        List<uint>? goneArts = null;
        lock (ground)
        {
            for (int i = ground.Count - 1; i >= 0; i--)
            {
                if (ground[i].Until > now) continue;
                (goneArts ??= []).Add(ground[i].Art);
                ground.RemoveAt(i);
            }
            if (ground.Count == 0 && goneArts is null) return;
            zones = ground.Count == 0 ? [] : [.. ground];
        }
        if (goneArts is not null)
            foreach (uint art in goneArts) RemoveEffect(conn, art);
        if (zones.Length == 0) return;
        double at = AreaTime(conn);
        var instance = conn.Instance;
        if (zones.Any(z => !z.Dot.IsZero))
        {
            var exposure = GroundExposures.GetOrCreateValue(conn);
            await ArenaHazards.Ground(conn, exposure, at, (pos, _) => GroundZonesDotAt(zones, pos),
                () => CurrentGameWork && ReferenceEquals(conn.Instance, instance));
        }
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || conn.Paused || conn.Immune || InGracePeriod(conn)) return;
        var here = conn.Walk.Position(at);
        int chill = 0, shock = 0;
        foreach (var z in zones)
        {
            if (z.Ailment is null || z.AilmentMagnitude <= 0 || Dist(here, z.At) > z.Radius) continue;
            if (z.Ailment == PlayerElementalAilments.Kind.Chill) chill = Math.Max(chill, z.AilmentMagnitude);
            else if (z.Ailment == PlayerElementalAilments.Kind.Shock) shock = Math.Max(shock, z.AilmentMagnitude);
        }
        if (chill == 0 && shock == 0) return;
        bool needChill = chill > 0 && conn.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Chill, at) < chill;
        bool needShock = shock > 0 && conn.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Shock, at) < shock;
        if (!needChill && !needShock) return;
        var stats = CharacterPlayerStats(conn);
        int Stat(uint key) => stats.GetValueOrDefault(key);
        ApplyPlayerAilmentsLocked(conn, at, a =>
        {
            if (needChill) a.Inflict(PlayerElementalAilments.Kind.Chill, chill, .35, at, Stat, conn.IncomingHitRandom);
            if (needShock) a.Inflict(PlayerElementalAilments.Kind.Shock, shock, .35, at, Stat, conn.IncomingHitRandom);
        });
    }
}