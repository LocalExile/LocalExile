using GameServer.Core;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static HashSet<ushort> RemovableBuffRows = [];

    /// <summary>Loads removable <c>BuffDefinitions</c> rows at startup (<c>GameModule.LoadData</c>).</summary>
    public static void LoadBuffCancelData()
    {
        var set = new HashSet<ushort>();
        foreach (var row in ClientTables.TableOrEmpty("BuffDefinitions"))
            if (row.BoolOrNull("Removable") == true) set.Add((ushort)row.Index);
        RemovableBuffRows = set;
    }

    /// <summary>Whether <c>BuffDefinitions</c> row <paramref name="row"/> is player-removable (right-click C→S 0x4b).</summary>
    public static bool IsRemovableBuff(ushort row) => RemovableBuffRows.Contains(row);

    /// <summary>Generic removable player buff cancellation (C→S 0x4b, <c>GameEvents.BuffCancel</c>): resolves the wire
    /// instance in <c>player.Area.Buffs</c>, verifies <c>BuffDefinitions.Removable</c>, clears the owning rule's state
    /// and its <c>AreaBuffs</c> entry, and refreshes pools/stats. Non-removable debuffs/buffs are rejected.</summary>
    public static async Task<bool> CancelRemovableBuff(Player player, ushort buffInstance)
    {
        if (buffInstance == 0 || !CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0)
            return false;
        bool publish = player.Connection.Connected || player.Area.Owned;
        Func<Task>? postPublish = null;
        bool cancelled;
        using (ProjectileMutation(player))
            cancelled = MutateLocked();
        if (!cancelled) return false;
        if (postPublish is not null) await postPublish();
        return true;

        bool MutateLocked()
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0) return false;
            double now = AreaTime(player);
            var activeBuff = player.Area.Buffs.Find(player.Id, buffInstance);
            // Any live AreaBuffs entry whose BuffDefinitions row is not Removable is rejected immediately.
            if (activeBuff is null || !IsRemovableBuff(activeBuff.Row)) return false;
            if (CancelCooldownTriggerLocked(player, activeBuff))
            {
                if (publish) postPublish = () => SendOwnAuraChanges(player);
                return true;
            }
            if (CancelCorruptingFeverLocked(player, activeBuff)) return true;
            if (CancelPlagueBearerLocked(player, activeBuff)) return true;
            if (activeBuff is { Row: HungerOfGhorrRow } && activeBuff.Source == player.Id)
            {
                ClearGhorrHunger(player);
                return true;
            }
            if (activeBuff.Source == player.Id && CancelPactBoon(player, activeBuff.Row)) return true;

            // 1. Grace Period (row 6, Removable = 1).
            if (activeBuff is { Row: GracePeriod.BuffRow } && activeBuff.Source == player.Id
                && player.EntryProtection.CurrentBuff is not null)
            {
                if (player.EntryProtection.End(now, player.Walk.Position(now), action: true) is ushort)
                    player.Area.Buffs.Clear(player.Id, GracePeriod.BuffRow, player.Id);
                return true;
            }

            // 2. Triggered/cast guard buff (Steelskin 1302, Immortal Call 110, Molten Shell 114; all Removable = 1).
            if (activeBuff.Source == player.Id
                && CurrentTriggeredGuard(player, now) is { Until: > 0 } guard
                && activeBuff.Row == GuardRow(guard.Effect))
            {
                bool guardHasSession = guard.Session is not null;
                EndTriggeredGuard(player, guard, now, triggerExplosion: guard.Effect == VaalMoltenShellEffect);
                if (!guardHasSession)
                {
                    guard.Buff = 0;
                    guard.Armour = 0;
                    player.Area.Buffs.Clear(player.Id, activeBuff.Row, player.Id);
                    player.Pools = PoolsFor(player, player.Pools.Level);
                    if (publish) postPublish = async () =>
                    {
                        await SendCharacterStats(player, PlayerStatChange.Sources);
                        await SendNativeCooldownRefresh(player);
                    };
                }
                return true;
            }

            // 3. Vaal Immortal Call (row 109, Removable = 1; owned by player.Area.Buffs).
            if (activeBuff is { Row: VaalImmortalCallBuffRow } && activeBuff.Source == player.Id
                && VaalImmortalCalls.TryGetValue(player, out _))
            {
                VaalImmortalCalls.Remove(player);
                player.Area.Buffs.Clear(player.Id, VaalImmortalCallBuffRow, player.Id);
                return true;
            }

            // 4. Temporary auras (Divine Blessing / Vaal Haste / Smite; all Removable = 1).
            if (activeBuff.Tag is 0 or (ushort)BuffTag.BloodAndSand or (ushort)BuffTag.VaalAura
                && activeBuff.Source == player.Id
                && TemporaryAuraStates.TryGetValue(player, out var tempState))
            {
                foreach (var (skillId, entry) in tempState.Active.ToArray())
                {
                    if (entry.Aura.BuffRow != activeBuff.Row
                        || OwnAuraBuffTag(entry.Aura) != activeBuff.Tag
                        || activeBuff.SourceSkill != 0 && entry.Aura.SkillId != activeBuff.SourceSkill)
                        continue;
                    RemoveTemporaryAura(player, tempState, skillId);
                    tempState.StatsPending = false;
                    player.Pools = BuildPoolsFor(player, player.Pools.Level);
                    player.ApplyMovementSpeed();
                    player.Area.Buffs.Clear(player.Id, entry.Aura.BuffRow, player.Id, tag: activeBuff.Tag);
                    player.Area.Buffs.Clear(player.Id, entry.Aura.BuffRow, player.Id, auraSource: true, tag: activeBuff.Tag);
                    if (publish) postPublish = async () =>
                    {
                        await SendOwnAuraChanges(player);
                        await SendCharacterStats(player, PlayerStatChange.Auras);
                    };
                    return true;
                }
            }

            // 5. Player toggled own auras (Determination, Grace, Discipline, etc.; Removable = 1).
            if (activeBuff.Tag is 0 or (ushort)BuffTag.BloodAndSand && activeBuff.Source == player.Id
                && player.Auras.Values.FirstOrDefault(a => a.BuffRow == activeBuff.Row
                    && OwnAuraBuffTag(a) == activeBuff.Tag
                    && (activeBuff.SourceSkill == 0 || a.SkillId == activeBuff.SourceSkill)) is { } ownAura
                && player.Auras.Remove(ownAura.SkillId))
            {
                EndReservationRuntimeLocked(player, ownAura);
                if (StanceCatalog.IsStance(ownAura.EffectId))
                    ReconcileStanceSelectionLocked(player, persist: true);
                if (GuardianBlessingTimes.TryGetValue(player, out var guardianTiming)
                    && guardianTiming.Skill == ownAura.SkillId)
                    GuardianBlessingTimes.Remove(player);
                bool wasEternal = EternalBlessingAura(ownAura);
                player.Pools = PoolsFor(player, player.Pools.Level);
                player.ApplyMovementSpeed();
                player.AuraStatsDirty = true;
                CharacterSaves.SetAuraActive(CharacterName(player), ownAura.EffectId, false);
                if (wasEternal)
                    ReconcileOwnAuras(player, validateSkills: true);
                ClampReservedResources(player);
                player.Area.Buffs.Clear(player.Id, ownAura.BuffRow, player.Id, tag: activeBuff.Tag);
                player.Area.Buffs.Clear(player.Id, ownAura.BuffRow, player.Id, auraSource: true, tag: activeBuff.Tag);
                if (publish) postPublish = async () =>
                {
                    await SendOwnAuraChanges(player);
                    await SendCharacterStats(player, PlayerStatChange.Auras);
                    await SendPlayerMana(player,
                        $"0x01b1 player mana {player.PlayerMana}/{player.Pools.Mana}, {ReservedMana(player)} reserved");
                };
                return true;
            }

            // 7. Petrified Blood (row 1819, Removable = 1).
            if (activeBuff.Row == PetrifiedBlood.BuffRow && activeBuff.Source == player.Id
                && player.PetrifiedBlood.Active)
            {
                player.PetrifiedBlood.Suspend();
                player.Pools = PoolsFor(player, player.Pools.Level);
                player.ApplyMovementSpeed();
                ClampReservedResources(player);
                player.AuraStatsDirty = true;
                CharacterSaves.SetPetrifiedBlood(CharacterName(player), false);
                player.Area.Buffs.Clear(player.Id, PetrifiedBlood.BuffRow, player.Id);
                if (publish) postPublish = async () =>
                {
                    await SendOwnAuraChanges(player);
                    await SendCharacterStats(player, PlayerStatChange.Auras);
                    await SendPlayerMana(player,
                        $"0x01b1 player mana {player.PlayerMana}/{player.Pools.Mana}, {ReservedMana(player)} reserved (Petrified Blood cancelled)");
                };
                return true;
            }

            // 8. Arcane Surge (row 895, Removable = 1).
            if (activeBuff.Row == ArcaneSurgeBuffRow && activeBuff.Source == player.Id
                && (player.SupportGems.ArcaneSurgeActiveAt(now) || player.SupportGems.PresentedArcaneSurge != 0))
            {
                var sg = player.SupportGems;
                sg.ArcaneSurgeUntil = 0;
                sg.ArcaneSurgeShownUntil = 0;
                sg.ArcaneSurgeCastSpeedPct = 0;
                sg.ArcaneSurgeManaRegenPct = 0;
                sg.ArcaneSurgeBuff = 0;
                sg.PresentedArcaneSurge = 0;
                sg.ArcaneSurgeRefresh = false;
                sg.PendingStats = false;
                player.Area.Buffs.Clear(player.Id, ArcaneSurgeBuffRow, player.Id);
                RecomputePoolsAndSpeed(player);
                if (publish) postPublish = () => SendCharacterStats(player, PlayerStatChange.Sources);
                return true;
            }

            // 9. Timed Phasing buff (row 336, Removable = 1) when not held by a permanent flask/source.
            if (activeBuff.Row == PhasingBuffRow && activeBuff.Source == player.Id
                && !player.SourcePhasing && !player.Phasing && !player.Reservations.CatPhasing
                && player.PhasingUntil > now)
            {
                player.PhasingUntil = 0;
                postPublish = () => SyncPhasing(player);
                return true;
            }

            // 10. Lightning Golem Wrath buff (row 676, Removable = 1).
            if (activeBuff.Row == 676
                && player.Golems.Buffs.TryGetValue(676, out var wrath) && wrath.Source == activeBuff.Source)
            {
                if (player.Golems.All.TryGetValue(wrath.Source, out var golem)) golem.WrathUntil = 0;
                using (new PlayerStatScope(player, fresh: true))
                    PlanGolemBuffsLocked(player, new Dictionary<uint, int>(CharacterPlayerStats(player)));
                if (publish) postPublish = () => SendCharacterStats(player, PlayerStatChange.Minions);
                return true;
            }

            // 11. Lifetap (row 1846, Removable = 1).
            if (activeBuff.Row == Lifetap.BuffRow && activeBuff.Source == player.Id
                && player.Lifetap.Until > 0)
            {
                player.Lifetap.Until = 0;
                player.Area.Buffs.Clear(player.Id, Lifetap.BuffRow, player.Id);
                if (player.RighteousFire is { ConditionalSupportMore.Lifetap: not 0 } rf)
                    RecordRighteousFireSupportChangeLocked(player, rf, now);
                return true;
            }

            return false;
        }
    }
}
