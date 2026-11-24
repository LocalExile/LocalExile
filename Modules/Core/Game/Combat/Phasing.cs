using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // BuffDefinitions' "phasing_buff" row: the icon and "You have Phasing, allowing you to move through enemies."
    // (raider's row 337 is the same icon without Removable; the general buff is the one the client shows).
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadPhasingData()
    {
        PhasingBuffRow = LoadPhasingBuffRow();
        PhasingVisualRow = LoadPhasingVisualRow();
    }

    static ushort PhasingBuffRow, PhasingVisualRow;

    static ushort LoadPhasingBuffRow()
    {
        if (!ClientTables.Has("BuffDefinitions")) return (ushort)336;
        foreach (var row in ClientTables.Table("BuffDefinitions"))
            if (row.Text("Id") == "phasing_buff") return (ushort)row.Index;
        return (ushort)336;
    }

    // The buff's own BuffVisuals row 340 has only the icon; raider_phasing_buff carries the client's phase.epk
    // (the blue model effect), so the buff is added with that visual override.
    static ushort LoadPhasingVisualRow()
    {
        if (!ClientTables.Has("BuffVisuals")) return (ushort)813;
        foreach (var row in ClientTables.Table("BuffVisuals"))
            if (row.Text("Id") == "raider_phasing_buff") return (ushort)row.Index;
        return (ushort)813;
    }

    /// <summary>The charm's "X% chance to gain Phasing for 4 seconds on Kill": one roll per kill.</summary>
    public static void GainPhasingOnKill(Player player)
    {
        int chance = StatSourceValues(player).GetValueOrDefault(Stat.PhasingFor4SecondsOnKillPct);
        if (chance <= 0 || AreaRng(player).Next(100) >= Math.Min(100, chance)) return;
        GainPhasing(player, 4);
        Console.WriteLine($"[gateway] phasing on kill: {chance}% -> 4 seconds (until {player.PhasingUntil:0.###})");
    }

    /// <summary>Phasing for some seconds (a kill, Rampage): its end moves, and the buff shows the countdown from now.</summary>
    public static void GainPhasing(Player player, double seconds)
    {
        double now = AreaTime(player);
        if (now + seconds <= player.PhasingUntil) return;
        player.PhasingUntil = now + seconds;
        if (player.PlayerLife > 0) player.Area.Buffs.Set(player.Id, PhasingBuff(player, seconds));
        PresentStats(player);
    }

    static Buff PhasingBuff(Player player, double? seconds) =>
        new(PhasingBuffRow, player.Id, U68: seconds is { } s ? Buff.Seconds(s) : null,
            Visual: seconds is null ? PhasingVisual(player) : PhasingVisualRow);

    // Phasing from a flask (a Quartz Flask's phase_through_objects) wears the flask's effect, phase.epk: the flask effect
    // cosmetic on the cell of a flask that grants it now (running, or one of Mageblood's) replaces it, and Invisible
    // Flask Effect hides it (FlaskEffectVisual).
    static ushort PhasingVisual(Player player)
    {
        if (!player.Phasing) return PhasingVisualRow;
        var items = ItemsFor(player);
        var bar = ItemsOf(player).Items(PlayerInventories.Flask).OrderBy(f => f.X).ToList();
        IReadOnlyList<InventoryItem> permanent = MagebloodBelt(player) is { } belt ? MagebloodFlasks(bar, MagebloodFlaskCount(belt, items), items) : [];
        foreach (var flask in bar)
            if ((player.UtilityFlaskEffects.Of(flask.Id) is not null || permanent.Contains(flask))
                && items.ByHash(flask.Item.TypeHash) is { } type
                && items.UtilityFlaskOf(type) is { } utility && UtilityFlasks.Phasing(utility.Stats))
                return FlaskEffectVisual(player, flask.X) ?? PhasingVisualRow;
        return PhasingVisualRow;
    }

    /// <summary>The Phasing Buff (BuffDefinitions "phasing_buff") and the virtual stat the client's pathfinder reads. The
    /// server's own blockers already follow <see cref="Player.IsPhasing"/>; without the stat the client walks around them and
    /// the estimates drift apart. A timed phasing's buff is set when it starts (GainPhasing); here the flask's untimed one
    /// starts when the timer is out, and the buff ends with the phasing.</summary>
    public static Task SyncPhasing(Player player)
    {
        if (player.Closed || player.LeavingArea) return Task.CompletedTask;
        double now = AreaTime(player);
        bool active = player.PlayerLife > 0 && player.IsPhasing;
        if (player.PhasingUntil > now) PresentIn(player, player.PhasingUntil - now);
        if (!active) player.Area.Buffs.Clear(player.Id, PhasingBuffRow, player.Id);
        else if (player.PhasingUntil <= now) player.Area.Buffs.Set(player.Id, PhasingBuff(player, null));
        var stats = new List<(uint Key, int Value)>(3)
        {
            (Stat.VirtualPhaseThroughObjects, active ? 1 : 0),
            (Stat.PhaseThroughObjects, active ? 1 : 0),
        };
        var current = PlayerStatValues(player);
        int phasedBonus = current.GetValueOrDefault(Stat.LightRadiusPlusPctWhilePhased);
        if (phasedBonus != 0)
        {
            int baseLight = current.GetValueOrDefault(Stat.LightRadiusPlusPct);
            int mapLight = MapModsOf(player)?.Get(Stat.MapPlayerLightRadiusPlusPct) ?? 0;
            int desiredLight = ActorScale.Saturate((long)baseLight + (active ? phasedBonus : 0) + mapLight);
            if (current.GetValueOrDefault(Stat.VirtualLightRadiusPlusPct) != desiredLight)
                MarkPlayerStats(player, PlayerStatStep.TailPanels);
            if (!player.Frames.Running)
                stats.Add((Stat.VirtualLightRadiusPlusPct, desiredLight));
        }
        SetStats(player, player.Id, stats);
        return Task.CompletedTask;
    }
}
