using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

// Runs on Game's minion schedule. Neither a worker nor a cipher is retained by this module.
internal sealed partial class HarbingerGameplay(Func<DateTimeOffset>? utcNow = null)
{
    static double Now(ClientState conn) => conn.ProjectileRuntimeState is { Active: true } runtime ? runtime.NowTicks / 120000d : conn.Clock.Elapsed.TotalSeconds;
    DateTimeOffset UtcNow() => utcNow?.Invoke() ?? DateTimeOffset.UtcNow;
    readonly ConditionalWeakTable<ClientState, State> states = new();
    readonly HarbingerCompanionCatalog normal = new(skills: Skills);
    readonly HarbingerCompanionCatalog hard = new(skills: HardModeSkills.Value);
    sealed class State(ClientState conn, DateTimeOffset attached)
    {
        internal readonly IModuleLifetime? Rules = CombatLifetime;
        internal readonly HarbingerSession Session = new(HarbingerRoster.For(CharacterName(conn)), Guid.NewGuid(), attached);
        internal readonly Dictionary<long, uint> Actors = [];
        internal readonly Dictionary<(long, int, double), ushort> Presented = [];
        internal Pending? Cast;
        internal CurseCast? CurseCast;
        internal readonly HarbingerCurses Curses = new();
        internal ushort? PresentedSelf;
        internal (int Slow, int OtherEffects) PresentedSelfStats;
        internal bool Closed;
    }
    sealed record Pending(HarbingerCompanionCatalog.Companion Source, ushort Skill, double At, Func<bool> Current);
    HarbingerCompanionCatalog Catalog(ClientState conn) => LeagueFor(conn).HardMode ? hard : normal;
    static bool Enabled(ClientState conn) => conn.MainAscendancy.Selection is
        { Family: AscendancyFamily.Phrecia, Id: "Occultist" } selection
        && AscendancyCatalog.IsKnownRevision(selection.Revision)
        && AscendancyPolicies.SourceOwner(conn) is not null;
    State Get(ClientState conn)
    {
        if (states.TryGetValue(conn, out var previous) && !ReferenceEquals(previous.Rules, CombatLifetime))
        {
            Close(conn);
            states.Remove(conn);
        }
        return states.GetValue(conn, c => new(c, UtcNow()));
    }
    internal bool Executable(ClientState conn, string effect) => Enabled(conn)
        && (effect == "TemporalChains" || Catalog(conn).ForNodes(conn.MainAscendancy.Allocated).Any(s => SkillsFor(conn).EffectId(s.SummonEffect) == effect));
    static HarbingerSession.Frame Frame(ClientState conn, IReadOnlyDictionary<uint, int> raw)
    {
        int Get(string name) => Stats.Key(name) is uint k ? conn.PlayerStats.GetValueOrDefault(k, raw.GetValueOrDefault(k)) : 0;
        var receipt = conn.PaidChannel;
        return new(conn.ProjectileEpoch, conn.PlayerLife > 0, conn.Paused, conn.Closed || conn.LeavingArea,
            !InTown(conn), conn.MainAscendancy.Allocated.ToArray(), HarbingerRecipientClock.RecipientRate(1, Get),
            receipt is null ? null : new(receipt.Skill, receipt.Action, receipt.Generation));
    }
    internal bool? Cast(ClientState conn, string? effect, ushort skill, (int X, int Y) from, Func<bool> current)
    {
        if (effect is null || !Executable(conn, effect)) return null;
        if (effect == "TemporalChains") return CastCurse(conn, skill, from, current);
        lock (conn.Monsters)
        {
            if (!current() || conn.Paused || conn.PlayerLife == 0) return false;
            var source = Catalog(conn).ForNodes(conn.MainAscendancy.Allocated)
                .Single(s => SkillsFor(conn).EffectId(s.SummonEffect) == effect);
            int? duration = CastMs(conn, source.SummonEffect, skill);
            if (duration is null) return false;
            Get(conn).Cast = new(source, skill, Now(conn) + duration.Value / 1000d, current);
            return true;
        }
    }
    internal Dictionary<uint, int> StatsFor(ClientState conn, Dictionary<uint, int> raw)
    {
        if (!Enabled(conn)) return raw;
        lock (conn.Monsters)
        {
            // Admission computes pools before vitals are restored. Its initial zero is not a death or a roster lease.
            if (conn.PlayerLife == 0 && !conn.PlayerObjectCreated) return raw;
            var state = Get(conn);
            if (conn.PlayerLife == 0) { state.Curses.Clear(); state.CurseCast = null; state.Cast = null; }
            WithSelfCurse(conn, state, raw);
            var frame = Frame(conn, raw);
            foreach (var (k, v) in state.Session.RecipientStats(frame, UtcNow()))
                raw[k] = (int)Math.Clamp((long)raw.GetValueOrDefault(k) + v, int.MinValue, int.MaxValue);
            foreach (var source in Catalog(conn).All)
                if (source.CountStat is uint count)
                    raw[count] = conn.MainAscendancy.Allocated.Contains(source.Node) && state.Actors.Keys.Any(id => state.Actors.TryGetValue(id, out uint actor)
                        && conn.Minions.All.Any(m => m.Id == actor && m.Skill == SkillsFor(conn).EffectId(source.SummonEffect))) ? 1 : 0;
            return raw;
        }
    }
    internal void Pause(ClientState conn, bool paused)
    {
        lock (conn.Monsters)
            if (states.TryGetValue(conn, out var state)) state.Session.Pause(UtcNow(), paused);
    }
    internal void Close(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!states.TryGetValue(conn, out var state) || state.Closed) return;
            state.Closed = true;
            state.Cast = null;
            state.CurseCast = null;
            foreach (var change in state.Curses.Retire())
            {
                if (!conn.Monsters.TryGetValue(change.Target.Id, out var monster)
                    || !ReferenceEquals(monster, change.Target.Identity)) continue;
                double now = Now(conn);
                double next = RecipientEffectTime.Rate(2, name => monster.Mods.Effects[name]);
                monster.Ignites.SetExpiryRate(now, next);
                monster.Bleeds.SetExpiryRate(now, next);
                monster.Poisons.SetExpiryRate(now, next);
                monster.Ailments.SetExpiryRate(now, next);
                monster.TauntedUntil = RecipientEffectTime.RetimedDeadline(monster.TauntedUntil, now, monster.RecipientDebuffRate, next);
                monster.RecipientDebuffRate = next;
                monster.ReceivedActionUntil = now;
                monster.ReceivedActionFactor = 1;
                if (change.Previous is { } old)
                    conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(change.Target.Id, old.Instance), "curse retired"));
                var clear = new List<(uint, int)>();
                foreach (string name in new[] { "buff_time_passed_+%_other_than_temporal_chains", "temporal_chains_action_speed_+%_final" })
                    if (Stats.Key(name) is uint k) clear.Add((k, monster.Mods.Effects[name]));
                conn.Minions.OutputQueue.Add(new(InstanceEntities.StatsUpdate(change.Target.Id, clear), "curse recipient retired"));
            }
            if (state.PresentedSelf is ushort self)
                conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, self), "self curse retired"));
            state.PresentedSelf = null;
            foreach (ushort instance in state.Presented.Values)
                conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, instance), "companion retired"));
            state.Presented.Clear();
            conn.Minions.StatRevision++;
            foreach (uint actor in state.Session.Retire(conn.TransferAccepted && conn.PlayerLife > 0, UtcNow()))
            {
                conn.Minions.Remove(actor);
                conn.Monsters.Remove(actor);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(actor), "companion retired"));
            }
        }
    }
    internal async Task Tick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double now)
    {
        if (!Enabled(conn)) { Close(conn); return; }
        State state;
        HarbingerSession.Observation observation;
        bool Current() => Enabled(conn) && !conn.Closed && !conn.LeavingArea && !conn.Paused;
        lock (conn.Monsters)
        {
            state = Get(conn);
            if (state.Closed || !Current()) return;
            var raw = CharacterStatSources(conn).Stats;
            var frame = Frame(conn, raw);
            if (state.Cast is { } cast && now >= cast.At)
            {
                state.Cast = null;
                bool Accepted() => cast.Current() && GemSkillEffects(conn).Any(g => g.Id == cast.Skill && g.Effect == cast.Source.SummonEffect)
                    && GemSkillInstanceDisabled(conn, cast.Source.SummonEffect, cast.Skill) is null;
                state.Session.Activate(cast.Source, frame, UtcNow(), Accepted, Stats.Key, Math.Max(0, now - cast.At));
            }
            observation = state.Session.Observe(frame, UtcNow(),
                receipt => conn.PaidChannel is { } paid && paid.Skill == receipt.Skill && paid.Action == receipt.Action
                    && paid.Generation == receipt.Generation && PaidChannelCurrent(conn, paid), allowStart: false);
        }
        bool OwnedCurrent() => Current() && !state.Closed && ReferenceEquals(state.Rules, CombatLifetime);
        foreach (var member in observation.Members)
        {
            if (state.Actors.ContainsKey(member.Id)) continue;
            var at = NextToPlayer(conn);
            await SpawnMonster(remote, stream, ciphers, conn, member.Source.Monster, (byte)conn.Pools.Level,
                MonsterRarity.Normal, at.X, at.Y, friendly: true,
                ownedMaximumLife: 1,
                extraStats: HarbingerActor.Stats(member.Source, Stats.Key), ownedSkills: HarbingerActor.Skills(member.Source, SkillsFor(conn)),
                ownedCurrent: () => OwnedCurrent() && conn.PlayerLife > 0
                    && conn.MainAscendancy.Allocated.Contains(member.Source.Node) && state.Session.Contains(member.Id),
                owned: (id, monster) =>
                {
                    if (!state.Session.Bind(member.Id, id)) return;
                    monster.Life = 1;
                    conn.Minions.Add(new(id, monster, SkillsFor(conn).EffectId(member.Source.SummonEffect)!, member.Source.Type.ToString())
                        { Until = double.PositiveInfinity });
                    state.Actors[member.Id] = id;
                });
        }
        await Send(remote, stream, ciphers, [], "companion state", atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!OwnedCurrent()) return null;
                var raw = CharacterStatSources(conn).Stats;
                var frame = Frame(conn, raw);
                var fresh = state.Session.Observe(frame, UtcNow(),
                    _ => conn.PaidChannel is { } receipt && PaidChannelCurrent(conn, receipt));
                var bytes = new List<byte>();
                ReconcileCurses(conn, state, now, bytes);
                foreach (var change in fresh.Changes)
                    if (state.Actors.TryGetValue(change.Owner.Id, out uint caster)
                        && conn.Monsters.TryGetValue(caster, out var native))
                        foreach (var cast in change.Change.Started)
                        {
                            var at = native.Walk.Position(now); var target = conn.Walk.Position(now);
                            bytes.AddRange(InstanceProtocol.ActionStarted(caster, at.X, at.Y, target.X, target.Y,
                                HarbingerActor.SkillId(change.Owner.Source, SkillsFor(conn), cast.Skill.Effect),
                                conn.NextActionSequence(), conn.MonsterOrderFlags, targetObjectId: PlayerId));
                            bytes.AddRange(InstanceProtocol.ActionEnded(caster));
                        }
                foreach (uint actor in state.Session.PruneActors())
                {
                    conn.Minions.Remove(actor); conn.Monsters.Remove(actor);
                    foreach (var key in state.Actors.Where(p => p.Value == actor).Select(p => p.Key).ToArray()) state.Actors.Remove(key);
                    bytes.AddRange(InstanceProtocol.RemoveObject(actor));
                }
                var active = fresh.Buffs.Select(b => (b.Owner.Id, b.Buff.Skill.Effect, b.Buff.StartedAt)).ToHashSet();
                if (state.Presented.Keys.Any(key => !active.Contains(key)))
                {
                    conn.Pools = PoolsFor(conn, conn.Pools.Level);
                    conn.ApplyMovementSpeed();
                    bytes.AddRange(InstanceEntities.StatsUpdate(PlayerId, PlayerStatsWithCleared(conn)));
                }
                foreach (var (key, instance) in state.Presented.ToArray())
                    if (!active.Contains(key)) { bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, instance)); state.Presented.Remove(key); }
                foreach (var buff in fresh.Buffs)
                {
                    var key = (buff.Owner.Id, buff.Buff.Skill.Effect, buff.Buff.StartedAt);
                    if (state.Presented.ContainsKey(key)) continue;
                    ushort instance = conn.NextBuff++;
                    var packet = state.Session.BuffPacket(buff, frame.Epoch, frame, UtcNow(), instance,
                        HarbingerActor.SkillId(buff.Owner.Source, SkillsFor(conn), buff.Buff.Skill.Effect),
                        actor => conn.Minions.Owns(actor) && conn.Monsters.ContainsKey(actor),
                        () =>
                        {
                            conn.Pools = PoolsFor(conn, conn.Pools.Level);
                            conn.ApplyMovementSpeed();
                            return InstanceEntities.StatsUpdate(PlayerId, PlayerStatsWithCleared(conn));
                        },
                        () => (Frame(conn, CharacterStatSources(conn).Stats), UtcNow()),
                        () => state.Presented[key] = instance);
                    if (packet is null) continue;
                    bytes.AddRange(packet);
                }
                foreach (var (member, actor) in state.Actors)
                    if (conn.Monsters.TryGetValue(actor, out var m) && Dist(m.Walk.Position(now), conn.Walk.Position(now)) > 12)
                    {
                        var at = NextToPlayer(conn);
                        m.Walk.Teleport(at.X, at.Y, now);
                        bytes.AddRange(InstanceProtocol.Teleport(actor, at.X, at.Y));
                    }
                return [.. bytes];
            }
        });
    }
}
