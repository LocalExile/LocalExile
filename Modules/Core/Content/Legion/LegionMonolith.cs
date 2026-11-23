using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// The Timeless Monolith: the room <c>legion_monolith.arm</c> only marks a point (<see cref="LocatorType"/>), so the server
/// creates the clickable <see cref="InitiatorType"/> there. Using it charges and shatters the monolith (its StateMachine
/// states <c>charging</c>, then <c>shattered</c>), plays the pulse and starts the Legion timer; when the timer ends the
/// monolith is <c>locked</c>. The army that stands in stasis meanwhile is not built yet: <see cref="BeginArmy"/> and
/// <see cref="ReleaseArmy"/> are the hand-over points for it. The encounter is the instance's, kept in
/// <c>Instance.ContentState</c>, and a client that enters later is shown it as it is.
/// </summary>
static class LegionMonolith
{
    public const string LocatorType = "Metadata/Terrain/Leagues/Legion/Objects/Legion_EncounterLocator";
    public const string InitiatorType = "Metadata/Terrain/Leagues/Legion/Objects/LegionInitiator";
    /// <summary>MiscAnimated 3270 LegionMonolithPulse.</summary>
    const string PulseModel = "Metadata/Effects/Spells/monsters_effects/essence/monolith_pulse/monolith_pulse.ao";
    const string SaveKey = "Legion";
    /// <summary>The seconds between the build-up (charging) and the shatter.</summary>
    const double ChargeSeconds = 1.0;
    /// <summary>The cells the player may stand from the monolith to use it.</summary>
    const int Reach = 12;

    enum Phase { Idle, Charging, Running, Finished }

    sealed record Saved(uint Id, int X, int Y, int Level, int Phase, double ChargeEnd, double EndsAt, uint Pulse, float Rotation = 0, float? Z = null, LegionArmy.ArmyPlan? Plan = null);

    /// <summary>The instance's encounter. Fields change under the object's lock.</summary>
    sealed class Encounter(Saved saved)
    {
        public uint Id = saved.Id;
        public int X = saved.X, Y = saved.Y, Level = saved.Level;
        public Phase Phase = (Phase)saved.Phase;
        public double ChargeEnd = saved.ChargeEnd, EndsAt = saved.EndsAt;
        public uint Pulse = saved.Pulse;
        public float Rotation = saved.Rotation;
        public float? Z = saved.Z;
        public LegionArmy.ArmyPlan? Plan = saved.Plan;
        public Saved ToSaved() => new(Id, X, Y, Level, (int)Phase, ChargeEnd, EndsAt, Pulse, Rotation, Z, Plan);
    }

    static readonly ConditionalWeakTable<object, Encounter> Encounters = new();
    /// <summary>The objects each connection has been sent (a connection is one area visit).</summary>
    static readonly ConditionalWeakTable<Player, HashSet<uint>> Shown = new();
    static readonly JsonSerializerOptions Json = new();

    static QuestObjectTypes.Info? Info;
    static StateMachineData? Defaults;

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }
    static int StateIndex;

    public static void Register(IModuleContext module)
    {
        string id = module.Manifest.Id;
        Info = QuestObjectTypes.Find(InitiatorType);
        Defaults = ObjectTypes.All().Any(t => t.Type == InitiatorType)
            ? ObjectTypes.StateMachine(ObjectTypes.All().First(t => t.Type == InitiatorType).Row, ObjectTypes.SharedStates()) : null;
        StateIndex = Info?.Components.ToList().IndexOf("StateMachine") ?? -1;
        if (Info is null || Defaults is null || StateIndex < 0 || !QuestObject.CanSerialize(Info.Components, true))
        {
            module.Log($"the monolith's type {InitiatorType} is not in the client data or cannot be written: no monolith");
            return;
        }
        // After a hot reload mid-timer, players already in the area see the updates only after they re-enter.
        module.Lifetime.Own(new Cleanup(() => { Encounters.Clear(); Shown.Clear(); }));
        GameEvents.AreaPopulated.Add(id, area => Place(module, area));
        GameEvents.PlayerEntered.Add(id, player => Enter(player));
        GameEvents.AreaTick.Add(id, player => HasLocator(player) ? (p, _) => Tick(module, p) : null);
        GameEvents.ObjectUse.Add(id, (player, use, requestedAt) => Use(module, player, use, requestedAt));
    }

    static bool HasLocator(Player player) => player.Layout.Level?.RoomObjects.Any(o => o.Type == LocatorType) == true;

    static Encounter? Of(Player player)
    {
        if (player.Instance is not { } instance) return null;
        if (Encounters.TryGetValue(instance, out var e)) return e;
        if (!instance.ContentState.TryGetValue(SaveKey, out string? text)) return null;
        // After a module reload: the saved encounter comes back.
        var saved = JsonSerializer.Deserialize<Saved>(text, Json);
        return saved is null ? null : Encounters.GetValue(instance, _ => new Encounter(saved));
    }

    static void Keep(Player player, Encounter e) =>
        player.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(e.ToSaved(), Json);

    static IEnumerable<Player> Members(Player player) => player.Instance.PlayersExcept(player.Id)
        .Prepend(player).Where(p => !p.Closed && !p.LeavingArea && p.Instance == player.Instance).Distinct();

    /// <summary>GameEvents.AreaPopulated: a new instance that rolled the monolith room gets its monolith at the room's locator.</summary>
    static async Task Place(IModuleContext module, AreaPopulated area)
    {
        var player = area.Player;
        if (player.Instance is not { } instance || Of(player) is not null) return;
        if (player.Layout.Level?.RoomObjects.FirstOrDefault(o => o.Type == LocatorType) is not { } locator) return;
        // The room's line has no fixed height (NaN): the object then takes the terrain's.
        float? z = float.IsNaN(locator.Z) ? null : locator.Z;
        var e = new Encounter(new(player.TakeObjectId(), locator.X, locator.Y, area.Level, 0, 0, 0, 0, locator.Rotation, z));
        Keep(player, e);   // saved first: a failure leaves no encounter that the client was not shown
        Encounters.Add(instance, e);
        module.Log($"{PlayerOf(player).Character}: monolith 0x{e.Id:x} placed at ({e.X}, {e.Y}) in {player.Layout.Id}");
        await Show(player, e);
    }

    /// <summary>GameEvents.PlayerEntered: a later player, or a returning one, is shown the monolith as it is now.</summary>
    static async Task Enter(Player player)
    {
        if (Of(player) is { } e) await Show(player, e);
    }

    static StateMachineData StatesOf(Phase phase)
    {
        var states = Defaults!;
        if (phase >= Phase.Charging) states = states.Set("charging", 1);
        if (phase >= Phase.Running) states = states.Set("shattered", 1);
        if (phase >= Phase.Finished) states = states.Set("locked", 1);
        return states;
    }

    static async Task Show(Player player, Encounter e)
    {
        var shown = Shown.GetValue(player, _ => []);
        Phase phase; uint pulse; double endsAt;
        lock (e) (phase, pulse, endsAt) = (e.Phase, e.Pulse, e.EndsAt);
        lock (shown) if (!shown.Add(e.Id)) return;
        var modPlayer = PlayerOf(player);
        var obj = new QuestObject(e.X, e.Y, Info!.Components, e.Rotation, e.Z, Info.Model) { StateMachine = StatesOf(phase) };
        if (!await global::Game.SendWorldCreate(modPlayer, e.Id, InstanceEntities.CreateObject(e.Id, InstanceEntities.ObjectTypeHash(InitiatorType), obj.Blob()),
            $"0x02bb Legion monolith 0x{e.Id:x} at ({e.X}, {e.Y}) phase {phase}"))
        {
            lock (shown) shown.Remove(e.Id);
            return;
        }
        await global::Game.SendWorldRecord(modPlayer, e.Id, InstanceProtocol.EnterWakeZone(e.Id));
        // A server-placed blocking object blocks walking until it charges (the client drops its collision then).
        // The phase may have moved on while the create was sent: a pulse is made only while the encounter still runs.
        lock (e) (phase, pulse) = (e.Phase, e.Pulse);
        if (phase == Phase.Idle && Info.Blocking) player.SetBlocker(e.Id, e.X, e.Y, Math.Max(1, Info.Size));
        if (phase == Phase.Running && pulse != 0) await ShowPulse(player, e, pulse);
        if (phase is Phase.Charging or Phase.Running)
        {
            double left = Math.Ceiling(endsAt - global::Game.AreaTime(player));
            if (left > 0 && left < 3600)
                await modPlayer.Send(InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Legion, (uint)left), $"0x0163 mission timer Legion: {left} s (sync)");
        }
    }

    static async Task ShowPulse(Player player, Encounter e, uint id)
    {
        lock (Shown.GetValue(player, _ => [])) Shown.GetValue(player, _ => []).Add(id);
        var modPlayer = PlayerOf(player);
        await global::Game.SendWorldCreate(modPlayer, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(e.X, e.Y, PulseModel).Blob()), $"0x02bb Legion monolith pulse 0x{id:x}");
        await global::Game.SendWorldRecord(modPlayer, id, InstanceProtocol.EnterWakeZone(id));
    }

    /// <summary>The Legion timer's length: the LegionBalancePerLevel row with the highest MinLevel &lt;= level (the first row if
    /// none), DurationMs / 1000 seconds (30 s at level 1 to 15 s from 68), times map_legion_duration_+% (client 0x1416290a0).
    /// The endless-mode stat is ignored: endless is the Domain, phase 3.</summary>
    public static int TimerSeconds(LegionData data, int level, int durationPercent)
    {
        LegionData.Balance? row = null;
        foreach (var b in data.Balances)
            if (b.MinLevel <= level && (row is null || b.MinLevel > row.MinLevel)) row = b;
        row ??= data.Balances.Count > 0 ? data.Balances[0] : null;
        double seconds = (row?.DurationMs ?? 20000) / 1000.0;
        return Math.Max(1, (int)Math.Round(seconds * Math.Max(0, 100 + durationPercent) / 100));
    }

    static async Task<bool> Use(IModuleContext module, Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (Of(player) is not { } e || use.TargetId != e.Id) return false;
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return true;
        lock (e) if (e.Phase != Phase.Idle) return true;
        if (!await WorldOf(player).WalkUpTo(e.X, e.Y, Reach, requestedAt)) return true;
        int seconds;
        string plan;
        lock (e)
        {
            if (e.Phase != Phase.Idle) return true;   // a second use does nothing
            // AreaLeagueStats reads the clicker's atlas passives, as Anarchy does (phase 4 settles whose stats count).
            // TODO(phase 4): the other map_legion_* stats (monster counts, chests, factions) are read with the atlas.
            var stats = global::Game.AreaLeagueStats(player);
            int percent = stats.GetValueOrDefault(Stat.MapLegionDurationPlusPct);
            seconds = TimerSeconds(LegionData.Current, e.Level, percent);
            e.Plan = LegionArmy.Plan(LegionData.Current, e.Level, player.Layout.Id, stats, global::Game.AreaRng(player));
            plan = LegionArmy.Summary(e.Plan);
            double now = global::Game.AreaTime(player);
            (e.Phase, e.ChargeEnd, e.EndsAt) = (Phase.Charging, now + ChargeSeconds, now + seconds);
            Keep(player, e);
        }
        module.Log($"{PlayerOf(player).Character}: monolith activated at ({e.X}, {e.Y}), timer {seconds} s");
        module.Log($"{PlayerOf(player).Character}: {plan}");
        await ToAll(player, e, StatesOf(Phase.Charging), "charging=1");
        foreach (var member in Members(player)) member.RemoveBlocker(e.Id);
        foreach (var member in Members(player))
            await PlayerOf(member).Send(InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Legion, (uint)seconds), $"0x0163 mission timer Legion: {seconds} s");
        await BeginArmy(module, player);
        return true;
    }

    /// <summary>GameEvents.AreaTick: the shatter after the build-up and the end of the timer. Only areas with the monolith
    /// room run it, and an idle or finished encounter returns at its first check.</summary>
    static async Task Tick(IModuleContext module, Player player)
    {
        if (Of(player) is not { } e || player.Closed || player.LeavingArea) return;
        int step = 0;
        uint pulse = 0;
        lock (e)
        {
            if (e.Phase is Phase.Idle or Phase.Finished) return;
            double now = global::Game.AreaTime(player);
            if (e.Phase == Phase.Charging && now >= e.ChargeEnd) { e.Phase = Phase.Running; e.Pulse = pulse = player.TakeObjectId(); step = 1; }
            else if (e.Phase == Phase.Running && now >= e.EndsAt) { e.Phase = Phase.Finished; pulse = e.Pulse; step = 2; }
            if (step != 0) Keep(player, e);
        }
        if (step == 1)
        {
            await ToAll(player, e, StatesOf(Phase.Running), "shattered=1");
            foreach (var member in Members(player).Where(m => Shown.TryGetValue(m, out var s) && s.Contains(e.Id))) await ShowPulse(member, e, pulse);
        }
        else if (step == 2)
        {
            foreach (var member in Members(player))
                await PlayerOf(member).Send(InstanceTimers.HideMissionTimer(InstanceTimers.MissionTimerType.Legion), "0x0163 mission timer Legion: hidden");
            await ToAll(player, e, StatesOf(Phase.Finished), "locked=1");
            if (pulse != 0) foreach (var member in Members(player).Where(m => Shown.TryGetValue(m, out var s) && s.Contains(pulse))) await global::Game.SendWorldRemoval(member, pulse);
            module.Log($"{PlayerOf(player).Character}: monolith 0x{e.Id:x} timer ended");
            await ReleaseArmy(module, player);
        }
    }

    static async Task ToAll(Player player, Encounter e, StateMachineData states, string what)
    {
        foreach (var member in Members(player))
        {
            if (!Shown.TryGetValue(member, out var shown) || !shown.Contains(e.Id)) continue;
            await global::Game.SendWorldRecord(PlayerOf(member), e.Id, InstanceEntities.StateMachineUpdate(e.Id, (byte)StateIndex, states),
                $"0x02bc Legion monolith 0x{e.Id:x}: {what}");
        }
    }

    /// <summary>Hand-over point for the army tasks: the monolith was used and the timer runs; the two armies are built here.</summary>
    static Task BeginArmy(IModuleContext module, Player player) => Task.CompletedTask;

    /// <summary>Hand-over point for the army tasks: the timer ended; the broken-out monsters wake and the others go.</summary>
    static Task ReleaseArmy(IModuleContext module, Player player) => Task.CompletedTask;
}
