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
                    g.Monster.Dead ? 0 : g.Monster.Life / (double)Math.Max(1, g.Monster.MaxLife),
                    g.Monster.Dead ? Math.Max(.001, g.ReviveAt - now) : 0))
                .Concat(conn.Golems.Waiting.Select(w => w.Saved with { ReviveSeconds = Math.Max(.001, w.At - now) })).ToArray());
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
        ushort source, GolemCatalog.Definition definition, (int X, int Y) aim, double life = 1, bool arrival = false, Func<bool>? current = null)
    {
        var supported = SupportedSkill(conn, source);
        if (supported.Effect != definition.Effect || !supported.RequirementsMet || Monsters.Find(definition.Variety) is not { Supported: true } variety) return 0;
        var player = CharacterPlayerStats(conn);
        int Own(string name) => GolemStat(supported.Stats, name);
        int Player(string name) => GolemStat(player, name);
        int limit = GolemCatalog.Limit(Own("base_number_of_golems_allowed"), Player);
        if (limit == 0) return 0;
        byte level = (byte)Math.Clamp(Own("base_display_minion_actor_level"), 1, 100);
        var probe = new GolemState.Golem(0, null!, definition, source) { GemLevel = supported.Level, Quality = supported.Quality, Own = supported.Stats, Player = new(player), Limit = limit };
        var mods = GolemModifiers(probe, player);
        uint maximum = GolemMaximumLife(variety, level, probe, player);
        probe.Abilities = GolemAbilities(conn, variety, level, probe, player);
        probe.Skills = GolemNativeSkills(conn, level, probe.Abilities);
        var at = definition.Kind == "Flame" && definition.Special && !arrival ? aim : NextToPlayer(conn);
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Normal, at.X, at.Y,
            friendly: true, mods: mods, unrisen: false, extraStats: MinionStats,
            ownedSkills: probe.Skills, ownedMaximumLife: maximum, damagedLife: (uint)Math.Clamp(maximum * life, 1, maximum),
            ownedCurrent: () => conn.PlayerLife > 0 && (current?.Invoke() ?? true)
                && GemSkillEffects(conn).Any(g => g.Id == source && g.Effect == definition.Effect),
            owned: (id, m) =>
            {
                if (conn.Golems.Replacement(definition.Kind, limit) is { } old) RemoveGolemLocked(conn, old, AreaTime(conn));
                probe.Id = id; probe.Monster = m; probe.SummonedAt = probe.LastStep = AreaTime(conn);
                probe.Shield = probe.MaximumShield = GolemShield(probe, player);
                conn.Golems.All.Add(id, probe);
                conn.Minions.Add(new(id, m, definition.Id, definition.Kind) { Until = double.PositiveInfinity });
                m.NoRewards = true; m.Aggro = true;
                conn.Golems.LastSummoned = AreaTime(conn);
                conn.Golems.RefreshAt = 0;
            });
        if (id == 0) return 0;
        if (!arrival && definition.Kind == "Flame" && definition.Special)
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
                await SummonGolem(remote, stream, ciphers, conn, source.Id, definition, NextToPlayer(conn), Math.Max(.001, old.Life), arrival: true);
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
        var rebuild = new List<(GolemState.Golem Golem, double Life)>();
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
                        rebuild.Add((golem, golem.Monster.Life / (double)Math.Max(1, golem.Monster.MaxLife)));
                        RemoveGolemLocked(conn, golem, now); continue;
                    }
                    RefreshGolemLocked(conn, golem, player);
                }
                int limit = conn.Golems.All.Values.Select(g => g.Limit).DefaultIfEmpty(0).Max();
                foreach (var surplus in conn.Golems.All.Values.OrderByDescending(g => g.SummonedAt).Skip(limit).ToArray())
                { RemoveGolemLocked(conn, surplus, now); changed = true; }
            }
            await RefreshGolemBuffs(remote, stream, ciphers, conn);
        }
        foreach (var (golem, fraction) in rebuild)
            await SummonGolem(remote, stream, ciphers, conn, golem.Source, golem.Definition, NextToPlayer(conn), fraction, arrival: true);
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
