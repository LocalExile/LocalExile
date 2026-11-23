using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public static SummonCatalog SummonData = null!;
    const string SummonSaveKey = "Game.SummonedMinions";
    const string GuardianSaveKey = "Game.AnimatedGuardianEquipment";
    static readonly Lazy<Dictionary<int, int>> SpectreOverrides = new(() =>
        ClientTables.TableOrEmpty("SpectreOverrides").ToDictionary(row => row.Int("Monster"), row => row.Int("Spectre")));

    public static void SaveSummons(Player player)
    {
        lock (MinionSaveLocks.GetOrCreateValue(player))
        {
            string text, guardian, character;
            if (!player.Summons.Restored) return;
            double now = AreaTime(player);
            text = JsonSerializer.Serialize(player.Summons.Fighters.Values.Where(g => !g.Monster.Dead && g.Monster.Life > 0
                    && player.Summons.Definitions[g.Id].Persistent)
                .Select(g => SavedSummon(player, g, now)).ToArray());
            guardian = JsonSerializer.Serialize(player.Summons.GuardianEquipment);
            character = CharacterName(player);
            Saves.Atomically(() =>
            {
                CharacterSaves.ChangeModuleData(character, SummonSaveKey, _ => text);
                CharacterSaves.ChangeModuleData(character, GuardianSaveKey, _ => guardian);
            });
        }
    }

    static SummonedMinions.Saved SavedSummon(Player player, GolemState.Golem fighter, double now)
    {
        var gem = SupportedSkill(player, fighter.Source);
        object? sourceId = gem.SourceGem is not null ? PlayerInventories.GemIdentity(gem.SourceGem) : null;
        var place = sourceId is null ? default : ItemsOf(player).SocketedGemPlaces().FirstOrDefault(p =>
            ReferenceEquals(PlayerInventories.GemIdentity(p.Gem), sourceId));
        return new(fighter.Definition.Id, fighter.Source, fighter.Monster.Variety!.Id,
            SavedLifeFraction(fighter.Monster),
            fighter.Equipment.Count > 0 ? fighter.Equipment : null,
            CaptureResources(fighter, now),
            Host: place.ItemId == 0 ? null : place.ItemId, Socket: place.ItemId == 0 ? null : place.Socket);
    }

    internal static ushort RestoredSummonSource(Player player, SummonedMinions.Saved saved) =>
        RestoredMinionSource(player, saved.Effect, saved.Source, saved.Host, saved.Socket);

    static ushort RestoredMinionSource(Player player, string effect, ushort source, uint? host, int? socket)
    {
        var candidates = GemSkillEffects(player).Where(g => SkillsFor(player).EffectId(g.Effect) == effect).ToArray();
        if (host is uint item && socket is int index)
        {
            if (index < 0)
                return candidates.FirstOrDefault(g => SupportedSkill(player, g.Id).WornGrant is { } grant
                    && grant.Host == item && grant.Socket == index).Id;
            var place = ItemsOf(player).SocketedGemPlaces().FirstOrDefault(p => p.ItemId == item && p.Socket == index);
            if (place.Gem is null) return 0;
            object placeId = PlayerInventories.GemIdentity(place.Gem);
            return candidates.FirstOrDefault(g => SupportedSkill(player, g.Id).SourceGem is { } sg
                && ReferenceEquals(PlayerInventories.GemIdentity(sg), placeId)).Id;
        }
        var exact = candidates.FirstOrDefault(g => g.Id == source);
        return exact.Name is not null ? exact.Id : candidates.FirstOrDefault().Id;
    }

    /// <summary>How many minions one use brings: Summon Skeletons its melee, archer and mage counts together (at least
    /// one), every other summon one.</summary>
    public static int SummonCount(SummonCatalog.Definition definition, Func<uint, int> stat) =>
        definition.Family == "Skeleton" ? Math.Max(1, new[] { Stat.NumberOfMeleeSkeletonsToSummon,
            Stat.NumberOfArcherSkeletonsToSummon, Stat.NumberOfMageSkeletonsToSummon }.Sum(stat)) : 1;

    public static int SummonLimit(Player player, SummonCatalog.Definition definition, SupportedGem gem)
    {
        if (definition.LimitStat == 0) return 1;
        var playerStats = CharacterPlayerStats(player);
        uint final = definition.FinalLimitStat;
        int value = GolemStat(gem.Stats, definition.LimitStat) + GolemStat(playerStats, definition.LimitStat) + GolemStat(playerStats, final);
        // Clones have no ordinary cap; duration is their lifetime constraint.
        return Math.Clamp(value == 0 && definition.Family == "Clone" ? 100 : value, 0, 200);
    }

    static double SummonDuration(Player player, SummonCatalog.Definition definition, SupportedGem gem)
    {
        if (definition.Persistent) return double.PositiveInfinity;
        bool secondary = definition.Family == "Absolution" || definition.Family.StartsWith("Dominating", StringComparison.Ordinal);
        int ms = GolemStat(gem.Stats, definition.Family == "Spectre" ? Stat.SpectresHaveBaseDurationMs
            : secondary ? Stat.BaseSecondarySkillEffectDuration : Stat.BaseSkillEffectDuration);
        var playerStats = CharacterPlayerStats(player);
        return SkillDurationModifiers.Seconds(ms, n => GolemStat(playerStats, n), n => GolemStat(gem.Stats, n),
            DurationKinds(player, gem.Effect) | (secondary ? SkillDurationModifiers.Kinds.Secondary : 0));
    }

    static SpawnedMonster? SummonCorpse(Player player, (int X, int Y) aim, uint target, double radius = 15)
    {
        bool Allowed(SpawnedMonster m) => m.Hostile && m.Dead && !m.CorpseConsumed && !m.Ailments.Shattered
            && m.Variety?.LifeData.IsUsableAsCorpse(m.Life) == true && m.Rarity != MonsterRarity.Unique;
        if (target != 0 && player.Area.Monsters.GetValueOrDefault(target) is { } selected && Allowed(selected)) return selected;
        return player.Area.Monsters.Values.Where(Allowed).Where(m => Dist(m.Walk.Position(AreaTime(player)), aim) <= radius)
            .MinBy(m => Dist(m.Walk.Position(AreaTime(player)), aim));
    }

    // The living actor uses the raised body's row, including the old saves that recorded the ordinary monster's row.
    static MonsterCatalog.Variety RaisedSpectreVariety(MonsterCatalog.Variety corpse) =>
        SpectreOverrides.Value.TryGetValue(corpse.Row, out int row) && (uint)row < (uint)Monsters.All.Count
            ? Monsters.All[row] : corpse;

    internal static int SummonCount(SummonCatalog.Definition definition, SupportedGem gem, Random random)
    {
        if (definition.Family == "Skeleton")
            return Math.Max(1, new[] { Stat.NumberOfMeleeSkeletonsToSummon, Stat.NumberOfArcherSkeletonsToSummon,
                Stat.NumberOfMageSkeletonsToSummon }.Sum(n => GolemStat(gem.Stats, n)));
        if (definition.Family == "RagingSpirit")
            return random.Next(100) < Math.Clamp(GolemStat(gem.Stats, Stat.SummonedRagingSpiritChanceToSpawnAdditionalMinionPct), 0, 100) ? 2 : 1;
        return Math.Max(1, GolemStat(gem.Stats, Stat.NumberOfMonstersToSummon));
    }

    internal static Task<bool> CastSummon(Player player,
        ushort source, SummonCatalog.Definition definition, (int X, int Y) aim, uint target, int recovery, Func<bool> current,
        bool triggered = false, bool playerConsumesCorpse = true) =>
        RepeatSummonCast(player, source, recovery, current, triggered, async () =>
        {
            var gem = SupportedSkill(player, source);
            int count = SummonCount(definition, gem, AreaRng(player));
            for (int i = 0; i < count && current(); i++)
            {
                if (definition.Family == "Reaper" && definition.Id != "SummonReaperAltX")
                    if (player.Summons.Fighters.Values.FirstOrDefault(g => g.Source == source && g.Definition.Id == definition.Id && !g.Monster.Dead) is { } reaper
                        && reaper.Abilities.FirstOrDefault(a => a.Effect == 10584) is { } ultimate)
                    {
                        GolemReserveLocked(player, reaper, ultimate, target, player.Area.Monsters.GetValueOrDefault(target), aim, AreaTime(player));
                        return true;
                    }
                if (definition.Id is "BlinkArrow" or "BlinkArrowAltX" or "BlinkArrowAltY")
                {
                    var origin = player.Walk.Position(AreaTime(player));
                    var landing = MovementLanding(player, gem.Effect, origin, aim);
                    uint clone = await SummonFighterCore(player, source, definition,
                        origin, target, current: current, triggered: triggered, playerConsumesCorpse: playerConsumesCorpse);
                    if (clone == 0) return false;
                    await CommitTravelLanding(player, landing,
                        () => player.Summons.Fighters.ContainsKey(clone));
                }
                else await SummonFighterCore(player, source, definition, aim, target,
                    current: current, triggered: triggered, playerConsumesCorpse: playerConsumesCorpse);
            }
            SaveSummons(player);
            return true;
        });

    public static Task<uint> SummonFighter(Player player,
        ushort source, SummonCatalog.Definition definition, (int X, int Y) aim, uint target = 0,
        string? restoredVariety = null, double life = 1, Func<bool>? current = null,
        Dictionary<byte, Item>? restoredEquipment = null, double? remaining = null,
        GolemState.MinionResourceSnapshot? resources = null, bool legacyArrival = false,
        GolemState.Golem? replacingOld = null, bool triggered = false, MonsterModSet? inheritedMods = null,
        MonsterRarity rarity = MonsterRarity.Normal) =>
        SummonFighterCore(player, source, definition, aim, target, restoredVariety, life,
            current, restoredEquipment, remaining, resources, legacyArrival, replacingOld, triggered, inheritedMods: inheritedMods, rarity: rarity);

    static async Task<uint> SummonFighterCore(Player player,
        ushort source, SummonCatalog.Definition definition, (int X, int Y) aim, uint target = 0,
        string? restoredVariety = null, double life = 1, Func<bool>? current = null,
        Dictionary<byte, Item>? restoredEquipment = null, double? remaining = null,
        GolemState.MinionResourceSnapshot? resources = null, bool legacyArrival = false,
        GolemState.Golem? replacingOld = null, bool triggered = false, SpawnedMonster? consumedCorpse = null, bool playerConsumesCorpse = true, MonsterModSet? inheritedMods = null,
        MonsterRarity rarity = MonsterRarity.Normal, IReadOnlyDictionary<uint, int>? retainedStats = null,
        AnimatedSource? preparedEquipment = null)
    {
        SupportedGem gem;
        Dictionary<uint, int> playerStats;
        FreshSummonAdmission? fresh;
        SummonReplacementAdmission? replacement;
        GemSourceLifetimes.SourceLifetime? gemSource;
        using (ProjectileMutation(player, advance: false, phase: "summon replacement preparation"))
        using (new PlayerStatScope(player, fresh: true))
        {
            gem = SupportedSkill(player, source);
            playerStats = new Dictionary<uint, int>(CharacterPlayerStats(player));
            gemSource = gem.RequirementsMet
                ? CaptureGemSourceLifetimeLocked(player, source, gem.Effect) : null;
            fresh = replacingOld is null
                ? PrepareFreshSummonLocked(player, source, gem, playerStats, gemSource) : null;
            replacement = replacingOld is null ? null
                : PrepareReplacementLocked(player, SummonReplacementFamily.Summon, replacingOld, source,
                    gem.Effect, gem, playerStats, AreaTime(player));
            if (replacingOld is not null && replacement is null) return 0;
        }
        if (!gem.RequirementsMet || gemSource is null || replacingOld is null && fresh is null
            || SkillsFor(player).EffectId(gem.Effect) != definition.Id) return 0;
        if (definition.Family == "Phantasm") gem = PhantasmGem(player, gem);
        inheritedMods ??= replacingOld?.InheritedMods;
        if (replacingOld is not null) rarity = replacingOld.Monster.Rarity;
        // A replacement keeps the stats its body was given at summon (a Vaal ascension's upgrade).
        retainedStats ??= replacingOld?.RetainedSkillStats;
        if (retainedStats is not null) gem = gem with { Stats = MergeSummonStats(gem.Stats, retainedStats) };
        int limit = SummonLimit(player, definition, gem);
        if (limit < 1) return 0;
        if (resources is { Life: 0 } || legacyArrival && (!double.IsFinite(life) || life <= 0)) return 0;
        SpawnedMonster? corpse = consumedCorpse;
        MonsterCatalog.Variety? corpseVariety = null;
        AnimatedSource? animated = null;
        string path = restoredVariety ?? definition.Variety;
        if (restoredVariety is null && definition.Corpse)
        {
            corpse = SummonCorpse(player, aim, target);
            if (corpse is null) return 0;
            if (definition.Family == "Spectre") corpseVariety = corpse.Variety;
            aim = corpse.Walk.Position(AreaTime(player));
        }
        if (definition.Family is "AnimatedWeapon" or "VaalAnimatedWeapon" or "AnimatedGuardian" or "Clone")
        {
            animated = preparedEquipment ?? AnimatedItems(player, definition, gem, aim, target, restoredEquipment);
            if (animated is null) return 0;
            if (animated.Ground is { } item) aim = (item.GridX, item.GridY);
        }
        if (definition.Id == "SummonSkeletonsAltY") path = $"Metadata/Monsters/RaisedSkeletons/RaisedSkeletonSpellcaster{AreaRng(player).Next(1, 4)}Jewel";
        if (definition.Id == "SummonHolyRelicAltX") path = "Metadata/Monsters/Relic/HolyRelicSkull";
        if (definition.Family == "Clone" && restoredVariety is null)
        {
            string variant = definition.Id.EndsWith("AltX", StringComparison.Ordinal) ? "RainOfArrows"
                : definition.Id.EndsWith("AltY", StringComparison.Ordinal) ? "ElementalShot" : "";
            path = "Metadata/Monsters/Clone/" + (player.Character.ClassRow switch { 0 => "Marauder", 1 => "Witch", 2 => "Scion", 3 => "Ranger", 4 => "Duelist", 5 => "Shadow", _ => "Templar" })
                + "CloneImmobile" + variant + (player.Character.ClassRow == 2 && variant.Length > 0 ? "__" : "");
        }
        var variety = corpseVariety ?? Monsters.Find(path);
        if (definition.Family == "Spectre" && variety is not null)
        {
            variety = RaisedSpectreVariety(variety);
            path = variety.Id;
        }
        if (variety is not { Supported: true }) return 0;
        int actorLevel = GolemStat(gem.Stats, definition.Family == "Spectre" ? Stat.RaisedSpectreLevel : Stat.BaseDisplayMinionActorLevel);
        if (actorLevel <= 0 && (definition.Family.StartsWith("Dominating", StringComparison.Ordinal) || definition.Family == "VaalDomination"))
            actorLevel = SkillsFor(player).PlayerLevelReq(gem.Effect, gem.Level);
        byte level = (byte)Math.Clamp(actorLevel, 1, 100);
        if (inheritedMods is null && definition.Family == "RagingSpirit")
            inheritedMods = MonsterModRolls.Create(MonsterModRolls.Species(variety.SpeciesModsOrEmpty, Random.Shared),
                [], MonsterRarity.Normal, level);
        var native = new GolemCatalog.Definition(gem.Effect, definition.Id, definition.Family, path, 0, [], false, false, false);
        double freshMeat = remaining is null && replacement is null && restoredVariety is null && resources is null && !legacyArrival
            && GolemStat(gem.Stats, FreshMeatDurationPercent) > 0
            ? FreshMeatSeconds(gem.Stats, SummonDuration(player, definition, gem)) : 0;
        var fighter = new GolemState.Golem(0, null!, native, source)
        { GemLevel = gem.Level, Quality = gem.Quality, Own = new(gem.Stats), Player = new(playerStats), Limit = limit,
            FreshMeatUntil = replacingOld?.FreshMeatUntil ?? (freshMeat > 0 ? AreaTime(player) + freshMeat : 0),
            InheritedMods = inheritedMods ?? MonsterModSet.None, RetainedSkillStats = retainedStats is null ? [] : new(retainedStats),
            SourceIdentity = gem.SourceGem is null ? null : PlayerInventories.GemIdentity(gem.SourceGem) };
        if (animated is not null)
        {
            fighter.Equipment = animated.Equipment;
            var items = definition.Family == "Clone" ? fighter.Equipment.Where(p => p.Key is 2 or 3).Select(p => p.Value) : fighter.Equipment.Values;
            fighter.Gear = ItemModStats.Sum(items, ItemsFor(player).Mods);
            if (fighter.Equipment.GetValueOrDefault((byte)2) is { } item) fighter.Weapon = WeaponIn(player, (item, ItemsFor(player).ByHash(item.TypeHash)));
        }
        fighter.Utility = MinionOfferingStats(player, AreaTime(player));
        var mods = GolemModifiers(player, fighter, playerStats);
        var pools = GolemPools.Calculate(Monsters, variety, level, fighter, playerStats, mods.Effects);
        uint transportMaximum = pools.Fatal ? 1 : pools.MaximumLife;
        uint maximumEnergyShield = NativeEnergyShield(pools.MaximumEnergyShield);
        uint wireLife = pools.Fatal ? 1 : replacement is { } liveReplacement
            ? Math.Min(liveReplacement.Resources.Life, pools.MaximumLife)
            : resources is { } saved
            ? Math.Min(saved.Life, pools.MaximumLife)
            : (uint)Math.Clamp(pools.MaximumLife *
                (double.IsFinite(life) ? Math.Clamp(life, 0, 1) : 0), 0, pools.MaximumLife);
        uint energyShield = replacement is { } liveShield
            ? Math.Min(liveShield.Resources.EnergyShield, maximumEnergyShield)
            : resources is { } savedShield
            ? Math.Min(savedShield.EnergyShield.Current, maximumEnergyShield)
            : legacyArrival ? 0 : maximumEnergyShield;
        if (!pools.Fatal)
        {
            fighter.Abilities = GolemAbilities(player, variety, level, fighter, playerStats);
            fighter.Skills = GolemNativeSkills(player, level, fighter.Abilities);
        }
        if (!player.Layout.WalkableFor(aim.X, aim.Y, 0)) aim = NextToPlayer(player);
        var consumed = corpse;
        uint id = await SpawnMonster(player, variety, level, rarity, aim.X, aim.Y,
            friendly: true, mods: mods, unrisen: false, extraStats: MinionStats,
            ownedSkills: fighter.Skills, ownedMaximumLife: transportMaximum, ownedMaximumEnergyShield: maximumEnergyShield,
            ownedEnergyShield: energyShield,
            damagedLife: wireLife,
            ownedInitialDeath: pools.Fatal,
            ownedLooks: SummonLooks(fighter),
            ownedAdmission: () => (replacement is null || SummonReplacementCurrentLocked(player, replacement))
                && (animated?.GuardianGear is not { } guardian || AnimatedGuardianGearCurrentLocked(player, guardian))
                && ConsumeAnimatedSourceLocked(player, animated),
            ownedCurrent: () => player.PlayerLife > 0 && (current?.Invoke() ?? true) && (consumed is null || !consumed.CorpseConsumed)
                && (replacement is { } admission
                    ? SummonReplacementCurrentLocked(player, admission)
                    : fresh is { } captured && FreshSummonCurrentLocked(player, captured)
                        && GemSkillInstanceDisabled(player, gem.Effect, source, triggered: triggered || restoredVariety is not null) is null),
            owned: (id, monster) =>
            {
                if (consumed is not null) ConsumeSummonCorpseLocked(player, consumed, playerConsumesCorpse);
                if (animated?.Ground is not null)
                    player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(animated.Id), "animated item consumed", WorldId: animated.Id, RemovesObject: true));
                if (animated?.BladeSource is not null)
                    player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(animated.Id), "lingering blade animated"));
                if (definition.Family == "AnimatedGuardian") player.Summons.GuardianEquipment = new(fighter.Equipment);
                bool SameLimit(GolemState.Golem g) => g.Definition.Kind == definition.Family && !g.Monster.Dead
                    && (definition.Family != "Clone" || player.Summons.Definitions[g.Id].LimitStat == definition.LimitStat);
                while (replacement is null && player.Summons.Fighters.Values.Count(SameLimit) >= limit)
                    RemoveSummonLocked(player, player.Summons.Fighters.Values.Where(SameLimit)
                        .MinBy(g => g.SummonedAt)!, AreaTime(player), reconcileBlessing: false);
                double committed = AreaTime(player);
                SummonReplacementResources? committedResources = replacement is null
                    ? null : CaptureReplacementResources(replacement.Old, committed);
                fighter.Id = id; fighter.Monster = monster;
                if (replacement is not null)
                    TransferIncomingReplacementStateLocked(replacement.Old, fighter, committed);
                if (replacement is not null) RetireReplacementLocked(player, replacement, committed);
                fighter.SummonedAt = replacement?.SummonedAt ?? committed;
                if (freshMeat > 0) fighter.FreshMeatUntil = committed + freshMeat;
                fighter.LastStep = replacement?.LastStep ?? committed;
                fighter.ShieldPool.SetMaximum(maximumEnergyShield,
                    refill: committedResources is null && resources is null && !legacyArrival);
                if (committedResources is { } liveResources)
                {
                    fighter.Regeneration = SavedLifeRegeneration(liveResources.LifeRegeneration);
                    fighter.ShieldPool.Restore(liveResources.EnergyShieldCapture, committed);
                }
                else if (resources is { } savedResources)
                {
                    fighter.Regeneration = SavedLifeRegeneration(savedResources.LifeRegeneration);
                    fighter.ShieldPool.Restore(savedResources.EnergyShield, committed);
                }
                else if (legacyArrival) fighter.ShieldPool.Interrupt(committed);
                monster.Mods = mods;
                player.Summons.Fighters.Add(id, fighter); player.Summons.Definitions[id] = definition;
                player.Summons.Until[id] = remaining is double duration ? committed + duration
                    : replacement?.Until ?? committed + SummonDuration(player, definition, gem);
                player.Minions.Add(new(id, monster, definition.Id, definition.Family) { Until = player.Summons.Until[id] });
                monster.NoRewards = true; monster.Aggro = true;
                if (definition.Id == "RaiseZombieAltY" && fighter.Abilities.FirstOrDefault(a => a.Effect == 7133) is { } fall)
                    GolemReserveLocked(player, fighter, fall, 0, null, aim, AreaTime(player));
                player.Minions.StatRevision++;
                if (!pools.Fatal) PresentTransferredGolemDotBuffsLocked(player, fighter, committed);
                if (pools.Fatal) GolemDieLocked(player, fighter, AreaTime(player), emitLife: false);
                else ReconcileGuardianBlessingLocked(player);
            });
        return id;
    }

    // Creating and refreshing a summon share one receipt. A proxy's consumption cannot recharge its owner's flask.
    static void ConsumeSummonCorpseLocked(Player player, SpawnedMonster corpse, bool playerConsumesCorpse = true)
    {
        if (corpse.CorpseConsumed) return;
        corpse.CorpseConsumed = true;
        if (playerConsumesCorpse) NoteCorpseConsumedLocked(player, corpse);
    }

    static void RemoveSummonLocked(Player player, GolemState.Golem fighter, double now, bool reconcileBlessing = true)
    {
        player.Summons.Fighters.Remove(fighter.Id); player.Summons.Until.Remove(fighter.Id);
        player.Summons.Definitions.Remove(fighter.Id); player.Summons.Weapons.Remove(fighter.Id);
        RemoveGolemLocked(player, fighter, now, reconcileBlessing);
        player.Minions.StatRevision++;
    }

    public static async Task SummonsTick(Player player, double now)
    {
        await HitSummonsTick(player, now);
        var state = player.Summons;
        if (!state.Restored)
        {
            state.Restored = true;
            var saved = CharacterSaves.Load(CharacterName(player))?.ModuleData?.GetValueOrDefault(SummonSaveKey);
            var guardian = CharacterSaves.Load(CharacterName(player))?.ModuleData?.GetValueOrDefault(GuardianSaveKey);
            if (guardian is not null)
                try { state.GuardianEquipment = JsonSerializer.Deserialize<Dictionary<byte, Item>>(guardian) ?? []; }
                catch (JsonException) { state.GuardianEquipment = []; }
            if (saved is not null)
            {
                SummonedMinions.Saved[] restore;
                try { restore = JsonSerializer.Deserialize<SummonedMinions.Saved[]>(saved) ?? []; }
                catch (JsonException) { restore = []; }
                foreach (var old in restore)
                    if (SummonData.Find(old.Effect) is { Persistent: true } definition)
                    {
                        ushort source = RestoredSummonSource(player, old);
                        if (source != 0) await SummonFighter(player, source, definition,
                            NextToPlayer(player), restoredVariety: old.Variety, life: old.Life, restoredEquipment: old.Equipment,
                            resources: old.Resources, legacyArrival: old.Resources is null);
                    }
            }
        }
        if (state.Fighters.Count == 0 && state.HolyBuff == 0) return;
        bool changed = false;
        var rescaled = new List<(GolemState.Golem Old, SummonCatalog.Definition Definition,
            Dictionary<byte, Item>? Equipment, string Variety, (int X, int Y) Aim)>();
        using (ProjectileMutation(player))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return;
            if (state.HolyBuff != 0 && now >= state.HolyRegenerationUntil)
            {
                player.Area.Buffs.ClearRow(player.Id, 1181);
                state.HolyBuff = 0; state.HolyRegenerationPerMinute = 0; player.Minions.StatRevision++;
            }
            var playerStats = now >= state.RefreshAt && state.Fighters.Count > 0 ? CharacterPlayerStats(player) : null;
            if (playerStats is not null) state.RefreshAt = now + .5;
            foreach (var fighter in state.Fighters.Values.ToArray())
            {
                if (player.PlayerLife == 0 || fighter.Monster.Dead || fighter.Monster.Life == 0 || now >= state.Until[fighter.Id])
                {
                    RemoveSummonLocked(player, fighter, now); changed = true; continue;
                }
                if (playerStats is null) { GolemStepLocked(player, fighter, now); continue; }
                var gem = SupportedSkill(player, fighter.Source);
                if (fighter.Definition.Kind == "Phantasm") gem = PhantasmGem(player, gem);
                if (fighter.Definition.Kind == "Phantasm" && GolemStat(gem.Stats, Stat.BaseNumberOfSupportGhostsAllowed) <= 0)
                { RemoveSummonLocked(player, fighter, now); changed = true; continue; }
                if (fighter.SourceIdentity is not null && (fighter.Definition.Kind == "Phantasm" || SentinelNativeType(fighter.Definition.Kind) != 0)
                    && (gem.SourceGem is null || !ReferenceEquals(fighter.SourceIdentity, PlayerInventories.GemIdentity(gem.SourceGem)))
                    || SkillsFor(player).EffectId(gem.Effect) != fighter.Definition.Id || !gem.RequirementsMet)
                { RemoveSummonLocked(player, fighter, now); changed = true; continue; }
                gem = gem with { Stats = MergeSummonStats(gem.Stats, fighter.RetainedSkillStats) };
                int actorLevel = GolemStat(gem.Stats, fighter.Definition.Kind == "Spectre" ? Stat.RaisedSpectreLevel : Stat.BaseDisplayMinionActorLevel);
                if (actorLevel <= 0 && (fighter.Definition.Kind.StartsWith("Dominating", StringComparison.Ordinal) || fighter.Definition.Kind == "VaalDomination"))
                    actorLevel = SkillsFor(player).PlayerLevelReq(gem.Effect, gem.Level);
                if (actorLevel > 0 && actorLevel != fighter.Monster.Level)
                {
                    rescaled.Add((fighter, state.Definitions[fighter.Id],
                        fighter.Equipment.Count > 0 ? fighter.Equipment : null, fighter.Monster.Variety!.Id,
                        fighter.Monster.Walk.Position(now)));
                    continue;
                }
                fighter.Own = new(gem.Stats); fighter.GemLevel = gem.Level; fighter.Quality = gem.Quality;
                RefreshGolemLocked(player, fighter, playerStats, now);
                GolemStepLocked(player, fighter, now);
            }
        }
        foreach (var (old, definition, equipment, variety, aim) in rescaled)
            if (await SummonFighter(player, old.Source, definition, aim,
                restoredVariety: variety, restoredEquipment: equipment, replacingOld: old) != 0)
                changed = true;
        if (changed) SaveSummons(player);
    }

    static void AddSummonCounts(Player player, StatMap stats)
    {
        foreach (var (rarity, count) in new[] { (MonsterRarity.Normal, Stat.NumberOfDominatedNormalMonsters),
            (MonsterRarity.Magic, Stat.NumberOfDominatedMagicMonsters), (MonsterRarity.Rare, Stat.NumberOfDominatedRareMonsters) })
            stats[count] = player.Summons.Fighters.Values.Count(g => g.Definition.Kind == "Dominating" + rarity && !g.Monster.Dead);
        if (Stat.NumberOfDominatedVaalMonsters is var ascended)
            stats[ascended] = player.Summons.Fighters.Values.Count(g => g.Definition.Kind == "VaalDomination" && !g.Monster.Dead);
        if (Stat.NumberOfDominatedMonsters is var dominated)
            stats[dominated] = player.Summons.Fighters.Values.Count(g => g.Definition.Kind.StartsWith("Dominating", StringComparison.Ordinal) && !g.Monster.Dead);
        foreach (var (kind, key) in SummonCountNames)
            stats[key] = player.Summons.Fighters.Values.Count(g => g.Definition.Kind == kind && !g.Monster.Dead && g.Monster.Life > 0);
    }
    static readonly IReadOnlyDictionary<string, uint> SummonCountNames = new Dictionary<string, uint>
    {
        ["Absolution"] = Stat.NumberOfActiveAbsolutionSentinels, ["VaalAbsolution"] = Stat.NumberOfActiveVaalAbsolutionSentinels,
        ["Phantasm"] = Stat.NumberOfActivePhantasms, ["Zombie"] = Stat.NumberOfActiveZombies, ["Spectre"] = Stat.NumberOfActiveSpectres,
        ["Skeleton"] = Stat.NumberOfActiveSkeletons, ["RagingSpirit"] = Stat.NumberOfActiveRagingSpirits, ["HolyRelic"] = Stat.NumberOfActiveRelics,
        ["Reaper"] = Stat.NumberOfActiveReapers, ["VaalAnimatedWeapon"] = Stat.NumberOfActiveVaalAnimatedWeapons,
        ["AnimatedWeapon"] = Stat.NumberOfActiveAnimatedWeapons, ["AnimatedGuardian"] = Stat.NumberOfActiveAnimatedArmour,
    };
}
