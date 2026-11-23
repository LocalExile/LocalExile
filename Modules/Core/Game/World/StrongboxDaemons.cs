using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

/// <summary>
/// The daemons a box's mods name (Mods.MonsterMetadata, <c>Metadata/Monsters/Daemon/ChestDaemon*</c>, the Strange Barrel's
/// and Weylam's): each is a "use skill once" daemon whose GrantedEffects are its skills. The summoning ones bring their
/// monsters as guards, in waves: a skill's <c>number_of_monsters_to_summon</c> (or packs), at its
/// <c>summoned_monster_rarity</c>, once and then <c>skill_repeat_count</c> more times, a cast time apart (the monster is
/// its SummonedSpecificMonsters row's, through <see cref="SkillCatalog.MonsterSummon"/>). The spell daemons are
/// StrongboxDaemonSpells.cs.
/// </summary>
public static partial class MapStrongboxes
{
    /// <summary>A wave of guards that arrives at <paramref name="AtTicks"/> (UTC ticks).</summary>
    public sealed record Wave(long AtTicks, List<AreaPopulation.Spawn> Spawns);

    /// <summary>The summon skills the guard mods already cover (their <c>summoned_monster_rarity</c> stats, the warbands)
    /// or that are not guards (the Vaal temple's side-area boss).</summary>
    static readonly HashSet<string> CoveredSummons = new(StringComparer.Ordinal)
    {
        "ChestSummonNormalMonsters", "ChestSummonMagicMonsters", "ChestSummonRareMonsters", "ChestSummonRareMonstersUniqueChest2",
        "ChestSummonRareMonstersUniqueChest3", "ChestSummonRareMonstersUniqueChest4", "ChestSummonFireWarband",
        "ChestSummonColdWarband", "ChestSummonLightningWarband", "ChestSummonChaosWarband", "ChestSummonAdditionalPack",
        // Hazards, not guards (StrongboxDaemonSpells.cs).
        "ChestSummonSandstormDaemon",
    };

    /// <summary>Summons whose SummonedSpecificMonsters row names no monster: the monsters of the same family stand in.
    /// OUR choice by name (the spiders' middle and large sizes for the barrel's magic and rare waves, the ghost pirates of
    /// Weylam's chest for its magic wave).</summary>
    static readonly Dictionary<string, string[]> SummonStandIns = new(StringComparer.Ordinal)
    {
        // Ossified: its alternate_minion does not resolve to a skeleton (as for the shrine's summon); the skeleton its
        // ActiveSkill summon_skeletons raises stands in.
        ["ChestSummonSkeletons"] = ["Metadata/Monsters/RaisedSkeletons/RaisedSkeletonStandard"],
        ["BarrelOfSpidersMagic"] = ["Metadata/Monsters/Spiders/SpiderBarrelOfSpidersMedium", "Metadata/Monsters/Spiders/SpiderBarrelOfSpidersMediumViperStrike",
            "Metadata/Monsters/Spiders/SpiderBarrelOfSpidersMediumFlickerStrike"],
        ["BarrelOfSpidersRare"] = ["Metadata/Monsters/Spiders/SpiderBarrelOfSpidersLarge", "Metadata/Monsters/Spiders/SpiderBarrelOfSpidersLargeViperStrike",
            "Metadata/Monsters/Spiders/SpiderBarrelOfSpidersLargeFlickerStriker"],
        ["WeylamsWarChestMagic"] = ["Metadata/Monsters/GhostPirates/GhostPirateBlackChest", "Metadata/Monsters/GhostPirates/GhostPirateBlackFlickerStrikeChest",
            "Metadata/Monsters/GhostPirates/GhostPirateBlackBowChest"],
        ["SummonKaomTotems"] = ["Metadata/Monsters/Totems/KaomsChestFlameTotem", "Metadata/Monsters/Totems/KaomsChestFirestormTotem",
            "Metadata/Monsters/Totems/KaomsChestFireballTotem", "Metadata/Monsters/Totems/KaomsChestFlameblastTotem"],
    };
    static readonly Dictionary<string, string[]> SummonAliases = new(StringComparer.Ordinal)
    {
        ["BarrelOfSpidersMagic2"] = SummonStandIns["BarrelOfSpidersMagic"],
    };

    /// <summary>The daemons of the box's mods: each mod's MonsterMetadata variety and that variety's GrantedEffects ids.</summary>
    static List<(string Mod, string Daemon, string[] Skills)> Daemons(Player player, StrongboxCatalog.Definition type, Box box)
    {
        var result = new List<(string, string, string[])>();
        var mods = ClientTables.TableOrEmpty("Mods");
        var varieties = ClientTables.TableOrEmpty("MonsterVarieties");
        var effects = ClientTables.TableOrEmpty("GrantedEffects");
        foreach (var (mod, _) in StrongboxMods.Lines(ItemsFor(player).Mods, type, box.Explicits))
        {
            if (mods.Row(mod.Row) is not { } row || row.TextOrNull("MonsterMetadata") is not { Length: > 0 } daemon) continue;
            var variety = varieties.FirstOrDefault(v => v.Text("Id") == daemon);
            if (variety.Text("Id") != daemon) continue;
            result.Add((mod.Id, daemon, [.. variety.Ints("GrantedEffectsKeys").Select(e => effects.Row(e)?.Text("Id") ?? "").Where(e => e.Length > 0)]));
        }
        return result;
    }

    /// <summary>The waves of the box's summoning daemons, the first at <paramref name="now"/>.</summary>
    static List<Wave> SummonWaves(Player player, StrongboxCatalog.Definition type, Box box, DateTime now, Random random)
    {
        var waves = new List<Wave>();
        var skills = SkillsFor(player);
        int level = AreaLevel(player);
        int spot = 0;
        foreach (var (mod, daemon, effects) in Daemons(player, type, box))
            foreach (string skill in effects)
            {
                if (CoveredSummons.Contains(skill)) continue;
                int row = skills.EffectRow(skill);
                if (row < 0) continue;
                var stats = skills.EffectStats(row, Math.Clamp(level, 1, 100)).ToDictionary(s => s.Item1, s => s.Item2);
                int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
                var pool = SummonPool(player, skill, skills.MonsterSummon(row, level)?.Variety, level, random);
                if (pool.Length == 0) continue;
                int count = Math.Max(1, Get("number_of_monsters_to_summon") + Get("number_of_melee_skeletons_to_summon"));
                int repeats = Get("skill_repeat_count");
                var rarity = (MonsterRarity)Math.Clamp(Get("summoned_monster_rarity"), 0, 3);
                double interval = Math.Max(0.25, skills.CastMs(row) / 1000.0 / Math.Max(0.05, 1 + Get("active_skill_cast_speed_+%_final") / 100.0));
                bool distinct = skill == "SummonKaomTotems";
                for (int wave = 0; wave <= repeats; wave++)
                {
                    var spawns = new List<AreaPopulation.Spawn>();
                    for (int i = 0; i < count; i++)
                    {
                        int variety = distinct ? pool[i % pool.Length] : pool[random.Next(pool.Length)];
                        double angle = spot++ * 2.399963229728653;
                        var (x, y) = player.Layout.Near((box.X + (int)Math.Round(Math.Cos(angle) * 7), box.Y + (int)Math.Round(Math.Sin(angle) * 7)));
                        spawns.Add(new(variety, x, y, rarity, skill));
                    }
                    waves.Add(new((now + TimeSpan.FromSeconds(wave * interval)).Ticks, spawns));
                }
                Console.WriteLine($"[map-content] strongbox {box.Type} {mod}: {skill} {count} × {repeats + 1} {rarity} every {interval:0.##} s");
            }
        return waves;
    }

    /// <summary>The monsters a summon skill may bring, as supported MonsterVarieties rows.</summary>
    static int[] SummonPool(Player player, string skill, int? variety, int level, Random random)
    {
        IEnumerable<int> rows;
        if (variety is int v) rows = [v];
        else if (SummonStandIns.TryGetValue(skill, out var ids) || SummonAliases.TryGetValue(skill, out ids))
            rows = ids.Select(id => Monsters.Find(id)?.Row ?? -1);
        // Ashes of the Condemned's stream: the Tormented Spirits of the area level (TormentSpirits).
        else if (skill == "ChestSummonTormentContinous")
            rows = ClientTables.TableOrEmpty("TormentSpirits")
                .Where(r => level >= (r.IntOrNull("MinZoneLevel") ?? 0) && level <= (r.IntOrNull("MaxZoneLevel") ?? 100) && (r.IntOrNull("SpawnWeight") ?? 0) > 0)
                .Select(r => r.IntOrNull("MonsterVarietiesKey") ?? -1);
        // Rallying's stream: the area's own monsters.
        else if (skill == "ChestSummonMonstersContinuous")
            rows = (Packs.For(player.Layout.Id)?.Scattered ?? []).SelectMany(p => p.Pack.Entries).Select(e => e.Variety);
        else return [];
        return [.. rows.Distinct().Where(r => r >= 0 && Monsters.Find(r.ToString()) is { Supported: true })];
    }

    /// <summary>The waves whose time has come, moved to the box's pending spawns.</summary>
    static Box ReleaseWaves(Box box, DateTime now)
    {
        if (box.Waves.Count == 0 || box.Waves[0].AtTicks > now.Ticks) return box;
        var due = box.Waves.Where(w => w.AtTicks <= now.Ticks).ToList();
        foreach (var wave in due) box.Pending.AddRange(wave.Spawns);
        return box with { Waves = [.. box.Waves.Except(due)] };
    }
}
