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
/// </summary>
public static partial class MapEssences
{
    static int AreaLevel(ClientState conn) =>
        Math.Clamp(MapRunOf(conn) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);

    /// <summary>The daemon variety an essence's display mod names and that variety's granted effects, or null when it
    /// names none (the alternating-buff essences Anguish/Loathing/Zeal/Scorn have an empty MonsterMetadata).</summary>
    static (string Daemon, int[] Effects)? DaemonEffects(int essenceRow)
    {
        if (ClientTables.TableOrEmpty("Essences").Row(essenceRow) is not { } essence
            || essence.IntOrNull("Display_Monster_ModsKey") is not int display
            || ClientTables.TableOrEmpty("Mods").Row(display) is not { } mod
            || mod.TextOrNull("MonsterMetadata") is not { Length: > 0 } daemon
            || Monsters.Find(daemon) is not { Supported: true } variety) return null;
        return variety.GrantedEffects is { Count: > 0 } effects ? (daemon, [.. effects]) : null;
    }

    /// <summary>The released rare monster's essence spells as kit specials (effect, attack, cooldown): the daemon
    /// effects of its essences, each built as the monster's own action at its level, with the skill's cooldown. Effects
    /// that hit for nothing, summon nothing and fire nothing are dropped, as a boss kit's are; the essence pulses/grab
    /// are cast separately by <see cref="EssenceSpellContact"/> on their own timer, not through the kit (a non-hit kit
    /// special fights the auto-attack and stalls the rare).</summary>
    public static IReadOnlyList<MonsterKitSkill> EssenceKit(ClientState conn, MonsterCatalog.Variety variety, byte level, IReadOnlyList<int> essences)
    {
        var catalog = SkillsFor(conn);
        var kit = new List<MonsterKitSkill>();
        var seen = new HashSet<int>();
        foreach (int row in essences)
        {
            if (DaemonEffects(row) is not { } daemon) continue;
            int tier = MapEssenceCatalog.Find(row)?.Tier ?? 7;
            foreach (int effect in daemon.Effects)
            {
                if (!seen.Add(effect)) continue;
                int effectLevel = catalog.MaxLevelAt(effect, level);
                double cooldown = catalog.CooldownMs(effect, effectLevel) / 1000d;
                ushort id = (ushort)(0x4000 + kit.Count);
                var attack = BossKits.AttackFor(conn, variety, level, effect, id);
                if (attack.Damage.IsZero && attack.Summon is null && attack.Projectile is null) continue;
                // The summon count scales with the essence tier (the tier-7 observation is the maximum).
                if (attack.Summon is { } summon)
                    attack = attack with { Summon = summon with { Count = ScaledSpellCount(effect, tier, summon.Count) } };
                kit.Add(new(effect, attack, Math.Max(0, cooldown)));
            }
        }
        return kit;
    }

    /// <summary>The encased preview monsters: the rare and the pack's magic monsters as paused, invulnerable monster
    /// objects in the frozen visual, standing in the crystals. The client draws the model, life bar and essence panel
    /// from these objects; the monolith crystal renders around them. Instance-owned so every client sees them.</summary>
    public static async Task<Monolith> SpawnEncasedPreviews(ClientState conn, Monolith monolith)
    {
        byte level = (byte)AreaLevel(conn);
        uint main = 0;
        // The imprisoned preview carries only its own rare affixes (null mods → the ordinary rare roll), NOT the essence
        // mods: the panel's top section is the monster's ObjectMagicProperties, and the essence names belong only to the
        // Monolith component's list below it. Applying essence mods here baked them into the preview, so a corruption
        // that changed the essences left the old names in the top section. The essence mods apply on release instead.
        if (Monsters.Find(monolith.Variety) is { Supported: true } rv)
            main = await SpawnEncased(conn, rv, level, MonsterRarity.Rare, monolith.X, monolith.Y, null);
        var minis = monolith.Minis.ToList();
        for (int i = 0; i < minis.Count; i++)
        {
            if (Monsters.Find(minis[i].Variety) is not { Supported: true } mv) continue;
            // The minis are the pack's monsters themselves, friendly (allies take no target or hover) and asleep, with
            // hide_mini_life_bar so not even the ally bar shows. They keep the model, the ice and the freeze.
            minis[i] = minis[i] with { Preview = await SpawnEncased(conn, mv, level, MonsterRarity.Magic, minis[i].X, minis[i].Y, null, friendly: true) };
        }
        return monolith with { Preview = main, Minis = minis };
    }

    static async Task<uint> SpawnEncased(ClientState conn, MonsterCatalog.Variety variety, byte level, MonsterRarity rarity,
        int x, int y, MonsterModSet? mods, bool friendly = false)
    {
        var w = conn.Wire;
        // Asleep (no orders) with the client's hide_mini_life_bar stat: the model renders, but no life bar. The rare is
        // hostile so the monolith's panel shows its mods; a mini is friendly, so the client does not target it at all.
        // Established: the crystal's cell is already placed and checked, and the monster stands in its centre. The
        // proposed placement's nearest-clear search would push it out of the crystal (the monolith's own blocker).
        uint id = await SpawnMonster(w.Remote, w.Stream, w.Ciphers, conn, variety, level, rarity, x, y,
            friendly: friendly, mods: mods, unrisen: false, alone: false, scripted: true, instanceOwned: true,
            placement: MonsterPlacement.Established, asleep: true, rarityGlow: false,
            // hide_mini_life_bar hides the bar. A mini (friendly) also gets virtual_is_hidden_monster (stat key 0x59d0,
            // row 22991), the client's other "not a real monster" marker, which should take the name and hover with it.
            extraStats: friendly ? [(StatKeys.HideMiniLifeBar, 1), (0x59d0, 1)] : [(StatKeys.HideMiniLifeBar, 1)],
            log: false);
        if (id == 0) return 0;
        // It stands in the crystal: it neither moves, attacks nor can be hurt or rewarded, but it is a real instance
        // monster (the monolith's panel and the client's encased state read it by id).
        lock (conn.Monsters)
            if (conn.Monsters.TryGetValue(id, out var monster))
            {
                monster.NoRewards = true; monster.Attack = null; monster.Aggro = false;
                monster.EncounterStats = new Dictionary<string, int> { ["base_cannot_be_damaged"] = 1 };
            }
        // The essence's own effect only: BuffDefinitions 1311 frozen_in_time with its visual overridden to
        // BuffVisuals 658 `monster_essence_effect_buff` (3.30 661), the light_blue monster effect. No BuffDefinitions
        // row uses 658 in either build, so an AddBuff visual override is its only use. The row's own visual is
        // legion_petrify and is never sent: it is Legion's, not the essence's.
        await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, conn, id,
            InstanceEntities.AddBuff(id, conn.NextBuff++, 1311, visual: 658), $"essence encased {variety.Id}");
        // Freeze the actor without a visual: 0x1b2 kind 0 plays animation 0 at speed 0 (paused in place).
        await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, conn, id,
            InstanceProtocol.AnimationUpdate(id, 0, 0, 0), $"essence freeze {variety.Id}");
        return id;
    }

    /// <summary>Removes the encased previews when the crystal breaks; the released monsters take their place.</summary>
    public static void RemoveEncasedPreviews(ClientState conn, Monolith monolith)
    {
        var ids = new List<uint> { monolith.Preview };
        ids.AddRange(monolith.Minis.Select(m => m.Preview));
        lock (conn.Monsters)
            foreach (uint id in ids)
                if (id != 0 && conn.Monsters.Remove(id, out var monster))
                {
                    monster.Dead = true; monster.Life = 0; monster.ActionGeneration++;
                    conn.MonsterViews.Remove(id); conn.AsleepMonsters.Remove(id);
                    conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), "essence encased ends", id, monster,
                        PlayerMinions.OutputKind.Remove, WorldId: id, RemovesObject: true));
                }
    }
}
