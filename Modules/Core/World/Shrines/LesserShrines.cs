using System.Diagnostics;
using System.Runtime.CompilerServices;
using GameServer.Modding;
using static Game;
using static Program;

namespace GameServer.Modules.Shrines;

/// <summary>Create Lesser Shrine starts on cooldown; removing/replacing its granting item starts that cooldown again.</summary>
public sealed class LesserShrineTrigger
{
    public string Source { get; private set; } = "";
    double remaining, updatedAt, recoverySeconds;
    public double ReadyAt => remaining <= 0 ? updatedAt : updatedAt + remaining * recoverySeconds;

    void Settle(double now)
    {
        remaining = recoverySeconds > 0 ? Math.Max(0, remaining - Math.Max(0, now - updatedAt) / recoverySeconds) : 0;
        updatedAt = now;
    }

    public void Observe(string source, double now, double cooldown)
    {
        Settle(now);
        recoverySeconds = cooldown;
        if (source != Source) remaining = source.Length == 0 ? 0 : 1;
        Source = source;
    }
    public void Rebase(double before, double after) { Settle(before); updatedAt = after; }
    public bool TryUse(double now, double cooldown)
    {
        Observe(Source, now, cooldown);
        if (Source.Length == 0 || remaining > 1e-9) return false;
        remaining = 1;
        return true;
    }
}

/// <summary>The Gull grants an on-kill skill through its item's actual GrantedEffectsPerLevelKeys, not a display stat.</summary>
public static class LesserShrines
{
    sealed class State
    {
        public readonly LesserShrineTrigger Trigger = new();
        public Stopwatch? Clock;
    }
    sealed record Grant(string Source, int Effect, int Level);
    sealed record Request((int X, int Y) At, double Seconds, string Type);
    static readonly ConditionalWeakTable<PlayerInventories, State> States = new();
    static readonly ConditionalWeakTable<ClientState, List<Request>> Pending = new();

    static Grant? Equipped(ClientState conn)
    {
        var items = ItemsOf(conn);
        var catalog = ItemsFor(conn);
        var skills = SkillsFor(conn);
        foreach (byte row in ItemModStats.EquipmentRows)
        foreach (var item in items.Items(row))
        {
            if (IsInactive(conn, item.Id)) continue;
            foreach (var mod in item.Item.Mods)
            foreach (int levelRow in catalog.Mods.ByHash(mod.Hash)?.GrantedEffectLevels ?? [])
                if (skills.EffectsPerLevel(levelRow) is { } level && skills.EffectId(level.Effect) == "TriggeredSummonLesserShrine")
                    return new($"{item.Id}/{mod.Hash}/{levelRow}", level.Effect, level.Level);
        }
        return null;
    }

    static double Cooldown(ClientState conn, Grant? grant)
    {
        if (grant is null) return 0;
        var stats = CharacterPlayerStats(conn);
        int recovery = Stats.Key("virtual_cooldown_speed_+%") is uint key ? stats.GetValueOrDefault(key) : 0;
        double rate = Math.Max(0, 1 + recovery / 100.0)
            * Math.Max(0, 1 + (MapModsOf(conn)?.Get("map_player_cooldown_speed_+%_final") ?? 0) / 100.0);
        return rate > 0 ? SkillsFor(conn).CooldownMs(grant.Effect, grant.Level) / 1000.0 / rate : double.PositiveInfinity;
    }

    static void Observe(State state, ClientState conn, Grant? grant, double cooldown)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (state.Clock is { } old && !ReferenceEquals(old, conn.Clock)) state.Trigger.Rebase(old.Elapsed.TotalSeconds, now);
        state.Clock = conn.Clock;
        state.Trigger.Observe(grant?.Source ?? "", now, cooldown);
    }

    public static void OnKill(KillContext kill)
    {
        if (!kill.PlayerKill || kill.Conn.PlayerLife == 0) return;
        var conn = kill.Conn;
        lock (conn.Monsters)
        {
            var grant = Equipped(conn);
            var state = States.GetOrCreateValue(ItemsOf(conn));
            double cooldown = Cooldown(conn, grant);
            lock (state)
            {
                Observe(state, conn, grant, cooldown);
                if (grant is null || !state.Trigger.TryUse(conn.Clock.Elapsed.TotalSeconds, cooldown)) return;
            }
            var stats = SkillsFor(conn).EffectStats(grant.Effect, grant.Level).ToDictionary(s => s.Item1, s => s.Item2);
            var player = CharacterPlayerStats(conn);
            int Own(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
            double seconds = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"),
                name => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0, Own);
            var choices = ShrineCatalog.All.Where(s => s.Lesser && !s.PvPOnly).ToArray();
            if (seconds <= 0 || choices.Length == 0) return;
            // This request is committed synchronously with the kill, including atomic projectile kills.
            // The tick publishes it through the same instance gate as generated shrines and claims.
            Pending.GetOrCreateValue(conn).Add(new(kill.At, seconds, choices[Random.Shared.Next(choices.Length)].Id));
        }
    }

    public static async Task Tick(ClientState conn)
    {
        Request[] requests;
        lock (conn.Monsters)
        {
            var state = States.GetOrCreateValue(ItemsOf(conn));
            var grant = Equipped(conn);
            lock (state) Observe(state, conn, grant, Cooldown(conn, grant));
            if (!Pending.TryGetValue(conn, out var pending)) return;
            requests = [.. pending]; pending.Clear();
        }
        foreach (var request in requests)
            await ShrineManager.SpawnLesser(conn, request.At, request.Seconds, request.Type);
    }
}
