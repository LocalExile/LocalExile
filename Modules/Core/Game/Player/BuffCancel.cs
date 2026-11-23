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
        if (buffInstance == 0 || !CurrentGameWork || player.Closed || player.LeavingArea) return false;
        double now = AreaTime(player);
        bool publish = player.Connection.Connected || player.Area.Owned;
        var activeBuff = player.Area.Buffs.Find(player.Id, buffInstance);
        // Any live AreaBuffs entry whose BuffDefinitions row is not Removable is rejected immediately.
        if (activeBuff is not null && !IsRemovableBuff(activeBuff.Row)) return false;

        // 1. Grace Period (row 6, Removable = 1).
        if (activeBuff is { Row: GracePeriod.BuffRow } && activeBuff.Source == player.Id
            && player.EntryProtection.CurrentBuff is not null)
        {
            await EndGracePeriod(player, action: true);
            return true;
        }

        // 2. Triggered/cast guard buff (Steelskin 1302, Immortal Call 110, Molten Shell 114; all Removable = 1).
        if (activeBuff is not null && activeBuff.Source == player.Id
            && CurrentTriggeredGuard(player, now) is { Until: > 0 } guard
            && activeBuff.Row == GuardRow(guard.Effect))
        {
            bool guardHasSession = guard.Session is not null;
            EndTriggeredGuard(player, guard, now);
            if (!guardHasSession)
            {
                guard.Buff = 0;
                player.Area.Buffs.Clear(player.Id, activeBuff.Row, player.Id);
                if (publish) await SendCharacterStats(player, PlayerStatChange.Sources);
            }
            return true;
        }

        // 3. Vaal Immortal Call (row 109, Removable = 1; published directly via InstanceEntities.AddBuff).
        if (VaalImmortalCalls.TryGetValue(player, out var vic)
            && (vic.Buff == buffInstance || activeBuff is { Row: VaalImmortalCallBuffRow })
            && IsRemovableBuff(VaalImmortalCallBuffRow))
        {
            VaalImmortalCalls.Remove(player);
            if (activeBuff is not null)
                player.Area.Buffs.Clear(player.Id, VaalImmortalCallBuffRow, player.Id);
            else if (publish)
                await SendWorldRecord(player, player.Id,
                    InstanceEntities.RemoveBuff(player.Id, buffInstance), "0x01c2 Vaal Immortal Call cancelled");
            return true;
        }

        if (activeBuff is null) return false;

        // 4. Vaal auras (Vaal Discipline row 160, etc.; Removable = 1).
        if (activeBuff.Tag == (ushort)BuffTag.VaalAura && activeBuff.Source == player.Id
            && VaalAuraStates.TryGetValue(player, out var vaalAura) && vaalAura.Buff != 0
            && vaalAura.Row == activeBuff.Row)
        {
            ClearVaalAuraBuffs(player, vaalAura);
            vaalAura.Buff = vaalAura.Source = 0;
            vaalAura.Until = 0;
            vaalAura.Stat = null;
            ++vaalAura.Token;
            if (publish) await SendCharacterStats(player, PlayerStatChange.Auras);
            return true;
        }

        // 5. Temporary auras (Divine Blessing / Vaal Haste / Smite; all Removable = 1).
        if (activeBuff.Tag == 0 && activeBuff.Source == player.Id
            && TemporaryAuraStates.TryGetValue(player, out var tempState))
        {
            foreach (var (skillId, entry) in tempState.Active.ToArray())
            {
                if (entry.Aura.BuffRow != activeBuff.Row
                    || activeBuff.SourceSkill != 0 && entry.Aura.SkillId != activeBuff.SourceSkill)
                    continue;
                RemoveTemporaryAura(player, tempState, skillId);
                tempState.StatsPending = false;
                player.Pools = BuildPoolsFor(player, player.Pools.Level);
                player.ApplyMovementSpeed();
                player.Area.Buffs.Clear(player.Id, entry.Aura.BuffRow, player.Id);
                player.Area.Buffs.Clear(player.Id, entry.Aura.BuffRow, player.Id, auraSource: true);
                if (publish)
                {
                    await SendOwnAuraChanges(player);
                    await SendCharacterStats(player, PlayerStatChange.Auras);
                }
                return true;
            }
        }

        // 6. Player toggled own auras (Determination, Grace, Discipline, etc.; Removable = 1).
        if (activeBuff.Tag == 0 && activeBuff.Source == player.Id
            && player.Auras.Values.FirstOrDefault(a => a.BuffRow == activeBuff.Row
                && (activeBuff.SourceSkill == 0 || a.SkillId == activeBuff.SourceSkill)) is { } ownAura
            && player.Auras.Remove(ownAura.SkillId))
        {
            EndReservationRuntimeLocked(player, ownAura);
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.ApplyMovementSpeed();
            player.AuraStatsDirty = true;
            CharacterSaves.SetAuraActive(CharacterName(player), ownAura.EffectId, false);
            player.Area.Buffs.Clear(player.Id, ownAura.BuffRow, player.Id);
            player.Area.Buffs.Clear(player.Id, ownAura.BuffRow, player.Id, auraSource: true);
            if (publish)
            {
                await SendOwnAuraChanges(player);
                await SendCharacterStats(player, PlayerStatChange.Auras);
                await SendPlayerMana(player,
                    $"0x01b1 player mana {player.PlayerMana}/{player.Pools.Mana}, {ReservedMana(player)} reserved");
            }
            return true;
        }

        // 7. Petrified Blood (row 1819, Removable = 1).
        if (activeBuff.Row == PetrifiedBlood.BuffRow && activeBuff.Source == player.Id
            && player.PetrifiedBlood.Active)
        {
            player.PetrifiedBlood.Suspend();
            player.AuraStatsDirty = true;
            CharacterSaves.SetPetrifiedBlood(CharacterName(player), false);
            player.Area.Buffs.Clear(player.Id, PetrifiedBlood.BuffRow, player.Id);
            if (publish)
            {
                await SendOwnAuraChanges(player);
                await SendCharacterStats(player, PlayerStatChange.Auras);
                await SendPlayerMana(player,
                    $"0x01b1 player mana {player.PlayerMana}/{player.Pools.Mana}, {ReservedMana(player)} reserved (Petrified Blood cancelled)");
            }
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
            if (publish) await SendCharacterStats(player, PlayerStatChange.Sources);
            return true;
        }

        // 9. Timed Phasing buff (row 336, Removable = 1) when not held by a permanent flask/source.
        if (activeBuff.Row == PhasingBuffRow && activeBuff.Source == player.Id
            && !player.SourcePhasing && !player.Phasing && !player.Reservations.CatPhasing
            && player.PhasingUntil > now)
        {
            player.PhasingUntil = 0;
            await SyncPhasing(player);
            return true;
        }

        // 10. Lightning Golem Wrath buff (row 676, Removable = 1).
        if (activeBuff.Row == 676
            && player.Golems.Buffs.TryGetValue(676, out var wrath) && wrath.Source == activeBuff.Source)
        {
            player.Golems.Buffs.Remove(676);
            if (player.Golems.All.TryGetValue(wrath.Source, out var golem)) golem.WrathUntil = 0;
            player.Area.Buffs.Clear(player.Id, 676, wrath.Source);
            RecomputePoolsAndSpeed(player);
            if (publish) await SendCharacterStats(player, PlayerStatChange.Minions);
            return true;
        }

        return false;
    }
}
