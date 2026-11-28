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

    /// <summary>How many rewards of this type a full wave's progress is worth: the common types pay more often than
    /// the rare ones. Explicit server policy — GGG publishes neither its per-type thresholds nor its reward weights.</summary>
    static int RewardPace(int kind) => kind switch
    {
        0 => 6,                 // currency
        11 or 23 => 5,          // essences, oils
        14 or 17 => 4,          // scarabs, divination cards
        1 => 2,                 // uniques
        _ => 3,
    };

    /// <summary>The kill points one payout of this reward type takes: the wave's progress split by its pace.</summary>
    public static int RewardGoal(Run run, int index) =>
        Math.Max(1, run.ProgressGoal / Math.Max(1, RewardPace(index < run.Rewards.Length ? run.Rewards[index] : 0)));

    /// <summary>Where a wave's rewards land: the arena's own spot, measured in the client, or the cell the player
    /// stands on when the wave ends. The entrance (the layout's spawn) is not it — the fight is far from there.</summary>
    static (int X, int Y)? RewardSpot(string area) => area switch
    {
        // Measured in the client 2026-11-24: the arena's own places, not the entrance the layout names.
        "AfflictionTown6" => (313, 627),
        "AfflictionTown7" => (554, 657),
        "AfflictionTown8" => (321, 401),
        "AfflictionTown10" => (405, 288),
        "AfflictionTown4" => (203, 349),
        _ => null,
    };

    /// <summary>A measured place in an arena: its cell and the object's facing in radians (the client's own unit).</summary>
    readonly record struct Spot(int X, int Y, float Turn = 0);

    static float Rad(float degrees) => degrees * MathF.PI / 180;

    /// <summary>Terrain a wave keeps its monsters off, by the <c>.tdt</c> tile's name: the ship in Lunacy's Watch is
    /// scenery the fight does not belong on. The grid's own tile map says which cell is which (<c>Level.TileAt</c>).</summary>
    static string[] NoSpawnTiles(string area) => area switch
    {
        "AfflictionTown6" => ["AfflictionShip_v01_01"],
        _ => [],
    };

    static bool SpawnAllowed(Player player, (int X, int Y) at)
    {
        var tiles = NoSpawnTiles(player.Layout.Id);
        if (tiles.Length == 0 || player.Layout.Level?.TileAt is not { } tileAt) return true;
        string? path = tileAt(at.X, at.Y);
        return path is null || !tiles.Any(t => path.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Where the stash between waves stands, when the arena has a measured spot for it.</summary>
    static Spot? StashSpot(string area) => area switch
    {
        "AfflictionTown6" => new Spot(370, 631, Rad(-87)),
        "AfflictionTown7" => new Spot(595, 642),
        "AfflictionTown8" => new Spot(313, 445, Rad(30)),
        "AfflictionTown10" => new Spot(380, 300),
        "AfflictionTown4" => new Spot(289, 352, Rad(-90)),
        _ => null,
    };

    /// <summary>Where the wave's initiator stands, when the arena has a measured spot for it. Without one it keeps the
    /// placement the area authored, or a cell near the entrance.</summary>
    static Spot? StarterSpot(string area) => area switch
    {
        "AfflictionTown6" => new Spot(354, 668, Rad(-50)),
        "AfflictionTown7" => new Spot(553, 705),
        "AfflictionTown8" => new Spot(384, 479, Rad(-40)),
        "AfflictionTown10" => new Spot(439, 364),
        "AfflictionTown4" => new Spot(306, 396),
        _ => null,
    };

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
            [Stat.MapMonstersPctPhysicalDamageToAddAsChaos] = Stat.PhysicalDamagePctToAddAsChaos,
            [Stat.MapMonsterSkillsChainXAdditionalTimes] = Stat.NumberOfChains,
            [Stat.MapMonstersPoisonOnHit] = Stat.BaseChanceToPoisonOnHitPct,
            [Stat.MapMonstersChanceToImpalePct] = Stat.MapMonstersChanceToImpalePct,
            [Stat.MapMonstersCriticalStrikeChancePlusPct] = Stat.CriticalStrikeChancePlusPct,
            [Stat.MapMonstersCriticalStrikeMultiplierPlus] = Stat.BaseCriticalStrikeMultiplierPlus,
            [Stat.MapMonstersBaseSelfCriticalStrikeMultiplierMinusPct] = Stat.BaseSelfCriticalStrikeMultiplierMinusPct,
            [Stat.MapMonstersRemoveChargesOnHitPct] = Stat.RemoveChargesOnHitPct,
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
            [Stat.MapMonstersRemoveEnemyFlaskChargeOnHitPctChance] = Stat.MapMonstersRemoveEnemyFlaskChargeOnHitPctChance,
            // Read by AlternateAilmentsOnPlayerLocked. Sapped stays out: Sap on the player's damage is not modelled.
            [Stat.MapMonstersChanceToScorchPct] = Stat.ChanceToScorchPct,
            [Stat.MapMonstersChanceToInflictBrittlePct] = Stat.ChanceToInflictFrostburnPct,
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
                uint source = mod.Stats[i].Id;
                string stat = Stats.Name(supported[source]);
                int value = rolled.Values[i];
                if (source == Stat.MapMonstersPoisonOnHit) value = value != 0 ? 100 : 0;
                effects[stat] = effects.GetValueOrDefault(stat) + value;
            }
            foreach (int family in mod.Families) families.Add(family);
            descriptions.Add(mods.Describe(rolled));
            candidates.RemoveAll(c => c.Mod.Id == mod.Id || c.Mod.Families.Any(families.Contains));
        }
        bool omni = wave >= 3 && (previous.OmniCount == 0 && wave >= 5 || random.NextDouble() < Math.Clamp(Setting("simulacrum_boss_chance_pct", 25) / 100, 0, 1));
        bool kosis = wave >= 5 && (previous.KosisCount == 0 && wave >= 10 || random.NextDouble() < Math.Clamp(Setting("simulacrum_boss_chance_pct", 25) / 100, 0, 1));
        var rewards = RewardKinds.OrderBy(_ => random.Next()).Take(SimulacrumRules.RewardTypes(wave)).ToArray();
        // Density/tier progress and boss chances are explicit server policy: GGG does not publish their weights.
        return new Run
        {
            ProgressGoal = WaveProgressGoal(wave),
            Rewards = rewards,
            RewardMods = RewardModsFor(player, rewards),
            Modifiers = effects, ModifierNames = descriptions.ToArray(), OmniThisWave = omni, KosisThisWave = kosis,
            Affixes = affixes.ToArray(),
        };
    }

    /// <summary>
    /// The wave's reward types as ObjectMagicProperties mods: AfflictionMapReward1..9 (the client's own list in
    /// AfflictionRewardMapMods) each carry map_endgame_affliction_reward_N, whose StatDescriptions line is
    /// "Delirium Reward Type: {0}" with the affliction_reward_type formatter — the value is an AfflictionRewardTypes id.
    /// They ride the explicit list, where the initiator's prompt already shows the wave's affixes.
    /// </summary>
    ItemMod[] RewardModsFor(Player player, IReadOnlyList<int> rewards)
    {
        var mods = ItemsFor(player).Mods;
        var rows = ClientTables.TableOrEmpty("AfflictionRewardMapMods").Select(r => r.Int("ModsKey")).ToArray();
        var result = new List<ItemMod>(rewards.Count);
        for (int i = 0; i < rewards.Count && i < rows.Length; i++)
            if (mods.Row(rows[i]) is { } mod)
                // The client's affliction_reward_type formatter treats the stat value as 1-based: it shows value - 1,
                // so the id goes out incremented (live 2026-11-24: sent [17,23,3,11], prompt showed [16,22,2,10]).
                result.Add(new ItemMod(ModSection.Explicit, mod.Hash, [rewards[i] + 1]));
        return [.. result];
    }

    async Task SpawnBatch(Player player, Run run)
    {
        double now = AreaTime(player);
        int activeLimit = (int)Math.Clamp(Setting("simulacrum_active_limit", 100), 20, 100);
        int count = run.SpawnAllowance(now, (int)Math.Clamp(Setting("simulacrum_pack_size", 15), 10, 20), activeLimit);
        if (count == 0 && run.CanSpawnDueBoss(now, activeLimit)) count = 1;
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
            // Keep the usual ordinals and release any remaining boss when the wave spends its points early.
            string? boss = run.DueBoss();
            bool omni = boss == SimulacrumRules.Omniphobia;
            bool kosis = boss == SimulacrumRules.Kosis;
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
            // Every Simulacrum monster stands in the fog; the client's Delirium logic reads the flag on each one.
            if (StatCatalog.IsClientId(Stat.MonsterIsInAfflictionFog)) stats[Stat.MonsterIsInAfflictionFog] = 1;
            if (StatCatalog.IsClientId(Stat.IsInAfflictionFog)) stats[Stat.IsInAfflictionFog] = 1;
            var extra = new MonsterModSet([], [], new MonsterModEffects(stats));
            double angle = run.Spawned * 2.399963229728653;
            int radius = 35 + run.Spawned % 40;
            // Scenery the wave keeps off (the ship): try around the ring before giving this one up. A tick that finds
            // nothing spawns nothing and tries again from wherever the player is then.
            (int X, int Y) at = default;
            bool allowed = false;
            for (int attempt = 0; attempt < 16 && !allowed; attempt++)
            {
                double a = angle + attempt * 0.7;
                int r = radius + attempt % 4 * 6;
                at = player.Layout.Near((centre.X + (int)(Math.Cos(a) * r), centre.Y + (int)(Math.Sin(a) * r)));
                allowed = SpawnAllowed(player, at);
            }
            if (!allowed) break;
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
                    if (omni) { run.OmniCount++; run.OmniSpawnedThisWave = true; }
                    if (kosis) { run.KosisCount++; run.KosisSpawnedThisWave = true; }
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
        var spot = RewardSpot(player.Layout.Id) ?? player.Walk.Position(AreaTime(player));
        for (int i = 0; i < loot.Count; i++)
        {
            double a = i * 2.399963229728653, r = 2 + Math.Sqrt(i) * 1.5;
            var at = player.Layout.Near((spot.X + (int)Math.Round(Math.Cos(a) * r), spot.Y + (int)Math.Round(Math.Sin(a) * r)));
            var item = new WorldItemObject(at.X, at.Y, loot[i]);
            output.Add((player.Instance.Ground.Add(item, player), item));
        }
        // What the wave did not account for (a boss's summons, its phased bodies) would stay behind unkillable.
        var leftovers = ClearMonsters(player, run);
        Save(player, run);
        await PublishTiles(player, run, force: true);
        foreach (var peer in Clients(player)) await WorldOf(peer).RefreshStats();
        foreach (var item in output) await SendGroundItem(player, item.Id, item.Item, null, justDropped: true);
        foreach (uint id in leftovers)
            await SendWorldRemoval(player, id, $"0x02bd remove monster 0x{id:x} (Simulacrum wave {wave} over)");
        await PublishStarter(player, run, recreate: true);
        await Broadcast(player, $"Simulacrum wave {wave}/15 complete — {output.Count} rewards. " +
            (wave == 15 ? "All waves defeated. The Delirious Bloodline is now available." : "Collect your loot, then interact with the entity to start the next wave."));
        context.Log($"Simulacrum {player.InstanceNumber} wave {wave} cleared: {run.Kills} kills, progress {run.KillPoints}/{run.ProgressGoal}, {output.Count} reward items" +
            (leftovers.Count > 0 ? $", {leftovers.Count} leftover monster(s) removed" : ""));
    }
}
