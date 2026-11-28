using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// The generals whose fight is more than one monster (R17, R18). Aukuna (E45) starts mounted (MonsterVarietiesKey2 of her
/// LegionMonsterVarieties row: no drops or experience); the mount's death clip is <c>dismount_01</c>, and on its events the server
/// leaves the Invisible bird object (frozen in the mount's pose by its own .ot, then its <c>explodeepk</c> state) and puts her on foot
/// (MapBoss, the loot) where the mount fell. Hyrri (E46) is two objects: the Fish, the targetable body that carries the loot and moves,
/// and the rider who shoots (untargetable, cannot be damaged, no drops, non-blocking), kept by the server on the Fish's cell; her water
/// clone mortar leaves a clone. The planned monster (the form on foot, the Fish) keeps the plan's place everywhere: the fighter's drops,
/// the Domain slot, its ghost and its revival. The state is per area, saved in the instance's ContentState for a module reload.
/// </summary>
static class LegionGenerals
{
    /// <summary>GUESS: the dismount_01 events (R17: spawnbird 1.73 s, birdepk 1.945 s, phase 2.64 s; the server has no clip clock), and how
    /// long the bird stays after the phase.</summary>
    const double SpawnBirdSeconds = 1.73, BirdExplodeSeconds = 1.945, PhaseSeconds = 2.64, BirdLingerSeconds = 2.0;
    /// <summary>GUESS: a water clone stands where the mortar lands, this long after the rider fires it (the server does not follow the
    /// mortar's flight), lives this long (DiesAfterTime has no duration in the client data), plays its <c>dying</c> state this long
    /// before it goes, and at most this many stand per Hyrri; it lands this far from the target at most (cells).</summary>
    const double CloneFlightSeconds = 1.0, CloneLifeSeconds = 8.0, CloneDyingSeconds = 1.0;
    const int MaxClones = 3, CloneScatter = 8;
    /// <summary>The rider follows the Fish at most this often (a travel moves the Fish over a few frames; one teleport per frame would
    /// flood the clients).</summary>
    const double FollowSeconds = 0.2;
    const string SaveKey = "LegionGenerals";
    static readonly JsonSerializerOptions Json = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>A mounted general: its variety row, level, rarity and monolith (owning_initiator; 0 in the Domain), where it stands (kept
    /// each frame, so a corpse that goes at once still dismounts there); Step 0 alive, 1 dead (dismount_01 plays), 2 the bird stands,
    /// 3 the bird exploded. Stats: its encounter stats (Eternal Conflict's damage), not saved.</summary>
    sealed class Mount
    {
        public uint Id { get; set; }
        public int Row { get; set; }
        public byte Level { get; set; }
        public int Rarity { get; set; }
        public int Initiator { get; set; }
        public double DiedAt { get; set; } = double.NaN;
        public int Step { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public uint Bird { get; set; }
        public double LifeScale { get; set; } = 1;
        [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyDictionary<uint, int>? Stats { get; set; }
    }

    /// <summary>Hyrri's rider on her Fish, the effect of its water clone mortar (-1: none) and when it was last used.</summary>
    sealed class Rider
    {
        public uint Body { get; set; }
        public uint Id { get; set; }
        public int Mortar { get; set; } = -1;
        public double MortarUsed { get; set; } = double.NegativeInfinity;
        public double FollowedAt { get; set; } = double.NegativeInfinity;
        public int Clones { get; set; }
    }

    /// <summary>A short-lived object (the bird, a clone): at Until its Dying state (if any) is set, at RemoveAt it goes.</summary>
    sealed class Expiring
    {
        public uint Id { get; set; }
        public uint Owner { get; set; }
        public double Until { get; set; }
        public double RemoveAt { get; set; } = double.PositiveInfinity;
        public string? Dying { get; set; }
    }

    sealed class PendingClone
    {
        public uint Rider { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public double At { get; set; }
    }

    sealed class Saved
    {
        public List<Mount> Mounts { get; set; } = [];
        public List<Rider> Riders { get; set; } = [];
        public List<Expiring> Expiring { get; set; } = [];
        public List<PendingClone> Pending { get; set; } = [];
    }

    sealed class AreaGenerals(Saved s)
    {
        public readonly Dictionary<uint, Mount> Mounts = s.Mounts.ToDictionary(m => m.Id);
        public readonly Dictionary<uint, Rider> Riders = s.Riders.ToDictionary(r => r.Body);
        public readonly List<Expiring> Expiring = s.Expiring;
        public readonly List<PendingClone> Pending = s.Pending;
        public bool Empty => Mounts.Count == 0 && Riders.Count == 0 && Expiring.Count == 0 && Pending.Count == 0;
        public Saved Snapshot() => new() { Mounts = [.. Mounts.Values], Riders = [.. Riders.Values], Expiring = [.. Expiring], Pending = [.. Pending] };
    }

    static readonly ConditionalWeakTable<GameArea, AreaGenerals> Areas = new();
    static IModuleContext? Module;

    public static void Register(IModuleContext module)
    {
        Module = module;
        module.Lifetime.Own(new Cleanup(() => { Areas.Clear(); Module = null; }));
        GameEvents.AreaTick.Add(module.Manifest.Id, player =>
            LegionMonolith.HasLocator(player) || LegionDomain.IsDomainArea(player.Layout.Id) ? (p, _) => Tick(p) : null);
    }

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    /// <summary>The area's generals: a lookup per frame (no allocation); made once per area, restored from the instance after a module reload.</summary>
    static AreaGenerals Of(Player player) => Areas.TryGetValue(player.Area, out var g) ? g : Create(player);

    static AreaGenerals Create(Player player)
    {
        Saved? saved = null;
        if (player.Instance?.ContentState.TryGetValue(SaveKey, out string? text) == true && !string.IsNullOrWhiteSpace(text))
            try { saved = JsonSerializer.Deserialize<Saved>(text, Json); } catch (JsonException) { }
        Areas.TryAdd(player.Area, new AreaGenerals(saved ?? new Saved()));
        return Areas.TryGetValue(player.Area, out var g) ? g : throw new InvalidOperationException("the area's generals were not kept");
    }

    /// <summary>Saves the area's generals (call under lock(g)); only on a change, never per frame.</summary>
    static void Keep(Player player, AreaGenerals g)
    {
        if (player.Instance is not { } instance) return;
        if (g.Empty) instance.ContentState.TryRemove(SaveKey, out _);
        else instance.ContentState[SaveKey] = JsonSerializer.Serialize(g.Snapshot(), Json);
    }

    /// <summary>Whether the monster is a mounted general: its death is not the general's (no Defeat line, no kill in the Domain), the
    /// form on foot that follows is.</summary>
    public static bool IsMount(Player player, uint id)
    {
        if (!Areas.TryGetValue(player.Area, out var g)) return false;
        lock (g) return g.Mounts.ContainsKey(id);
    }

    /// <summary>A general just started fighting (woken in a map encounter, spawned or revived in the Domain): a mounted one is followed to
    /// its dismount, a Fish gets its rider.</summary>
    public static async Task Woken(Player player, uint id, double lifeScale = 1)
    {
        if (!player.Area.Monsters.TryGetValue(id, out var monster) || monster.Variety is not { } variety) return;
        var data = LegionData.Current;
        if (data.Mounts.TryGetValue(variety.Row, out var mount) && mount.Mounted == variety.Row)
        {
            var states = player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin) ? origin.Object.StateMachine : monster.States;
            int initiator = states?.IndexOf("owning_initiator") is int i and >= 0 && i < states.SharedStates.Count ? states.SharedStates[i] : 0;
            var (x, y) = monster.Walk.Position(global::Game.AreaTime(player));
            var g = Of(player);
            lock (g)
            {
                g.Mounts[id] = new Mount { Id = id, Row = variety.Row, Level = monster.Level, Rarity = (int)monster.Rarity, Initiator = initiator, X = x, Y = y, Stats = monster.EncounterStats, LifeScale = lifeScale };
                Keep(player, g);
            }
        }
        else if (data.Riders.TryGetValue(variety.Row, out var rows))
            await SpawnRider(player, id, monster, rows);
    }

    /// <summary>The rider on the Fish's cell, with the body's encounter states (present, breakout, activated, owning_initiator).</summary>
    static async Task SpawnRider(Player player, uint bodyId, SpawnedMonster body, LegionData.RiderRows rows)
    {
        if (Monsters.Find(rows.Rider.ToString()) is not { Supported: true } variety) return;
        var (x, y) = body.Walk.Position(global::Game.AreaTime(player));
        var bodyStates = player.Instance.World.MonsterOrigins.TryGetValue(bodyId, out var origin) ? origin.Object.StateMachine : body.States;
        uint id = await SpawnMonster(player, variety, body.Level, body.Rarity, x, y,
            friendly: false, unrisen: false, alone: false, instanceOwned: true, bulk: true, log: false, nonBlocking: true,
            placement: MonsterPlacement.Established, extraStats: [(Stat.HideMiniLifeBar, 1)],
            shape: o => o with { StateMachine = Carry(bodyStates, o.StateMachine ?? variety.StateMachine) });
        if (id == 0 || !player.Area.Monsters.TryGetValue(id, out var rider)) return;
        // Its .ot: untargetable, base_cannot_be_damaged, no drops. The server keeps hits, minions and rewards off it too.
        rider.DamageImmune = true;
        rider.NoRewards = true;
        int mortar = LegionSkillHooks.WaterCloneMortarEffect is >= 0 and int mortarEffect && rider.Kit.Any(k => k.Effect == mortarEffect) ? mortarEffect : -1;
        var g = Of(player);
        lock (g)
        {
            g.Riders[bodyId] = new Rider { Body = bodyId, Id = id, Mortar = mortar, MortarUsed = mortar >= 0 ? rider.KitReadyAt.GetValueOrDefault(mortar, double.NegativeInfinity) : 0 };
            Keep(player, g);
        }
        Module?.Log($"Hyrri: rider 0x{id:x} ({variety.Id}) on the body 0x{bodyId:x} at ({x},{y}), kit [{string.Join(",", rider.Kit.Select(k => k.Effect))}]");
    }

    /// <summary>The encounter states of <paramref name="from"/> written onto <paramref name="to"/> (each name both have).</summary>
    static StateMachineData? Carry(StateMachineData? from, StateMachineData? to)
    {
        if (from is null || to is null) return to;
        foreach (string name in (string[])["present", "breakout", "activated", "owning_initiator"])
            if (from.IndexOf(name) is int i and >= 0 && i < from.SharedStates.Count && to.IndexOf(name) >= 0) to = to.Set(name, from.SharedStates[i]);
        return to;
    }

    /// <summary>The area's frame: each mount's dismount, each rider's follow and mortar, the clones and the bird.</summary>
    static Task Tick(Player player)
    {
        if (player.Closed || player.LeavingArea) return Task.CompletedTask;
        var g = Of(player);
        return g.Empty ? Task.CompletedTask : Step(player, g);   // the common case: a cheap test per frame
    }

    static async Task Step(Player player, AreaGenerals g)
    {
        double now = global::Game.AreaTime(player);
        var monsters = player.Area.Monsters;
        List<(Mount Mount, SpawnedMonster? Corpse, int Step)>? dismount = null;
        List<RiderWork>? riders = null;
        List<PendingClone>? clones = null;
        List<(Expiring E, bool Remove)>? expiring = null;
        bool changed = false;
        List<uint>? ended = null;
        lock (g)
        {
            foreach (var mount in g.Mounts.Values)
            {
                monsters.TryGetValue(mount.Id, out var m);
                if (mount.Step == 0)
                {
                    // Gone without a corpse is dead too (a consumed corpse), unless the Domain took it away (its end).
                    if (m is not null) (mount.X, mount.Y) = m.Walk.Position(now);
                    if (m is { Dead: false, Life: > 0 }) continue;
                    if (m is null && LegionDomain.IsDomainArea(player.Layout.Id) && !LegionDomain.Fights(player, mount.Id))
                    {
                        (ended ??= []).Add(mount.Id);
                        continue;
                    }
                    (mount.DiedAt, mount.Step) = (now, 1);
                    // Drops and Killed skip a mount: its reward symbols end here (the form on foot shows them again).
                    player.Area.Buffs.ClearRow(mount.Id, LegionStasis.RewardBuff);
                    changed = true;
                    Module?.Log($"Aukuna: the mount 0x{mount.Id:x} died at ({mount.X},{mount.Y}): dismount_01");
                }
                int due = now >= mount.DiedAt + PhaseSeconds ? 4 : now >= mount.DiedAt + BirdExplodeSeconds ? 3 : now >= mount.DiedAt + SpawnBirdSeconds ? 2 : 1;
                if (due > mount.Step) { (dismount ??= []).Add((mount, m, due)); changed = true; }
            }
            foreach (var r in g.Riders.Values)
                if (CheckRider(player, g, r, now) is { } work) { (riders ??= []).Add(work); changed |= work.Gone || work.Cloned; }
            for (int i = g.Pending.Count - 1; i >= 0; i--)
                if (now >= g.Pending[i].At) { (clones ??= []).Add(g.Pending[i]); g.Pending.RemoveAt(i); changed = true; }
            // A clone plays its dying state, then goes; the bird goes at once.
            for (int i = g.Expiring.Count - 1; i >= 0; i--)
            {
                var e = g.Expiring[i];
                if (now >= e.RemoveAt) { (expiring ??= []).Add((e, true)); g.Expiring.RemoveAt(i); changed = true; }
                else if (now >= e.Until && double.IsPositiveInfinity(e.RemoveAt))
                {
                    e.RemoveAt = now + (e.Dying is null ? 0 : CloneDyingSeconds);
                    if (e.Dying is null) g.Expiring.RemoveAt(i);
                    (expiring ??= []).Add((e, e.Dying is null));
                    changed = true;
                }
            }
            if (expiring is not null)
                foreach (var (e, remove) in expiring)
                    if (remove && g.Riders.Values.FirstOrDefault(r => r.Id == e.Owner) is { } owner) owner.Clones = Math.Max(0, owner.Clones - 1);
            if (riders is not null)
                foreach (var work in riders)
                    if (work.Gone) g.Riders.Remove(work.Rider.Body);
            if (ended is not null) { foreach (uint id in ended) g.Mounts.Remove(id); changed = true; }
            if (changed) Keep(player, g);
        }
        if (dismount is not null)
            foreach (var (mount, corpse, step) in dismount) await Dismount(player, g, mount, corpse, step, now);
        if (riders is not null)
            foreach (var work in riders) await StepRider(player, work, now);
        if (clones is not null)
            foreach (var pending in clones) await SpawnClone(player, g, pending, now);
        if (expiring is not null)
        {
            foreach (var (e, remove) in expiring)
                if (!remove && e.Dying is { } dying && monsters.TryGetValue(e.Id, out var m) && !m.Dead)
                    await LegionStasis.SetState(player, e.Id, m, dying);
            if (expiring.Where(x => x.Remove).Select(x => x.E.Id).ToList() is { Count: > 0 } gone)
                await LegionRevive.Remove(player, gone);
        }
    }

    /// <summary>The steps of dismount_01 due now, from the mount's step up to <paramref name="due"/> (a late frame plays several).</summary>
    static async Task Dismount(Player player, AreaGenerals g, Mount mount, SpawnedMonster? corpse, int due, double now)
    {
        int from;
        lock (g)
        {
            from = mount.Step;
            if (due <= from) return;
            mount.Step = due;
            if (due >= 4) g.Mounts.Remove(mount.Id);
            Keep(player, g);
        }
        bool known = LegionData.Current.Mounts.TryGetValue(mount.Row, out var rows);
        if (known && from < 2 && due >= 2)
        {
            // spawnbird: the bird copies the mount's pose (its .ot: CopyAnimation(reference), PauseAnimation) and the mount drops its own bird.
            if (corpse is not null) await LegionStasis.SetState(player, mount.Id, corpse, "nobird");
            if (Monsters.Find(rows!.Bird.ToString()) is { Supported: true } birdVariety)
            {
                uint bird = await SpawnInert(player, birdVariety, mount.Level, MonsterRarity.Normal, mount.X, mount.Y);
                if (bird != 0)
                    lock (g)
                    {
                        mount.Bird = bird;
                        g.Expiring.Add(new Expiring { Id = bird, Owner = mount.Id, Until = mount.DiedAt + PhaseSeconds + BirdLingerSeconds });
                        Keep(player, g);
                    }
                Module?.Log($"Aukuna: bird 0x{bird:x} at ({mount.X},{mount.Y}) (spawnbird {SpawnBirdSeconds} s)");
            }
        }
        if (from < 3 && due >= 3 && mount.Bird != 0 && player.Area.Monsters.TryGetValue(mount.Bird, out var birdMonster))
            await LegionStasis.SetState(player, mount.Bird, birdMonster, "explodeepk");
        if (due < 4) return;
        // phase: Aukuna on foot where the mount fell; the mount's corpse (made invisible by its own disappear event) goes. Without a form
        // on foot the Domain's slot falls (it revives), a map's general is lost.
        var foot = known && Monsters.Find(rows!.OnFoot.ToString()) is { Supported: true } v ? v : null;
        var (tx, ty) = (corpse?.Target ?? player).Walk.Position(now);
        float rotation = (tx, ty) != (mount.X, mount.Y) ? (float)Math.Atan2(tx - mount.X, -(ty - mount.Y)) : 0f;
        uint id = LegionDomain.IsDomainArea(player.Layout.Id)
            ? await LegionDomain.Dismounted(player, mount.Id, foot, mount.X, mount.Y, rotation)
            : foot is null ? 0 : await OnFoot(player, mount, foot, rotation);
        if (corpse is not null) await LegionRevive.Remove(player, [mount.Id]);
        Module?.Log($"Aukuna: dismounted, mount 0x{mount.Id:x} -> on foot 0x{id:x} ({foot?.Id ?? "unsupported"}) at ({mount.X},{mount.Y})");
    }

    /// <summary>A map encounter's general on foot, as LegionStasis.Wake makes a general: the mount's encounter states, level and life scale
    /// (as more life in its create), its fighter entry (the drops, the Defeat line) with the reward icon and symbols, Eternal Conflict's
    /// buff and damage, and its place among the encounter's released monsters; the Dismount line.</summary>
    static async Task<uint> OnFoot(Player player, Mount mount, MonsterCatalog.Variety foot, float rotation)
    {
        var entry = LegionStasis.FighterOf(player, mount.Id, remove: true);
        int[]? rewards = entry?.Rewards is { Length: > 0 } r ? r : null;
        MonsterModSet? scaled = mount.LifeScale > 1.001
            ? MonsterModSet.None with { Effects = MonsterModEffects.None.Plus(MonsterModCatalog.EncounterLifeStat, (int)Math.Round((mount.LifeScale - 1) * 100)) }
            : null;
        // Fighting as the woken mount was (LegionStasis.Wake): present, broken out, activated, its monolith.
        uint id = await SpawnMonster(player, foot, mount.Level, (MonsterRarity)mount.Rarity, mount.X, mount.Y,
            friendly: false, unrisen: false, alone: false, instanceOwned: true, bulk: true, log: false, placement: MonsterPlacement.Established,
            rotation: rotation, extraStats: LegionStasis.RewardStats(rewards, false), extraMods: scaled, shape: o =>
            {
                var sm = (o.StateMachine ?? foot.StateMachine)?.Set("present", 1).Set("breakout", 1).Set("activated", 1);
                if (mount.Initiator != 0) sm = sm?.Set("owning_initiator", mount.Initiator);
                return o with { StateMachine = sm };
            });
        if (id == 0 || !player.Area.Monsters.TryGetValue(id, out var fighter)) return 0;
        if (mount.Stats is { } stats) fighter.EncounterStats = stats;   // Eternal Conflict's extra damage
        if (entry is not null)
        {
            LegionStasis.AddFighter(player, id, entry with { Variety = foot.Id, X = mount.X, Y = mount.Y, OriginalId = id });
            if (entry.RewardFactor > 1.0) player.Area.Buffs.Set(id, new Buff(LegionStasis.EnhancedBuff, Tag: 1));
            LegionStasis.ShowRewards(player, id, rewards, false);
        }
        LegionMonolith.Replace(player, mount.Id, id);
        await LegionSpeech.Say(player, foot.Id, "Dismount");
        return id;
    }

    /// <summary>An object that only shows (the bird; a clone can be hit): no AI, no body, no rewards; the bird no hits and no bar.</summary>
    static async Task<uint> SpawnInert(Player player, MonsterCatalog.Variety variety, byte level, MonsterRarity rarity, int x, int y, bool hittable = false)
    {
        // A clone is a Legion monster: out of its crystal (present, breakout, activated), or a map draws it encased.
        uint id = await SpawnMonster(player, variety, level, rarity, x, y, nonBlocking: true,
            friendly: false, unrisen: false, alone: false, scripted: true, instanceOwned: true, bulk: true, log: false,
            placement: MonsterPlacement.Established, extraStats: hittable ? null : [(Stat.HideMiniLifeBar, 1), (Stat.VirtualIsHiddenMonster, 1)],
            shape: hittable ? o => o with { StateMachine = (o.StateMachine ?? variety.StateMachine)?.Set("present", 1).Set("breakout", 1).Set("activated", 1) } : null);
        if (id == 0 || !player.Area.Monsters.TryGetValue(id, out var m)) return 0;
        m.NoRewards = true; m.Attack = null; m.Aggro = false; m.BusyUntil = double.PositiveInfinity;
        m.EntranceGrace = !hittable;
        return id;
    }

    /// <summary>What a rider's frame must still do outside the lock: go with its Fish, follow it, or tell of a queued clone.</summary>
    sealed record RiderWork(Rider Rider, SpawnedMonster? Self, bool Gone, (int X, int Y)? Move, bool Cloned);

    /// <summary>The rider's frame under lock(g) (null, and no allocation, when there is nothing to send): gone with its Fish; else a
    /// follow when the Fish moved, and a water clone queued when its mortar's cooldown was restarted (whichever path swung it).
    /// Rider teleports are filtered at kit build time by <see cref="LegionSkillHooks"/>.</summary>
    static RiderWork? CheckRider(Player player, AreaGenerals g, Rider r, double now)
    {
        var monsters = player.Area.Monsters;
        monsters.TryGetValue(r.Body, out var body);
        monsters.TryGetValue(r.Id, out var self);
        if (body is null || body.Dead || body.Life == 0 || self is null) return new RiderWork(r, self, true, null, false);
        (int X, int Y)? move = null;
        bool clone = false;
        var at = body.Walk.Position(now);
        if (at != self.Walk.Position(now) && now - r.FollowedAt >= FollowSeconds) { r.FollowedAt = now; move = at; }
        if (r.Mortar >= 0 && self.KitReadyAt.TryGetValue(r.Mortar, out double used) && used != r.MortarUsed)
        {
            r.MortarUsed = used;
            if (self.Target is { } target && r.Clones < MaxClones)
            {
                var (tx, ty) = target.Walk.Position(now);
                var rng = global::Game.AreaRng(player);
                g.Pending.Add(new PendingClone { Rider = r.Id, X = tx + rng.Next(-CloneScatter, CloneScatter + 1), Y = ty + rng.Next(-CloneScatter, CloneScatter + 1), At = now + CloneFlightSeconds });
                r.Clones++;
                clone = true;
            }
        }
        return move is null && !clone ? null : new RiderWork(r, self, false, move, clone);
    }

    static async Task StepRider(Player player, RiderWork work, double now)
    {
        var r = work.Rider;
        if (work.Gone)
        {
            if (work.Self is not null) await LegionRevive.Remove(player, [r.Id]);
            Module?.Log($"Hyrri: the body 0x{r.Body:x} is dead or gone: rider 0x{r.Id:x} removed");
            return;
        }
        if (work.Move is var (x, y) && work.Self is { } self)
        {
            self.Walk.Teleport(x, y, now);
            await LegionOutput.ToArea(player, p => global::Game.SendWorldRecord(p, r.Id, InstanceProtocol.Teleport(r.Id, x, y), null));
        }
        if (work.Cloned) Module?.Log($"Hyrri: rider 0x{r.Id:x} fired its water clone mortar: a clone is queued ({r.Clones} so far)");
    }

    /// <summary>A water clone where the mortar landed (GUESS: near the target), hittable, no AI; it plays <c>dying</c> at its end.</summary>
    static async Task SpawnClone(Player player, AreaGenerals g, PendingClone pending, double now)
    {
        Rider? owner;
        lock (g) owner = g.Riders.Values.FirstOrDefault(r => r.Id == pending.Rider);
        if (owner is null || !player.Area.Monsters.TryGetValue(owner.Id, out var rider) || rider.Variety is not { } variety
            || LegionData.Current.Riders.Values.FirstOrDefault(rr => rr.Rider == variety.Row) is not { } rows
            || Monsters.Find(rows.WaterClone.ToString()) is not { Supported: true } cloneVariety)
        {
            if (owner is not null) lock (g) owner.Clones = Math.Max(0, owner.Clones - 1);
            return;
        }
        var (x, y) = (pending.X, pending.Y);
        if (player.Layout.Walkable?.Invoke(x, y) == false) (x, y) = player.Layout.Near((x, y));
        uint id = await SpawnInert(player, cloneVariety, rider.Level, rider.Rarity, x, y, hittable: true);
        lock (g)
        {
            if (id == 0) owner.Clones = Math.Max(0, owner.Clones - 1);
            else g.Expiring.Add(new Expiring { Id = id, Owner = owner.Id, Until = now + CloneLifeSeconds, Dying = "dying" });
            Keep(player, g);
        }
        if (id != 0) Module?.Log($"Hyrri: water clone 0x{id:x} at ({x},{y}) for {CloneLifeSeconds} s");
    }
}
