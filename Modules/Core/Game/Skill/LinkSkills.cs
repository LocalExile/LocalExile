using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    // number_of_active_links: one per link (each source buff's condition stat). The client's derived aura effect
    // (0x142829960) and PassiveBonus add non_curse_aura_effect_+%_while_linked (Absolution) only while it is nonzero.
    static Dictionary<uint, int> WithLinks(Player player, Dictionary<uint, int> sources)
    {
        int count = player.Links.Count;
        if (count > 0 && Stat.NumberOfActiveLinks is var key) sources[key] = sources.GetValueOrDefault(key) + count;
        if (count >= 2 && sources.GetValueOrDefault(Stat.MovementSpeedPlusPctWhileYouHaveTwoLinkedTargets) is int movement and not 0)
            sources[Stat.BaseMovementVelocityPlusPct] = ActorScale.Saturate((long)sources.GetValueOrDefault(Stat.BaseMovementVelocityPlusPct) + movement);
        if (count >= 4 && sources.GetValueOrDefault(Stat.DefencesPlusPctWhileYouHaveFourLinkedTargets) is int defences and not 0)
            sources[Stat.GlobalDefencesPlusPct] = ActorScale.Saturate((long)sources.GetValueOrDefault(Stat.GlobalDefencesPlusPct) + defences);
        return sources;
    }

    // A link gem's cast (CastGemSkill): the link starts at the end of its cast time, as a spell's effect lands, unless the
    // cast was interrupted (another action, death).
    static async Task<bool> CastLink(Player player,
        ushort skillId, int effect, uint targetId, Func<bool> current)
    {
        if (CastMs(player, effect, skillId) is not int ms) return false;
        await AreaDelay(player, ms / 1000.0);
        await WhilePaused(player);
        return current() && await StartLink(player, skillId, targetId);
    }

    /// <summary>
    /// Links the skill to its target: the aimed object when it qualifies, else the nearest that does, one not linked yet
    /// first (the client's candidate order, 0x141bfab10). A link skill has one target and a target one link of the
    /// player's, so the new link replaces both. It lasts <c>base_skill_effect_duration</c> at the effective gem level
    /// (Deep Connections' +5 included) with the skill duration modifiers and <c>link_skill_duration_+%</c>, and costs the
    /// skill's <c>mana_cost_per_minute</c> while it lasts. False when nothing qualifies.
    /// </summary>
    public static async Task<bool> StartLink(Player player,
        ushort skillId, uint targetId)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea) return false;
        var gem = SupportedSkill(player, skillId);
        string? effectId = SkillsFor(player).EffectId(gem.Effect);
        if (effectId is null || !LinkSkills.Buffs.TryGetValue(effectId, out var rows)) return false;
        var playerStats = CharacterPlayerStats(player);
        int Player(uint k) => playerStats.GetValueOrDefault(k);
        int Own(uint k) => gem.Stats.GetValueOrDefault(k);
        double seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Player,
            name => Own(name) + (name == SkillDurationModifiers.IncreasedDuration ? Player(Stat.LinkSkillDurationPlusPct) : 0));
        var bytes = new List<byte>();
        LinkSkills.Link link;
        double now = AreaTime(player);
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0) return false;
        if (LinkTargetLocked(player, targetId, Player, now) is not uint target)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {effectId}: no link target (an allied player, or a damageable minion with link_skills_can_target_minions, within {LinkSkills.Range(Player(Stat.LinkSkillRangePlusPct))} cells)");
            return false;
        }
        if (player.Links.Count == 0) player.Links.PaidAt = now; // the cost runs from the first link
        foreach (var old in player.Links.All.Where(l => l.SkillId == skillId || l.Target == target).ToArray())
            EndLinkLocked(player, old, bytes);
        bool paysLife = Lifetap.ConvertsCostToLife(gem.Stats, playerStats);
        link = new(target, skillId, effectId)
        {
            SkillStats = gem.Stats,
            BreakGraceSeconds = Player(Stat.LinkGracePeriod8SecondOverride) != 0 ? 8 : LinkSkills.BreakSeconds,
            Until = now + seconds, ManaPerMinute = paysLife ? 0 : Own(Stat.ManaCostPerMinute),
            LifePerMinute = paysLife ? Own(Stat.ManaCostPerMinute) : 0, LifePaidAt = now,
            SourceBuff = player.NextBuff++, TargetBuff = player.NextBuff++, SourceRow = rows.Source, TargetRow = rows.Target,
        };
        player.Links.Add(link);
        if (RandomRolls.Chance(Player(Stat.ChanceToStartEnergyShieldRechargePctOnLinkingTarget), 100, AreaRng(player)))
            player.EnergyShield.StartRecharge(now, Player);
        if (effectId == "IntuitiveLink") CaptureIntuitiveLink(player, link, gem);
        player.Area.Buffs.Set(player.Id, new(rows.Source, player.Id, skillId, U68: Buff.Seconds(seconds), Tag: link.SourceBuff));
        player.Area.Buffs.Set(target, new(rows.Target, player.Id, skillId, U68: Buff.Seconds(seconds), Tag: link.TargetBuff));
        if (!SkillsFor(player).IsInstant(gem.Effect))
            RecordNonInstantSpellUseLocked(player, gem.Effect, now);
        LinksChangedLocked(player);
        await Send(player, [.. bytes], $"0x01c1 {effectId} links 0x{link.Target:x} for {seconds:0.##} s at level {gem.Level}, {(link.ManaPerMinute + link.LifePerMinute) / 60.0:0.#} {(link.LifePerMinute > 0 ? "life" : "mana")} per second");
        await SendCharacterStats(player, PlayerStatChange.Sources);
        return true;
    }

    // The target a link cast takes. It qualifies as in the client's filter (0x141bfaf20): not the player, within the link
    // range, and, as there is no party play, one of his living minions while he has link_skills_can_target_minions
    // (they carry is_player_minion and is_semantic_minion, and none has cannot_be_damaged).
    static uint? LinkTargetLocked(Player player, uint aimed, Func<uint, int> playerStats, double now)
    {
        if (playerStats(Stat.LinkSkillsCanTargetMinions) == 0) return null;
        var here = player.Walk.Position(now);
        int range = LinkSkills.Range(playerStats(Stat.LinkSkillRangePlusPct));
        var valid = player.Minions.All.Where(m => LinkTargetAliveLocked(player, m)
            && !m.Monster.Invulnerable && m.Monster.Effects[Stat.BaseCannotBeDamaged] == 0
            && (OwnedFighter(player, m.Id) is not { } fighter || !GolemIgnoresDots(fighter))
            && Dist(m.Monster.Walk.Position(now), here) <= range).ToList();
        if (valid.FirstOrDefault(m => m.Id == aimed) is { } chosen) return chosen.Id;
        return valid.OrderBy(m => player.Links.On(m.Id) is null ? 0 : 1).ThenBy(m => Dist(m.Monster.Walk.Position(now), here))
            .Select(m => (uint?)m.Id).FirstOrDefault();
    }

    static bool LinkTargetAliveLocked(Player player, uint id) =>
        player.Minions.All.FirstOrDefault(m => m.Id == id) is { } minion && LinkTargetAliveLocked(player, minion);

    static bool LinkTargetAliveLocked(Player player, PlayerMinions.Minion minion) =>
        player.Area.Monsters.TryGetValue(minion.Id, out var held)
        && ReferenceEquals(held, minion.Monster) && !held.Dead && held.Life > 0;

    static LinkSkills.Link? ActiveMinionLink(Player player, GolemState.Golem fighter, double now) =>
        player.PlayerLife > 0 && player.Links.On(fighter.Id) is { } link && now < link.Until
        && (link.AwaySince is not double away || now - away < link.BreakGraceSeconds) ? link : null;

    public static void GrantLinkedMinionRareModifiers(KillContext kill)
    {
        var player = kill.Player;
        var monster = kill.Monster;
        if (player.Links.Count == 0 || player.PlayerLife == 0 || monster.Rarity != MonsterRarity.Rare
            || !monster.Mods.Affixes.Any() || !KilledByPlayerOrOwned(kill)) return;
        var stats = CharacterPlayerStats(player);
        int centiseconds = stats.GetValueOrDefault(Stat.LinkSkillsGrantRandomLinkedMinionRareMonsterModsOnKillCentiseconds);
        if (centiseconds <= 0) return;
        double now = AreaTime(player);
        var recipients = player.Links.All.Select(link => OwnedFighter(player, link.Target))
            .OfType<GolemState.Golem>().Where(minion => LinkTargetAliveLocked(player, minion.Id)
                && ActiveMinionLink(player, minion, now) is not null).ToArray();
        if (recipients.Length == 0) return;
        var target = recipients[AreaRng(player).Next(recipients.Length)];
        (target.LinkedModifiers ??= new()).Steal(monster.Mods, ItemsFor(player).Mods, now, centiseconds / 100d);
        RefreshGolemLocked(player, target, stats, now);
    }

    static void ReconcileLinkedMinionModifiersLocked(Player player, GolemState.Golem fighter, double now, bool dead = false)
    {
        if (fighter.LinkedModifiers is not { } modifiers) return;
        if (modifiers.Step(now, dead, out var buff, out _) is null && modifiers.Count > 0) return;
        fighter.InputHash = 0;
        if (buff.Count > 0)
            player.Area.Buffs.Set(fighter.Id, new(StolenMods.BuffRow, player.Id, U68: Buff.Seconds(buff.Remaining),
                Stacks: (ushort)Math.Min(ushort.MaxValue, buff.Count)));
        else
        {
            fighter.LinkedModifiers = null;
            player.Area.Buffs.Clear(fighter.Id, StolenMods.BuffRow, player.Id);
        }
    }

    static long LinkBuffPercent(GolemState.Golem fighter, IReadOnlyDictionary<uint, int> casterStats) =>
        Math.Max(0, 100L + casterStats.GetValueOrDefault(Stat.LinkSkillBuffEffectPlusPct)
            + fighter.InheritedMods.Effects[Stat.LinkBuffEffectOnSelfPlusPct]
            + (fighter.Definition.Kind == "AnimatedGuardian" ? casterStats.GetValueOrDefault(Stat.LinkBuffEffectPlusPctOnAnimateGuardian) : 0));

    static int ScaledLinkStat(LinkSkills.Link link, uint stat, long effect) =>
        ActorScale.Saturate(link.SkillStats.GetValueOrDefault(stat) * effect / 100);

    static double SoulLinkDamageTaken(LinkSkills.Link link, long effect) =>
        Math.Max(0, 1 + ScaledLinkStat(link, Stat.SoulLinkGrantsDamageTakenPlusPctFinal, effect) / 100d);

    static double HallowedMonarchDamageTaken(LinkSkills.Link? link, IReadOnlyDictionary<uint, int> stats) =>
        link is null ? 1 : Math.Max(0, 1 + stats.GetValueOrDefault(
            Stat.LinkSkillsGrantMinionsDamageTakenPlusPctFinalFromHallowedMonarch) / 100d);

    internal static double MinionLinkDamageTaken(Player player, GolemState.Golem fighter, double now)
    {
        if (ActiveMinionLink(player, fighter, now) is not { } link) return 1;
        var stats = PlayerStatValues(player);
        double damageTaken = HallowedMonarchDamageTaken(link, stats);
        return link.EffectId == "SoulLink" ? damageTaken * SoulLinkDamageTaken(link, LinkBuffPercent(fighter, stats)) : damageTaken;
    }

    readonly record struct MinionLinkOffence(int FireMin = 0, int FireMax = 0, int Ignite = 0,
        int? MainHandCrit = null, int CritMultiplier = 0, int Accuracy = 0);

    static MinionLinkOffence MinionLinkOffenceAt(Player player, GolemState.Golem fighter,
        IReadOnlyDictionary<uint, int> casterStats, double now)
    {
        if (ActiveMinionLink(player, fighter, now) is not { } link) return default;
        long effect = LinkBuffPercent(fighter, casterStats);
        if (link.EffectId == "DestructiveLink")
            return new(MainHandCrit: player.Pools.AttackCriticalStrikeChance,
                CritMultiplier: ScaledLinkStat(link, Stat.CriticalLinkGrantsBaseCriticalStrikeMultiplierPlus, effect),
                Accuracy: ScaledLinkStat(link, Stat.CriticalLinkGrantsAccuracyRatingPlusPct, effect));
        if (link.EffectId != "FlameLink") return default;
        long fromLife = (long)player.PlayerMaxLife * link.SkillStats.GetValueOrDefault(Stat.FlameLinkAddedFireDamageFromLifePct) / 100;
        int Fire(uint stat) => ActorScale.Saturate((fromLife + link.SkillStats.GetValueOrDefault(stat)) * effect / 100);
        return new(Fire(Stat.FlameLinkMinimumFireDamage), Fire(Stat.FlameLinkMaximumFireDamage),
            ScaledLinkStat(link, Stat.FlameLinkGrantsChanceToIgnitePct, effect));
    }

    // A link ends: both its buffs come off (the target's only while its object is there).
    static void EndLinkLocked(Player player, LinkSkills.Link link, List<byte> bytes)
    {
        player.Links.Remove(link.Target);
        player.Area.Buffs.Clear(player.Id, link.SourceRow, player.Id, tag: link.SourceBuff);
        player.Area.Buffs.Clear(link.Target, link.TargetRow, player.Id, tag: link.TargetBuff);
    }

    // The link count changed: Absolution's aura effect follows it, so the pools and the auras' stats are worked out again.
    static void LinksChangedLocked(Player player)
    {
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        player.AuraStatsDirty = true;
    }

    /// <summary>
    /// One step of the player's links at game time <paramref name="now"/> (StartPlayerMinions, after the minions' step).
    /// The player's death ends them all. A linked target that died or ended (a minion's duration running out counts, wiki
    /// Link skill) kills the player through the normal death path, which in Hardcore is a real death. Otherwise the cost
    /// since the last step is paid from mana (every link's <c>mana_cost_per_minute</c>); when it cannot be, all links
    /// break. Then a link ends with its duration, or after 4 s with its target out of range. Field Medicine's flask buffs on
    /// the targets end with their duration.
    /// </summary>
    public static async Task LinkSkillsTick(Player player, double now)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
        if (player.Links.Count == 0 && player.Links.Flasks.Count == 0) { player.Links.PaidAt = now; return; }
        var playerStats = CharacterPlayerStats(player);
        var bytes = new List<byte>();
        var ended = new List<string>();
        bool died = false, paid = false;
        uint lifePaid = 0;
        if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
        var links = player.Links;
        var spent = links.Flasks.Where(f => now >= f.Until || !LinkTargetAliveLocked(player, f.Target)).ToArray();
        foreach (var flask in spent)
        {
            links.Flasks.Remove(flask);
            if (!player.Area.Monsters.ContainsKey(flask.Target)) continue;
            player.Area.Buffs.Clear(flask.Target, flask.Row, player.Id, tag: flask.Buff);
            if (flask.Extra is ushort extra) player.Area.Buffs.Clear(flask.Target, flask.ExtraRow, player.Id, tag: extra);
        }
        foreach (uint target in spent.Select(f => f.Target).Distinct())
            if (OwnedFighter(player, target) is { } minion) RefreshMinionFlaskStatsLocked(player, minion, now);
        // Every target is a minion (no party play), so without link_skills_can_target_minions (Deep Connections refunded)
        // every link ends, without a death: our rule, as for an Elemental Valiance refund (paladin.md).
        string? all = player.PlayerLife == 0 ? "the player died"
            : !(Stat.LinkSkillsCanTargetMinions is var can) || playerStats.GetValueOrDefault(can) == 0
                ? "link_skills_can_target_minions is gone" : null;
        if (all is null && links.All.FirstOrDefault(l => !LinkTargetAliveLocked(player, l.Target)) is { } lost)
        {
            all = $"its target 0x{lost.Target:x} died";
            died = true;
        }
        double elapsed = Math.Max(0, now - links.PaidAt);
        bool outOfMana = false;
        if (all is null && links.Count > 0 && playerStats.GetValueOrDefault(Stat.NoManaCost) == 0)
        {
            double due = links.ManaCarry + links.All.Sum(l => l.ManaPerMinute) / 60 * elapsed;
            uint whole = (uint)Math.Min(uint.MaxValue, Math.Floor(due));
            if (player.PlayerMana < whole) outOfMana = true;
            else (player.PlayerMana, links.ManaCarry, paid) = (player.PlayerMana - whole, due - whole, whole > 0);
        }
        links.PaidAt = now;
        var here = player.Walk.Position(now);
        int range = LinkSkills.Range(Stat.LinkSkillRangePlusPct is var r ? playerStats.GetValueOrDefault(r) : 0);
        foreach (var link in links.All.ToArray())
        {
            string? why = all ?? (outOfMana && link.ManaPerMinute > 0 ? "not enough mana" : null);
            if (why is null && link.LifePerMinute > 0)
            {
                double seconds = Math.Max(0, Math.Min(now, link.Until) - link.LifePaidAt);
                uint payment = Lifetap.PayOngoingLife(player.PlayerLife, link.LifePerMinute * seconds / 60,
                    ref link.LifeCarry, out bool exhausted);
                player.PlayerLife -= payment;
                if (payment > 0) player.RecentEvents.Record(StatConditions.SpentLife, now);
                lifePaid += payment;
                link.LifePaidAt = now;
                if (exhausted) why = "not enough life";
            }
            if (why is null && now >= link.Until) why = "its duration ended";
            else if (why is null)
            {
                bool away = Dist(player.Area.Monsters[link.Target].Walk.Position(now), here) > range;
                link.AwaySince = away ? link.AwaySince ?? now : null;
                if (away && now - link.AwaySince >= link.BreakGraceSeconds)
                    why = $"out of range ({range} cells) for {link.BreakGraceSeconds:0.##} s";
            }
            if (why is null)
            {
                ApplyFrigidBondLocked(player, link, here, playerStats, now);
                continue;
            }
            EndLinkLocked(player, link, bytes);
            ended.Add($"{link.EffectId} on 0x{link.Target:x}: {why}");
        }
        if (died)
        {
            links.ManaCarry = 0;
            player.PlayerLife = 0;
            player.EnergyShield.Empty();
        }
        if (ended.Count > 0) LinksChangedLocked(player);
        if (bytes.Count > 0) await Send(player, [.. bytes], $"0x01c2 links end: {string.Join("; ", ended)}");
        if (died) await PlayerDied(player, $"0x0055 death screen ({player.Character.League ?? "?"} league, a linked target died)");
        else if (paid || lifePaid > 0) await SendSkillCost(player, lifePaid, null);
        if (ended.Count > 0) await SendCharacterStats(player, PlayerStatChange.Sources);
    }

    // Field Medicine (flasks_apply_to_your_linked_targets): a non-unique utility flask the player uses puts its effect on
    // each linked target for the flask's duration (Path of Building's nonUniqueFlasksApplyToMinion): its buff, the Silver
    // Flask's Onslaught too, and its stats at the flask's own increased effect but without the player's "applied to you"
    // ones (flask_effect_+%, magic_utility_flask_effect_+%; PoB's effectIncNonPlayer).
    static void FlaskToLinkedTargetsLocked(Player player,
        Item flask, IReadOnlyDictionary<uint, int> local, UtilityFlasks.Utility utility, Func<uint, int> character)
    {
        if (flask.Rarity == ItemRarity.Unique || character(Stat.FlasksApplyToYourLinkedTargets) == 0) return;
        var (stats, seconds) = ItemUtilityFlaskFor(utility, flask, local,
            key => key is Stat.FlaskEffectPlusPct or Stat.MagicUtilityFlaskEffectPlusPct ? 0 : character(key),
            finalMultiplier: MapFlaskEffectMultiplier(player));
        uint duration = BitConverter.SingleToUInt32Bits((float)seconds);
        double now = AreaTime(player);
        foreach (var link in player.Links.All.ToArray())
        {
            if (OwnedFighter(player, link.Target) is not { } minion || !LinkTargetAliveLocked(player, link.Target)
                || ActiveMinionLink(player, minion, now) is null) continue;
            ushort buff = player.NextBuff++;
            ushort? extra = utility.Extra is null ? null : player.NextBuff++;
            player.Links.Flasks.Add(new(link.Target, buff, extra, now + seconds, stats, utility.BuffRow, utility.Extra?.BuffRow ?? 0));
            player.Area.Buffs.Set(link.Target, new(utility.BuffRow, player.Id, U68: duration, Tag: buff));
            if (utility.Extra is { } x && extra is ushort e)
                player.Area.Buffs.Set(link.Target, new(x.BuffRow, player.Id, U68: duration, Tag: e));
            RefreshMinionFlaskStatsLocked(player, minion, now);
        }
    }
}
