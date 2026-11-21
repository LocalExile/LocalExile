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

    Run PrepareWave(Player player, int wave, Run previous)
    {
        var random = AreaRng(player);
        var effects = new Dictionary<string, int>(StringComparer.Ordinal);
        var descriptions = new List<string>();
        var affixes = new List<ItemMod>();
        var mods = ItemsFor(player).Mods;
        // Only effects the combat model executes are admitted; do not advertise an affix with no gameplay effect.
        var supported = new Dictionary<uint, uint>
        {
            [Stat.MapMonstersLifePlusPct] = Stat.MaximumLifePlusPct,
            [Stat.MapMonstersDamagePlusPct] = Stat.DamagePlusPct,
            [Stat.MapMonstersMovementSpeedPlusPct] = Stat.BaseMovementVelocityPlusPct,
            [Stat.MapMonstersAttackSpeedPlusPct] = Stat.AttackSpeedPlusPct,
            [Stat.MapMonstersCastSpeedPlusPct] = Stat.BaseCastSpeedPlusPct,
            [Stat.MapMonstersPctPhysicalDamageToAddAsFire] = Stat.PhysicalDamagePctToAddAsFire,
            [Stat.MapMonstersPctPhysicalDamageToAddAsCold] = Stat.PhysicalDamagePctToAddAsCold,
            [Stat.MapMonstersPctPhysicalDamageToAddAsLightning] = Stat.PhysicalDamagePctToAddAsLightning,
            [Stat.Of("map_monsters_chaos_damage_%_to_add_as_chaos")] = Stat.NonChaosDamageToAddAsChaosDamagePct,
            [Stat.Of("map_monsters_base_resist_all_elements_%")] = Stat.BaseResistAllElementsPct,
            [Stat.Of("map_monsters_base_chaos_damage_resistance_%")] = Stat.BaseChaosDamageResistancePct,
            [Stat.Of("map_monsters_additional_physical_damage_reduction_%")] = Stat.BaseAdditionalPhysicalDamageReductionPct,
            [Stat.MapMonstersAdditionalPhysicalDamageReduction] = Stat.BaseAdditionalPhysicalDamageReductionPct,
            [Stat.MapMonstersAdditionalFireResistance] = Stat.BaseFireDamageResistancePct,
            [Stat.MapMonstersAdditionalColdResistance] = Stat.BaseColdDamageResistancePct,
            [Stat.MapMonstersAdditionalLightningResistance] = Stat.BaseLightningDamageResistancePct,
            [Stat.MapMonstersAdditionalChaosResistance] = Stat.BaseChaosDamageResistancePct,
            [Stat.MapMonstersMovementSpeedCannotBeReducedBelowBase] = Stat.MovementSpeedCannotBeReducedBelowBase,
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
                string stat = Stats.Name(supported[mod.Stats[i].Id]);
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

    async Task SpawnBatch(Player player, Run run)
    {
        double now = AreaTime(player);
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
        var centre = player.Walk.Position(now);
        for (; run.Spawned < finish; )
        {
            if (!context.Lifetime.IsActive || !Current(player) || player.PlayerLife == 0) break;
            if (run.SpawningDone) break;
            // Bosses enter the wave too, rather than being held behind an unknown final monster count.
            bool omni = run.OmniThisWave && run.Spawned == 20;
            bool kosis = run.KosisThisWave && run.Spawned == 40;
            string? boss = omni ? SimulacrumRules.Omniphobia : kosis ? SimulacrumRules.Kosis : null;
            var variety = boss is not null ? Monsters.Find(boss) : pool[AreaRng(player).Next(pool.Length)];
            if (variety is not { Supported: true })
            {
                // A boss the client data cannot spawn would stop the wave at this count for good: it goes on without it.
                Console.WriteLine($"[gateway] Simulacrum: {boss} cannot be spawned; wave {run.Wave} goes on without it");
                if (omni) run.OmniThisWave = false;
                if (kosis) run.KosisThisWave = false;
                Save(player, run);
                continue;
            }
            variety = variety with { StateMachine = MonsterStates(variety.StateMachine), AggroRange = Math.Max(100, variety.AggroRange) };
            int rarityRoll = AreaRng(player).Next(100);
            var rarity = boss is not null ? MonsterRarity.Unique : rarityRoll < 4 ? MonsterRarity.Rare
                : rarityRoll < 20 ? MonsterRarity.Magic : MonsterRarity.Normal;
            int points = run.ProgressGoal > 0 ? Math.Min(ProgressWeight(rarity), run.ProgressGoal - run.SpawnPoints) : 0;
            double delirious = SimulacrumRules.Delirious(run.Wave) / 100.0;
            // This scales life rather than reducing incoming damage twice; live HP and all hits use the same value.
            int lifeMore = (int)Math.Round((rarity is MonsterRarity.Unique or MonsterRarity.Rare ? 360 : 500) * delirious);
            var rolledMods = MonsterModsFor(player, variety, level, rarity, null, true);
            var stats = run.Modifiers.ToDictionary(p => Stat.Of(p.Key), p => p.Value);
            stats[Stat.MaximumLifePlusPct] = stats.GetValueOrDefault(Stat.MaximumLifePlusPct)
                + (int)((100L + rolledMods.Effects[Stat.MaximumLifePlusPct] + stats.GetValueOrDefault(Stat.MaximumLifePlusPct)) * lifeMore / 100);
            stats[Stat.MonsterRarityDamagePlusPctFinal] = (int)Math.Round((100 + rolledMods.Effects[Stat.MonsterRarityDamagePlusPctFinal]) * .7 * delirious);
            var extra = new MonsterModSet([], [], new MonsterModEffects(stats));
            double angle = run.Spawned * 2.399963229728653;
            int radius = 35 + run.Spawned % 40;
            var at = player.Layout.Near((centre.X + (int)(Math.Cos(angle) * radius), centre.Y + (int)(Math.Sin(angle) * radius)));
            uint id = await SpawnMonster(player, variety, level, rarity, at.X, at.Y,
                mods: rolledMods, extraMods: extra, unrisen: false, log: false,
                ownedCurrent: () => context.Lifetime.IsActive && Current(player) && run.Active,
                owned: (spawnedId, monster) =>
                {
                    monster.Aggro = true;
                    run.Monsters.Add(spawnedId);
                    run.Bodies[spawnedId] = monster;
                    if (points > 0) { run.MonsterPoints[spawnedId] = points; run.SpawnPoints += points; }
                    run.Spawned++;
                    if (omni) run.OmniCount++;
                    if (kosis) run.KosisCount++;
                    Save(player, run);
                });
            if (id == 0) break;
        }
    }

    async Task Complete(Player player, Run run)
    {
        int wave = run.Wave;
        var loot = RollRewards(player, run);
        var output = new List<(uint Id, WorldItemObject Item)>();
        if (!context.Lifetime.IsActive || !Current(player) || run.Wave != wave || !run.TryComplete()) return;
        // Mark and place rewards atomically before sending anything. Re-entry and duplicate final deaths cannot pay twice.
        for (int i = 0; i < loot.Count; i++)
        {
            double a = i * 2.399963229728653, r = 5 + Math.Sqrt(i) * 2;
            var at = player.Layout.Near((player.Layout.SpawnX + 18 + (int)(Math.Cos(a) * r), player.Layout.SpawnY + 12 + (int)(Math.Sin(a) * r)));
            var item = new WorldItemObject(at.X, at.Y, loot[i]);
            output.Add((player.Instance.Ground.Add(item, player), item));
        }
        Save(player, run);
        foreach (var item in output) await SendGroundItem(player, item.Id, item.Item, null, justDropped: true);
        await PublishStarter(player, run, recreate: true);
        await Broadcast(player, $"Simulacrum wave {wave}/15 complete — {output.Count} rewards. " +
            (wave == 15 ? "All waves defeated. The Delirious Bloodline is now available." : "Collect your loot, then interact with the entity to start the next wave."));
        context.Log($"Simulacrum {player.InstanceNumber} wave {wave} cleared: {run.Kills} kills, progress {run.KillPoints}/{run.ProgressGoal}, {output.Count} reward items");
    }
}
