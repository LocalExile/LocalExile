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
        if (GolemStat(gem.Stats, Stat.UniqueCosprisMaliceColdSpellsTriggered) != 0)
            return "this skill can only trigger on a melee critical strike with the item it is socketed in";
        if (GolemStat(gem.Stats, Stat.SkillTriggeredBySnipe) != 0)
            return "this skill can only be triggered by Snipe";
        if (GolemStat(gem.Stats, Stat.TriggerOnSkillUsePctIfYouHaveASpiritCharge) > 0)
            return "Spirit Burst triggers on skill use while a Spirit Charge is held";
        if (gem.Effect >= 0 && IsRelicSkill(Skills.EffectId(gem.Effect) ?? ""))
            return "Summon Elemental Relic triggers automatically; manual use is unavailable";
        if (GolemStat(gem.Stats, Stat.SpellUncastableIfTriggerable) != 0
            || gem.Supports.Any(s => s.Effect.Added.Contains(36)))
            return "this trigger has no server executor; manual use is unavailable";
        return null;
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
