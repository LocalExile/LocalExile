using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string AutomationSupport = "SupportAutomation", AutoexertionSupport = "SupportAutoexertion";
    const ushort AutomationBuffRow = 2437, AutoexertionBuffRow = 2438;

    sealed record CooldownTrigger(ushort Row, int Effect, GemSourceLifetimes.SourceLifetime Source);
    sealed class CooldownTriggerState
    {
        public readonly Dictionary<ushort, CooldownTrigger> Active = [];
        public readonly Dictionary<ushort, double> NextTriggerAt = [];
        public int Mana, Life;
    }
    sealed record CarriedCooldownTrigger(GemSkillIds.Gem Source, int Effect);
    sealed record CooldownTriggerTransfer(string Character, DateTime At, CarriedCooldownTrigger[] Active);
    static readonly RuleTable<Player, CooldownTriggerState> CooldownTriggers = new();
    static readonly ConcurrentDictionary<uint, CooldownTriggerTransfer> CarriedCooldownTriggers = new();

    static ushort CooldownTriggerController(string? effect) => effect switch
    {
        "Automation" => AutomationBuffRow,
        "Autoexertion" => AutoexertionBuffRow,
        _ => 0,
    };

    static GemSupports.Applied? CooldownTriggerSupport(SupportedGem gem) =>
        gem.Supports.FirstOrDefault(s => s.Effect.Id is AutomationSupport or AutoexertionSupport);

    static bool CooldownTriggerSelfBuff(string? effect) => effect is "PhaseRun" or "BloodRage";

    static Buff CooldownTriggerBuff(Player player, ushort id, CooldownTrigger trigger) =>
        new(trigger.Row, player.Id, SourceSkill: id,
            ActiveSkill: SkillsFor(player).ActionHash(trigger.Effect) is ushort hash ? (id, hash) : null,
            Tag: id);

    static bool CooldownTriggerExecutable(Player player, SupportedGem gem)
    {
        if (CooldownTriggerSupport(gem) is not { } support || gem.Supports.Count(s => s.Effect.Added.Contains(36)) != 1
            || PlacedKind(gem) is not (false, false) || GolemStat(gem.Stats, Stat.IsTotem) != 0) return false;
        var skills = SkillsFor(player);
        if (skills.CooldownMs(gem.Effect, gem.Level) <= 0 || skills.IsVaal(gem.Effect)
            || skills.IsRetaliation(gem.Effect)) return false;
        if (support.Effect.Id == AutoexertionSupport) return skills.IsWarcry(gem.Effect);
        return skills.IsSpell(gem.Effect) && skills.IsInstant(gem.Effect) && !skills.IsAttack(gem.Effect)
            && skills.Reservation(gem.Effect, gem.Level) is null
            && (CooldownTriggerSelfBuff(skills.EffectId(gem.Effect))
                || IsBrandRecall(player, gem.Effect)
                || IsRighteousFireEffect(skills.EffectId(gem.Effect)) || IsMineDetonation(skills.EffectId(gem.Effect))
                || TriggerPayloadAvailable(player, gem));
    }

    // Client skill-bar usability (0x140120f27..0x14012116e) and CanStart (0x141ae7a00) for Automation (0xb5c1) and
    // Autoexertion/CallToArms (0xe18b) require meta_skill_linked_skill_count (0x372e) > 0; network skill records do
    // not run client stat calculators, so publish the exact count of valid linked children here.
    static int CooldownTriggerLinkedCount(Player player, ushort controllerId, int effect)
    {
        ushort row = CooldownTriggerController(SkillsFor(player).EffectId(effect));
        if (row == 0) return 0;
        var sources = PlayerSkillSourcesLocked(player);
        var controller = sources.FirstOrDefault(s => s.Active && s.Id == controllerId);
        if (controller is null) return 0;
        string supportId = row == AutomationBuffRow ? AutomationSupport : AutoexertionSupport;
        int count = 0;
        foreach (var source in sources)
        {
            if (!source.Active || source.Id == controllerId || source.Identity.Host != controller.Identity.Host) continue;
            var gem = SupportedSkill(player, source.Id);
            if (CooldownTriggerSupport(gem) is { } support && support.Effect.Id == supportId
                && support.Gem.Socket == controller.Identity.Socket && gem.RequirementsMet
                && CooldownTriggerExecutable(player, gem))
                count++;
        }
        return count;
    }

    static int CooldownTriggerReservation(Player player, bool life) =>
        CooldownTriggers.TryGetValue(player, out var state) ? life ? state.Life : state.Mana : 0;

    static bool CooldownTriggerCurrent(Player player, ushort id, CooldownTrigger trigger) =>
        GemSourceLifetimeCurrentLocked(player, id, trigger.Effect, trigger.Source)
        && SupportedSkill(player, id).RequirementsMet;

    static void EndCooldownTrigger(Player player, CooldownTriggerState state, ushort id)
    {
        state.NextTriggerAt.Remove(id);
        if (state.Active.Remove(id, out var held)) player.Area.Buffs.Clear(player.Id, held.Row, player.Id, tag: id);
    }

    static void PresentCooldownTriggersLocked(Player player)
    {
        if (!CooldownTriggers.TryGetValue(player, out var state) || state.Active.Count == 0) return;
        foreach (var (id, trigger) in state.Active)
            player.Area.Buffs.Keep(player.Id, CooldownTriggerBuff(player, id, trigger));
    }

    static bool CancelCooldownTriggerLocked(Player player, Buff buff)
    {
        if (buff.Row is not (AutomationBuffRow or AutoexertionBuffRow) || buff.Source != player.Id
            || !CooldownTriggers.TryGetValue(player, out var state)
            || !state.Active.TryGetValue(buff.SourceSkill, out var active) || active.Row != buff.Row) return false;
        EndCooldownTrigger(player, state, buff.SourceSkill);
        (_, state.Mana, state.Life) = ReadCooldownTriggers(player, state);
        ClampReservedResources(player);
        player.AuraStatsDirty = true;
        player.Frames.Area?.Life.Changed(player);
        return true;
    }

    static (List<(ushort Controller, CooldownTrigger Trigger, ushort Skill, SupportedGem Gem, GemSupports.Applied Support)> Children, int Mana, int Life)
        ReadCooldownTriggers(Player player, CooldownTriggerState state)
    {
        var children = new List<(ushort, CooldownTrigger, ushort, SupportedGem, GemSupports.Applied)>();
        if (state.Active.Count == 0) return (children, 0, 0);
        long mana = 0, life = 0;
        var sources = PlayerSkillSourcesLocked(player);
        foreach (var (id, trigger) in state.Active)
        {
            var supportId = trigger.Row == AutomationBuffRow ? AutomationSupport : AutoexertionSupport;
            foreach (var source in sources)
            {
                if (!source.Active || source.Id == id || source.Identity.Host != trigger.Source.Key.Host) continue;
                var gem = SupportedSkill(player, source.Id);
                if (CooldownTriggerSupport(gem) is not { } support || support.Effect.Id != supportId
                    || support.Gem.Socket != trigger.Source.Key.Socket || !gem.RequirementsMet
                    || !CooldownTriggerExecutable(player, gem)) continue;
                children.Add((id, trigger, source.Id, gem, support));
                if (SkillsFor(player).Reservation(support.Gem.Effect, support.Gem.Level) is not { } raw
                    || GolemStat(gem.Stats, Stat.BaseSkillNoReservation) != 0) continue;
                var context = ReservationContext(player, source.Id);
                if (context.Life && (GolemStat(gem.Stats, Stat.BaseSkillNoLifeReservation) != 0
                    || GolemStat(gem.Stats, Stat.SkillNoLifeReservation) != 0)) continue;
                decimal factor = gem.Supports.Aggregate(1m, (value, applied) => value * applied.CostMultiplier / 100m);
                int percent = (int)Math.Clamp(decimal.Floor(raw.ManaPercent * factor), 0, int.MaxValue);
                if (context.Life) life += PetrifiedBlood.Required(player.Pools.Life,
                    player.Pools.LifeReservationEfficiencyPct, percent, context.Efficiency);
                else mana += PetrifiedBlood.Required(player.Pools.Mana,
                    player.Pools.ManaReservationEfficiencyPct, percent, context.Efficiency);
            }
        }
        return (children, ActorScale.Saturate(mana), ActorScale.Saturate(life));
    }

    static bool FitsCooldownTriggerReservation(Player player, int mana, int life) =>
        (long)AuraCatalog.RequiredMana(player.Pools, player.Auras.Values)
            + PetrifiedBlood.RequiredMana(player.Pools, player.PetrifiedBlood)
            + PlacedSkills.Reserved(player.Pools, player.Placed.All.Values, false) + mana <= player.Pools.Mana
        && (long)LifeReservation.Of(player.Pools, player.QualifiedItemLifeReservationPercent, player.Auras.Values,
            PetrifiedBlood.RequiredLife(player.Pools, player.PetrifiedBlood)).Required
            + PlacedSkills.Reserved(player.Pools, player.Placed.All.Values, true) + life < player.Pools.Life;

    public static void CarryCooldownTriggers(Player player, uint ticket)
    {
        if (player.Closed || player.RageRetired || player.PlayerLife == 0) return;
        foreach (var (old, entry) in CarriedCooldownTriggers)
            if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedCooldownTriggers.TryRemove(old, out _);
        if (!CooldownTriggers.TryGetValue(player, out var state) || state.Active.Count == 0) return;
        var active = state.Active.Values.Select(t => new CarriedCooldownTrigger(t.Source.Key, t.Effect)).ToArray();
        if (active.Length > 0) CarriedCooldownTriggers.TryAdd(ticket, new(CharacterName(player), DateTime.UtcNow, active));
    }

    public static void RestoreCooldownTriggers(Player player)
    {
        if (!CarriedCooldownTriggers.TryGetValue(player.Ticket, out var carry)
            || carry.Character != CharacterName(player)
            || !CarriedCooldownTriggers.TryRemove(new KeyValuePair<uint, CooldownTriggerTransfer>(player.Ticket, carry))) return;
        if (player.PlayerLife == 0 || DateTime.UtcNow - carry.At > TimeSpan.FromMinutes(5)) return;
        var sources = PlayerSkillSourcesLocked(player);
        var state = CooldownTriggers.GetValue(player, static _ => new());
        foreach (var saved in carry.Active)
        {
            var source = sources.FirstOrDefault(s => s.Active && s.Effect == saved.Effect && s.Identity == saved.Source);
            if (source is null) continue;
            ushort row = CooldownTriggerController(SkillsFor(player).EffectId(source.Effect));
            if (row == 0) continue;
            var trigger = new CooldownTrigger(row, source.Effect, CaptureGemSourceLifetimeLocked(player, source));
            if (!CooldownTriggerCurrent(player, source.Id, trigger)) continue;
            state.Active[source.Id] = trigger;
        }
        if (state.Active.Count == 0) return;
        var (children, mana, life) = ReadCooldownTriggers(player, state);
        foreach (var id in state.Active.Keys.ToArray())
            if (!children.Any(c => c.Controller == id)) state.Active.Remove(id);
        (children, mana, life) = ReadCooldownTriggers(player, state);
        if (!FitsCooldownTriggerReservation(player, mana, life))
        {
            foreach (var (id, trigger) in state.Active.ToArray())
                if (trigger.Row == AutoexertionBuffRow) state.Active.Remove(id);
            (_, mana, life) = ReadCooldownTriggers(player, state);
        }
        state.Mana = mana;
        state.Life = life;
        ClampReservedResources(player);
    }

    static async Task<(bool Accepted, bool Activated)> ToggleCooldownTrigger(Player player, ushort id, Func<bool> current)
    {
        bool activated;
        using (ProjectileMutation(player, advance: false))
        {
            if (!current() || player.Paused || InTown(player) || player.PlayerLife == 0
                || CurrentPlayerSkillSourceLocked(player, id) is not { Active: true } source) return default;
            ushort row = CooldownTriggerController(SkillsFor(player).EffectId(source.Effect));
            if (row == 0) return default;
            var state = CooldownTriggers.GetValue(player, static _ => new());
            activated = !state.Active.ContainsKey(id);
            if (!activated)
            {
                EndCooldownTrigger(player, state, id);
                (_, state.Mana, state.Life) = ReadCooldownTriggers(player, state);
            }
            else
            {
                var trigger = new CooldownTrigger(row, source.Effect, CaptureGemSourceLifetimeLocked(player, source));
                if (!CooldownTriggerCurrent(player, id, trigger)) return default;
                state.Active.Add(id, trigger);
                var (children, mana, life) = ReadCooldownTriggers(player, state);
                if (!children.Any(c => c.Controller == id) || !FitsCooldownTriggerReservation(player, mana, life))
                {
                    state.Active.Remove(id);
                    return default;
                }
                state.Mana = mana;
                state.Life = life;
                player.Area.Buffs.Set(player.Id, CooldownTriggerBuff(player, id, trigger));
            }
            ClampReservedResources(player);
            player.AuraStatsDirty = true;
            player.Frames.Area?.Life.Changed(player);
        }
        await SendOwnAuraChanges(player);
        return (true, activated);
    }

    static async Task TickCooldownTriggers(Player player, double now)
    {
        if (!CooldownTriggers.TryGetValue(player, out var state) || state.Active.Count == 0) return;
        bool changed = false;
        using (ProjectileMutation(player, advance: false))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PendingTransfer is not null || player.Paused) return;
            foreach (var (id, trigger) in state.Active.ToArray())
                if (player.PlayerLife == 0 || !CooldownTriggerCurrent(player, id, trigger))
                {
                    changed = true;
                    EndCooldownTrigger(player, state, id);
                }
            var (children, mana, life) = ReadCooldownTriggers(player, state);
            bool droppedEmpty = false;
            foreach (var id in state.Active.Keys.ToArray())
                if (!children.Any(c => c.Controller == id))
                {
                    EndCooldownTrigger(player, state, id);
                    droppedEmpty = true;
                }
            if (droppedEmpty)
            {
                (children, mana, life) = ReadCooldownTriggers(player, state);
                changed = true;
            }
            if (!FitsCooldownTriggerReservation(player, mana, life))
            {
                bool ended = false;
                foreach (var (id, trigger) in state.Active.ToArray())
                    if (trigger.Row == AutoexertionBuffRow)
                    {
                        EndCooldownTrigger(player, state, id);
                        ended = true;
                    }
                if (ended)
                {
                    (children, mana, life) = ReadCooldownTriggers(player, state);
                    changed = true;
                }
            }
            if (state.Mana != mana || state.Life != life)
            {
                state.Mana = mana;
                state.Life = life;
                ClampReservedResources(player);
                changed = true;
            }
            if (changed)
            {
                player.AuraStatsDirty = true;
                player.Frames.Area?.Life.Changed(player);
            }

            if (!InTown(player) && HideoutCatalog.ByArea(player.Layout.Hash) is null
                && WaypointCatalog.Areas.Find(player.Layout.Hash)?.Id != WaypointCatalog.Kingsmarch)
                foreach (var child in children)
                    RunCooldownTriggerLocked(player, state, child.Controller, child.Trigger, child.Skill, child.Gem, child.Support, now);
        }
        if (changed) await SendOwnAuraChanges(player);
    }

    static void RunCooldownTriggerLocked(Player player, CooldownTriggerState state, ushort id, CooldownTrigger trigger,
        ushort skillId, SupportedGem gem, GemSupports.Applied support, double now)
    {
        if (now < state.NextTriggerAt.GetValueOrDefault(id)
            || CooldownForSkill(player, skillId) is not { } cooldown
            || (TriggerGuardSpell(player, gem) && !GuardAvailableForTrigger(player, now))
            || GemSkillInstanceDisabled(player, gem.Effect, skillId, triggered: true) is not null
            || now < LinkedTriggersOf(player).ReadyAt.GetValueOrDefault(gem.Effect)) return;
        int epoch = player.ProjectileEpoch;
        var rules = CombatLifetime;
        bool Current() => CurrentGameWork && ReferenceEquals(CombatLifetime, rules) && player.ProjectileActive(epoch)
            && !player.Paused && player.PlayerLife > 0 && player.PendingTransfer is null
            && ReferenceEquals(state.Active.GetValueOrDefault(id), trigger) && CooldownTriggerCurrent(player, id, trigger)
            && SameTriggerSource(gem, SupportedSkill(player, skillId));

        var session = ProjectilesFor(player);
        var aim = player.Walk.Position(now);
        var effect = SkillsFor(player).EffectId(gem.Effect);
        bool warcry = trigger.Row == AutoexertionBuffRow;
        bool guard = TriggerGuardSpell(player, gem);
        Action? release;
        if (warcry) release = () => UseWarcry(player, gem.Effect, gem.Stats, skillId, triggered: true);
        else if (IsMineDetonation(effect))
        {
            if (!player.Placed.All.Values.Any(mine => mine.Mine && double.IsPositiveInfinity(mine.DetonateAt)
                && now < mine.Until && MineCanDetonateFrom(mine, aim))) return;
            release = () => DetonateMinesLocked(player, now);
        }
        else if (IsBrandRecall(player, gem.Effect))
        {
            if (!BrandStates.TryGetValue(player, out var brands) || !brands.Brands.Any(b => !b.CannotBeRecalled)) return;
            release = () => session.Enqueue(async () =>
            {
                if (Current() && GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId) is { Name: not null } skill)
                    await CastBrandRecall(player, skillId, skill, Current);
            });
        }
        else if (CooldownTriggerSelfBuff(effect))
            release = () => session.Enqueue(async () =>
            {
                if (Current()) await CastSelfBuff(player, skillId, gem.Effect);
            });
        else if (IsRighteousFireEffect(effect))
        {
            if (player.RighteousFire is not null) return;
            release = () => session.Enqueue(async () =>
            {
                if (Current())
                    await ActivateRighteousFire(player, skillId, gem.Effect, gem.Level, gem.Quality, gem.SourceGem?.TypeHash ?? 0);
            });
        }
        else release = PrepareTriggeredPayload(player, session, skillId, aim, Current);
        if (release is null || !TryBeginCooldownUseCore(player, gem.Effect, cooldown, instant: true, () => { },
                out var ticket, null, true, null, skillId, Current, triggered: true)
            || ticket?.TryExecute(player, gem.Effect) != true) return;
        int supportCooldownMs = SkillsFor(player).CooldownMs(support.Gem.Effect, support.Gem.Level);
        if (supportCooldownMs > 0) state.NextTriggerAt[id] = now + supportCooldownMs / 1000.0;
        ticket.CommitNativeUse(player, skillId);
        release();
        if (!guard)
        {
            var instant = InstanceProtocol.InstantAction(player.Id, skillId, aim.X, aim.Y,
                sequence: player.Connection.LockstepClient ? NextLockstepSequence(player) : (ushort)0xffff,
                flags: player.Connection.LockstepClient ? (ushort)0x101 : (ushort)0, triggered: true);
            session.Enqueue(() => SendWorldRecord(player, player.Id, instant,
                warcry ? $"0x01ce Autoexertion triggers {effect}" : $"0x01ce Automation triggers {effect}"));
        }
        if (ticket.Paid is { } paid && (paid.Mana > 0 || paid.Life > 0))
            session.Enqueue(() => SendSkillCost(player, paid.Life, warcry ? "Autoexertion cost" : "Automation cost"));
        session.Enqueue(() => SendNativeCooldownRefresh(player));
    }
}
