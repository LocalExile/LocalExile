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
    public sealed record StanceUseReceipt(ClientState Owner, GameServer.Modding.IModuleLifetime Rules,
        PlayerSkillSource Source, AuraCatalog.Aura Aura, ushort SkillId, int Effect, int ActionToken, int ProjectileEpoch,
        double AcceptedAt, string Selected, bool Changed)
    {
        public bool Current(ClientState conn)
        {
            if (!ReferenceEquals(Owner, conn)) return false;
            lock (conn.Monsters) return CurrentLocked(conn);
        }

        public bool CurrentLocked(ClientState conn) =>
            ReferenceEquals(Owner, conn) && CurrentGameRules && ReferenceEquals(Rules, CombatLifetime)
            && Rules.IsActive && conn.ProjectileActive(ProjectileEpoch) && !conn.Closed && !conn.LeavingArea
            && !conn.RageRetired && conn.PlayerLife != 0 && !conn.Paused && conn.PendingTransfer is null
            && conn.ActionCount == ActionToken
            && CurrentStanceSourceLocked(conn, SkillId, Effect) is { } current
            && current.Source.Identity == Source.Identity
            && conn.Auras.TryGetValue(SkillId, out var currentAura)
            && currentAura.StanceOwner is { } currentOwner
            && Aura.StanceOwner is { } acceptedOwner
            && ReferenceEquals(currentOwner, acceptedOwner);
    }

    /// <summary>
    /// Resolve the exact current gem/requirement/source snapshot for a stance use. This is deliberately stricter than
    /// finding an aura-catalog row or merely checking that some item still exists.
    /// </summary>
    public static StanceSourceSnapshot? CurrentStanceSourceLocked(ClientState conn, ushort skillId, int effect)
    {
        var equipment = ItemsOf(conn).EquipmentSnapshot();
        var requirements = CurrentGemRequirements(conn, equipment);
        var source = PlayerSkillSourcesAt(CharacterName(conn), equipment, requirements.Inactive, LeagueFor(conn),
                GrantedSkillsOf(conn), WornGrantedSkillsOf(conn, equipment))
            .FirstOrDefault(s => s.Id == skillId && s.Effect == effect && s.Active);
        if (source is null || !StanceCatalog.IsSource(SkillsFor(conn).EffectId(effect))) return null;
        var supported = SupportedSkillAt(conn, skillId, requirements);
        if (supported.Effect != effect || !supported.RequirementsMet
            || GemSkillInstanceDisabled(conn, effect, skillId) is not null
            || SupportedReservationCost(conn, skillId) is not { } reservation) return null;
        return new(source, supported, reservation);
    }

    /// <summary>
    /// Build the complete state change before a cooldown or action is spent. Applying the returned plan cannot refuse.
    /// </summary>
    public static bool TryPlanStanceLocked(ClientState conn, ushort skillId, int effect, double acceptedAt,
        out StanceAdmissionPlan? plan)
    {
        plan = null;
        if (!CurrentGameRules || CombatLifetime is not { IsActive: true } || !conn.ProjectileActive(conn.ProjectileEpoch)
            || conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0 || conn.Paused
            || InTown(conn) || OwnAuraRestrictionValue(conn) != 0 || CastingSpell(conn, effect) && SpellsDisabled(conn)
            || CurrentStanceSourceLocked(conn, skillId, effect) is not { } source) return false;
        string sourceEffectId = SkillsFor(conn).EffectId(effect) ?? "";
        var current = conn.Auras.Values.FirstOrDefault(a => StanceCatalog.IsStance(a.EffectId)
            && StanceCatalog.SourceFamily(a.EffectId) == StanceCatalog.SourceFamily(sourceEffectId));
        if (current is not null && current.SkillId != skillId) return false;
        bool changed = current is not null;
        string selected = changed ? StanceCatalog.Toggle(StanceCatalog.Selected(conn.Auras.Values))
            : StanceCatalog.Selected(conn.Auras.Values);
        var aura = StanceAura(conn, skillId, effect, source.Source.Name, selected, source.Reservation,
            source.Supported.Level, source.Supported.Quality);
        var proposedAuras = !changed
            ? [.. conn.Auras.Values, aura]
            : conn.Auras.Values.Select(a => a.SkillId == skillId ? aura
                : StanceCatalog.IsStance(a.EffectId) ? StanceSelection(a, selected) : a).ToArray();
        aura = proposedAuras.Single(a => a.SkillId == skillId);
        int currentMana = AuraCatalog.RequiredMana(conn.Pools, conn.Auras.Values)
            + PetrifiedBlood.RequiredMana(conn.Pools, conn.PetrifiedBlood)
            + PlacedSkills.Reserved(conn.Pools, conn.Placed.All.Values, false);
        int proposedMana = AuraCatalog.RequiredMana(conn.Pools, proposedAuras)
            + PetrifiedBlood.RequiredMana(conn.Pools, conn.PetrifiedBlood)
            + PlacedSkills.Reserved(conn.Pools, conn.Placed.All.Values, false);
        var currentLife = LifeReservationFor(conn, conn.Auras.Values);
        var proposedLife = LifeReservationFor(conn, proposedAuras);
        long additionalMana = Math.Max(0L, (long)proposedMana - currentMana);
        long additionalLife = Math.Max(0L, (long)proposedLife.Required - currentLife.Required);
        if (proposedMana > conn.Pools.Mana || proposedLife.Required >= conn.Pools.Life
            || additionalMana > conn.PlayerMana
            || additionalLife > 0 && additionalLife >= conn.PlayerLife) return false;
        uint manaRemaining = conn.PlayerMana >= additionalMana
            ? (uint)Math.Min(conn.PlayerMana - additionalMana, conn.Pools.Mana - (uint)proposedMana)
            : 0;
        uint lifeRemaining = conn.PlayerLife >= additionalLife
            ? (uint)Math.Min(conn.PlayerLife - additionalLife, proposedLife.Unreserved)
            : 0;
        plan = new(source, aura, proposedAuras, manaRemaining, lifeRemaining, changed,
            conn.ProjectileEpoch, acceptedAt);
        return true;
    }

    public static StanceUseReceipt ApplyStancePlanLocked(ClientState conn, ushort skillId,
        StanceAdmissionPlan plan, int action, int projectEpoch)
    {
        var proposed = plan.ProposedAuras.Select(a =>
        {
            bool replaced = ReferenceEquals(a, plan.Aura) || a.SkillId == skillId;
            bool switched = plan.Changed && StanceCatalog.IsStance(a.EffectId);
            if (!replaced && !switched) return a;
            return a with
            {
                Buff = conn.NextBuff++,
                SourceBuff = a.SourceBuff is not null ? conn.NextBuff++ : null,
                StanceOwner = new object(),
            };
        }).ToArray();
        foreach (var old in conn.Auras.Values.Where(a => StanceCatalog.IsStance(a.EffectId)
                     && proposed.All(p => p.SkillId != a.SkillId)).ToArray())
            EndReservationRuntimeLocked(conn, old);
        foreach (var aura in proposed.Where(a => StanceCatalog.IsStance(a.EffectId))) conn.Auras[aura.SkillId] = aura;
        var committed = conn.Auras[skillId];
        conn.Stance = plan.Aura.Stance ?? StanceCatalog.Selected(proposed);
        conn.PlayerMana = plan.ManaAfter;
        conn.PlayerLife = plan.LifeAfter;
        if (plan.Changed) conn.RecentEvents.Record(StatConditions.ChangedStance, plan.AcceptedAt);
        conn.Pools = PoolsFor(conn, conn.Pools.Level);
        conn.ApplyMovementSpeed();
        conn.AuraStatsDirty = true;
        foreach (var aura in proposed.Where(a => StanceCatalog.IsStance(a.EffectId)))
            CharacterSaves.SetAuraActive(CharacterName(conn), aura.EffectId, true);
        CharacterSaves.SetActiveStance(CharacterName(conn), conn.Stance);
        return new(conn, CombatLifetime!, plan.Source.Source, committed, skillId, plan.Source.Source.Effect, action,
            plan.ProjectileEpoch, plan.AcceptedAt, conn.Stance, plan.Changed);
    }

    public static void ReconcileStanceSelectionLocked(ClientState conn, bool persist)
    {
        bool owned = conn.Auras.Values.Any(a => StanceCatalog.IsStance(a.EffectId));
        conn.Stance = owned ? StanceCatalog.Selected(conn.Auras.Values) : StanceCatalog.BloodSelection;
        if (persist) CharacterSaves.SetActiveStance(CharacterName(conn), owned ? conn.Stance : null);
    }

    static AuraCatalog.Aura StanceAura(ClientState conn, ushort skillId, int effect, string name, string selected,
        SkillCatalog.ReservationCost reservation, int level, int quality)
    {
        var supported = SupportedSkill(conn, skillId);
        var (supportEffect, affectsSelf) = AuraSupport(conn, skillId);
        var context = ReservationContext(conn, skillId);
        return StanceAura(skillId, effect, SkillsFor(conn).EffectId(effect), selected, name,
            reservation, level, quality, supported, supportEffect, affectsSelf, context);
    }

    static AuraCatalog.Aura StanceAura(ushort skillId, int effect, string? sourceEffectId, string selected, string name,
        SkillCatalog.ReservationCost reservation, int level, int quality, SupportedGem supported,
        int supportEffect, bool affectsSelf, (bool Life, int Efficiency) context)
    {
        string effectId = StanceCatalog.RuntimeEffectId(sourceEffectId, selected);
        return new(skillId, effect, effectId, name, 0, AuraCatalog.BuffRow(effectId, selected),
            reservation.ManaFlat, reservation.ManaPercent, reservation.LifeFlat, reservation.LifePercent,
            level, quality, SourceBuff: 0, WornGrant: supported.WornGrant,
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

    public static async Task<bool> PublishStanceUse(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, StanceUseReceipt receipt, string name)
    {
        if (!receipt.Current(conn)) return false;
        await SendOwnAuraChanges(remote, stream, ciphers, conn);
        if (!receipt.Current(conn)) return false;
        await SendCharacterStats(remote, stream, ciphers, conn,
            receipt.Changed ? $"stance {receipt.Selected}" : $"stance owner {name} on (default {receipt.Selected})");
        if (!receipt.Current(conn)) return false;
        await SendPlayerMana(remote, stream, ciphers, conn,
            $"0x01b1 player mana {conn.PlayerMana}/{conn.Pools.Mana}, {ReservedMana(conn)} reserved");
        return receipt.Current(conn);
    }

    /// <summary>
    /// Blood and Sand's own melee-area modifiers. The two unconditional output stats are materialized once from the
    /// active gem's exact GEPL/constant-source rows, scaled by the gem's buff effect, and remain isolated to melee area
    /// attacks so bow and spell skills cannot inherit them.
    /// </summary>
    public static void ApplyBloodAndSandMeleeStats(ClientState conn, int skillEffect, Dictionary<uint, int> own)
    {
        if (!SkillsFor(conn).IsAttack(skillEffect) || !SkillsFor(conn).IsMelee(skillEffect)
            || Supports.Of(skillEffect)?.Types.Contains(7) != true) return;
        var owner = conn.Auras.Values.FirstOrDefault(a => a.AffectsSelf
            && StanceCatalog.SourceFamily(a.EffectId) == StanceCatalog.BloodAndSand);
        if (owner is null) return;
        var source = SupportedSkill(conn, owner.SkillId);
        if (skillEffect == source.Effect) return;
        int Value(string stat) => Stats.Key(stat) is uint key ? source.Stats.GetValueOrDefault(key) : 0;
        bool blood = StanceCatalog.SelectionOf(owner) == StanceCatalog.BloodSelection;
        int damage = Value("blood_sand_stance_melee_skills_area_damage_+%_final")
            + Value(blood ? "blood_sand_stance_melee_skills_area_damage_+%_final_in_blood_stance"
                : "blood_sand_stance_melee_skills_area_damage_+%_final_in_sand_stance");
        int area = Value("blood_sand_stance_melee_skills_area_of_effect_+%_final")
            + Value(blood ? "blood_sand_stance_melee_skills_area_of_effect_+%_final_in_blood_stance"
                : "blood_sand_stance_melee_skills_area_of_effect_+%_final_in_sand_stance");
        double buffEffect = Math.Max(0, 1 + Value("blood_sand_stance_buff_effect_+%") / 100.0);
        Set("blood_sand_stance_melee_skills_area_damage_+%_final", damage, buffEffect);
        Set("blood_sand_stance_melee_skills_area_of_effect_+%_final", area, buffEffect);
        void Set(string target, int value, double scale)
        {
            if (Stats.Key(target) is uint key)
                own[key] = (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
        }
    }
}
