using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Inspiration charges cap at five (poedb: maximum_righteous_charges is the buff's own maximum).
    public const int InspirationMaxCharges = 5;
    const uint InspirationElementalMore = Stat.ElementalDamagePlusPctFinalPerRighteousCharge;

    // BuffDefinitions rows: arcane_surge (895), righteous_charge (1378) and cruelty (1815).
    const ushort ArcaneSurgeBuffRow = 895, RighteousChargeBuffRow = 1378, CrueltyBuffRow = 1815;

    /// <summary>Arcane Surge without the support: "Arcane Surge grants 20% increased Cast Speed and 30% increased Mana
    /// Regeneration rate, for 4 seconds" (ReminderTextArcaneSurge; SupportArcaneSurge's constant stats are the same).</summary>
    public const int ArcaneSurgeCastSpeedPct = 20, ArcaneSurgeManaRegenPct = 30, ArcaneSurgeDurationMs = 4000;

    // A refresh that moves the shown deadline by less than this keeps the client's buff (a hit per target would
    // otherwise send a remove and an add for each).
    const double ArcaneSurgeRepresentSeconds = .25;

    /// <summary>Gain or refresh the one Arcane Surge buff (in the area's frame). Its values are captured at the gain,
    /// at least those already running.</summary>
    public static void GainArcaneSurgeLocked(Player player, double now, int durationMs, int castSpeedPct, int manaRegenPct)
    {
        var state = player.SupportGems;
        bool running = state.ArcaneSurgeActiveAt(now);
        double until = now + durationMs / 1000.0;
        bool stronger = !running || castSpeedPct > state.ArcaneSurgeCastSpeedPct || manaRegenPct > state.ArcaneSurgeManaRegenPct;
        if (!stronger && until <= state.ArcaneSurgeUntil) return;
        if (stronger) state.PendingStats = true;
        state.ArcaneSurgeCastSpeedPct = running ? Math.Max(state.ArcaneSurgeCastSpeedPct, castSpeedPct) : castSpeedPct;
        state.ArcaneSurgeManaRegenPct = running ? Math.Max(state.ArcaneSurgeManaRegenPct, manaRegenPct) : manaRegenPct;
        state.ArcaneSurgeUntil = running ? Math.Max(state.ArcaneSurgeUntil, until) : until;
        PresentStats(player);
        if (stronger || state.ArcaneSurgeUntil - state.ArcaneSurgeShownUntil >= ArcaneSurgeRepresentSeconds)
        {
            state.ArcaneSurgeRefresh = true;
            state.ArcaneSurgeShownUntil = state.ArcaneSurgeUntil;
        }
    }

    /// <summary>Arcane Blessing, "Gain Arcane Surge when you or your Totems Hit an Enemy with a Spell": a spell hit that
    /// landed on a hostile monster (the area and projectile executors, a totem's shots included; under the Monsters
    /// lock). <paramref name="player"/> is the hit's caster stats (a totem's are its owner's).</summary>
    public static void ArcaneSurgeOnSpellHitLocked(Player player, IReadOnlyDictionary<uint, int> playerStats, SpawnedMonster target)
    {
        if (!target.Hostile || player.LeavingArea || player.PlayerLife == 0
            || !(Stat.GainArcaneSurgeOnSpellHitByYouOrYourTotems is var key)
            || playerStats.GetValueOrDefault(key) == 0) return;
        GainArcaneSurgeLocked(player, AreaTime(player), ArcaneSurgeDurationMs, ArcaneSurgeCastSpeedPct,
            ArcaneSurgeManaRegenPct);
    }

    // Normal and Vaal admissions have already debited these resources. Vaal Life costs retain the Vaal
    // resource clock for the buff deadline. RF observes the committed change on the player tick's area clock.
    static void ApplyPaidSupportCostsLocked(Player player, ushort skill, IReadOnlyDictionary<uint, int> own,
        uint mana, int life, double? lifePaidAt = null)
    {
        if (mana == 0 && life == 0) return;
        double now = AreaTime(player);
        if (life > 0)
        {
            double prior = player.Lifetap.Until;
            Lifetap.Spend(player, own, life, lifePaidAt ?? now);
            if (prior != player.Lifetap.Until && player.RighteousFire is { ConditionalSupportMore.Lifetap: not 0 } rf)
                RecordRighteousFireSupportChangeLocked(player, rf, now);
        }
        SupportManaSpentLocked(player, skill, own, mana, now);
    }

    /// <summary>Arcane Surge and Inspiration bookkeeping for one paid supported skill. Called in the area's frame,
    /// after the skill's cost has been paid and before the cast starts.</summary>
    public static void SupportManaSpentLocked(Player player, ushort skillId, IReadOnlyDictionary<uint, int> stats,
        uint cost, double now)
    {
        if (cost == 0 || player.LeavingArea || player.PlayerLife == 0) return;
        int Get(uint key) => stats.GetValueOrDefault(key);

        // Arcane Surge: "each supported spell tracks how much mana you spend on it, granting a buff when the total
        // mana spent reaches a threshold".
        int threshold = Get(Stat.SupportArcaneSurgeGainBuffOnManaUseThreshold);
        if (threshold <= 0 && Get(Stat.SupportArcaneSurgeSpellDamagePlusPctFinalWhileYouHaveArcaneSurge) != 0)
            threshold = 1;
        if (threshold > 0)
        {
            long spent = player.SupportGems.ArcaneSurgeSpent.GetValueOrDefault(skillId) + cost;
            if (spent >= threshold)
            {
                int durationMs = Get(Stat.SupportArcaneSurgeBaseDurationMs);
                GainArcaneSurgeLocked(player, now, durationMs > 0 ? durationMs : ArcaneSurgeDurationMs,
                    Get(Stat.SupportArcaneSurgeCastSpeedPlusPct), Get(Stat.SupportArcaneSurgeManaRegenerationRatePlusPct));
                spent = 0;
            }
            player.SupportGems.ArcaneSurgeSpent[skillId] = spent;
        }

        // Inspiration: one shared mana counter for every skill linked to the support. A use that pushes it to the
        // support's threshold resets the counter and removes all charges; otherwise the use gains a charge, five at
        // most, and refreshes the charges' ten-second lifetime.
        int gain = Get(Stat.GainRighteousChargeOnManaSpentPct);
        if (gain <= 0 && (Get(Stat.SupportInspirationManaCostPlusPctFinal) != 0
            || Get(InspirationElementalMore) != 0)) gain = 100;
        if (gain > 0)
        {
            ExpireInspirationLocked(player.SupportGems, now);
            player.SupportGems.InspirationSpent += cost;
            int lose = Get(Stat.LoseAllRighteousChargesOnManaUseThreshold);
            if (lose > 0 && player.SupportGems.InspirationSpent >= lose)
            {
                player.SupportGems.InspirationSpent = 0;
                if (player.SupportGems.InspirationCharges != 0) player.SupportGems.PendingStats = true;
                player.SupportGems.InspirationCharges = 0;
                PresentStats(player);
            }
            else
            {
                int before = player.SupportGems.InspirationCharges;
                if (gain >= 100 || AreaRng(player).Next(100) < gain)
                {
                    int durationPct = Get(Stat.InspirationChargeDurationPlusPct);
                    player.SupportGems.InspirationUntil = now + 10 * Math.Max(0, 1 + durationPct / 100.0);
                    player.SupportGems.InspirationRefresh = true;
                    player.SupportGems.InspirationCharges = Math.Min(InspirationMaxCharges,
                        player.SupportGems.InspirationCharges + 1);
                }
                if (player.SupportGems.InspirationCharges != before) player.SupportGems.PendingStats = true;
                if (player.SupportGems.InspirationRefresh || player.SupportGems.PendingStats) PresentStats(player);
            }
            if (player.RighteousFire is { ConditionalSupportMore.PerInspiration: not 0 } rf)
                RecordRighteousFireSupportChangeLocked(player, rf, now);
        }
    }

    /// <summary>The Arcane Surge buff's stats (BuffDefinitions arcane_surge: arcane_surge_cast_speed_+%,
    /// mana_regeneration_rate_+%, spell_damage_+%_final_from_arcane_surge), raised by <c>arcane_surge_effect_+%</c> and
    /// truncated, and the "while you have Arcane Surge" stats moved to their plain forms, added to the character's stat
    /// sources while it runs. The support's own more Spell Damage is applied per supported hit in
    /// ConditionalSupportDamage; the buff's spell_damage_+%_final_from_arcane_surge is Arcane Blessing's "Arcane Surge
    /// also grants 20% more Spell Damage to you".</summary>
    public static Dictionary<uint, int> WithSupportGemBuffs(Player player, Dictionary<uint, int> stats)
    {
        stats = WithSelfBuffs(player, WithTriggeredGuard(player, stats));
        AddResonanceStats(player, stats);
        if (!player.SupportGems.ArcaneSurgeActiveAt(AreaTime(player))) return stats;
        StatsChangeIn(player.SupportGems.ArcaneSurgeUntil - AreaTime(player));
        int Get(uint k) => stats.GetValueOrDefault(k);
        void Add(uint name, long value)
        {
            if (value != 0 && name is var key)
                stats[key] = (int)Math.Clamp(stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        }
        long effect = Math.Max(0, 100L + Get(Stat.ArcaneSurgeEffectPlusPct));
        long Buff(int value) => value * effect / 100;
        Add(Stat.HasArcaneSurge, 1);
        Add(Stat.ArcaneSurgeCastSpeedPlusPct, Buff(player.SupportGems.ArcaneSurgeCastSpeedPct));
        Add(Stat.ManaRegenerationRatePlusPct, Buff(player.SupportGems.ArcaneSurgeManaRegenPct));
        Add(ArcaneSurgeSpellMore, Buff(Get(Stat.ArcaneSurgeOnYouSpellDamagePlusPctFinalFromHierophant)));
        Add(SkillArea.BaseIncreased, Get(Stat.AreaOfEffectPlusPctWhileYouHaveArcaneSurge));
        Add(Stat.BaseLifeLeechFromSpellDamagePermyriad, Get(Stat.LifeLeechFromSpellDamagePermyriadWhileYouHaveArcaneSurge));
        Add(Stat.NonDamagingAilmentEffectPlusPctOnSelf, Get(Stat.NonDamagingAilmentEffectPlusPctOnSelfWhileYouHaveArcaneSurge));
        return stats;
    }

    /// <summary>The Arcane Surge buff's more Spell Damage on the player (PlayerHitDamage's spell more).</summary>
    public const uint ArcaneSurgeSpellMore = Stat.SpellDamagePlusPctFinalFromArcaneSurge;

    public static bool ArcaneSurgeActive(Player player, double now) => player.SupportGems.ArcaneSurgeActiveAt(now);

    // A generic Arcane Surge source (equipment, passives, tattoos; not the support gem's per-spell threshold): the buff
    // is the same one Arcane Surge Support grants - 20% increased Cast Speed, 30% increased Mana Regeneration Rate for
    // 4 seconds (poewiki Arcane Surge: equipment/passive/Ascendancy sources are identical to the support's buff; the
    // support's more Spell Damage is its own per-supported-skill stat). A gain never downgrades a stronger running buff.
    public const int ArcaneSurgeCastSpeed = ArcaneSurgeCastSpeedPct, ArcaneSurgeManaRegen = ArcaneSurgeManaRegenPct;
    public const double ArcaneSurgeSeconds = ArcaneSurgeDurationMs / 1000.0;

    /// <summary>Grants Arcane Surge from a generic source (the Honoured Flood tattoo, "Gain Arcane Surge on Hit with
    /// Spells"): the same helper as the support's gain, so both share the 0.25 s refresh limit, the shown deadline and
    /// the strongest running values, and an expired surge's values are not carried into this one. Under the Monsters
    /// lock; the stat presentations show it.</summary>
    public static void GainArcaneSurgeLocked(Player player, double now) =>
        GainArcaneSurgeLocked(player, now, ArcaneSurgeDurationMs, ArcaneSurgeCastSpeed, ArcaneSurgeManaRegen);

    /// <summary>"X% chance to Gain Arcane Surge on Hit with Spells" (the Honoured Flood tattoo and other sources): one
    /// roll per spell hit that landed. In the area's frame, after the hit's damage.</summary>
    public static void ArcaneSurgeOnSpellHitLocked(Player player, IReadOnlyDictionary<uint, int> playerStats, double now,
        Random random)
    {
        uint key = Stat.GainArcaneSurgeOnHitPctChance;
        if (RandomRolls.Chance(playerStats.GetValueOrDefault(key), 100, random)) GainArcaneSurgeLocked(player, now);
    }

    static void ExpireInspirationLocked(SupportGemState state, double now)
    {
        if (state.InspirationCharges > 0 && now >= state.InspirationUntil)
        {
            state.InspirationCharges = 0;
            state.PendingStats = true;
        }
    }

    static List<byte[]> PresentationLocked(Player player)
    {
        var state = player.SupportGems;
        double now = AreaTime(player);
        var packets = new List<byte[]>();
        foreach (double ends in state.CrueltyInstances.Values.Append(state.ArcaneSurgeUntil)
            .Append(state.InspirationCharges > 0 ? state.InspirationUntil : 0))
            if (ends > now) PresentIn(player, ends - now);
        bool arcane = state.ArcaneSurgeActiveAt(now);
        if (state.PresentedArcaneSurge != 0 && (!arcane || state.ArcaneSurgeRefresh))
        {
            player.Area.Buffs.Clear(player.Id, ArcaneSurgeBuffRow, player.Id);
            state.PresentedArcaneSurge = 0;
            state.PendingStats = true;
        }
        state.ArcaneSurgeRefresh = false;
        if (arcane && state.PresentedArcaneSurge == 0)
        {
            state.ArcaneSurgeBuff = player.NextBuff++;
            state.PresentedArcaneSurge = state.ArcaneSurgeBuff;
            player.Area.Buffs.Set(player.Id, new(ArcaneSurgeBuffRow, player.Id, U68: Buff.Seconds(state.ArcaneSurgeUntil - now)));
            state.PendingStats = true;
        }

        // Cruelty: one buff for the player, showing the strongest running instance (BuffDefinitions 1815, its one
        // GrantedStats value is the more damage over time); when it ends a weaker running one is shown in its place.
        // The client's field +0x82 is a percent increase to the shown value, so it is sent as 0, as the elemental
        // ailment rows' is.
        var (crueltyPct, crueltyUntil) = state.CrueltyActiveAt(now);
        int shownCruelty = state.PresentedCrueltyPct;
        bool crueltyChanged = crueltyPct != shownCruelty;
        if (state.PresentedCruelty != 0 && (crueltyPct == 0 || crueltyChanged
            || crueltyUntil - state.CrueltyShownUntil >= ArcaneSurgeRepresentSeconds))
        {
            player.Area.Buffs.Clear(player.Id, CrueltyBuffRow, player.Id);
            state.PresentedCruelty = 0;
            state.PresentedCrueltyPct = 0;
        }
        // The ailment rows the panel shows change with the buff, so a change of value refreshes the character stats; a
        // refresh of the deadline alone only re-sends the buff.
        if (crueltyChanged) state.PendingStats = true;
        if (crueltyPct > 0 && state.PresentedCruelty == 0)
        {
            state.CrueltyBuff = player.NextBuff++;
            state.PresentedCruelty = state.CrueltyBuff;
            state.PresentedCrueltyPct = crueltyPct;
            state.CrueltyShownUntil = crueltyUntil;
            player.Area.Buffs.Set(player.Id, new(CrueltyBuffRow, player.Id, U68: Buff.Seconds(crueltyUntil - now),
                U82: 0, Values: [(uint)crueltyPct]));
        }

        // Inspiration charges last ten seconds (longer with the support's quality) and all of them expire together.
        ExpireInspirationLocked(state, now);
        int charges = state.InspirationCharges;
        if (state.PresentedInspiration != 0 && charges == 0)
        {
            player.Area.Buffs.Clear(player.Id, RighteousChargeBuffRow, player.Id);
            state.PresentedInspiration = 0;
            state.PresentedInspirationCharges = 0;
            state.InspirationRefresh = false;
        }
        if (charges > 0 && (state.PresentedInspiration == 0 || state.PresentedInspirationCharges != charges
            || state.InspirationRefresh))
        {
            state.InspirationBuff = player.NextBuff++;
            state.PresentedInspiration = state.InspirationBuff;
            state.PresentedInspirationCharges = charges;
            player.Area.Buffs.Set(player.Id, new(RighteousChargeBuffRow, player.Id, U68: Buff.Seconds(state.InspirationUntil - now),
                Stacks: (ushort)charges));
            state.InspirationRefresh = false;
        }
        PresentIntensity(player, now);
        return packets;
    }

    /// <summary>The Arcane Surge, Cruelty and Inspiration buff instances and their stat refresh: a stat presentation.</summary>
    public static async Task SendSupportGemBuffs(Player player)
    {
        List<byte[]> packets;
        bool stats;
        if (player.Closed || player.LeavingArea || player.RageRetired
            || (!player.Connection.PlayerObjectCreated && !player.Connection.PlayerSent)) return;
        packets = PresentationLocked(player);
        stats = player.SupportGems.PendingStats;
        player.SupportGems.PendingStats = false;
        foreach (var packet in packets) await Send(player, packet, null);
        if (stats) await SendCharacterStats(player, PlayerStatChange.Sources);
    }
}
