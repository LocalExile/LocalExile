using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal static readonly GolemCatalog GolemData = new();
    const string GolemSaveKey = "Game.Golems";
    static int GolemStat(IReadOnlyDictionary<uint, int> stats, string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
    static void GolemAdd(Dictionary<uint, int> stats, string name, int value)
    { if (value != 0 && Stats.Key(name) is uint key) stats[key] = ActorScale.Saturate((long)stats.GetValueOrDefault(key) + value); }

    static double SavedLifeFraction(SpawnedMonster monster)
    {
        if (monster.Dead || monster.Life == 0 || monster.MaxLife == 0) return 0;
        double fraction = monster.Life / (double)monster.MaxLife;
        return double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
    }

    static double SavedLifeRegeneration(double carry) =>
        double.IsFinite(carry) && carry is > -1 and < 1 ? carry : 0;

    static GolemState.MinionResourceSnapshot CaptureResources(GolemState.Golem golem, double now) =>
        new(golem.Monster.Life, SavedLifeRegeneration(golem.Regeneration), golem.ShieldPool.Capture(now));

    enum SummonReplacementFamily { Golem, Summon }

    sealed record SummonReplacementResources(uint Life, uint MaximumLife, uint EnergyShield, uint MaximumEnergyShield,
        double LifeRegeneration, EnergyShield.Snapshot EnergyShieldCapture, double CapturedAt, double LastStep);

    sealed record SummonReplacementAdmission(SummonReplacementFamily Family, GolemState.Golem Old, ushort Source,
        int Effect, int Level, int Quality, bool SummonedRecently, Dictionary<uint, int> Own, Dictionary<uint, int> Player,
        GemSkillIds.Gem Identity, StatSourcesResult Sources, object Area, object? Lifetime,
        SummonReplacementResources Resources, double SummonedAt, double LastStep, double Until);

    static bool SameDouble(double left, double right) =>
        BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);

    static bool SameStats(IReadOnlyDictionary<uint, int> left, IReadOnlyDictionary<uint, int> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out int value) && value == pair.Value);

    static SummonReplacementResources CaptureReplacementResources(GolemState.Golem golem, double at) =>
        new(golem.Monster.Life, golem.Monster.MaxLife, golem.ShieldPool.Current, golem.ShieldPool.Maximum,
            golem.Regeneration, golem.ShieldPool.Capture(at), at, golem.LastStep);

    static PlayerSkillSource? CurrentPlayerSkillSourceLocked(ClientState conn, ushort source)
    {
        string name = CharacterName(conn);
        var equipment = ItemsOf(conn).EquipmentSnapshot();
        return PlayerSkillSourcesAt(name, equipment, InactiveItems.GetValueOrDefault(name), LeagueFor(conn),
            GrantedSkillsOf(conn), WornGrantedSkillsOf(conn, equipment)).FirstOrDefault(row => row.Id == source);
    }

    static bool ReplacementRosterCurrentLocked(SummonReplacementFamily family, ClientState conn, GolemState.Golem old) =>
        family == SummonReplacementFamily.Golem
            ? conn.Golems.All.GetValueOrDefault(old.Id) == old
            : conn.Summons.Fighters.GetValueOrDefault(old.Id) == old
                && conn.Summons.Definitions.GetValueOrDefault(old.Id)?.Id == old.Definition.Id;

    static SummonReplacementAdmission? PrepareReplacementLocked(ClientState conn, SummonReplacementFamily family,
        GolemState.Golem old, ushort source, int effect, SupportedGem supported, Dictionary<uint, int> player, double now)
    {
        if (supported.Level <= 0 || supported.Quality < 0
            || old.Monster.Dead || old.Monster.Life == 0 || old.Source != source || old.Definition.Effect != effect
            || conn.Monsters.GetValueOrDefault(old.Id) != old.Monster || !conn.Minions.Owns(old.Id, old.Monster)
            || !ReplacementRosterCurrentLocked(family, conn, old))
            return null;
        var sourceRow = CurrentPlayerSkillSourceLocked(conn, source);
        if (sourceRow is null || !sourceRow.Active || sourceRow.Effect != effect || conn.StatSourcesCache is not { } sources
            || family == SummonReplacementFamily.Summon && !conn.Summons.Until.ContainsKey(old.Id))
            return null;
        double until = family == SummonReplacementFamily.Summon ? conn.Summons.Until[old.Id] : double.PositiveInfinity;
        if (double.IsFinite(until) && now >= until) return null;
        return new(family, old, source, effect, supported.Level, supported.Quality,
            GolemSummonedInPastEightSeconds(old, now),
            new(supported.Stats), new(player), sourceRow.Identity, sources, conn.Instance, CombatLifetime,
            CaptureReplacementResources(old, now), old.SummonedAt, old.LastStep, until);
    }

    static bool SummonReplacementCurrentLocked(ClientState conn, SummonReplacementAdmission admission)
    {
        var old = admission.Old;
        double now = AreaTime(conn);
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0
            || !ReferenceEquals(conn.Instance, admission.Area) || !ReferenceEquals(CombatLifetime, admission.Lifetime)
            || !ReplacementRosterCurrentLocked(admission.Family, conn, old)
            || conn.Monsters.GetValueOrDefault(old.Id) != old.Monster || !conn.Minions.Owns(old.Id, old.Monster)
            || old.Monster.Dead || old.Monster.Life == 0 || old.Source != admission.Source
            || old.Definition.Effect != admission.Effect
            || GolemSummonedInPastEightSeconds(old, now) != admission.SummonedRecently)
            return false;
        var captured = admission.Resources;
        if (old.Monster.Life != captured.Life || old.Monster.MaxLife != captured.MaximumLife
            || old.ShieldPool.Current != captured.EnergyShield || old.ShieldPool.Maximum != captured.MaximumEnergyShield
            || !SameDouble(old.Regeneration, captured.LifeRegeneration)
            || !SameDouble(old.LastStep, captured.LastStep)
            || old.ShieldPool.Capture(captured.CapturedAt) != captured.EnergyShieldCapture)
            return false;
        using var scope = new PlayerStatScope(conn, fresh: true);
        var player = CharacterPlayerStats(conn);
        var supported = SupportedSkill(conn, admission.Source);
        var sourceRow = CurrentPlayerSkillSourceLocked(conn, admission.Source);
        if (!supported.RequirementsMet || supported.Effect != admission.Effect
            || supported.Level <= 0 || supported.Quality < 0
            || supported.Level != admission.Level || supported.Quality != admission.Quality
            || !SameStats(supported.Stats, admission.Own) || !SameStats(player, admission.Player)
            || sourceRow is null || !sourceRow.Active || sourceRow.Effect != admission.Effect
            || sourceRow.Identity != admission.Identity
            || conn.StatSourcesCache is not { } sources || !ReferenceEquals(sources, admission.Sources)
            || !sources.Key.Matches(admission.Sources.Key))
            return false;
        if (admission.Family == SummonReplacementFamily.Summon
            && (!conn.Summons.Until.TryGetValue(old.Id, out double until)
                || !SameDouble(until, admission.Until) || double.IsFinite(until) && AreaTime(conn) >= until))
            return false;
        return admission.Family switch
        {
            SummonReplacementFamily.Golem => conn.Golems.All.GetValueOrDefault(old.Id) == old,
            SummonReplacementFamily.Summon => conn.Summons.Fighters.GetValueOrDefault(old.Id) == old,
            _ => false,
        };
    }

    static void TransferIncomingReplacementStateLocked(GolemState.Golem oldGolem, GolemState.Golem replacement, double now)
    {
        var old = oldGolem.Monster;
        var target = replacement.Monster;
        old.Ignites.MoveTo(target.Ignites);
        old.Bleeds.MoveTo(target.Bleeds);
        old.Poisons.MoveTo(target.Poisons);
        old.Ailments.MoveTo(target.Ailments);

        target.ReceivedActionFactor = old.ReceivedActionFactor;
        target.ReceivedActionUntil = old.ReceivedActionUntil;
        target.ReceivedActionFloor = old.ReceivedActionFloor;
        target.RecipientDebuffRate = old.RecipientDebuffRate;
        target.FlinchUntil = old.FlinchUntil;
        target.StunImmuneUntil = old.StunImmuneUntil;
        if (double.IsFinite(old.BusyUntil) && old.BusyUntil > now) target.BusyUntil = old.BusyUntil;
        if (oldGolem.HolyRegenerationUntil > now)
        {
            replacement.HolyRegenerationUntil = oldGolem.HolyRegenerationUntil;
            replacement.HolyRegenerationPerSecond = oldGolem.HolyRegenerationPerSecond;
        }
    }

    static void RetireReplacementLocked(ClientState conn, SummonReplacementAdmission admission, double now)
    {
        if (admission.Family == SummonReplacementFamily.Golem) RemoveGolemLocked(conn, admission.Old, now);
        else RemoveSummonLocked(conn, admission.Old, now);
    }

    static double SavedPositiveDelay(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? seconds : .001;

    internal static bool GolemSummonedInPastEightSeconds(ClientState conn, double at)
    {
        lock (conn.Monsters)
        {
            double last = conn.Golems.LastSummoned;
            return double.IsFinite(last) && at >= last && at - last < 8;
        }
    }

    static double GolemSummonedSecondsRemaining(GolemState.Golem golem, double now) =>
        !golem.Monster.Dead && GolemSummonedInPastEightSeconds(golem, now)
            ? Math.Clamp(8 - Math.Max(0, now - golem.SummonedAt), 0, 8) : 0;

    static void AddGolemStats(ClientState conn, Dictionary<uint, int> stats)
    {
        AddSummonCounts(conn, stats);
        lock (conn.Monsters)
        {
            if (Stats.Key("number_of_active_mines") is uint mines)
                stats[mines] = conn.Placed.All.Values.Count(p => p.Mine);
            if (Stats.Key("number_of_active_traps") is uint traps)
                stats[traps] = conn.Placed.All.Values.Count(p => !p.Mine);
        }
        lock (conn.Monsters)
            if (conn.PlayerLife > 0)
                foreach (var (key, value) in conn.Golems.OwnerStats)
                    stats[key] = ActorScale.Saturate((long)stats.GetValueOrDefault(key) + value);
    }

    internal static void SaveGolems(ClientState conn)
    {
        string text;
        lock (conn.Monsters)
        {
            if (!conn.Golems.Restored) return;
            double now = AreaTime(conn);
            text = JsonSerializer.Serialize(conn.Golems.All.Values.Where(g => !g.Monster.Dead || double.IsFinite(g.ReviveAt))
                .Select(g => new GolemState.Saved(g.Definition.Id, g.Source,
                    SavedLifeFraction(g.Monster), g.Monster.Dead ? SavedPositiveDelay(g.ReviveAt - now) : 0,
                    GolemSummonedSecondsRemaining(g, now), CaptureResources(g, now)))
                .Concat(conn.Golems.Waiting.Select(w => w.Saved with
                    { ReviveSeconds = SavedPositiveDelay(w.At - now) })).ToArray());
        }
        CharacterSaves.ChangeModuleData(CharacterName(conn), GolemSaveKey, _ => text);
    }

    static async Task<bool> CastGolem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort source, GolemCatalog.Definition definition, (int X, int Y) aim, int recovery, Func<bool> current)
    {
        double release = AreaTime(conn) + (CastMs(conn, definition.Effect, source) ?? 1000) / 1000.0;
        var lifetime = CombatLifetime;
        while (true)
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || !ReferenceEquals(CombatLifetime, lifetime) || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0
                    || recovery != conn.ActionInterruptionGeneration || !current()) return false;
                if (!conn.Paused && AreaTime(conn) >= release) break;
            }
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        await SummonGolem(remote, stream, ciphers, conn, source, definition, aim, current: current);
        return true;
    }

    internal static async Task<uint> SummonGolem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort source, GolemCatalog.Definition definition, (int X, int Y) aim, double life = 1, bool arrival = false,
        Func<bool>? current = null, double summonedSecondsRemaining = 0,
        GolemState.MinionResourceSnapshot? resources = null, bool legacyArrival = false,
        GolemState.Golem? replacingOld = null)
    {
        double preparedNow = AreaTime(conn);
        SupportedGem supported;
        Dictionary<uint, int> player;
        SummonReplacementAdmission? replacement;
        using (ProjectileMutation(conn, advance: false, phase: "golem replacement preparation"))
        using (new PlayerStatScope(conn, fresh: true))
        {
            supported = SupportedSkill(conn, source);
            player = CharacterPlayerStats(conn);
            replacement = replacingOld is null ? null
                : PrepareReplacementLocked(conn, SummonReplacementFamily.Golem, replacingOld, source,
                    definition.Effect, supported, player, preparedNow);
            if (replacingOld is not null && replacement is null) return 0;
        }
        if (supported.Effect != definition.Effect || !supported.RequirementsMet
            || Monsters.Find(definition.Variety) is not { Supported: true } variety) return 0;
        int Own(string name) => GolemStat(supported.Stats, name);
        int Player(string name) => GolemStat(player, name);
        int limit = GolemCatalog.Limit(Own("base_number_of_golems_allowed"), Player);
        if (limit == 0) return 0;
        byte level = (byte)Math.Clamp(Own("base_display_minion_actor_level"), 1, 100);
        double remaining = arrival && double.IsFinite(summonedSecondsRemaining)
            ? Math.Clamp(summonedSecondsRemaining, 0, 8) : arrival ? 0 : 8;
        double summonedAt = replacement?.SummonedAt ?? preparedNow - (8 - remaining);
        var probe = new GolemState.Golem(0, null!, definition, source)
        {
            GemLevel = supported.Level, Quality = supported.Quality, Own = supported.Stats, Player = new(player),
            Limit = limit, SummonedAt = summonedAt,
        };
        var mods = GolemModifiers(conn, probe, player, preparedNow);
        var pools = GolemPools.Calculate(Monsters, variety, level, probe, player, mods.Effects);
        uint transportMaximum = pools.Fatal ? 1 : pools.MaximumLife;
        uint maximumEnergyShield = NativeEnergyShield(pools.MaximumEnergyShield);
        uint wireLife = pools.Fatal ? 1 : replacement is { } liveReplacement
            ? Math.Min(liveReplacement.Resources.Life, pools.MaximumLife)
            : resources is { } saved
            ? Math.Min(saved.Life, pools.MaximumLife)
            : (uint)Math.Clamp(pools.MaximumLife * (double.IsFinite(life) ? Math.Clamp(life, 0, 1) : 0),
                0, pools.MaximumLife);
        uint energyShield = replacement is { } liveShield
            ? Math.Min(liveShield.Resources.EnergyShield, maximumEnergyShield)
            : resources is { } savedShield
            ? Math.Min(savedShield.EnergyShield.Current, maximumEnergyShield)
            : legacyArrival ? 0 : maximumEnergyShield;
        if (!pools.Fatal)
        {
            probe.Abilities = GolemAbilities(conn, variety, level, probe, player, preparedNow);
            probe.Skills = GolemNativeSkills(conn, level, probe.Abilities);
        }
        var at = definition.Kind == "Flame" && definition.Special && !arrival ? aim : NextToPlayer(conn);
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Normal, at.X, at.Y,
            friendly: true, mods: mods, unrisen: false, extraStats: MinionStats,
            ownedSkills: probe.Skills, ownedMaximumLife: transportMaximum, ownedMaximumEnergyShield: maximumEnergyShield,
            ownedEnergyShield: energyShield,
            damagedLife: wireLife,
            ownedInitialDeath: pools.Fatal,
            ownedCurrent: () => conn.PlayerLife > 0 && (current?.Invoke() ?? true)
                && (replacement is { } admission
                    ? SummonReplacementCurrentLocked(conn, admission)
                    : GemSkillEffects(conn).Any(g => g.Id == source && g.Effect == definition.Effect)),
            owned: (id, m) =>
            {
                double committed = AreaTime(conn);
                SummonReplacementResources? committedResources = replacement is null
                    ? null : CaptureReplacementResources(replacement.Old, committed);
                probe.Id = id; probe.Monster = m;
                if (replacement is not null)
                    TransferIncomingReplacementStateLocked(replacement.Old, probe, committed);
                if (replacement is not null) RetireReplacementLocked(conn, replacement, committed);
                else if (conn.Golems.Replacement(definition.Kind, limit) is { } old)
                    RemoveGolemLocked(conn, old, committed);
                probe.SummonedAt = replacement?.SummonedAt ?? committed - (8 - remaining);
                probe.LastStep = replacement?.LastStep ?? committed;
                probe.ShieldPool.SetMaximum(maximumEnergyShield,
                    refill: committedResources is null && resources is null && !legacyArrival);
                if (committedResources is { } liveResources)
                {
                    probe.Regeneration = SavedLifeRegeneration(liveResources.LifeRegeneration);
                    probe.ShieldPool.Restore(liveResources.EnergyShieldCapture, committed);
                }
                else if (resources is { } savedResources)
                {
                    probe.Regeneration = SavedLifeRegeneration(savedResources.LifeRegeneration);
                    probe.ShieldPool.Restore(savedResources.EnergyShield, committed);
                }
                else if (legacyArrival) probe.ShieldPool.Interrupt(committed);
                m.Mods = mods;
                conn.Golems.All.Add(id, probe);
                conn.Minions.Add(new(id, m, definition.Id, definition.Kind) { Until = double.PositiveInfinity });
                m.NoRewards = true; m.Aggro = true;
                if (!arrival) conn.Golems.LastSummoned = committed;
                conn.Golems.RefreshAt = 0;
                if (!pools.Fatal) PresentTransferredGolemDotBuffsLocked(conn, probe, committed);
                if (pools.Fatal) GolemDieLocked(conn, probe, committed, emitLife: false);
            });
        if (id == 0) return 0;
        if (!arrival && definition.Kind == "Flame" && definition.Special && !pools.Fatal)
            using (ProjectileMutation(conn))
                if (conn.Golems.All.TryGetValue(id, out var spawned)) GolemMeteorLocked(remote, stream, ciphers, conn, spawned, AreaTime(conn));
        await RefreshGolemBuffs(remote, stream, ciphers, conn);
        SaveGolems(conn);
        return id;
    }

    static void RemoveGolemLocked(ClientState conn, GolemState.Golem golem, double now)
    {
        conn.Golems.All.Remove(golem.Id); conn.Minions.Remove(golem.Id);
        conn.Golems.IncomingHits.RemoveAll(hit => ReferenceEquals(hit.Target, golem));
        golem.Pending = null;
        foreach (var hazard in golem.Hazards) if (hazard.Object is uint art)
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(art), null));
        golem.Hazards.Clear(); golem.Pulses.Clear(); golem.ReviveAt = double.PositiveInfinity;
        golem.Monster.ActionGeneration++; golem.Monster.Dead = true; golem.Monster.Life = 0;
        if (conn.Monsters.TryGetValue(golem.Id, out var held) && ReferenceEquals(held, golem.Monster))
        {
            conn.Monsters.Remove(golem.Id); conn.MinionOrigins.TryRemove(golem.Id, out _);
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(golem.Id), "golem removed", golem.Id,
                Kind: PlayerMinions.OutputKind.Remove));
        }
        conn.Golems.RefreshAt = 0;
    }

    internal static async Task GolemsTick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double now)
    {
        if (!conn.PlayerSent || !CurrentGameWork || conn.Closed || conn.LeavingArea) return;
        if (!conn.Golems.Restored)
        {
            GolemState.Saved[] saved;
            lock (conn.Monsters)
            {
                conn.Golems.Restored = true;
                try { saved = JsonSerializer.Deserialize<GolemState.Saved[]>(CharacterSaves.ModuleData(CharacterName(conn), GolemSaveKey) ?? "[]") ?? []; }
                catch (JsonException) { saved = []; }
            }
            var gems = GemSkillEffects(conn).ToArray();
            foreach (var old in saved.Take(100))
            {
                if (GolemData.Find(old.Effect) is not { } definition) continue;
                var source = gems.FirstOrDefault(g => g.Id == old.Skill && g.Effect == definition.Effect);
                if (source.Name is null) source = gems.FirstOrDefault(g => g.Effect == definition.Effect);
                if (source.Name is null) continue;
                if (old.ReviveSeconds > 0)
                { conn.Golems.Waiting.Add(new(old with { Skill = source.Id }, now + old.ReviveSeconds)); continue; }
                await SummonGolem(remote, stream, ciphers, conn, source.Id, definition, NextToPlayer(conn),
                    double.IsFinite(old.Life) ? Math.Clamp(old.Life, 0, 1) : 0, arrival: true,
                    summonedSecondsRemaining: old.SummonedSecondsRemaining, resources: old.Resources,
                    legacyArrival: old.Resources is null);
            }
        }
        foreach (var revive in conn.Golems.Waiting.Where(w => conn.PlayerLife > 0 && now >= w.At).ToArray())
        {
            conn.Golems.Waiting.Remove(revive);
            if (GolemData.Find(revive.Saved.Effect) is { } definition)
                await SummonGolem(remote, stream, ciphers, conn, revive.Saved.Skill, definition, NextToPlayer(conn));
        }
        // Deadlines run at the companion cadence, independently of the slower stat-source refresh.
        GolemState.Golem[] due;
        lock (conn.Monsters)
        {
            foreach (var ended in conn.Golems.All.Values.Where(g => (g.Monster.Dead || g.Monster.Life == 0) && !double.IsFinite(g.ReviveAt)))
                GolemDieLocked(conn, ended, now);
            due = conn.PlayerLife > 0 ? conn.Golems.All.Values.Where(g => g.Monster.Dead && now >= g.ReviveAt).ToArray() : [];
        }
        foreach (var dead in due)
            {
                lock (conn.Monsters) RemoveGolemLocked(conn, dead, now);
                await SummonGolem(remote, stream, ciphers, conn, dead.Source, dead.Definition, NextToPlayer(conn));
            }
        bool changed = false;
        if (conn.Golems.All.Count == 0) return;
        var rebuild = new List<GolemState.Golem>();
        if (now >= conn.Golems.RefreshAt)
        {
            using var scope = new PlayerStatScope(conn, fresh: true);
            var player = CharacterPlayerStats(conn);
            var sources = GemSkillEffects(conn).ToDictionary(g => g.Id, g => g.Effect);
            lock (conn.Monsters)
            {
                conn.Golems.RefreshAt = now + .5;
                foreach (var golem in conn.Golems.All.Values.ToArray())
                {
                    if (!sources.TryGetValue(golem.Source, out int effect) || effect != golem.Definition.Effect)
                    { RemoveGolemLocked(conn, golem, now); changed = true; continue; }
                    var gem = SupportedSkill(conn, golem.Source);
                    if (!gem.RequirementsMet) { RemoveGolemLocked(conn, golem, now); changed = true; continue; }
                    if (golem.Monster.Dead)
                    {
                        continue;
                    }
                    golem.Own = gem.Stats; golem.GemLevel = gem.Level; golem.Quality = gem.Quality;
                    golem.Limit = GolemCatalog.Limit(GolemStat(gem.Stats, "base_number_of_golems_allowed"), n => GolemStat(player, n));
                    int level = GolemStat(gem.Stats, "base_display_minion_actor_level");
                    if (level != golem.Monster.Level)
                    {
                        rebuild.Add(golem); continue;
                    }
                    RefreshGolemLocked(conn, golem, player, now);
                }
                int limit = conn.Golems.All.Values.Select(g => g.Limit).DefaultIfEmpty(0).Max();
                foreach (var surplus in conn.Golems.All.Values.OrderByDescending(g => g.SummonedAt).Skip(limit).ToArray())
                { RemoveGolemLocked(conn, surplus, now); changed = true; }
            }
            await RefreshGolemBuffs(remote, stream, ciphers, conn);
        }
        foreach (var golem in rebuild)
            if (await SummonGolem(remote, stream, ciphers, conn, golem.Source, golem.Definition, NextToPlayer(conn),
                arrival: true, replacingOld: golem) != 0)
                changed = true;
        using (ProjectileMutation(conn))
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return;
            GolemIncomingContactsLocked(remote, stream, ciphers, conn, now);
            foreach (var golem in conn.Golems.All.Values.ToArray())
                GolemStepLocked(remote, stream, ciphers, conn, golem, now);
        }
        if (changed) SaveGolems(conn);
        await PublishMinionOutcomes(remote, stream, ciphers, conn);
    }

    internal static async Task RefreshGolemBuffs(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var player = CharacterPlayerStats(conn);
        await Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return null;
                var bytes = new List<byte>(); var totals = new Dictionary<uint, int>();
                var alive = conn.Golems.All.Values.Where(g => !g.Monster.Dead && g.Monster.Life > 0).ToArray();
                var buffs = new Dictionary<ushort, GolemState.Golem>();
                var changedRecovery = new HashSet<uint>();
                foreach (var g in alive)
                {
                    int oldGuard = GolemStat(g.OwnerBuff, "melee_damage_removed_from_stone_golem_before_life_or_es_%");
                    g.OwnerBuff = [];
                    if (conn.PlayerLife == 0 || g.Definition.Buff == 0) continue;
                    double scale = 1 + GolemCatalog.BuffEffect(g.Definition, n => GolemStat(player, n), n => GolemStat(g.Own, n), alive.Length) / 100.0;
                    foreach (var (source, target) in g.Definition.BuffStats)
                        GolemAdd(g.OwnerBuff, target, (int)Math.Floor(GolemStat(g.Own, source) * scale));
                    if (g.Definition.Buff == 592 && oldGuard != GolemStat(g.OwnerBuff, "melee_damage_removed_from_stone_golem_before_life_or_es_%"))
                        changedRecovery.Add(g.Id);
                    if (!buffs.TryGetValue(g.Definition.Buff, out var best) || g.OwnerBuff.Values.Sum() > best.OwnerBuff.Values.Sum())
                        buffs[g.Definition.Buff] = g;
                }
                var wrath = alive.Where(g => g.WrathUntil > AreaTime(conn)
                        && Dist(g.Monster.Walk.Position(AreaTime(conn)), conn.Walk.Position(AreaTime(conn))) <= 60)
                    .Select(g => (Golem: g, Skill: g.Abilities.FirstOrDefault(a => a.Effect == 5906))).Where(p => p.Skill is not null)
                    .OrderByDescending(p => p.Golem.Monster.Level).FirstOrDefault();
                foreach (var (row, old) in conn.Golems.Buffs.ToArray())
                    if (row == 676 ? wrath.Skill is null || wrath.Golem.Id != old.Source
                        : !buffs.TryGetValue(row, out var next) || old.Source != next.Id || row == 592 && changedRecovery.Contains(old.Source))
                    { bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, old.Instance)); conn.Golems.Buffs.Remove(row); }
                foreach (var (row, g) in buffs)
                {
                    if (!conn.Golems.Buffs.ContainsKey(row))
                    {
                        ushort instance = conn.NextBuff++;
                        bytes.AddRange(GolemOwnerBuffPacket(instance, row, g));
                        conn.Golems.Buffs[row] = (g.Id, instance);
                    }
                    foreach (var (k, v) in g.OwnerBuff) totals[k] = totals.GetValueOrDefault(k) + v;
                }
                // Wrath belongs to the Lightning Golem, scales at its level, and has a finite duration/range.
                if (wrath.Skill is not null)
                {
                    foreach (string kind in new[] { "attack", "spell" })
                        foreach (string bound in new[] { "minimum", "maximum" })
                        {
                            string stat = $"{kind}_{bound}_added_lightning_damage";
                            GolemAdd(totals, stat, GolemStat(wrath.Skill.Stats, stat));
                        }
                    if (!conn.Golems.Buffs.ContainsKey(676))
                    {
                        ushort instance = conn.NextBuff++;
                        bytes.AddRange(InstanceEntities.AddBuff(PlayerId, instance, 676, wrath.Golem.Id));
                        conn.Golems.Buffs[676] = (wrath.Golem.Id, instance);
                    }
                }
                GolemAdd(totals, "virtual_total_number_of_active_golems", alive.Length);
                GolemAdd(totals, "virtual_current_number_of_golem_types", alive.Select(g => g.Definition.Kind).Distinct().Count());
                foreach (var family in alive.GroupBy(g => g.Definition.Kind))
                    GolemAdd(totals, "current_number_of_" + (family.Key == "Flame" ? "fire" : family.Key.ToLowerInvariant()) + "_golems", family.Count());
                bool changed = !totals.OrderBy(p => p.Key).SequenceEqual(conn.Golems.OwnerStats.OrderBy(p => p.Key));
                if (changed)
                {
                    conn.Golems.OwnerStats.Clear(); foreach (var entry in totals) conn.Golems.OwnerStats.Add(entry.Key, entry.Value);
                    conn.Golems.Version++; conn.Minions.StatRevision++;
                    conn.Pools = BuildPoolsFor(conn, conn.Pools.Level); conn.ApplyMovementSpeed();
                    using var fresh = new PlayerStatScope(conn, fresh: true);
                    bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                    bytes.AddRange(PlayerSkillsIfChanged(conn, PlayerSkills(conn)));
                }
                return bytes.Count == 0 ? null : [.. bytes];
            }
        });
    }

    internal static byte[] GolemOwnerBuffPacket(ushort instance, ushort row, GolemState.Golem golem) =>
        InstanceEntities.AddBuff(PlayerId, instance, row, golem.Id,
            values: row == 592 ? [(uint)GolemStat(golem.OwnerBuff, "melee_damage_removed_from_stone_golem_before_life_or_es_%")] : null,
            u82: row == 592 ? (ushort)0 : null);
}
