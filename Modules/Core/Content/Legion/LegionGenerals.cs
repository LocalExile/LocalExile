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
/// (MapBoss, the loot) where the mount fell. The planned monster (the form on foot) keeps the plan's place everywhere: the fighter's
/// drops, the Domain slot, its ghost and its revival. The state is per area, saved in the instance's ContentState for a module reload.
/// </summary>
static class LegionGenerals
{
    /// <summary>GUESS: the dismount_01 events (R17: spawnbird 1.73 s, birdepk 1.945 s, phase 2.64 s; the server has no clip clock), and how
    /// long the bird stays after the phase.</summary>
    const double SpawnBirdSeconds = 1.73, BirdExplodeSeconds = 1.945, PhaseSeconds = 2.64, BirdLingerSeconds = 2.0;
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
        [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyDictionary<uint, int>? Stats { get; set; }
    }

    /// <summary>A short-lived object (the bird): it goes at Until.</summary>
    sealed class Expiring
    {
        public uint Id { get; set; }
        public uint Owner { get; set; }
        public double Until { get; set; }
    }

    sealed class Saved
    {
        public List<Mount> Mounts { get; set; } = [];
        public List<Expiring> Expiring { get; set; } = [];
    }

    sealed class AreaGenerals(Saved s)
    {
        public readonly Dictionary<uint, Mount> Mounts = s.Mounts.ToDictionary(m => m.Id);
        public readonly List<Expiring> Expiring = s.Expiring;
        public bool Empty => Mounts.Count == 0 && Expiring.Count == 0;
        public Saved Snapshot() => new() { Mounts = [.. Mounts.Values], Expiring = [.. Expiring] };
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

    /// <summary>The area's generals, restored from the instance after a module reload (once per area).</summary>
    static AreaGenerals Of(Player player) => Areas.GetValue(player.Area, _ =>
    {
        Saved? saved = null;
        if (player.Instance?.ContentState.TryGetValue(SaveKey, out string? text) == true && !string.IsNullOrWhiteSpace(text))
            try { saved = JsonSerializer.Deserialize<Saved>(text, Json); } catch (JsonException) { }
        return new AreaGenerals(saved ?? new Saved());
    });

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
    /// its dismount.</summary>
    public static async Task Woken(Player player, uint id)
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
                g.Mounts[id] = new Mount { Id = id, Row = variety.Row, Level = monster.Level, Rarity = (int)monster.Rarity, Initiator = initiator, X = x, Y = y, Stats = monster.EncounterStats };
                Keep(player, g);
            }
        }
    }

    /// <summary>The area's frame: each mount's dismount and the bird.</summary>
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
        List<Expiring>? expiring = null;
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
                    changed = true;
                    Module?.Log($"Aukuna: the mount 0x{mount.Id:x} died at ({mount.X},{mount.Y}): dismount_01");
                }
                int due = now >= mount.DiedAt + PhaseSeconds ? 4 : now >= mount.DiedAt + BirdExplodeSeconds ? 3 : now >= mount.DiedAt + SpawnBirdSeconds ? 2 : 1;
                if (due > mount.Step) { (dismount ??= []).Add((mount, m, due)); changed = true; }
            }
            foreach (var e in g.Expiring)
                if (now >= e.Until) (expiring ??= []).Add(e);
            if (expiring is not null) { foreach (var e in expiring) g.Expiring.Remove(e); changed = true; }
            if (ended is not null) { foreach (uint id in ended) g.Mounts.Remove(id); changed = true; }
            if (changed) Keep(player, g);
        }
        if (dismount is not null)
            foreach (var (mount, corpse, step) in dismount) await Dismount(player, g, mount, corpse, step, now);
        if (expiring is not null) await LegionRevive.Remove(player, [.. expiring.Select(e => e.Id)]);
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
        if (!LegionData.Current.Mounts.TryGetValue(mount.Row, out var rows)) return;
        if (from < 2 && due >= 2)
        {
            // spawnbird: the bird copies the mount's pose (its .ot: CopyAnimation(reference), PauseAnimation) and the mount drops its own bird.
            if (corpse is not null) await LegionStasis.SetState(player, mount.Id, corpse, "nobird");
            if (Monsters.Find(rows.Bird.ToString()) is { Supported: true } birdVariety)
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
        // phase: Aukuna on foot where the mount fell; the mount's corpse (made invisible by its own disappear event) goes.
        if (Monsters.Find(rows.OnFoot.ToString()) is not { Supported: true } foot) return;
        var (tx, ty) = (corpse?.Target ?? player).Walk.Position(now);
        float rotation = (tx, ty) != (mount.X, mount.Y) ? (float)Math.Atan2(tx - mount.X, -(ty - mount.Y)) : 0f;
        uint id = LegionDomain.IsDomainArea(player.Layout.Id)
            ? await LegionDomain.Dismounted(player, mount.Id, foot, mount.X, mount.Y, rotation)
            : await OnFoot(player, mount, foot, rotation);
        if (corpse is not null) await LegionRevive.Remove(player, [mount.Id]);
        Module?.Log($"Aukuna: dismounted, mount 0x{mount.Id:x} -> on foot 0x{id:x} ({foot.Id}) at ({mount.X},{mount.Y})");
    }

    /// <summary>A map encounter's general on foot: the mount's encounter states and level, its fighter entry (the drops, the Defeat line)
    /// and its place among the encounter's released monsters; the Dismount line.</summary>
    static async Task<uint> OnFoot(Player player, Mount mount, MonsterCatalog.Variety foot, float rotation)
    {
        // Fighting as the woken mount was (LegionStasis.Wake): present, broken out, activated, its monolith.
        uint id = await SpawnMonster(player, foot, mount.Level, (MonsterRarity)mount.Rarity, mount.X, mount.Y,
            friendly: false, unrisen: false, alone: false, instanceOwned: true, bulk: true, log: false, placement: MonsterPlacement.Established,
            rotation: rotation, shape: o =>
            {
                var sm = (o.StateMachine ?? foot.StateMachine)?.Set("present", 1).Set("breakout", 1).Set("activated", 1);
                if (mount.Initiator != 0) sm = sm?.Set("owning_initiator", mount.Initiator);
                return o with { StateMachine = sm };
            });
        if (id == 0 || !player.Area.Monsters.TryGetValue(id, out var fighter)) return 0;
        if (mount.Stats is { } stats) fighter.EncounterStats = stats;   // Eternal Conflict's extra damage
        if (LegionStasis.FighterOf(player, mount.Id, remove: true) is { } entry) LegionStasis.AddFighter(player, id, entry);
        LegionMonolith.Replace(player, mount.Id, id);
        await LegionSpeech.Say(player, foot.Id, "Dismount");
        return id;
    }

    /// <summary>An object that only shows (the bird): no AI, no body, no hits, no rewards, no bar.</summary>
    static async Task<uint> SpawnInert(Player player, MonsterCatalog.Variety variety, byte level, MonsterRarity rarity, int x, int y)
    {
        uint id = await SpawnMonster(player, variety, level, rarity, x, y, nonBlocking: true,
            friendly: false, unrisen: false, alone: false, scripted: true, instanceOwned: true, bulk: true, log: false,
            placement: MonsterPlacement.Established, extraStats: [(Stat.HideMiniLifeBar, 1), (Stat.VirtualIsHiddenMonster, 1)]);
        if (id == 0 || !player.Area.Monsters.TryGetValue(id, out var m)) return 0;
        m.NoRewards = true; m.Attack = null; m.Aggro = false; m.BusyUntil = double.PositiveInfinity; m.EntranceGrace = true;
        return id;
    }
}
