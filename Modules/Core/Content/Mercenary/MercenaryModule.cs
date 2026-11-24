using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Mercenary;

/// <summary>
/// The Mercenaries of Trarthus' mercenary: an area of a map may hold one (WorldAreaLeagueChances' Mercenaries column and the
/// map's own stats roll it), met, talked to, duelled and hired. It belongs to the area instance, not to a player: whoever
/// enters sees and drives the same encounter. This file hooks it; the roll, the placement and the flow are in
/// <c>MercenaryEncounter.cs</c>, the messages in <see cref="MercenaryWire"/>.
/// <para>
/// The flow follows the two official duels: the mercenary waits friendly and scripted (Met), a press on it opens its panel
/// (Talk), the panel's duel choice starts a short countdown (Countdown), the friendly one is replaced by a hostile one of the
/// same variety (Duel), and its death leaves the <c>*Allied</c> mercenary in its place (Hired).
/// </para>
/// </summary>
public sealed partial class MercenaryModule : IModule
{
    const string StateKey = "Mercenary.Encounter";

    /// <summary>The draws of the encounter, apart from the packs' (the same instance gets the same mercenary).</summary>
    const int RollTag = 0x4d657263, PlanTag = 0x4d657232;

    /// <summary>Where the encounter is, in this instance: kept in <c>Instance.ContentState</c> too, so a module reload does not
    /// lose it.</summary>
    public enum Stage { None, Met, Talk, Countdown, Duel, Hired }

    public sealed class Run
    {
        public bool Rolled { get; set; }
        public Stage Stage { get; set; }
        public int Class { get; set; } = -1;
        public int Build { get; set; } = -1;
        public int X { get; set; }
        public int Y { get; set; }
        public byte Level { get; set; }
        public int Seed { get; set; }
        /// <summary>The mercenary's body now (the met one, then the duel opponent) and, at the end, the hired one.</summary>
        public uint Body { get; set; }
        public uint Hired { get; set; }
        public double Clock { get; set; }
        public int Steps { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public bool Busy;
    }

    readonly ConditionalWeakTable<InstanceState, Run> runs = new();
    readonly ConditionalWeakTable<Player, object> told = new();
    IModuleContext context = null!;

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    /// <summary>This module's game data, built when it loads (before the Gateway listens); a test that uses the module calls it once.</summary>
    public static void LoadData() => MercenaryTables.LoadGameData();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        context = module;
        string id = module.Manifest.Id;
        module.Lifetime.Own(new Cleanup(() => { runs.Clear(); told.Clear(); }));
        GameEvents.AreaPopulated.Add(id, Place);
        GameEvents.PlayerEntered.Add(id, Enter);
        // Only a map area can hold a mercenary, and only the connection stepping the instance's monsters advances the countdown.
        GameEvents.AreaTick.Add(id, start => MapDevices.IsMapArea(start.Layout.Hash) ? (player, seconds) => Tick(player, seconds) : null);
        // The press: the mercenary is a monster, so its click reaches the module as the talk of the NPC it is registered as.
        GameEvents.NpcTalk.Add(id, Talk);
        // The panel's duel choice, a C→S ruleset action.
        GameEvents.Packet(InstanceRulesets.ActionId).Add(id, Choose);
        GameEvents.Kill.Add(id, OnKill);
    }

    Run? Peek(Player player)
    {
        if (runs.TryGetValue(player.Instance, out var run)) return run;
        if (!player.Instance.ContentState.TryGetValue(StateKey, out var text)) return null;
        return runs.GetValue(player.Instance, _ => JsonSerializer.Deserialize<Run>(text) ?? new());
    }

    Run State(Player player) => Peek(player) ?? runs.GetValue(player.Instance, _ => new());

    void Save(Player player, Run run)
    {
        string json;
        lock (run) json = JsonSerializer.Serialize(run);
        player.Instance.ContentState[StateKey] = json;
    }
}
