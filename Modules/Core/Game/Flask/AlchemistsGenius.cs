using GameServer.Core;

/// <summary>
/// Alchemist's Genius: a flask use grants the buff (BuffDefinitions "alchemists_genius",
/// flask_effect_on_self_+% 10 and charges_gained_+% 20 for 4 s per ReminderText row 189
/// ReminderTextAlchemistsGenius) when the character holds gain_alchemists_genius_on_flask_use_%, rolled as a % chance
/// per use. While it runs its stats count in CharacterStatSources (feeding PoolsFor, Mageblood, running flasks and
/// FlaskCharges.GainStats) and the flasks already running are rescaled to the new effect; it ends on its deadline,
/// on death or with the area, like the flask buffs. There is no setting: this is the ordinary behaviour.
/// </summary>
public static partial class Game
{
    const string AlchemistsGeniusBuffId = "alchemists_genius";
    const double AlchemistsGeniusSeconds = 4.0;

    static readonly (uint Stat, int Value)[] AlchemistsGeniusStats =
        [(Stat.FlaskEffectOnSelfPlusPct, 10), (Stat.ChargesGainedPlusPct, 20)];

    // The buff's BuffDefinitions row, looked up by Id once at data load (never by row number); null when absent.
    static ushort? AlchemistsGeniusRow;

    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadAlchemistsGeniusData()
    {
        AlchemistsGeniusRow = null;
        if (!ClientTables.Has("BuffDefinitions")) return;
        foreach (var row in ClientTables.Table("BuffDefinitions"))
            if (row.Text("Id") == AlchemistsGeniusBuffId) { AlchemistsGeniusRow = (ushort)row.Index; return; }
    }

    /// <summary>The CharacterStatSources part: while the buff runs, its stats count on the character before PoolsFor
    /// and PlayerStatStep.UtilityFlasks, so Mageblood permanent flasks, new flask uses (FlaskEffectScaling), running
    /// flasks (via rescale on grant/expiry) and charge gains (FlaskCharges.GainStats) all receive it.</summary>
    public static Dictionary<uint, int> WithAlchemistsGenius(Player player, Dictionary<uint, int> sources)
    {
        var genius = player.AlchemistsGenius;
        if (!genius.Active) return sources;
        double now = AreaTime(player);
        if (now >= genius.Ends) return sources;
        StatsChangeIn(genius.Ends - now);
        foreach (var (stat, value) in AlchemistsGeniusStats)
            sources[stat] = ActorScale.Saturate((long)sources.GetValueOrDefault(stat) + value);
        return sources;
    }

    /// <summary>On an admitted flask use (utility or recovery, inside SpendFlask's accepted charge
    /// transaction, under Monsters and FlaskBarGate): roll the character's grant stat with the area's
    /// random and, when it hits, start/refresh the non-stacking buff (MergeMode = 3), show its 0x01c1,
    /// and rescale the flasks already running to the new effect. No row for the buff's Id, no stat, or
    /// a failed roll: nothing.</summary>
    public static void TryGrantAlchemistsGenius(Player player)
    {
        int chance = CharacterPlayerStats(player).GetValueOrDefault(Stat.GainAlchemistsGeniusOnFlaskUsePct);
        if (chance <= 0) return;
        if (AreaRng(player).Next(100) >= chance) return;
        if (AlchemistsGeniusRow is not ushort row)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0070 Alchemist's Genius: no BuffDefinitions row '{AlchemistsGeniusBuffId}', not granted");
            return;
        }
        double now = AreaTime(player);
        var genius = player.AlchemistsGenius;
        genius.Start(row, now, AlchemistsGeniusSeconds);
        player.Area.Buffs.Set(player.Id, BuffRows.Spec(row, player.Id, null, AlchemistsGeniusSeconds));
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0070 Alchemist's Genius granted: row {row} for {AlchemistsGeniusSeconds:0.#} s");
        BuffChanged(player);
    }

    /// <summary>Ends the buff when its deadline passed or the player died: its 0x01c2, and the flasks
    /// still running rescaled back to the effect without it. Returns whether one ended.</summary>
    public static bool EndAlchemistsGeniusIfDue(Player player, double now, bool dead)
    {
        var genius = player.AlchemistsGenius;
        if (!genius.Active || !(dead || genius.Due(now))) return false;
        player.Area.Buffs.Clear(player.Id, genius.Row, player.Id);
        genius.Clear();
        BuffChanged(player);
        return true;
    }

    // The buff started or ended: the stat graph's flask steps run again, the running flasks take the new effect, and
    // the pools and speed follow them.
    static void BuffChanged(Player player)
    {
        AccrueUtilityFlaskDamage(player, AreaTime(player));
        RescaleUtilityFlasks(player);
        player.MarkStatsDirty(StatInput.FlaskEffects);
        RecomputePoolsAndSpeed(player);
        Automation(player).StatRevision++;
    }

    /// <summary>Works every running utility flask's stats out again as a use would (UtilityFlaskUseFor), keeping each
    /// effect's buff instance, cell and deadline: the client's icon and timer do not move, only the stats change.
    /// The character's stats come from a fresh build (CharacterPlayerStatsSnapshot), not from CharacterPlayerStats:
    /// in an area's frames that is the stat graph's last step, which has not yet re-run the Sources step with the buff
    /// started or ended just now. A grant or an expiry is rare, so one full build for it costs nothing in per-hit paths.</summary>
    static void RescaleUtilityFlasks(Player player)
    {
        var effects = player.UtilityFlaskEffects.All();
        if (effects.Count == 0) return;
        var character = CharacterPlayerStatsSnapshot(player, out _, out _);
        int Get(uint key) => character.GetValueOrDefault(key);
        var bar = ItemsOf(player).Items(PlayerInventories.Flask);
        var items = ItemsFor(player);
        int level = player.Pools.Level;
        foreach (var effect in effects)
        {
            if (bar.FirstOrDefault(i => i.Id == effect.ItemId) is not { } flask) continue;
            if (items.ByHash(flask.Item.TypeHash) is not { } type || items.UtilityFlaskOf(type) is not { } utility) continue;
            var use = UtilityFlaskUseFor(player, effect.Slot, flask.Item, utility,
                ItemModStats.Local(flask.Item, items.Mods), Get, level);
            player.UtilityFlaskEffects.Retune(effect, use.Stats, use.SelfChaosRate, use.FlatSelfChaosRate);
        }
    }
}
