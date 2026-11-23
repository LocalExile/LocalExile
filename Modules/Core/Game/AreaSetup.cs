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
        GameEvents.AreaStarted.Add(module.Manifest.Id, (area, player) => Start(area, player, source));
        // Areas already running get this generation's systems at their next frame.
        foreach (var (area, view) in AreaFrames.LiveAreas())
            area.Defer(a => AreaFrames.StartRules(a, view));
        module.Lifetime.Own(new Removal(source));
    }

    static void Start(GameArea area, Player player, IModuleLifetime source)
    {
        // A reload reaches an area that already has this module's systems from an earlier start.
        if (area.Systems.All.Any(s => ReferenceEquals(s.Source, source))) return;
        Running.AddOrUpdate(area, source);
        MonsterRegeneration.Start(area, player, source);
        Game.StartMonsterDamageOverTime(area, player, source);
        Game.StartMonsterAilments(area, player, source);
        Game.StartMonsterAi(area, player, source);
        Game.StartMonsterContacts(area, player, source);
        Game.StartStats(area, player, source);
        // The area's walkers, once a frame for everyone (AreaMovement); whoever needs them current between frames steps
        // them to its time first, and this pass then has nothing left to do. The zones' membership follows.
        var frames = player.Frames;
        area.Systems.Add("movement", AreaPhase.Movement, a =>
        {
            var players = frames.Players();
            if (players.Length == 0) return;
            bool moved;
            if (Game.AreaSteps(players[0])) Game.StepAreaWalkers(players[0], a.Time);
            moved = Game.PlaceWalkers(players, a.Time, a.Grid);
            // Who is inside each zone (auras, grounds, burning radii) follows the walkers' moves.
            a.Zones.Update(moved);
            if (moved) foreach (var p in players) Game.PresentNearby(p);
        }, source: source);
        // The area's wake zone (SharedMonsters.cs): every half second, as GGG checks it.
        area.Systems.Add("wake zone", AreaPhase.Movement, _ =>
        {
            if (frames.View is { } view) Game.StepAreaWake(view);
        }, every: 15, source: source);
        Console.WriteLine($"[area] {player.Layout.Id}: {area.Systems.All.Count(s => ReferenceEquals(s.Source, source))} game system(s) in the area's world");
    }

    // The areas that run the game's systems, with the rules (module lifetime) that started them there.
    static readonly ConditionalWeakTable<GameArea, object> Running = new();

    /// <summary>Whether this connection's area runs the game's systems of the rules loaded now: the loops they replace
    /// (each connection's AI, its tick's monster damage over time, ...) then leave that work to the area.</summary>
    public static bool Runs(Player player) =>
        player.Instance is not null && player.Frames.Running && player.Frames.Area is { } area
        && Running.TryGetValue(area, out var source) && ReferenceEquals(source, Game.CombatLifetime);

    sealed class Removal(IModuleLifetime source) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (area, _) in AreaFrames.LiveAreas()) area.Defer(a => a.Drop(source));
        }
    }
}
