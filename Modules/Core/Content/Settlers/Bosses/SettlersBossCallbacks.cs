using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Settlers.Bosses;

/// <summary>The three Settlers of Kalguur pinnacle/ambush boss encounters (3.29.3c retail).</summary>
public enum SettlersBossKind
{
    /// <summary>The Black Knight (<c>SettlersBossFallenStar</c>, WorldAreas#1919, Starfall Crater).</summary>
    BlackKnight,
    /// <summary>Admiral Valerius (<c>SettlersBossPirateCove</c>, WorldAreas#1920, Sailor's Folly).</summary>
    AdmiralValerius,
    /// <summary>Sasan, the Bandit Lord (<c>SettlersBossAlleyway</c>, WorldAreas#1921, Abandoned Port).</summary>
    BanditLord,
}

/// <summary>Payload emitted when a player claims one of the post-kill Verisium outcrops in Starfall Crater.</summary>
public sealed record BossVerisiumClaimedEvent(
    Player Player,
    int OreAmount,
    bool IsMainMeteorite,
    int AreaLevel,
    int NodesClaimed,
    int TotalNodes);

/// <summary>Payload emitted when Admiral Valerius is defeated in Sailor's Folly to rescue a hijacked shipment and crew.</summary>
public sealed record RescueShipmentEvent(
    Player Player,
    string AreaId,
    int AreaLevel,
    int GoldDropped);

/// <summary>Payload emitted when Sasan, the Bandit Lord is defeated in Abandoned Port to rescue kidnapped Atlas runners.</summary>
public sealed record RescueMappersEvent(
    Player Player,
    string AreaId,
    int AreaLevel,
    int GoldDropped);

/// <summary>Payload emitted whenever any of the three Settlers bosses is defeated.</summary>
public sealed record SettlersBossDefeatedEvent(
    Player Player,
    SettlersBossKind Boss,
    string AreaId,
    int AreaLevel);

/// <summary>
/// Clean integration callbacks between the three Settlers boss arena controllers and the Kingsmarch / Settlers town
/// module. The Settlers module subscribes on load (and unsubscribes on unload via the returned <see cref="IDisposable"/>
/// or clearing delegates) so boss arenas stay completely decoupled from town persistence and UI wire codecs.
/// </summary>
public static class SettlersBossCallbacks
{
    static readonly object Gate = new();

    static Func<BossVerisiumClaimedEvent, Task>? _onBossVerisiumClaimed;
    static Func<RescueShipmentEvent, Task>? _onRescueShipment;
    static Func<RescueMappersEvent, Task>? _onRescueMappers;
    static Func<SettlersBossDefeatedEvent, Task>? _onBossDefeated;

    /// <summary>Optional multiplier supplied by Settlers settings for Verisium ore gained from Starfall Crater outcrops (retail default 1.0).</summary>
    public static Func<double>? GetOreFoundMultiplier { get; set; }

    /// <summary>Optional multiplier supplied by Settlers settings for boss loot / gold drops (retail default 1.0).</summary>
    public static Func<double>? GetBossRewardMultiplier { get; set; }

    /// <summary>Invoked when a player clicks a post-kill Verisium outcrop in Starfall Crater.</summary>
    public static event Func<BossVerisiumClaimedEvent, Task>? OnBossVerisiumClaimed
    {
        add { lock (Gate) _onBossVerisiumClaimed += value; }
        remove { lock (Gate) _onBossVerisiumClaimed -= value; }
    }

    /// <summary>Invoked when Admiral Valerius dies in Sailor's Folly, rescuing the hijacked ship and its crew.</summary>
    public static event Func<RescueShipmentEvent, Task>? OnRescueShipment
    {
        add { lock (Gate) _onRescueShipment += value; }
        remove { lock (Gate) _onRescueShipment -= value; }
    }

    /// <summary>Invoked when Sasan, the Bandit Lord dies in Abandoned Port, rescuing the kidnapped Atlas map runners.</summary>
    public static event Func<RescueMappersEvent, Task>? OnRescueMappers
    {
        add { lock (Gate) _onRescueMappers += value; }
        remove { lock (Gate) _onRescueMappers -= value; }
    }

    /// <summary>Invoked whenever any of the three Settlers bosses is defeated.</summary>
    public static event Func<SettlersBossDefeatedEvent, Task>? OnBossDefeated
    {
        add { lock (Gate) _onBossDefeated += value; }
        remove { lock (Gate) _onBossDefeated -= value; }
    }

    /// <summary>Effective ore multiplier clamped to a safe non-negative range (default 1.0).</summary>
    public static double OreFoundMultiplier()
    {
        double m = 1.0;
        try { if (GetOreFoundMultiplier is { } fn) m = fn(); }
        catch { m = 1.0; }
        return double.IsFinite(m) && m > 0 ? m : 1.0;
    }

    /// <summary>Effective reward multiplier clamped to a safe non-negative range (default 1.0).</summary>
    public static double BossRewardMultiplier()
    {
        double m = 1.0;
        try { if (GetBossRewardMultiplier is { } fn) m = fn(); }
        catch { m = 1.0; }
        return double.IsFinite(m) && m > 0 ? m : 1.0;
    }

    /// <summary>Clears all registered callbacks (used during module hot-reload/unload).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _onBossVerisiumClaimed = null;
            _onRescueShipment = null;
            _onRescueMappers = null;
            _onBossDefeated = null;
        }
        GetOreFoundMultiplier = null;
        GetBossRewardMultiplier = null;
    }

    internal static async Task RaiseBossVerisiumClaimed(BossVerisiumClaimedEvent evt)
    {
        Func<BossVerisiumClaimedEvent, Task>? handlers;
        lock (Gate) handlers = _onBossVerisiumClaimed;
        if (handlers is null) return;
        foreach (Func<BossVerisiumClaimedEvent, Task> handler in handlers.GetInvocationList())
        {
            try { await handler(evt); }
            catch (Exception ex) { Console.WriteLine($"[settlers-bosses] OnBossVerisiumClaimed handler failed: {ex.Message}"); }
        }
    }

    internal static async Task RaiseRescueShipment(RescueShipmentEvent evt)
    {
        Func<RescueShipmentEvent, Task>? handlers;
        lock (Gate) handlers = _onRescueShipment;
        if (handlers is null) return;
        foreach (Func<RescueShipmentEvent, Task> handler in handlers.GetInvocationList())
        {
            try { await handler(evt); }
            catch (Exception ex) { Console.WriteLine($"[settlers-bosses] OnRescueShipment handler failed: {ex.Message}"); }
        }
    }

    internal static async Task RaiseRescueMappers(RescueMappersEvent evt)
    {
        Func<RescueMappersEvent, Task>? handlers;
        lock (Gate) handlers = _onRescueMappers;
        if (handlers is null) return;
        foreach (Func<RescueMappersEvent, Task> handler in handlers.GetInvocationList())
        {
            try { await handler(evt); }
            catch (Exception ex) { Console.WriteLine($"[settlers-bosses] OnRescueMappers handler failed: {ex.Message}"); }
        }
    }

    internal static async Task RaiseBossDefeated(SettlersBossDefeatedEvent evt)
    {
        Func<SettlersBossDefeatedEvent, Task>? handlers;
        lock (Gate) handlers = _onBossDefeated;
        if (handlers is null) return;
        foreach (Func<SettlersBossDefeatedEvent, Task> handler in handlers.GetInvocationList())
        {
            try { await handler(evt); }
            catch (Exception ex) { Console.WriteLine($"[settlers-bosses] OnBossDefeated handler failed: {ex.Message}"); }
        }
    }

    /// <summary>Opens a clickable portal at <paramref name="at"/> leading into one of the three Settlers boss arenas.
    /// Sailor's Folly and Abandoned Port default to a single-use portal (matching retail 3.29 ransom/hostage rules);
    /// Starfall Crater defaults to 6 portals (re-enterable on death).</summary>
    public static Task<uint?> OpenBossArenaPortal(Player player, (int X, int Y) at, SettlersBossKind boss, int? uses = null)
    {
        ushort targetHash = boss switch
        {
            SettlersBossKind.BlackKnight => BlackKnightController.AreaHash,
            SettlersBossKind.AdmiralValerius => AdmiralValeriusController.AreaHash,
            SettlersBossKind.BanditLord => BanditLordController.AreaHash,
            _ => BlackKnightController.AreaHash,
        };
        int maxUses = uses ?? (boss == SettlersBossKind.BlackKnight ? 6 : 1);
        var opts = new PortalOptions(
            NewInstance: true,
            UsesLeft: maxUses,
            SpawnReturnPortal: boss == SettlersBossKind.BlackKnight,
            Owner: CharacterName(player));
        return SpawnCustomPortal(player, at.X, at.Y, targetHash, opts);
    }
}
