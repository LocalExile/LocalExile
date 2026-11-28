-- League+account-scoped persistence tables for Kingsmarch (Settlers of Kalguur).
-- Stored in characters.db alongside character saves and account stashes.

-- Town header: treasury gold, offline simulation clock, recruitment reroll state, and monotonic ID counters.
CREATE TABLE village_towns (
    account TEXT NOT NULL COLLATE NOCASE,
    league TEXT NOT NULL COLLATE NOCASE,
    treasury_gold INTEGER NOT NULL DEFAULT 0,
    last_simulated_utc INTEGER NOT NULL DEFAULT 0,
    last_reroll_utc INTEGER NOT NULL DEFAULT 0,
    reroll_cost_gold INTEGER NOT NULL DEFAULT 0,
    super_reroll_cost_gold INTEGER NOT NULL DEFAULT 0,
    shipping_seed INTEGER NOT NULL DEFAULT 0,
    next_worker_id INTEGER NOT NULL DEFAULT 1,
    next_shipment_id INTEGER NOT NULL DEFAULT 1,
    next_map_run_id INTEGER NOT NULL DEFAULT 1,
    tutorial_flags INTEGER NOT NULL DEFAULT 0,
    state_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(state_json)),
    PRIMARY KEY (account, league)
) STRICT;

-- Town resource balances (VillageResources.dat rows: ores, bars, crops, Thaumaturgic Dust) and sub-unit progress.
CREATE TABLE village_resources (
    account TEXT NOT NULL COLLATE NOCASE,
    league TEXT NOT NULL COLLATE NOCASE,
    resource_id INTEGER NOT NULL,
    amount INTEGER NOT NULL DEFAULT 0,
    fractional_progress REAL NOT NULL DEFAULT 0.0,
    PRIMARY KEY (account, league, resource_id)
) STRICT;

-- Six Kingsmarch farm plots (plot_index 0..5), their assigned crop resource row, and fractional growth progress.
CREATE TABLE village_farm_plots (
    account TEXT NOT NULL COLLATE NOCASE,
    league TEXT NOT NULL COLLATE NOCASE,
    plot_index INTEGER NOT NULL CHECK (plot_index BETWEEN 0 AND 5),
    crop_resource_id INTEGER NOT NULL DEFAULT 0,
    growth_progress REAL NOT NULL DEFAULT 0.0,
    PRIMARY KEY (account, league, plot_index)
) STRICT;

-- Hired Kingsmarch townsfolk (both idle in roster and assigned to jobs, plus captured/lost status).
CREATE TABLE village_workers (
    account TEXT NOT NULL COLLATE NOCASE,
    league TEXT NOT NULL COLLATE NOCASE,
    worker_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    portrait_id INTEGER NOT NULL DEFAULT 0,
    wage_per_hour INTEGER NOT NULL DEFAULT 0,
    assigned_job INTEGER NOT NULL DEFAULT 0,
    assigned_slot INTEGER NOT NULL DEFAULT -1,
    status INTEGER NOT NULL DEFAULT 0,
    skills_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(skills_json)),
    PRIMARY KEY (account, league, worker_id)
) STRICT;

-- Available recruits in Raulf's recruitment pool.
CREATE TABLE village_recruits (
    account TEXT NOT NULL COLLATE NOCASE,
    league TEXT NOT NULL COLLATE NOCASE,
    slot_index INTEGER NOT NULL,
    name TEXT NOT NULL,
    portrait_id INTEGER NOT NULL DEFAULT 0,
    hire_cost_gold INTEGER NOT NULL DEFAULT 0,
    wage_per_hour INTEGER NOT NULL DEFAULT 0,
    skills_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(skills_json)),
    PRIMARY KEY (account, league, slot_index)
) STRICT;

-- Three Kingsmarch ships (ship_index 0..2), active voyage state, cargo, crew, and pirate ambush state.
CREATE TABLE village_ships (
    account TEXT NOT NULL COLLATE NOCASE,
    league TEXT NOT NULL COLLATE NOCASE,
    ship_index INTEGER NOT NULL CHECK (ship_index BETWEEN 0 AND 2),
    state INTEGER NOT NULL DEFAULT 0,
    port_id INTEGER NOT NULL DEFAULT 0,
    departed_utc INTEGER NOT NULL DEFAULT 0,
    arrival_utc INTEGER NOT NULL DEFAULT 0,
    risk_percent INTEGER NOT NULL DEFAULT 0,
    shipment_value INTEGER NOT NULL DEFAULT 0,
    cargo_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(cargo_json)),
    crew_json TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(crew_json)),
    event_id INTEGER NOT NULL DEFAULT 0,
    ransom_gold INTEGER NOT NULL DEFAULT 0,
    boss_pending INTEGER NOT NULL DEFAULT 0 CHECK (boss_pending IN (0, 1)),
    PRIMARY KEY (account, league, ship_index)
) STRICT;

-- Per-port favoured resource quotas and bonus percentages.
CREATE TABLE village_port_quotas (
    account TEXT NOT NULL COLLATE NOCASE,
    league TEXT NOT NULL COLLATE NOCASE,
    port_id INTEGER NOT NULL,
    quota_index INTEGER NOT NULL,
    resource_id INTEGER NOT NULL,
    required_amount INTEGER NOT NULL DEFAULT 0,
    fulfilled_amount INTEGER NOT NULL DEFAULT 0,
    bonus_percent INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (account, league, port_id, quota_index)
) STRICT;

-- Three Kingsmarch Atlas map devices (device_index 0..2), active run progress, and bandit ambush state.
CREATE TABLE village_map_devices (
    account TEXT NOT NULL COLLATE NOCASE,
    league TEXT NOT NULL COLLATE NOCASE,
    device_index INTEGER NOT NULL CHECK (device_index BETWEEN 0 AND 2),
    state INTEGER NOT NULL DEFAULT 0,
    active_map_slot INTEGER NOT NULL DEFAULT -1,
    map_started_utc INTEGER NOT NULL DEFAULT 0,
    map_duration_seconds INTEGER NOT NULL DEFAULT 0,
    completion_percent INTEGER NOT NULL DEFAULT 0,
    portals_remaining INTEGER NOT NULL DEFAULT 0,
    boss_ambush_pending INTEGER NOT NULL DEFAULT 0 CHECK (boss_ambush_pending IN (0, 1)),
    ransom_gold INTEGER NOT NULL DEFAULT 0,
    runner_ids_json TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(runner_ids_json)),
    summary_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(summary_json)),
    PRIMARY KEY (account, league, device_index)
) STRICT;

-- Pending reward items returned by ships or map runners (source_kind distinguishes shipping vs mapping).
CREATE TABLE village_reward_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    account TEXT NOT NULL COLLATE NOCASE,
    league TEXT NOT NULL COLLATE NOCASE,
    source_kind INTEGER NOT NULL,
    source_index INTEGER NOT NULL,
    slot_x INTEGER NOT NULL,
    slot_y INTEGER NOT NULL,
    item_json TEXT NOT NULL CHECK (json_valid(item_json))
) STRICT;
CREATE INDEX village_reward_items_lookup ON village_reward_items (account, league, source_kind, source_index);

-- Rog's disenchanting queue progress keyed to item slots in KalguurDisenchanting1 (inventory row 87).
CREATE TABLE village_disenchant_queue (
    account TEXT NOT NULL COLLATE NOCASE,
    league TEXT NOT NULL COLLATE NOCASE,
    slot_index INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    dust_total INTEGER NOT NULL DEFAULT 0,
    dust_granted INTEGER NOT NULL DEFAULT 0,
    duration_seconds REAL NOT NULL DEFAULT 0.0,
    elapsed_seconds REAL NOT NULL DEFAULT 0.0,
    PRIMARY KEY (account, league, slot_index)
) STRICT;
