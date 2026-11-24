using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The node and tattoo stats that name a Spirit chieftain trigger with an event the server has. The Ascendancy
    // nodes grant their Summon Spirit skills directly; the Loyalty tattoos grant the level 1 ones from
    // PassiveSkillOverrides.Effect and carry the same trigger stat in their TattooStats row.
    public const uint CritByUniqueTrigger = Stat.HasTriggerOnCritByUniqueEnemy,
        SavageHitFromUniqueTrigger = Stat.HasTriggerOnTakingSavageHitFromUnique,
        BlockFromUniqueTrigger = Stat.HasTriggerOnBlockHitFromUnique,
        TotemDeathInPresenceOfUniqueTrigger = Stat.HasTriggerOnTotemDeathInPresenceOfUnique,
        CritVsMarkedUniqueTrigger = Stat.HasTriggerOnCritVsMarkedUnique,
        SuppressHitFromUniqueTrigger = Stat.HasTriggerOnSuppressHitFromUnique,
        MaxRageInPresenceTrigger = Stat.HasTriggerOnReachingMaximumRageInPresenceOfUnique,
        TravelSkillInPresenceTrigger = Stat.HasTriggerOnTravelSkillUseInPresenceOfUnique,
        EsRechargeInPresenceTrigger = Stat.HasTriggerOnEsRechargeInPresenceOfUnique,
        LowLifeInPresenceTrigger = Stat.HasTriggerOnReachingLowLifeInPresenceOfUnique;

    /// <summary>"while a Unique Enemy is in your Presence": a living hostile Unique monster within Character.ot's
    /// fixed base_presence_radius (80 cells, the wiki's 8 metres) of the player. In the area's frame.</summary>
    public static bool UniqueInPresenceLocked(Player player, double now)
    {
        var here = player.Walk.Position(now);
        return player.Area.Monsters.Values.Any(m => m.Hostile && !m.Dead && m.Life > 0
            && m.Rarity == MonsterRarity.Unique && Dist(m.Walk.Position(now), here) <= PresenceRadius);
    }

    // The Presence-gated Loyalty triggers (maximum Rage, Travel Skill, Energy Shield Recharge, Low Life). Under the
    // Monsters lock; false without a Unique in the player's presence.
    static bool LoyaltyInPresenceTriggerLocked(Player player, uint trigger, double now)
    {
        if (!CurrentGameWork || player.PlayerLife == 0 || !UniqueInPresenceLocked(player, now)) return false;
        return TriggerSpiritChieftainLocked(player, trigger, CharacterPlayerStats(player));
    }

    /// <summary>"Trigger ... when you reach Maximum Rage while a Unique Enemy is in your Presence" (Loyalty Tattoo of
    /// Akoya): called when Rage reaches its maximum. In the area's frame.</summary>
    public static void LoyaltyMaxRageLocked(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        double now = AreaTime(player);
        if (!CurrentGameWork || player.PlayerLife == 0 || !UniqueInPresenceLocked(player, now)) return;
        TriggerSpiritChieftainLocked(player, MaxRageInPresenceTrigger, stats);
    }

    /// <summary>"when you use a Travel Skill while a Unique Enemy is in your Presence" (Loyalty Tattoo of Ikiaho).</summary>
    public static void LoyaltyTravelSkillTriggered(Player player)
    {
        if (!CurrentGameWork || player.PlayerLife == 0) return;
        LoyaltyInPresenceTriggerLocked(player, TravelSkillInPresenceTrigger, AreaTime(player));
    }

    /// <summary>The once-per-transition Loyalty checks (Energy Shield Recharge starting, Low Life reached) from the
    /// player's recovery tick. In the area's frame.</summary>
    public static void LoyaltyResourceStateLocked(Player player, IReadOnlyDictionary<uint, int> stats, double now)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0)
        {
            player.LoyaltyRechargeSeen = player.LoyaltyLowLifeSeen = false;
            return;
        }
        int Stat(uint key) => stats.GetValueOrDefault(key);
        bool recharging = player.EnergyShield.Recharging;
        if (recharging && !player.LoyaltyRechargeSeen) LoyaltyInPresenceTriggerLocked(player, EsRechargeInPresenceTrigger, now);
        player.LoyaltyRechargeSeen = recharging;
        bool low = LowLife.Is(player.PlayerLife, player.PlayerMaxLife,
            LowLife.Threshold(Stat(global::Stat.LowLifeThresholdPctOverride), Stat(global::Stat.LowLifeThresholdPct)));
        if (low && !player.LoyaltyLowLifeSeen) LoyaltyInPresenceTriggerLocked(player, LowLifeInPresenceTrigger, now);
        player.LoyaltyLowLifeSeen = low;
    }

    /// <summary>"when you Suppress Spell Damage from a Unique Enemy" (Loyalty Tattoo of Ahuana): after a Unique's spell
    /// hit was suppressed. In the area's frame.</summary>
    public static void LoyaltySuppressedHitLocked(Player player, IReadOnlyDictionary<uint, int> stats) =>
        TriggerSpiritChieftainLocked(player, SuppressHitFromUniqueTrigger, stats);

    /// <summary>"on Critical Strike against Marked Unique Enemy" (Loyalty Tattoo of Rakiata): the target is a living
    /// Unique and carries a Mark the player applied (Mark curses are modelled by Curses). In the area's frame.</summary>
    public static void LoyaltyCritVsMarkedUniqueLocked(Player player, SpawnedMonster m,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (!CurrentGameWork || player.PlayerLife == 0 || m.Rarity != MonsterRarity.Unique) return;
        if (m.Curses.All.Values.Any(c => c.Caster == player.Id && c.Mark))
            TriggerSpiritChieftainLocked(player, CritVsMarkedUniqueTrigger, stats);
    }

    /// <summary>
    /// After a Unique monster's hit on the living player, in the area's frame: blocking it triggers Spirit of Kiloava,
    /// taking it as a critical strike Spirit of Kahuturoa, blocked or not (wiki Blocking: a block does not prevent the
    /// defender's when-hit effects), and taking it as a Savage Hit (it removed at least 15% of maximum Life; Energy
    /// Shield, Ward and Mana do not count, wiki Savage hit) Spirit of Utula. True when one triggered.
    /// </summary>
    public static bool SpiritChieftainsWhenHitLocked(Player player, IReadOnlyDictionary<uint, int> stats, bool blocked,
        bool critical, long lifeLost)
    {
        if (!CurrentGameWork || player.PlayerLife == 0) return false;
        if (blocked && TriggerSpiritChieftainLocked(player, BlockFromUniqueTrigger, stats)) return true;
        return critical && TriggerSpiritChieftainLocked(player, CritByUniqueTrigger, stats)
            || lifeLost * 100 >= (long)player.PlayerMaxLife * 15 && TriggerSpiritChieftainLocked(player, SavageHitFromUniqueTrigger, stats);
    }

    // The allocated Spirit skill with this trigger, if its shared cooldown is ready: its Leadership for its duration
    // (base_skill_effect_duration with the player's skill effect and minion duration: these skills are CreatesMinion), the cooldown with the player's recovery.
    static bool TriggerSpiritChieftainLocked(Player player, uint trigger, IReadOnlyDictionary<uint, int> stats)
    {
        if (!CurrentGameWork) return false;
        double now = AreaTime(player);
        foreach (var grant in TriggeredGrantsLocked(player, trigger))
        {
            var own = SkillsFor(player).EffectStats(grant.Effect, grant.Level).ToDictionary(s => s.Item1, s => s.Item2);
            if (SpiritChieftains.LeadershipOf(own) is not { } leadership
                || SingleUseCooldown.ForSkill(SkillsFor(player), Stats, grant.Effect, grant.Level, stats, own) is not { } plan) continue;
            int Player(uint key) => stats.GetValueOrDefault(key);
            int Own(uint key) => own.GetValueOrDefault(key);
            double seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Player, Own, minion: true);
            if (!player.SpiritChieftains.TryTrigger(now, plan, leadership.Row, leadership.Stats, seconds)) continue;
            // The chieftain at the player's level up to the skill's maximum (minion_actor_level_is_user_level_up_to_maximum).
            if (leadership.Minion is int minion)
                player.SpiritChieftains.Pending = new(grant.EffectId, minion,
                    (byte)Math.Clamp(Math.Min(player.Pools.Level, Own(Stat.MinionActorLevelIsUserLevelUpToMaximum)), 1, 100), now + seconds);
            Console.WriteLine($"[gateway] {CharacterName(player)}: {grant.EffectId} triggered ({trigger}): buff {leadership.Row} "
                + $"for {seconds:0.##} s, cooldown {plan.BaseMilliseconds} ms at {plan.RecoveryRate:0.##}x after it");
            return true;
        }
        return false;
    }

    // The running Leadership's stats join the player's (CharacterPlayerStatsCore), while he lives.
    static void AddSpiritLeadershipStats(Player player, StatMap stats)
    {
        if (player.PlayerLife > 0 && player.SpiritChieftains.Active(AreaTime(player)) is { } buff)
        {
            StatsChangeIn(buff.Until - AreaTime(player));
            foreach (var (key, value) in buff.Stats)
                stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        }
    }

    // The Leadership buff on the player (S→C 0x1c1 with its remaining seconds, 0x1c2 when it ends or the player dies) and
    // the player's stats with or without its stats. Sent after a trigger and as a stat presentation.
    public static byte[]? SpiritChieftainPresentation(Player player)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea) return null;
        var spirits = player.SpiritChieftains;
        double now = AreaTime(player);
        var active = player.PlayerLife > 0 ? spirits.Active(now) : null;
        if (ReferenceEquals(active, spirits.Sent)) return null;
        var bytes = new List<byte>();
        if (spirits.Sent is { } old) player.Area.Buffs.Clear(player.Id, old.Row, player.Id);
        if (active is not null)
            player.Area.Buffs.Set(player.Id, new(active.Row, player.Id, U68: Buff.Seconds(active.Until - now)));
        spirits.Sent = active;
        bytes.AddRange(PlayerStatsOffFrames(player));
        Console.WriteLine($"[gateway] {CharacterName(player)}: Leadership buff "
            + (active is null ? "ends" : $"{active.Row} for {active.Until - now:0.##} s"));
        return [.. bytes];
    }

    static async Task SendSpiritChieftain(Player player)
    {
        await Send(player, [], null, atGate: () => SpiritChieftainPresentation(player));
        if (!CurrentGameWork) return;
        await SpiritChieftainMinion(player);
    }

    /// <summary>
    /// The chieftain minions, after a trigger and as a stat presentation. One whose time is up, or whose owner died, dies (0x1b1
    /// at 0) and its body goes a second later (0x2bd); a queued summon then rises next to the living player: the chieftain's
    /// MonsterVarieties row (AncestralEmbraceVariations) at its level, on his team, as his minion (is_player_minion), with
    /// his minion modifiers (minion_damage_+%, minion_attack_speed_+%, minion_attack_and_cast_speed_+%,
    /// minion_movement_speed_+%) as its own, until its Leadership ends. It is immortal: nothing on the server damages it.
    /// </summary>
    public static async Task SpiritChieftainMinion(Player player)
    {
        var bytes = new List<byte>();
        SpiritChieftains.Summon? summon;
        double now;
        if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
        now = AreaTime(player);
        bool alive = player.PlayerLife > 0;
        summon = alive ? player.SpiritChieftains.Pending : null;
        if (!alive) player.SpiritChieftains.Pending = null;
        RemoveMinionBodiesLocked(player, now);
        // The buff ends with the player's death (SpiritChieftains.End), so its minion does too.
        double until = alive ? player.SpiritChieftains.Active(now)?.Until ?? now : now;
        foreach (var minion in player.Minions.All.Where(m => m.Fights && m.SpiritChieftain).ToArray())
        {
            var m = minion.Monster;
            bool current = player.Area.Monsters.TryGetValue(minion.Id, out var held) && ReferenceEquals(held, m);
            if (current && !m.Dead && now < Math.Min(minion.Until, until)) continue;
            player.Minions.Remove(minion.Id);
            if (!current) continue;
            m.Aggro = false; m.Dead = true; m.Life = 0;
            player.Minions.Bodies.Add((minion.Id, m, now + 1));
            SetStats(player, minion.Id, [(Stat.HideMiniLifeBar, 1)]);
            Console.WriteLine($"[gateway] {CharacterName(player)}: chieftain 0x{minion.Id:x} ({m.Label}) ends");
        }
        if (bytes.Count > 0) await Send(player, [.. bytes], "0x01b1/0x02bd chieftain minion ends");
        if (!CurrentGameWork || summon is null || now >= summon.Until) return;
        if (!DarkMonarchAllows("SpiritChieftain", StatSourceValues(player)))
        {
            if (ReferenceEquals(player.SpiritChieftains.Pending, summon)) player.SpiritChieftains.Pending = null;
            return;
        }
        if (Monsters.Find(summon.Variety.ToString()) is not { Supported: true } variety)
        {
            Console.WriteLine($"[gateway] {CharacterName(player)}: {summon.Skill}: MonsterVarieties row {summon.Variety} cannot be created");
            return;
        }
        var playerStats = CharacterPlayerStats(player);
        if (!CanHaveMinion(playerStats, OwnedMinionFamily.Other))
        {
            if (ReferenceEquals(player.SpiritChieftains.Pending, summon)) player.SpiritChieftains.Pending = null;
            return;
        }
        int Player(uint k) => playerStats.GetValueOrDefault(k);
        // shortcut: the player's minion modifiers are taken at the summon, not followed for its 12 s.
        var mods = MonsterModSet.None with { Effects = new(new Dictionary<uint, int>
        {
            [Stat.DamagePlusPct] = Player(Stat.MinionDamagePlusPct),
            [Stat.AttackSpeedPlusPct] = Player(Stat.MinionAttackSpeedPlusPct) + Player(Stat.MinionAttackAndCastSpeedPlusPct),
            [Stat.BaseMovementVelocityPlusPct] = Player(Stat.MinionMovementSpeedPlusPct),
        }) };
        (int X, int Y) at;
        at = NextToPlayer(player);
        await SpawnMonster(player, variety, summon.Level, MonsterRarity.Normal, at.X, at.Y, friendly: true, mods: mods,
            extraStats: MinionStats, owned: (id, m) =>
            {
                if (ReferenceEquals(player.SpiritChieftains.Pending, summon)) player.SpiritChieftains.Pending = null;
                m.Aggro = true;
                player.Minions.Add(new(id, m, summon.Skill, variety.Name)
                    { Until = summon.Until, Fights = true, SpiritChieftain = true });
            }, ownedCurrent: () => player.PlayerLife > 0 && ReferenceEquals(player.SpiritChieftains.Pending, summon)
                && AreaTime(player) < summon.Until && CanHaveOtherMinions(player)
                && DarkMonarchAllows("SpiritChieftain", StatSourceValues(player)));
        if (!CurrentGameWork) return;
        Console.WriteLine($"[gateway] {CharacterName(player)}: {summon.Skill} summons {variety.Name} at level {summon.Level} for "
            + $"{summon.Until - now:0.##} s");
    }
}
