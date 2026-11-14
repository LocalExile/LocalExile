using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Delirium;

public sealed partial class DeliriumModule
{
    static readonly int[] RewardKinds = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 13, 14, 15, 17, 19, 20, 22, 23, 24, 25];

    static double Setting(string key, double fallback) => SystemSettings.Default.Values.TryGetValue(key, out var v) ? v : fallback;
    static int WaveProgressGoal(int wave) => (int)Math.Clamp(Math.Round(SimulacrumRules.Population(wave) * 1.28
        * Math.Clamp(Setting("simulacrum_population_scale_pct", 100), 10, 300) / 100), 20, 3000);
    static int ProgressWeight(MonsterRarity rarity) => (int)Math.Clamp(rarity switch
    {
        MonsterRarity.Unique => Setting("simulacrum_progress_unique", 25),
        MonsterRarity.Rare => Setting("simulacrum_progress_rare", 4),
        MonsterRarity.Magic => Setting("simulacrum_progress_magic", 2),
        _ => Setting("simulacrum_progress_normal", 1),
    }, 1, 100);

    Run PrepareWave(ClientState conn, int wave, Run previous)
    {
        var random = Random.Shared;
        var effects = new Dictionary<string, int>(StringComparer.Ordinal);
        var descriptions = new List<string>();
        var affixes = new List<ItemMod>();
        var mods = ItemsFor(conn).Mods;
        // Only effects the combat model executes are admitted; do not advertise an affix with no gameplay effect.
        var supported = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["map_monsters_life_+%"] = "maximum_life_+%",
            ["map_monsters_damage_+%"] = "damage_+%",
            ["map_monsters_movement_speed_+%"] = "base_movement_velocity_+%",
            ["map_monsters_attack_speed_+%"] = "attack_speed_+%",
            ["map_monsters_cast_speed_+%"] = "base_cast_speed_+%",
            ["map_monsters_%_physical_damage_to_add_as_fire"] = "physical_damage_%_to_add_as_fire",
            ["map_monsters_%_physical_damage_to_add_as_cold"] = "physical_damage_%_to_add_as_cold",
            ["map_monsters_%_physical_damage_to_add_as_lightning"] = "physical_damage_%_to_add_as_lightning",
            ["map_monsters_chaos_damage_%_to_add_as_chaos"] = "non_chaos_damage_to_add_as_chaos_damage_%",
            ["map_monsters_base_resist_all_elements_%"] = "base_resist_all_elements_%",
            ["map_monsters_base_chaos_damage_resistance_%"] = "base_chaos_damage_resistance_%",
            ["map_monsters_additional_physical_damage_reduction_%"] = "base_additional_physical_damage_reduction_%",
            ["map_monsters_additional_physical_damage_reduction"] = "base_additional_physical_damage_reduction_%",
            ["map_monsters_additional_fire_resistance"] = "base_fire_damage_resistance_%",
            ["map_monsters_additional_cold_resistance"] = "base_cold_damage_resistance_%",
            ["map_monsters_additional_lightning_resistance"] = "base_lightning_damage_resistance_%",
            ["map_monsters_additional_chaos_resistance"] = "base_chaos_damage_resistance_%",
            ["map_monsters_movement_speed_cannot_be_reduced_below_base"] = "movement_speed_cannot_be_reduced_below_base",
        };
        var candidates = ClientTables.Table("AfflictionEndgameWaveMods")
            .Where(r => r.Int("__col3") > 0 && wave >= r.Int("__col1") && wave <= r.Int("__col2"))
            .Select(r => (Mod: mods.Row(r.Int("ModsKey")), Weight: r.Int("__col3")))
            .Where(r => r.Mod is { Stats.Count: > 0 } && r.Mod.Stats.All(s => supported.ContainsKey(s.Id)))
            .Select(r => (Mod: r.Mod!, r.Weight)).DistinctBy(r => r.Mod.Id).ToList();
        var families = new HashSet<int>();
        while (candidates.Count > 0 && descriptions.Count < SimulacrumRules.ModifierCount(wave))
        {
            int choice = random.Next(candidates.Sum(c => c.Weight));
            int selected = 0;
            for (; selected < candidates.Count - 1; selected++)
            { choice -= candidates[selected].Weight; if (choice < 0) break; }
            var mod = candidates[selected].Mod;
            var rolled = mod.Roll(ModSection.Explicit, random);
            affixes.Add(rolled);
            for (int i = 0; i < mod.Stats.Count; i++)
            {
                string stat = supported[mod.Stats[i].Id];
                effects[stat] = effects.GetValueOrDefault(stat) + rolled.Values[i];
            }
            foreach (int family in mod.Families) families.Add(family);
            descriptions.Add(mods.Describe(rolled));
            candidates.RemoveAll(c => c.Mod.Id == mod.Id || c.Mod.Families.Any(families.Contains));
        }
        bool omni = wave >= 3 && (previous.OmniCount == 0 && wave >= 5 || random.NextDouble() < Math.Clamp(Setting("simulacrum_boss_chance_pct", 25) / 100, 0, 1));
        bool kosis = wave >= 5 && (previous.KosisCount == 0 && wave >= 10 || random.NextDouble() < Math.Clamp(Setting("simulacrum_boss_chance_pct", 25) / 100, 0, 1));
        // Density/tier progress and boss chances are explicit server policy: GGG does not publish their weights.
        return new Run
        {
            ProgressGoal = WaveProgressGoal(wave),
            Rewards = [.. RewardKinds.OrderBy(_ => random.Next()).Take(SimulacrumRules.RewardTypes(wave))],
            Modifiers = effects, ModifierNames = descriptions.ToArray(), OmniThisWave = omni, KosisThisWave = kosis,
            Affixes = affixes.ToArray(),
        };
    }

    async Task SpawnBatch(ClientState conn, Run run)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        int count = run.SpawnAllowance(now, (int)Math.Clamp(Setting("simulacrum_pack_size", 15), 10, 20),
            (int)Math.Clamp(Setting("simulacrum_active_limit", 100), 20, 100));
        if (count == 0) return;
        // The instance clock pauses with gameplay; no timer, worker or catch-up burst after a stall.
        run.NextSpawnAt = now + Math.Clamp(Setting("simulacrum_pack_interval_ms", 750), 250, 5000) / 1000;
        var level = (byte)SimulacrumRules.Level(run.Wave);
        var pool = new[] { "AfflictionMinion1", "AfflictionMinion2", "AfflictionMinion3", "AfflictionMinion4", "AfflictionMinion5_", "AfflictionMinion6_" }
            .Select(id => Monsters.Find("Metadata/Monsters/LeagueAffliction/" + id)).OfType<MonsterCatalog.Variety>()
            .Where(v => v.Supported).ToArray();
        if (pool.Length == 0) throw new InvalidOperationException("No supported Delirium monsters in the client data");
        int finish = run.Spawned + count;
        var centre = conn.Walk.Position(now);
        for (; run.Spawned < finish; )
        {
            if (!context.Lifetime.IsActive || !Current(conn) || conn.PlayerLife == 0) break;
            if (run.SpawningDone) break;
            // Bosses enter the wave too, rather than being held behind an unknown final monster count.
            bool omni = run.OmniThisWave && run.Spawned == 20;
            bool kosis = run.KosisThisWave && run.Spawned == 40;
            string? boss = omni ? SimulacrumRules.Omniphobia : kosis ? SimulacrumRules.Kosis : null;
            var variety = boss is not null ? Monsters.Find(boss) : pool[Random.Shared.Next(pool.Length)];
            if (variety is not { Supported: true }) throw new InvalidOperationException($"Unsupported Simulacrum monster: {boss}");
            variety = variety with { StateMachine = MonsterStates(variety.StateMachine), AggroRange = Math.Max(100, variety.AggroRange) };
            int rarityRoll = Random.Shared.Next(100);
            var rarity = boss is not null ? MonsterRarity.Unique : rarityRoll < 4 ? MonsterRarity.Rare
                : rarityRoll < 20 ? MonsterRarity.Magic : MonsterRarity.Normal;
            int points = run.ProgressGoal > 0 ? Math.Min(ProgressWeight(rarity), run.ProgressGoal - run.SpawnPoints) : 0;
            double delirious = SimulacrumRules.Delirious(run.Wave) / 100.0;
            // This scales life rather than reducing incoming damage twice; live HP and all hits use the same value.
            int lifeMore = (int)Math.Round((rarity is MonsterRarity.Unique or MonsterRarity.Rare ? 360 : 500) * delirious);
            var rolledMods = MonsterModsFor(conn, variety, level, rarity, null, true);
            var stats = new Dictionary<string, int>(run.Modifiers, StringComparer.Ordinal);
            stats["maximum_life_+%"] = stats.GetValueOrDefault("maximum_life_+%")
                + (int)((100L + rolledMods.Effects["maximum_life_+%"] + stats.GetValueOrDefault("maximum_life_+%")) * lifeMore / 100);
            stats["monster_rarity_damage_+%_final"] = (int)Math.Round((100 + rolledMods.Effects["monster_rarity_damage_+%_final"]) * .7 * delirious);
            var extra = new MonsterModSet([], [], new MonsterModEffects(stats));
            double angle = run.Spawned * 2.399963229728653;
            int radius = 35 + run.Spawned % 40;
            var at = conn.Layout.Near((centre.X + (int)(Math.Cos(angle) * radius), centre.Y + (int)(Math.Sin(angle) * radius)));
            var w = conn.Wire;
            uint id = await SpawnMonster(w.Remote, w.Stream, w.Ciphers, conn, variety, level, rarity, at.X, at.Y,
                mods: rolledMods, extraMods: extra, unrisen: false, log: false,
                ownedCurrent: () => context.Lifetime.IsActive && Current(conn) && run.Active,
                owned: (spawnedId, monster) =>
                {
                    monster.Aggro = true;
                    run.Monsters.Add(spawnedId);
                    if (points > 0) { run.MonsterPoints[spawnedId] = points; run.SpawnPoints += points; }
                    run.Spawned++;
                    if (omni) run.OmniCount++;
                    if (kosis) run.KosisCount++;
                    Save(conn, run);
                });
            if (id == 0) break;
        }
    }

    async Task Complete(ClientState conn, Run run)
    {
        int wave = run.Wave;
        var loot = RollRewards(conn, run);
        var output = new List<(uint Id, WorldItemObject Item)>();
        lock (conn.Monsters)
        {
            if (!context.Lifetime.IsActive || !Current(conn) || run.Wave != wave || !run.TryComplete()) return;
            // Mark and place rewards atomically before sending anything. Re-entry and duplicate final deaths cannot pay twice.
            for (int i = 0; i < loot.Count; i++)
            {
                double a = i * 2.399963229728653, r = 5 + Math.Sqrt(i) * 2;
                var at = conn.Layout.Near((conn.Layout.SpawnX + 18 + (int)(Math.Cos(a) * r), conn.Layout.SpawnY + 12 + (int)(Math.Sin(a) * r)));
                var item = new WorldItemObject(at.X, at.Y, loot[i]);
                output.Add((conn.Instance.Ground.Add(item, conn), item));
            }
            Save(conn, run);
        }
        var wire = conn.Wire;
        foreach (var item in output) await SendGroundItem(wire.Remote, wire.Stream, wire.Ciphers, conn, item.Id, item.Item, null, justDropped: true);
        await PublishStarter(conn, run, recreate: true);
        await Broadcast(conn, $"Simulacrum wave {wave}/15 complete — {output.Count} rewards. " +
            (wave == 15 ? "All waves defeated. The Delirious Bloodline is now available." : "Collect your loot, then interact with the entity to start the next wave."));
        context.Log($"Simulacrum {conn.InstanceNumber} wave {wave} cleared: {run.Kills} kills, progress {run.KillPoints}/{run.ProgressGoal}, {output.Count} reward items");
    }
}
