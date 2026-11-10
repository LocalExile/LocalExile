using System.Runtime.CompilerServices;
using GameServer.Core.World;
using static Game;

/// <summary>
/// The Hierophant's node effects that need state the stat sum does not have (Agents/docs/game/hierophant.md): the
/// number of Summoned Totems (Ritual of Awakening), the unreserved mana behind Sanctuary of Thought's area, Divine
/// Guidance's mana increases applied to damage and Arcane Blessing's Arcane Surge on spell hits. Arcane Surge itself is
/// the shared buff of SupportGemBuffs.
/// </summary>
static class Hierophant
{
    internal const string ManaModifiersToDamage = "additive_mana_modifiers_apply_to_damage_at_30%_value";
    internal const string AreaPerUnreservedMana = "hierophant_area_of_effect_+%_per_50_unreserved_mana_up_to_100%";
    internal const string LifeRegenPerTotem = "you_and_totem_life_regeneration_rate_per_minute_%_per_active_totem";
    internal const string ManaRegenPerTotem = "mana_regeneration_rate_per_minute_%_per_active_totem";
    internal const string MorePerTotem = "hierophant_passive_damage_+%_final_per_totem";

    /// <summary>"Increases and Reductions to Maximum Mana also apply to Damage at 30% of their value"
    /// (ReminderTextPsudoKeystoneTransfigurationOfMind).</summary>
    internal const int ManaToDamagePct = 30;

    /// <summary>"{0}% increased Area of Effect per 50 Unreserved Maximum Mana, up to 100%".</summary>
    internal const int ManaPerAreaStep = 50, AreaCap = 100;

    /// <summary>Divine Guidance: the increased damage from the summed <c>maximum_mana_+%</c>, truncated toward zero.</summary>
    internal static int DamageFromMana(Func<string, int> get) =>
        get(ManaModifiersToDamage) == 0 ? 0 : (int)((long)get("maximum_mana_+%") * ManaToDamagePct / 100);

    /// <summary>Sanctuary of Thought: X% per whole 50 unreserved maximum mana, the product capped at 100.</summary>
    internal static int AreaFromMana(int perStep, int unreservedMana) =>
        perStep <= 0 ? 0 : (int)Math.Min(AreaCap, (long)perStep * (Math.Max(0, unreservedMana) / ManaPerAreaStep));

    internal sealed class State
    {
        // What the stat sum adds now (PlayerStats); only the tick changes them, so pools and client switch together.
        public int Totems, ManaArea;
        public long Revision;
        public double LastTick = double.NaN;
    }

    // Each totem's fraction of a Life point regenerated but not yet added.
    sealed class Carry { public double Life; }
    static readonly ConditionalWeakTable<TotemState.Totem, Carry> TotemCarry = new();

    static readonly ConditionalWeakTable<ClientState, State> States = new();
    internal static State Of(ClientState conn) => States.GetValue(conn, _ => new());

    internal static long Revision(ClientState conn) => States.TryGetValue(conn, out var state) ? state.Revision : 0;

    // The passive and item sum (the stat sources cache), without what PlayerStats adds.
    static int Raw(ClientState conn, string name) => Stats.Key(name) is uint key && conn.StatSourcesCache is { } cached
        ? cached.Sources.GetValueOrDefault(key) : 0;

    /// <summary>GameEvents.PlayerStatSources: per-totem regeneration, the unreserved-mana area and mana-to-damage.</summary>
    public static Dictionary<uint, int> PlayerStats(ClientState conn, Dictionary<uint, int> stats)
    {
        int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        void Add(string name, long value)
        {
            if (value != 0 && Stats.Key(name) is uint k)
                stats[k] = (int)Math.Clamp(stats.GetValueOrDefault(k) + value, int.MinValue, int.MaxValue);
        }
        // Read before anything below adds to maximum_mana_+% (nothing does) or damage_+%.
        Add("damage_+%", DamageFromMana(Get));
        if (!States.TryGetValue(conn, out var state)) return stats;
        Add("life_regeneration_rate_per_minute_%", (long)Get(LifeRegenPerTotem) * state.Totems);
        Add("mana_regeneration_rate_per_minute_%", (long)Get(ManaRegenPerTotem) * state.Totems);
        // Joins the base form, which the panel's total and every skill read (as Impact's and Rampage's do).
        Add(SkillArea.BaseIncreased, state.ManaArea);
        return stats;
    }

    /// <summary>GameEvents.OutgoingDamage: Ritual of Awakening's more damage per Summoned Totem, on the hits of the
    /// player and of his totems (whose stats are the player's).</summary>
    public static DamagePacket OutgoingDamage(ClientState conn, IReadOnlyDictionary<uint, int> player, SpawnedMonster target,
        DamagePacket rolled)
    {
        int per = Stats.Key(MorePerTotem) is uint key ? player.GetValueOrDefault(key) : 0;
        if (per == 0 || !States.TryGetValue(conn, out var state) || state.Totems == 0) return rolled;
        return rolled.Scale(Math.Max(0, 1 + (double)per * state.Totems / 100));
    }

    /// <summary>The player tick: count the Summoned Totems and the unreserved-mana area (only with a stat that reads
    /// them), regenerate the totems' Life, and when a value changed rebuild the pools and send the stats.</summary>
    public static async Task Tick(ClientState conn)
    {
        if (!conn.PlayerSent || conn.Closed || conn.LeavingArea) return;
        bool changed;
        lock (conn.Monsters)
        {
            var state = Of(conn);
            double now = conn.Clock.Elapsed.TotalSeconds;
            double elapsed = double.IsNaN(state.LastTick) || conn.Paused ? 0 : Math.Max(0, now - state.LastTick);
            state.LastTick = now;
            bool alive = conn.PlayerLife > 0;
            int lifePerTotem = Raw(conn, LifeRegenPerTotem);
            int totems = alive && (lifePerTotem != 0 || Raw(conn, ManaRegenPerTotem) != 0 || Raw(conn, MorePerTotem) != 0)
                ? conn.Totems.Living(now).Count() : 0;
            // "You and your Totems Regenerate 1% of Life per second for each Summoned Totem": the totems' part.
            if (totems > 0 && lifePerTotem > 0 && elapsed > 0)
                foreach (var totem in conn.Totems.Living(now))
                {
                    if (totem.Life >= totem.MaxLife) continue;
                    var carry = TotemCarry.GetValue(totem, _ => new());
                    carry.Life += totem.MaxLife * (double)lifePerTotem * totems / 100 / 60 * elapsed;
                    uint whole = (uint)Math.Min(totem.MaxLife - totem.Life, Math.Floor(carry.Life));
                    carry.Life -= whole;
                    totem.Life += whole;
                    if (totem.Life >= totem.MaxLife) carry.Life = 0;
                }
            int perStep = Raw(conn, AreaPerUnreservedMana);
            int area = alive && perStep != 0 ? AreaFromMana(perStep, UnreservedMana(conn)) : 0;
            changed = (totems, area) != (state.Totems, state.ManaArea);
            (state.Totems, state.ManaArea) = (totems, area);
            if (changed)
            {
                state.Revision++;
                RecomputePoolsAndSpeed(conn);
                Console.WriteLine($"[gateway] Hierophant: {totems} summoned totems, unreserved-mana area +{area}%");
            }
        }
        if (!changed) return;
        var (remote, stream, ciphers) = conn.Wire;
        await SendCharacterStats(remote, stream, ciphers, conn, "Hierophant totems or mana area changed");
    }

    /// <summary>A new area is a new connection: the leaving one forgets its state.</summary>
    public static Task Leaving(ClientState conn)
    {
        States.Remove(conn);
        return Task.CompletedTask;
    }
}
