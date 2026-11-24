using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    sealed class DamageTakenTriggers(int epoch, object? rules)
    {
        public readonly int Epoch = epoch;
        public readonly object? Rules = rules;
        public readonly Dictionary<ushort, TriggerCounter> Counters = [];
        public readonly Queue<(ushort Skill, TriggerCounter Counter, (int X, int Y) Aim, double At)> Pending = [];
    }
    sealed class TriggerCounter(SupportedGem gem)
    {
        public readonly SupportedGem Gem = gem;
        public long Damage;
        public double ReadyAt;
        public bool Pending;
    }
    static readonly Store<Player, DamageTakenTriggers> DamageTriggers = new();
    static readonly Store<Player, DamageTakenTriggers> StunTriggers = new();

    static bool IncomingHitTriggersPending(Player player) =>
        (DamageTriggers.TryGetValue(player, out var damage) && damage.Pending.Count != 0)
        || (StunTriggers.TryGetValue(player, out var stun) && stun.Pending.Count != 0);

    static void ClearIncomingHitTriggers(Player player)
    {
        DamageTriggers.Remove(player);
        StunTriggers.Remove(player);
    }

    static GemSupports.Applied? IncomingTriggerSupport(SupportedGem gem, bool stunned) =>
        gem.Supports.FirstOrDefault(s => stunned ? SupportHas(s, Stat.CastOnStunnedPct)
            : s.Effect.Id == "SupportCastWhenDamageTaken");

    public static string? TriggerOnlyReason(SupportedGem gem)
    {
        if (GolemStat(gem.Stats, Stat.CastOnLoseCatsStealth) != 0)
            return "this skill can only trigger when Cat's Stealth is lost";
        if (gem.Supports.Any(s => s.Effect.Id == "SupportCastWhenDamageTaken"))
            return "this skill can only trigger when damage is taken";
        if (gem.Supports.Any(s => SupportHas(s, Stat.CastOnStunnedPct)))
            return "this skill can only trigger when stunned";
        if (gem.Stats.GetValueOrDefault(Stat.SupportManaforgedArrowsManaCostPctThreshold) > 0)
            return "this skill can only trigger after spending Mana on another bow attack";
        if (GolemStat(gem.Stats, Stat.CastSpellOnLinkedAttackCrit) != 0)
            return "this skill can only trigger on a linked attack critical strike";
        if (GolemStat(gem.Stats, Stat.CastSpellOnLinkedMeleeKill) != 0)
            return "this skill can only trigger on a linked melee attack's kill";
        if (GolemStat(gem.Stats, Stat.CastSpellWhileLinkedSkillChannelling) != 0)
            return "this skill can only trigger while a linked skill is channelled";
        if (GolemStat(gem.Stats, Stat.SkillTriggeredBySnipe) != 0)
            return "this skill can only be triggered by Snipe";
        if (GolemStat(gem.Stats, Stat.TriggerOnSkillUsePctIfYouHaveASpiritCharge) > 0)
            return "Spirit Burst triggers on skill use while a Spirit Charge is held";
        if (gem.Effect >= 0 && IsRelicSkill(Skills.EffectId(gem.Effect) ?? ""))
            return "Summon Elemental Relic triggers automatically; manual use is unavailable";
        if (gem.Supports.Any(s => s.Effect.Id == "SupportTriggerElementalSpellOnBlock"))
            return "this skill triggers automatically when you block";
        if (GolemStat(gem.Stats, Stat.SpellUncastableIfTriggerable) != 0
            || gem.Supports.Any(s => s.Effect.Added.Contains(36)))
            return "this trigger has no server executor; manual use is unavailable";
        return null;
    }

    sealed class OnBlockTriggerState(int epoch, object? rules)
    {
        public readonly int Epoch = epoch;
        public readonly object? Rules = rules;
        public double ReadyAt;
        public int NextIndex;
    }
    static readonly Store<Player, OnBlockTriggerState> OnBlockTriggers = new();

    static double BlockTriggerCooldownRate(IReadOnlyDictionary<uint, int> playerStats, SupportedGem gem) => Math.Max(0.01,
        1 + (GolemStat(playerStats, Stat.VirtualCooldownSpeedPlusPct)
            + GolemStat(playerStats, Stat.BaseSpellCooldownSpeedPlusPct)
            + GolemStat(gem.Stats, Stat.BaseSpellCooldownSpeedPlusPct)
            + GolemStat(gem.Stats, Stat.BaseCooldownSpeedPlusPct)
            + GolemStat(gem.Stats, Stat.VirtualCooldownSpeedPlusPct)) / 100.0);

    /// <summary>Svalinn ("Trigger a Socketed Elemental Spell when you Block, with a 0.25 second Cooldown"): triggers the
    /// next eligible socketed elemental spell supported by <c>SupportTriggerElementalSpellOnBlock</c>.</summary>
    public static void TriggerOnBlockSpells(Player player, uint attackerId)
    {
        if (!CurrentGameRules || player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0 || InTown(player)) return;
        var candidates = GemSkillEffects(player)
            .Where(s => SupportedSkill(player, s.Id) is var gem
                && gem.Supports.Any(sup => sup.Effect.Id == "SupportTriggerElementalSpellOnBlock")
                && TriggeredSpellExecutable(player, s.Id, gem))
            .ToList();
        if (candidates.Count == 0) return;
        double now = AreaTime(player);
        if (!OnBlockTriggers.TryGetValue(player, out var state) || state.Epoch != player.ProjectileEpoch
            || !ReferenceEquals(state.Rules, CombatLifetime))
        {
            OnBlockTriggers.Remove(player);
            OnBlockTriggers.Add(player, state = new(player.ProjectileEpoch, CombatLifetime));
        }
        if (now < state.ReadyAt) return;

        var origin = player.Walk.Position(now);
        var aim = player.Area.Monsters.TryGetValue(attackerId, out var attacker) && attacker.Hostile && !attacker.Dead && attacker.Life > 0
            ? attacker.Walk.Position(now)
            : player.Area.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0)
                .OrderBy(e => Dist(origin, e.Value.Walk.Position(now))).ThenBy(e => e.Key)
                .Select(e => e.Value.Walk.Position(now)).FirstOrDefault((origin.X + 20, origin.Y));

        for (int i = 0; i < candidates.Count; i++)
        {
            int idx = (state.NextIndex + i) % candidates.Count;
            var spell = candidates[idx];
            var gem = SupportedSkill(player, spell.Id);
            if (GemSkillInstanceDisabled(player, gem.Effect, spell.Id, triggered: true) is not null) continue;
            if (now < LinkedTriggersOf(player).ReadyAt.GetValueOrDefault(gem.Effect)) continue;

            var sup = gem.Supports.FirstOrDefault(s => s.Effect.Id == "SupportTriggerElementalSpellOnBlock");
            int cdMs = Math.Max(SkillsFor(player).CooldownMs(gem.Effect, gem.Level),
                sup is null ? 0 : SkillsFor(player).CooldownMs(sup.Gem.Effect, sup.Gem.Level));
            if (cdMs <= 0) cdMs = 250;
            var playerStats = CharacterPlayerStats(player);
            double rate = BlockTriggerCooldownRate(playerStats, gem);

            var session = ProjectilesFor(player);
            bool Current() => state.Epoch == player.ProjectileEpoch && CurrentGameRules
                && ReferenceEquals(state.Rules, CombatLifetime) && session.Active && !player.Paused
                && player.PlayerLife > 0 && !player.LeavingArea && player.PendingTransfer is null;

            var release = PrepareTriggeredPayload(player, session, spell.Id, aim, Current);
            if (release is null) continue;

            if (!PayMana(player, spell.Id, out uint cost, out uint lifeCost))
            {
                state.NextIndex = (idx + 1) % candidates.Count;
                return;
            }

            double firedAt = AreaTime(player);
            state.ReadyAt = firedAt + (cdMs / 1000.0) / rate;
            LinkedTriggersOf(player).ReadyAt[gem.Effect] = state.ReadyAt;
            state.NextIndex = (idx + 1) % candidates.Count;
            release();
            if (cost > 0 || lifeCost > 0)
                session.Enqueue(() => SendSkillCost(player, lifeCost, "trigger on block cost"));
            return;
        }
    }
    static bool SameTriggerSource(SupportedGem a, SupportedGem b) => a.Effect == b.Effect && a.Level == b.Level
        && a.Quality == b.Quality && a.Host == b.Host && a.Group == b.Group && (a.SourceGem is null ? b.SourceGem is null
            : b.SourceGem is not null && ReferenceEquals(PlayerInventories.GemIdentity(a.SourceGem), PlayerInventories.GemIdentity(b.SourceGem)))
        && a.Supports.Select(s => s.Gem).SequenceEqual(b.Supports.Select(s => s.Gem));

    // Every payload needs an independent executor; unsupported mechanics stay unavailable.
    static bool TriggeredSpellExecutable(Player player, ushort id, SupportedGem gem) => SkillsFor(player).IsSpell(gem.Effect)
        && !SkillsFor(player).IsAttack(gem.Effect) && !SkillsFor(player).IsTravel(gem.Effect)
        && Supports.Of(gem.Effect)?.Types.Contains(47) != true && PlacedKind(gem) is (false, false)
        && gem.Supports.Count(s => s.Effect.Added.Contains(36)) == 1
        && SupportedReservation(player, id) is null && SkillsFor(player).VaalBudget(gem.Effect, gem.Level) is null
        && TriggerPayloadAvailable(player, gem);

    // Called only by accepted hit damage, before Ward/ES/Mana absorb it. DoT and costs never enter this hook.
    public static void RecordDamageTakenTriggers(Player player, int damage, double at) =>
        RecordIncomingHitTriggers(player, damage, at, stunned: false);

    static void RecordIncomingHitTriggers(Player player, int damage, double at, bool stunned)
    {
        var triggers = stunned ? StunTriggers : DamageTriggers;
        if (!CurrentGameRules || player.Closed || player.LeavingArea || player.PendingTransfer is not null
            || player.PlayerLife == 0) { triggers.Remove(player); return; }
        if (player.Paused || (!stunned && damage <= 0) || InTown(player)) return;
        if (!triggers.TryGetValue(player, out var state) || state.Epoch != player.ProjectileEpoch
            || !ReferenceEquals(state.Rules, CombatLifetime))
        {
            triggers.Remove(player);
            triggers.Add(player, state = new(player.ProjectileEpoch, CombatLifetime));
        }
        var live = new HashSet<ushort>();
        foreach (var skill in GemSkillEffects(player)) // native equipment/socket order
        {
            var gem = SupportedSkill(player, skill.Id);
            var support = IncomingTriggerSupport(gem, stunned);
            if (support is null || !TriggeredSpellExecutable(player, skill.Id, gem)
                || GemSkillInstanceDisabled(player, gem.Effect, skill.Id, triggered: true) is not null) continue;
            live.Add(skill.Id);
            if (!state.Counters.TryGetValue(skill.Id, out var counter) || !SameTriggerSource(counter.Gem, gem))
                state.Counters[skill.Id] = counter = new(gem);
            if (TriggerGuardSpell(player, gem) && !TriggerGuardReady(player, at)) continue;
            if (counter.Pending || at < counter.ReadyAt || at < LinkedTriggersOf(player).ReadyAt.GetValueOrDefault(gem.Effect)) continue;
            if (stunned)
            {
                if (!RandomRolls.Chance(gem.Stats.GetValueOrDefault(Stat.CastOnStunnedPct), 100, player.IncomingHitRandom)) continue;
            }
            else
            {
                int threshold = gem.Stats.GetValueOrDefault(Stat.CastOnDamageTakenThreshold);
                if (threshold <= 0) continue;
                threshold = (int)Math.Clamp((long)threshold * Math.Max(0L, 100L
                    + gem.Stats.GetValueOrDefault(Stat.CastWhenDamageTakenTriggerThresholdPlusPct)) / 100, 1, int.MaxValue);
                counter.Damage += damage;
                if (counter.Damage < threshold) continue;
            }
            if (state.Pending.Count >= 128) continue;
            counter.Damage = 0; counter.Pending = true;
            var origin = player.Walk.Position(at);
            var aim = player.Area.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0)
                .OrderBy(e => Dist(origin, e.Value.Walk.Position(at))).ThenBy(e => e.Key)
                .Select(e => e.Value.Walk.Position(at)).FirstOrDefault((origin.X + 20, origin.Y));
            state.Pending.Enqueue((skill.Id, counter, aim, at));
        }
        foreach (var id in state.Counters.Keys.Where(id => !live.Contains(id)).ToArray()) state.Counters.Remove(id);
    }

    public static void ReleaseDamageTakenTriggers(Player player, ProjectileSession session)
    {
        ReleaseIncomingHitTriggers(player, session, stunned: false);
        ReleaseIncomingHitTriggers(player, session, stunned: true);
    }

    static void ReleaseIncomingHitTriggers(Player player, ProjectileSession session, bool stunned)
    {
        var triggers = stunned ? StunTriggers : DamageTriggers;
        if (!triggers.TryGetValue(player, out var state)) return;
        if (!CurrentGameRules || !ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != player.ProjectileEpoch
            || player.Closed || player.LeavingArea || player.PendingTransfer is not null || player.PlayerLife == 0)
        { triggers.Remove(player); return; }
        if (player.Paused) return;
        while (state.Pending.TryDequeue(out var request))
        {
            ushort skillId = request.Skill;
            var counter = request.Counter; counter.Pending = false;
            var gem = SupportedSkill(player, request.Skill);
            double now = Math.Max(request.At, AreaTime(player));
            if (now < LinkedTriggersOf(player).ReadyAt.GetValueOrDefault(gem.Effect)) continue;
            if (!state.Counters.TryGetValue(request.Skill, out var current) || !ReferenceEquals(current, counter)
                || !SameTriggerSource(counter.Gem, gem)
                || GemSkillInstanceDisabled(player, gem.Effect, request.Skill, triggered: true) is not null
                || !TriggerPayloadAvailable(player, gem)) continue;
            var skill = GemSkillEffects(player).FirstOrDefault(g => g.Id == request.Skill);
            if (skill.Name is null) continue;
            var support = IncomingTriggerSupport(gem, stunned);
            if (support is null) continue;
            bool Current() => state.Epoch == player.ProjectileEpoch && CurrentGameRules
                && ReferenceEquals(state.Rules, CombatLifetime) && session.Active && !player.Paused
                && player.PlayerLife > 0 && !player.LeavingArea && player.PendingTransfer is null
                && SameTriggerSource(gem, SupportedSkill(player, skillId));
            if (TryReleaseAutomaticPayload(player, session, skillId, gem, support, request.Aim, Current, now,
                stunned ? "Cast when Stunned" : "CWDT", out double readyAt))
                counter.ReadyAt = readyAt;
        }
    }

    static bool TryReleaseAutomaticPayload(Player player, ProjectileSession session, ushort skillId, SupportedGem gem,
        GemSupports.Applied support, (int X, int Y) aim, Func<bool> current, double now, string label, out double readyAt)
    {
        readyAt = 0;
        if (!current()) return false;
        var release = PrepareTriggeredPayload(player, session, skillId, aim, current);
        if (release is null || !PayMana(player, skillId, out uint mana, out uint life)) return false;
        var playerStats = CharacterPlayerStats(player);
        int milliseconds = Math.Max(SkillsFor(player).CooldownMs(gem.Effect, gem.Level),
            SkillsFor(player).CooldownMs(support.Gem.Effect, support.Gem.Level));
        double rate = Math.Max(0, 1 + ((long)playerStats.GetValueOrDefault(Stat.VirtualCooldownSpeedPlusPct)
            + gem.Stats.GetValueOrDefault(Stat.BaseCooldownSpeedPlusPct)
            + gem.Stats.GetValueOrDefault(Stat.VirtualCooldownSpeedPlusPct)) / 100d);
        readyAt = milliseconds == 0 ? now : rate > 0 ? now + milliseconds / 1000d / rate : double.PositiveInfinity;
        LinkedTriggersOf(player).ReadyAt[gem.Effect] = readyAt;
        release();
        if (mana > 0 || life > 0) session.Enqueue(() => SendSkillCost(player, life, label + " cost"));
        return true;
    }
}
