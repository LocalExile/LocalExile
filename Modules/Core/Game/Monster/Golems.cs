using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public static GolemCatalog GolemData = null!;
    const string GolemSaveKey = "Game.Golems";
    static int GolemStat(IReadOnlyDictionary<uint, int> stats, uint name) => name is var key ? stats.GetValueOrDefault(key) : 0;
    static int GolemEffectiveLimit(int gemGolems, Func<uint, int> playerStat) =>
        Math.Min(100, ApplyDarkMonarchLimit(GolemCatalog.Limit(gemGolems, playerStat), playerStat, "Golem"));
    static void GolemAdd(Dictionary<uint, int> stats, uint name, int value)
    { if (value != 0 && name is var key) stats[key] = ActorScale.Saturate((long)stats.GetValueOrDefault(key) + value); }

    static double SavedLifeFraction(SpawnedMonster monster)
    {
        if (monster.Dead || monster.Life == 0 || monster.MaxLife == 0) return 0;
        double fraction = monster.Life / (double)monster.MaxLife;
        return double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
    }

    static double SavedLifeRegeneration(double carry) =>
        double.IsFinite(carry) && carry is > -1 and < 1 ? carry : 0;

    static GolemState.MinionResourceSnapshot CaptureResources(GolemState.Golem golem, double now) =>
        new(golem.Monster.Life, SavedLifeRegeneration(golem.Regeneration), golem.ShieldPool.Capture(now));

    enum SummonReplacementFamily { Golem, Summon }

    sealed record SummonReplacementResources(uint Life, uint MaximumLife, uint EnergyShield, uint MaximumEnergyShield,
        double LifeRegeneration, EnergyShield.Snapshot EnergyShieldCapture, double CapturedAt, double LastStep);

    sealed record SummonReplacementAdmission(SummonReplacementFamily Family, GolemState.Golem Old, ushort Source,
        int Effect, int Level, int Quality, bool SummonedRecently, Dictionary<uint, int> Own, Dictionary<uint, int> Player,
        GemSkillIds.Gem Identity, StatSourcesResult Sources, object Area, object? Lifetime,
        SummonReplacementResources Resources, double SummonedAt, double LastStep, double Until,
        GemSourceLifetimes.SourceLifetime GemSource);

    sealed record FreshSummonAdmission(ushort Source, int Effect, int Level, int Quality,
        Dictionary<uint, int> Own, Dictionary<uint, int> Player, GemSkillIds.Gem Identity,
        StatSourcesKey StatSourcesKey, object Area, object? Lifetime, GemSourceLifetimes.SourceLifetime GemSource);

    sealed record LocalGolemSave(ushort Skill, int Effect, GolemState.Saved Saved,
        GemSourceLifetimes.SourceLifetime? GemSource);

    static bool SameDouble(double left, double right) =>
        BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);

    static bool SameStats(IReadOnlyDictionary<uint, int> left, IReadOnlyDictionary<uint, int> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out int value) && value == pair.Value);

    static SummonReplacementResources CaptureReplacementResources(GolemState.Golem golem, double at) =>
        new(golem.Monster.Life, golem.Monster.MaxLife, golem.ShieldPool.Current, golem.ShieldPool.Maximum,
            golem.Regeneration, golem.ShieldPool.Capture(at), at, golem.LastStep);

    static IReadOnlyList<PlayerSkillSource> PlayerSkillSourcesLocked(Player player)
    {
        if (BuildScopeOf(player) is not { Building: true } && CurrentActiveSkills(player) is { } current)
            return current.Sources ??= PlayerSkillSourcesNow(player);
        return PlayerSkillSourcesNow(player);
    }

    static IReadOnlyList<PlayerSkillSource> PlayerSkillSourcesNow(Player player)
    {
        string name = CharacterName(player);
        var equipment = ItemsOf(player).EquipmentSnapshot();
        return PlayerSkillSourcesAt(name, equipment, InactiveItems.GetValueOrDefault(name), LeagueFor(player),
            GrantedSkillsOf(player), WornGrantedSkillsOf(player, equipment));
    }

    static PlayerSkillSource? CurrentPlayerSkillSourceLocked(Player player, ushort source) =>
        PlayerSkillSourcesLocked(player).FirstOrDefault(row => row.Id == source);

    static FreshSummonAdmission? PrepareFreshSummonLocked(Player player, ushort source, SupportedGem supported,
        Dictionary<uint, int> playerStats, GemSourceLifetimes.SourceLifetime? gemSource)
    {
        var sourceRow = CurrentPlayerSkillSourceLocked(player, source);
        if (!supported.RequirementsMet || supported.Level <= 0 || supported.Quality < 0 || gemSource is null
            || sourceRow is null || !sourceRow.Active || sourceRow.Effect != supported.Effect
            || sourceRow.Identity != gemSource.Key || player.StatSourcesCache is not { } sources)
            return null;
        return new(source, supported.Effect, supported.Level, supported.Quality,
            new(supported.Stats), new(playerStats), sourceRow.Identity, sources.Key, player.Instance, CombatLifetime, gemSource);
    }

    const int MaxMinionRestoreRetries = 5;

    sealed class MinionRestoreGuard
    {
        public bool Restoring;
        public int NextIndex;
        public int Retries;
    }
    static readonly RuleTable<GolemState, MinionRestoreGuard> GolemRestoreGuards = new();
    static readonly RuleTable<SummonedMinions, MinionRestoreGuard> SummonRestoreGuards = new();

    static bool FreshSummonCurrentLocked(Player player, FreshSummonAdmission admission, bool arrival = false)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0
            || !ReferenceEquals(player.Instance, admission.Area) || !ReferenceEquals(CombatLifetime, admission.Lifetime)
            || !player.GemSourceLifetimes.IsCurrent(admission.GemSource))
            return false;
        using var scope = new PlayerStatScope(player, fresh: true);
        var supported = SupportedSkill(player, admission.Source);
        var sourceRow = CurrentPlayerSkillSourceLocked(player, admission.Source);
        if (!supported.RequirementsMet || supported.Effect != admission.Effect
            || supported.Level <= 0 || supported.Quality < 0
            || sourceRow is not { Active: true } || sourceRow.Effect != admission.Effect
            || sourceRow.Identity != admission.Identity || sourceRow.Identity != admission.GemSource.Key)
            return false;
        // Arrival restores only require structural validity: concurrent Grace Period, aura restoration or sibling
        // minion spawns may shift StatSourcesCache.Key / playerStats while the create packet waits for its gate.
        if (arrival) return true;
        var playerStats = CharacterPlayerStats(player);
        return supported.Level == admission.Level && supported.Quality == admission.Quality
            && SameStats(supported.Stats, admission.Own) && SameStats(playerStats, admission.Player)
            && player.StatSourcesCache is { } currentStatSourcesCache
            && currentStatSourcesCache.Key.Matches(admission.StatSourcesKey);
    }

    static bool ReplacementRosterCurrentLocked(SummonReplacementFamily family, Player player, GolemState.Golem old) =>
        family == SummonReplacementFamily.Golem
            ? player.Golems.All.GetValueOrDefault(old.Id) == old
            : player.Summons.Fighters.GetValueOrDefault(old.Id) == old
                && player.Summons.Definitions.GetValueOrDefault(old.Id)?.Id == old.Definition.Id;

    static SummonReplacementAdmission? PrepareReplacementLocked(Player player, SummonReplacementFamily family,
        GolemState.Golem old, ushort source, int effect, SupportedGem supported, Dictionary<uint, int> playerStats, double now)
    {
        if (supported.Level <= 0 || supported.Quality < 0
            || old.Monster.Dead || old.Monster.Life == 0 || old.Source != source || old.Definition.Effect != effect
            || player.Area.Monsters.GetValueOrDefault(old.Id) != old.Monster || !player.Minions.Owns(old.Id, old.Monster)
            || !ReplacementRosterCurrentLocked(family, player, old))
            return null;
        var sourceRow = CurrentPlayerSkillSourceLocked(player, source);
        if (sourceRow is null || !sourceRow.Active || sourceRow.Effect != effect || player.StatSourcesCache is not { } sources
            || !supported.RequirementsMet
            || family == SummonReplacementFamily.Summon && !player.Summons.Until.ContainsKey(old.Id))
            return null;
        var gemSource = old.GemSource ?? CaptureGemSourceLifetimeLocked(player, sourceRow);
        if (gemSource is null || !player.GemSourceLifetimes.IsCurrent(gemSource) || gemSource.Key != sourceRow.Identity)
            return null;
        double until = family == SummonReplacementFamily.Summon ? player.Summons.Until[old.Id] : double.PositiveInfinity;
        if (double.IsFinite(until) && now >= until) return null;
        return new(family, old, source, effect, supported.Level, supported.Quality,
            GolemSummonedInPastEightSeconds(old, now),
            new(supported.Stats), new(playerStats), sourceRow.Identity, sources, player.Instance, CombatLifetime,
            CaptureReplacementResources(old, now), old.SummonedAt, old.LastStep, until, gemSource);
    }

    static bool SummonReplacementCurrentLocked(Player player, SummonReplacementAdmission admission)
    {
        var old = admission.Old;
        double now = AreaTime(player);
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0
            || !player.GemSourceLifetimes.IsCurrent(admission.GemSource)
            || !ReferenceEquals(player.Instance, admission.Area) || !ReferenceEquals(CombatLifetime, admission.Lifetime)
            || !ReplacementRosterCurrentLocked(admission.Family, player, old)
            || player.Area.Monsters.GetValueOrDefault(old.Id) != old.Monster || !player.Minions.Owns(old.Id, old.Monster)
            || old.Monster.Dead || old.Monster.Life == 0 || old.Source != admission.Source
            || old.Definition.Effect != admission.Effect
            || GolemSummonedInPastEightSeconds(old, now) != admission.SummonedRecently)
            return false;
        var captured = admission.Resources;
        if (old.Monster.Life != captured.Life || old.Monster.MaxLife != captured.MaximumLife
            || old.ShieldPool.Current != captured.EnergyShield || old.ShieldPool.Maximum != captured.MaximumEnergyShield
            || !SameDouble(old.Regeneration, captured.LifeRegeneration)
            || !SameDouble(old.LastStep, captured.LastStep)
            || old.ShieldPool.Capture(captured.CapturedAt) != captured.EnergyShieldCapture)
            return false;
        using var scope = new PlayerStatScope(player, fresh: true);
        var playerStats = CharacterPlayerStats(player);
        var supported = SupportedSkill(player, admission.Source);
        var sourceRow = CurrentPlayerSkillSourceLocked(player, admission.Source);
        if (!supported.RequirementsMet || supported.Effect != admission.Effect
            || supported.Level <= 0 || supported.Quality < 0
            || supported.Level != admission.Level || supported.Quality != admission.Quality
            || !SameStats(supported.Stats, admission.Own) || !SameStats(playerStats, admission.Player)
            || sourceRow is null || !sourceRow.Active || sourceRow.Effect != admission.Effect
            || sourceRow.Identity != admission.Identity
            || sourceRow.Identity != admission.GemSource.Key
            || player.StatSourcesCache is not { } sources || !ReferenceEquals(sources, admission.Sources)
            || !sources.Key.Matches(admission.Sources.Key))
            return false;
        if (admission.Family == SummonReplacementFamily.Summon
            && (!player.Summons.Until.TryGetValue(old.Id, out double until)
                || !SameDouble(until, admission.Until) || double.IsFinite(until) && AreaTime(player) >= until))
            return false;
        return admission.Family switch
        {
            SummonReplacementFamily.Golem => player.Golems.All.GetValueOrDefault(old.Id) == old,
            SummonReplacementFamily.Summon => player.Summons.Fighters.GetValueOrDefault(old.Id) == old,
            _ => false,
        };
    }

    static void TransferIncomingReplacementStateLocked(GolemState.Golem oldGolem, GolemState.Golem replacement, double now)
    {
        var old = oldGolem.Monster;
        var target = replacement.Monster;
        old.Ignites.MoveTo(target.Ignites);
        old.Bleeds.MoveTo(target.Bleeds);
        old.Poisons.MoveTo(target.Poisons);
        old.Ailments.MoveTo(target.Ailments);

        target.ReceivedActionFactor = old.ReceivedActionFactor;
        target.ReceivedActionUntil = old.ReceivedActionUntil;
        target.ReceivedActionFloor = old.ReceivedActionFloor;
        target.RecipientDebuffRate = old.RecipientDebuffRate;
        target.FlinchUntil = old.FlinchUntil;
        target.StunImmuneUntil = old.StunImmuneUntil;
        if (double.IsFinite(old.BusyUntil) && old.BusyUntil > now) target.BusyUntil = old.BusyUntil;
        if (oldGolem.HolyRegenerationUntil > now)
        {
            replacement.HolyRegenerationUntil = oldGolem.HolyRegenerationUntil;
            replacement.HolyRegenerationPerSecond = oldGolem.HolyRegenerationPerSecond;
        }
        if (oldGolem.RecallUntil > now)
        {
            replacement.RecallUntil = oldGolem.RecallUntil;
            replacement.RecallRate = oldGolem.RecallRate;
        }
        replacement.BlockedAt = oldGolem.BlockedAt;
    }

    static void RetireReplacementLocked(Player player, SummonReplacementAdmission admission, double now)
    {
        if (admission.Family == SummonReplacementFamily.Golem) RemoveGolemLocked(player, admission.Old, now, reconcileBlessing: false);
        else RemoveSummonLocked(player, admission.Old, now, reconcileBlessing: false);
    }

    static double SavedPositiveDelay(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? seconds : .001;

    public static bool GolemSummonedInPastEightSeconds(Player player, double at)
    {
        double last = player.Golems.LastSummoned;
        return double.IsFinite(last) && at >= last && at - last < 8;
    }

    static double GolemSummonedSecondsRemaining(GolemState.Golem golem, double now) =>
        !golem.Monster.Dead && GolemSummonedInPastEightSeconds(golem, now)
            ? Math.Clamp(8 - Math.Max(0, now - golem.SummonedAt), 0, 8) : 0;

    static void AddGolemStats(Player player, StatMap stats)
    {
        AddSummonCounts(player, stats);
        if (Stat.NumberOfActiveMines is var mines)
            stats[mines] = player.Placed.All.Values.Count(p => p.Mine);
        if (Stat.NumberOfActiveTraps is var traps)
            stats[traps] = player.Placed.All.Values.Count(p => !p.Mine);
        if (player.PlayerLife > 0)
            foreach (var (key, value) in player.Golems.OwnerStats)
                stats[key] = ActorScale.Saturate((long)stats.GetValueOrDefault(key) + value);
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, object> MinionSaveLocks = new();

    public static void SaveGolems(Player player)
    {
        lock (MinionSaveLocks.GetOrCreateValue(player))
        {
            Dictionary<ushort, PlayerSkillSource> canonical;
            LocalGolemSave[] local;
            string character;
            Guid owner;
            if (!player.Golems.Restored) return;
            double now = AreaTime(player);
            canonical = PlayerSkillSourcesLocked(player).Where(source => source.Active)
                .GroupBy(source => source.Id).ToDictionary(group => group.Key, group => group.First());
            local = player.Golems.All.Values
                .Where(g => !g.Monster.Dead || double.IsFinite(g.ReviveAt))
                .Select(g => new LocalGolemSave(g.Source, g.Definition.Effect, SavedGolem(player, g, now), g.GemSource))
                .Concat(player.Golems.Waiting.Select(w => new LocalGolemSave(
                    w.Saved.Skill, GolemData.Find(w.Saved.Effect)?.Effect ?? int.MinValue, w.Saved with
                    { ReviveSeconds = SavedPositiveDelay(w.At - now) }, w.GemSource)))
                .ToArray();
            character = CharacterName(player);
            owner = player.VitalsSessionId;
            Saves.Atomically(() => CharacterSaves.ChangeModuleData(character, GolemSaveKey, existing =>
            {
                var inventory = PlayerInventories.Current(character);
                bool LocalCurrent(LocalGolemSave entry)
                {
                    if (entry.GemSource is not { } lease) return true; // A labelled legacy fixture has no lease.
                    if (lease.Retired || lease.Parent is { Retired: true }) return false;
                    if (!canonical.TryGetValue(entry.Skill, out var source)
                        || source.Effect != entry.Effect || source.Identity != lease.Key) return false;
                    if (lease.Parent is not { } marker) return true;
                    if (inventory?.GemActivationWriter(GolemSaveKey, entry.Skill) is not { } writer) return true;
                    return writer.Owner == owner && ReferenceEquals(writer.Marker, marker);
                }
                var eligible = local.Where(LocalCurrent).ToArray();
                var ownedSkills = eligible.Select(entry => entry.Skill).ToHashSet();
                bool ForeignCurrent(GolemState.Saved entry)
                {
                    if (ownedSkills.Contains(entry.Skill) || inventory is null
                        || !canonical.TryGetValue(entry.Skill, out var source)
                        || GolemData.Find(entry.Effect) is not { } definition
                        || source.Effect != definition.Effect
                        || inventory.GemActivationWriter(GolemSaveKey, entry.Skill) is not { } writer
                        || writer.Marker.Retired || writer.Marker.Key != source.Identity
                        || writer.Marker.Key.Effect != definition.Effect)
                        return false;
                    return writer.Owner != owner;
                }
                GolemState.Saved[] durable;
                try { durable = JsonSerializer.Deserialize<GolemState.Saved[]>(existing ?? "[]") ?? []; }
                catch (JsonException) { durable = []; }
                return JsonSerializer.Serialize(eligible.Select(entry => entry.Saved)
                    .Concat(durable.Where(ForeignCurrent)).ToArray());
            }));
        }
    }

    static GolemState.Saved SavedGolem(Player player, GolemState.Golem golem, double now)
    {
        var gem = SupportedSkill(player, golem.Source);
        object? sourceId = gem.SourceGem is not null ? PlayerInventories.GemIdentity(gem.SourceGem) : null;
        var place = sourceId is null ? default : ItemsOf(player).SocketedGemPlaces().FirstOrDefault(p =>
            ReferenceEquals(PlayerInventories.GemIdentity(p.Gem), sourceId));
        return new(golem.Definition.Id, golem.Source,
            SavedLifeFraction(golem.Monster),
            golem.Monster.Dead ? SavedPositiveDelay(golem.ReviveAt - now) : 0,
            GolemSummonedSecondsRemaining(golem, now), CaptureResources(golem, now),
            Host: gem.WornGrant?.Host ?? (place.ItemId == 0 ? null : place.ItemId),
            Socket: gem.WornGrant?.Socket ?? (place.ItemId == 0 ? null : place.Socket));
    }

    internal static ushort RestoredGolemSource(Player player, GolemState.Saved saved) =>
        RestoredMinionSource(player, saved.Effect, saved.Skill, saved.Host, saved.Socket);

    internal static Task<bool> CastGolem(Player player,
        ushort source, GolemCatalog.Definition definition, (int X, int Y) aim, int recovery, Func<bool> current,
        bool triggered = false) =>
        RepeatSummonCast(player, source, recovery, current, triggered, async () =>
        {
            await SummonGolem(player, source, definition, aim, current: current);
            return true;
        });

    public static async Task<uint> SummonGolem(Player player,
        ushort source, GolemCatalog.Definition definition, (int X, int Y) aim, double life = 1, bool arrival = false,
        Func<bool>? current = null, double summonedSecondsRemaining = 0,
        GolemState.MinionResourceSnapshot? resources = null, bool legacyArrival = false,
        GolemState.Golem? replacingOld = null, GemSourceLifetimes.SourceLifetime? retainedSource = null,
        bool claimActivation = true)
    {
        if (replacingOld is null && Monsters.Find(definition.Variety) is { Supported: true } preVariety)
        {
            var preSupported = SupportedSkill(player, source);
            if (preSupported.Effect == definition.Effect && preSupported.RequirementsMet)
            {
                byte preLevel = (byte)Math.Clamp(GolemStat(preSupported.Stats, Stat.BaseDisplayMinionActorLevel), 1, 100);
                await EnsureMonsterTypeSent(player, preVariety, preLevel);
                if (!CurrentGameWork || player.Closed || player.LeavingArea) return 0;
            }
        }
        double preparedNow;
        SupportedGem supported;
        Dictionary<uint, int> playerStats;
        FreshSummonAdmission? fresh;
        SummonReplacementAdmission? replacement;
        GemSourceLifetimes.SourceLifetime? gemSource;
        using (ProjectileMutation(player, advance: false, phase: "golem replacement preparation"))
        using (new PlayerStatScope(player, fresh: true))
        {
            preparedNow = AreaTime(player);
            supported = SupportedSkill(player, source);
            playerStats = new Dictionary<uint, int>(CharacterPlayerStats(player));
            gemSource = retainedSource ?? (supported.RequirementsMet
                ? CaptureGemSourceLifetimeLocked(player, source, definition.Effect) : null);
            fresh = replacingOld is null
                ? PrepareFreshSummonLocked(player, source, supported, playerStats, gemSource) : null;
            replacement = replacingOld is null ? null
                : PrepareReplacementLocked(player, SummonReplacementFamily.Golem, replacingOld, source,
                    definition.Effect, supported, playerStats, preparedNow);
            if (replacement is not null) gemSource = replacement.GemSource;
            if (replacingOld is not null && replacement is null) return 0;
        }
        if (supported.Effect != definition.Effect || !supported.RequirementsMet
            || gemSource is null || replacingOld is null && fresh is null
            || Monsters.Find(definition.Variety) is not { Supported: true } variety) return 0;
        int Own(uint name) => GolemStat(supported.Stats, name);
        int Player(uint name) => GolemStat(playerStats, name);
        int limit = GolemEffectiveLimit(Own(Stat.BaseNumberOfGolemsAllowed), Player);
        if (limit == 0) return 0;
        byte level = (byte)Math.Clamp(Own(Stat.BaseDisplayMinionActorLevel), 1, 100);
        double remaining = arrival && double.IsFinite(summonedSecondsRemaining)
            ? Math.Clamp(summonedSecondsRemaining, 0, 8) : arrival ? 0 : 8;
        double summonedAt = replacement?.SummonedAt ?? preparedNow - (8 - remaining);
        double freshMeat = !arrival && replacement is null ? FreshMeatSeconds(supported.Stats, double.PositiveInfinity) : 0;
        var probe = new GolemState.Golem(0, null!, definition, source, gemSource)
        {
            GemLevel = supported.Level, Quality = supported.Quality, Own = new(supported.Stats), Player = new(playerStats),
            Limit = limit, SummonedAt = summonedAt, Utility = MinionOfferingStats(player, preparedNow),
            FreshMeatUntil = replacingOld?.FreshMeatUntil ?? (freshMeat > 0 ? preparedNow + freshMeat : 0),
        };
        var mods = GolemModifiers(player, probe, playerStats, preparedNow);
        var pools = GolemPools.Calculate(Monsters, variety, level, probe, playerStats, mods.Effects);
        uint transportMaximum = pools.Fatal ? 1 : pools.MaximumLife;
        uint maximumEnergyShield = NativeEnergyShield(pools.MaximumEnergyShield);
        uint wireLife = pools.Fatal ? 1 : replacement is { } liveReplacement
            ? Math.Min(liveReplacement.Resources.Life, pools.MaximumLife)
            : resources is { } saved
            ? Math.Min(saved.Life, pools.MaximumLife)
            : (uint)Math.Clamp(pools.MaximumLife * (double.IsFinite(life) ? Math.Clamp(life, 0, 1) : 0),
                0, pools.MaximumLife);
        uint energyShield = replacement is { } liveShield
            ? Math.Min(liveShield.Resources.EnergyShield, maximumEnergyShield)
            : resources is { } savedShield
            ? Math.Min(savedShield.EnergyShield.Current, maximumEnergyShield)
            : legacyArrival ? 0 : maximumEnergyShield;
        if (!pools.Fatal)
        {
            probe.Abilities = GolemAbilities(player, variety, level, probe, playerStats, preparedNow);
            probe.Skills = GolemNativeSkills(player, level, probe.Abilities);
        }
        var at = definition.Kind == "Flame" && definition.Special && !arrival
            ? aim : NextToPlayer(player, Math.Clamp(variety.ObjectSize - 1, 0, 127));
        uint id = await SpawnMonster(player, variety, level, MonsterRarity.Normal, at.X, at.Y,
            friendly: true, mods: mods, unrisen: false, extraStats: MinionStats,
            ownedSkills: probe.Skills, ownedMaximumLife: transportMaximum, ownedMaximumEnergyShield: maximumEnergyShield,
            ownedEnergyShield: energyShield,
            damagedLife: wireLife,
            ownedInitialDeath: pools.Fatal,
            ownedCurrent: () => player.PlayerLife > 0 && (current?.Invoke() ?? true)
                && (replacement is { } admission
                    ? SummonReplacementCurrentLocked(player, admission)
                    : fresh is { } captured && FreshSummonCurrentLocked(player, captured, arrival: arrival)),
            owned: (id, m) =>
            {
                double committed = AreaTime(player);
                SummonReplacementResources? committedResources = replacement is null
                    ? null : CaptureReplacementResources(replacement.Old, committed);
                probe.Id = id; probe.Monster = m;
                if (replacement is not null)
                    TransferIncomingReplacementStateLocked(replacement.Old, probe, committed);
                if (replacement is not null) RetireReplacementLocked(player, replacement, committed);
                else if (player.Golems.Replacement(definition.Kind, limit) is { } old)
                    RemoveGolemLocked(player, old, committed, reconcileBlessing: false);
                probe.SummonedAt = replacement?.SummonedAt ?? committed - (8 - remaining);
                if (freshMeat > 0) probe.FreshMeatUntil = committed + freshMeat;
                probe.LastStep = replacement?.LastStep ?? committed;
                probe.ShieldPool.SetMaximum(maximumEnergyShield,
                    refill: committedResources is null && resources is null && !legacyArrival);
                if (committedResources is { } liveResources)
                {
                    probe.Regeneration = SavedLifeRegeneration(liveResources.LifeRegeneration);
                    probe.ShieldPool.Restore(liveResources.EnergyShieldCapture, committed);
                }
                else if (resources is { } savedResources)
                {
                    probe.Regeneration = SavedLifeRegeneration(savedResources.LifeRegeneration);
                    probe.ShieldPool.Restore(savedResources.EnergyShield, committed);
                }
                else if (legacyArrival) probe.ShieldPool.Interrupt(committed);
                m.Mods = mods;
                player.Golems.All.Add(id, probe);
                player.Minions.Add(new(id, m, definition.Id, definition.Kind) { Until = double.PositiveInfinity });
                if (claimActivation && replacement is null && gemSource?.Parent is { } marker)
                    PlayerInventories.Current(CharacterName(player))?.ClaimGemActivation(
                        GolemSaveKey, source, marker, player.VitalsSessionId);
                m.NoRewards = true; m.Aggro = true;
                if (!arrival) player.Golems.LastSummoned = committed;
                player.Golems.RefreshAt = 0;
                if (!pools.Fatal) PresentTransferredGolemDotBuffsLocked(player, probe, committed);
                if (pools.Fatal) GolemDieLocked(player, probe, committed, emitLife: false);
                else ReconcileGuardianBlessingLocked(player);
            });
        if (id == 0) return 0;
        if (!arrival && definition.Kind == "Flame" && definition.Special && !pools.Fatal)
            using (ProjectileMutation(player))
                if (player.Golems.All.TryGetValue(id, out var spawned)) GolemMeteorLocked(player, spawned, AreaTime(player));
        await RefreshGolemBuffs(player);
        SaveGolems(player);
        return id;
    }

    static void RemoveGolemLocked(Player player, GolemState.Golem golem, double now, bool reconcileBlessing = true)
    {
        player.Golems.All.Remove(golem.Id); player.Minions.Remove(golem.Id);
        player.Golems.IncomingHits.RemoveAll(hit => ReferenceEquals(hit.Target, golem));
        golem.Pending = null;
        foreach (var hazard in golem.Hazards) if (hazard.Object is uint art)
            player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(art), null, WorldId: art, RemovesObject: true));
        golem.Hazards.Clear(); golem.Pulses.Clear(); golem.ReviveAt = double.PositiveInfinity;
        golem.Monster.ActionGeneration++; golem.Monster.Dead = true; golem.Monster.Life = 0;
        if (player.Area.Monsters.TryGetValue(golem.Id, out var held) && ReferenceEquals(held, golem.Monster))
        {
            player.Area.Monsters.Remove(golem.Id); player.MinionOrigins.TryRemove(golem.Id, out _);
            player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(golem.Id), "golem removed", golem.Id,
                Kind: PlayerMinions.OutputKind.Remove, WorldId: golem.Id, RemovesObject: true));
        }
        player.Golems.RefreshAt = 0;
        if (reconcileBlessing) ReconcileGuardianBlessingLocked(player);
    }

    public static async Task GolemsTick(Player player, double now)
    {
        if (!player.Connection.PlayerSent || !CurrentGameWork || player.Closed || player.LeavingArea) return;
        // Golems are suppressed in Town; keep Restored = false so SaveGolems on leaving Town never wipes saved golems.
        if (InTown(player)) return;
        if (!player.Golems.Restored)
        {
            var guard = GolemRestoreGuards.GetOrCreateValue(player.Golems);
            if (guard.Restoring) return;
            guard.Restoring = true;
            try
            {
                GolemState.Saved[] saved;
                try { saved = JsonSerializer.Deserialize<GolemState.Saved[]>(CharacterSaves.ModuleData(CharacterName(player), GolemSaveKey) ?? "[]") ?? []; }
                catch (JsonException) { saved = []; }
                int count = Math.Min(saved.Length, 100);
                bool transientAbort = false;
                for (int i = guard.NextIndex; i < count; i++)
                {
                    if (!CurrentGameWork || player.Closed || player.LeavingArea) { transientAbort = true; break; }
                    var old = saved[i];
                    if (GolemData.Find(old.Effect) is not { } definition) { guard.NextIndex = i + 1; continue; }
                    ushort source = RestoredGolemSource(player, old);
                    if (source == 0) { guard.NextIndex = i + 1; continue; }
                    if (old.ReviveSeconds > 0)
                    {
                        var lease = CaptureGemSourceLifetimeLocked(player, source, definition.Effect);
                        if (lease?.Parent is not { } marker
                            || !GemSourceLifetimeCurrentLocked(player, source, definition.Effect, lease)
                            || PlayerInventories.Current(CharacterName(player))?.ClaimGemActivation(
                                GolemSaveKey, source, marker, player.VitalsSessionId) != true)
                        { guard.NextIndex = i + 1; continue; }
                        player.Golems.Waiting.Add(new(old with { Skill = source }, now + old.ReviveSeconds, lease));
                        guard.NextIndex = i + 1;
                        continue;
                    }
                    if (player.PlayerLife == 0) { transientAbort = true; break; }
                    int bodySize = Monsters.Find(definition.Variety) is { } v ? Math.Clamp(v.ObjectSize - 1, 0, 127) : 1;
                    uint restoredId = await SummonGolem(player, source, definition, NextToPlayer(player, bodySize),
                        double.IsFinite(old.Life) ? Math.Clamp(old.Life, 0, 1) : 0, arrival: true,
                        summonedSecondsRemaining: old.SummonedSecondsRemaining, resources: old.Resources,
                        legacyArrival: old.Resources is null);
                    if (restoredId == 0)
                    {
                        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0)
                        {
                            transientAbort = true;
                            break;
                        }
                        var check = SupportedSkill(player, source);
                        if (check.Effect == definition.Effect && check.RequirementsMet
                            && GemSkillInstanceDisabled(player, check.Effect, source, triggered: true) is null
                            && Monsters.Find(definition.Variety) is { Supported: true }
                            && GolemCatalog.Limit(GolemStat(check.Stats, Stat.BaseNumberOfGolemsAllowed),
                                n => GolemStat(CharacterPlayerStats(player), n)) > 0
                            && ++guard.Retries < MaxMinionRestoreRetries)
                        {
                            transientAbort = true;
                            break;
                        }
                    }
                    guard.NextIndex = i + 1;
                    guard.Retries = 0;
                }
                if (!transientAbort && CurrentGameWork && !player.Closed && !player.LeavingArea)
                    player.Golems.Restored = true;
            }
            finally { guard.Restoring = false; }
            if (!player.Golems.Restored) return;
        }
        if (player.Golems.All.Count == 0 && player.Golems.Waiting.Count == 0) return;
        bool changed = false;
        List<(GolemState.Awaiting Waiting, bool Current, ushort Source)> dueWaiting;
        dueWaiting = player.Golems.Waiting.Where(w => player.PlayerLife > 0 && now >= w.At)
            .Select(waiting =>
            {
                ushort source = waiting.GemSource is null ? RestoredGolemSource(player, waiting.Saved) : waiting.Saved.Skill;
                bool current = source != 0 && (waiting.GemSource is null
                    || GolemData.Find(waiting.Saved.Effect) is { } definition
                        && GemSourceLifetimeCurrentLocked(player, waiting.Saved.Skill, definition.Effect,
                            waiting.GemSource));
                return (waiting, current, source);
            }).ToList();
        player.Golems.Waiting.RemoveAll(waiting => dueWaiting.Exists(due => ReferenceEquals(due.Waiting, waiting)));
        if (dueWaiting.Count > 0) changed = true;
        foreach (var (revive, current, source) in dueWaiting.Where(due => due.Current))
            if (GolemData.Find(revive.Saved.Effect) is { } definition)
                await SummonGolem(player, source, definition,
                    NextToPlayer(player, Monsters.Find(definition.Variety) is { } rv ? Math.Clamp(rv.ObjectSize - 1, 0, 127) : 1),
                    retainedSource: revive.GemSource, claimActivation: false);
        // Deadlines run at the companion cadence, independently of the slower stat-source refresh.
        List<(GolemState.Golem Golem, bool Current)> due;
        foreach (var ended in player.Golems.All.Values.Where(g => (g.Monster.Dead || g.Monster.Life == 0) && !double.IsFinite(g.ReviveAt)))
            GolemDieLocked(player, ended, now);
        var dueActors = player.PlayerLife > 0
            ? player.Golems.All.Values.Where(g => g.Monster.Dead && now >= g.ReviveAt).ToArray() : [];
        due = dueActors.Select(golem =>
        {
            bool current = golem.GemSource is null
                || GemSourceLifetimeCurrentLocked(player, golem.Source, golem.Definition.Effect, golem.GemSource);
            RemoveGolemLocked(player, golem, now);
            return (golem, current);
        }).ToList();
        if (due.Count > 0) changed = true;
        foreach (var (dead, current) in due.Where(due => due.Current))
            await SummonGolem(player, dead.Source, dead.Definition,
                NextToPlayer(player, Monsters.Find(dead.Definition.Variety) is { } dv ? Math.Clamp(dv.ObjectSize - 1, 0, 127) : 1),
                retainedSource: dead.GemSource, claimActivation: false);
        if (player.Golems.All.Count == 0)
        {
            if (changed)
            {
                SaveGolems(player);
                await RefreshGolemBuffs(player);
                await PublishMinionOutcomes(player);
            }
            return;
        }
        var rebuild = new List<GolemState.Golem>();
        if (now >= player.Golems.RefreshAt)
        {
            using var scope = new PlayerStatScope(player, fresh: true);
            var playerStats = CharacterPlayerStats(player);
            var sources = GemSkillEffects(player).ToDictionary(g => g.Id, g => g.Effect);
            player.Golems.RefreshAt = now + .5;
            foreach (var golem in player.Golems.All.Values.ToArray())
            {
                if (!sources.TryGetValue(golem.Source, out int effect) || effect != golem.Definition.Effect)
                { RemoveGolemLocked(player, golem, now); changed = true; continue; }
                var gem = SupportedSkill(player, golem.Source);
                if (!gem.RequirementsMet || !MinionFamilyAllowed(n => GolemStat(playerStats, n), "Golem"))
                { RemoveGolemLocked(player, golem, now); changed = true; continue; }
                if (golem.Monster.Dead)
                {
                    continue;
                }
                golem.Own = new(gem.Stats); golem.GemLevel = gem.Level; golem.Quality = gem.Quality;
                golem.Limit = GolemEffectiveLimit(GolemStat(gem.Stats, Stat.BaseNumberOfGolemsAllowed), n => GolemStat(playerStats, n));
                int level = GolemStat(gem.Stats, Stat.BaseDisplayMinionActorLevel);
                if (level != golem.Monster.Level)
                {
                    rebuild.Add(golem); continue;
                }
                RefreshGolemLocked(player, golem, playerStats, now);
            }
            int limit = player.Golems.All.Values.Select(g => g.Limit).DefaultIfEmpty(0).Max();
            foreach (var surplus in player.Golems.All.Values.OrderByDescending(g => g.SummonedAt).Skip(limit).ToArray())
            { RemoveGolemLocked(player, surplus, now); changed = true; }
            await RefreshGolemBuffs(player);
        }
        foreach (var golem in rebuild)
            if (await SummonGolem(player, golem.Source, golem.Definition,
                NextToPlayer(player, LivingCollision.Size(golem.Monster), golem.Id),
                arrival: true, replacingOld: golem) != 0)
                changed = true;
        using (ProjectileMutation(player))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
            GolemIncomingContactsLocked(player, now);
            foreach (var golem in player.Golems.All.Values.ToArray())
                GolemStepLocked(player, golem, now);
        }
        if (changed) SaveGolems(player);
        await PublishMinionOutcomes(player);
    }

    // One synchronous state transition for the normal refresh and direct physical-source retirement. The caller owns
    // Monsters and supplies one fresh player snapshot; only the normal network wrapper publishes that snapshot.
    static (byte[][] Packets, bool Changed) PlanGolemBuffsLocked(Player player, Dictionary<uint, int> playerStats)
    {
        var bytes = new List<byte[]>(); var totals = new Dictionary<uint, int>();
        var alive = player.Golems.All.Values.Where(g => !g.Monster.Dead && g.Monster.Life > 0).ToArray();
        var buffs = new Dictionary<ushort, GolemState.Golem>();
        var changedRecovery = new HashSet<uint>();
        foreach (var g in alive)
        {
            int oldGuard = GolemStat(g.OwnerBuff, Stat.MeleeDamageRemovedFromStoneGolemBeforeLifeOrEsPct);
            g.OwnerBuff = [];
            if (player.PlayerLife == 0 || g.Definition.Buff == 0) continue;
            double scale = 1 + GolemCatalog.BuffEffect(g.Definition, n => GolemStat(playerStats, n), n => GolemStat(g.Own, n), alive.Length) / 100.0;
            foreach (var (source, target) in g.Definition.BuffStats)
                GolemAdd(g.OwnerBuff, target, (int)Math.Floor(GolemStat(g.Own, source) * scale));
            if (g.Definition.Buff == 592 && oldGuard != GolemStat(g.OwnerBuff, Stat.MeleeDamageRemovedFromStoneGolemBeforeLifeOrEsPct))
                changedRecovery.Add(g.Id);
            if (!buffs.TryGetValue(g.Definition.Buff, out var best) || g.OwnerBuff.Values.Sum() > best.OwnerBuff.Values.Sum())
                buffs[g.Definition.Buff] = g;
        }
        var wrath = alive.Where(g => g.WrathUntil > AreaTime(player)
                && Dist(g.Monster.Walk.Position(AreaTime(player)), player.Walk.Position(AreaTime(player))) <= 60)
            .Select(g => (Golem: g, Skill: g.Abilities.FirstOrDefault(a => a.Effect == 5906))).Where(p => p.Skill is not null)
            .OrderByDescending(p => p.Golem.Monster.Level).FirstOrDefault();
        foreach (var (row, old) in player.Golems.Buffs.ToArray())
            if (row == 676 ? wrath.Skill is null || wrath.Golem.Id != old.Source
                : !buffs.TryGetValue(row, out var next) || old.Source != next.Id || row == 592 && changedRecovery.Contains(old.Source))
            { player.Area.Buffs.Clear(player.Id, row, old.Source); player.Golems.Buffs.Remove(row); }
        foreach (var (row, g) in buffs)
        {
            if (!player.Golems.Buffs.ContainsKey(row))
            {
                player.Area.Buffs.Set(player.Id, GolemOwnerBuff(row, g));
                player.Golems.Buffs[row] = (g.Id, 1);
            }
            foreach (var (k, v) in g.OwnerBuff) totals[k] = totals.GetValueOrDefault(k) + v;
        }
        // Wrath belongs to the Lightning Golem, scales at its level, and has a finite duration/range.
        if (wrath.Skill is not null)
        {
            foreach (string kind in new[] { "attack", "spell" })
                foreach (string bound in new[] { "minimum", "maximum" })
                {
                    uint stat = Stat.Of($"{kind}_{bound}_added_lightning_damage");
                    GolemAdd(totals, stat, GolemStat(wrath.Skill.Stats, stat));
                }
            if (!player.Golems.Buffs.ContainsKey(676))
            {
                player.Area.Buffs.Set(player.Id, new(676, wrath.Golem.Id));
                player.Golems.Buffs[676] = (wrath.Golem.Id, 1);
            }
        }
        GolemAdd(totals, Stat.VirtualTotalNumberOfActiveGolems, alive.Length);
        GolemAdd(totals, Stat.VirtualCurrentNumberOfGolemTypes, alive.Select(g => g.Definition.Kind).Distinct().Count());
        foreach (var family in alive.GroupBy(g => g.Definition.Kind))
            GolemAdd(totals, Stat.Of("current_number_of_" + (family.Key == "Flame" ? "fire" : family.Key.ToLowerInvariant()) + "_golems"), family.Count());
        bool changed = !totals.OrderBy(p => p.Key).SequenceEqual(player.Golems.OwnerStats.OrderBy(p => p.Key));
        if (changed)
        {
            player.Golems.OwnerStats.Clear(); foreach (var entry in totals) player.Golems.OwnerStats.Add(entry.Key, entry.Value);
            player.Golems.Version++; MarkPlayerStats(player, PlayerStatStep.Golems); player.Minions.StatRevision++;
            player.Pools = BuildPoolsFor(player, player.Pools.Level); player.ApplyMovementSpeed();
        }
        return ([.. bytes], changed);
    }

    public static async Task RefreshGolemBuffs(Player player)
    {
        await SendWorldRecords(player, player.Id, () =>
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea) return null;
            (byte[][] Packets, bool Changed) plan;
            using (new PlayerStatScope(player, fresh: true))
                plan = PlanGolemBuffsLocked(player, new Dictionary<uint, int>(CharacterPlayerStats(player)));
            if (!plan.Changed) return plan.Packets.Length == 0 ? null : plan.Packets;
            var bytes = new List<byte[]>(plan.Packets);
            // The planner updates OwnerStats, while SkillBuildScope caches immutable character inputs for its
            // lifetime. Project through a new scope so the packet contains the reconciled counts and buffs.
            using (new PlayerStatScope(player, fresh: true))
            {
                bytes.Add(PlayerStatsOffFrames(player));
                bytes.Add(PlayerSkillsIfChanged(player, PlayerSkills(player)));
            }
            return bytes.Count == 0 ? null : [.. bytes];
        });
    }

    public static Buff GolemOwnerBuff(ushort row, GolemState.Golem golem) => new(row, golem.Id,
        Values: row == 592 ? [(uint)GolemStat(golem.OwnerBuff, Stat.MeleeDamageRemovedFromStoneGolemBeforeLifeOrEsPct)] : null,
        U82: row == 592 ? (ushort)0 : null);
}
