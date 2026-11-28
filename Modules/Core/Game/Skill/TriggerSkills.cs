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
        public readonly Queue<(ushort Skill, TriggerCounter Counter, (int X, int Y) Aim, double At, uint Target)> Pending = [];
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
        if (gem.Stats.GetValueOrDefault(Stat.SpellslingerTriggerOnWandAttackPct) > 0)
            return "this spell can only trigger when Spellslinger fires it";
        if (CooldownTriggerSupport(gem) is { } timed)
            return timed.Effect.Id == AutoexertionSupport
                ? "this warcry can only be triggered by Autoexertion" : "this spell can only be triggered by Automation";
        if (ItemEffectRules.Enabled && gem.WornGrant is { } grant && ItemEffectRules.MarkFamily(grant.EffectId) is not null)
            return "this mark is triggered by the granting item";
        if (gem.WornGrant is { EffectId: GuardianWeaponEffect })
            return "Animate Guardian's Weapon triggers from a qualifying minion kill";
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
        if (GolemStat(gem.Stats, Stat.TriggerOnTriggerLinkTargetHit) != 0)
            return "this skill can only trigger on an Intuitive Link target's hit";
        if (GolemStat(gem.Stats, Stat.CastSpellWhileLinkedSkillChannelling) != 0)
            return "this skill can only trigger while a linked skill is channelled";
        if (GolemStat(gem.Stats, Stat.UniqueCosprisMaliceColdSpellsTriggered) != 0)
            return "this skill can only trigger on a melee critical strike with the item it is socketed in";
        if (GolemStat(gem.Stats, Stat.SkillTriggeredBySnipe) != 0)
            return "this skill can only be triggered by Snipe";
        if (GolemStat(gem.Stats, Stat.TriggeredByFissureSupport) != 0)
            return "Fissure triggers on a supported slam's ground impact";
        if (GolemStat(gem.Stats, Stat.TriggeredByCrystalfallSupport) != 0)
            return "Falling Crystal triggers on a supported slam hit";
        if (GolemStat(gem.Stats, Stat.TriggeredByFoulgraspSupport) != 0)
            return "Foulgrasp triggers when a supported Brand is recalled";
        if (GolemStat(gem.Stats, Stat.TriggeredPrismaticBurst) != 0)
            return "Prismatic Burst triggers on a supported attack hit";
        if (GolemStat(gem.Stats, Stat.TriggeredHextoad) != 0)
            return "Bursting Toad triggers from a supported cursed ground";
        if (GolemStat(gem.Stats, Stat.TriggeredByInfernalCry) != 0)
            return "Combust triggers on an attack exerted by Infernal Cry";
        if (GolemStat(gem.Stats, Stat.TriggeredByShockwaveSupport) != 0)
            return "this Shockwave triggers on supported melee hits";
        if (GolemStat(gem.Stats, Stat.TriggeredByPyreSupport) != 0)
            return "Call the Pyre triggers on supported melee hits against ignited enemies";
        if (GolemStat(gem.Stats, Stat.TriggeredByBonespireSupport) != 0)
            return "Seize the Flesh triggers on use of a supported warcry";
        if (GolemStat(gem.Stats, Stat.TriggeredByKineticInstabilitySupport) != 0)
            return "Kinetic Flux triggers from attacks supported by Kinetic Instability";
        if (GolemStat(gem.Stats, Stat.TriggeredByVoidstormSupport) != 0)
            return "Voidstorm triggers when a supported ranged attack lands";
        if (GolemStat(gem.Stats, Stat.TriggeredBySupportMovement) != 0)
            return "Windburst triggers on a supported attack hit after moving";
        if (GolemStat(gem.Stats, Stat.TriggeredByHivebornSupport) != 0)
            return "Summon Hiveborn triggers when you use a supported offering";
        if (gem.WornGrant is { EffectId: GoreShockwaveEffect })
            return "Gore Shockwave triggers on melee hits with at least 150 Strength";
        if (GolemStat(gem.Stats, Stat.TriggeredByDivineCry) != 0)
            return "this spell triggers on an attack exerted by Battlemage's Cry";
        if (GolemStat(gem.Stats, Stat.InfernalBoltTriggeredWhenTotemWithThisSkillHitByEnemy) != 0)
            return "Avenging Flame triggers when its totem is hit by an enemy";
        if (GolemStat(gem.Stats, Stat.TriggerOnSkillUsePctIfYouHaveASpiritCharge) > 0)
            return "Spirit Burst triggers on skill use while a Spirit Charge is held";
        if (gem.Effect >= 0)
        {
            string effect = Skills.EffectId(gem.Effect) ?? "";
            if (IsDoomBlastEffect(effect)) return "Doom Blast triggers when a supported hex ends";
            if (effect == SummonSacredWispEffect) return "Sacred Wisps are summoned by a supported wand hit";
            if (IsRelicSkill(effect)) return "Summon Elemental Relic triggers automatically; manual use is unavailable";
        }
        if (ItemEffectRules.Enabled && gem.Supports.Any(s => s.Effect.Added.Contains(36)
            && ItemEffectRules.Current.TriggersOnBlock(s.Stats)))
            return "this socketed spell triggers when you block";
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
        && !SkillsFor(player).IsAttack(gem.Effect) && (!SkillsFor(player).IsTravel(gem.Effect)
            || SkillsFor(player).EffectId(gem.Effect) is CorpseBodyswapEffect or BodyswapSacrificeEffect)
        && !SkillsFor(player).IsRetaliation(gem.Effect)
        && Supports.Of(gem.Effect)?.Types.Contains(47) != true && PlacedKind(gem) is (false, false)
        && gem.Supports.Count(s => s.Effect.Added.Contains(36)) == 1
        && SupportedReservation(player, id) is null && SkillsFor(player).VaalBudget(gem.Effect, gem.Level) is null
        && TriggerPayloadAvailable(player, gem);

    // Called only by accepted hit damage, before Ward/ES/Mana absorb it. DoT and costs never enter this hook.
    public static void RecordDamageTakenTriggers(Player player, int damage, double at) =>
        RecordIncomingHitTriggers(player, damage, at, stunned: false);

    static void RecordIncomingHitTriggers(Player player, int damage, double at, bool stunned, uint attacker = 0)
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
        ((int X, int Y) At, uint Target)? source = null;
        foreach (var skill in GemSkillEffects(player)) // native equipment/socket order
        {
            var gem = SupportedSkill(player, skill.Id);
            var support = IncomingTriggerSupport(gem, stunned);
            if (support is null || !TriggeredSpellExecutable(player, skill.Id, gem)
                || GemSkillInstanceDisabled(player, gem.Effect, skill.Id, triggered: true) is not null) continue;
            live.Add(skill.Id);
            if (!state.Counters.TryGetValue(skill.Id, out var counter) || !SameTriggerSource(counter.Gem, gem))
                state.Counters[skill.Id] = counter = new(gem);
            if (TriggerGuardSpell(player, gem) && !GuardAvailableForTrigger(player, at)) continue;
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
            if (source is null)
            {
                // Target the hit that crossed this spell's threshold or caused the stun.
                if (attacker != 0 && player.Area.Monsters.TryGetValue(attacker, out var enemy))
                    source = (enemy.Walk.Position(at), attacker);
                else
                {
                    var origin = player.Walk.Position(at);
                    var aim = player.Area.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0)
                        .OrderBy(e => Dist(origin, e.Value.Walk.Position(at))).ThenBy(e => e.Key)
                        .Select(e => e.Value.Walk.Position(at)).FirstOrDefault((origin.X + 20, origin.Y));
                    source = (aim, 0);
                }
            }
            state.Pending.Enqueue((skill.Id, counter, source.Value.At, at, source.Value.Target));
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
                stunned ? "Cast when Stunned" : "CWDT", out double readyAt, request.Target))
                counter.ReadyAt = readyAt;
        }
    }

    static bool TryReleaseAutomaticPayload(Player player, ProjectileSession session, ushort skillId, SupportedGem gem,
        GemSupports.Applied? support, (int X, int Y) aim, Func<bool> current, double now, string label, out double readyAt,
        uint triggerTarget = 0)
    {
        readyAt = 0;
        if (!current()) return false;
        var release = PrepareTriggeredPayload(player, session, skillId, aim, current, triggerTarget: triggerTarget);
        if (release is null || !PayMana(player, skillId, out uint mana, out uint life)) return false;
        var playerStats = CharacterPlayerStats(player);
        int milliseconds = Math.Max(SkillsFor(player).CooldownMs(gem.Effect, gem.Level),
            support is { } trigger ? SkillsFor(player).CooldownMs(trigger.Gem.Effect, trigger.Gem.Level) : 0);
        if (gem.Stats.GetValueOrDefault(Stat.SpellCastTimeAddedToCooldownIfTriggered) != 0)
            milliseconds += SkillsFor(player).CastTimeMs(gem.Effect);
        double rate = Math.Max(0, 1 + ((long)playerStats.GetValueOrDefault(Stat.VirtualCooldownSpeedPlusPct)
            + gem.Stats.GetValueOrDefault(Stat.BaseCooldownSpeedPlusPct)
            + gem.Stats.GetValueOrDefault(Stat.VirtualCooldownSpeedPlusPct)) / 100d);
        readyAt = milliseconds == 0 ? now : rate > 0 ? now + milliseconds / 1000d / rate : double.PositiveInfinity;
        LinkedTriggersOf(player).ReadyAt[gem.Effect] = readyAt;
        release();
        if (mana > 0 || life > 0) session.Enqueue(() => SendSkillCost(player, life, label + " cost"));
        return true;
    }

    static double TriggerCooldownRate(IReadOnlyDictionary<uint, int> playerStats, SupportedGem gem) => Math.Max(0.01,
        1 + (GolemStat(playerStats, Stat.VirtualCooldownSpeedPlusPct)
            + GolemStat(playerStats, Stat.BaseSpellCooldownSpeedPlusPct)
            + GolemStat(gem.Stats, Stat.BaseSpellCooldownSpeedPlusPct)
            + GolemStat(gem.Stats, Stat.BaseCooldownSpeedPlusPct)
            + GolemStat(gem.Stats, Stat.VirtualCooldownSpeedPlusPct)) / 100.0);

    public static void TriggerCraftedWeaponSpells(Player player, (int X, int Y) aim, double at)
    {
        if (!CurrentGameRules || player.Closed || player.LeavingArea || player.PendingTransfer is not null
            || player.Paused || player.PlayerLife == 0 || InTown(player)) return;
        using var mutation = ProjectileMutation(player, advance: false);
        if (!CurrentGameRules || player.Closed || player.LeavingArea || player.PendingTransfer is not null
            || player.Paused || player.PlayerLife == 0 || InTown(player)) return;
        var session = ProjectilesFor(player);
        if (!session.Active) return;

        // Group crafted-trigger spells by weapon host in native socket order: each weapon triggers up to one spell
        // per skill use, rotating independently and keeping a per-spell cooldown in LinkedTriggersOf(player).
        List<(uint Host, List<(ushort Id, SupportedGem Gem)> Spells)>? byHost = null;
        foreach (var s in GemSkillEffects(player))
        {
            var gem = SupportedSkill(player, s.Id);
            if (GolemStat(gem.Stats, Stat.SpellHasTriggerFromCraftedItemMod) == 0
                && !gem.Supports.Any(sup => sup.Effect.Id == "SupportTriggerSpellOnSkillUse")) continue;
            if (!TriggeredSpellExecutable(player, s.Id, gem)) continue;
            byHost ??= [];
            List<(ushort Id, SupportedGem Gem)>? list = null;
            foreach (var entry in byHost)
                if (entry.Host == gem.Host) { list = entry.Spells; break; }
            if (list is null) byHost.Add((gem.Host, list = []));
            list.Add((s.Id, gem));
        }
        if (byHost is null) return;

        var state = LinkedTriggersOf(player);
        double now = Math.Max(at, AreaTime(player));
        var origin = player.Walk.Position(now);
        if (aim == default || aim == (origin.X, origin.Y))
        {
            aim = player.Area.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0)
                .OrderBy(e => Dist(origin, e.Value.Walk.Position(now))).ThenBy(e => e.Key)
                .Select(e => e.Value.Walk.Position(now)).FirstOrDefault((origin.X + 20, origin.Y));
        }
        var playerStats = CharacterPlayerStats(player);

        foreach (var (host, candidates) in byHost)
        {
            var key = (host, -1, "CraftedWeapon");
            int next = state.Rotation.TryGetValue(key, out var previous) ? previous.Next : 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                int idx = (next + i) % candidates.Count;
                var (spellId, gem) = candidates[idx];
                if (GemSkillInstanceDisabled(player, gem.Effect, spellId, triggered: true) is not null) continue;
                if (now < state.ReadyAt.GetValueOrDefault(gem.Effect)) continue;

                int supportCdMs = GolemStat(gem.Stats, Stat.LocalWeaponTriggerSocketedSpellOnSkillUseDisplayCooldownMs);
                if (supportCdMs <= 0 && gem.Supports.FirstOrDefault(s => s.Effect.Id == "SupportTriggerSpellOnSkillUse") is { } sup)
                    supportCdMs = SkillsFor(player).CooldownMs(sup.Gem.Effect, sup.Gem.Level);
                if (supportCdMs <= 0) supportCdMs = 4000;
                int cdMs = Math.Max(SkillsFor(player).CooldownMs(gem.Effect, gem.Level), supportCdMs);
                if (gem.Stats.GetValueOrDefault(Stat.SpellCastTimeAddedToCooldownIfTriggered) != 0)
                    cdMs += SkillsFor(player).CastTimeMs(gem.Effect);

                double rate = TriggerCooldownRate(playerStats, gem);
                bool Current() => state.Epoch == player.ProjectileEpoch && CurrentGameRules
                    && ReferenceEquals(state.Rules, CombatLifetime) && session.Active && !player.Paused
                    && player.PlayerLife > 0 && !player.LeavingArea && player.PendingTransfer is null
                    && SameTriggerSource(gem, SupportedSkill(player, spellId));

                var release = PrepareTriggeredPayload(player, session, spellId, aim, Current);
                if (release is null) continue;

                if (!PayMana(player, spellId, out uint cost, out uint lifeCost))
                {
                    state.Rotation[key] = (null, (idx + 1) % candidates.Count);
                    break;
                }

                state.ReadyAt[gem.Effect] = now + (cdMs / 1000.0) / rate;
                state.Rotation[key] = (null, (idx + 1) % candidates.Count);
                release();
                if (cost > 0 || lifeCost > 0)
                    session.Enqueue(() => SendSkillCost(player, lifeCost, "trigger wand cost"));
                break;
            }
        }
    }
}
