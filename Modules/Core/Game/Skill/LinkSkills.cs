using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // number_of_active_links: one per link (each source buff's condition stat). The client's derived aura effect
    // (0x142829960) and PassiveBonus add non_curse_aura_effect_+%_while_linked (Absolution) only while it is nonzero.
    static Dictionary<uint, int> WithLinks(Player player, Dictionary<uint, int> sources)
    {
        int count;
        count = player.Links.Count;
        if (count > 0 && Stat.NumberOfActiveLinks is var key) sources[key] = sources.GetValueOrDefault(key) + count;
        return sources;
    }

    // A link gem's cast (CastGemSkill): the link starts at the end of its cast time, as a spell's effect lands, unless the
    // cast was interrupted (another action, death).
    static async Task<bool> CastLink(Player player,
        ushort skillId, int effect, uint targetId, Func<bool> current)
    {
        if (CastMs(player, effect, skillId) is not int ms) return false;
        await Task.Delay(ms);
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
        link = new(target, skillId, effectId)
        {
            Until = now + seconds, ManaPerMinute = Own(Stat.ManaCostPerMinute),
            SourceBuff = player.NextBuff++, TargetBuff = player.NextBuff++, SourceRow = rows.Source, TargetRow = rows.Target,
        };
        player.Links.Add(link);
        player.Area.Buffs.Set(player.Id, new(rows.Source, player.Id, skillId, U68: Buff.Seconds(seconds), Tag: link.SourceBuff));
        player.Area.Buffs.Set(target, new(rows.Target, player.Id, skillId, U68: Buff.Seconds(seconds), Tag: link.TargetBuff));
        LinksChangedLocked(player);
        await Send(player, [.. bytes], $"0x01c1 {effectId} links 0x{link.Target:x} for {seconds:0.##} s at level {gem.Level}, {link.ManaPerMinute / 60.0:0.#} mana per second");
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
        var valid = player.Minions.All.Where(m => LinkTargetAliveLocked(player, m.Id)
            && Dist(m.Monster.Walk.Position(now), here) <= range).ToList();
        if (valid.FirstOrDefault(m => m.Id == aimed) is { } chosen) return chosen.Id;
        return valid.OrderBy(m => player.Links.On(m.Id) is null ? 0 : 1).ThenBy(m => Dist(m.Monster.Walk.Position(now), here))
            .Select(m => (uint?)m.Id).FirstOrDefault();
    }

    static bool LinkTargetAliveLocked(Player player, uint id) =>
        player.Minions.All.FirstOrDefault(m => m.Id == id) is { } minion && player.Area.Monsters.TryGetValue(id, out var held)
        && ReferenceEquals(held, minion.Monster) && !held.Dead && held.Life > 0;

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
        foreach (uint target in spent.Select(f => f.Target).Distinct()) MinionFlaskSpeedLocked(player, target, now, bytes);
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
        if (all is null && links.Count > 0)
        {
            double due = links.ManaCarry + links.All.Sum(l => l.ManaPerMinute) / 60 * Math.Max(0, now - links.PaidAt);
            uint whole = (uint)Math.Min(uint.MaxValue, Math.Floor(due));
            if (player.PlayerMana < whole) all = $"not enough mana for the cost ({player.PlayerMana} < {whole})";
            else (player.PlayerMana, links.ManaCarry, paid) = (player.PlayerMana - whole, due - whole, whole > 0);
        }
        links.PaidAt = now;
        var here = player.Walk.Position(now);
        int range = LinkSkills.Range(Stat.LinkSkillRangePlusPct is var r ? playerStats.GetValueOrDefault(r) : 0);
        foreach (var link in links.All.ToArray())
        {
            string? why = all;
            if (why is null && now >= link.Until) why = "its duration ended";
            else if (why is null)
            {
                bool away = Dist(player.Area.Monsters[link.Target].Walk.Position(now), here) > range;
                link.AwaySince = away ? link.AwaySince ?? now : null;
                if (away && now - link.AwaySince >= LinkSkills.BreakSeconds) why = $"out of range ({range} cells) for 4 s";
            }
            if (why is null) continue;
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
        else if (paid) await SendPlayerMana(player, null);
        if (ended.Count > 0) await SendCharacterStats(player, PlayerStatChange.Sources);
    }

    // Field Medicine (flasks_apply_to_your_linked_targets): a non-unique utility flask the player uses puts its effect on
    // each linked target for the flask's duration (Path of Building's nonUniqueFlasksApplyToMinion): its buff, the Silver
    // Flask's Onslaught too, and its stats at the flask's own increased effect but without the player's "applied to you"
    // ones (flask_effect_+%, magic_utility_flask_effect_+%; PoB's effectIncNonPlayer). A minion target walks at their
    // movement speed (MinionFlaskSpeedLocked); their other stats have nothing to act on (paladin.md).
    static void FlaskToLinkedTargetsLocked(Player player,
        Item flask, IReadOnlyDictionary<uint, int> local, UtilityFlasks.Utility utility, Func<uint, int> character)
    {
        if (flask.Rarity == ItemRarity.Unique || character(Stat.FlasksApplyToYourLinkedTargets) == 0) return;
        var (stats, seconds) = ItemUtilityFlaskFor(utility, flask, local,
            name => name is Stat.FlaskEffectPlusPct or Stat.MagicUtilityFlaskEffectPlusPct ? 0 : character(name));
        uint duration = BitConverter.SingleToUInt32Bits((float)seconds);
        var bytes = new List<byte>();
        double now = AreaTime(player);
        foreach (var link in player.Links.All.Where(l => LinkTargetAliveLocked(player, l.Target)).ToArray())
        {
            ushort buff = player.NextBuff++;
            ushort? extra = utility.Extra is null ? null : player.NextBuff++;
            player.Links.Flasks.Add(new(link.Target, buff, extra, now + seconds, stats, utility.BuffRow, utility.Extra?.BuffRow ?? 0));
            player.Area.Buffs.Set(link.Target, new(utility.BuffRow, player.Id, U68: duration, Tag: buff));
            if (utility.Extra is { } x && extra is ushort e)
                player.Area.Buffs.Set(link.Target, new(x.BuffRow, player.Id, U68: duration, Tag: e));
            MinionFlaskSpeedLocked(player, link.Target, now, bytes);
        }
        if (bytes.Count > 0) Automation(player).Packets.Add(new([.. bytes], $"0x01c1 {utility.BuffId} on the linked targets for {seconds:0.##} s"));
    }

    // A minion's walk with the player's flasks on it (Field Medicine): its variety's speed × (1 + its mods' and the
    // flasks' base_movement_velocity_+%, the strongest of each flask effect), in the server's walk and, as
    // movement_velocity_+% (0xf5), in the client's, which walks every mover at its base × (1 + 0xf5 / 100) (0x141e827a0).
    static void MinionFlaskSpeedLocked(Player player, uint target, double now, List<byte> bytes)
    {
        if (!player.Area.Monsters.TryGetValue(target, out var m) || m.Dead || m.Variety is not { } variety) return;
        int pct = m.Mods.Effects.MovementPct + UtilityFlasks.Strongest(player.Links.Flasks.Where(f => f.Target == target)
            .SelectMany(f => f.Stats)).Where(s => s.Stat == Stat.BaseMovementVelocityPlusPct).Sum(s => s.Value);
        m.Walk.SetSpeed(Math.Max(1, variety.MovementSpeed * Math.Max(0, 100 + pct) / 100.0), now);
        SetStats(player, target, [(Stat.MovementVelocityPlusPct, pct)]);
    }
}
