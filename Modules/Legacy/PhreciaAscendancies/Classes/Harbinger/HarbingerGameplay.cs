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
    static double Now(Player player) => player.ProjectileRuntimeState is { Active: true } runtime ? runtime.NowTicks / 120000d : AreaTime(player);
    DateTimeOffset UtcNow() => utcNow?.Invoke() ?? DateTimeOffset.UtcNow;
    readonly ConditionalWeakTable<Player, State> states = new();
    readonly HarbingerCompanionCatalog normal = new(skills: Skills);
    readonly HarbingerCompanionCatalog hard = new(skills: HardModeSkills);
    sealed class State(Player player, DateTimeOffset attached)
    {
        internal readonly IModuleLifetime? Rules = CombatLifetime;
        internal readonly HarbingerSession Session = new(HarbingerRoster.For(CharacterName(player)), Guid.NewGuid(), attached);
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
    HarbingerCompanionCatalog Catalog(Player player) => LeagueFor(player).HardMode ? hard : normal;
    static bool Enabled(Player player) => player.Character.MainAscendancy.Selection is
        { Family: AscendancyFamily.Phrecia, Id: "Occultist" } selection
        && AscendancyCatalog.IsKnownRevision(selection.Revision)
        && AscendancyPolicies.SourceOwner(player) is not null;
    State Get(Player player)
    {
        if (states.TryGetValue(player, out var previous) && !ReferenceEquals(previous.Rules, CombatLifetime))
        {
            Close(player);
            states.Remove(player);
        }
        return states.GetValue(player, c => new(c, UtcNow()));
    }
    internal bool Executable(Player player, string effect) => Enabled(player)
        && (effect == "TemporalChains" || Catalog(player).ForNodes(player.Character.MainAscendancy.Allocated).Any(s => SkillsFor(player).EffectId(s.SummonEffect) == effect));
    static HarbingerSession.Frame Frame(Player player, IReadOnlyDictionary<uint, int> raw)
    {
        int Get(uint k) => raw.GetValueOrDefault(k);
        var receipt = player.PaidChannel;
        return new(player.ProjectileEpoch, player.PlayerLife > 0, player.Paused, player.Closed || player.LeavingArea,
            !InTown(player), player.Character.MainAscendancy.Allocated.ToArray(), HarbingerRecipientClock.RecipientRate(1, Get),
            receipt is null ? null : new(receipt.Skill, receipt.Action, receipt.Generation));
    }
    internal bool? Cast(Player player, string? effect, ushort skill, (int X, int Y) from, Func<bool> current)
    {
        if (effect is null || !Executable(player, effect)) return null;
        if (effect == "TemporalChains") return CastCurse(player, skill, from, current);
        if (!current() || player.Paused || player.PlayerLife == 0) return false;
        var source = Catalog(player).ForNodes(player.Character.MainAscendancy.Allocated)
            .Single(s => SkillsFor(player).EffectId(s.SummonEffect) == effect);
        int? duration = CastMs(player, source.SummonEffect, skill);
        if (duration is null) return false;
        Get(player).Cast = new(source, skill, Now(player) + duration.Value / 1000d, current);
        return true;
    }
    internal Dictionary<uint, int> StatsFor(Player player, Dictionary<uint, int> raw)
    {
        if (!Enabled(player)) return raw;
        // Admission computes pools before vitals are restored. Its initial zero is not a death or a roster lease.
        if (player.PlayerLife == 0 && !player.Connection.PlayerObjectCreated) return raw;
        var state = Get(player);
        if (player.PlayerLife == 0) { state.Curses.Clear(); state.CurseCast = null; state.Cast = null; }
        WithSelfCurse(player, state, raw);
        var frame = Frame(player, raw);
        foreach (var (k, v) in state.Session.RecipientStats(frame, UtcNow()))
            raw[k] = (int)Math.Clamp((long)raw.GetValueOrDefault(k) + v, int.MinValue, int.MaxValue);
        foreach (var source in Catalog(player).All)
            if (source.CountStat is uint count)
                raw[count] = player.Character.MainAscendancy.Allocated.Contains(source.Node) && state.Actors.Keys.Any(id => state.Actors.TryGetValue(id, out uint actor)
                    && player.Minions.All.Any(m => m.Id == actor && m.Skill == SkillsFor(player).EffectId(source.SummonEffect))) ? 1 : 0;
        return raw;
    }
    internal void Pause(Player player, bool paused)
    {
        if (states.TryGetValue(player, out var state)) state.Session.Pause(UtcNow(), paused);
    }
    internal void Close(Player player)
    {
        if (!states.TryGetValue(player, out var state) || state.Closed) return;
        state.Closed = true;
        state.Cast = null;
        state.CurseCast = null;
        foreach (var change in state.Curses.Retire())
        {
            if (!player.Area.Monsters.TryGetValue(change.Target.Id, out var monster)
                || !ReferenceEquals(monster, change.Target.Identity)) continue;
            double now = Now(player);
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
                player.Area.Buffs.ClearTag(change.Target.Id, old.Instance);
            var clear = new List<(uint, int)>();
            foreach (uint name in new[] { Stat.BuffTimePassedPlusPctOtherThanTemporalChains, Stat.TemporalChainsActionSpeedPlusPctFinal })
                clear.Add((name, monster.Mods.Effects[name]));
            SetStats(player, change.Target.Id, clear);
        }
        if (state.PresentedSelf is ushort self)
            player.Area.Buffs.ClearTag(player.Id, self);
        state.PresentedSelf = null;
        foreach (ushort instance in state.Presented.Values)
            player.Area.Buffs.ClearTag(player.Id, instance);
        state.Presented.Clear();
        player.Minions.StatRevision++;
        foreach (uint actor in state.Session.Retire(player.TransferAccepted && player.PlayerLife > 0, UtcNow()))
        {
            player.Minions.Remove(actor);
            player.Area.Monsters.Remove(actor);
            player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(actor), "companion retired"));
        }
    }
    internal async Task Tick(Player player, double now)
    {
        if (!Enabled(player)) { Close(player); return; }
        State state;
        HarbingerSession.Observation observation;
        bool Current() => Enabled(player) && !player.Closed && !player.LeavingArea && !player.Paused;
        state = Get(player);
        if (state.Closed || !Current()) return;
        var raw = StatSourceValues(player);
        var frame = Frame(player, raw);
        if (state.Cast is { } cast && now >= cast.At)
        {
            state.Cast = null;
            bool Accepted() => cast.Current() && GemSkillEffects(player).Any(g => g.Id == cast.Skill && g.Effect == cast.Source.SummonEffect)
                && GemSkillInstanceDisabled(player, cast.Source.SummonEffect, cast.Skill) is null;
            state.Session.Activate(cast.Source, frame, UtcNow(), Accepted, Stats.Key, Math.Max(0, now - cast.At));
        }
        observation = state.Session.Observe(frame, UtcNow(),
            receipt => player.PaidChannel is { } paid && paid.Skill == receipt.Skill && paid.Action == receipt.Action
                && paid.Generation == receipt.Generation && PaidChannelCurrent(player, paid), allowStart: false);
        bool OwnedCurrent() => Current() && !state.Closed && ReferenceEquals(state.Rules, CombatLifetime);
        foreach (var member in observation.Members)
        {
            if (state.Actors.ContainsKey(member.Id)) continue;
            var at = NextToPlayer(player);
            await SpawnMonster(player, member.Source.Monster, (byte)player.Pools.Level,
                MonsterRarity.Normal, at.X, at.Y, friendly: true,
                ownedMaximumLife: 1,
                extraStats: HarbingerActor.Stats(member.Source, Stats.Key), ownedSkills: HarbingerActor.Skills(member.Source, SkillsFor(player)),
                ownedCurrent: () => OwnedCurrent() && player.PlayerLife > 0
                    && player.Character.MainAscendancy.Allocated.Contains(member.Source.Node) && state.Session.Contains(member.Id),
                owned: (id, monster) =>
                {
                    if (!state.Session.Bind(member.Id, id)) return;
                    monster.Life = 1;
                    player.Minions.Add(new(id, monster, SkillsFor(player).EffectId(member.Source.SummonEffect)!, member.Source.Type.ToString())
                        { Until = double.PositiveInfinity });
                    state.Actors[member.Id] = id;
                });
        }
        await Send(player, [], "companion state", atGate: () =>
        {
            if (!OwnedCurrent()) return null;
            var raw = StatSourceValues(player);
            var frame = Frame(player, raw);
            var fresh = state.Session.Observe(frame, UtcNow(),
                _ => player.PaidChannel is { } receipt && PaidChannelCurrent(player, receipt));
            var bytes = new List<byte>();
            ReconcileCurses(player, state, now, bytes);
            foreach (var change in fresh.Changes)
                if (state.Actors.TryGetValue(change.Owner.Id, out uint caster)
                    && player.Area.Monsters.TryGetValue(caster, out var native))
                    foreach (var cast in change.Change.Started)
                    {
                        var at = native.Walk.Position(now); var target = player.Walk.Position(now);
                        bytes.AddRange(InstanceProtocol.ActionStarted(caster, at.X, at.Y, target.X, target.Y,
                            HarbingerActor.SkillId(change.Owner.Source, SkillsFor(player), cast.Skill.Effect),
                            player.NextActionSequence(), player.MonsterOrderFlags, targetObjectId: player.Id));
                        bytes.AddRange(InstanceProtocol.ActionEnded(caster));
                    }
            foreach (uint actor in state.Session.PruneActors())
            {
                player.Minions.Remove(actor); player.Area.Monsters.Remove(actor);
                foreach (var key in state.Actors.Where(p => p.Value == actor).Select(p => p.Key).ToArray()) state.Actors.Remove(key);
                bytes.AddRange(InstanceProtocol.RemoveObject(actor));
            }
            var active = fresh.Buffs.Select(b => (b.Owner.Id, b.Buff.Skill.Effect, b.Buff.StartedAt)).ToHashSet();
            if (state.Presented.Keys.Any(key => !active.Contains(key)))
            {
                player.Pools = PoolsFor(player, player.Pools.Level);
                player.ApplyMovementSpeed();
                PublishPlayerStats(player);
            }
            foreach (var (key, instance) in state.Presented.ToArray())
                if (!active.Contains(key)) { player.Area.Buffs.ClearTag(player.Id, instance); state.Presented.Remove(key); }
            foreach (var buff in fresh.Buffs)
            {
                var key = (buff.Owner.Id, buff.Buff.Skill.Effect, buff.Buff.StartedAt);
                if (state.Presented.ContainsKey(key)) continue;
                ushort instance = player.NextBuff++;
                var packet = state.Session.BuffPacket(player.Id, buff, frame.Epoch, frame, UtcNow(), instance,
                    HarbingerActor.SkillId(buff.Owner.Source, SkillsFor(player), buff.Buff.Skill.Effect),
                    actor => player.Minions.Owns(actor) && player.Area.Monsters.ContainsKey(actor),
                    () =>
                    {
                        player.Pools = PoolsFor(player, player.Pools.Level);
                        player.ApplyMovementSpeed();
                        return PublishPlayerStats(player);
                    },
                    () => (Frame(player, StatSourceValues(player)), UtcNow()),
                    () => state.Presented[key] = instance, buff => player.Area.Buffs.Set(player.Id, buff));
                if (packet is null) continue;
                bytes.AddRange(packet);
            }
            foreach (var (member, actor) in state.Actors)
                if (player.Area.Monsters.TryGetValue(actor, out var m) && Dist(m.Walk.Position(now), player.Walk.Position(now)) > 12)
                {
                    var at = NextToPlayer(player);
                    m.Walk.Teleport(at.X, at.Y, now);
                    bytes.AddRange(InstanceProtocol.Teleport(actor, at.X, at.Y));
                }
            return [.. bytes];
        });
    }
}
