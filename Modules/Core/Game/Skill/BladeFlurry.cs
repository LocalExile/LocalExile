using GameServer.Core.World;

public static partial class Game
{
    const ushort BladeFlurryStagesBuff = 640;
    static readonly RuleTable<Player, BladeFlurryChannel> BladeFlurryChannels = new();

    sealed class BladeFlurryChannel(SupportedGem source, ushort skill, int action, int generation, int epoch,
        int interruption, ushort sequence, (int X, int Y) aim, int maximum)
    {
        public readonly SupportedGem Source = source;
        public readonly ushort Skill = skill;
        public readonly int Action = action;
        public readonly int Generation = generation;
        public readonly int Epoch = epoch;
        public readonly int Interruption = interruption;
        public readonly ushort Sequence = sequence;
        public readonly int Maximum = maximum;
        public readonly Dictionary<uint, int> PreviousTargets = [];
        public (int X, int Y) Aim = aim;
        public (int X, int Y)? SearchCentre;
        public int Stages, ReleaseRemaining;
    }

    internal readonly record struct BladeFlurryBonus(double Damage, double CriticalMore, int CriticalMultiplier);

    static bool IsBladeFlurry(Player player, int effect) => SkillsFor(player).ActionHash(effect) == 0xaf62;

    internal static int BladeFlurryMaxStages(IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> player)
    {
        int maximum = own.GetValueOrDefault(Stat.DisplayMaxChargedAttackStats);
        return Math.Clamp(maximum + (player.GetValueOrDefault(Stat.Plus1MaxChargedAttackStages) != 0 ? 1 : 0), 1, 255);
    }

    internal static BladeFlurryBonus BladeFlurryStageBonus(IReadOnlyDictionary<uint, int> own, int count, int maximum)
    {
        int stage = Math.Clamp(count, 0, maximum);
        int maximumDamage = stage == maximum ? own.GetValueOrDefault(Stat.BladeFlurryDamagePlusPctFinalWhileAtMaxStages) : 0;
        int maximumCritical = stage == maximum ? own.GetValueOrDefault(Stat.BladeFlurryCriticalStrikeMultiplierPlusWhileAtMaxStages) : 0;
        double damage = 1 + stage * own.GetValueOrDefault(Stat.ChargedAttackDamagePerStackPlusPctFinal) / 100.0;
        double critical = 1 + stage * own.GetValueOrDefault(Stat.BladeFlurryCriticalStrikeChancePerStagePlusPctFinal) / 100.0;
        return new(damage * (1 + maximumDamage / 100.0), critical, maximumCritical);
    }

    internal static void BeginBladeFlurry(Player player, SupportedGem gem, ushort skill, int action, int generation,
        ushort sequence, (int X, int Y) aim)
    {
        if (BladeFlurryChannels.TryGetValue(player, out var previous))
            RetireBladeFlurry(player, previous.Generation);
        BladeFlurryChannels.AddOrUpdate(player, new(gem, skill, action, generation, player.ProjectileEpoch,
            player.ActionInterruptionGeneration, sequence, aim, BladeFlurryMaxStages(gem.Stats, CharacterPlayerStats(player))));
    }

    static bool IsBladeFlurryChannelling(Player player, ushort skill, int generation, int action) =>
        BladeFlurryChannels.TryGetValue(player, out var flurry) && flurry.Skill == skill
            && flurry.Generation == generation && flurry.Action == action;

    static bool AimBladeFlurry(Player player, (int X, int Y) aim)
    {
        if (!BladeFlurryChannels.TryGetValue(player, out var flurry)
            || flurry.Generation != player.ChannelGeneration || flurry.Action != player.ActionCount
            || aim == flurry.Aim) return false;
        flurry.Aim = aim;
        flurry.SearchCentre = null;
        return true;
    }

    static void GainBladeFlurryStage(Player player, BladeFlurryChannel flurry)
    {
        if (flurry.Stages == flurry.Maximum) return;
        flurry.Stages++;
        player.Area.Stats.Set(player.Id, Stat.CurrentChargedAttackStacks, flurry.Stages);
        player.Area.Buffs.Set(player.Id, new(BladeFlurryStagesBuff, player.Id, flurry.Skill,
            Stacks: (ushort)flurry.Stages, KeepInstance: true));
    }

    static void RetireBladeFlurry(Player player, int generation)
    {
        if (!BladeFlurryChannels.TryGetValue(player, out var flurry) || flurry.Generation != generation) return;
        BladeFlurryChannels.Remove(player);
        if (flurry.Stages > 0 && flurry.Epoch == player.ProjectileEpoch && !player.LeavingArea && !player.Closed)
        {
            player.Area.Stats.Set(player.Id, Stat.CurrentChargedAttackStacks, 0);
            player.Area.Buffs.Clear(player.Id, BladeFlurryStagesBuff, player.Id);
        }
    }

    internal static (int X, int Y) BladeFlurrySearchCentre((int X, int Y) origin, (int X, int Y) aim,
        int distance, ushort sequence, uint actorId)
    {
        double dx = (double)aim.X - origin.X, dy = (double)aim.Y - origin.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length == 0)
        {
            var random = new AnimationVariantSelector.NativeRandom([sequence, actorId]);
            float angle = random.Uniform(0, 1024) * (MathF.PI / 512);
            (dx, dy, length) = (MathF.Cos(angle), MathF.Sin(angle), 1);
        }
        return (origin.X + (int)(dx * distance / length), origin.Y + (int)(dy * distance / length));
    }

    static bool BladeFlurryContact(Player player, BladeFlurryChannel flurry, IReadOnlyDictionary<uint, int> own,
        double at, bool release, out (int X, int Y) centre, out int hitRadius)
    {
        var area = SkillAreaStats(player, own, flurry.Source.Effect);
        int scale = 100 + SkillArea.RadiusPercent(area.GetValueOrDefault, spell: false);
        int acquisition = (release ? 28 : 22) * scale / 100;
        hitRadius = 14 * scale / 100;
        centre = default;
        if (acquisition <= 0 || hitRadius <= 0) return false;
        var origin = player.Walk.Position(at);
        var search = release && flurry.SearchCentre is { } previous ? previous
            : BladeFlurrySearchCentre(origin, flurry.Aim, 22 * scale / 100, flurry.Sequence, player.Id);
        if (!release) flurry.SearchCentre = search;
        uint chosen = 0, total = 0;
        var random = new AnimationVariantSelector.NativeRandom(
            [(uint)(release ? flurry.ReleaseRemaining : flurry.Stages), flurry.Sequence, (uint)flurry.PreviousTargets.Count]);
        var candidates = player.Area.MonstersNear(search, acquisition)
            .Where(e => e.Value is { Hostile: true, Dead: false, Life: > 0, OwnerId: null, WorldHidden: false })
            .Select(e => (Id: e.Key, Monster: e.Value, At: e.Value.Walk.Position(at)))
            .OrderBy(e => e.At.Y / 23).ThenBy(e => e.At.X / 23).ThenBy(e => e.Id);
        foreach (var (id, monster, target) in candidates)
        {
            double distance = Dist(target, search);
            if (distance > acquisition + LivingCollision.Size(monster)
                || player.Layout.Sight is { } sight && ClientPathfinder.LineBelow(search, target, sight, 1)) continue;
            flurry.PreviousTargets.TryAdd(id, 0);
            uint weight = (uint)Math.Max(1, 1_000_000 / Math.Max(1, ((int)distance + flurry.PreviousTargets[id] * 5) * 10));
            total += weight;
            if (random.Uniform(total) >= weight) continue;
            chosen = id;
            centre = target;
        }
        if (chosen == 0) return false;
        flurry.PreviousTargets[chosen] = flurry.PreviousTargets.GetValueOrDefault(chosen) + 1;
        return true;
    }

    internal static Task ReleaseBladeFlurry(Player player, double at)
    {
        if (!BladeFlurryChannels.TryGetValue(player, out var flurry)
            || !IsBladeFlurryChannelling(player, player.ChannelSkill, player.ChannelGeneration, player.ActionCount))
            return Task.CompletedTask;
        RetireBladeFlurry(player, flurry.Generation);
        return flurry.Stages == 0 || PlayerActionBlocked(player, at, at)
            ? Task.CompletedTask : RunBladeFlurryRelease(player, flurry, at);
    }

    static async Task RunBladeFlurryRelease(Player player, BladeFlurryChannel flurry, double at)
    {
        bool Current() => CurrentGameWork && !player.Closed && !player.LeavingArea && player.PlayerLife > 0
            && player.ProjectileEpoch == flurry.Epoch && player.ActionCount == flurry.Action
            && player.ActionInterruptionGeneration == flurry.Interruption
            && !PlayerActionBlocked(player, AreaTime(player), AreaTime(player))
            && SameTriggerSource(flurry.Source, SupportedSkill(player, flurry.Skill));
        for (int hit = 0; hit < flurry.Stages; hit++)
        {
            await AreaDelay(player, Math.Max(0, at + .08 + hit * .025 - AreaTime(player)));
            await WhilePaused(player);
            if (!Current()) return;
            var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == flurry.Skill);
            if (gem.Name is null || !IsBladeFlurry(player, gem.Effect)
                || GemSkillInstanceDisabled(player, gem.Effect, flurry.Skill) is not null) return;
            flurry.ReleaseRemaining = flurry.Stages - hit;
            var origin = player.Walk.Position(AreaTime(player));
            await AreaSkillHitForUse(ruthlessMore: 1, player, flurry.Skill, gem, origin, flurry.Aim, immediate: true,
                sequence: flurry.Sequence, actionToken: flurry.Action, triggeredCurrent: Current, flurryRelease: flurry);
        }
    }
}
