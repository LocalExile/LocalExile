using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// The spells an essence monster casts. Each essence's display mod (Essences.Display_Monster_ModsKey) names a daemon
/// variety through Mods.MonsterMetadata (Metadata/Monsters/Daemon/EssenceDaemon*); the daemon's granted effects are the
/// spells. Only the released rare monster casts them — essence modifiers can only appear on rare or unique monsters (the
/// wiki), and the pack's magic monsters cast nothing. The spells go into the rare's kit as specials with the skill's own
/// cooldown, so the ordinary monster AI rotates them with its default attack (BossKits.Choose/Consume) rather than
/// spamming: a summoning spell brings its monsters on its cooldown, a pulse hits on its cooldown.
/// <para>Essence behaviour that a monster carries lives here, one file per essence, beside EssenceContempt,
/// EssenceEnvyAura and EssenceDeathEffects: the
/// kit and the periodic caster (EssenceSpells), each essence's own spell, the buffs on the rare and the tier scaling. The
/// encounter itself — the monolith, its panel, the encased monsters, corruption, the atlas and the drops — is
/// World/Essence/.</para>
/// </summary>
public static partial class MapEssences
{
    /// <summary>Every daemon an essence names, with that variety's granted effects. The display mod
    /// (Essences.Display_Monster_ModsKey) names one through Mods.MonsterMetadata, and so can any of the essence's
    /// Monster_ModsKeys: Anguish's meteor Firestorm rides EssenceDaemonMeteorFirestorm and Insanity's spectral spirits ride
    /// SpiritMinionEssenceMonster1, both monster mods, while their display mods name no daemon at all. Reading only the
    /// display mod left those two essences casting nothing. Contempt's Karui monster mod is here too; the kit defers to
    /// Monster/Mods/EssenceContempt.cs for it.</summary>
    static IReadOnlyList<(string Daemon, int[] Effects)> DaemonEffects(int essenceRow)
    {
        if (ClientTables.TableOrEmpty("Essences").Row(essenceRow) is not { } essence) return [];
        var mods = ClientTables.TableOrEmpty("Mods");
        var rows = new List<int>();
        if (essence.IntOrNull("Display_Monster_ModsKey") is int display) rows.Add(display);
        rows.AddRange(essence.Ints("Monster_ModsKeys"));
        var list = new List<(string, int[])>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (int row in rows)
        {
            if (mods.Row(row) is not { } mod || mod.TextOrNull("MonsterMetadata") is not { Length: > 0 } daemon
                || !seen.Add(daemon) || Monsters.Find(daemon) is not { Supported: true } variety) continue;
            if (variety.GrantedEffects is { Count: > 0 } effects) list.Add((daemon, [.. effects]));
        }
        return list;
    }

    /// <summary>The released rare monster's essence spells as kit specials (effect, attack, cooldown): the daemon
    /// effects of its essences, each built as the monster's own action at its level, with the skill's cooldown. Effects
    /// that hit for nothing, summon nothing and fire nothing are dropped, as a boss kit's are; the essence pulses/grab
    /// are cast separately by <see cref="EssenceSpellContact"/> on their own timer, not through the kit (a non-hit kit
    /// special fights the auto-attack and stalls the rare).</summary>
    public static IReadOnlyList<MonsterKitSkill> EssenceKit(ClientState conn, MonsterCatalog.Variety variety, byte level,
        IReadOnlyList<int> essences, MonsterModSet mods)
    {
        var catalog = SkillsFor(conn);
        var kit = new List<MonsterKitSkill>();
        var seen = new HashSet<int>();
        // A summon another essence consumer already drives is not cast twice. Contempt's chasing spirits come from the
        // EssenceDaemonSummonMovingKaruiSpirit1 monster mod, which Monster/Mods/EssenceContempt.cs casts on its own cycle
        // with its own cap, defences and parent lifecycle; that mod appears from tier 5 up. While it is on the rare, the
        // display daemon's own Karui summon stays out of the kit — below tier 5 the mod is absent and the kit is the only
        // source, so the low tiers still summon.
        bool karuiOwned = Items.Mods.ById(EssenceKaruiSummonMod) is { } karui && mods.Mods.Any(m => m.Hash == karui.Hash);
        foreach (int row in essences)
        {
            int tier = MapEssenceCatalog.Find(row)?.Tier ?? 7;
            foreach (var daemon in DaemonEffects(row))
            foreach (int effect in daemon.Effects)
            {
                if (!seen.Add(effect)) continue;
                int effectLevel = catalog.MaxLevelAt(effect, level);
                var native = GolemData.Skill(catalog, effect, level);
                int Get(string name) => Stats.Key(name) is uint key ? native.Stats.GetValueOrDefault(key) : 0;
                // A triggered spell (cast_when_triggered_by_other_skill) is fired by the essence's pulse, not a standalone
                // kit special — the periodic caster plays it (EssenceSpells). Leaving it in the kit makes the kit all
                // cooldown specials, and BossKits.Choose then has no chase attack between their cooldowns, freezing the
                // rare in place; the rare keeps its default attack and fights normally instead.
                if (Get("cast_when_triggered_by_other_skill_%") != 0) continue;
                // A spell the periodic caster handles on its own timer (a pulse, the grab, a rune cluster, a frost bomb) is
                // not a swung hit: it stays out of the hit kit so it is not cast twice.
                if (EssenceSpellKind(catalog.EffectId(effect)).Length > 0) continue;
                double cooldown = catalog.CooldownMs(effect, effectLevel) / 1000d;
                ushort id = (ushort)(0x4000 + kit.Count);
                var attack = BossKits.AttackFor(conn, variety, level, effect, id);
                if (attack.Damage.IsZero && attack.Summon is null && attack.Projectile is null) continue;
                if (karuiOwned && attack.Summon is not null) continue;
                // An essence spell is cast alongside the rare's own fighting, not instead of its positioning. A spell's
                // AttackFor reach is the variety's aggro range (60+), and MonsterSurround stands a ranged chaser off at
                // reach - 1 — so a long-reach essence spell in the kit made the rare back away to its aggro edge instead
                // of closing in. The spell keeps the rare's own attack reach, which is where it fights from.
                if (attack.Range > variety.AttackRange) attack = attack with { Range = variety.AttackRange };
                // The summon count scales with the essence tier (the tier-7 observation is the maximum).
                if (attack.Summon is { } summon)
                    attack = attack with { Summon = summon with { Count = ScaledSpellCount(effect, tier, summon.Count) } };
                kit.Add(new(effect, attack, Math.Max(0, cooldown)));
            }
        }
        return kit;
    }
}
