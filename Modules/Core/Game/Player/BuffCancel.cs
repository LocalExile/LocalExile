using GameServer.Core;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static readonly Lazy<HashSet<ushort>> RemovableBuffRows = new(() =>
    {
        var set = new HashSet<ushort>();
        foreach (var row in ClientTables.TableOrEmpty("BuffDefinitions"))
            if (row.BoolOrNull("Removable") == true) set.Add((ushort)row.Index);
        return set;
    });

    /// <summary>Whether <c>BuffDefinitions</c> row <paramref name="row"/> is player-removable (right-click C→S 0x4b).</summary>
    public static bool IsRemovableBuff(ushort row) => RemovableBuffRows.Value.Contains(row);

    /// <summary>Generic removable player buff cancellation (C→S 0x4b, <c>GameEvents.BuffCancel</c>): matches active
    /// player buffs whose <c>BuffDefinitions.Removable</c> flag is true, clears their state, recalculates pools/stats
    /// when needed, and emits S→C 0x1c2. Unremovable debuffs/buffs are rejected.</summary>
    public static async Task<bool> CancelRemovableBuff(ClientState conn, ushort buffInstance)
    {
        if (buffInstance == 0 || !CurrentGameWork || conn.Closed || conn.LeavingArea) return false;
        var (remote, stream, ciphers) = conn.Wire;

        // 1. Grace Period (row 6, Removable = 1).
        if (conn.EntryProtection.CurrentBuff == buffInstance && IsRemovableBuff(GracePeriod.BuffRow))
        {
            if (stream is not null) await EndGracePeriod(remote, stream, ciphers, conn, action: true);
            else conn.EntryProtection.End(conn.Clock.Elapsed.TotalSeconds, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), action: true);
            return true;
        }

        // 2. Triggered/cast guard buff (Steelskin 1302, Immortal Call 110, Molten Shell 114; all Removable = 1).
        bool guardCancelled = false, guardHasSession = false;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (CurrentTriggeredGuard(conn, now) is { Until: > 0 } guard && guard.Buff == buffInstance)
            {
                ushort guardRow = guard.Effect switch { "ImmortalCall" => 110, "MoltenShell" => 114, _ => 1302 };
                if (IsRemovableBuff(guardRow))
                {
                    guardHasSession = guard.Session is not null;
                    EndTriggeredGuard(conn, guard, now);
                    if (!guardHasSession) guard.Buff = 0;
                    guardCancelled = true;
                }
            }
        }
        if (guardCancelled)
        {
            if (!guardHasSession && stream is not null)
            {
                await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                    InstanceEntities.RemoveBuff(PlayerId, buffInstance), "0x01c2 guard buff cancelled");
                await SendCharacterStats(remote, stream, ciphers, conn, "guard buff cancelled");
            }
            return true;
        }

        // 3. Vaal Immortal Call (row 109, Removable = 1).
        bool vicCancelled = false;
        lock (conn.Monsters)
        {
            if (VaalImmortalCalls.TryGetValue(conn, out var vic) && vic.Buff == buffInstance
                && IsRemovableBuff(VaalImmortalCallBuffRow))
            {
                VaalImmortalCalls.Remove(conn);
                vicCancelled = true;
            }
        }
        if (vicCancelled)
        {
            if (stream is not null)
                await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                    InstanceEntities.RemoveBuff(PlayerId, buffInstance), "0x01c2 Vaal Immortal Call cancelled");
            return true;
        }

        // 4. Vaal Discipline (row 160, Removable = 1).
        byte[]? vaalAuraRemove = null;
        lock (conn.Monsters)
        {
            if (VaalAuraStates.TryGetValue(conn, out var aura) && aura.Buff != 0
                && (aura.Buff == buffInstance || aura.Source == buffInstance) && IsRemovableBuff(160))
            {
                var bytes = new List<byte>(InstanceEntities.RemoveBuff(PlayerId, aura.Buff));
                if (aura.Source != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, aura.Source));
                aura.Buff = aura.Source = 0;
                aura.Until = 0;
                aura.Stat = null;
                ++aura.Token;
                vaalAuraRemove = [.. bytes];
            }
        }
        if (vaalAuraRemove is not null)
        {
            if (stream is not null)
            {
                using var fresh = new PlayerStatScope(conn, fresh: true);
                await Send(remote, stream, ciphers, [], "0x01c2 Vaal aura cancelled", atGate: () =>
                {
                    lock (conn.Monsters)
                    {
                        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired) return null;
                        return [.. vaalAuraRemove, .. PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn))];
                    }
                });
            }
            return true;
        }

        // 5. Temporary auras (Divine Blessing / Vaal Haste; all Removable = 1).
        bool tempAuraCancelled = false;
        lock (conn.Monsters)
        {
            if (TemporaryAuraStates.TryGetValue(conn, out var tempState))
                foreach (var (skillId, entry) in tempState.Active.ToArray())
                    if ((entry.Aura.Buff == buffInstance || entry.Aura.SourceBuff == buffInstance)
                        && IsRemovableBuff(entry.Aura.BuffRow))
                    {
                        RemoveTemporaryAura(conn, tempState, skillId);
                        tempState.StatsPending = false;
                        conn.Pools = BuildPoolsFor(conn, conn.Pools.Level);
                        conn.ApplyMovementSpeed();
                        tempAuraCancelled = true;
                        break;
                    }
        }
        if (tempAuraCancelled)
        {
            if (stream is not null)
            {
                await SendOwnAuraChanges(remote, stream, ciphers, conn);
                await SendCharacterStats(remote, stream, ciphers, conn, "temporary aura cancelled");
            }
            return true;
        }

        // 6. Player toggled own auras (Determination, Grace, Discipline, etc.; Removable = 1).
        AuraCatalog.Aura? ownAuraEnded = null;
        lock (conn.Monsters)
        {
            if (conn.Auras.Values.FirstOrDefault(a => (a.Buff == buffInstance || a.SourceBuff == buffInstance)
                && IsRemovableBuff(a.BuffRow)) is { } active && conn.Auras.Remove(active.SkillId))
            {
                ownAuraEnded = active;
                EndReservationRuntimeLocked(conn, active);
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
                conn.AuraStatsDirty = true;
                CharacterSaves.SetAuraActive(CharacterName(conn), active.EffectId, false);
            }
        }
        if (ownAuraEnded is not null)
        {
            if (stream is not null)
            {
                await SendOwnAuraChanges(remote, stream, ciphers, conn);
                await SendCharacterStats(remote, stream, ciphers, conn, $"aura {ownAuraEnded.Name} cancelled");
                await SendPlayerMana(remote, stream, ciphers, conn,
                    $"0x01b1 player mana {conn.PlayerMana}/{conn.Pools.Mana}, {ReservedMana(conn)} reserved");
            }
            return true;
        }

        // 7. Petrified Blood (row 1819, Removable = 1).
        bool petrifiedCancelled = false;
        lock (conn.Monsters)
        {
            if (conn.PetrifiedBlood.Active && conn.PetrifiedBlood.Buff == buffInstance
                && IsRemovableBuff(PetrifiedBlood.BuffRow))
            {
                conn.PetrifiedBlood.Suspend();
                conn.AuraStatsDirty = true;
                CharacterSaves.SetPetrifiedBlood(CharacterName(conn), false);
                petrifiedCancelled = true;
            }
        }
        if (petrifiedCancelled)
        {
            if (stream is not null)
            {
                await SendOwnAuraChanges(remote, stream, ciphers, conn);
                await SendPlayerMana(remote, stream, ciphers, conn,
                    $"0x01b1 player mana {conn.PlayerMana}/{conn.Pools.Mana}, {ReservedMana(conn)} reserved (Petrified Blood cancelled)");
            }
            return true;
        }

        // 8. Arcane Surge (row 895, Removable = 1).
        bool arcaneCancelled = false;
        lock (conn.Monsters)
        {
            var sg = conn.SupportGems;
            if (sg.PresentedArcaneSurge == buffInstance && IsRemovableBuff(ArcaneSurgeBuffRow))
            {
                sg.ArcaneSurgeUntil = 0;
                sg.ArcaneSurgeShownUntil = 0;
                sg.ArcaneSurgeCastSpeedPct = 0;
                sg.ArcaneSurgeManaRegenPct = 0;
                sg.ArcaneSurgeBuff = 0;
                sg.PresentedArcaneSurge = 0;
                sg.ArcaneSurgeRefresh = false;
                sg.PendingStats = false;
                RecomputePoolsAndSpeed(conn);
                arcaneCancelled = true;
            }
        }
        if (arcaneCancelled)
        {
            if (stream is not null)
            {
                using var fresh = new PlayerStatScope(conn, fresh: true);
                await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                    InstanceEntities.RemoveBuff(PlayerId, buffInstance), "0x01c2 Arcane Surge cancelled");
                await SendCharacterStats(remote, stream, ciphers, conn, "Arcane Surge cancelled");
            }
            return true;
        }

        // 9. Timed Phasing buff (row 336, Removable = 1) when not held by a permanent flask/source.
        bool phasingCancelled = false;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (conn.PhasingBuff == buffInstance && IsRemovableBuff(PhasingBuffRow.Value)
                && !conn.SourcePhasing && !conn.Phasing && !conn.Reservations.CatPhasing && conn.PhasingUntil > now)
            {
                conn.PhasingUntil = 0;
                phasingCancelled = true;
            }
        }
        if (phasingCancelled)
        {
            if (stream is not null) await SyncPhasing(remote, stream, ciphers, conn);
            else lock (conn.Monsters) { conn.PhasingBuff = 0; conn.PhasingSentUntil = 0; }
            return true;
        }

        // 10. Lightning Golem Wrath buff (row 676, Removable = 1).
        bool wrathCancelled = false;
        lock (conn.Monsters)
        {
            if (conn.Golems.Buffs.TryGetValue(676, out var wrath) && wrath.Instance == buffInstance && IsRemovableBuff(676))
            {
                conn.Golems.Buffs.Remove(676);
                if (conn.Golems.All.TryGetValue(wrath.Source, out var golem)) golem.WrathUntil = 0;
                RecomputePoolsAndSpeed(conn);
                wrathCancelled = true;
            }
        }
        if (wrathCancelled)
        {
            if (stream is not null)
            {
                using var fresh = new PlayerStatScope(conn, fresh: true);
                await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                    InstanceEntities.RemoveBuff(PlayerId, buffInstance), "0x01c2 Lightning Golem Wrath cancelled");
                await SendCharacterStats(remote, stream, ciphers, conn, "Lightning Golem Wrath cancelled");
            }
            return true;
        }

        return false;
    }
}
