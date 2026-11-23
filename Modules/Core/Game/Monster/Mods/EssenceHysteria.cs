using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Hysteria's Flameblast (<c>EssenceDaemonVaalFlameblast</c>), 8-projectile Fireball burst
/// (<c>EssenceFireballBurst</c>) and periodic Burning Ground patches (<c>FireElementalMeteorEssence</c> / Hysteria rare
/// and its Meteor Golems).</summary>
public static partial class MapEssences
{
    const string HysteriaFlameblastTargetArt = "Metadata/Effects/Spells/monsters_effects/flameblast/flameblast_target.ao";
    const string HysteriaFlameblastExplodeArt = "Metadata/Effects/Spells/monsters_effects/flameblast/flameblast_explode_large.ao";
    const string HysteriaFireballProjectile = "Metadata/Projectiles/Fireball";
    const string HysteriaBurningGroundArt = "Metadata/Effects/Spells/ground_effects/fire/fire1.ao";
    const string HysteriaMeteorGolem = "Metadata/Monsters/FireElemental/EssenceFireElementalMeteor";
    const int HysteriaEssenceType = 20;

    sealed class BurningGroundState { public double NextPatch; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpawnedMonster, BurningGroundState> HysteriaGround = new();

    static async Task CastFlameblast(Player conn, SpawnedMonster monster, Player target, SpecialSpell spell)
    {
        var catalog = SkillsFor(conn);
        var raw = EssenceSkillDamage(conn, spell.Pulse, monster.Level);
        if (raw.IsZero) return;
        var native = GolemData.Skill(catalog, spell.Pulse, monster.Level);
        int Get(string name) => Stats.Key(name) is uint key ? native.Stats.GetValueOrDefault(key) : 0;
        int perStack = Math.Max(0, Get("charged_blast_spell_damage_+%_final_per_stack"));
        var damage = raw.Scale(1 + perStack * 2 / 100d);
        var spot = target.Walk.Position(AreaTime(target));
        uint circle = SpawnEffect(conn, spot, HysteriaFlameblastTargetArt, 1.4f);
        await Task.Delay(TimeSpan.FromSeconds(1.6));
        RemoveEffect(conn, circle);
        if (conn.Closed || conn.LeavingArea) return;
        uint boom = SpawnEffect(conn, spot, HysteriaFlameblastExplodeArt, 1.4f);
        _ = RemovePulseLater(conn, boom, 1.0);
        await EssenceSpellHit(conn, monster, spell.Pulse, damage, radius: 16, spot);
        Console.WriteLine($"[map-content] essence hysteria flameblast by 0x{monster.ObjectId:x} at ({spot.X}, {spot.Y})");
    }

    static Task CastFireballBurst(Player conn, SpawnedMonster monster, Player target, SpecialSpell spell)
    {
        if (monster.Dead || monster.Life == 0 || monster.Variety is null) return Task.CompletedTask;
        var catalog = SkillsFor(conn);
        var damage = EssenceSkillDamage(conn, spell.Pulse, monster.Level);
        if (damage.IsZero) return Task.CompletedTask;
        int count = ScaledSpellCount(spell.Pulse, spell.Tier, 8);
        var native = GolemData.Skill(catalog, spell.Pulse, monster.Level);
        int Get(uint key) => native.Stats.GetValueOrDefault(key);
        int speed = catalog.Projectile(HysteriaFireballProjectile)?.Speed ?? 1040;
        var projectile = new SkillCatalog.MonsterProjectileInfo(HysteriaFireballProjectile, speed, false, 1)
        {
            Count = count,
            SpreadRadians = Math.PI * 1.25,
            AnimationKey = "SpellProjectile",
        };
        var attack = new MonsterAttack(0x4000, 1, 70, monster.Mods.Effects.Hit(damage))
        {
            Spell = true,
            Ranged = true,
            Ailments = IncomingAilmentSettings(Get),
            Projectile = projectile,
        };
        var session = ProjectilesFor(conn);
        var aim = target.Walk.Position(AreaTime(target));
        var use = MonsterProjectileUse(session, monster.ObjectId, monster, attack, projectile, monster.ActionGeneration, orderedAim: aim);
        session.Schedule(ProjectileRuntime.Time(ProjectileWorldTime(conn)),
            () => !monster.Dead && monster.Life > 0 && !conn.Closed, () => use);
        Console.WriteLine($"[map-content] essence hysteria fireball burst: {count} shots from 0x{monster.ObjectId:x}");
        return Task.CompletedTask;
    }

    static void TickHysteriaBurningGround(Player conn)
    {
        if (conn.Closed || conn.LeavingArea || !Areas.TryGetValue(conn.Instance, out var area) || area.Monoliths is null) return;
        var rares = new HashSet<uint>();
        foreach (var mono in area.Monoliths)
        {
            if (mono.Stage < 4 || mono.Monster == 0) continue;
            foreach (int row in mono.Essences)
                if (MapEssenceCatalog.Find(row)?.Type == HysteriaEssenceType) { rares.Add(mono.Monster); break; }
        }
        if (rares.Count == 0) return;
        double now = Now;
        double areaTime = AreaTime(conn);
        List<SpawnedMonster> sources = [];
        foreach (uint id in rares)
            if (conn.Area.Monsters.TryGetValue(id, out var rare) && !rare.Dead && rare.Life > 0 && rare.Aggro)
                sources.Add(rare);
        foreach (var m in conn.Area.Monsters.Values)
            if (!m.Dead && m.Life > 0 && m.SummonerId is uint s && rares.Contains(s) && m.Variety?.Id == HysteriaMeteorGolem)
                sources.Add(m);
        if (sources.Count == 0) return;
        int effect = SkillsFor(conn).EffectRow("FireElementalMeteorEssence");
        foreach (var src in sources)
        {
            var state = HysteriaGround.GetOrCreateValue(src);
            if (now < state.NextPatch) continue;
            state.NextPatch = now + 2.5;
            var dot = EssenceSkillDot(conn, effect, src.Level);
            if (dot.IsZero) continue;
            AddGroundZone(conn, area, src.Walk.Position(areaTime), HysteriaBurningGroundArt, radius: 10, duration: 4.5, dot);
        }
    }
}