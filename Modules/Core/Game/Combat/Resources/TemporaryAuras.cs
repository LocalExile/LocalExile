using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    internal sealed record TemporaryAura(AuraCatalog.Aura Aura, double Until, SupportedGem Source, bool Blessing);
    internal sealed class TemporaryAuraState
    {
        public readonly Dictionary<ushort, TemporaryAura> Active = [];
        public bool StatsPending;
    }
    internal static readonly RuleTable<Player, TemporaryAuraState> TemporaryAuraStates = new();
    static readonly Dictionary<string, ushort> TimedVaalAuraBuffRows = new(StringComparer.Ordinal)
    {
        ["VaalClarity"] = 158,
        ["VaalGrace"] = 159,
        ["VaalDiscipline"] = 160,
        ["VaalHaste"] = 161,
        ["VaalImpurityOfFire"] = 971,
        ["VaalImpurityOfIce"] = 972,
        ["VaalImpurityOfLightning"] = 973,
    };

    internal static bool IsBlessing(SupportedGem skill) => IsBlessingSkill(skill)
        && GolemStat(skill.Stats, Stat.SupportAuraDurationBaseBuffDuration) > 0;

    internal static uint BlessingBaseCost(uint maximumMana, int flat, int percent, int added, decimal multiplier) =>
        (uint)Math.Clamp(decimal.Floor((flat + maximumMana * percent / 10000m + added) * multiplier), 0, uint.MaxValue);

    static IReadOnlyDictionary<uint, int> WithBlessingCost(Player player, SupportedGem skill)
    {
        if (!IsBlessing(skill) || SkillsFor(player).Reservation(skill.Effect, skill.Level) is not { } reserve) return skill.Stats;
        var own = new Dictionary<uint, int>(skill.Stats);
        decimal factor = skill.Supports.Aggregate(1m, (v, s) => v * s.CostMultiplier / 100m);
        int cost = (int)Math.Min(int.MaxValue, BlessingBaseCost(player.Pools.Mana, reserve.ManaFlat, reserve.ManaPercent,
            GolemStat(skill.Stats, Stat.BaseManaCostPlus), factor));
        own[Stat.BaseManaCost] = cost;
        own[Stat.ManaCost] = cost;
        return own;
    }

    static bool IsTemporaryAura(Player player, AuraCatalog.Aura aura) =>
        TemporaryAuraStates.TryGetValue(player, out var state) && state.Active.TryGetValue(aura.SkillId, out var current)
        && current.Aura.Buff == aura.Buff;

    static void RemoveTemporaryAura(Player player, TemporaryAuraState state, ushort id)
    {
        if (!state.Active.Remove(id, out var old)) return;
        if (player.Auras.TryGetValue(id, out var aura) && aura.Buff == old.Aura.Buff) player.Auras.Remove(id);
        state.StatsPending = true;
        player.AuraStatsDirty = true;
    }

    // Called by the existing aura/tick lifecycle, including while idle. No per-aura worker or timer.
    internal static void ExpireTemporaryAuras(Player player)
    {
        if (!TemporaryAuraStates.TryGetValue(player, out var state)) return;
        int before = state.Active.Count;
        foreach (var (id, value) in state.Active.ToArray())
            if (player.PlayerLife == 0 || InTown(player) || AreaTime(player) >= value.Until
                || !player.Auras.TryGetValue(id, out var aura) || aura.Buff != value.Aura.Buff
                || !SameTriggerSource(value.Source, SupportedSkill(player, id)))
                RemoveTemporaryAura(player, state, id);
        if (before != state.Active.Count)
        {
            player.Pools = BuildPoolsFor(player, player.Pools.Level);
            player.ApplyMovementSpeed();
        }
    }

    static double? TemporaryAuraSecondsLeft(Player player, AuraCatalog.Aura aura) =>
        TemporaryAuraStates.TryGetValue(player, out var state) && state.Active.TryGetValue(aura.SkillId, out var value)
            && value.Aura.Buff == aura.Buff ? Math.Max(0, value.Until - AreaTime(player)) : null;

    internal static AuraCatalog.Aura StartTemporaryAuraLocked(Player player, ushort id, string family, ushort row,
        double seconds, bool blessing)
    {
        var source = SupportedSkill(player, id);
        var state = TemporaryAuraStates.GetOrCreateValue(player);
        if (blessing) EndOtherBlessingsLocked(player);
        foreach (var (other, entry) in state.Active.ToArray())
            if (other == id || blessing && entry.Blessing || entry.Aura.EffectId == family)
                RemoveTemporaryAura(player, state, other);
        var support = AuraSupport(player, id);
        var (artVariation, hideVisual, sourceVisual, buffVisual) =
            ReservationCosmeticVisuals(player, id, family, hasSource: true);
        if (family == "VaalDiscipline")
        {
            sourceVisual ??= 329; // BuffDefinitions 160 uses BuffVisuals 329.
            buffVisual ??= 329;
            // Recasting starts a new HUD countdown even when the row and source are unchanged.
            player.Area.Buffs.Clear(player.Id, row, player.Id, tag: (ushort)BuffTag.VaalAura);
        }
        var aura = new AuraCatalog.Aura(id, source.Effect, family, family,
            player.NextBuff++, row, 0, 0, source.Level, source.Quality, SourceBuff: player.NextBuff++,
            SkillStats: [.. source.Stats.Select(p => (p.Key, p.Value))],
            SupportEffectPct: support.EffectPct, AffectsSelf: support.AffectsSelf,
            ArtVariation: artVariation, HideVisual: hideVisual, SourceVisual: sourceVisual, BuffVisual: buffVisual);
        aura = WithCasterAuraStamps(player, aura, CharacterStatSources(player).Stats);
        state.Active[id] = new(aura, AreaTime(player) + seconds, source, blessing);
        state.StatsPending = false; // The activation publishes its new character snapshot itself.
        player.Auras[id] = aura;
        player.AuraStatsDirty = true;
        return aura;
    }

    static async Task SendTemporaryAuraExpiry(Player player)
    {
        if (!TemporaryAuraStates.TryGetValue(player, out var state)) return;
        ExpireTemporaryAuras(player);
        if (!state.StatsPending || player.PlayerLife == 0) return;
        state.StatsPending = false;
        await SendOwnAuraChanges(player);
        await SendCharacterStats(player, PlayerStatChange.Auras);
    }

    static async Task<bool> ActivateBlessing(Player player, ushort id, Func<bool> current)
    {
        if (!current()) return false;
        var skill = SupportedSkill(player, id);
        string effect = SkillsFor(player).EffectId(skill.Effect) ?? "";
        if (!IsBlessing(skill) || !AuraCatalog.Supported(effect)) return false;
        double seconds = VaalAuraSeconds(GolemStat(skill.Stats, Stat.SupportAuraDurationBaseBuffDuration),
            CharacterPlayerStats(player), skill.Stats);
        if (seconds <= 0) return false;
        StartTemporaryAuraLocked(player, id, effect, AuraCatalog.BuffRow(effect), seconds, blessing: true);
        await SendOwnAuraChanges(player);
        await SendCharacterStats(player, PlayerStatChange.Auras);
        return true;
    }

    static bool ScheduleBlessing(Player player, ushort id, int recovery, Func<bool> current)
    {
        var source = SupportedSkill(player, id);
        if (CastMs(player, source.Effect, id) is not int cast) return false;
        var session = ProjectilesFor(player);
        bool Valid() => session.Active && current() && player.ActionInterruptionGeneration == recovery
            && PlayerStateAllowsAction(player) && SameTriggerSource(source, SupportedSkill(player, id));
        session.Runtime.Schedule(ProjectileRuntime.Time(AreaTime(player) + cast / 1000d), () =>
        {
            if (Valid()) session.Enqueue(async () => { await ActivateBlessing(player, id, Valid); });
        });
        return true;
    }

    internal static IDisposable CleanupTemporaryAuras() => new TemporaryAuraCleanup();

    static async Task<bool> ActivateTimedVaalAura(Player player, ushort id, string effect, ushort row, Func<bool> current)
    {
        if (!current()) return false;
        var skill = SupportedSkill(player, id);
        double duration = VaalAuraSeconds(GolemStat(skill.Stats, Stat.BaseSkillEffectDuration),
            CharacterPlayerStats(player), skill.Stats);
        if (duration <= 0) return false;
        StartTemporaryAuraLocked(player, id, effect, row, duration, blessing: false);
        await SendOwnAuraChanges(player);
        await SendCharacterStats(player, PlayerStatChange.Auras);
        return true;
    }

    // A Vaal aura's buff: buff_duration_+% and the Vaal skills' increased duration add to its increased duration
    // (kinds); its soul gain prevention takes the buff duration modifiers only (VaalUses).
    static double VaalAuraSeconds(int baseMilliseconds, IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own,
        SkillDurationModifiers.Kinds kinds = SkillDurationModifiers.Kinds.Vaal | SkillDurationModifiers.Kinds.Buff) =>
        SkillDurationModifiers.Seconds(baseMilliseconds, n => GolemStat(player, n), n => GolemStat(own, n), kinds);

    static bool SharedPlayerAura(string effect) => AuraCatalog.Shared(effect) || effect == "Smite" || TimedVaalAuraBuffRows.ContainsKey(effect);

    static int TemporaryAuraValue(AuraCatalog.Aura aura, uint stat, int effect)
    {
        int value = (aura.SkillStats ?? Skills.EffectStats(aura.Effect, aura.Level, aura.Quality))
            .FirstOrDefault(s => s.Item1 == stat).Item2;
        return (int)Math.Clamp(decimal.Truncate(value * (100m + Math.Max(-100,
            (aura.ExternalEffectPct ?? effect + aura.SupportEffectPct) + aura.PerAuraEffectPct)) / 100m), int.MinValue, int.MaxValue);
    }

    static CharacterStats.Bonus WithTemporaryAuraBonus(IEnumerable<AuraCatalog.Aura> auras, int effect, CharacterStats.Bonus bonus)
    {
        int attackSpeed = 0, movementSpeed = 0, fireMaximum = 0, coldMaximum = 0, lightningMaximum = 0;
        foreach (var aura in auras)
        {
            if (!aura.AffectsSelf) continue;
            int Value(uint stat) => TemporaryAuraValue(aura, stat, effect);
            switch (aura.EffectId)
            {
                case "VaalHaste":
                    attackSpeed = Math.Max(attackSpeed, Value(Stat.AttackSpeedPlusPctGrantedFromSkill));
                    movementSpeed = Math.Max(movementSpeed, Value(Stat.BaseMovementVelocityPlusPct));
                    break;
                case "VaalImpurityOfFire":
                    fireMaximum = Math.Max(fireMaximum, Value(Stat.BaseMaximumFireDamageResistancePct));
                    break;
                case "VaalImpurityOfIce":
                    coldMaximum = Math.Max(coldMaximum, Value(Stat.BaseMaximumColdDamageResistancePct));
                    break;
                case "VaalImpurityOfLightning":
                    lightningMaximum = Math.Max(lightningMaximum, Value(Stat.BaseMaximumLightningDamageResistancePct));
                    break;
            }
        }
        return bonus with
        {
            AttackSpeedPct = bonus.AttackSpeedPct + attackSpeed,
            MovementVelocityPct = bonus.MovementVelocityPct + movementSpeed,
            FireMaxResistance = bonus.FireMaxResistance + fireMaximum,
            ColdMaxResistance = bonus.ColdMaxResistance + coldMaximum,
            LightningMaxResistance = bonus.LightningMaxResistance + lightningMaximum,
        };
    }

    static void WithTemporaryAuraStats(Player player, IDictionary<uint, int> stats)
    {
        static bool Temporary(AuraCatalog.Aura aura) => aura.EffectId == "Smite" || TimedVaalAuraBuffRows.ContainsKey(aura.EffectId);
        if (!player.Auras.Values.Any(Temporary) && !player.ReceivedAuras.Values.Any(Temporary)) return;
        WithTemporaryAuraStats(AffectingAuras(player), player.Pools.AuraEffectPct, stats);
    }

    static void WithTemporaryAuraStats(IEnumerable<AuraCatalog.Aura> auras, int effect, IDictionary<uint, int> stats)
    {
        void Add(uint key, IEnumerable<AuraCatalog.Aura> group, uint from)
        {
            int value = group.Select(a => TemporaryAuraValue(a, from, effect)).DefaultIfEmpty().Max();
            if (value != 0) stats[key] = (stats.TryGetValue(key, out int cur) ? cur : 0) + value;
        }
        var smite = auras.Where(a => a.AffectsSelf && a.EffectId == "Smite").ToArray();
        Add(Stat.AttackMinimumAddedLightningDamage, smite, Stat.MinimumAddedLightningDamageFromSkill);
        Add(Stat.AttackMaximumAddedLightningDamage, smite, Stat.MaximumAddedLightningDamageFromSkill);
        Add(Stat.SpellMinimumAddedLightningDamage, smite, Stat.MinimumAddedLightningDamageFromSkill);
        Add(Stat.SpellMaximumAddedLightningDamage, smite, Stat.MaximumAddedLightningDamageFromSkill);
        Add(Stat.BaseChanceToShockPct, smite, Stat.BaseChanceToShockPctFromSkill);
        // This is a count supplied by the Vaal buff, not a scalable damage stat.
        int extra = smite.Select(a =>
                Skills.EffectStats(a.Effect, a.Level, a.Quality).FirstOrDefault(p =>
                    p.Item1 == Stat.BuffGrantsSmiteAdditionalLightningTargets).Item2).DefaultIfEmpty().Max();
        if (extra != 0)
            stats[Stat.BaseSmiteNumberOfTargets] = (stats.TryGetValue(Stat.BaseSmiteNumberOfTargets, out int curExtra) ? curExtra : 0) + extra;
        Add(Stat.CastSpeedPlusPct, auras.Where(a => a.AffectsSelf && a.EffectId == "VaalHaste"), Stat.CastSpeedPlusPctGrantedFromSkill);
        Add(Stat.ChanceToEvadeAttacksPct, auras.Where(a => a.AffectsSelf && a.EffectId == "VaalGrace"), Stat.ChanceToEvadeAttacksPct);
        foreach (var aura in auras)
        {
            if (!aura.AffectsSelf) continue;
            switch (aura.EffectId)
            {
                case "VaalClarity": stats[Stat.NoManaCost] = 1; break;
                case "VaalDiscipline": stats[Stat.EnergyShieldRechargeNotDelayedByDamage] = 1; break;
                case "VaalGrace": stats[Stat.DamageTakenFromSuppressedHitsIsUnlucky] = 1; break;
                case "VaalImpurityOfFire": stats[Stat.BaseImmuneToIgnite] = stats[Stat.ImmuneToIgnite] = 1; break;
                case "VaalImpurityOfIce":
                    stats[Stat.BaseImmuneToFreeze] = stats[Stat.ImmuneToFreeze] = 1;
                    stats[Stat.BaseImmuneToChill] = stats[Stat.ImmuneToChill] = 1;
                    break;
                case "VaalImpurityOfLightning": stats[Stat.BaseImmuneToShock] = stats[Stat.ImmuneToShock] = 1; break;
            }
        }
    }

    sealed class TemporaryAuraCleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var (player, state) in TemporaryAuraStates)
            {
                foreach (ushort id in state.Active.Keys.ToArray()) RemoveTemporaryAura(player, state, id);
                player.Pools = BuildPoolsFor(player, player.Pools.Level);
                player.ApplyMovementSpeed();
            }
        }
    }
}
