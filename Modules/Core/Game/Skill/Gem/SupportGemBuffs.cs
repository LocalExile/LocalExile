using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Inspiration charges cap at five (poedb: maximum_righteous_charges is the buff's own maximum).
    public const int InspirationMaxCharges = 5;

    // BuffDefinitions rows: arcane_surge (895) and righteous_charge (1378).
    const ushort ArcaneSurgeBuffRow = 895, RighteousChargeBuffRow = 1378;

    /// <summary>Arcane Surge and Inspiration bookkeeping for one paid supported skill. Called under the Monsters lock,
    /// after the skill's cost has been paid and before the cast starts.</summary>
    public static void SupportManaSpentLocked(ClientState conn, ushort skillId, IReadOnlyDictionary<uint, int> stats,
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

        // Inspiration: one shared mana counter for every skill linked to the support. A use that pushes it to the
        // support's threshold resets the counter and removes all charges; otherwise the use gains a charge, five at
        // most, and refreshes the charges' ten-second lifetime.
        int gain = Get("gain_righteous_charge_on_mana_spent_%");
        if (gain <= 0 && (Get("support_inspiration_mana_cost_+%_final") != 0
            || Get("elemental_damage_+%_final_per_righteous_charge") != 0)) gain = 100;
        if (gain > 0)
        {
            conn.SupportGems.InspirationSpent += cost;
            int lose = Get("lose_all_righteous_charges_on_mana_use_threshold");
            if (lose > 0 && conn.SupportGems.InspirationSpent >= lose)
            {
                conn.SupportGems.InspirationSpent = 0;
                if (conn.SupportGems.InspirationCharges != 0) conn.SupportGems.PendingStats = true;
                conn.SupportGems.InspirationCharges = 0;
            }
            else
            {
                int before = conn.SupportGems.InspirationCharges;
                if (gain >= 100 || Random.Shared.Next(100) < gain)
                {
                    int durationPct = Get("inspiration_charge_duration_+%");
                    conn.SupportGems.InspirationUntil = now + 10 * Math.Max(0, 1 + durationPct / 100.0);
                    conn.SupportGems.InspirationRefresh = true;
                    conn.SupportGems.InspirationCharges = Math.Min(InspirationMaxCharges,
                        conn.SupportGems.InspirationCharges + 1);
                }
                if (conn.SupportGems.InspirationCharges != before) conn.SupportGems.PendingStats = true;
            }
        }
    }

    /// <summary>The Arcane Surge buff's cast speed and mana regeneration, added to the character's stat sources while
    /// it runs (its more Spell Damage is applied per supported hit in ConditionalSupportDamage).</summary>
    public static Dictionary<uint, int> WithSupportGemBuffs(ClientState conn, Dictionary<uint, int> stats)
    {
        lock (conn.Monsters)
        {
            stats = WithTriggeredGuard(conn, stats);
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

    public static bool ArcaneSurgeActive(ClientState conn, double now) => conn.SupportGems.ArcaneSurgeActiveAt(now);

    // A generic Arcane Surge source (equipment, passives, tattoos; not the support gem's per-spell threshold): the buff
    // is the same one Arcane Surge Support grants - 20% increased Cast Speed, 30% increased Mana Regeneration Rate for
    // 4 seconds (poewiki Arcane Surge: equipment/passive/Ascendancy sources are identical to the support's buff; the
    // support's more Spell Damage is its own per-supported-skill stat). A gain never downgrades a stronger running buff.
    public const int ArcaneSurgeCastSpeed = 20, ArcaneSurgeManaRegen = 30;
    public const double ArcaneSurgeSeconds = 4;

    /// <summary>Grants Arcane Surge from a generic source (the Honoured Flood tattoo, "Gain Arcane Surge on Hit with
    /// Spells"), refreshing its four seconds. Under the Monsters lock; the player tick presents it.</summary>
    public static void GainArcaneSurgeLocked(ClientState conn, double now)
    {
        var state = conn.SupportGems;
        state.ArcaneSurgeUntil = Math.Max(state.ArcaneSurgeUntil, now + ArcaneSurgeSeconds);
        state.ArcaneSurgeCastSpeedPct = Math.Max(state.ArcaneSurgeCastSpeedPct, ArcaneSurgeCastSpeed);
        state.ArcaneSurgeManaRegenPct = Math.Max(state.ArcaneSurgeManaRegenPct, ArcaneSurgeManaRegen);
        state.ArcaneSurgeRefresh = true;
        state.PendingStats = true;
    }

    /// <summary>"X% chance to Gain Arcane Surge on Hit with Spells" (the Honoured Flood tattoo and other sources): one
    /// roll per spell hit that landed. Under the Monsters lock, after the hit's damage.</summary>
    public static void ArcaneSurgeOnSpellHitLocked(ClientState conn, IReadOnlyDictionary<uint, int> player, double now,
        Random random)
    {
        if (Stats.Key("gain_arcane_surge_on_hit_%_chance") is not uint key) return;
        if (RandomRolls.Chance(player.GetValueOrDefault(key), 100, random)) GainArcaneSurgeLocked(conn, now);
    }

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

        // Inspiration charges last ten seconds (longer with the support's quality) and all of them expire together.
        if (state.InspirationCharges > 0 && now >= state.InspirationUntil)
        {
            state.InspirationCharges = 0;
            state.PendingStats = true;
        }
        int charges = state.InspirationCharges;
        if (state.PresentedInspiration != 0 && charges == 0)
        {
            packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.PresentedInspiration));
            state.PresentedInspiration = 0;
            state.PresentedInspirationCharges = 0;
            state.InspirationRefresh = false;
        }
        if (charges > 0 && (state.PresentedInspiration == 0 || state.PresentedInspirationCharges != charges
            || state.InspirationRefresh))
        {
            if (state.PresentedInspiration != 0)
                packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.PresentedInspiration));
            state.InspirationBuff = conn.NextBuff++;
            state.PresentedInspiration = state.InspirationBuff;
            state.PresentedInspirationCharges = charges;
            packets.Add(InstanceEntities.AddBuff(PlayerId, state.InspirationBuff, RighteousChargeBuffRow, PlayerId,
                u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, state.InspirationUntil - now)),
                stacks: (ushort)charges));
            state.InspirationRefresh = false;
        }
        return packets;
    }

    /// <summary>The Arcane Surge and Inspiration buff instances and their stat refresh, from the player tick.</summary>
    public static async Task SendSupportGemBuffs(string remote, NetworkStream stream, ConnectionCiphers ciphers,
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
