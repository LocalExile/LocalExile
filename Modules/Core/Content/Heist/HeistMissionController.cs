using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Heist;

/// <summary>
/// Per-<see cref="InstanceState"/> Heist mission lifecycle controller driving Alert Level (S→C <c>0x0164</c> kind 2)
/// and Lockdown Countdown (S→C <c>0x0163</c>, <c>MissionTimerType.Heist == 8</c>). Safe across re-entry, party join,
/// and module hot-reload.
/// </summary>
public sealed class HeistMissionController
{
    const string StateKey = "Heist.Mission";
    const float DefaultLockdownSeconds = 20f;

    readonly ConditionalWeakTable<InstanceState, MissionState> _missions = new();
    readonly JsonSerializerOptions _json = new() { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

    public sealed class MissionState
    {
        public float AlertLevel { get; set; }
        public bool LockdownActive { get; set; }
        public bool LockdownFinished { get; set; }
        public float LockdownRemaining { get; set; }
        public int LastSentTimerSeconds { get; set; }

        [System.Text.Json.Serialization.JsonIgnore] public readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>Advances alert level; returns true if this increase crossed <c>1.0</c> and triggered lockdown.</summary>
        public bool AddAlert(float delta, float lockdownDuration = DefaultLockdownSeconds)
        {
            if (LockdownActive || LockdownFinished || !float.IsFinite(delta) || delta <= 0f) return false;
            AlertLevel = Math.Clamp(AlertLevel + delta, 0f, 1f);
            if (AlertLevel >= 1f)
            {
                LockdownActive = true;
                LockdownRemaining = MathF.Max(1f, lockdownDuration);
                LastSentTimerSeconds = (int)MathF.Ceiling(LockdownRemaining);
                return true;
            }
            return false;
        }

        /// <summary>Ticks the lockdown countdown; returns updated whole seconds to send (0 when finished), or null if unchanged.</summary>
        public int? TickLockdown(float dt)
        {
            if (!LockdownActive || LockdownFinished || dt <= 0f) return null;
            LockdownRemaining = MathF.Max(0f, LockdownRemaining - dt);
            int sec = (int)MathF.Ceiling(LockdownRemaining);
            if (LockdownRemaining <= 0f)
            {
                LockdownActive = false;
                LockdownFinished = true;
                LastSentTimerSeconds = 0;
                return 0;
            }
            if (sec != LastSentTimerSeconds)
            {
                LastSentTimerSeconds = sec;
                return sec;
            }
            return null;
        }
    }

    public void Clear() => _missions.Clear();

    public MissionState For(Player player) => _missions.GetValue(player.Instance, instance =>
        instance.ContentState.TryGetValue(StateKey, out var text)
            ? JsonSerializer.Deserialize<MissionState>(text, _json) ?? new()
            : new());

    void Save(Player player, MissionState state) =>
        player.Instance.ContentState[StateKey] = JsonSerializer.Serialize(state, _json);

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static IEnumerable<Player> Clients(Player player) =>
        InGame.Keys.Where(c => !c.Controlled && ReferenceEquals(c.Instance, player.Instance) && Current(c));

    public async Task OnPlayerEntered(Player player)
    {
        if (!HeistTables.IsMissionArea(player.Layout.Id, player.Layout.Hash)) return;
        var state = For(player);
        await state.Gate.WaitAsync();
        try
        {
            await PlayerOf(player).Send(InstanceHeist.AlertLevel(state.AlertLevel),
                $"0x0164 Heist alert level {state.AlertLevel:0.00} (enter)");
            if (state.LockdownActive && state.LockdownRemaining > 0f)
            {
                uint sec = (uint)Math.Max(1, (int)MathF.Ceiling(state.LockdownRemaining));
                await PlayerOf(player).Send(InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Heist, sec),
                    $"0x0163 Heist lockdown timer {sec}s (enter)");
            }
        }
        finally { state.Gate.Release(); }
    }

    public async Task RaiseAlert(Player player, float delta)
    {
        if (!HeistTables.IsMissionArea(player.Layout.Id, player.Layout.Hash)) return;
        var state = For(player);
        float alert;
        bool triggered;
        uint lockdownSec;
        await state.Gate.WaitAsync();
        try
        {
            triggered = state.AddAlert(delta);
            alert = state.AlertLevel;
            lockdownSec = (uint)Math.Max(0, state.LastSentTimerSeconds);
            Save(player, state);
        }
        finally { state.Gate.Release(); }

        var alertPkt = InstanceHeist.AlertLevel(alert);
        var timerPkt = triggered ? InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Heist, lockdownSec) : null;
        foreach (var peer in Clients(player))
        {
            await PlayerOf(peer).Send(alertPkt, $"0x0164 Heist alert level {alert:0.00}");
            if (timerPkt is not null)
                await PlayerOf(peer).Send(timerPkt, $"0x0163 Heist lockdown timer {lockdownSec}s");
        }
    }

    public async Task OnTick(Player player, double dt)
    {
        if (!HeistTables.IsMissionArea(player.Layout.Id, player.Layout.Hash)) return;
        if (!Current(player) || player.Paused || player.PlayerLife == 0) return;
        if (!ReferenceEquals(Clients(player).Where(c => c.PlayerLife > 0).MinBy(c => c.Id), player)) return;

        var state = For(player);
        int? nextSec;
        await state.Gate.WaitAsync();
        try
        {
            nextSec = state.TickLockdown((float)Math.Clamp(dt, 0.0, 0.5));
            if (nextSec is not null) Save(player, state);
        }
        finally { state.Gate.Release(); }

        if (nextSec is int sec)
        {
            var pkt = sec > 0
                ? InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Heist, (uint)sec)
                : InstanceTimers.HideMissionTimer(InstanceTimers.MissionTimerType.Heist);
            foreach (var peer in Clients(player))
                await PlayerOf(peer).Send(pkt, sec > 0 ? $"0x0163 Heist lockdown {sec}s" : "0x0163 Heist lockdown ended");
        }
    }
}
