using System.Runtime.CompilerServices;
using GameServer.Modding;

/// <summary>
/// The game's systems in an area's own world (GameArea): added when the area starts (GameEvents.AreaStarted), or when the
/// game module loads while areas run, with the module's lifetime as their source, and dropped from every area at its next
/// frame when the module unloads. Each system is a function of the area, run in the area's frame.
/// </summary>
public static class AreaSetup
{
    public static void Load(IModuleContext module)
    {
        var source = module.Lifetime;
        GameEvents.AreaStarted.Add(module.Manifest.Id, (area, conn) => Start(area, conn, source));
        // Areas already running get this generation's systems at their next frame.
        foreach (var (area, view) in AreaFrames.LiveAreas())
            area.Defer(a => AreaFrames.StartRules(a, view));
        module.Lifetime.Own(new Removal(source));
    }

    static void Start(GameArea area, ClientState conn, IModuleLifetime source)
    {
        // A reload reaches an area that already has this module's systems from an earlier start.
        if (area.Systems.All.Any(s => ReferenceEquals(s.Source, source))) return;
        Running.AddOrUpdate(area, source);
        MonsterRegeneration.Start(area, conn, source);
        Game.StartMonsterDamageOverTime(area, conn, source);
        Game.StartMonsterAilments(area, conn, source);
        Game.StartMonsterAi(area, conn, source);
        Game.StartStats(area, conn, source);
        // The area's walkers, once a frame for everyone (AreaMovement); whoever needs them current between frames steps
        // them to its time first, and this pass then has nothing left to do.
        var frames = conn.Frames;
        var monsters = conn.Monsters;
        var cells = new Game.WalkerCells();
        area.Systems.Add("movement", AreaPhase.Movement, a =>
        {
            var players = frames.Players();
            if (players.Length == 0 || !Game.AreaSteps(players[0])) return;
            bool moved;
            lock (monsters)
            {
                Game.StepAreaWalkers(players[0], a.Time);
                moved = cells.Moved(players, a.Time);
            }
            if (moved) foreach (var p in players) Game.PresentNearby(p);
        }, source: source);
        Console.WriteLine($"[area] {conn.Layout.Id}: {area.Systems.All.Count(s => ReferenceEquals(s.Source, source))} game system(s) in the area's world");
    }

    // The areas that run the game's systems, with the rules (module lifetime) that started them there.
    static readonly ConditionalWeakTable<GameArea, object> Running = new();

    /// <summary>Whether this connection's area runs the game's systems of the rules loaded now: the loops they replace
    /// (each connection's AI, its tick's monster damage over time, ...) then leave that work to the area.</summary>
    public static bool Runs(ClientState conn) =>
        conn.Instance is not null && conn.Frames.Running && conn.Frames.Area is { } area
        && Running.TryGetValue(area, out var source) && ReferenceEquals(source, Game.CombatLifetime);

    sealed class Removal(IModuleLifetime source) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (area, _) in AreaFrames.LiveAreas()) area.Defer(a => a.Drop(source));
        }
    }
}
