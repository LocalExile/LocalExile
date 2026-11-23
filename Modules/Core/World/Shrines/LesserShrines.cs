using System.Diagnostics;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
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
        public ItemCatalog? CachedCatalog;
        public SkillCatalog? CachedSkills;
        public (uint Id, Item Item)[]? CachedActiveItems;
        public Grant? CachedGrant;
    }
    sealed record Grant(string Source, int Effect, int Level);
    sealed record Request((int X, int Y) At, double Seconds, string Type);
    static readonly ConditionalWeakTable<PlayerInventories, State> States = new();
    static readonly ConditionalWeakTable<Player, List<Request>> Pending = new();

    static Grant? Equipped(Player player, State state)
    {
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        var skills = SkillsFor(player);
        // Fast check against the last scanned active equipment snapshot; avoids scanning every item's mods on every kill/tick.
        lock (state)
        {
            if (ReferenceEquals(state.CachedCatalog, catalog) && ReferenceEquals(state.CachedSkills, skills)
                && state.CachedActiveItems is { } prev)
            {
                int idx = 0;
                bool same = true;
                foreach (byte row in ItemModStats.EquipmentRows)
                {
                    foreach (var item in items.Items(row))
                    {
                        if (IsInactive(player, item.Id)) continue;
                        if (idx >= prev.Length || prev[idx].Id != item.Id || !ReferenceEquals(prev[idx].Item, item.Item))
                        { same = false; break; }
                        idx++;
                    }
                    if (!same) break;
                }
                if (same && idx == prev.Length) return state.CachedGrant;
            }
            var active = new List<(uint Id, Item Item)>();
            Grant? grant = null;
            foreach (byte row in ItemModStats.EquipmentRows)
            foreach (var item in items.Items(row))
            {
                if (IsInactive(player, item.Id)) continue;
                active.Add((item.Id, item.Item));
                if (grant is not null) continue;
                foreach (var mod in item.Item.Mods)
                foreach (int levelRow in catalog.Mods.ByHash(mod.Hash)?.GrantedEffectLevels ?? [])
                    if (skills.EffectsPerLevel(levelRow) is { } level && skills.EffectId(level.Effect) == "TriggeredSummonLesserShrine")
                    { grant = new($"{item.Id}/{mod.Hash}/{levelRow}", level.Effect, level.Level); break; }
            }
            state.CachedCatalog = catalog;
            state.CachedSkills = skills;
            state.CachedActiveItems = [.. active];
            state.CachedGrant = grant;
            return grant;
        }
    }

    static double Cooldown(Player player, Grant? grant)
    {
        if (grant is null) return 0;
        var stats = PlayerStatValues(player);
        int recovery = Stat.VirtualCooldownSpeedPlusPct is var key ? stats.GetValueOrDefault(key) : 0;
        double rate = Math.Max(0, 1 + recovery / 100.0)
            * Math.Max(0, 1 + (MapModsOf(player)?.Get(Stat.MapPlayerCooldownSpeedPlusPctFinal) ?? 0) / 100.0);
        return rate > 0 ? SkillsFor(player).CooldownMs(grant.Effect, grant.Level) / 1000.0 / rate : double.PositiveInfinity;
    }

    static void Observe(State state, Player player, Grant? grant, double cooldown)
    {
        double now = AreaTime(player);
        if (state.Clock is { } old && !ReferenceEquals(old, player.Clock)) state.Trigger.Rebase(old.Elapsed.TotalSeconds, now);
        state.Clock = player.Clock;
        state.Trigger.Observe(grant?.Source ?? "", now, cooldown);
    }

    public static void OnKill(KillContext kill)
    {
        if (!kill.PlayerKill || kill.Player.PlayerLife == 0) return;
        var player = kill.Player;
        var state = States.GetOrCreateValue(ItemsOf(player));
        var grant = Equipped(player, state);
        double cooldown = Cooldown(player, grant);
        lock (state)
        {
            Observe(state, player, grant, cooldown);
            if (grant is null || !state.Trigger.TryUse(AreaTime(player), cooldown)) return;
        }
        var stats = SkillsFor(player).EffectStats(grant.Effect, grant.Level).ToDictionary(s => s.Item1, s => s.Item2);
        var playerStats = PlayerStatValues(player);
        int Own(uint key) => stats.GetValueOrDefault(key);
        double seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration),
            name => playerStats.GetValueOrDefault(name), Own);
        var choices = ShrineCatalog.All.Where(s => s.Lesser && !s.PvPOnly).ToArray();
        if (seconds <= 0 || choices.Length == 0) return;
        // This request is committed synchronously with the kill, including atomic projectile kills.
        // The tick publishes it through the same instance gate as generated shrines and claims.
        Pending.GetOrCreateValue(player).Add(new(kill.At, seconds, choices[AreaRng(player).Next(choices.Length)].Id));
    }

    public static async Task Tick(Player player)
    {
        Request[] requests;
        var state = States.GetOrCreateValue(ItemsOf(player));
        var grant = Equipped(player, state);
        lock (state) Observe(state, player, grant, Cooldown(player, grant));
        if (!Pending.TryGetValue(player, out var pending)) return;
        requests = [.. pending]; pending.Clear();
        foreach (var request in requests)
            await ShrineManager.SpawnLesser(player, request.At, request.Seconds, request.Type);
    }
}
