using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // Inspiration charges cap at five (poedb: maximum_righteous_charges is the buff's own maximum).
    internal const int InspirationMaxCharges = 5;

    // BuffDefinitions rows: arcane_surge (895) and righteous_charge (1378).
    const ushort ArcaneSurgeBuffRow = 895, RighteousChargeBuffRow = 1378;

    /// <summary>Arcane Surge and Inspiration bookkeeping for one paid supported skill. Called under the Monsters lock,
    /// after the skill's cost has been paid and before the cast starts.</summary>
    internal static void SupportManaSpentLocked(ClientState conn, ushort skillId, IReadOnlyDictionary<uint, int> stats,
        uint cost, double now)
    {
        if (cost == 0 || conn.LeavingArea || conn.PlayerLife == 0) return;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

        // Arcane Surge: "each supported spell tracks how much mana you spend on it, granting a buff when the total
        // mana spent reaches a threshold".
        int threshold = Get("support_arcane_surge_gain_buff_on_mana_use_threshold");
        if (threshold <= 0 && Get("support_arcane_surge_spell_damage_+%_final_while_you_have_arcane_surge") != 0)
            threshold = 1;
        if (threshold > 0)
        {
            long spent = conn.SupportGems.ArcaneSurgeSpent.GetValueOrDefault(skillId) + cost;
            if (spent >= threshold)
            {
                int durationMs = Get("support_arcane_surge_base_duration_ms");
                conn.SupportGems.ArcaneSurgeUntil = now + (durationMs > 0 ? durationMs : 4000) / 1000.0;
                conn.SupportGems.ArcaneSurgeCastSpeedPct = Get("support_arcane_surge_cast_speed_+%");
                conn.SupportGems.ArcaneSurgeManaRegenPct = Get("support_arcane_surge_mana_regeneration_rate_+%");
                conn.SupportGems.ArcaneSurgeRefresh = true;
                conn.SupportGems.PendingStats = true;
                spent = 0;
            }
            conn.SupportGems.ArcaneSurgeSpent[skillId] = spent;
        }

        // Inspiration: gain a charge on mana spent, five at most; a single use at or above the support's threshold
        // consumes all of them instead.
        int gain = Get("gain_righteous_charge_on_mana_spent_%");
        if (gain <= 0 && (Get("support_inspiration_mana_cost_+%_final") != 0
            || Get("elemental_damage_+%_final_per_righteous_charge") != 0)) gain = 100;
        if (gain > 0)
        {
            int before = conn.SupportGems.InspirationCharges;
            int lose = Get("lose_all_righteous_charges_on_mana_use_threshold");
            if (lose > 0 && cost >= lose) conn.SupportGems.InspirationCharges = 0;
            else if (gain >= 100 || Random.Shared.Next(100) < gain)
                conn.SupportGems.InspirationCharges = Math.Min(InspirationMaxCharges,
                    conn.SupportGems.InspirationCharges + 1);
            if (conn.SupportGems.InspirationCharges != before) conn.SupportGems.PendingStats = true;
        }
    }

    /// <summary>The Arcane Surge buff's cast speed and mana regeneration, added to the character's stat sources while
    /// it runs (its more Spell Damage is applied per supported hit in ConditionalSupportDamage).</summary>
    internal static Dictionary<uint, int> WithSupportGemBuffs(ClientState conn, Dictionary<uint, int> stats)
    {
        lock (conn.Monsters)
        {
            if (!conn.SupportGems.ArcaneSurgeActiveAt(conn.Clock.Elapsed.TotalSeconds)) return stats;
            void Add(string name, int value)
            {
                if (value != 0 && Stats.Key(name) is uint key)
                    stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
            }
            Add("arcane_surge_cast_speed_+%", conn.SupportGems.ArcaneSurgeCastSpeedPct);
            Add("mana_regeneration_rate_+%", conn.SupportGems.ArcaneSurgeManaRegenPct);
        }
        return stats;
    }

    internal static bool ArcaneSurgeActive(ClientState conn, double now) => conn.SupportGems.ArcaneSurgeActiveAt(now);

    static List<byte[]> PresentationLocked(ClientState conn)
    {
        var state = conn.SupportGems;
        double now = conn.Clock.Elapsed.TotalSeconds;
        var packets = new List<byte[]>();
        bool arcane = state.ArcaneSurgeActiveAt(now);
        if (state.PresentedArcaneSurge != 0 && (!arcane || state.ArcaneSurgeRefresh))
        {
            packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.PresentedArcaneSurge));
            state.PresentedArcaneSurge = 0;
            state.PendingStats = true;
        }
        state.ArcaneSurgeRefresh = false;
        if (arcane && state.PresentedArcaneSurge == 0)
        {
            state.ArcaneSurgeBuff = conn.NextBuff++;
            state.PresentedArcaneSurge = state.ArcaneSurgeBuff;
            packets.Add(InstanceEntities.AddBuff(PlayerId, state.ArcaneSurgeBuff, ArcaneSurgeBuffRow, PlayerId,
                u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, state.ArcaneSurgeUntil - now))));
            state.PendingStats = true;
        }

        int charges = state.InspirationCharges;
        if (state.PresentedInspiration != 0 && charges == 0)
        {
            packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.PresentedInspiration));
            state.PresentedInspiration = 0;
            state.PresentedInspirationCharges = 0;
        }
        if (charges > 0 && (state.PresentedInspiration == 0 || state.PresentedInspirationCharges != charges))
        {
            if (state.PresentedInspiration != 0)
                packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.PresentedInspiration));
            state.InspirationBuff = conn.NextBuff++;
            state.PresentedInspiration = state.InspirationBuff;
            state.PresentedInspirationCharges = charges;
            packets.Add(InstanceEntities.AddBuff(PlayerId, state.InspirationBuff, RighteousChargeBuffRow, PlayerId,
                stacks: (ushort)charges));
        }
        return packets;
    }

    /// <summary>The Arcane Surge and Inspiration buff instances and their stat refresh, from the player tick.</summary>
    internal static async Task SendSupportGemBuffs(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn)
    {
        List<byte[]> packets;
        bool stats;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired
                || (!conn.PlayerObjectCreated && !conn.PlayerSent)) return;
            packets = PresentationLocked(conn);
            stats = conn.SupportGems.PendingStats;
            conn.SupportGems.PendingStats = false;
        }
        foreach (var packet in packets) await Send(remote, stream, ciphers, packet, null);
        if (stats) await SendCharacterStats(remote, stream, ciphers, conn, "support gem buffs changed");
    }
}
