using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public const string UnhingeEffect = "Unhinge";
    public const string UnhingeGrant = "GrantsUnhingeUnique__1";
    public const string UnhingedCriticalFinal = "critical_strike_chance_+%_final_while_unhinged";
    public const string SaneDamageFinal = "physical_and_chaos_damage_taken_+%_final_while_not_unhinged";
    public const string SaneHitRecovery = "regenerate_%_life_over_1_second_when_hit_while_not_unhinged";
    public const string UnhingedCorpseDestroy = "enemies_explode_on_kill_while_unhinged";
    public const ushort UnhingedBuffRow = 1723;

    /// <summary>
    /// Resolve the live equipped grant and reconcile source ownership. Unhinge itself starts sane; toggling changes only
    /// this connection-owned state. A changed owner, host/mod/GEPL identity, inactive item, mode refusal or death retires
    /// the toggle and queues its native buff for removal.
    /// </summary>
    static UnhingedState UnhingedAt(ClientState conn, double now)
    {
        lock (conn.Monsters)
        {
            var state = conn.Unhinged;
            var owner = ResourceEffectsOf(conn);
            if (!ResourceEffectsCurrent(conn, owner))
            {
                RetireUnhinged(conn);
                return state;
            }
            var equipment = ItemsOf(conn).EquipmentSnapshot();
            var grant = AllWornGrantedSkillsOf(conn, equipment)
                .Select(g => (WornGrantedSkills.Grant?)g)
                .FirstOrDefault(g => g is { } candidate
                    && candidate.EffectId == UnhingeEffect && candidate.ModId == UnhingeGrant
                    && candidate.EffectsPerLevelRow == 69315 && !candidate.Support
                    && !IsInactive(conn, candidate.Host)
                    && LeagueFor(conn).Rules.AllowsGemEffect(candidate.EffectId));
            if (grant is not { } matched)
            {
                RetireUnhinged(conn);
                return state;
            }
            if (!ReferenceEquals(state.Owner, owner) || state.Source != matched)
            {
                if (state.Retire() is ushort old and not 0) conn.RetiredUnhingedBuffs.Add(old);
                state.Install(owner, matched);
            }
            return state;
        }
    }

    static void RetireUnhinged(ClientState conn)
    {
        if (conn.Unhinged.Retire() is ushort buff and not 0) conn.RetiredUnhingedBuffs.Add(buff);
    }

    public static bool Unhinged(ClientState conn)
    {
        lock (conn.Monsters) return UnhingedAt(conn, ResourceEffectsTime(conn)).Active;
    }

    /// <summary>The exact raw sane Damage Taken final for one component. It has no effect on elemental damage.</summary>
    public static int UnhingedPhysicalChaosTakenPct(DamageType type, Func<string, int> get) =>
        type is DamageType.Physical or DamageType.Chaos ? get(SaneDamageFinal) : 0;

    /// <summary>Apply the sane physical/chaos final once, after ordinary mitigation/increased taken.</summary>
    public static int ApplyUnhingedTaken(int damage, DamageType type, Func<string, int> get) =>
        (int)Math.Clamp((long)Math.Max(0, damage) * Math.Max(0L, 100L + UnhingedPhysicalChaosTakenPct(type, get)) / 100,
            0, int.MaxValue);

    /// <summary>
    /// Accepted hit contact while sane starts or refreshes one non-stacking one-second regeneration contribution. This
    /// runs after block/avoidance/range admission, so a fully absorbed or blocked hit still recovers and a miss does not.
    /// </summary>
    public static void UnhingedHitRecoveryLocked(ClientState conn, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (conn.PlayerLife == 0) return;
        var state = UnhingedAt(conn, now);
        if (!state.Exists || state.Enabled) return;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        int percent = Get(SaneHitRecovery);
        if (percent <= 0) return;
        double perSecond = conn.PlayerMaxLife * (double)percent / 100
            * PanelStats.LifeRegenerationMultiplier(Get);
        if (perSecond > 0) state.HitRecovery.Begin(now, 1, perSecond);
    }

    public static bool UnhingedCorpseDestruction(ClientState conn)
    {
        if (!Unhinged(conn)) return false;
        var stats = CharacterPlayerStats(conn);
        return Stats.Key(UnhingedCorpseDestroy) is uint key && stats.GetValueOrDefault(key) != 0;
    }

    /// <summary>Freeze the item decision as part of the lethal hit mutation, before hit notifications can yield.</summary>
    public static void CaptureUnhingedHitKillLocked(ClientState conn, SpawnedMonster m, bool playerHit)
    {
        if (m.Life == 0)
            m.UnhingedHitKill = new(conn, playerHit && UnhingedCorpseDestruction(conn));
    }

    /// <summary>Consume the decision for this exact monster and owner. No current-state fallback is allowed.</summary>
    public static bool TakeUnhingedHitKillLocked(ClientState conn, SpawnedMonster m)
    {
        var receipt = m.UnhingedHitKill;
        if (receipt is null || !ReferenceEquals(receipt.Owner, conn)) return false;
        m.UnhingedHitKill = null;
        return receipt.DestroyCorpse;
    }

    public static Task PublishUnhingedCorpseDestruction(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint id, SpawnedMonster m, bool committed)
    {
        if (!committed) return Task.CompletedTask;
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || m.CorpseConsumed
                || !conn.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)) return Task.CompletedTask;
            m.CorpseConsumed = true;
        }
        // Item text: "Enemies Killed by your Hits are destroyed while Insane." The extraction supplies only the boolean,
        // so remove the accepted hit's corpse without inventing damage, another kill event, loot roll or experience award.
        // `committed` is the qualification captured at the lethal hit: later source/toggle changes cannot rewrite it.
        return SendWorldRemoval(remote, stream, ciphers, conn, id,
            $"0x02bb corpse of 0x{id:x} destroyed while unhinged", still: () =>
            {
                lock (conn.Monsters)
                    return CurrentGameWork && !conn.Closed && !conn.LeavingArea
                        && conn.Monsters.TryGetValue(id, out var current) && ReferenceEquals(current, m);
            });
    }

    public static async Task<bool> ToggleUnhinged(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, int effect, Func<bool> current)
    {
        bool toggled = false;
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, [], "0x01c1 player buff unhinged toggled", atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!current()) return null;
                var actor = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
                if (actor.Effect != effect || SkillEffectId(conn, skillId) != UnhingeEffect) return null;
                double now = ResourceEffectsTime(conn);
                var state = UnhingedAt(conn, now);
                if (!state.Exists || state.Source is not { } source || IsInactive(conn, source.Host)) return null;
                bool enabled = state.Toggle();
                toggled = true;
                if (enabled)
                {
                    state.Buff = conn.NextBuff++;
                    return InstanceEntities.AddBuff(PlayerId, state.Buff, UnhingedBuffRow, PlayerId);
                }
                ushort buff = state.Buff;
                state.Buff = 0;
                return buff == 0 ? [] : InstanceEntities.RemoveBuff(PlayerId, buff);
            }
        });
        return toggled;
    }

    public static Task SendUnhinged(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        SendWorldRecords(remote, stream, ciphers, conn, PlayerId, () => UnhingedPresentationRecords(conn));

    public static byte[]? UnhingedPresentation(ClientState conn) =>
        UnhingedPresentationRecords(conn) is { } records ? [.. records.SelectMany(p => p)] : null;

    static byte[][]? UnhingedPresentationRecords(ClientState conn)
    {
        lock (conn.Monsters)
        {
            var bytes = new List<byte[]>();
            foreach (ushort buff in conn.RetiredUnhingedBuffs)
                bytes.Add(InstanceEntities.RemoveBuff(PlayerId, buff));
            conn.RetiredUnhingedBuffs.Clear();
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired)
                return bytes.Count == 0 ? null : [.. bytes];
            double now = ResourceEffectsTime(conn);
            var state = UnhingedAt(conn, now);
            foreach (ushort buff in conn.RetiredUnhingedBuffs)
                bytes.Add(InstanceEntities.RemoveBuff(PlayerId, buff));
            conn.RetiredUnhingedBuffs.Clear();
            if (!state.Exists || !state.Enabled) return bytes.Count == 0 ? null : [.. bytes];
            if (state.Buff == 0)
            {
                state.Buff = conn.NextBuff++;
                bytes.Add(InstanceEntities.AddBuff(PlayerId, state.Buff, UnhingedBuffRow, PlayerId));
            }
            return bytes.Count == 0 ? null : [.. bytes];
        }
    }
}
