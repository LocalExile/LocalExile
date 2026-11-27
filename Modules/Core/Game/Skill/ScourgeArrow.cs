using GameServer.Core.Projectiles;

public static partial class Game
{
    const string ScourgeEffect = "ScourgeArrow";
    const string ScourgeRoyaleEffect = "ScourgeArrowRoyale";
    const string ScourgeMenaceEffect = "ScourgeArrowAltX";
    const string ScourgeProjectile = "Metadata/Projectiles/VirulentArrow";
    const string ScourgeMenaceProjectile = "Metadata/Projectiles/VirulentArrowLesser";
    const string ScourgeThorn = "Metadata/Projectiles/VirulentBloomArrow";
    const string ScourgePodArt = "Metadata/Effects/Spells/virulent_arrow/bloompod.ao";
    const ushort ScourgeStageBuff = 1180;

    static readonly ProjectileSkillDefinition NormalScourgeProjectile = new(ScourgeProjectile,
        new((get, speed, _) => GatewayProjectileDefaults.Resolve(ScourgeProjectile, get, speed, attack: true, arrows: true)));
    static readonly ProjectileSkillDefinition MenaceScourgeProjectile = new(ScourgeMenaceProjectile,
        new((get, speed, _) => GatewayProjectileDefaults.Resolve(ScourgeMenaceProjectile, get, speed, attack: true, arrows: true)))
    {
        SpecializeRelease = (context, use) => use with
        {
            Payload = ((ProjectileOffense)use.Payload!) with
            {
                ScourgePods = Math.Max(0, context.Own(Stat.ScourgeArrowXPodsPerProjectile)),
                ScourgePodsPerProjectile = true
            }
        }
    };

    static ProjectileSkillDefinition? ScourgeProjectileDefinition(string effect) => effect switch
    {
        ScourgeEffect or ScourgeRoyaleEffect => NormalScourgeProjectile,
        ScourgeMenaceEffect => MenaceScourgeProjectile,
        _ => null
    };

    static bool IsChannelledScourgeArrow(int effect) => Skills.EffectId(effect) is ScourgeEffect or ScourgeRoyaleEffect;

    sealed class ScourgeChannel(SupportedGem source, ushort skill, int generation, int action,
        int epoch, ushort sequence, (int X, int Y) aim)
    {
        public int Stages;
        public int MaxStages { get; } = Math.Clamp(source.Stats.GetValueOrDefault(Stat.VirulentArrowMaximumNumberOfStacks), 0, 256);
        public (int X, int Y) Aim = aim;
        public SupportedGem Source { get; } = source;
        public ushort Skill { get; } = skill;
        public int Generation { get; } = generation;
        public int Action { get; } = action;
        public int Epoch { get; } = epoch;
        public ushort Sequence { get; } = sequence;
    }

    static readonly RuleTable<Player, ScourgeChannel> ScourgeChannels = new();

    static void StartScourgeChannel(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        int action, int generation, ushort sequence, (int X, int Y) aim, bool initialCostPaid)
    {
        if (ScourgeChannels.TryGetValue(player, out var previous))
            RetireScourgeChannel(player, previous.Generation, previous.Action);
        var channel = new ScourgeChannel(SupportedSkill(player, gem.Id), gem.Id,
            generation, action, player.ProjectileEpoch, sequence, aim);
        ScourgeChannels.AddOrUpdate(player, channel);
        if (initialCostPaid && channel.MaxStages > 0)
        {
            channel.Stages = 1;
            player.Area.Buffs.Set(player.Id, new(ScourgeStageBuff, player.Id, channel.Skill,
                Stacks: 1, KeepInstance: true));
        }
    }

    static bool IsScourgeChannelling(Player player, ushort skill, int generation, int action) =>
        ScourgeChannels.TryGetValue(player, out var channel) && channel.Skill == skill
            && channel.Generation == generation && channel.Action == action;

    static void ScourgeAimedAt(Player player, (int X, int Y) aim)
    {
        if (ScourgeChannels.TryGetValue(player, out var channel)
            && channel.Generation == player.ChannelGeneration && channel.Action == player.ActionCount)
            channel.Aim = aim;
    }

    static void GainScourgeStage(Player player, int generation, int action)
    {
        if (!ScourgeChannels.TryGetValue(player, out var channel) || channel.Generation != generation
            || channel.Action != action || channel.Stages >= channel.MaxStages) return;
        channel.Stages++;
        player.Area.Buffs.Set(player.Id, new(ScourgeStageBuff, player.Id, channel.Skill,
            Stacks: (ushort)channel.Stages, KeepInstance: true));
    }

    static void RetireScourgeChannel(Player player, int generation, int action)
    {
        if (!ScourgeChannels.TryGetValue(player, out var channel) || channel.Generation != generation
            || channel.Action != action) return;
        ScourgeChannels.Remove(player);
        if (channel.Stages != 0) player.Area.Buffs.Clear(player.Id, ScourgeStageBuff, player.Id);
    }

    static void ReleaseScourgeChannel(Player player, double at)
    {
        using (ProjectileMutation(player, advance: false))
        {
            if (!ScourgeChannels.TryGetValue(player, out var channel)
                || !ChannelCurrent(player, channel.Skill, channel.Action, channel.Generation)) return;
            RetireScourgeChannel(player, channel.Generation, channel.Action);
            if (player.ProjectileEpoch != channel.Epoch || PlayerActionBlocked(player, at, at)) return;
            var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == channel.Skill);
            if (gem.Name is null || !IsChannelledScourgeArrow(gem.Effect)
                || !SameTriggerSource(channel.Source, SupportedSkill(player, channel.Skill))
                || GemSkillInstanceDisabled(player, gem.Effect, channel.Skill) is not null) return;
            var session = ProjectilesFor(player);
            var critical = CaptureSkillCriticalRoll(player, channel.Skill);
            session.Schedule(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(at)),
                () => session.Active && player.PlayerLife > 0 && player.ProjectileEpoch == channel.Epoch,
                () =>
                {
                    var use = CaptureGemProjectile(player, session, channel.Skill, gem, channel.Aim, critical, channel.Sequence);
                    var shot = (ProjectileOffense)use.Payload!;
                    int StageStat(uint key) => shot.SkillStats.GetValueOrDefault(key);
                    double more = Math.Max(0, 1 + StageStat(Stat.VirulentArrowDamagePlusPctFinalPerStage) * (double)channel.Stages / 100);
                    long pods = (long)channel.Stages + (channel.Stages == channel.MaxStages ? StageStat(Stat.VirulentArrowAdditionalSporesAtMaxStages) : 0);
                    return use with { Payload = shot.ScaleDamage(more) with { ScourgePods = (int)Math.Clamp(pods, 0, 256) } };
                });
        }
    }

    public sealed partial class ProjectileSession
    {
        void ScheduleScourgePods(ProjectileEntity arrow, ProjectileOffense shot)
        {
            int arrows = Math.Max(1, arrow.Profile.Count);
            int count = shot.ScourgePodsPerProjectile ? Math.Min(256, shot.ScourgePods)
                : shot.ScourgePods / arrows + (arrow.VolleyIndex < shot.ScourgePods % arrows ? 1 : 0);
            int thorns = shot.SkillStats.GetValueOrDefault(Stat.VirulentArrowNumberOfPodProjectiles);
            if (count <= 0 || thorns <= 0 || arrow.Speed <= 0) return;

            // Distribute pods between release and aim on that arrow's flight, up to its travel range.
            double reach = Math.Min(arrow.Profile.Range, (arrow.Use.Aim - arrow.Position).Length);
            int nativeArrowSpeed = SkillsFor(Player).Projectile(arrow.Profile.Id)?.Speed ?? 4890;
            int nativeThornSpeed = SkillsFor(Player).Projectile(ScourgeThorn)?.Speed ?? 2000;
            double thornSpeed = arrow.Speed * nativeThornSpeed / Math.Max(1, nativeArrowSpeed);
            const double bloom = .55, thornLife = .45;
            int variation = SkillArtVariation(Player, shot.Gem);
            var thorn = new ProjectileProfile(Art.Projectile(ScourgeThorn, variation))
            {
                Speed = thornSpeed, Range = thornSpeed * thornLife, Count = Math.Min(256, thorns),
                Lifetime = ProjectileRuntime.Time(thornLife), NativeVolley = new() { Nova = true },
                HitRule = new(ProjectileHitScope.Emission)
            };
            double thornMore = Math.Max(0, 1 + shot.SkillStats.GetValueOrDefault(Stat.VirulentArrowPodProjectileDamagePlusPctFinal) / 100d);
            var thornOffense = shot.ScaleDamage(thornMore) with
            {
                ScourgePods = 0, ScourgePodsPerProjectile = false, MirageSource = null,
                ClientPredicted = false, Presentation = null, AreaVisual = null
            };
            var origin = arrow.Position;
            for (int i = 0; i < count; i++)
            {
                double distance = reach * (i + 1) / (count + 1);
                var point = origin + arrow.Direction * distance;
                if (TraceTerrain(origin, point) is not null) break;
                Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(distance / arrow.Speed), () =>
                {
                    if (!Active || Player.PlayerLife == 0 || arrow.Distance + .01 < distance) return;
                    RainArt(point, ScourgePodArt, bloom, clip: 0);
                    Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(bloom), () =>
                    {
                        if (!Active || Player.PlayerLife == 0) return;
                        Runtime.Release(arrow.Use with { Profile = thorn, Origin = point,
                            Aim = point + arrow.Direction, Payload = thornOffense, ProjectileTargets = null, ProjectileSpeeds = null,
                            ProjectilePayloads = null, TargetSplit = null, TargetPierce = null });
                    });
                });
            }
        }
    }
}
