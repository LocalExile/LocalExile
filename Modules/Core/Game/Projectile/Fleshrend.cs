using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const ushort FleshrendBuffRow = 2762;
    const string FleshrendPulseStage = "fleshrend-debuff";
    const int FleshrendRadius = 10;
    const int FleshrendStackLimit = 3;
    static readonly RuleTable<GameArea, Dictionary<uint, SpawnedMonster>> FleshrendMonsters = new();
    static readonly RuleTable<SpawnedMonster, FleshrendDebuff> FleshrendDebuffs = new();

    internal sealed record FleshrendCast(Player Caster, double Dps, double Duration, int Taken);

    sealed class FleshrendDebuff(double cursor)
    {
        internal readonly record struct Stack(ProjectileEntity Flight, FleshrendCast Cast, double Ends);
        internal readonly List<Stack> Stacks = new(FleshrendStackLimit);
        internal uint ShownSource;
        internal int ShownStacks;
        internal Player? DamagingCaster;
        double carry;
        double at = cursor;

        internal void Accrue(SpawnedMonster monster, double now)
        {
            while (at < now && Stacks.Count > 0)
            {
                double end = now, total = 0, strongest = 0;
                foreach (var stack in Stacks)
                {
                    end = Math.Min(end, stack.Ends);
                    total += stack.Cast.Dps;
                    if (stack.Cast.Dps <= strongest) continue;
                    strongest = stack.Cast.Dps;
                    DamagingCaster = stack.Cast.Caster;
                }
                if (end > at)
                    carry += (end - at) * total * MonsterDotTakenOver(monster, DamageType.Physical, at, end, at);
                at = Math.Max(at, end);
                for (int i = Stacks.Count - 1; i >= 0; i--)
                    if (Stacks[i].Ends <= at) Stacks.RemoveAt(i);
            }
            at = Math.Max(at, now);
        }

        internal void Refresh(ProjectileEntity flight, FleshrendCast cast, double now, double recipientRate)
        {
            int replace = -1, oldest = 0;
            for (int i = 0; i < Stacks.Count; i++)
            {
                if (Stacks[i].Ends < Stacks[oldest].Ends) oldest = i;
                if (ReferenceEquals(Stacks[i].Flight, flight)) replace = i;
            }
            var stack = new Stack(flight, cast, now + cast.Duration / Math.Max(.25, recipientRate));
            if (replace >= 0) Stacks[replace] = stack;
            else if (Stacks.Count == FleshrendStackLimit) Stacks[oldest] = stack;
            else Stacks.Add(stack);
        }

        internal int Damage()
        {
            int damage = (int)Math.Clamp(carry, 0, int.MaxValue);
            carry -= damage;
            return damage;
        }

        internal int IncreasedTaken(double now)
        {
            if (double.IsNaN(now)) now = at;
            int total = 0;
            foreach (var stack in Stacks)
                if (stack.Ends > now) total += stack.Cast.Taken;
            return total;
        }

        internal void Retime(SpawnedMonster monster, double now, double oldRate, double rate)
        {
            Accrue(monster, now);
            for (int i = 0; i < Stacks.Count; i++)
            {
                var stack = Stacks[i];
                Stacks[i] = stack with { Ends = now + (stack.Ends - now) * Math.Max(.25, oldRate) / Math.Max(.25, rate) };
            }
        }
    }

    static void ReleaseFleshrend(ProjectileSession session, ushort skill, int effect, (int X, int Y)? castAim)
    {
        var player = session.Player;
        var caster = CharacterPlayerStats(player);
        var skillStats = SupportedSkill(player, skill).Stats;
        var stats = MergeSkillStats(caster, skillStats);
        int Character(uint stat) => caster.GetValueOrDefault(stat);
        int Own(uint stat) => skillStats.GetValueOrDefault(stat);
        int Get(uint stat) => stats.GetValueOrDefault(stat);

        double increased = (double)Get(Stat.DamagePlusPct) + Get(Stat.PhysicalDamagePlusPct)
            + Get(Stat.DamageOverTimePlusPct) + Get(Stat.PhysicalDamageOverTimePlusPct) + Get(Stat.DamageOverTimePlusPctWithSpells);
        if (Own(Stat.SpellDamageModifiersApplyToSkillDot) != 0) increased += Get(Stat.SpellDamagePlusPct);
        double more = Math.Max(0, 1 + Own(Stat.SupportBrutalityPhysicalDamagePlusPctFinal) / 100d)
            * Math.Max(0, 1 + Own(Stat.SupportPhysChaosProjectilePhysicalDamageOverTimePlusPctFinal) / 100d);
        foreach (uint stat in EssenceDrainSupportMore)
            if (stat is not (Stat.SupportVoidManipulationChaosDamagePlusPctFinal
                or Stat.SupportPhysChaosProjectileChaosDamageOverTimePlusPctFinal))
                more *= Math.Max(0, 1 + Own(stat) / 100d);
        double multiplier = 1 + ((double)Get(Stat.DotMultiplierPlus) + Get(Stat.PhysicalDotMultiplierPlus)
            + Get(Stat.DamageOverTimeMultiplierPlusWithSpells)) / 100d;
        double dps = Own(Stat.BasePhysicalDamageToDealPerMinute) / 60d * Math.Max(0, 1 + increased / 100d)
            * more * Math.Max(0, multiplier);
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Character, Own);
        var damage = new SkillDamageContext(SkillHitKind.Spell, caster).OverTime(new(DamageType.Physical, dps, duration));
        int radius = SkillRadius(player, FleshrendRadius, skillStats, spell: true, effect, caster);
        const long interval = ProjectileRuntime.TickQuanta;
        int count = Get(Stat.NumberOfProjectilesOverride);
        if (count <= 0)
        {
            int baseCount = Get(Stat.BaseNumberOfProjectiles);
            count = Get(Stat.ModifiersToProjectileCountDoNotApply) != 0
                ? baseCount
                : baseCount + Get(Stat.NumberOfAdditionalProjectiles) + Get(Stat.SpellsNumberOfAdditionalProjectiles);
        }
        if (Get(Stat.MaximumNumberOfProjectilesToFireIs1) != 0) count = Math.Min(1, count);

        int baseSpeed = SkillsFor(player).Projectile("Metadata/Projectiles/Fleshrend")?.Speed ?? 500;
        double speed = ProjectileSpeed.CellsPerSecond(baseSpeed, Character, Own);
        if (!double.IsFinite(speed) || speed <= 0) return;

        long now = session.Runtime.Now;
        var from = player.Walk.Position(now / (double)ProjectileRuntime.QuantaPerSecond);
        var origin = ProjectileSession.Vector(from);
        // Self-targeted casts carry the caster's cell. Give both simulations the same nonzero aim.
        var aim = castAim ?? from;
        if (aim == from)
        {
            double best = SoulrendSeekRadius * SoulrendSeekRadius;
            foreach (var actor in session.ActorsNear(origin, SoulrendSeekRadius, now))
            {
                if (actor.Team == 1 || actor.Key.Id == player.Id) continue;
                var at = actor.Position(now);
                var offset = at - origin;
                double squared = offset.Dot(offset);
                if (squared <= 0 || squared >= best || session.TraceTerrain(origin, at) is not null) continue;
                best = squared;
                aim = ((int)Math.Round(at.X), (int)Math.Round(at.Y));
            }
            if (aim == from) aim = (from.X + SoulrendSeekRadius, from.Y);
        }
        var target = ProjectileSession.Vector(aim);
        bool lockstep = player.Connection.LockstepClient;
        ushort sequence = lockstep ? NextLockstepSequence(player) : (ushort)(++session.PactTriggerSequence & 0x7fff);
        var key = new ProjectileActorKey(player.Id, player.ProjectileEpoch);
        var profile = new ProjectileProfile("Fleshrend")
        {
            Count = Math.Clamp(count, 1, 253),
            NativeVolley = new() { Nova = true },
            Speed = speed,
            SpeedVariationPercent = Math.Clamp(Own(Stat.ActiveSkillProjectileSpeedPlusPctVariationFinal) / 2, 0, 99),
            Range = Get(Stat.ProjectileMaximumRangeOverride) is > 0 and var range ? range : 150,
            DirectDamage = false,
            Steering = SoulrendSteering(session),
            Pulse = radius > 0 && duration > 0
                ? new(FleshrendPulseStage, radius, interval)
                {
                    HitRule = new(ProjectileHitScope.Projectile, ProjectileRepeat.Interval, interval),
                }
                : null,
        };
        var shot = new ProjectileOffense(DamagePacket.Empty, true, 0, 0, 0, "Fleshrend")
        {
            SourceSkill = skill, Effect = effect, ClientPredicted = true, ActionSequence = sequence,
            Fleshrend = new(player, DamageStats.Prohibited(DamageType.Physical, Get) ? 0 : damage.DamagePerSecond,
                duration, Own(Stat.BloodrendDebuffDamageOverTimeTakenPlusPctPerStack)),
        };
        session.WorldRecords(player.Id, () => [InstanceProtocol.InstantAction(player.Id, skill, aim.X, aim.Y,
            sequence: sequence, flags: lockstep ? (ushort)0x101 : (ushort)0, triggered: true)],
            ServerTrace.Projectiles ? $"Fleshrend trigger seq={sequence}" : null);
        session.ReleaseNow(new(profile, key, key, 1, origin, target)
        {
            Sequence = sequence,
            Payload = shot,
            RadialLaunchOffset = NativeProjectileForwardCells(Get, null),
        });
    }

    static ProjectileHitResult ApplyFleshrendDamage(ProjectileDamage contact, SpawnedMonster monster, FleshrendCast cast)
    {
        if (!monster.Hostile || monster.OwnerId is not null || monster.Invulnerable || monster.Effects[Stat.BaseCannotBeDamaged] != 0)
            return ProjectileHitResult.Miss;
        var area = cast.Caster.Area;
        uint id = contact.Target.Id;
        double now = contact.At / (double)ProjectileRuntime.QuantaPerSecond;
        if (!FleshrendDebuffs.TryGetValue(monster, out var state))
        {
            FleshrendDebuffs.Add(monster, state = new(now));
            FleshrendMonsters.GetOrCreateValue(area)[id] = monster;
        }
        state.Accrue(monster, now);
        state.Refresh(contact.Projectile, cast, now, monster.RecipientDebuffRate);
        PresentFleshrend(area, id, state);
        return ProjectileHitResult.Hit;
    }

    static void PresentFleshrend(GameArea area, uint id, FleshrendDebuff state)
    {
        int count = state.Stacks.Count;
        if (count == state.ShownStacks) return;
        if (count == 0)
        {
            area.Buffs.Clear(id, FleshrendBuffRow, state.ShownSource);
            state.ShownSource = 0;
        }
        else
        {
            if (state.ShownStacks == 0) state.ShownSource = state.Stacks[0].Cast.Caster.Id;
            area.Buffs.Set(id, new(FleshrendBuffRow, state.ShownSource, Stacks: (ushort)count, KeepInstance: true));
        }
        state.ShownStacks = count;
    }

    static bool HasFleshrendDamage(GameArea area) =>
        FleshrendMonsters.TryGetValue(area, out var monsters) && monsters.Count != 0;

    static int FleshrendDamageTaken(SpawnedMonster monster, double now) =>
        FleshrendDebuffs.TryGetValue(monster, out var state) ? state.IncreasedTaken(now) : 0;

    static void RetimeFleshrendDamage(SpawnedMonster monster, double now, double oldRate, double rate)
    {
        if (FleshrendDebuffs.TryGetValue(monster, out var state)) state.Retime(monster, now, oldRate, rate);
    }
}
