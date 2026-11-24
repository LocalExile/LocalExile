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
/// monolith is <c>locked</c>. The army stands in stasis meanwhile: <see cref="BeginArmy"/> and
/// <see cref="ReleaseArmy"/> are the hand-over points for it. The encounter is the instance's, kept in
/// <c>Instance.ContentState</c>; its objects are the area's, each record sent once (LegionOutput), and a client that
/// enters later is given them by the area as they are.
/// </summary>
static partial class LegionMonolith
{
    /// <summary>The generic locator of the Legion room the generator adds when an area's Legion chance rolls (maps).</summary>
    public const string LocatorType = "Metadata/Terrain/Leagues/Legion/Objects/Legion_EncounterLocator";
    /// <summary>The locators that get a monolith: the generic one and the campaign's fixed ones, which sit inside the areas' own
    /// rooms (chance 0 there): The Climb (1_1_6, flag_hill_1.arm) and The Dried Lake (1_4_2, nightwane_boss.arm). An exact set:
    /// LegionEndgame_EncounterLocator belongs to the endgame (Domain), not to this encounter.</summary>
    static readonly HashSet<string> LocatorTypes =
    [
        LocatorType,
        "Metadata/Terrain/Leagues/Legion/Objects/FirstTimeLegion_EncounterLocator",
        "Metadata/Terrain/Leagues/Legion/Objects/DriedLake_EncounterLocator",
    ];
    public const string InitiatorType = "Metadata/Terrain/Leagues/Legion/Objects/LegionInitiator";
    /// <summary>MiscAnimated 3270 LegionMonolithPulse.</summary>
    const string PulseModel = "Metadata/Effects/Spells/monsters_effects/essence/monolith_pulse/monolith_pulse.ao";
    const string SaveKey = "Legion";
    /// <summary>The seconds between the build-up (charging) and the shatter: the length of monolith_fx.ao's build_up clip (R10 section 1).</summary>
    const double ChargeSeconds = 1.5;
    /// <summary>The cells the player may stand from the monolith to use it.</summary>
    const int Reach = 12;
    /// <summary>GUESS (live check): how long an unbroken general stands fleeing before it is removed.</summary>
    const double FleeSeconds = 2;

    enum Phase { Idle, Charging, Running, Finished }

    sealed record Saved(uint Id, int X, int Y, int Level, int Phase, double ChargeEnd, double EndsAt, uint Pulse, float Rotation = 0, float? Z = null, LegionArmy.ArmyPlan? Plan = null, LegionStasis.Member[]? Army = null, Box[]? Boxes = null, float PulseScale = 1);

    /// <summary>The instance's encounter. Fields change under the object's lock.</summary>
    sealed class Encounter(Saved saved)
    {
        public uint Id = saved.Id;
        public int X = saved.X, Y = saved.Y, Level = saved.Level;
        public Phase Phase = (Phase)saved.Phase;
        public double ChargeEnd = saved.ChargeEnd, EndsAt = saved.EndsAt;
        public uint Pulse = saved.Pulse;
        /// <summary>The scale the pulse ring was sent with (it reaches the army's farthest member).</summary>
        public float PulseScale = saved.PulseScale;
        public float Rotation = saved.Rotation;
        public float? Z = saved.Z;
        public LegionArmy.ArmyPlan? Plan = saved.Plan;
        /// <summary>The spawned army (kept in the save); null until it was spawned.</summary>
        public LegionStasis.Member[]? Army = saved.Army;
        /// <summary>True while BeginArmy still spawns: the early end waits for the whole army (not saved).</summary>
        public bool Spawning;
        /// <summary>The chests the war chests became (kept in the save).</summary>
        public Box[]? Boxes = saved.Boxes;
        public Saved ToSaved() => new(Id, X, Y, Level, (int)Phase, ChargeEnd, EndsAt, Pulse, Rotation, Z, Plan, Army, Boxes, PulseScale);
    }

    static readonly ConditionalWeakTable<object, Encounter> Encounters = new();
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
        module.Lifetime.Own(new Cleanup(() => { Encounters.Clear(); Holds.Clear(); LegionStasis.Fighters.Clear(); anyHold = false; Models = null; }));
        GameEvents.OutgoingDamage.Add(id, (player, _, target, rolled) => Hit(module, player, target, rolled));
        GameEvents.AreaPopulated.Add(id, area => Place(module, area));
        GameEvents.PlayerEntered.Add(id, player => Enter(player));
        GameEvents.AreaTick.Add(id, player => HasLocator(player) ? (p, _) => Tick(module, p) : null);
        GameEvents.ObjectUse.Add(id, (player, use, requestedAt) => Use(module, player, use, requestedAt));
    }

    static bool HasLocator(Player player) => player.Layout.Level?.RoomObjects.Any(o => LocatorTypes.Contains(o.Type)) == true;

    static Encounter? Of(Player player)
    {
        if (player.Instance is not { } instance) return null;
        if (Encounters.TryGetValue(instance, out var e)) return e;
        if (!instance.ContentState.TryGetValue(SaveKey, out string? text)) return null;
        // After a module reload: the saved encounter comes back.
        var saved = JsonSerializer.Deserialize<Saved>(text, Json);
        if (saved is null) return null;
        var restored = Encounters.GetValue(instance, _ => new Encounter(saved));
        // The old module's holds are gone but its members keep EntranceGrace: hold them again (their life is kept).
        if (restored.Army is { } army && restored.Phase != Phase.Finished)
            foreach (var m in army) Arm(player, restored, m);
        // A reload during the reveal or the spawn after the shatter lost the pending steps: reveal what is still hidden.
        if (restored.Phase == Phase.Running && restored.Army is { } standing && standing.Where(m => !m.Revealed).ToList() is { Count: > 0 } hidden)
            global::Game.SendAfter(player, 0, () => RevealMembers(player, restored, hidden));
        return restored;
    }

    static void Keep(Player player, Encounter e) =>
        player.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(e.ToSaved(), Json);

    static IEnumerable<Player> Members(Player player) => LegionOutput.Members(player);

    /// <summary>Whether the area's clients have the object: the area keeps what it wrote of its objects until their removal,
    /// and gives it to each client that joins later (AreaObjectRecords).</summary>
    static bool InArea(Player player, uint id) => player.Area.Objects.Has(id);

    /// <summary>GameEvents.AreaPopulated: a new instance that rolled the monolith room gets its monolith at the room's locator.</summary>
    static async Task Place(IModuleContext module, AreaPopulated area)
    {
        var player = area.Player;
        if (player.Instance is not { } instance || Of(player) is not null) return;
        if (player.Layout.Level?.RoomObjects.FirstOrDefault(o => LocatorTypes.Contains(o.Type)) is not { } locator) return;
        // The room's line has no fixed height (NaN): the object then takes the terrain's.
        float? z = float.IsNaN(locator.Z) ? null : locator.Z;
        var e = new Encounter(new(player.TakeObjectId(), locator.X, locator.Y, area.Level, 0, 0, 0, 0, locator.Rotation, z));
        Keep(player, e);   // saved first: a failure leaves no encounter that the client was not shown
        Encounters.Add(instance, e);
        module.Log($"{PlayerOf(player).Character}: monolith 0x{e.Id:x} placed at ({e.X}, {e.Y}) in {player.Layout.Id}");
        await Show(player, e);
    }

    /// <summary>GameEvents.PlayerEntered: a later player, or a returning one. The area gives its client the monolith, the pulse,
    /// the chests and the army as they are; the army's stasis, which the area does not keep, is sent to that player alone.</summary>
    static async Task Enter(Player player)
    {
        if (Of(player) is not { } e) return;
        await Show(player, e);
        await ShowRules(player, e);
        await LegionStasis.Present(player, e.Army, e.Plan);
        await ShowBoxes(player, e);
    }

    static StateMachineData StatesOf(Phase phase, Encounter? e = null)
    {
        var states = Defaults!;
        if (phase >= Phase.Charging) states = states.Set("charging", 1);
        if (phase >= Phase.Running) states = states.Set("shattered", 1);
        if (phase >= Phase.Finished)
        {
            // The end: shattered=2 plays crystal_out.epk and death_02 and hides the minimap icon, so no crystal stays in the shattered monolith.
            states = states.Set("shattered", 2).Set("locked", 1);
        }
        // The general indicator: bolts from the monolith towards the general while it stands frozen, until it breaks out (R3 section 4).
        if (e?.Army is { } army && army.Any(m => m.Tier == 3 && !m.Chest && !m.Broken)) states = states.Set("spawn_lightning", 1);
        return states;
    }

    /// <summary>The monolith for the area, created once (the area keeps it for the players who enter later; a later call
    /// creates it only if the first create failed), and the player's own part: its blocker before the charge and the timer
    /// while it runs.</summary>
    static async Task Show(Player player, Encounter e)
    {
        Phase phase; uint pulse; double endsAt;
        lock (e) (phase, pulse, endsAt) = (e.Phase, e.Pulse, e.EndsAt);
        if (!InArea(player, e.Id))
        {
            var obj = new QuestObject(e.X, e.Y, Info!.Components, e.Rotation, e.Z, Info.Model) { StateMachine = StatesOf(phase, e) };
            var create = InstanceEntities.CreateObject(e.Id, InstanceEntities.ObjectTypeHash(InitiatorType), obj.Blob());
            bool created = false;
            await LegionOutput.ToArea(player, async p =>
            {
                if (!await global::Game.SendWorldCreate(PlayerOf(p), e.Id, create, $"0x02bb Legion monolith 0x{e.Id:x} at ({e.X}, {e.Y}) phase {phase}")) return;
                created = true;
                await global::Game.SendWorldRecord(PlayerOf(p), e.Id, InstanceProtocol.EnterWakeZone(e.Id));
            });
            if (!created) return;
        }
        // A server-placed blocking object blocks walking until it charges (the client drops its collision then).
        // The phase may have moved on while the create was sent: a pulse is made only while the encounter still runs.
        lock (e) (phase, pulse) = (e.Phase, e.Pulse);
        if (phase == Phase.Idle && Info!.Blocking) player.SetBlocker(e.Id, e.X, e.Y, Math.Max(1, Info.Size));
        if (phase == Phase.Running && pulse != 0) await ShowPulse(player, e, pulse);
        // The timer shows from the shatter: the charge is not part of the countdown.
        if (phase == Phase.Running)
        {
            double left = Math.Ceiling(endsAt - global::Game.AreaTime(player));
            if (left > 0 && left < 3600)
                await PlayerOf(player).Send(InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Legion, (uint)left), $"0x0163 mission timer Legion: {left} s (sync)");
        }
    }

    static async Task ShowPulse(Player player, Encounter e, uint id)
    {
        if (InArea(player, id)) return;
        var create = InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(e.X, e.Y, PulseModel) { Scale = e.PulseScale }.Blob());
        await LegionOutput.ToArea(player, async p =>
        {
            if (await global::Game.SendWorldCreate(PlayerOf(p), id, create, $"0x02bb Legion monolith pulse 0x{id:x}"))
                await global::Game.SendWorldRecord(PlayerOf(p), id, InstanceProtocol.EnterWakeZone(id));
        });
    }

    /// <summary>The Legion timer's length: the LegionBalancePerLevel row with the highest MinLevel &lt;= level (the first row if
    /// none), DurationMs milliseconds, exact (30 s at level 1 to 15 s from 68), times map_legion_duration_+% (client 0x1416290a0).
    /// The endless-mode stat is ignored: endless is the Domain, phase 3.</summary>
    public static double TimerMilliseconds(LegionData data, int level, int durationPercent)
    {
        LegionData.Balance? row = null;
        foreach (var b in data.Balances)
            if (b.MinLevel <= level && (row is null || b.MinLevel > row.MinLevel)) row = b;
        row ??= data.Balances.Count > 0 ? data.Balances[0] : null;
        // Exact: the server's end time is not rounded; only the whole seconds the client's timer shows are (SecondsShown, up).
        return Math.Max(1000, (row?.DurationMs ?? 20000) * Math.Max(0, 100 + durationPercent) / 100.0);
    }

    static uint SecondsShown(double seconds) => (uint)Math.Max(1, Math.Ceiling(seconds));

    static async Task<bool> Use(IModuleContext module, Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (Of(player) is not { } e) return false;
        if (use.TargetId != e.Id) return await OpenBox(player, e, use.TargetId, requestedAt);
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return true;
        lock (e) if (e.Phase != Phase.Idle) return true;
        if (!await WorldOf(player).WalkUpTo(e.X, e.Y, Reach, requestedAt)) return true;
        double seconds;
        string plan;
        lock (e)
        {
            if (e.Phase != Phase.Idle) return true;   // a second use does nothing
            // AreaLeagueStats reads the clicker's atlas passives, as Anarchy does (phase 4 settles whose stats count).
            // TODO(phase 4): the other map_legion_* stats (monster counts, chests, factions) are read with the atlas.
            var stats = global::Game.AreaLeagueStats(player);
            int percent = stats.GetValueOrDefault(Stat.MapLegionDurationPlusPct);
            seconds = TimerMilliseconds(LegionData.Current, e.Level, percent) / 1000.0;
            e.Plan = LegionArmy.Plan(LegionData.Current, e.Level, player.Layout.Id, stats, global::Game.AreaRng(player));
            plan = LegionArmy.Summary(e.Plan);
            double now = global::Game.AreaTime(player);
            (e.Phase, e.ChargeEnd, e.EndsAt) = (Phase.Charging, now + ChargeSeconds, now + ChargeSeconds + seconds);   // the timer starts at the shatter
            Keep(player, e);
        }
        module.Log($"{PlayerOf(player).Character}: monolith activated at ({e.X}, {e.Y}), timer {seconds} s");
        module.Log($"{PlayerOf(player).Character}: {plan}");
        await ToAll(player, e, StatesOf(Phase.Charging, e), "charging=1");
        foreach (var member in Members(player)) await SendRules(member, LegionRules.Add(), "0x001f ruleset LegionRules");
        foreach (var member in Members(player)) member.RemoveBlocker(e.Id);
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
            if (e.Phase == Phase.Charging && now >= e.ChargeEnd) { e.Phase = Phase.Running; pulse = player.TakeObjectId(); step = 1; }   // e.Pulse is set with its scale (RevealArmy)
            else if (e.Phase == Phase.Running && now >= e.EndsAt) { e.Phase = Phase.Finished; pulse = e.Pulse; step = 2; }
            if (step != 0) Keep(player, e);
        }
        if (step == 1)
        {
            await ToAll(player, e, StatesOf(Phase.Running, e), "shattered=1");
            double left = Math.Max(1, e.EndsAt - global::Game.AreaTime(player));
            foreach (var member in Members(player))
                await PlayerOf(member).Send(InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Legion, SecondsShown(left)), $"0x0163 mission timer Legion: {left:0.0} s (at the shatter)");
            foreach (var member in Members(player)) await SendRules(member, RulesBegin(player, e), "0x02c9 LegionRules mode 0 (HUD and area audio)");
            await RevealArmy(module, player, e, pulse);   // the pulse and the reveal start at the same instant
            await EarlyEnd(module, player, e);   // a break-out during the charge could not end it yet
        }
        else if (step == 2) await Finish(module, player, e, pulse, "timer");
    }

    /// <summary>The end of the encounter, reached once: the caller moved the phase to Finished under the encounter's lock (the
    /// timer in Tick, or EarlyEnd). Hides the timer, locks the monolith and releases the army.</summary>
    static async Task Finish(IModuleContext module, Player player, Encounter e, uint pulse, string reason)
    {
        foreach (var member in Members(player))
            await PlayerOf(member).Send(InstanceTimers.HideMissionTimer(InstanceTimers.MissionTimerType.Legion), "0x0163 mission timer Legion: hidden");
        await ToAll(player, e, StatesOf(Phase.Finished, e), "locked=1, shattered=2");
        foreach (var member in Members(player)) await SendRules(member, LegionRules.End(e.Id), "0x02c9 LegionRules mode 5 (finished)");
        if (pulse != 0 && InArea(player, pulse)) await LegionOutput.Remove(player, pulse);
        module.Log($"{PlayerOf(player).Character}: monolith 0x{e.Id:x} ended ({reason})");
        await ReleaseArmy(module, player, reason);
    }

    /// <summary>After a break-out: at least 90% of the plain and elite monsters and every chest, sergeant and general broken out
    /// ends the encounter at once (wiki). One scan of the army per break-out; the phase moves to Finished under the lock, so the
    /// timer and this cannot both finish it.</summary>
    static async Task EarlyEnd(IModuleContext module, Player player, Encounter e)
    {
        uint pulse;
        lock (e)
        {
            if (e.Phase != Phase.Running || e.Spawning || e.Army is not { Length: > 0 } army) return;
            int plain = 0, plainBroken = 0;
            foreach (var m in army)
            {
                if (m.Chest || m.Tier >= 2) { if (!m.Broken) return; }
                else { plain++; if (m.Broken) plainBroken++; }
            }
            if (plainBroken * 10 < plain * 9) return;
            e.Phase = Phase.Finished; pulse = e.Pulse;
            Keep(player, e);
        }
        await Finish(module, player, e, pulse, "early");
    }

    /// <summary>The monolith's states, once to every client in the area (the area keeps the latest for a client that joins).</summary>
    static async Task ToAll(Player player, Encounter e, StateMachineData states, string what)
    {
        if (!InArea(player, e.Id)) return;
        await LegionOutput.ToArea(player, p => global::Game.SendWorldRecord(PlayerOf(p), e.Id,
            InstanceEntities.StateMachineUpdate(e.Id, (byte)StateIndex, states), $"0x02bc Legion monolith 0x{e.Id:x}: {what}"));
    }

    /// <summary>The monolith was used and the timer runs: the two armies stand up frozen around it (LegionStasis.cs).</summary>
    static async Task BeginArmy(IModuleContext module, Player player)
    {
        if (Of(player) is not { } e) return;
        LegionArmy.ArmyPlan? plan;
        lock (e) { if (e.Army is not null || e.Plan is null) return; plan = e.Plan; e.Army = []; e.Spawning = true; }
        // Each member joins e.Army as soon as it stands, so a timer that ends meanwhile (ReleaseArmy) removes it too;
        // once the encounter is Finished a new member is refused and Spawn removes it itself.
        bool Keeps(LegionStasis.Member m)
        {
            lock (e)
            {
                if (e.Phase == Phase.Finished) return false;
                e.Army = [.. e.Army ?? [], m];
                Arm(player, e, m);
                return true;
            }
        }
        // A member that stands after the shatter is revealed at once (RevealArmy revealed the ones standing then).
        async Task Standing(LegionStasis.Member m)
        {
            bool reveal;
            lock (e) reveal = e.Phase == Phase.Running;
            if (reveal) await RevealMembers(player, e, [m], keep: false);
        }
        try { await LegionStasis.Spawn(module, player, plan, e.X, e.Y, e.Level, Keeps, Standing); }
        finally { lock (e) e.Spawning = false; }
        Phase phase;
        lock (e) { phase = e.Phase; Keep(player, e); }
        await EarlyEnd(module, player, e);   // a break-out during the spawn could not end it yet
        // The general indicator is part of the monolith's states: the bolts show where the general stands.
        if (phase != Phase.Finished && e.Army!.Any(m => m.Tier == 3 && !m.Chest)) await ToAll(player, e, StatesOf(phase, e), "spawn_lightning=1");
    }

    /// <summary>The encounter ended (the timer or EarlyEnd). The members that broke out wake and fight (LegionStasis.Wake) and leave
    /// the army list and the holds; the others are removed; a general still frozen flees and goes after <see cref="FleeSeconds"/>.
    /// Chests that were opened or are openable are objects, not members, and stay.</summary>
    static async Task ReleaseArmy(IModuleContext module, Player player, string reason)
    {
        if (Of(player) is not { } e) return;
        if (reason == "early") await BreakRemaining(player, e);   // the 90% rule breaks out the rest (patch 3.29.0), not removes it
        LegionStasis.Member[]? army;
        lock (e) { army = e.Army; e.Army = []; Keep(player, e); }
        if (army is not { Length: > 0 }) return;
        var remove = new List<LegionStasis.Member>();
        var ended = new List<LegionStasis.Member>();
        var flee = new List<LegionStasis.Member>();
        int woken = 0;
        bool generalFought = false;
        foreach (var m in army)
        {
            if (!player.Area.Monsters.TryGetValue(m.Id, out var monster) || monster.Dead) continue;
            if (m.Broken && !m.Chest)
            {
                Holds.Remove(monster);   // its hits count again
                if (await LegionStasis.Wake(player, m, monster, e.X, e.Y)) { woken++; generalFought |= m.Tier == 3; }
                else remove.Add(m);   // no replacement could be made: it goes like an unbroken one
            }
            else if (m.Tier == 3 && !m.Chest) flee.Add(m);
            else remove.Add(m);
            if (m.Revealed && !m.Broken) ended.Add(m);   // still frozen: the action ends (state 5: speed restored, props gone)
        }
        // The action's end (R10: state 5), before the members that are removed go; a hidden one needs none.
        if (ended.Count > 0)
            await LegionOutput.ToArea(player, async p =>
            {
                foreach (var m in ended) await LegionStasis.SendState(p, m.Id, m.Sequence, LegionStasis.Ended, 100, null);
            });
        await LegionStasis.Remove(player, remove);
        foreach (var g in flee)
        {
            if (!player.Area.Monsters.TryGetValue(g.Id, out var monster)) continue;
            Holds.Remove(monster);
            // LIVE CHECK: with the action ended (state 5, sent above) and the animation running again, the client plays its flee animation (fleeing=1).
            if (LegionStasis.SendFreezeBuff)
                await LegionOutput.ToArea(player, async p =>
                {
                    await global::Game.SendWorldRecord(p, g.Id, InstanceEntities.RemoveBuff(g.Id, g.Buff), null);
                    await global::Game.SendWorldRecord(p, g.Id, InstanceProtocol.AnimationUpdate(g.Id, 0, 0, 1), null);
                });
            await LegionStasis.SetState(player, g.Id, monster, "fleeing");
            // The server forgets it now (no leak if the timer below is dropped, and it gives no rewards); only the client's removal waits.
            if (player.Area.Monsters.Remove(g.Id, out var gone)) { gone.Dead = true; gone.Life = 0; gone.ActionGeneration++; }
            player.Instance.World.MonsterOrigins.TryRemove(g.Id, out _);
        }
        // One area timer: it fires if the player who ended the encounter left, and takes the players there then.
        if (flee.Count > 0)
            global::Game.SendAfter(player, FleeSeconds, () => LegionOutput.ToArea(player, async p =>
            {
                foreach (var g in flee) await global::Game.SendWorldRemoval(p, g.Id);
            }));
        string general = flee.Count > 0 ? "fled" : generalFought ? "fought" : "none";
        module.Log($"army released ({reason}): {army.Count(m => m.Broken)} of {army.Length} broken out, general {general}");
        await ToAll(player, e, StatesOf(Phase.Finished, e), "spawn_lightning=0");
    }
}
