using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const ushort ValourBuffRow = 2480, PlacedBannerBuffRow = 2482;
    const ushort WarBannerAuraRow = 1221, DreadBannerAuraRow = 1222, DefianceBannerAuraRow = 1864;
    const string BannerArtRoot = "Metadata/Effects/Spells/Banner/";

    sealed record PlacedBanner(ushort Skill, SupportedGem Gem, GemSourceLifetimes.SourceLifetime Source, uint Object,
        ushort Row, (int X, int Y) At, int Radius, double Until, IReadOnlyDictionary<uint, int> Stats, double AuraEffect,
        bool AffectsOwner)
    {
        public readonly HashSet<Player> Recipients = [];
    }

    sealed class BannerState
    {
        public double Valour;
        public double NextValourAt;
        public PlacedBanner? Active;
    }

    static readonly RuleTable<Player, BannerState> Banners = new();
    static readonly RuleTable<InstanceState, Dictionary<uint, PlacedBanner>> AreaBanners = new();

    static ushort BannerAuraRow(string? id) => id switch
    {
        "WarBanner" => WarBannerAuraRow,
        "DreadBanner" => DreadBannerAuraRow,
        "DefianceBanner" => DefianceBannerAuraRow,
        _ => 0,
    };

    static bool HasBannerSource(Player player) => GemSkillEffects(player).Any(g =>
        BannerAuraRow(SkillsFor(player).EffectId(g.Effect)) != 0 && SupportedSkill(player, g.Id).RequirementsMet);

    static void ShowValour(Player player, BannerState state)
    {
        if (state.Valour < 1) player.Area.Buffs.Clear(player.Id, ValourBuffRow, player.Id);
        else player.Area.Buffs.Set(player.Id, new(ValourBuffRow, player.Id,
            Stacks: (ushort)Math.Min(ushort.MaxValue, Math.Floor(state.Valour)), KeepInstance: true));
    }

    static void GainValourFromMeleeLocked(Player player, SpawnedMonster monster, bool killed, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (player.PlayerLife == 0 || !monster.Hostile
            || !killed && monster.Rarity is not (MonsterRarity.Rare or MonsterRarity.Unique)) return;
        if (Banners.TryGetValue(player, out var current)
            && (now < current.NextValourAt || current.Active is not null
                && stats.GetValueOrDefault(Stat.CanGainBannerResourceWhileBannerIsPlaced) == 0)) return;
        if (!HasBannerSource(player)) return;
        var state = current ?? Banners.GetOrCreateValue(player);
        long maximum = Math.Max(0, 50L + stats.GetValueOrDefault(Stat.BannerResourceMaximumPlus));
        state.Valour = Math.Min(maximum, state.Valour
            + Math.Max(0, 1 + stats.GetValueOrDefault(Stat.BannerResourceGainedPlusPct) / 100d));
        state.NextValourAt = now + .5;
        ShowValour(player, state);
    }

    static bool CanUseBanner(Player player, ushort skill) => Banners.TryGetValue(player, out var state)
        && (state.Active is { } active ? active.Skill == skill : state.Valour >= 1 && CanPaySkillCost(player, skill));

    static bool BannerReaches(Player recipient, PlacedBanner banner, double now) =>
        !recipient.Closed && !recipient.LeavingArea && recipient.PlayerLife > 0 && now < banner.Until
        && Dist(recipient.Walk.Position(now), banner.At) <= banner.Radius;

    static void AddBannerAuraStats(Player player, Dictionary<uint, int> stats)
    {
        if (player.Instance is not { } instance || !AreaBanners.TryGetValue(instance, out var placed) || placed.Count == 0) return;
        double now = AreaTime(player);
        foreach (var banner in placed.Values.Where(b => b.Recipients.Contains(player) && BannerReaches(player, b, now))
            .GroupBy(b => b.Row).Select(g => g.MaxBy(b => b.AuraEffect)!))
        {
            StatsChangeIn(banner.Until - now);
            foreach (var (id, value) in banner.Stats)
                stats[id] = ActorScale.Add(id, stats.GetValueOrDefault(id), value);
        }
    }

    static void StepBannerAuraLocked(Player owner, PlacedBanner banner, double now)
    {
        void For(Player recipient)
        {
            if (!BannerReaches(recipient, banner, now) || ReferenceEquals(owner, recipient) && !banner.AffectsOwner) return;
            if (!banner.Recipients.Add(recipient)) return;
            owner.Area.Buffs.Set(recipient.Id, BuffRows.Spec(banner.Row, banner.Object, banner.Stats));
            MarkPlayerStats(recipient, PlayerStatStep.Sources);
        }

        foreach (var recipient in banner.Recipients.ToArray())
            if (!ReferenceEquals(recipient.Instance, owner.Instance) || !BannerReaches(recipient, banner, now))
            {
                owner.Area.Buffs.Clear(recipient.Id, banner.Row, banner.Object);
                banner.Recipients.Remove(recipient);
                MarkPlayerStats(recipient, PlayerStatStep.Sources);
            }
        For(owner);
        foreach (var ally in owner.Instance.PlayersExcept(owner.Id)) For(ally);
    }

    static void EndBannerLocked(Player player, BannerState state, PlacedBanner banner, bool retiring = false)
    {
        state.Active = null;
        if (AreaBanners.TryGetValue(player.Instance, out var held)
            && ReferenceEquals(held.GetValueOrDefault(banner.Object), banner)) held.Remove(banner.Object);
        if (!Banners.TryGetValue(player, out var other) || ReferenceEquals(other, state) || other.Active is null)
            player.Area.Buffs.Clear(player.Id, PlacedBannerBuffRow, player.Id);
        foreach (var recipient in banner.Recipients)
        {
            player.Area.Buffs.Clear(recipient.Id, banner.Row, banner.Object);
            MarkPlayerStats(recipient, PlayerStatStep.Sources);
        }
        banner.Recipients.Clear();
        player.ProjectileObjects.Remove(banner.Object);
        if (player.LeavingArea || player.Closed) return;
        if (retiring) player.RetiredCombatObjects.Add(banner.Object);
        else ProjectilesFor(player).WorldRecords(banner.Object, () => [InstanceProtocol.RemoveObject(banner.Object)], remove: true);
    }

    static void RetireBannerLocked(Player player)
    {
        if (!Banners.TryGetValue(player, out var state)) return;
        var area = player.Area;
        void Cleanup(GameArea _)
        {
            using var mutation = ProjectileMutation(player, advance: false);
            if (Banners.TryGetValue(player, out var current) && ReferenceEquals(current, state)) Banners.Remove(player);
            if (state.Active is { } banner) EndBannerLocked(player, state, banner, retiring: true);
            if (!Banners.TryGetValue(player, out var other) || other.Valour < 1)
                area.Buffs.Clear(player.Id, ValourBuffRow, player.Id);
        }
        // The module watcher also retires projectile sessions. Shared aura state and buff records belong to the area.
        if (area.Driven && !area.Owned) area.Defer(Cleanup);
        else Cleanup(area);
    }

    static async Task<(bool Accepted, bool Activated)> ToggleBanner(Player player, ushort skill, Func<bool> current)
    {
        uint mana = 0, life = 0;
        bool activated = false;
        using (ProjectileMutation(player, advance: false))
        {
            if (!current() || player.Paused || player.Instance is null || InTown(player) || !CanUseBanner(player, skill)
                || !Banners.TryGetValue(player, out var state)) return (false, false);
            if (state.Active is { } previous) EndBannerLocked(player, state, previous);
            else
            {
                var gem = SupportedSkill(player, skill);
                if (CaptureGemSourceLifetimeLocked(player, skill, gem.Effect) is not { } source
                    || !GemSourceLifetimeCurrentLocked(player, skill, gem.Effect, source) || !gem.RequirementsMet) return (false, false);
                ushort row = BannerAuraRow(SkillsFor(player).EffectId(gem.Effect));
                if (row == 0) return (false, false);
                var stats = CharacterPlayerStats(player);
                int Own(uint id) => gem.Stats.GetValueOrDefault(id);
                int Combined(uint id) => ActorScale.Saturate((long)Own(id) + stats.GetValueOrDefault(id));
                double now = AreaTime(player), valour = Math.Floor(state.Valour);
                double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration),
                    id => stats.GetValueOrDefault(id), Own, DurationKinds(player, gem.Effect));
                if (duration <= 0 || !PayMana(player, skill, out mana, out life)) return (false, false);
                activated = true;
                var areaStats = new Dictionary<uint, int>(gem.Stats);
                areaStats[SkillArea.BaseIncreased] = ActorScale.Add(SkillArea.BaseIncreased,
                    areaStats.GetValueOrDefault(SkillArea.BaseIncreased), Combined(Stat.BannerAreaOfEffectPlusPct));
                double areaFactor = Math.Max(0, 1 + valour * Own(Stat.BannerAreaOfEffectPlusPctFinalPerResource) / 100);
                int radius = Math.Max(1, (int)Math.Round(SkillRadius(player, areaStats, spell: true, gem.Effect) * Math.Sqrt(areaFactor)));
                uint bannerEffect = row == WarBannerAuraRow ? Stat.WarBannerAuraEffectPlusPct : row == DreadBannerAuraRow
                    ? Stat.DreadBannerAuraEffectPlusPct : 0;
                double effect = Math.Max(0, 1 + ((double)Combined(Stat.NonCurseAuraEffectPlusPct)
                    + Combined(Stat.AuraEffectPlusPct) + (bannerEffect == 0 ? 0 : Combined(bannerEffect))) / 100)
                    * Math.Max(0, 1 + valour * Own(Stat.BannerBuffEffectPlusPctFinalPerResource) / 100);
                uint[] fields = row switch
                {
                    WarBannerAuraRow => [Stat.WarBannerPhysicalDamagePlusPctFinalWithMeleeSkills, Stat.AccuracyRatingPlusPct],
                    DreadBannerAuraRow => [Stat.AdditionalBlockPct, Stat.RecoverXLifeOnBlock],
                    _ => [Stat.BaseMovementVelocityPlusPct, Stat.BaseSpellSuppressionChancePct, Stat.DamageTakenPlusFromSuppressedHits],
                };
                var values = fields.ToDictionary(id => id, id => ActorScale.Saturate((long)Math.Truncate(Own(id) * effect)));
                uint id = player.TakeObjectId();
                var at = player.Walk.Position(now);
                var banner = new PlacedBanner(skill, gem, source, id, row, at, radius, now + duration, values, effect,
                    Own(Stat.AuraCannotAffectSelf) == 0);
                state.Active = banner;
                state.Valour = 0;
                ShowValour(player, state);
                AreaBanners.GetOrCreateValue(player.Instance)[id] = banner;
                player.Area.Buffs.Set(player.Id, new(PlacedBannerBuffRow, player.Id, skill, U68: Buff.Seconds(duration)));
                string art = BannerArtRoot + (row == WarBannerAuraRow ? "WarBannerPole.ao" : row == DreadBannerAuraRow
                    ? "DreadBannerPole.ao" : "ArmourEvasionBannerPole.ao");
                var visual = new EffectObject(at.X, at.Y, art);
                player.ProjectileObjects.Add(id);
                ProjectilesFor(player).Create(id, () => ReferenceEquals(state.Active, banner)
                    ? InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), visual.Blob())
                    : null, wake: true);
                StepBannerAuraLocked(player, banner, now);
            }
        }
        if (mana > 0 || life > 0) await SendSkillCost(player, life, "banner cost");
        await SendCharacterStats(player, PlayerStatChange.Sources);
        return (true, activated);
    }

    static void TickBanner(Player player, double now)
    {
        if (!Banners.TryGetValue(player, out var state)) return;
        using (ProjectileMutation(player, advance: false))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return;
            if (state.Active is { } banner)
            {
                if (!BannerReaches(player, banner, now)
                    || !GemSourceLifetimeCurrentLocked(player, banner.Skill, banner.Gem.Effect, banner.Source))
                    EndBannerLocked(player, state, banner);
                else StepBannerAuraLocked(player, banner, now);
            }
            if (state.Valour > 0 && (player.PlayerLife == 0 || !HasBannerSource(player)))
            {
                state.Valour = 0;
                ShowValour(player, state);
            }
        }
    }
}
