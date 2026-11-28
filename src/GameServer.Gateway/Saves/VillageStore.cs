using GameServer.Storage;

/// <summary>
/// League+account-scoped SQLite persistence for Kingsmarch (<c>village_*</c> tables in <c>characters.db</c>,
/// schema in <c>sql/updates/pending_characters/settlers_village.sql</c>). Pure storage plumbing — no game rules.
/// </summary>
public static class VillageStore
{
    public sealed record TownRow(
        string Account,
        string League,
        long TreasuryGold,
        long LastSimulatedUtc,
        long LastRerollUtc,
        long RerollCostGold,
        long SuperRerollCostGold,
        long ShippingSeed,
        long NextWorkerId,
        long NextShipmentId,
        long NextMapRunId,
        long TutorialFlags,
        string StateJson);

    public sealed record ResourceRow(int ResourceId, long Amount, double FractionalProgress);

    public sealed record FarmPlotRow(int PlotIndex, int CropResourceId, double GrowthProgress);

    public sealed record WorkerRow(
        long WorkerId,
        string Name,
        int PortraitId,
        long WagePerHour,
        int AssignedJob,
        int AssignedSlot,
        int Status,
        string SkillsJson);

    public sealed record RecruitRow(
        int SlotIndex,
        string Name,
        int PortraitId,
        long HireCostGold,
        long WagePerHour,
        string SkillsJson);

    public sealed record ShipRow(
        int ShipIndex,
        int State,
        int PortId,
        long DepartedUtc,
        long ArrivalUtc,
        int RiskPercent,
        long ShipmentValue,
        string CargoJson,
        string CrewJson,
        int EventId,
        long RansomGold,
        bool BossPending);

    public sealed record PortQuotaRow(
        int PortId,
        int QuotaIndex,
        int ResourceId,
        long RequiredAmount,
        long FulfilledAmount,
        int BonusPercent);

    public sealed record MapDeviceRow(
        int DeviceIndex,
        int State,
        int ActiveMapSlot,
        long MapStartedUtc,
        long MapDurationSeconds,
        int CompletionPercent,
        int PortalsRemaining,
        bool BossAmbushPending,
        long RansomGold,
        string RunnerIdsJson,
        string SummaryJson);

    public sealed record RewardItemRow(long Id, int SourceKind, int SourceIndex, int SlotX, int SlotY, string ItemJson);

    public sealed record DisenchantQueueRow(
        int SlotIndex,
        long ItemId,
        long DustTotal,
        long DustGranted,
        double DurationSeconds,
        double ElapsedSeconds);

    public sealed record TownSnapshot(
        TownRow Town,
        IReadOnlyList<ResourceRow> Resources,
        IReadOnlyList<FarmPlotRow> FarmPlots,
        IReadOnlyList<WorkerRow> Workers,
        IReadOnlyList<RecruitRow> Recruits,
        IReadOnlyList<ShipRow> Ships,
        IReadOnlyList<PortQuotaRow> PortQuotas,
        IReadOnlyList<MapDeviceRow> MapDevices,
        IReadOnlyList<RewardItemRow> RewardItems,
        IReadOnlyList<DisenchantQueueRow> DisenchantQueue);

    public static (string Account, string League) ScopeOf(Player player)
    {
        string account = string.IsNullOrEmpty(player.Character.Account) ? Program.CharacterName(player) : player.Character.Account;
        string league = string.IsNullOrEmpty(player.Character.League) ? "Standard" : player.Character.League;
        return (account, league);
    }

    public static TownSnapshot? Load(string account, string league) => Load(Saves.Db, account, league);

    public static TownSnapshot? Load(SaveDatabase db, string account, string league) => db.Read(() =>
    {
        TownRow? town = null;
        using (var c = db.Command(
            "SELECT treasury_gold, last_simulated_utc, last_reroll_utc, reroll_cost_gold, super_reroll_cost_gold, " +
            "shipping_seed, next_worker_id, next_shipment_id, next_map_run_id, tutorial_flags, state_json " +
            "FROM village_towns WHERE account = $a AND league = $l",
            ("$a", account), ("$l", league)))
        using (var r = c.ExecuteReader())
        {
            if (r.Read())
            {
                town = new TownRow(
                    account, league,
                    r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4),
                    r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9),
                    r.GetString(10));
            }
        }
        if (town is null) return null;

        var resources = new List<ResourceRow>();
        using (var c = db.Command(
            "SELECT resource_id, amount, fractional_progress FROM village_resources WHERE account = $a AND league = $l ORDER BY resource_id",
            ("$a", account), ("$l", league)))
        using (var r = c.ExecuteReader())
            while (r.Read()) resources.Add(new(r.GetInt32(0), r.GetInt64(1), r.GetDouble(2)));

        var plots = new List<FarmPlotRow>();
        using (var c = db.Command(
            "SELECT plot_index, crop_resource_id, growth_progress FROM village_farm_plots WHERE account = $a AND league = $l ORDER BY plot_index",
            ("$a", account), ("$l", league)))
        using (var r = c.ExecuteReader())
            while (r.Read()) plots.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetDouble(2)));

        var workers = new List<WorkerRow>();
        using (var c = db.Command(
            "SELECT worker_id, name, portrait_id, wage_per_hour, assigned_job, assigned_slot, status, skills_json " +
            "FROM village_workers WHERE account = $a AND league = $l ORDER BY worker_id",
            ("$a", account), ("$l", league)))
        using (var r = c.ExecuteReader())
            while (r.Read())
                workers.Add(new(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3),
                    r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetString(7)));

        var recruits = new List<RecruitRow>();
        using (var c = db.Command(
            "SELECT slot_index, name, portrait_id, hire_cost_gold, wage_per_hour, skills_json " +
            "FROM village_recruits WHERE account = $a AND league = $l ORDER BY slot_index",
            ("$a", account), ("$l", league)))
        using (var r = c.ExecuteReader())
            while (r.Read())
                recruits.Add(new(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetInt64(4), r.GetString(5)));

        var ships = new List<ShipRow>();
        using (var c = db.Command(
            "SELECT ship_index, state, port_id, departed_utc, arrival_utc, risk_percent, shipment_value, " +
            "cargo_json, crew_json, event_id, ransom_gold, boss_pending " +
            "FROM village_ships WHERE account = $a AND league = $l ORDER BY ship_index",
            ("$a", account), ("$l", league)))
        using (var r = c.ExecuteReader())
            while (r.Read())
                ships.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3), r.GetInt64(4),
                    r.GetInt32(5), r.GetInt64(6), r.GetString(7), r.GetString(8), r.GetInt32(9),
                    r.GetInt64(10), r.GetInt32(11) != 0));

        var quotas = new List<PortQuotaRow>();
        using (var c = db.Command(
            "SELECT port_id, quota_index, resource_id, required_amount, fulfilled_amount, bonus_percent " +
            "FROM village_port_quotas WHERE account = $a AND league = $l ORDER BY port_id, quota_index",
            ("$a", account), ("$l", league)))
        using (var r = c.ExecuteReader())
            while (r.Read())
                quotas.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3), r.GetInt64(4), r.GetInt32(5)));

        var devices = new List<MapDeviceRow>();
        using (var c = db.Command(
            "SELECT device_index, state, active_map_slot, map_started_utc, map_duration_seconds, completion_percent, " +
            "portals_remaining, boss_ambush_pending, ransom_gold, runner_ids_json, summary_json " +
            "FROM village_map_devices WHERE account = $a AND league = $l ORDER BY device_index",
            ("$a", account), ("$l", league)))
        using (var r = c.ExecuteReader())
            while (r.Read())
                devices.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3), r.GetInt64(4),
                    r.GetInt32(5), r.GetInt32(6), r.GetInt32(7) != 0, r.GetInt64(8), r.GetString(9), r.GetString(10)));

        var rewards = new List<RewardItemRow>();
        using (var c = db.Command(
            "SELECT id, source_kind, source_index, slot_x, slot_y, item_json " +
            "FROM village_reward_items WHERE account = $a AND league = $l ORDER BY id",
            ("$a", account), ("$l", league)))
        using (var r = c.ExecuteReader())
            while (r.Read())
                rewards.Add(new(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5)));

        var disenchant = new List<DisenchantQueueRow>();
        using (var c = db.Command(
            "SELECT slot_index, item_id, dust_total, dust_granted, duration_seconds, elapsed_seconds " +
            "FROM village_disenchant_queue WHERE account = $a AND league = $l ORDER BY slot_index",
            ("$a", account), ("$l", league)))
        using (var r = c.ExecuteReader())
            while (r.Read())
                disenchant.Add(new(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetDouble(4), r.GetDouble(5)));

        return new TownSnapshot(town, resources, plots, workers, recruits, ships, quotas, devices, rewards, disenchant);
    });

    public static void Save(TownSnapshot snapshot) => Save(Saves.Db, snapshot);

    public static void Save(SaveDatabase db, TownSnapshot snapshot) => db.Atomically(() =>
    {
        var t = snapshot.Town;
        string a = t.Account, l = t.League;

        using (var c = db.Command(
            "INSERT INTO village_towns (account, league, treasury_gold, last_simulated_utc, last_reroll_utc, " +
            "reroll_cost_gold, super_reroll_cost_gold, shipping_seed, next_worker_id, next_shipment_id, " +
            "next_map_run_id, tutorial_flags, state_json) " +
            "VALUES ($a, $l, $gold, $sim, $reroll, $rc, $src, $seed, $nwid, $nsid, $nmid, $tf, $sj) " +
            "ON CONFLICT(account, league) DO UPDATE SET " +
            "treasury_gold = excluded.treasury_gold, last_simulated_utc = excluded.last_simulated_utc, " +
            "last_reroll_utc = excluded.last_reroll_utc, reroll_cost_gold = excluded.reroll_cost_gold, " +
            "super_reroll_cost_gold = excluded.super_reroll_cost_gold, shipping_seed = excluded.shipping_seed, " +
            "next_worker_id = excluded.next_worker_id, next_shipment_id = excluded.next_shipment_id, " +
            "next_map_run_id = excluded.next_map_run_id, tutorial_flags = excluded.tutorial_flags, " +
            "state_json = excluded.state_json",
            ("$a", a), ("$l", l), ("$gold", t.TreasuryGold), ("$sim", t.LastSimulatedUtc), ("$reroll", t.LastRerollUtc),
            ("$rc", t.RerollCostGold), ("$src", t.SuperRerollCostGold), ("$seed", t.ShippingSeed),
            ("$nwid", t.NextWorkerId), ("$nsid", t.NextShipmentId), ("$nmid", t.NextMapRunId),
            ("$tf", t.TutorialFlags), ("$sj", t.StateJson)))
            c.ExecuteNonQuery();

        DeleteScoped(db, "village_resources", a, l);
        foreach (var row in snapshot.Resources)
        {
            using var c = db.Command(
                "INSERT INTO village_resources (account, league, resource_id, amount, fractional_progress) VALUES ($a, $l, $r, $amt, $fp)",
                ("$a", a), ("$l", l), ("$r", row.ResourceId), ("$amt", row.Amount), ("$fp", row.FractionalProgress));
            c.ExecuteNonQuery();
        }

        DeleteScoped(db, "village_farm_plots", a, l);
        foreach (var row in snapshot.FarmPlots)
        {
            using var c = db.Command(
                "INSERT INTO village_farm_plots (account, league, plot_index, crop_resource_id, growth_progress) VALUES ($a, $l, $p, $crop, $gp)",
                ("$a", a), ("$l", l), ("$p", row.PlotIndex), ("$crop", row.CropResourceId), ("$gp", row.GrowthProgress));
            c.ExecuteNonQuery();
        }

        DeleteScoped(db, "village_workers", a, l);
        foreach (var row in snapshot.Workers)
        {
            using var c = db.Command(
                "INSERT INTO village_workers (account, league, worker_id, name, portrait_id, wage_per_hour, assigned_job, assigned_slot, status, skills_json) " +
                "VALUES ($a, $l, $id, $n, $p, $w, $j, $s, $st, $sk)",
                ("$a", a), ("$l", l), ("$id", row.WorkerId), ("$n", row.Name), ("$p", row.PortraitId),
                ("$w", row.WagePerHour), ("$j", row.AssignedJob), ("$s", row.AssignedSlot), ("$st", row.Status), ("$sk", row.SkillsJson));
            c.ExecuteNonQuery();
        }

        DeleteScoped(db, "village_recruits", a, l);
        foreach (var row in snapshot.Recruits)
        {
            using var c = db.Command(
                "INSERT INTO village_recruits (account, league, slot_index, name, portrait_id, hire_cost_gold, wage_per_hour, skills_json) " +
                "VALUES ($a, $l, $s, $n, $p, $hc, $w, $sk)",
                ("$a", a), ("$l", l), ("$s", row.SlotIndex), ("$n", row.Name), ("$p", row.PortraitId),
                ("$hc", row.HireCostGold), ("$w", row.WagePerHour), ("$sk", row.SkillsJson));
            c.ExecuteNonQuery();
        }

        DeleteScoped(db, "village_ships", a, l);
        foreach (var row in snapshot.Ships)
        {
            using var c = db.Command(
                "INSERT INTO village_ships (account, league, ship_index, state, port_id, departed_utc, arrival_utc, " +
                "risk_percent, shipment_value, cargo_json, crew_json, event_id, ransom_gold, boss_pending) " +
                "VALUES ($a, $l, $si, $st, $p, $dep, $arr, $risk, $val, $cg, $cr, $ev, $rg, $bp)",
                ("$a", a), ("$l", l), ("$si", row.ShipIndex), ("$st", row.State), ("$p", row.PortId),
                ("$dep", row.DepartedUtc), ("$arr", row.ArrivalUtc), ("$risk", row.RiskPercent), ("$val", row.ShipmentValue),
                ("$cg", row.CargoJson), ("$cr", row.CrewJson), ("$ev", row.EventId), ("$rg", row.RansomGold), ("$bp", row.BossPending ? 1 : 0));
            c.ExecuteNonQuery();
        }

        DeleteScoped(db, "village_port_quotas", a, l);
        foreach (var row in snapshot.PortQuotas)
        {
            using var c = db.Command(
                "INSERT INTO village_port_quotas (account, league, port_id, quota_index, resource_id, required_amount, fulfilled_amount, bonus_percent) " +
                "VALUES ($a, $l, $p, $qi, $r, $req, $ful, $bp)",
                ("$a", a), ("$l", l), ("$p", row.PortId), ("$qi", row.QuotaIndex), ("$r", row.ResourceId),
                ("$req", row.RequiredAmount), ("$ful", row.FulfilledAmount), ("$bp", row.BonusPercent));
            c.ExecuteNonQuery();
        }

        DeleteScoped(db, "village_map_devices", a, l);
        foreach (var row in snapshot.MapDevices)
        {
            using var c = db.Command(
                "INSERT INTO village_map_devices (account, league, device_index, state, active_map_slot, map_started_utc, " +
                "map_duration_seconds, completion_percent, portals_remaining, boss_ambush_pending, ransom_gold, runner_ids_json, summary_json) " +
                "VALUES ($a, $l, $di, $st, $slot, $ms, $md, $cp, $pr, $bap, $rg, $rj, $sj)",
                ("$a", a), ("$l", l), ("$di", row.DeviceIndex), ("$st", row.State), ("$slot", row.ActiveMapSlot),
                ("$ms", row.MapStartedUtc), ("$md", row.MapDurationSeconds), ("$cp", row.CompletionPercent),
                ("$pr", row.PortalsRemaining), ("$bap", row.BossAmbushPending ? 1 : 0), ("$rg", row.RansomGold),
                ("$rj", row.RunnerIdsJson), ("$sj", row.SummaryJson));
            c.ExecuteNonQuery();
        }

        DeleteScoped(db, "village_reward_items", a, l);
        foreach (var row in snapshot.RewardItems)
        {
            using var c = db.Command(
                "INSERT INTO village_reward_items (account, league, source_kind, source_index, slot_x, slot_y, item_json) " +
                "VALUES ($a, $l, $sk, $si, $x, $y, $ij)",
                ("$a", a), ("$l", l), ("$sk", row.SourceKind), ("$si", row.SourceIndex),
                ("$x", row.SlotX), ("$y", row.SlotY), ("$ij", row.ItemJson));
            c.ExecuteNonQuery();
        }

        DeleteScoped(db, "village_disenchant_queue", a, l);
        foreach (var row in snapshot.DisenchantQueue)
        {
            using var c = db.Command(
                "INSERT INTO village_disenchant_queue (account, league, slot_index, item_id, dust_total, dust_granted, duration_seconds, elapsed_seconds) " +
                "VALUES ($a, $l, $s, $id, $dt, $dg, $dur, $el)",
                ("$a", a), ("$l", l), ("$s", row.SlotIndex), ("$id", row.ItemId),
                ("$dt", row.DustTotal), ("$dg", row.DustGranted), ("$dur", row.DurationSeconds), ("$el", row.ElapsedSeconds));
            c.ExecuteNonQuery();
        }
    });

    /// <summary>Atomically adjusts a single resource balance (e.g. map ore claim or Refiner's Bargain grant)
    /// and ensures a <c>village_towns</c> row exists for the scope.</summary>
    public static long AddResource(string account, string league, int resourceId, long delta) =>
        AddResource(Saves.Db, account, league, resourceId, delta);

    public static long AddResource(SaveDatabase db, string account, string league, int resourceId, long delta) => db.Atomically(() =>
    {
        using (var ensure = db.Command(
            "INSERT OR IGNORE INTO village_towns (account, league, last_simulated_utc) VALUES ($a, $l, $now)",
            ("$a", account), ("$l", league), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())))
            ensure.ExecuteNonQuery();

        using (var upsert = db.Command(
            "INSERT INTO village_resources (account, league, resource_id, amount, fractional_progress) " +
            "VALUES ($a, $l, $r, max(0, $d), 0.0) " +
            "ON CONFLICT(account, league, resource_id) DO UPDATE SET amount = max(0, amount + $d)",
            ("$a", account), ("$l", league), ("$r", resourceId), ("$d", delta)))
            upsert.ExecuteNonQuery();

        using var read = db.Command(
            "SELECT amount FROM village_resources WHERE account = $a AND league = $l AND resource_id = $r",
            ("$a", account), ("$l", league), ("$r", resourceId));
        return Convert.ToInt64(read.ExecuteScalar());
    });

    static void DeleteScoped(SaveDatabase db, string table, string account, string league)
    {
        using var c = db.Command($"DELETE FROM {table} WHERE account = $a AND league = $l", ("$a", account), ("$l", league));
        c.ExecuteNonQuery();
    }
}
