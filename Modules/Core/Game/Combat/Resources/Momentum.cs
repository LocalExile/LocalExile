using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal static bool OrdinaryMomentumUse(ClientState conn, SupportedGem gem) => gem.RequirementsMet
        && SkillsFor(conn).IsAttack(gem.Effect) && Supports.Of(gem.Effect)?.Types.Contains(47) != true
        && Stats.Key("support_momentum_max_stacks") is uint key && gem.Stats.GetValueOrDefault(key) > 0;

    static void WithMomentum(ClientState conn, Dictionary<uint, int> stats)
    {
        if (conn.ResourceEffectsState is ResourceEffectsState state && ResourceEffectsCurrent(conn, state)
            && Stats.Key("base_current_momentum") is uint key) stats.TryAdd(key, state.Momentum.Count);
    }

    static int MomentumMovement(ClientState conn) => conn.PlayerLife > 0
        && conn.ResourceEffectsState is ResourceEffectsState state && ResourceEffectsCurrent(conn, state)
            ? state.Momentum.MovementAt(conn.Clock.Elapsed.TotalSeconds) : 0;

    static void MomentumPaid(ClientState conn, ushort skill, int effect, int action)
    {
        var gem = SupportedSkill(conn, skill);
        if (gem.Effect != effect || !OrdinaryMomentumUse(conn, gem)) return;
        var owner = ResourceEffectsOf(conn);
        if (!ResourceEffectsCurrent(conn, owner)) return;
        int Get(string name) => Stats.Key(name) is uint key ? gem.Stats.GetValueOrDefault(key) : 0;
        var player = CharacterPlayerStats(conn);
        int Player(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        double seconds = SkillDurationModifiers.Seconds(Get("support_momentum_base_buff_duration_ms"), Player, Get);
        owner.Momentum.Paid(new(action, skill, new(Get("support_momentum_max_stacks"), seconds,
            Get("support_momentum_movement_speed_+%_per_stack_removed"))), conn.Clock.Elapsed.TotalSeconds);
    }

    static void MomentumAcknowledged(ClientState conn, ushort skill, int effect)
    {
        if (!SkillsFor(conn).IsAttack(effect)) return;
        var gem = SupportedSkill(conn, skill);
        if (gem.Effect != effect || !OrdinaryMomentumUse(conn, gem))
        {
            if (conn.ResourceEffectsState is ResourceEffectsState existing && ResourceEffectsCurrent(conn, existing))
                existing.Momentum.Pending = null;
            return;
        }
        var state = ResourceEffectsOf(conn);
        if (ResourceEffectsCurrent(conn, state))
        {
            long before = state.Momentum.Revision;
            state.Momentum.Acknowledge(conn.ActionCount, skill, conn.Clock.Elapsed.TotalSeconds);
            // Called inside the action echo's send gate: queue its delta after that echo, before a later use.
            if (before != state.Momentum.Revision && conn.Wire.Stream is { } stream && conn.Wire.Ciphers is { } ciphers)
                _ = PublishAcknowledgedMomentum(conn.Wire.Remote, stream, ciphers, conn);
        }
    }

    static async Task PublishAcknowledgedMomentum(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        try { await SendMomentum(remote, stream, ciphers, conn); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    static void MomentumMoved(ClientState conn)
    {
        if (conn.ResourceEffectsState is ResourceEffectsState state && ResourceEffectsCurrent(conn, state)) state.Momentum.Moved();
    }

    // The existing 50 ms world pass publishes current state at the send gate, after the admitted action's echo.
    internal static Task SendMomentum(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        lock (conn.Monsters)
            if (conn.RetiredSkillBuffs.Count == 0 && (conn.ResourceEffectsState is not ResourceEffectsState owner
                || owner.Momentum.Revision == owner.Momentum.SentRevision
                    && (owner.Momentum.SwiftUntil == 0 || conn.Clock.Elapsed.TotalSeconds < owner.Momentum.SwiftUntil)))
                return Task.CompletedTask;
        return Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            using (ProjectileMutation(conn))
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || !conn.PlayerSent) return null;
                var packets = new List<byte>();
                bool retired = conn.RetiredSkillBuffs.Count != 0;
                foreach (ushort buff in conn.RetiredSkillBuffs) packets.AddRange(InstanceEntities.RemoveBuff(PlayerId, buff));
                conn.RetiredSkillBuffs.Clear();
                if (retired && Stats.Key("base_current_momentum") is uint retiredCount)
                    packets.AddRange(InstanceEntities.StatsUpdate(PlayerId, [(retiredCount, 0)]));
                var owner = conn.ResourceEffectsState as ResourceEffectsState;
                if (owner is null || !ResourceEffectsCurrent(conn, owner))
                {
                    if (!retired || conn.PlayerLife == 0) return packets.Count == 0 ? null : [.. packets];
                    owner = ResourceEffectsOf(conn);
                    if (!ResourceEffectsCurrent(conn, owner)) return packets.Count == 0 ? null : [.. packets];
                }
                var state = owner.Momentum;
                int movement = state.MovementAt(conn.Clock.Elapsed.TotalSeconds);
                if (state.Revision == state.SentRevision && !retired) return packets.Count == 0 ? null : [.. packets];
                if (state.Count == 0 && state.Buff != 0)
                { packets.AddRange(InstanceEntities.RemoveBuff(PlayerId, state.Buff)); state.Buff = 0; }
                else if (state.Count > 0 && state.Count != state.SentCount)
                {
                    if (state.Buff == 0) state.Buff = conn.NextBuff++;
                    packets.AddRange(InstanceEntities.AddBuff(PlayerId, state.Buff, 2220,
                        stacks: state.Count == 1 ? null : (ushort)state.Count));
                }
                if (state.SwiftUntil == 0 && state.SwiftBuff != 0)
                { packets.AddRange(InstanceEntities.RemoveBuff(PlayerId, state.SwiftBuff)); state.SwiftBuff = 0; }
                else if (state.SwiftUntil > 0 && state.SwiftUntil != state.SentSwiftUntil)
                {
                    if (state.SwiftBuff == 0) state.SwiftBuff = conn.NextBuff++;
                    packets.AddRange(InstanceEntities.AddBuff(PlayerId, state.SwiftBuff, 2221,
                        u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, state.SwiftUntil-conn.Clock.Elapsed.TotalSeconds))));
                }
                if (movement != state.SentMovement || retired)
                {
                    conn.Pools = PoolsFor(conn, conn.Pools.Level);
                    conn.ApplyMovementSpeed();
                    packets.AddRange(InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.MovementVelocityPct, conn.Pools.MovementVelocityPct)]));
                }
                if (Stats.Key("base_current_momentum") is uint count)
                    packets.AddRange(InstanceEntities.StatsUpdate(PlayerId, [(count, state.Count)]));
                // A later full refresh must not resend the pre-use count from an admission-scope cache hit.
                if (state.Count != state.SentCount || retired) conn.PlayerSkillsCache = null;
                if ((state.Count != state.SentCount || retired)
                    && Stats.Key("support_momentum_attack_speed_+%_per_stack") is uint perStack)
                    foreach (var skill in PlayerSkills(conn, (id, gem) => conn.ClientSkillIds.Contains(id)
                        && gem.Stats.GetValueOrDefault(perStack) != 0))
                    {
                        if (!conn.ClientSkillIds.Contains(skill.Id) || skill.Stats is not { } current
                            || !current.Any(v => v.Key == perStack && v.Value != 0)) continue;
                        var changed = current.Where(v => Stats.Name(v.Key) is string name
                            && (name is "current_momentum" or "main_hand_attack_speed_+%" or "off_hand_attack_speed_+%"
                                || name.Contains("per_second", StringComparison.Ordinal))).ToArray();
                        packets.AddRange(InstanceEntities.SkillStatsUpdate(PlayerId, skill.Id, changed));
                    }
                state.SentCount = state.Count; state.SentMovement = movement;
                state.SentSwiftUntil = state.SwiftUntil; state.SentRevision = state.Revision;
                return [.. packets];
            }
        });
    }
}
