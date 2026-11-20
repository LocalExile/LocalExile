using GameServer.Core.World;
using static Game;
using static Program;

public static partial class MapStrongboxes
{
    public abstract record VesselCast(long Started, string Daemon)
    {
        public SkillCriticalRoll CriticalRoll { get; init; } = new(0);
        public List<VesselHit> Pending { get; init; } = [];
        public HashSet<uint>? Removals { get; set; }
        public uint Actor { get; set; }
        public int Landed { get; set; }
        internal virtual (int X, int Y) Target(Box box) => (box.X, box.Y);
    }

    public sealed record VesselHit(uint Player, int Epoch, MonsterHitOutcome Result);

    sealed record VesselImpact(MonsterAttack Attack, DamagePacket AilmentBase);
    sealed record VesselSkill(int Effect, ushort Action, int ActorLevel, Dictionary<uint, int> Stats,
        MonsterAttack ActionRecord, int Critical, SpellCastTiming? Timing = null)
    {
        public double Seconds => ActionRecord.Seconds;
        public int Required(string name) => Stats[Program.Stats.Key(name)!.Value];
        public int Get(string name) => Program.Stats.Key(name) is uint key ? Stats.GetValueOrDefault(key) : 0;
        public VesselImpact Impact(int radius, int hitMore = 0, int ailmentMore = 0) =>
            new(ActionRecord with { Range = radius, Damage = ActionRecord.Damage.Scale(1 + hitMore / 100d),
                Ailments = IncomingAilmentSettings(Get) }, ActionRecord.Damage.Scale(1 + ailmentMore / 100d));
    }

    static VesselSkill ReadVesselSpell(ClientState conn, string name)
    {
        var catalog = SkillsFor(conn);
        int effect = catalog.EffectRow(name), level = AreaLevel(conn);
        var native = GolemData.Skill(catalog, effect, level);
        int castMs = catalog.CastMs(effect);
        int moreSpeed = native.Stats.GetValueOrDefault(Stats.Key("active_skill_cast_speed_+%_final")!.Value);
        ushort action = (ushort)(catalog.ActionHash(effect)!.Value & 0x3fff);
        var timing = SpellCastTiming.Calculate(castMs, moreSpeed);
        var attack = new MonsterAttack(action, timing.DurationSeconds!.Value, DefaultRadius, Packet(native.Stats, "spell").Scale(native.Multiplier))
            { Spell = true, Ranged = true };
        return new(effect, action, level, native.Stats, attack, native.Crit, timing);
    }

    static (VesselCast Cast, VesselSkill Skill)? PrepareVesselSpell(ClientState conn, Area area, StrongboxCatalog.Definition type, Box box)
    {
        // The Vaalsmithed prefixes share one native mod family. The suffix has its own family.
        foreach (var (_, daemon, effects) in Daemons(conn, type, box))
        {
            if (effects.Contains(VaalCausticArrowEffect))
            {
                var skill = area.CausticArrowSkill ??= ReadCausticArrow(conn, daemon);
                var cast = PrepareCausticArrow(conn, box, daemon, skill);
                box.CausticArrows.Add(cast);
                return (cast, skill.Skill);
            }
            if (effects.Contains(VaalFirestormEffect))
            {
                var skill = area.FirestormSkill ??= ReadFirestorm(conn);
                var random = new Random(unchecked((int)conn.Seed ^ box.Index * 9973 ^ box.Cycle * 31));
                var ground = new uint[skill.Meteors];
                for (int i = 0; i < ground.Length; i++) ground[i] = conn.TakeObjectId();
                var storm = new VaalFirestorm(conn.Clock.Elapsed.Ticks, random.NextDouble() * Math.Tau, daemon, ground);
                box.Firestorms.Add(storm);
                return (storm, skill.Spell);
            }
            if (effects.Contains(VaalFlameblastEffect))
            {
                var skill = area.FlameblastSkill ??= ReadFlameblast(conn);
                var blast = new VaalFlameblast(conn.Clock.Elapsed.Ticks, daemon);
                box.Flameblasts.Add(blast);
                return (blast, skill.Spell);
            }
        }
        return null;
    }

    static void WatchVesselPositions(ClientState player, Area area, Box box)
    {
        foreach (var storm in box.Firestorms.Where(s => s.Landed < s.Ground.Length))
        {
            var skill = area.FirestormSkill ??= ReadFirestorm(player);
            Watch(storm, storm.Ground.Length, skill.Spell.Timing!.Value.DurationMilliseconds!.Value * TimeSpan.TicksPerMillisecond,
                skill.DelayMs * TimeSpan.TicksPerMillisecond);
        }
        foreach (var blast in box.Flameblasts)
        {
            var skill = area.FlameblastSkill ??= ReadFlameblast(player);
            Watch(blast, skill.Bursts.Length, skill.BurstTicks, skill.BurstTicks);
        }

        foreach (var cast in box.CausticArrows.Where(c => c.Landed < c.Steps.Length))
        {
            var skill = area.CausticArrowSkill ??= ReadCausticArrow(player, cast.Daemon);
            Watch(cast, cast.Steps.Length, skill.ReleaseTicks + skill.StepTicks, skill.StepTicks);
        }

        void Watch(VesselCast cast, int count, long first, long interval)
        {
            var key = (box.Id, cast.Started);
            lock (player.Monsters)
            {
                ViewFor(player).PositionWatches.Add(key);
                player.Walk.WatchPositions(key, Enumerable.Range(cast.Landed, count - cast.Landed)
                    .Select(i => (cast.Started + first + i * interval) / (double)TimeSpan.TicksPerSecond));
            }
        }
    }

    static async Task<bool> PublishVesselHits(ClientState conn, Area area, VesselCast cast)
    {
        if (cast.Pending.Count == 0) return CurrentGameWork;
        var members = Members(conn).ToDictionary(p => p.PeerId);
        await Task.WhenAll(cast.Pending.ToArray().Select(receipt =>
        {
            if (members.TryGetValue(receipt.Player, out var p))
                return PresentVesselHit(conn.Instance, p, cast.Actor, receipt.Epoch, receipt.Result, () => Acknowledge(receipt));
            lock (conn.Monsters) Acknowledge(receipt);
            return Task.CompletedTask;
        }));
        return CurrentGameWork && cast.Pending.Count == 0;

        void Acknowledge(VesselHit receipt)
        {
            cast.Pending.Remove(receipt);
            Keep(conn, area);
        }
    }

    static async Task ShowVesselCast(ClientState conn, Area area, Box box, VesselCast cast, VesselSkill skill, bool play = true)
    {
        if (cast.Actor == 0)
            await Show(conn, cast.Daemon, skill.Effect, skill.ActorLevel, skill.Stats, (box.X, box.Y),
                id => { cast.Actor = id; Keep(conn, area); }, play, skill.Timing, skill.ActionRecord, cast.Target(box));
        else if (play)
            await PlayDaemon(conn, cast.Actor, skill.Action, (box.X, box.Y), cast.Target(box), cast.Daemon, shared: true);
    }

    // All current recipients and the cast advance in one combat commit, before one peer's socket can interrupt another.
    static async Task<bool> LandVesselImpact(ClientState conn, Area area, Box box, VesselCast cast, VesselSkill skill,
        VesselImpact impact, (int X, int Y) at, long due, Func<ClientState, (int X, int Y), (int X, int Y)?>? contact = null)
    {
        var instance = conn.Instance;
        double landing = due / (double)TimeSpan.TicksPerSecond;
        var players = Members(conn).Where(p => p.PlayerSent && ViewFor(p).Entered is long entered && entered <= due).ToArray();
        if (players.Length != 0 && cast.Actor == 0)
        {
            await ShowVesselCast(conn, area, box, cast, skill, play: false);
            if (cast.Actor == 0)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return false;
                throw new InvalidOperationException($"Could not admit vessel caster {cast.Daemon} for effect {skill.Effect}");
            }
        }
        lock (instance.World.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || !CombatLifetime!.TryRunSynchronous(Commit)) return false;
        }
        return await PublishVesselHits(conn, area, cast);

        void Commit()
        {
            var previous = ProjectileOutputGuard.Value;
            ProjectileOutputGuard.Value = null;
            try
            {
                foreach (var player in players)
                {
                    if (player.Closed || player.LeavingArea || player.Instance != conn.Instance || player.Paused
                        || player.PlayerLife == 0 || player.Immune || InGracePeriod(player)) continue;
                    double now = player.Clock.Elapsed.TotalSeconds;
                    var here = player.Walk.WatchedPosition((box.Id, cast.Started), landing);
                    if (contact is null && Distance(here, at) > impact.Attack.Range) continue;
                    var source = contact is null ? at : contact(player, here);
                    if (source is not { } from) continue;
                    var hit = CommitMonsterHitLocked(player.Wire.Remote, player, cast.Actor, conn.Monsters[cast.Actor],
                        impact.Attack, now, here, from, false, MonsterActionSequence, impact.AilmentBase, skill.Critical, cast.CriticalRoll);
                    if (hit is not null) cast.Pending.Add(new(player.PeerId, player.IncomingHitEpoch, hit));
                }
                cast.Landed++;
                Keep(conn, area);
            }
            finally { ProjectileOutputGuard.Value = previous; }
        }
    }

    static async Task<bool> RemoveVesselActor(ClientState conn, Area area, Box box, VesselCast cast)
    {
        uint actor = cast.Actor;
        var members = Members(conn).ToDictionary(p => p.PeerId);
        lock (conn.Monsters)
        {
            foreach (var p in area.Visitors.Concat(members.Values).Distinct())
            {
                p.Walk.EndPositionWatch((box.Id, cast.Started));
                if (Presented.TryGetValue(p, out var view)) view.PositionWatches.Remove((box.Id, cast.Started));
            }
            if (cast.Removals is null)
            {
                cast.Removals = [];
                if (conn.Monsters.Remove(actor, out var m)) { m.Dead = true; m.Life = 0; m.ActionGeneration++; }
                conn.Instance.World.MonsterOrigins.TryRemove(actor, out _);
                foreach (var p in members.Values)
                {
                    if (Presented.TryGetValue(p, out var view)) view.Casts.Remove(actor);
                    if (p.MonsterViews.Remove(actor)) cast.Removals.Add(p.PeerId);
                    p.AsleepMonsters.Remove(actor);
                }
            }
            cast.Removals.RemoveWhere(peer => !members.ContainsKey(peer));
            Keep(conn, area);
        }
        await Task.WhenAll(cast.Removals.ToArray().Select(peer =>
        {
            var p = members[peer];
            return PublishInstanceOutput(conn.Instance, p, current =>
                SendWorldRemoval(p.Wire.Remote, p.Wire.Stream, p.Wire.Ciphers, p, actor, "vessel spell ends", current, () =>
                {
                    lock (conn.Monsters)
                    {
                        if (!cast.Removals.Remove(peer)) return null;
                        Keep(conn, area);
                        return GameServer.Core.Protocol.Instance.InstanceProtocol.RemoveObject(actor);
                    }
                }));
        }));
        return cast.Removals.Count == 0;
    }
}
