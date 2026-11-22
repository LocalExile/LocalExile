using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>The resistance novas — Hatred (cold), Anger (fire), Wrath (lightning): a ring of the essence's projectiles fired
/// through the monster projectile runtime, plus the resistance debuff the pulse applies. Kept apart from the grab, the chaos
/// pulse and the chilling trail so a change to one cannot disturb the others.</summary>
public static partial class MapEssences
{
    /// <summary>The lightning impact a Wrath shell bursts into where it lands (LightningMortarImpact, the lightning
    /// molten-projectile impact art).</summary>
    const string WrathImpactArt = "Metadata/Effects/Spells/monsters_effects/molten_projectile_small/lightning/moltenimpact_small.ao";

    /// <summary>The resistance pulse's nova: a ring of the essence's projectiles fired through the monster projectile
    /// runtime (the Rampage nova pattern), so the client draws them and the server simulates their hits on the player. A
    /// daemon action alone drew no projectiles — the runtime creates the flying objects. The shot is the rare's own (it
    /// originates from the rare's live position, not the pulse daemon's): Hatred fires Creeping Frost (ArcticBreath) and
    /// leaves a chilling trail, Anger fires the fire Creeping Frost (FlameBreath) with its fire damage, and Wrath
    /// is a lobbed lightning mortar the runtime lands in a ring around the rare.</summary>
    static void FireNova(ClientState conn, SpawnedMonster monster, SpecialSpell spell)
    {
        if (monster.Dead || monster.Life <= 0 || monster.Variety is null) return;
        var catalog = SkillsFor(conn);
        var stats = catalog.EffectStats(spell.Effect, catalog.MaxLevelAt(spell.Effect, monster.Level)).ToDictionary(s => s.Item1, s => s.Item2);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        int count = ScaledSpellCount(spell.Effect, spell.Tier, Get("number_of_additional_projectiles") + 1);
        SkillCatalog.MonsterProjectileInfo projectile;
        if (catalog.MonsterProjectile(spell.Effect, includeWeaponAttacks: true) is { } mortar)
            // Wrath: a lightning mortar (MortarLightning, the lightning molten-projectile rig), lobbed. A full-turn spread
            // lands each shell its landing distance out in its own direction — a ring of explosions around the rare. The
            // row's speed 750 crossed the short landing in ~0.14 s (too fast to see), so the shell is slowed to a visible
            // arc; its landing distance is 280 (the 400 it was widened to, less the 30% the retail ring is tighter by).
            projectile = mortar with { Metadata = "Metadata/Projectiles/MortarLightning", Count = count,
                SpreadRadians = Math.PI * 2, Speed = 300, MinimumRange = 280 };
        else
        {
            // Hatred's Creeping Frost (ArcticBreath), or Anger's fire Creeping Frost (FlameBreath — the fire arctic breath
            // its EssenceTriggeredFlameBreath effect names).
            string projId = spell.Kind == "fire" ? "Metadata/Projectiles/FlameBreath" : "Metadata/Projectiles/ArcticBreath";
            if (catalog.Projectile(projId) is not { } meta) return;
            projectile = new SkillCatalog.MonsterProjectileInfo(meta.Id, meta.Speed, false, 1)
                { Count = count, SpreadRadians = Math.PI * 2 };
        }
        var attack = BossKits.AttackFor(conn, monster.Variety, (byte)AreaLevel(conn), spell.Effect, 0x4000);
        var session = ProjectilesFor(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn);
        var use = MonsterProjectileUse(session, monster.ObjectId, monster, attack, projectile, monster.ActionGeneration);
        // Each Wrath shell bursts into a lightning impact where it lands (the MortarLightning rig carries no impact clip of
        // its own). The landing is the projectile's Area event, as every other impact art hooks (ProjectileSkills): End
        // fires when the projectile object is retired, after the landing, which read as a late explosion. The impact art
        // plays for 1.4 s — 0.7 s cut it off mid-burst.
        if (spell.Kind == "lightning" && use.Payload is HostileProjectileOffense hostile)
        {
            var presentation = (hostile.Presentation ?? new ProjectilePresentation()) with
            {
                EventEffect = e => e.Kind == ProjectileEventKind.Area ? WrathImpactArt : null,
                EffectAnimatedFlags = 8,
                EffectLifetime = ProjectileRuntime.Time(1.4),
            };
            use = use with { Payload = hostile with { Presentation = presentation } };
        }
        session.Schedule(ProjectileRuntime.Time(ProjectileWorldTime(conn)), () => !monster.Dead && monster.Life > 0 && !conn.Closed, () => use);
        if (spell.Kind == "cold") FrostTrail(conn, monster, count);
        Console.WriteLine($"[map-content] essence {spell.Kind} nova: {count} {projectile.Metadata}{(projectile.Lobbed ? " (lobbed)" : "")} from rare 0x{monster.ObjectId:x}");
    }

    /// <summary>The resistance pulse: -10% of its element on the players near the rare for the pulse's duration. The same
    /// element's debuff refreshes rather than stacks; the buff (746) is invisible, so the debuff is the stat alone.</summary>
    static async Task ResDebuff(ClientState source, (int X, int Y) at, double range, SpecialSpell spell)
    {
        string statName = spell.Kind switch
        {
            "cold" => "base_cold_damage_resistance_%",
            "fire" => "base_fire_damage_resistance_%",
            _ => "base_lightning_damage_resistance_%",
        };
        if (Stats.Key(statName) is not uint key || spell.Value == 0) return;
        double seconds = Math.Max(1, spell.Duration);
        foreach (var conn in PlayersNear(source, at, range).ToArray())
        {
            if (conn.Immune || InGracePeriod(conn)) continue;
            var effects = Effects.GetOrCreateValue(conn);
            ushort buff = conn.NextBuff++;
            lock (effects)
            {
                effects.Debuffs.RemoveAll(d => d.Until <= Now || d.Key == key);
                effects.Debuffs.Add((new Dictionary<uint, int> { [key] = spell.Value }, Now + seconds, buff, key));
            }
            var (remote, stream, ciphers) = conn.Wire;
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, buff, 746,
                u68: BitConverter.SingleToUInt32Bits((float)seconds)), $"essence {spell.Kind} resistance pulse {spell.Value}%");
            await RefreshStats(conn);
        }
    }
}