using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string AvengingFlameEffect = "AvengingFlame";
    const string InfernalBoltProjectile = "Metadata/Projectiles/InfernalBolt";
    const ushort AvengingFlameTotemSkill = 0x4001;
    const double AvengingFlameAimSeconds = .2;
    const double AvengingFlameLaunchSpacing = .07;

    sealed record FlamewoodSource(GrantedSkill Grant, ushort Parent, SupportedGem Placement, PlayerSkillSource Source,
        SupportedGem Spell)
    {
        public SingleUseCooldownState? Cooldown;
    }

    static readonly RuleTable<TotemState.Totem, FlamewoodSource> FlamewoodTotems = new();

    static FlamewoodSource? TotemFlamewood(Player player, ushort skillId)
    {
        const uint trigger = Stat.SupportFlamewoodTotemsTriggerInfernalBoltWhenHit;
        var placement = SupportedSkill(player, skillId);
        if (placement.Stats.GetValueOrDefault(trigger) == 0
            || placement.Supports.FirstOrDefault(s => SupportHas(s, trigger)) is not { } support)
            return null;

        uint host = placement.WornGrant?.Host ?? placement.Host;
        var catalog = SkillsFor(player);
        var source = PlayerSkillSourcesLocked(player).FirstOrDefault(s => s.Active && s.Identity.Host == host
            && catalog.EffectId(s.Effect) == AvengingFlameEffect
            && (s.Identity.Socket == support.Gem.Socket
                || s.WornGrant is { ModId: "HungryLoopSupportedByFlamewood" } && s.Level == support.Gem.Level));
        if (source is null) return null;
        var spell = SupportedSkill(player, source.Id);
        if (spell.Stats.GetValueOrDefault(Stat.InfernalBoltBaseFireDamagePctMaximumLife) <= 0) return null;

        var caster = TotemCasterStats(CharacterPlayerStats(player));
        var payload = SkillAreaStats(player, spell.Stats, spell.Effect, caster);
        payload[Stat.IsTotem] = 0;
        payload[Stat.SkillIsTotemified] = 0;
        payload[Stat.BaseSkillIsTotemified] = 0;
        payload[Stat.SkillIsNonPathing] = 1;
        payload[Stat.UsesNoMana] = 1;
        payload[Stat.ManaCost] = 0;
        payload[Stat.ActiveSkillAreaOfEffectRadius] = SkillArea.Radius(
            SkillArea.BaseRadius(AvengingFlameEffect, payload.GetValueOrDefault), payload.GetValueOrDefault, spell: true);
        payload[Stat.TotalSkillAreaOfEffectRadiusPlusPctIncludingFinal] = SkillArea.RadiusPercent(payload.GetValueOrDefault, spell: true);
        ApplyProjectilePanelStats(spell.Effect, payload);
        double speed = ProjectileSpeed.Scale(1, caster.GetValueOrDefault, spell.Stats.GetValueOrDefault);
        payload[Stat.ProjectileSpeedPlusPct] = (int)Math.Round((speed - 1) * 100);
        var grant = catalog.FindByRow(AvengingFlameTotemSkill, spell.Effect, spell.Level,
            [.. payload.Select(pair => (pair.Key, pair.Value))]);
        return grant is null ? null : new(grant with { Parent = (player.Id, skillId) }, skillId, placement, source, spell);
    }

    static void TriggerFlamewoodLocked(Player player, TotemState.Totem totem, FlamewoodSource flamewood, uint attackerId,
        SpawnedMonster attacker, double now)
    {
        var session = ProjectilesFor(player);
        if (!session.Active || player.Paused || player.PlayerLife == 0 || !player.ProjectileActive(player.ProjectileEpoch)
            || !SameLinkedSkillSource(flamewood.Placement, SupportedSkill(player, flamewood.Parent))
            || !SameTriggerSource(flamewood.Spell, SupportedSkill(player, flamewood.Source.Id))
            || CooldownForSkill(player, flamewood.Source.Id) is not { } plan)
            return;
        var cooldown = flamewood.Cooldown ??= new(now, plan.RecoveryRate);
        cooldown.Capacity(plan.Uses);
        cooldown.Refresh(now, plan.RecoveryRate);
        if (!cooldown.TryUse(now, plan)) return;

        int epoch = player.ProjectileEpoch;
        var initialAim = attacker.Walk.Position(now);
        bool lockstep = player.Connection.LockstepClient;
        ushort sequence = ++totem.Sequence;
        if (lockstep) sequence |= 0x8000;
        session.World(totem.Id, InstanceProtocol.InstantAction(totem.Id, AvengingFlameTotemSkill,
            initialAim.X, initialAim.Y, targetObjectId: attackerId == 0 ? null : attackerId,
            sequence: sequence, flags: lockstep ? (ushort)0x101 : (ushort)0, triggered: true),
            $"0x01ce Avenging Flame totem 0x{totem.Id:x} at {initialAim}");

        session.Schedule(ProjectileRuntime.Time(now + AvengingFlameAimSeconds),
            () => player.ProjectileActive(epoch) && player.PlayerLife > 0 && totem.Alive(AreaTime(player)),
            () =>
            {
                double at = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
                var aim = player.Area.Monsters.TryGetValue(attackerId, out var current) && ReferenceEquals(attacker, current)
                    ? attacker.Walk.Position(at) : initialAim;
                var spell = flamewood.Spell;
                ushort skill = flamewood.Source.Id;
                var gem = (skill, spell.Effect, AvengingFlameEffect, spell.Level, spell.Quality, flamewood.Source.Gem);
                var use = CaptureGemProjectile(player, session, skill, gem, aim,
                    CaptureSkillCriticalRoll(player, skill), sequence, totem: totem, capturedSkill: spell);

                // Infernal Bolt releases a short upward stage before its fall. Native fall time scales with projectile speed.
                var caster = CharacterPlayerStats(player);
                double speed = Math.Max(.01, ProjectileSpeed.Scale(1, caster.GetValueOrDefault, spell.Stats.GetValueOrDefault));
                var area = SkillAreaStats(player, spell.Stats, spell.Effect, caster);
                var destinations = FlamewoodDestinations(player, use, totem.Id, sequence, area.GetValueOrDefault);
                var speeds = destinations.Select(p => Math.Max(.001, (p.At - use.Origin).Length)
                    / (Math.Max(.01, p.Index * AvengingFlameLaunchSpacing) + 1 / speed)).ToArray();
                return use with
                {
                    Profile = use.Profile with { Count = destinations.Count, SpreadRadians = 0,
                        Lifetime = ProjectileRuntime.Time(1 + use.Profile.Count * AvengingFlameLaunchSpacing + 1 / speed) },
                    ProjectileTargets = destinations.Select(p => p.At).ToArray(),
                    ProjectileSpeeds = speeds,
                };
            });
    }

    static List<(ProjectileVector At, int Index)> FlamewoodDestinations(Player player, ProjectileUse use, uint totem,
        ushort sequence, Func<uint, int> area)
    {
        List<(ProjectileVector, int)> destinations = [(use.Aim, 0)];
        // The client samples squared radius in consecutive annuli (0x141cf6bc0).
        // The retained aim action starts with derived ID two after the transient trigger action.
        var random = new AnimationVariantSelector.NativeRandom([sequence, 2, 0, totem, 0]);
        int spread = 0;
        for (int i = 1; i < use.Profile.Count; i++)
        {
            int inner = SkillArea.Radius(spread, area, spell: true);
            int outer = SkillArea.Radius(spread + 4, area, spell: true);
            float width = Math.Max(0, outer - inner);
            float squared = width * width;
            uint range = (uint)Math.Clamp(1000 * (squared + 1), 1, uint.MaxValue - 256);
            for (int attempt = 0; attempt < 9; attempt++)
            {
                float angle = MathF.Tau * (random.Uniform(2001) / 2000f);
                float radius = inner + MathF.Sqrt(random.Uniform(range + 1) / (float)range * squared);
                int dx = (int)(radius * MathF.Sin(angle)), dy = (int)(radius * MathF.Cos(angle));
                int x = (int)use.Aim.X + dx, y = (int)use.Aim.Y - dy;
                if (!player.Layout.WalkableFor(x, y, 0) || player.Barrier.Contains((x, y))
                    || player.Layout.Sight is { } sight && ClientPathfinder.LineBelow(((int)use.Aim.X, (int)use.Aim.Y), (x, y), sight, 1))
                    continue;
                destinations.Add((new(x, y), i));
                spread += Math.Max(2, 4 - Math.Max(0, i - 4));
                break;
            }
        }
        return destinations;
    }
}
