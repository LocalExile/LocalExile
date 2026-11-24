public static partial class Game
{
    const string AutomationSupport = "SupportAutomation", AutoexertionSupport = "SupportAutoexertion";
    const ushort AutomationBuffRow = 2437, AutoexertionBuffRow = 2438;

    sealed record CooldownTrigger(ushort Row, int Effect, GemSourceLifetimes.SourceLifetime Source);
    sealed class CooldownTriggerState
    {
        public readonly Dictionary<ushort, CooldownTrigger> Active = [];
        public int Mana, Life;
    }
    static readonly RuleTable<Player, CooldownTriggerState> CooldownTriggers = new();

    static ushort CooldownTriggerController(string? effect) => effect switch
    {
        "Automation" => AutomationBuffRow,
        "Autoexertion" => AutoexertionBuffRow,
        _ => 0,
    };

    static GemSupports.Applied? CooldownTriggerSupport(SupportedGem gem) =>
        gem.Supports.FirstOrDefault(s => s.Effect.Id is AutomationSupport or AutoexertionSupport);

    static bool CooldownTriggerSelfBuff(string? effect) => effect is "PhaseRun" or "BloodRage";

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
                || IsRighteousFireEffect(skills.EffectId(gem.Effect)) || IsMineDetonation(skills.EffectId(gem.Effect))
                || TriggerPayloadAvailable(player, gem));
    }

    static int CooldownTriggerReservation(Player player, bool life) =>
        CooldownTriggers.TryGetValue(player, out var state) ? life ? state.Life : state.Mana : 0;

    static bool CooldownTriggerCurrent(Player player, ushort id, CooldownTrigger trigger) =>
        GemSourceLifetimeCurrentLocked(player, id, trigger.Effect, trigger.Source)
        && SupportedSkill(player, id).RequirementsMet;

    static void EndCooldownTrigger(Player player, CooldownTriggerState state, ushort id)
    {
        if (state.Active.Remove(id, out var held)) player.Area.Buffs.Clear(player.Id, held.Row, player.Id, tag: id);
    }

    static bool CancelCooldownTriggerLocked(Player player, Buff buff)
    {
        if (buff.Row is not (AutomationBuffRow or AutoexertionBuffRow) || buff.Source != player.Id
            || !CooldownTriggers.TryGetValue(player, out var state)
            || !state.Active.TryGetValue(buff.SourceSkill, out var active) || active.Row != buff.Row) return false;
        EndCooldownTrigger(player, state, buff.SourceSkill);
        (_, state.Mana, state.Life) = ReadCooldownTriggers(player, state);
        ClampReservedResources(player);
        return true;
    }

    static (List<(ushort Controller, CooldownTrigger Trigger, ushort Skill, SupportedGem Gem)> Children, int Mana, int Life)
        ReadCooldownTriggers(Player player, CooldownTriggerState state)
    {
        var children = new List<(ushort, CooldownTrigger, ushort, SupportedGem)>();
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
                children.Add((id, trigger, source.Id, gem));
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

    static async Task<bool> ToggleCooldownTrigger(Player player, ushort id, Func<bool> current)
    {
        using (ProjectileMutation(player, advance: false))
        {
            if (!current() || player.Paused || InTown(player) || player.PlayerLife == 0
                || CurrentPlayerSkillSourceLocked(player, id) is not { Active: true } source) return false;
            ushort row = CooldownTriggerController(SkillsFor(player).EffectId(source.Effect));
            if (row == 0) return false;
            var state = CooldownTriggers.GetValue(player, static _ => new());
            if (state.Active.ContainsKey(id))
            {
                EndCooldownTrigger(player, state, id);
                (_, state.Mana, state.Life) = ReadCooldownTriggers(player, state);
            }
            else
            {
                var trigger = new CooldownTrigger(row, source.Effect, CaptureGemSourceLifetimeLocked(player, source));
                if (!CooldownTriggerCurrent(player, id, trigger)) return false;
                state.Active.Add(id, trigger);
                var (_, mana, life) = ReadCooldownTriggers(player, state);
                if (!FitsCooldownTriggerReservation(player, mana, life))
                {
                    state.Active.Remove(id);
                    return false;
                }
                state.Mana = mana;
                state.Life = life;
                player.Area.Buffs.Set(player.Id, new(row, player.Id, SourceSkill: id, Tag: id));
            }
            ClampReservedResources(player);
        }
        await SendOwnAuraChanges(player);
        return true;
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

            if (!InTown(player) && HideoutCatalog.ByArea(player.Layout.Hash) is null
                && WaypointCatalog.Areas.Find(player.Layout.Hash)?.Id != WaypointCatalog.Kingsmarch)
                foreach (var child in children)
                    RunCooldownTriggerLocked(player, state, child.Controller, child.Trigger, child.Skill, child.Gem, now);
        }
        if (changed) await SendOwnAuraChanges(player);
    }

    static void RunCooldownTriggerLocked(Player player, CooldownTriggerState state, ushort id, CooldownTrigger trigger,
        ushort skillId, SupportedGem gem, double now)
    {
        if (CooldownForSkill(player, skillId) is not { } cooldown
            || (TriggerGuardSpell(player, gem) && !TriggerGuardReady(player, now))
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
        Action? release;
        if (warcry) release = () => UseWarcry(player, gem.Effect, gem.Stats, skillId, triggered: true);
        else if (IsMineDetonation(effect))
        {
            if (!player.Placed.All.Values.Any(mine => mine.Mine && double.IsPositiveInfinity(mine.DetonateAt)
                && now < mine.Until && MineCanDetonateFrom(mine, aim))) return;
            release = () => DetonateMinesLocked(player, now);
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
        ticket.CommitNativeUse(player, skillId);
        release();
        if (ticket.Paid is { } paid && (paid.Mana > 0 || paid.Life > 0))
            session.Enqueue(() => SendSkillCost(player, paid.Life, warcry ? "Autoexertion cost" : "Automation cost"));
        session.Enqueue(() => SendNativeCooldownRefresh(player));
    }
}
