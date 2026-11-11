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

    /// <summary>Arcane Surge without the support: "Arcane Surge grants 20% increased Cast Speed and 30% increased Mana
    /// Regeneration rate, for 4 seconds" (ReminderTextArcaneSurge; SupportArcaneSurge's constant stats are the same).</summary>
    public const int ArcaneSurgeCastSpeedPct = 20, ArcaneSurgeManaRegenPct = 30, ArcaneSurgeDurationMs = 4000;

    // A refresh that moves the shown deadline by less than this keeps the client's buff (a hit per target would
    // otherwise send a remove and an add for each).
    const double ArcaneSurgeRepresentSeconds = .25;

    /// <summary>Gain or refresh the one Arcane Surge buff (under the Monsters lock). Its values are captured at the gain,
    /// at least those already running.</summary>
    public static void GainArcaneSurgeLocked(ClientState conn, double now, int durationMs, int castSpeedPct, int manaRegenPct)
    {
        var state = conn.SupportGems;
        bool running = state.ArcaneSurgeActiveAt(now);
        double until = now + durationMs / 1000.0;
        bool stronger = !running || castSpeedPct > state.ArcaneSurgeCastSpeedPct || manaRegenPct > state.ArcaneSurgeManaRegenPct;
        if (!stronger && until <= state.ArcaneSurgeUntil) return;
        if (stronger) state.PendingStats = true;
        state.ArcaneSurgeCastSpeedPct = running ? Math.Max(state.ArcaneSurgeCastSpeedPct, castSpeedPct) : castSpeedPct;
        state.ArcaneSurgeManaRegenPct = running ? Math.Max(state.ArcaneSurgeManaRegenPct, manaRegenPct) : manaRegenPct;
        state.ArcaneSurgeUntil = running ? Math.Max(state.ArcaneSurgeUntil, until) : until;
        if (stronger || state.ArcaneSurgeUntil - state.ArcaneSurgeShownUntil >= ArcaneSurgeRepresentSeconds)
        {
            state.ArcaneSurgeRefresh = true;
            state.ArcaneSurgeShownUntil = state.ArcaneSurgeUntil;
        }
    }

    /// <summary>Arcane Blessing, "Gain Arcane Surge when you or your Totems Hit an Enemy with a Spell": a spell hit that
    /// landed on a hostile monster (the area and projectile executors, a totem's shots included; under the Monsters
    /// lock). <paramref name="player"/> is the hit's caster stats (a totem's are its owner's).</summary>
    public static void ArcaneSurgeOnSpellHitLocked(ClientState conn, IReadOnlyDictionary<uint, int> player, SpawnedMonster target)
    {
        if (!target.Hostile || conn.LeavingArea || conn.PlayerLife == 0
            || Stats.Key("gain_arcane_surge_on_spell_hit_by_you_or_your_totems") is not uint key
            || player.GetValueOrDefault(key) == 0) return;
        GainArcaneSurgeLocked(conn, conn.Clock.Elapsed.TotalSeconds, ArcaneSurgeDurationMs, ArcaneSurgeCastSpeedPct,
            ArcaneSurgeManaRegenPct);
    }

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
                GainArcaneSurgeLocked(conn, now, durationMs > 0 ? durationMs : ArcaneSurgeDurationMs,
                    Get("support_arcane_surge_cast_speed_+%"), Get("support_arcane_surge_mana_regeneration_rate_+%"));
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

    /// <summary>The Arcane Surge buff's stats (BuffDefinitions arcane_surge: arcane_surge_cast_speed_+%,
    /// mana_regeneration_rate_+%, spell_damage_+%_final_from_arcane_surge), raised by <c>arcane_surge_effect_+%</c> and
    /// truncated, and the "while you have Arcane Surge" stats moved to their plain forms, added to the character's stat
    /// sources while it runs. The support's own more Spell Damage is applied per supported hit in
    /// ConditionalSupportDamage; the buff's spell_damage_+%_final_from_arcane_surge is Arcane Blessing's "Arcane Surge
    /// also grants 20% more Spell Damage to you".</summary>
    public static Dictionary<uint, int> WithSupportGemBuffs(ClientState conn, Dictionary<uint, int> stats)
    {
        lock (conn.Monsters)
        {
            stats = WithTriggeredGuard(conn, stats);
            if (!conn.SupportGems.ArcaneSurgeActiveAt(conn.Clock.Elapsed.TotalSeconds)) return stats;
            int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
            void Add(string name, long value)
            {
                if (value != 0 && Stats.Key(name) is uint key)
                    stats[key] = (int)Math.Clamp(stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
            }
            long effect = Math.Max(0, 100L + Get("arcane_surge_effect_+%"));
            long Buff(int value) => value * effect / 100;
            Add("has_arcane_surge", 1);
            Add("arcane_surge_cast_speed_+%", Buff(conn.SupportGems.ArcaneSurgeCastSpeedPct));
            Add("mana_regeneration_rate_+%", Buff(conn.SupportGems.ArcaneSurgeManaRegenPct));
            Add(ArcaneSurgeSpellMore, Buff(Get("arcane_surge_on_you_spell_damage_+%_final_from_hierophant")));
            Add(SkillArea.BaseIncreased, Get("area_of_effect_+%_while_you_have_arcane_surge"));
            Add("base_life_leech_from_spell_damage_permyriad", Get("life_leech_from_spell_damage_permyriad_while_you_have_arcane_surge"));
            Add("non_damaging_ailment_effect_+%_on_self", Get("non_damaging_ailment_effect_+%_on_self_while_you_have_arcane_surge"));
        }
        return stats;
    }

    /// <summary>The Arcane Surge buff's more Spell Damage on the player (PlayerHitDamage's spell more).</summary>
    public const string ArcaneSurgeSpellMore = "spell_damage_+%_final_from_arcane_surge";

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
