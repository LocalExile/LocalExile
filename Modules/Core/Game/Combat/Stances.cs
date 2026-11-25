using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public sealed record StanceSourceSnapshot(PlayerSkillSource Source, SupportedGem Supported,
        SkillCatalog.ReservationCost Reservation);

    public sealed record StanceAdmissionPlan(StanceSourceSnapshot Source, AuraCatalog.Aura Aura,
        AuraCatalog.Aura[] ProposedAuras, uint ManaAfter, uint LifeAfter, bool Changed, int ProjectileEpoch,
        double AcceptedAt);

    /// <summary>
    /// The accepted stance use owns its exact source identity, module, action and projectile epoch. A later source
    /// replacement or invalidation suppresses delayed presentation/acknowledgement without erasing the committed event.
    /// </summary>
    public sealed record StanceUseReceipt(Player Owner, GameServer.Modding.IModuleLifetime Rules,
        PlayerSkillSource Source, AuraCatalog.Aura Aura, ushort SkillId, int Effect, int ActionToken, int ProjectileEpoch,
        double AcceptedAt, string Selected, bool Changed)
    {
        public bool Current(Player player)
        {
            if (!ReferenceEquals(Owner, player)) return false;
            return CurrentLocked(player);
        }

        public bool CurrentLocked(Player player) =>
            ReferenceEquals(Owner, player) && CurrentGameRules && ReferenceEquals(Rules, CombatLifetime)
            && Rules.IsActive && player.ProjectileActive(ProjectileEpoch) && !player.Closed && !player.LeavingArea
            && !player.RageRetired && player.PlayerLife != 0 && !player.Paused && player.PendingTransfer is null
            && player.ActionCount == ActionToken
            && CurrentStanceSourceLocked(player, SkillId, Effect) is { } current
            && current.Source.Identity == Source.Identity
            && player.Auras.TryGetValue(SkillId, out var currentAura)
            && currentAura.StanceOwner is { } currentOwner
            && Aura.StanceOwner is { } acceptedOwner
            && ReferenceEquals(currentOwner, acceptedOwner);
    }

    /// <summary>
    /// Resolve the exact current gem/requirement/source snapshot for a stance use. This is deliberately stricter than
    /// finding an aura-catalog row or merely checking that some item still exists.
    /// </summary>
    public static StanceSourceSnapshot? CurrentStanceSourceLocked(Player player, ushort skillId, int effect)
    {
        var equipment = ItemsOf(player).EquipmentSnapshot();
        var requirements = CurrentGemRequirements(player, equipment);
        var source = PlayerSkillSourcesAt(CharacterName(player), equipment, requirements.Inactive, LeagueFor(player),
                GrantedSkillsOf(player), WornGrantedSkillsOf(player, equipment))
            .FirstOrDefault(s => s.Id == skillId && s.Effect == effect && s.Active);
        if (source is null || !StanceCatalog.IsSource(SkillsFor(player).EffectId(effect))) return null;
        var supported = SupportedSkillAt(player, skillId, requirements);
        if (supported.Effect != effect || !supported.RequirementsMet
            || GemSkillInstanceDisabled(player, effect, skillId) is not null
            || SupportedReservationCost(player, skillId) is not { } reservation) return null;
        return new(source, supported, reservation);
    }

    /// <summary>
    /// Build the complete state change before a cooldown or action is spent. Applying the returned plan cannot refuse.
    /// </summary>
    public static bool TryPlanStanceLocked(Player player, ushort skillId, int effect, double acceptedAt,
        out StanceAdmissionPlan? plan)
    {
        plan = null;
        if (!CurrentGameRules || CombatLifetime is not { IsActive: true } || !player.ProjectileActive(player.ProjectileEpoch)
            || player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0 || player.Paused
            || InTown(player) || OwnAuraRestrictionValue(player) != 0 || CastingSpell(player, effect) && SpellsDisabled(player)
            || CurrentStanceSourceLocked(player, skillId, effect) is not { } source) return false;
        string sourceEffectId = SkillsFor(player).EffectId(effect) ?? "";
        var current = player.Auras.Values.FirstOrDefault(a => StanceCatalog.IsStance(a.EffectId)
            && StanceCatalog.SourceFamily(a.EffectId) == StanceCatalog.SourceFamily(sourceEffectId));
        if (current is not null && current.SkillId != skillId) return false;
        bool changed = current is not null;
        string selected = changed ? StanceCatalog.Toggle(StanceCatalog.Selected(player.Auras.Values))
            : StanceCatalog.Selected(player.Auras.Values);
        var aura = StanceAura(player, skillId, effect, source.Source.Name, selected, source.Reservation,
            source.Supported.Level, source.Supported.Quality);
        var proposedAuras = !changed
            ? [.. player.Auras.Values, aura]
            : player.Auras.Values.Select(a => a.SkillId == skillId ? aura
                : StanceCatalog.IsStance(a.EffectId) ? StanceSelection(a, selected) : a).ToArray();
        aura = proposedAuras.Single(a => a.SkillId == skillId);
        long otherMana = (long)PetrifiedBlood.RequiredMana(player.Pools, player.PetrifiedBlood)
            + PlacedSkills.Reserved(player.Pools, player.Placed.All.Values, false)
            + CooldownTriggerReservation(player, life: false);
        long currentMana = AuraCatalog.RequiredMana(player.Pools, player.Auras.Values) + otherMana;
        long proposedMana = AuraCatalog.RequiredMana(player.Pools, proposedAuras) + otherMana;
        var currentLife = LifeReservationFor(player, player.Auras.Values);
        var proposedLife = LifeReservationFor(player, proposedAuras);
        long additionalMana = Math.Max(0L, (long)proposedMana - currentMana);
        long additionalLife = Math.Max(0L, (long)proposedLife.Required - currentLife.Required);
        if (proposedMana > player.Pools.Mana || proposedLife.Required >= player.Pools.Life
            || additionalMana > player.PlayerMana
            || additionalLife > 0 && additionalLife >= player.PlayerLife) return false;
        uint manaRemaining = player.PlayerMana >= additionalMana
            ? (uint)Math.Min(player.PlayerMana - additionalMana, player.Pools.Mana - (uint)proposedMana)
            : 0;
        uint lifeRemaining = player.PlayerLife >= additionalLife
            ? (uint)Math.Min(player.PlayerLife - additionalLife, proposedLife.Unreserved)
            : 0;
        plan = new(source, aura, proposedAuras, manaRemaining, lifeRemaining, changed,
            player.ProjectileEpoch, acceptedAt);
        return true;
    }

    public static StanceUseReceipt ApplyStancePlanLocked(Player player, ushort skillId,
        StanceAdmissionPlan plan, int action, int projectEpoch)
    {
        var proposed = plan.ProposedAuras.Select(a =>
        {
            bool replaced = ReferenceEquals(a, plan.Aura) || a.SkillId == skillId;
            bool switched = plan.Changed && StanceCatalog.IsStance(a.EffectId);
            if (!replaced && !switched) return a;
            return a with
            {
                Buff = player.NextBuff++,
                SourceBuff = a.SourceBuff is not null ? player.NextBuff++ : null,
                StanceOwner = new object(),
            };
        }).ToArray();
        foreach (var old in player.Auras.Values.Where(a => StanceCatalog.IsStance(a.EffectId)
                     && proposed.All(p => p.SkillId != a.SkillId)).ToArray())
            EndReservationRuntimeLocked(player, old);
        foreach (var aura in proposed.Where(a => StanceCatalog.IsStance(a.EffectId))) player.Auras[aura.SkillId] = aura;
        var committed = player.Auras[skillId];
        player.Stance = plan.Aura.Stance ?? StanceCatalog.Selected(proposed);
        player.PlayerMana = plan.ManaAfter;
        player.PlayerLife = plan.LifeAfter;
        if (plan.Changed) player.RecentEvents.Record(StatConditions.ChangedStance, plan.AcceptedAt);
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        player.AuraStatsDirty = true;
        foreach (var aura in proposed.Where(a => StanceCatalog.IsStance(a.EffectId)))
            CharacterSaves.SetAuraActive(CharacterName(player), aura.EffectId, true);
        CharacterSaves.SetActiveStance(CharacterName(player), player.Stance);
        return new(player, CombatLifetime!, plan.Source.Source, committed, skillId, plan.Source.Source.Effect, action,
            plan.ProjectileEpoch, plan.AcceptedAt, player.Stance, plan.Changed);
    }

    public static void ReconcileStanceSelectionLocked(Player player, bool persist)
    {
        bool owned = player.Auras.Values.Any(a => StanceCatalog.IsStance(a.EffectId));
        player.Stance = owned ? StanceCatalog.Selected(player.Auras.Values) : StanceCatalog.BloodSelection;
        if (persist) CharacterSaves.SetActiveStance(CharacterName(player), owned ? player.Stance : null);
    }

    static AuraCatalog.Aura StanceAura(Player player, ushort skillId, int effect, string name, string selected,
        SkillCatalog.ReservationCost reservation, int level, int quality)
    {
        var supported = SupportedSkill(player, skillId);
        var (supportEffect, affectsSelf) = AuraSupport(player, skillId);
        var context = ReservationContext(player, skillId);
        return StanceAura(skillId, effect, SkillsFor(player).EffectId(effect), selected, name,
            reservation, level, quality, supported, supportEffect, affectsSelf, context);
    }

    static AuraCatalog.Aura StanceAura(ushort skillId, int effect, string? sourceEffectId, string selected, string name,
        SkillCatalog.ReservationCost reservation, int level, int quality, SupportedGem supported,
        int supportEffect, bool affectsSelf, (bool Life, int Efficiency) context)
    {
        string effectId = StanceCatalog.RuntimeEffectId(sourceEffectId, selected);
        return new(skillId, effect, effectId, name, 0, AuraCatalog.BuffRow(effectId, selected),
            reservation.ManaFlat, reservation.ManaPercent, level, quality,
            LifeFlat: reservation.LifeFlat, LifePercent: reservation.LifePercent,
            SourceBuff: 0, WornGrant: supported.WornGrant,
            SkillStats: [.. supported.Stats.Select(kv => (kv.Key, kv.Value))],
            SupportEffectPct: supportEffect, AffectsSelf: affectsSelf, Stance: selected,
            ReservesLife: context.Life, ReservationEfficiencyPct: context.Efficiency);
    }

    static AuraCatalog.Aura StanceSelection(AuraCatalog.Aura aura, string selected) =>
        aura with
        {
            EffectId = StanceCatalog.RuntimeEffectId(aura.EffectId, selected),
            BuffRow = AuraCatalog.BuffRow(StanceCatalog.RuntimeEffectId(aura.EffectId, selected), selected),
            Stance = selected,
        };

    public static async Task<bool> PublishStanceUse(Player player, StanceUseReceipt receipt, string name)
    {
        if (!receipt.Current(player)) return false;
        await SendOwnAuraChanges(player);
        if (!receipt.Current(player)) return false;
        await SendCharacterStats(player, PlayerStatChange.Auras);
        if (!receipt.Current(player)) return false;
        await SendPlayerMana(player,
            $"0x01b1 player mana {player.PlayerMana}/{player.Pools.Mana}, {ReservedMana(player)} reserved");
        return receipt.Current(player);
    }

    /// <summary>
    /// Blood and Sand's own melee-area modifiers. The two unconditional output stats are materialized once from the
    /// active gem's exact GEPL/constant-source rows, scaled by the gem's buff effect, and remain isolated to melee area
    /// attacks so bow and spell skills cannot inherit them.
    /// </summary>
    public static void ApplyBloodAndSandMeleeStats(Player player, int skillEffect, Dictionary<uint, int> own)
    {
        if (!SkillsFor(player).IsAttack(skillEffect) || !SkillsFor(player).IsMelee(skillEffect)
            || Supports.Of(skillEffect)?.Types.Contains(7) != true) return;
        var owner = player.Auras.Values.FirstOrDefault(a => a.AffectsSelf
            && StanceCatalog.SourceFamily(a.EffectId) == StanceCatalog.BloodAndSand);
        if (owner is null) return;
        var source = SupportedSkill(player, owner.SkillId);
        if (skillEffect == source.Effect) return;
        int Value(uint key) => source.Stats.GetValueOrDefault(key);
        bool blood = StanceCatalog.SelectionOf(owner) == StanceCatalog.BloodSelection;
        int damage = Value(Stat.BloodSandStanceMeleeSkillsAreaDamagePlusPctFinal)
            + Value(blood ? Stat.BloodSandStanceMeleeSkillsAreaDamagePlusPctFinalInBloodStance
                : Stat.BloodSandStanceMeleeSkillsAreaDamagePlusPctFinalInSandStance);
        int area = Value(Stat.BloodSandStanceMeleeSkillsAreaOfEffectPlusPctFinal)
            + Value(blood ? Stat.BloodSandStanceMeleeSkillsAreaOfEffectPlusPctFinalInBloodStance
                : Stat.BloodSandStanceMeleeSkillsAreaOfEffectPlusPctFinalInSandStance);
        double buffEffect = Math.Max(0, 1 + Value(Stat.BloodSandStanceBuffEffectPlusPct) / 100.0);
        Set(Stat.BloodSandStanceMeleeSkillsAreaDamagePlusPctFinal, damage, buffEffect);
        Set(Stat.BloodSandStanceMeleeSkillsAreaOfEffectPlusPctFinal, area, buffEffect);
        void Set(uint key, int value, double scale)
        {
                own[key] = (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
        }
    }
}
