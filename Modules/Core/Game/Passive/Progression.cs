using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;

public static partial class Game
{
    // Called before inventory loading or any save normalization on a real connection.
    public static void CheckAscendancyClientCompatibility(Player player) =>
        AscendancyClientCompatibility.RequirePlayable(player, CharacterSaves.Load(CharacterName(player))?.MainAscendancy?.Selection);

    // Read-only admission checks, shared with LoadProgress: no inventory normalization or gold migration. A saved
    // Intuitive Leap/Thread of Hope needs the saved items to prove its bypass nodes, so PlayerInventories.For may load
    // them read-only here; a save-less character has no passives and does not build a start kit.
    public static void ValidateSavedProgress(Player player, CharacterSaves.Save? save)
    {
        // Without the tree the saved passives can't be checked; dropping them would lose them at the next save.
        if (save?.Passives is { Count: > 0 } && !PassivesFor(player).Loaded)
            throw new UnknownSavedItemException("allocated passives, but the passive tree (the PassiveSkills client table) is not loaded");
        var ordinary = save?.Passives ?? [];
        var ascendancy = save?.MainAscendancy ?? new();
        AscendancyPolicies.RequireAvailable(player, ascendancy.Selection);
        if (ordinary.Any(n => PassivesFor(player).Find(n) is not { IsAscendancy: false }))
            throw new UnknownSavedItemException("unknown or ascendancy nodes in the ordinary passive allocation; the save was retained");
        Game.AscendancyRules.Validate((byte)SkillsFor(player).ClassFor(player.Character.ClassRow).IntegerId, ascendancy);
        if (save?.Descendancy is { } descRow && descRow != 0)
        {
            int mainSpent = Game.AscendancyRules.SpentPoints((byte)SkillsFor(player).ClassFor(player.Character.ClassRow).IntegerId, ascendancy);
            Game.Descendancies.Validate(descRow, save.DescendancyNodes ?? [], mainSpent, ascendancy.EarnedPoints,
                DescendancyCatalog.BonusPoints(descRow));
        }
        ValidateOrdinaryPassives(player, ordinary, ascendancy);
    }

    // The character's experience and passives from its save; returns its level. The saved experience decides the level: the
    // account's (the ticket's) is only a copy for the character list, which a failed update at a level-up leaves behind. A save
    // without experience (an older one) starts at the account's level. Passives beyond the level's points are dropped.
    public static int LoadProgress(Player player, int level)
    {
        ImportedProgress.Restore(CharacterName(player));
        var save = CharacterSaves.Load(CharacterName(player));
        // Quest flags whose refund points were never credited (QuestStaticRewards; a save from before the credit): now, once.
        if (save is not null && QuestStaticRewards.Default.NewRefunds(save.QuestFlags ?? [], save.QuestRefundsCredited ?? []).Points > 0)
        {
            CharacterSaves.CreditQuestRefunds(CharacterName(player));
            save = CharacterSaves.Load(CharacterName(player));
        }
        level = CharacterSaves.LevelOf(save, level);
        ValidateSavedProgress(player, save);
        var ordinary = save?.Passives ?? [];
        var ascendancy = save?.MainAscendancy ?? new();
        // Gold is the account's (AccountStash.Gold): gold an older save kept with the character moves into the account, in one
        // transaction with the save letting go of it.
        if (save?.Gold is > 0 and uint oldGold)
        {
            uint now = Saves.Atomically(() =>
            {
                uint gold = PersonalStashOf(player).AddGold(oldGold);
                CharacterSaves.TakeGold(CharacterName(player));
                return gold;
            });
            Console.WriteLine($"[gateway] {CharacterName(player)}: {oldGold} gold from the character save moved to the account, now {now}");
        }
        player.Character.MainAscendancy = ascendancy;
        player.Character.DescendancyRow = save?.Descendancy ?? 0;
        // The layer's start is implicit, like an ascendancy start: sending it in 0x33 would allocate it and spend a point.
        player.Character.DescendancyNodes.Clear();
        foreach (ushort n in save?.DescendancyNodes ?? []) player.Character.DescendancyNodes.Add(n);
        AscendancyPolicies.SelectionChanged(player);
        player.Character.Passives = [.. ordinary];
        var atlasPresets = save?.AtlasPassivePresets ?? [];
        for (int i = 0; i < player.Character.AtlasPassives.Length; i++)
            player.Character.AtlasPassives[i] = i < atlasPresets.Count ? [.. atlasPresets[i].Where(n => AtlasPassiveNodes.Find(n) is not null).Distinct().Order()] : [];
        player.Character.AtlasPassivePoints = save?.AtlasPassivePoints ?? 0;

        // The Atlas layout picked on the map device is a character choice; fall back to the first starting node.
        if (save?.SelectedAtlasLayout is { } atlasId && MapDevices.Atlas is { } atlas
            && atlas.Find(atlasId) is { } selected && CharacterName(player) is { } atlasCharacter)
            atlas.Select(atlasCharacter, selected);
        player.Character.Waypoints.UnionWith(save?.Waypoints ?? []);
        player.Character.Visited.UnionWith(save?.Visited ?? []);
        // Legacy saves (before OpeningTutorial) and imported builds have no Tutorial record: keep their tutorials
        // disabled without rewriting the save, while new characters start with tutorials active until skipped or completed.
        var initialFlags = save is { Tutorial: null } ? Quests.EveryCharacter.Concat(Quests.AllTutorialFlags) : Quests.EveryCharacter;
        player.Character.UnearnedQuestFlags = [.. initialFlags.Except(save?.QuestFlags ?? [])];
        // An unearned endgame flag is not the character's (Quests.EndgameOnly): PlayerQuestFlags sends it where it is needed.
        player.Character.QuestFlags = [.. save?.QuestFlags ?? [], .. initialFlags.Where(f => !(Quests.EndgameOnly.Contains(f) && player.Character.UnearnedQuestFlags.Contains(f)))];
        // The league's (SetLeagueQuestFlags): every character of the account in this league shares them.
        player.Character.LeagueQuestFlags.UnionWith(PersonalStashOf(player).LeagueQuestFlags);
        player.Character.ExtraPassivePoints = save?.ExtraPassivePoints ?? 0;
        player.Character.RefundPoints = save?.RefundPoints ?? 0;
        // The chosen Pantheon gods (the Pantheon module chooses and applies them; the Player object shows them).
        player.Character.PantheonMajor = save?.PantheonMajor;
        player.Character.PantheonMinor = save?.PantheonMinor;
        RestoreGod(player);
        // The cluster jewel nodes are the client's own layout ids, so they come back only as far as the socketed
        // jewels still have room for them; with no cluster jewel they are dropped rather than listed for nodes the
        // client cannot resolve.
        int clusterBudget = ClusterNodeBudget(player);
        player.Character.ClusterPassives = clusterBudget > 0 ? [.. (save?.ClusterPassives ?? []).Take(clusterBudget)] : [];
        player.Character.Experience = save?.Experience ?? CharacterStats.ExperienceFor(level);
        TrimPassives(player, level);
        player.Character.MasteryEffects = (save?.MasteryEffects ?? []).Where(kv => player.Character.Passives.Contains(kv.Key)
            && PassivesFor(player).FindEffect(kv.Value) is { } effect && PassivesFor(player).IsEffectFor(kv.Key, effect))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        player.Character.Tattoos = (save?.Tattoos ?? []).Where(kv => PassivesFor(player).Find(kv.Key) is not null
            && TattooCatalogue.Find(kv.Value) is not null).ToDictionary(kv => kv.Key, kv => kv.Value);
        // A random tattoo (Journey Makanui's keystone, a Forbidden ascendancy notable) keeps its saved pick; one
        // whose pick did not survive validation is dropped with the tattoo.
        bool RandomPickSaved(ushort node, ushort chosen)
        {
            if (player.Character.Tattoos.GetValueOrDefault(node) is not { } name) return false;
            if (name == "Journey Tattoo of Makanui") return JourneyMakanuiCatalogue.FindByGraphId(chosen) is not null;
            return ForbiddenTattoo.IsForbidden(name) && ForbiddenTattoo.Pool(name)?.Contains(chosen) == true;
        }
        player.Character.TattooKeystones = (save?.TattooKeystones ?? [])
            .Where(kv => RandomPickSaved(kv.Key, kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (ushort node in player.Character.Tattoos
            .Where(kv => kv.Value == "Journey Tattoo of Makanui" || ForbiddenTattoo.IsForbidden(kv.Value))
            .Where(kv => !player.Character.TattooKeystones.ContainsKey(kv.Key))
            .Select(kv => kv.Key).ToList())
            player.Character.Tattoos.Remove(node);
        player.Character.TattooRevision++;
        player.Character.SavedPassiveProgress = new(player.Character.MainAscendancy, player.Character.Passives, player.Character.MasteryEffects, player.Character.RefundPoints);
        // Persist the pre-existing ordinary level/mastery normalization once. The compare protects a newer connection.
        if (!player.Character.SavedPassiveProgress.Matches(save ?? new CharacterSaves.Save([])))
            CharacterSaves.SetMainAscendancy(CharacterName(player), player.Character.MainAscendancy, player.Character.Passives, player.Character.MasteryEffects,
                expected: CharacterSaves.PassiveProgressSnapshot.From(save));
        player.Pools = PoolsFor(player, level);
        RestoreRememberedAuras(player, save?.ActiveAuras ?? []);
        player.ApplyMovementSpeed();
        return level;
    }

    // The character's stats from its allocated passives and the global mods of its active equipped items (ItemModStats),
    // by client stat key. Worn items whose requirements the class, the passives and the other active items do not meet are
    // inactive (ItemModStats.Inactive): remembered per character for the weapon profile and the gem skills.
    public static (Dictionary<uint, int> Stats, AttributeModifiers Attributes) CharacterStatSources(Player player,
        IEnumerable<ushort>? nodes = null, int? level = null)
    {
        var before = InactiveItems.GetValueOrDefault(CharacterName(player));
        var exemptions = GameEvents.ExemptItems(player);
        if (exemptions.Count > 0)
            before = new HashSet<uint>((before ?? []).Where(id => !exemptions.Contains(id)));
        var equipment = ItemsOf(player).EquipmentSnapshot();
        var (sources, attributes, inactive, worn) = EquipmentStatSources(player, equipment, nodes, level);
        if (!player.Closed && !player.LeavingArea && !player.RageRetired)
        {
            // A queued old-area calculation must not replace the new connection's qualification snapshot.
            InactiveItems[CharacterName(player)] = inactive;
            if (inactive.Count > 0 && (before is null || !before.SetEquals(inactive)))
                Console.WriteLine($"[gateway] {CharacterName(player)}: inactive items (requirements not met): " +
                    string.Join(", ", worn.Where(w => inactive.Contains(w.Id)).Select(w =>
                        $"{w.Id} {ItemsFor(player).ByHash(w.Item.TypeHash)?.Name} (str {w.Strength}, dex {w.Dexterity}, int {w.Intelligence}, level {w.Level})")));
        }
        player.SourcePhasing = player.StatSourcesCache?.Key.Flags?.GetValueOrDefault("source_phasing") == true;
        if (player.SourcePhasing)
            foreach (uint key in new[] { Stat.PhaseThroughObjects, Stat.VirtualPhaseThroughObjects })
                sources[key] = Math.Max(1, sources.GetValueOrDefault(key));
        foreach (var (key, value) in MadnessRawStatsLocked(player, sources))
            sources[key] = ActorScale.Saturate((long)sources.GetValueOrDefault(key) + value);
        ConfigureChargeDegeneration(player, worn, inactive);
        ConfigureRecentHitLifeLoss(player, worn, inactive);
        sources = WithAvatarOfFire(player, sources);
        sources = WithHerEmbrace(player, sources);
        sources = WithElusive(player, sources);
        sources = WithTinctures(player, sources);
        sources = WithTimedBuffs(player, sources);
        sources = WithLinks(player, GameEvents.WithPlayerStatSources(player, WithQuestStats(player, WithAntiquarian(player, WithFortification(player, WithCharges(player, WithSupportGemBuffs(player, sources))), equipment, inactive))));
        if (sources.GetValueOrDefault(Stat.MovementSpeedPlusPctPerNearbyEnemy) is int perEnemy and not 0)
            sources[Stat.BaseMovementVelocityPlusPct] = ActorScale.Saturate((long)sources.GetValueOrDefault(Stat.BaseMovementVelocityPlusPct)
                + (long)Math.Min(8, sources.GetValueOrDefault(Stat.NumberOfNearbyEnemies)) * perEnemy);
        // The stat buffs on the player's entity in its area (AreaStatBuffs): sources like any other, so the pools have them.
        if (player.Frames.Area is { } statArea) statArea.AddStatBuffs(new EntityId(player.Id), sources);
        AddWarcryStats(player, sources);
        sources = WithSummonBuffs(player, sources);
        sources = WithRighteousFireSpellBonus(player, sources);
        sources = WithSpreadPoisonRegeneration(player, sources);
        if (sources.GetValueOrDefault(Stat.AdditiveModifiersToMinionAttackSpeedAlsoAffectYou) != 0)
            sources[Stat.AttackSpeedPlusPct] = ActorScale.Saturate((long)sources.GetValueOrDefault(Stat.AttackSpeedPlusPct)
                + sources.GetValueOrDefault(Stat.MinionAttackSpeedPlusPct) + sources.GetValueOrDefault(Stat.MinionAttackAndCastSpeedPlusPct));
        ApplyRecoveryKeystoneStats(sources);
        double now = AreaTime(player);
        ApplyZombieDamageBonus(player, sources, now);
        int mapBuffTime = MapModsOf(player)?.Get(Stat.MapPlayerBuffTimePassedPlusPctOnlyBuffCategory) ?? 0;
        int Get(uint k) => k == Stat.BuffTimePassedPlusPctOnlyBuffCategory
            ? ActorScale.Saturate((long)sources.GetValueOrDefault(k) + mapBuffTime)
            : sources.GetValueOrDefault(k);
        FortificationOf(player).SetExpiryRate(now, RecipientEffectTime.Rate(1, Get));
        player.UtilityFlaskEffects.SetExpiryRate(now, RecipientEffectTime.Rate(16, Get));
        player.ElementalAilments.SetExpiryRate(now, RecipientEffectTime.Rate(2, Get));
        MadnessExpiryRateLocked(player, AreaTime(player), RecipientEffectTime.Rate(2, Get));
        return (sources, attributes);
    }

    // Shared equipment/passive resolver. The requirement snapshot uses this without publishing caches or pools.
    public static (Dictionary<uint, int> Sources, AttributeModifiers Attributes, HashSet<uint> Inactive,
        List<ItemModStats.Worn> Worn) EquipmentStatSources(
        Player player, IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        IEnumerable<ushort>? nodes = null, int? level = null) =>
        EquipmentStatSources(player, equipment, nodes, level, null);

    public static (Dictionary<uint, int> Sources, AttributeModifiers Attributes, HashSet<uint> Inactive,
        List<ItemModStats.Worn> Worn) EquipmentStatSources(
        Player player, IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        IEnumerable<ushort>? nodes, int? level, Action? afterConditionCapture)
    {
        equipment = EquipmentForStats(player, equipment);
        // A level change can trim the list while /set_stat builds pools outside the combat lock.
        // Mastery dictionaries are replaced as a whole, so retaining their current reference is safe.
        long probe = ConnectionPerformance.ProbeStart();   // Game_TRACE=lag: [latency-lookup]
        IReadOnlyDictionary<ushort, ushort> masteries;
        Dictionary<ushort, string> tattoos;
        Dictionary<ushort, ushort> tattooKeystones;
        AscendancyFamily family;
        AscendancyIdentity? selection;
        nodes = (nodes ?? player.Character.Passives).Concat(player.Character.MainAscendancy.Allocated).Concat(player.Character.DescendancyNodes).ToArray();
        masteries = player.Character.MasteryEffects;
        tattoos = new(player.Character.Tattoos);
        tattooKeystones = new(player.Character.TattooKeystones);
        selection = player.Character.MainAscendancy.Selection;
        family = selection?.Family ?? AscendancyFamily.Normal;
        AscendancyPolicies.RequireAvailable(player, selection);
        var selectedDefinition = selection is null ? null : Ascendancies.Find(selection);
        var c = SkillsFor(player).ClassFor(player.Character.ClassRow);
        var items = ItemsOf(player);
        var allocated = nodes.ToHashSet();
        // Jewels in the passive tree count like worn items (their global mods, requirements and inactive state): the
        // PassiveJewels1 pane holds one per allocated ordinary jewel socket and one per open cluster jewel socket. The
        // Adorned scales corrupted magic jewels in those sockets before their mods are summed.
        var treeJewels = items.Items(PlayerInventories.PassiveJewels)
            .Where(i => IsNormalJewel(i.Item)
                && (PassivesFor(player).JewelNodeForSlot(i.X) is { } node && allocated.Contains(node) || ClusterSlotOpen(player, i.X)))
            .ToList();
        // A Wildwood charm in one of the Primalist's three charm sockets counts like a worn item: its one Domain 35
        // modifier reaches the stats while its Charm Socket descendancy node is allocated.
        var charms = items.Items(PlayerInventories.PassiveJewels)
            .Where(i => IsCharm(i.Item)
                && PassivesFor(player).CharmNodeForSlot(i.X) is { } charmNode && allocated.Contains(charmNode))
            .ToList();
        int adorned = AdornedPercent(treeJewels);
        var worn = equipment.Select(e => e.Item)
            .Concat(treeJewels.Select(i => i with { Item = AdornedJewel(i.Item, adorned) }))
            .Concat(charms)
            .ToArray();
        var before = InactiveItems.GetValueOrDefault(CharacterName(player));
        var exemptions = GameEvents.ExemptItems(player);
        if (exemptions.Count > 0)
            before = new HashSet<uint>((before ?? []).Where(id => !exemptions.Contains(id)));
        // Conditional item stats (issue #504) depend on state the connection keeps: the auras running now, the events of
        // the last four seconds, the attributes an "if you have at least N X" id compares, and current named amounts such
        // as the player's active non-Vaal minion count. Capture one snapshot for both the cache key and the resolver.
        IReadOnlySet<string> auraEffects;
        double conditionNow;
        IReadOnlySet<string> recentEvents;
        double? recentExpiry;
        int strength, dexterity, intelligence, life, mana, energyShield, poisonStacks;
        IReadOnlyDictionary<string, bool> conditionFlags;
        LifeReservationConditionSnapshot lifeReservationConditions;
        ReadAmounts recentAmounts;
        auraEffects = StatConditions.AuraSet(AffectingAuras(player).Where(a => a.EffectId != "Vulnerability").Select(a => a.EffectId));
        conditionNow = AreaTime(player);
        recentEvents = player.RecentEvents.Snapshot(conditionNow);
        recentExpiry = player.RecentEvents.NextExpiry(conditionNow);
        strength = (int)player.Pools.Strength;
        dexterity = (int)player.Pools.Dexterity;
        intelligence = player.Pools.Intelligence;
        life = (int)player.Pools.Life;
        mana = (int)player.Pools.Mana;
        energyShield = (int)Math.Min(int.MaxValue, player.Pools.EnergyShield);
        poisonStacks = player.IncomingAilments.PoisonStacks(conditionNow);
        conditionFlags = ItemConditionFlags(player, conditionNow, null, out lifeReservationConditions);
        if (conditionFlags.GetValueOrDefault("cursed_by_vulnerability"))
            auraEffects = StatConditions.AuraSet(AffectingAuras(player).Where(a => a.EffectId != "Vulnerability")
                .Select(a => a.EffectId).Append("Vulnerability"));
        var measured = player.RecentEvents.Amounts(conditionNow);
        measured.TryAdd(StatConditions.KilledEnemy, 0);
        measured.TryAdd(StatConditions.SpentMana, 0);
        measured.TryAdd(StatConditions.UsedWarcry, 0);
        measured[StatConditions.ActiveNonVaalSkillMinionCount] =
            ActiveNonVaalSkillMinionCountLocked(player, conditionNow);
        OwnedCountItemStats.Capture(player, conditionNow, AreaTime(player), measured);
        recentAmounts = new(measured);
        // A deterministic test can interleave another owner update here, after this call has captured its condition view.
        afterConditionCapture?.Invoke();
        // The sum below is a pure function of these inputs (items are immutable records), and the combat paths ask for
        // it many times a second (every monster hit): reuse the last result while none of them changed.
        var sourcesKey = new StatSourcesKey([.. nodes], masteries, worn, player.Character.ClassRow, player.God, level ?? player.Pools.Level,
            before, [.. player.Character.ClusterPassives.Order()], player.Character.TattooRevision, [.. exemptions.Order()],
            auraEffects, recentEvents, recentExpiry, strength, dexterity, intelligence, life, mana, energyShield,
            conditionFlags, recentAmounts, poisonStacks, ItemsFor(player));
        if (player.StatSourcesCache is { } cached && cached.Key.Matches(sourcesKey))
        {
            PublishQualifiedItemLifeReservationPercent(player, cached.Key.QualifiedItemLifeReservationPercent);
            player.Connection.Performance.LookupHit(probe);
            return (new(cached.Sources), cached.Attributes, new(cached.Inactive), cached.Worn);
        }
        string missed = probe == 0 ? "" : player.StatSourcesCache is { } previous ? previous.Key.Difference(sourcesKey) : "first build";
        // A timeless jewel replaces (or augments) the stats of allocated ordinary nodes in its radius. The item is a
        // normal jewel in an ordinary tree socket; its local alternate-tree stats are read by TimelessJewelCatalog.
        var tree = PassivesFor(player);
        var catalog = ItemsFor(player);
        var pathJewels = JewelPathEffects.Apply(player, tree, catalog, family, sourcesKey.Nodes,
            sourcesKey.ClusterPassives, items.Items(PlayerInventories.PassiveJewels), treeJewels);
        if (!ReferenceEquals(pathJewels, treeJewels))
        {
            treeJewels = pathJewels;
            worn = equipment.Select(e => e.Item)
                .Concat(treeJewels.Select(i => i with { Item = AdornedJewel(i.Item, adorned) })).Concat(charms).ToArray();
        }
        var timeless = items.Items(PlayerInventories.PassiveJewels)
            .Where(i => TimelessJewelCatalog.IsTimeless(i.Item, catalog)
                && tree.JewelNodeForSlot(i.X) is { } socket && allocated.Contains(socket))
            .OrderBy(i => i.X)
            .Select(i => (Jewel: i, Socket: tree.JewelNodeForSlot(i.X)!.Value,
                Spec: TimelessJewelCatalog.Read(i.Item, catalog.Mods)))
            .FirstOrDefault(x => x.Spec is not null);
        var nodeStats = new Dictionary<ushort, IReadOnlyList<(uint Key, int Value)>>();
        foreach (ushort node in nodes)
            if (tree.Find(node) is { } passive)
                nodeStats[node] = passive.IsAscendancy && selectedDefinition is not null
                    ? AscendancyPolicies.NodeStats(player, selectedDefinition, node, passive.Stats) : passive.Stats;
        var radiusJewels = treeJewels
            .Where(i => Passives.ClusterNodeForSlot(i.X) is null)
            .Select(i => (Socket: tree.JewelNodeForSlot(i.X), Conversion: RadiusJewelCatalogue.ReadConversion(i.Item, catalog.Mods)))
            .Where(x => x.Socket is not null && x.Conversion is not null)
            .OrderBy(x => x.Socket)
            .Select(x => (Socket: x.Socket!.Value, Conversion: x.Conversion!))
            .ToList();
        var tattooEffect = new Dictionary<ushort, int>();
        foreach (var (socket, conversion) in radiusJewels)
            if (conversion.TattooEffectPct != 0)
                foreach (ushort node in tree.NodesInRadius(socket, conversion.Radius))
                    if (tattoos.TryGetValue(node, out string? tattooName) && tattooName != "Journey Tattoo of Makanui"
                        && TattooCatalogue.Find(tattooName)?.IsRunegraft != true)
                        tattooEffect[node] = tattooEffect.GetValueOrDefault(node) + conversion.TattooEffectPct;
        IReadOnlyList<(uint Key, int Value)> TattooEffect(ushort node, IReadOnlyList<(uint Key, int Value)> stats) =>
            tattooEffect.TryGetValue(node, out int pct) ? ScaleStats(stats, pct) : stats;
        // An applied tattoo replaces the allocated passive's stats. A timeless jewel that replaces the node still
        // wins (the tattoo is disabled); an augmenting timeless jewel adds its stats to the tattoo stats.
        // Makanga/Honoured Makanga tattoos are conditional on the number of allocated direct neighbours: when the
        // condition is not met the replacement grants nothing, but the tattoo stays applied and can activate later.
        var grantedReplacements = new List<(ushort Tattooed, ushort Granted)>();
        if (tattoos.Count > 0)
            foreach (var (node, name) in tattoos)
                if (allocated.Contains(node) && nodeStats.ContainsKey(node)
                    && (tree.Find(node) is { IsAscendancy: false } || ForbiddenTattoo.IsForbidden(name))
                    && TattooCatalogue.Find(name) is { } entry)
                {
                    IReadOnlyList<(uint Key, int Value)>? tattooStats = TattooStats.For(name);
                    if (name == "Journey Tattoo of Makanui")
                        tattooStats = tattooKeystones.TryGetValue(node, out ushort chosen)
                            && tree.Find(chosen) is { IsKeystone: true } chosenKeystone
                                ? chosenKeystone.Stats : [];
                    else if (ForbiddenTattoo.IsForbidden(name) && tattooKeystones.TryGetValue(node, out ushort granted)
                        && tree.Find(granted) is { IsAscendancy: true } grantedPassive)
                        // The granted notable replaces this node's stats, so it goes through the same ascendancy
                        // policy as a normally allocated ascendancy node (per-node choices included).
                        tattooStats = selectedDefinition is not null
                            ? AscendancyPolicies.NodeStats(player, selectedDefinition, granted, grantedPassive.Stats)
                            : grantedPassive.Stats;
                    if (tattooStats is null) continue;
                    int adjacent = OrdinaryPassives.Adjacent(node, family).Count(allocated.Contains);
                    bool tattooActive = entry.RequiresAdjacent > 0
                        ? adjacent >= entry.RequiresAdjacent
                        : entry.MaxAdjacent <= 0 || adjacent <= entry.MaxAdjacent;
                    if (!tattooActive)
                    {
                        nodeStats[node] = [];
                        continue;
                    }
                    nodeStats[node] = tattooStats;
                    // Remember only a replacement that actually went in: an inactive tattoo must not silence the
                    // allocated copy of its granted passive.
                    if (tattooKeystones.TryGetValue(node, out ushort appliedGrant))
                        grantedReplacements.Add((node, appliedGrant));
                }
        var conquered = new HashSet<ushort>();
        var replacedByTimeless = new HashSet<ushort>();
        if (timeless.Spec is { } timelessSpec)
        {
            var affected = new HashSet<ushort>(tree.NodesInRadius(timeless.Socket, timelessSpec.Radius));
            conquered = affected;
            foreach (ushort node in nodes)
                if (affected.Contains(node) && tree.Find(node) is { } passive
                    && TimelessJewels.Alternate(passive, timelessSpec, node) is { } alternate)
                {
                    if (alternate.Replaces)
                    {
                        nodeStats[node] = alternate.Stats;
                        replacedByTimeless.Add(node);
                    }
                    else nodeStats[node] = [.. TattooEffect(node, nodeStats[node]), .. alternate.Stats];
                    tattooEffect.Remove(node);
                }
        }
        // A random tattoo's pick replaces its node's stats with the granted passive's. If that passive is also
        // allocated, its own node must not add the same stats a second time: the granted node keeps none. A granted
        // node a timeless jewel replaced has already lost its stats, and a node with its own tattoo was replaced by
        // that tattoo, so both are left alone. A tattooed node a timeless jewel replaced has lost the tattoo's stats
        // with it, so the granted passive is no longer granted there and keeps its own stats.
        foreach (var (tattooedNode, grantedNode) in grantedReplacements)
            if (!replacedByTimeless.Contains(tattooedNode) && allocated.Contains(grantedNode)
                && !conquered.Contains(grantedNode) && !tattoos.ContainsKey(grantedNode)
                && nodeStats.ContainsKey(grantedNode))
                nodeStats[grantedNode] = [];
        // Radius jewels run in Path of Building's order: the first pass applies conversions, notable replacements,
        // passive additions and resistance additions; Might of the Meek's effect/suppression are collected here and
        // applied after the pass; the second pass computes the per-node scaling from the resulting stats. A passive
        // conquered by a timeless jewel is left alone, matching the client.

        // The first pass matches Path of Building's "Other" radius jewels: conversions, notable replacements,
        // passive additions and resistance-scaled additions. Might of the Meek's effect and suppression are
        // collected here and applied after the pass, so a later replacement cannot survive suppression.
        var effectPct = new Dictionary<ushort, int>(tattooEffect);
        var suppressedNotables = new HashSet<ushort>();
        var suppressedSmalls = new HashSet<ushort>();
        foreach (var (socket, conversion) in radiusJewels)
        {
            if (!allocated.Contains(socket)) continue;
            var affected = tree.NodesInRadius(socket, conversion.Radius).ToList();
            if (conversion.EffectPct != 0 || conversion.SuppressNotables || conversion.SuppressAllocatedSmalls)
                foreach (ushort node in affected)
                {
                    if (conquered.Contains(node) || tree.Find(node) is not { } passive
                        || passive.IsAscendancy || passive.IsStart || passive.IsJewelSocket || passive.IsMastery
                        || passive.IsProxy || passive.IsExpansion) continue;
                    if (conversion.SuppressNotables && passive.IsNotable) suppressedNotables.Add(node);
                    if (conversion.SuppressAllocatedSmalls && !passive.IsNotable && !passive.IsKeystone)
                        suppressedSmalls.Add(node);
                    if (conversion.EffectPct != 0 && !passive.IsKeystone)
                        effectPct[node] = effectPct.GetValueOrDefault(node) + conversion.EffectPct;
                }
            foreach (ushort node in affected)
                if (allocated.Contains(node) && !conquered.Contains(node)
                    && nodeStats.TryGetValue(node, out var stats) && tree.Find(node) is { } passive
                    && !passive.IsAscendancy && !passive.IsStart && !passive.IsJewelSocket && !passive.IsMastery
                    && !passive.IsProxy && !passive.IsExpansion)
                {
                    if (conversion.NotableStats is { } notable)
                    {
                        if (passive.IsNotable) nodeStats[node] = notable;
                        continue;
                    }
                    if (conversion.AdditionStats is { } additions)
                    {
                        nodeStats[node] = [.. stats, .. additions];
                        continue;
                    }
                    if (conversion.Resistance is { } resistance)
                    {
                        int added = resistance.Added(stats);
                        if (added != 0) nodeStats[node] = [.. stats, (resistance.Target, added)];
                        continue;
                    }
                    nodeStats[node] = RadiusJewelCatalogue.Convert(stats, conversion);
                }
        }
        foreach (ushort node in suppressedNotables)
            if (allocated.Contains(node) && nodeStats.ContainsKey(node))
                nodeStats[node] = [];
        foreach (ushort node in suppressedSmalls)
            if (allocated.Contains(node) && nodeStats.ContainsKey(node))
                nodeStats[node] = [];
        // Path of Building applies PassiveSkillEffect after the first pass and before its per-node scaling pass.
        foreach (var (node, pct) in effectPct)
            if (allocated.Contains(node) && nodeStats.TryGetValue(node, out var stats)
                && tree.Find(node) is { IsKeystone: false })
                nodeStats[node] = ScaleStats(stats, pct);

        // The second pass is the per-node scaling (Anatomical Knowledge, the Tempered/Transcendent jewels, ...) and
        // Unnatural Instinct's copy of the unallocated small passives in its radius.
        var scalingStats = new Dictionary<uint, int>();
        var copiedSmallStats = new Dictionary<uint, int>();
        foreach (var (socket, conversion) in radiusJewels)
        {
            if (conversion.CopyUnallocatedSmalls)
                foreach (ushort node in tree.NodesInRadius(socket, conversion.Radius))
                {
                    if (allocated.Contains(node) || conquered.Contains(node) || tree.Find(node) is not { } passive
                        || passive.IsNotable || passive.IsKeystone || passive.IsAscendancy || passive.IsStart
                        || passive.IsJewelSocket || passive.IsMastery || passive.IsProxy || passive.IsExpansion)
                        continue;
                    AddCounts(copiedSmallStats, UnallocatedStats(node, passive));
                }
            if (conversion.Scaling is { } scaling)
            {
                var allocatedCounts = new Dictionary<uint, int>();
                var unallocatedCounts = new Dictionary<uint, int>();
                foreach (ushort node in tree.NodesInRadius(socket, conversion.Radius))
                {
                    if (conquered.Contains(node) || tree.Find(node) is not { } passive
                        || passive.IsAscendancy || passive.IsStart || passive.IsJewelSocket || passive.IsMastery
                        || passive.IsProxy || passive.IsExpansion) continue;
                    if (allocated.Contains(node) && nodeStats.TryGetValue(node, out var stats))
                        AddCounts(allocatedCounts, stats);
                    else if (!allocated.Contains(node))
                        AddCounts(unallocatedCounts, UnallocatedStats(node, passive));
                }
                foreach (var stat in scaling)
                {
                    int count = stat.Allocated ? allocatedCounts.GetValueOrDefault(stat.Count)
                        : unallocatedCounts.GetValueOrDefault(stat.Count);
                    int value = stat.ValueIsDivisor
                        ? (stat.Value != 0 ? (int)Math.Floor(count / (double)stat.Value) : 0)
                        : (int)Math.Floor(count * (double)stat.Value / stat.Per);
                    scalingStats[stat.Target] = scalingStats.GetValueOrDefault(stat.Target) + value;
                }
            }
        }
        IReadOnlyList<(uint Key, int Value)> UnallocatedStats(ushort node, PassiveTree.Node passive)
        {
            if (suppressedNotables.Contains(node)) return [];
            // Unnatural Instinct grants a tattoo on an unallocated small passive, not the original passive.
            var stats = tattoos.TryGetValue(node, out string? tattooName) && TattooStats.For(tattooName) is { } tattoo
                ? tattoo : passive.Stats;
            return effectPct.TryGetValue(node, out int pct) && pct != 0 && !passive.IsKeystone
                ? ScaleStats(stats, pct) : stats;
        }
        static IReadOnlyList<(uint Key, int Value)> ScaleStats(
            IReadOnlyList<(uint Key, int Value)> stats, int pct)
        {
            var scaled = new List<(uint Key, int Value)>(stats.Count);
            foreach (var (key, value) in stats)
            {
                int scaledValue = RadiusJewelCatalogue.ScaleEffect(value, pct);
                if (scaledValue != 0) scaled.Add((key, scaledValue));
            }
            return scaled;
        }
        static void AddCounts(Dictionary<uint, int> counts, IReadOnlyList<(uint Key, int Value)> stats)
        {
            foreach (var (key, value) in stats)
                counts[key] = counts.GetValueOrDefault(key) + value;
        }
        // Wand Mastery's extra projectile is wand-only: its stat is tree-only and mapped onto the generic attack
        // count the projectile resolver reads. The client further requires the skill's skill_can_fire_wand_projectiles
        // flag, which the server does not have, so a wand also gives it to a non-wand projectile attack such as
        // Spectral Shield Throw (an approximation, not the in-game rule). The mastery's attack life and mana leech
        // uses ordinary generic stats and applies with any weapon, as the client's stat text does.
        bool wieldingWand = ActiveMainHand(player)?.Type?.Class == "Wand";
        uint wandProjectile = Stat.WandAttacksFireAnAdditionalProjectile;
        uint attackProjectiles = Stat.AttacksNumberOfAdditionalProjectiles;
        static bool HasStat(PassiveTree.MasteryEffect effect, uint key) => key != 0 && effect.Stats.Any(s => s.Key == key);
        var passives = new Dictionary<uint, int>();
        foreach (ushort node in nodes)
        {
            if (nodeStats.TryGetValue(node, out var stats))
                foreach (var (key, value) in stats) passives[key] = ActorScale.Add(key, passives.GetValueOrDefault(key), value);
            if (!(tattoos.TryGetValue(node, out var replacement) && TattooCatalogue.Find(replacement)?.IsRunegraft == true)
                && masteries.TryGetValue(node, out ushort effect) && tree.FindEffect(effect) is { } mastery)
            {
                if (HasStat(mastery, wandProjectile))
                {
                    if (wieldingWand && attackProjectiles != 0)
                    {
                        int extra = mastery.Stats.Where(s => s.Key == wandProjectile).Sum(s => s.Value);
                        if (extra != 0)
                            passives[attackProjectiles] = ActorScale.Add(attackProjectiles,
                                passives.GetValueOrDefault(attackProjectiles), extra);
                    }
                    continue;
                }
                foreach (var (key, value) in mastery.Stats)
                    passives[key] = ActorScale.Add(key, passives.GetValueOrDefault(key), value);
            }
        }
        foreach (var (key, value) in TattooCountingStats(tattoos, allocated.Where(n => !suppressedNotables.Contains(n) && !suppressedSmalls.Contains(n)).ToHashSet(), effectPct))
            passives[key] = ActorScale.Add(key, passives.GetValueOrDefault(key), value);
        foreach (var (key, value) in scalingStats)
            passives[key] = ActorScale.Add(key, passives.GetValueOrDefault(key), value);
        foreach (var (key, value) in copiedSmallStats)
            passives[key] = ActorScale.Add(key, passives.GetValueOrDefault(key), value);
        // Socketed cluster jewels' own passives: each node gives its theme, notables or nothing (ClusterJewelStats).
        var clusterJewels = items.Items(PlayerInventories.PassiveJewels)
            .Where(i => ClusterSize(i.Item) is not null
                && (tree.JewelNodeForSlot(i.X) is { } ordinary && allocated.Contains(ordinary) || ClusterSlotOpen(player, i.X)))
            .OrderBy(i => i.X).ToList();
        var clusterAttributes = new Dictionary<uint, long>();
        var clusterStats = ClusterJewelStats.For(player, clusterJewels, catalog.Mods, Stats).ToArray();
        foreach (var (key, value) in clusterStats)
        {
            passives[key] = passives.GetValueOrDefault(key) + value;
            if (AttributeModifiers.IsInput(key))
                clusterAttributes[key] = clusterAttributes.GetValueOrDefault(key) + value;
        }
        // Threshold jewels: Path of Building's Threshold pass sums the attributes of every ordinary passive in
        // radius, allocated or not, and grants the skill stat only when the total reaches the requirement. The
        // client applies the same local mods; this keeps the server's stat view in step. Unknown target stats are
        // skipped by ThresholdJewels.Read.
        foreach (var item in items.Items(PlayerInventories.PassiveJewels))
        {
            if (Passives.ClusterNodeForSlot(item.X) is not null) continue;
            if (tree.JewelNodeForSlot(item.X) is not ushort thresholdSocket || !allocated.Contains(thresholdSocket))
                continue;
            var (thresholdRadius, thresholdEffects) = ThresholdJewels.Read(item.Item, catalog.Mods);
            if (thresholdEffects.Count == 0) continue;
            var thresholdAttributes = new Dictionary<uint, int>();
            foreach (ushort node in tree.NodesInRadius(thresholdSocket, thresholdRadius))
            {
                if (node == thresholdSocket || tree.Find(node) is not { IsAscendancy: false, IsStart: false,
                    IsJewelSocket: false, IsMastery: false, IsProxy: false, IsExpansion: false } passive)
                    continue;
                IReadOnlyList<(uint Key, int Value)> stats =
                    allocated.Contains(node) && nodeStats.TryGetValue(node, out var allocatedStats)
                        ? allocatedStats : UnallocatedStats(node, passive);
                AddCounts(thresholdAttributes, stats);
            }
            foreach (var effect in thresholdEffects)
                if (ThresholdJewels.Met(effect, thresholdAttributes))
                {
                    passives[effect.Key] = passives.GetValueOrDefault(effect.Key) + effect.Value;
                    Console.WriteLine($"[gateway] threshold jewel socket {thresholdSocket}: " +
                        $"{Stats.Name(effect.Key)} {effect.Value}");
                }
        }
        if (StolenMods.RandomSourceStat is var stealKey)
            foreach (var jewel in treeJewels)
                if (tree.JewelNodeForSlot(jewel.X) is ushort source && tree.ClusterNodeForSlot(jewel.X) is null)
                {
                    int count = InspiredLearning.Granted(jewel.Item, catalog.Mods, tree, source, allocated);
                    if (count > 0) passives[stealKey] = passives.GetValueOrDefault(stealKey) + count;
                }
        // Might in All Forms: Dexterity and Intelligence from allocated passives in the jewel's radius count toward
        // the Strength melee damage bonus, one percent per five points like Strength itself. The summed view is
        // divided once so the truncation matches CharacterStats' str / 5.
        int meleeAttributes = 0;
        foreach (var (socket, conversion) in radiusJewels)
            if (conversion.StrengthMeleeBonus)
                foreach (ushort node in tree.NodesInRadius(socket, conversion.Radius))
                    if (allocated.Contains(node) && !conquered.Contains(node)
                        && nodeStats.TryGetValue(node, out var stats)
                        && tree.Find(node) is { IsAscendancy: false, IsStart: false, IsJewelSocket: false,
                            IsMastery: false, IsProxy: false, IsExpansion: false })
                        meleeAttributes += RadiusJewelCatalogue.MeleeAttributes(stats);
        if (meleeAttributes != 0 && Stat.MeleePhysicalDamagePlusPct is var meleeKey)
            passives[meleeKey] = passives.GetValueOrDefault(meleeKey) + meleeAttributes / 5;
        // The allocated passives' self buffs (PassiveBuffAuras) add their stats like the passives themselves.
        PassiveBuffAuras.WithSelfBuffs(passives, nodes, LeagueFor(player).HardMode);
        // Only attributes need a wide source view. Keep each raw key until overrides have replaced its entire total.
        var passiveAttributes = new Dictionary<uint, long>();
        foreach (var (name, value) in clusterAttributes)
            passiveAttributes[name] = passiveAttributes.GetValueOrDefault(name) + value;
        foreach (ushort node in nodes)
        {
            var effects = !(tattoos.TryGetValue(node, out var replacement) && TattooCatalogue.Find(replacement)?.IsRunegraft == true)
                && masteries.TryGetValue(node, out ushort effect) ? tree.FindEffect(effect)?.Stats : null;
            foreach (var (key, value) in (nodeStats.GetValueOrDefault(node) ?? []).Concat(effects ?? []))
            {
                if (AttributeModifiers.IsInput(key))
                    passiveAttributes[key] = passiveAttributes.GetValueOrDefault(key) + value;
            }
        }
        foreach (var (key, value) in scalingStats.Concat(copiedSmallStats))
            if (AttributeModifiers.IsInput(key))
                passiveAttributes[key] = passiveAttributes.GetValueOrDefault(key) + value;
        foreach (var (key, value) in MasteryCounts.Apply(passives, nodes, tree))
            passiveAttributes[key] = passiveAttributes.GetValueOrDefault(key) + value;
        var offhand = equipment.FirstOrDefault(e => e.Row == PlayerInventories.Offhand1).Item?.Item;
        var shield = offhand is not null && catalog.ByHash(offhand.TypeHash)?.Class == "Shield" ? offhand : null;
        bool passiveAegis = passives.GetValueOrDefault(Stat.MinionsGetShieldStatsInsteadOfYou) != 0;
        var aegisItems = shield is null || passiveAegis ? [] : worn.Select(i => i.Item)
            .Where(item => !ReferenceEquals(item, shield)
                && PlainItemSum(item, catalog.Mods).GetValueOrDefault(Stat.MinionsGetShieldStatsInsteadOfYou) != 0)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        IReadOnlyList<Item> ItemBonuses(IReadOnlyList<Item> activeItems)
            => shield is not null && (passiveAegis || aegisItems.Count > 0 && activeItems.Any(aegisItems.Contains))
                ? activeItems.Where(item => !ReferenceEquals(item, shield)).ToArray()
                : activeItems;

        StatConditions.Subject ConditionSubject(IReadOnlyDictionary<uint, long> raw, IReadOnlyList<Item>? activeItems = null,
            IReadOnlyCollection<Item>? influencedEquipment = null)
        {
            var attributes = AttributeInputs(raw).Apply(c.Strength + (player.God ? 1000 : 0),
                c.Dexterity + (player.God ? 1000 : 0), c.Intelligence + (player.God ? 1000 : 0));
            var equipmentValues = EquipmentConditionValues(equipment, catalog, activeItems);
            var itemFlags = QualifiedEquipmentItemFlags(conditionFlags, equipment, catalog, activeItems);
            var weapons = WeaponProfileFor(player);
            bool offHandEmpty = equipment.All(e => e.Row != PlayerInventories.Offhand1);
            bool axe = activeItems is null ? weapons.Axe : equipment.Any(e =>
                e.Row is PlayerInventories.Weapon1 or PlayerInventories.Offhand1
                && activeItems.Any(item => ReferenceEquals(item, e.Item.Item))
                && ItemsFor(player).ByHash(e.Item.Item.TypeHash)?.Class is "One Hand Axe" or "Two Hand Axe");
            return new(DualWield: weapons.DualWield, Shield: weapons.Shield, TwoHanded: weapons.TwoHand,
                OneHanded: weapons.OneHand, OffHandEmpty: offHandEmpty, Axe: axe, Auras: auraEffects, Recent: recentEvents,
                Value: name => name switch
                {
                    "strength" => attributes.Strength, "dexterity" => attributes.Dexterity, "intelligence" => attributes.Intelligence,
                    "life" => life, "mana" => mana, "energy_shield" => energyShield,
                    _ => equipmentValues.TryGetValue(name, out int count) ? count : null,
                }, KnownRecent: TrackedItemRecentEvents, Flags: itemFlags, Amounts: recentAmounts,
                ItemValue: ConditionalRings(equipment, activeItems).Value, NamedConditions: NamedItemConditionSuffixes,
                InfluencedEquipment: influencedEquipment);
        }

        Dictionary<uint, long> RawAttributes(IEnumerable<Item> active)
        {
            var items = active.ToArray();
            var itemBonuses = ItemBonuses(items);
            var raw = ItemModStats.RawAttributes(itemBonuses, ItemsFor(player).Mods);
            foreach (var (name, value) in passiveAttributes) raw[name] = raw.GetValueOrDefault(name) + value;
            // Independent equipment-count modifiers settle before comparisons that read those final attributes.
            // Keep wide attribute inputs and use the same conditional view for qualification and final pools.
            for (int pass = 0; pass < 4; pass++)
            {
                var next = ItemModStats.RawAttributes(itemBonuses, ItemsFor(player).Mods, ConditionSubject(raw, items), Stats.Has);
                foreach (var (name, value) in passiveAttributes) next[name] = next.GetValueOrDefault(name) + value;
                bool same = raw.Count == next.Count && raw.All(p => next.TryGetValue(p.Key, out long value) && p.Value == value);
                raw = next;
                if (same) break;
            }
            return raw;
        }
        int god = player.God ? 1000 : 0;
        int requirementsPct = AttributeRequirementsStat is var reqKey ? passives.GetValueOrDefault(reqKey) : 0;
        int Req(int value) => ScaledRequirement(value, requirementsPct);
        // A worn item needs the most any of its socketed gems needs too, as the client shows it: a gem the character is too
        // low for makes the whole item inactive, so its mods and every gem in it do nothing and its skills leave the skill
        // list and the skill bar (GemSkillEffectsAt). The gems' requirements are catalog data compared with the totals this
        // resolver counts itself, never the pools, which may not exist yet (the old check read them and switched items off
        // at login; PR #175 then dropped the rule entirely).
        ItemModStats.Worn WithGems(ItemModStats.Worn item)
        {
            foreach (var socket in item.Item.Sockets)
                if (socket is { Colour: SocketColour.Abyss, Gem: { } jewel } && catalog.ByHash(jewel.TypeHash) is { Class: "AbyssJewel" } eye)
                    item = item with { Level = Math.Max(item.Level, ItemModStats.LevelRequirement(jewel, eye.DropLevel, catalog.Mods)) };
                else if (socket.Gem is { } gem && SocketedGemRequirements(player, gem, requirementsPct) is var g)
                    item = item with { Level = Math.Max(item.Level, g.Level), Strength = Math.Max(item.Strength, g.Str),
                        Dexterity = Math.Max(item.Dexterity, g.Dex), Intelligence = Math.Max(item.Intelligence, g.Int) };
            return item;
        }
        var wornItems = worn
            .Select(i => (i, t: ItemsFor(player).ByHash(i.Item.TypeHash)))
            .Select(x =>
            {
                if (x.t is not { } t) return new ItemModStats.Worn(x.i.Id, x.i.Item, 0, 0, 0, 0);
                var (str, dex, intel) = ItemModStats.AttributeRequirements(ItemsFor(player), x.i.Item, t);
                return new ItemModStats.Worn(x.i.Id, x.i.Item, Req(str), Req(dex), Req(intel),
                    ItemModStats.LevelRequirement(x.i.Item, t.DropLevel, ItemsFor(player).Mods));
            })
            .Select(w => ItemModStats.ApplyBaseRequirementOverrides(w, ItemsFor(player).Mods))
            .Select(WithGems).ToList();
        // The mod catalog lets the resolver see the hidden modifier that waives requirements.
        var (inactive, active) = ItemModStats.ResolveEquipment(wornItems, level ?? player.Pools.Level, active =>
        {
            var ids = active.Select(w => w.Id).ToHashSet();
            var candidateItems = WithQuiverModifierEffect(AbyssJewels.Sources(active.Select(w => w.Item)
                .Concat(RingReflections(equipment, i => ids.Contains(i.Id), catalog.Mods)), catalog).ToArray(), catalog, passives);
            var (str, dex, intel) = AttributeInputs(RawAttributes(candidateItems))
                .Apply(c.Strength + god, c.Dexterity + god, c.Intelligence + god);
            return (str, dex, intel);
        }, before, ItemsFor(player).Mods);
        // Exempt items must join the active list as well as leave the inactive set, so their mods reach the stats.
        if (exemptions.Count > 0)
        {
            var exempt = wornItems.Where(w => exemptions.Contains(w.Id) && inactive.Contains(w.Id))
                .Select(w => w.Item).ToList();
            if (exempt.Count > 0) active = [.. active, .. exempt];
            inactive.ExceptWith(exemptions);
        }
        // Keep the actual active equipment set before Kalandra's Touch reflection or Necromantic Aegis filtering
        // so influence thresholds (Bound By Destiny) count genuine equipped items without double-counting reflections.
        var equippedActive = active;
        // Kalandra's Touch: the opposite active ring's modifiers once more (RingReflections).
        active = AbyssJewels.Sources(active.Concat(RingReflections(equipment, i => !inactive.Contains(i.Id), catalog.Mods)),
            catalog).ToArray();
        // The Bow Mastery's increased effect on the equipped quiver's own modifiers: scale the quiver in the copy
        // whose stats become the character's, before the raw attributes and the item sum read it.
        active = WithQuiverModifierEffect(active, catalog, passives);
        var itemBonuses = ItemBonuses(active);
        var rawAttributes = RawAttributes(active);
        // Qualification for this exact build is now known. Re-evaluate reservation-only conditions from that active set,
        // so an item's first action after a requirement change cannot use the previous inactive cache.
        conditionFlags = RequalifyLifeReservation(conditionFlags, player, active, lifeReservationConditions);
        var qualifiedSlotFlags = new Dictionary<string, bool>(conditionFlags);
        foreach (var (key, value) in ConditionalRings(equipment, active).Flags) qualifiedSlotFlags[key] = value;
        conditionFlags = qualifiedSlotFlags;
        var curseInputs = SelfCurseConditionInputs(player, itemBonuses, passives, conditionNow, recentEvents);
        bool cursed = Stat.CursedByVulnerability is var cursedKey && curseInputs.GetValueOrDefault(cursedKey) != 0;
        auraEffects = StatConditions.AuraSet(AffectingAuras(player).Where(a => a.EffectId != "Vulnerability").Select(a => a.EffectId)
            .Concat(cursed ? ["Vulnerability"] : []));
        bool lowLife = CriticalOnLowLife(player, curseInputs);
        conditionFlags = new Dictionary<string, bool>(conditionFlags)
        {
            ["_while_on_low_life"] = lowLife,
            ["_while_not_low_life"] = player.PlayerLife > 0 && !lowLife,
            ["_while_not_on_low_life"] = player.PlayerLife > 0 && !lowLife,
            ["_while_on_full_life"] = CountsAsFullLife(player.PlayerLife, player.PlayerMaxLife, curseInputs),
            ["cursed_by_vulnerability"] = cursed,
            ["_while_cursed"] = player.PlayerLife > 0 && cursed,
            ["_while_not_cursed"] = player.PlayerLife > 0 && !cursed,
            ["_while_blinded"] = conditionFlags.GetValueOrDefault("_while_blinded")
                && !(Stat.CannotBeBlinded is var blindImmunity && curseInputs.GetValueOrDefault(blindImmunity) != 0),
        };
        var phaseInputs = curseInputs;
        conditionFlags = RequalifyAvatarOfFire(player, RequalifyElusive(player,
            RequalifyHerEmbrace(player, conditionFlags, active, phaseInputs), phaseInputs, active), phaseInputs, active);
        conditionFlags = RequalifySourcePhasing(player, conditionFlags, phaseInputs, recentEvents, auraEffects, conditionNow);
        conditionFlags = QualifiedEquipmentItemFlags(conditionFlags, equipment, catalog, active);
        int qualifiedItemReservationPercent = LifeReservation.ItemPercentStat is var itemReservationKey
            ? WornItemSum(player, itemBonuses).GetValueOrDefault(itemReservationKey)
            : 0;
        sourcesKey = sourcesKey with
        {
            Flags = conditionFlags,
            Auras = auraEffects,
            QualifiedItemLifeReservationPercent = qualifiedItemReservationPercent,
        };
        // A stat whose id carries a condition only counts while that condition holds (issue #504). The subject answers
        // from state the server already keeps: the weapon profile, the auras running right now, the events of the last
        // four seconds (RecentEvents), and the attributes an "if you have at least N X" id compares. A condition it
        // cannot answer stays unmodelled and keeps its previous behaviour rather than counting wrongly.
        var conditionSubject = ConditionSubject(rawAttributes, active, equippedActive);
        var sources = ItemModStats.Plus(FoldPassiveConditions(passives, conditionSubject),
            ItemModStats.Global(itemBonuses, ItemsFor(player).Mods, Stats, conditionSubject));
        // The tree's base_devotion and the item's ..._per_10_devotion stats meet here: replace the latter with their
        // normal stat at floor(Devotion / 10) steps before the pools and panels read this view.
        DevotionStats.Apply(sources, Stats);
        GrandSpectrum.Apply(sources, Stats);
        // The first snapshot after equipment/point changes must use the new limits and conversion, before the charge
        // ticker observes them. Rebuild the conditional sum from its inputs, never add a second folded contribution.
        for (int pass = 0; pass < 4; pass++)
        {
            var chargeFlags = RequalifySourcePhasing(player,
                RequalifyChargeConditions(player, conditionFlags, sources), phaseInputs, recentEvents, auraEffects, conditionNow);
            if (chargeFlags.All(p => conditionFlags.TryGetValue(p.Key, out bool prior) && p.Value == prior)) break;
            conditionFlags = chargeFlags;
            var passSubject = ConditionSubject(rawAttributes, active, equippedActive);
            sources = ItemModStats.Plus(FoldPassiveConditions(passives, passSubject),
                ItemModStats.Global(itemBonuses, ItemsFor(player).Mods, Stats, passSubject));
            DevotionStats.Apply(sources, Stats);
            GrandSpectrum.Apply(sources, Stats);
        }
        sourcesKey = sourcesKey with { Flags = conditionFlags };
        ApplyMinionSocketConversions(active, catalog, sources);
        // Clamp only this outgoing integer view; the authoritative attribute inputs remain wide beside it.
        foreach (var (name, value) in rawAttributes)
            sources[name] = AttributeModifiers.Signed(value);
        AbyssJewels.Count(active, catalog, Stats, sources);
        ApplySocketedGhastlyEyeBonuses(active, catalog, sources);
        ItemStatAliases.Apply(sources);
        var attributes = AttributeInputs(rawAttributes);
        var finalAttributes = attributes.Apply(c.Strength + (player.God ? 1000 : 0),
            c.Dexterity + (player.God ? 1000 : 0), c.Intelligence + (player.God ? 1000 : 0));
        AttributeOffenceSources.Apply(sources, Stats,
            finalAttributes.Strength, finalAttributes.Dexterity, finalAttributes.Intelligence);
        AttributeDefenceSources.Apply(sources, Stats, finalAttributes.Strength);
        LevelItemSources.Apply(sources, Stats, sourcesKey.Level);
        OwnedCountItemStats.Apply(sources, Stats, sourcesKey.Amounts);
        ApplyRecentSavageHitItemStats(sources, Stats, sourcesKey.Flags);
        ApplyFlaggedItemStats(sources, sourcesKey.Flags);
        if (recentAmounts.GetValueOrDefault(StatConditions.DetonatedMine) is long mines and > 0)
        {
            int count = ActorScale.Saturate(mines);
            if (sources.GetValueOrDefault(Stat.TrapCooldownSpeedPlusPctPerRecentMineDetonation) != 0)
                sources[Stat.NumberOfMinesDetonatedRecently] = count;
            if (sources.GetValueOrDefault(Stat.CriticalStrikeChancePlusPctPerMineDetonatedRecentlyUpTo100Pct) is int perMine and > 0)
                sources[Stat.CriticalStrikeChancePlusPct] = ActorScale.Saturate(sources.GetValueOrDefault(Stat.CriticalStrikeChancePlusPct)
                    + Math.Min(100L, (long)count * perMine));
        }
        player.StatSourcesCache = new(sourcesKey with { Before = inactive }, new(sources), attributes, inactive, wornItems);
        PublishQualifiedItemLifeReservationPercent(player, qualifiedItemReservationPercent);
        player.Connection.Performance.LookupMiss(probe, missed);
        return (sources, attributes, inactive, wornItems);
    }

    /// <summary>Moves a conditional passive stat onto its plain name when its condition holds, exactly as an item mod
    /// does (issue #504, #1102): <c>attack_skills_damage_+%_while_dual_wielding</c> counts as
    /// <c>attack_skills_damage_+%</c> only while dual wielding, and not at all otherwise. A condition this build cannot
    /// answer, a stat with a consumer of its own (<c>DedicatedConsumers</c>) and an id whose plain name this build does
    /// not know all keep their previous behaviour, so nothing that counted before stops counting.</summary>
    internal static Dictionary<uint, int> FoldPassiveConditions(IReadOnlyDictionary<uint, int> passives,
        StatConditions.Subject subject)
    {
        var folded = new Dictionary<uint, int>(passives.Count);
        void Add(uint key, int value) => folded[key] = ActorScale.Add(key, folded.GetValueOrDefault(key), value);
        foreach (var (key, raw) in passives)
        {
            if (Stats.Name(key) is not { } name) { Add(key, raw); continue; }
            var state = StatConditions.Of(name, subject);
            // A stat whose rule records the event its condition waits for keeps its id (StatConditions.KeepsOwnId).
            if (!StatConditions.Counts(state)) { if (StatConditions.KeepsOwnId(name)) Add(key, 1); continue; }
            int value = StatConditions.Counted(name, raw, state, subject, Stats.Has, out var targets);
            if (targets is null) Add(key, value);
            else
            {
                foreach (string target in targets)
                    if (Stats.Key(target) is uint targetKey) Add(targetKey, value);
                if (StatConditions.KeepsOwnId(name)) Add(key, 1);
            }
        }
        return folded;
    }

    static int ActiveNonVaalSkillMinionCountLocked(Player player, double now)
    {
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return 0;
        var skills = SkillsFor(player);
        int count = 0;
        foreach (var minion in player.Minions.All)
        {
            if (minion.Monster.Dead || minion.Monster.Life == 0 || now >= minion.Until
                || !player.Area.Monsters.TryGetValue(minion.Id, out var held) || !ReferenceEquals(held, minion.Monster)) continue;
            int effect = skills.EffectRow(minion.Skill);
            // Missing provenance and a Vaal creator cannot establish this item's non-Vaal predicate.
            if (effect >= 0 && !skills.IsVaal(effect)) count++;
        }
        return count;
    }

    static void PublishQualifiedItemLifeReservationPercent(Player player, int percent)
    {
        player.QualifiedItemLifeReservationPercent = percent;
    }

    // "Items and Gems have X% increased/reduced Attribute Requirements", rounded down (PoB CalcPerform m_floor).
    // shortcut: only passives (and /set_stat) count, not an item's own global mod, which would decide its own activation.
    public const uint AttributeRequirementsStat = Stat.GlobalItemAttributeRequirementsPlusPct;
    public static int ScaledRequirement(int requirement, int increasedPct) =>
        requirement <= 0 ? requirement : (int)Math.Max(0, (long)requirement * (100 + increasedPct) / 100);

    public static int AttributeRequirementsPct(Player player)
    {
        if (!(AttributeRequirementsStat is var key)) return 0;
                    var tree = PassivesFor(player);
        if (AttributeRequirementSources.TryGetValue(player, out var last)
            && ReferenceEquals(tree, last.Tree)
            && player.Character.Passives.SequenceEqual(last.Ordinary) && player.Character.MainAscendancy.Allocated.SequenceEqual(last.Ascendancy)
            && player.Character.MasteryEffects.Count == last.Masteries.Count
            && player.Character.MasteryEffects.All(kv => last.Masteries.TryGetValue(kv.Key, out ushort value) && value == kv.Value))
            return last.Value;
        ushort[] ordinary = [.. player.Character.Passives], ascendancy = [.. player.Character.MainAscendancy.Allocated];
        var masteries = new Dictionary<ushort, ushort>(player.Character.MasteryEffects);
        int value = tree.Stats([.. ordinary, .. ascendancy], masteries).GetValueOrDefault(key);
        AttributeRequirementSources.AddOrUpdate(player, new(tree, ordinary, ascendancy, masteries, value));
        return value;
    }

    sealed record AttributeRequirementSource(PassiveTree Tree, ushort[] Ordinary, ushort[] Ascendancy,
        Dictionary<ushort, ushort> Masteries, int Value);
    static readonly Store<Player, AttributeRequirementSource> AttributeRequirementSources = new();

    /// <summary>The inputs of CharacterStatSources' passive and equipment sum, compared by value (items by reference).</summary>
    public sealed record StatSourcesKey(ushort[] Nodes, IReadOnlyDictionary<ushort, ushort> Masteries,
        GameServer.Core.Protocol.Instance.InventoryItem[] Worn, int ClassRow, bool God, int Level,
        IReadOnlySet<uint>? Before, ushort[] ClusterPassives, int TattooRevision, uint[] RunItems,
        IReadOnlySet<string> Auras, IReadOnlySet<string> Recent, double? RecentExpiry,
        int Strength, int Dexterity, int Intelligence, int Life, int Mana, int EnergyShield = 0,
        IReadOnlyDictionary<string, bool>? Flags = null, ReadAmounts? Amounts = null,
        int PoisonStacks = 0, ItemCatalog? Catalog = null, int QualifiedItemLifeReservationPercent = 0)
    {
        public bool Matches(StatSourcesKey o) =>
            ClassRow == o.ClassRow && God == o.God && Level == o.Level && TattooRevision == o.TattooRevision
            && ReferenceEquals(Catalog, o.Catalog)
            && ReferenceEquals(Masteries, o.Masteries)
            && Nodes.AsSpan().SequenceEqual(o.Nodes) && ClusterPassives.AsSpan().SequenceEqual(o.ClusterPassives)
            && Worn.Length == o.Worn.Length && Worn.Zip(o.Worn).All(p => p.First.Id == p.Second.Id
                && p.First.X == p.Second.X && (ReferenceEquals(p.First.Item, p.Second.Item)
                    || Catalog is not null && SameSkillItem(p.First.Item, p.Second.Item, Catalog)))
            && (Before ?? new HashSet<uint>()).SetEquals(o.Before ?? new HashSet<uint>())
            && RunItems.AsSpan().SequenceEqual(o.RunItems)
            // A refreshed deadline does not change the resolver's boolean "recently" inputs. Snapshot(now) above
            // drops an expired event, which invalidates this cache at the actual boundary.
            && Auras.SetEquals(o.Auras) && Recent.SetEquals(o.Recent)
            && Strength == o.Strength && Dexterity == o.Dexterity && Intelligence == o.Intelligence
            && Life == o.Life && Mana == o.Mana && EnergyShield == o.EnergyShield
            && PoisonStacks == o.PoisonStacks
            && (Flags?.Count ?? 0) == (o.Flags?.Count ?? 0)
            && (Flags is null || Flags.All(p => o.Flags is not null && o.Flags.TryGetValue(p.Key, out bool active) && p.Value == active))
            && (Amounts?.Count ?? 0) == (o.Amounts?.Count ?? 0)
            && (Amounts is null || o.Amounts is not null && Amounts.ReadSame(o.Amounts));

        /// <summary>The first input in which <paramref name="o"/> differs, in Matches' order, for the lag probe's
        /// [latency-lookup] line: why a lookup built its result again.</summary>
        public string Difference(StatSourcesKey o) =>
            ClassRow != o.ClassRow || God != o.God || Level != o.Level ? "class or level"
            : TattooRevision != o.TattooRevision ? "tattoos"
            : !ReferenceEquals(Catalog, o.Catalog) ? "item catalogue"
            : !ReferenceEquals(Masteries, o.Masteries) ? "masteries"
            : !Nodes.AsSpan().SequenceEqual(o.Nodes) ? "passives"
            : !ClusterPassives.AsSpan().SequenceEqual(o.ClusterPassives) ? "cluster passives"
            : Worn.Length != o.Worn.Length || !Worn.Zip(o.Worn).All(p => p.First.Id == p.Second.Id && p.First.X == p.Second.X
                && (ReferenceEquals(p.First.Item, p.Second.Item) || Catalog is not null && SameSkillItem(p.First.Item, p.Second.Item, Catalog))) ? "worn items"
            : !(Before ?? new HashSet<uint>()).SetEquals(o.Before ?? new HashSet<uint>()) ? "inactive items"
            : !RunItems.AsSpan().SequenceEqual(o.RunItems) ? "exempt items"
            : !Auras.SetEquals(o.Auras) ? "auras"
            : !Recent.SetEquals(o.Recent) ? "recent event " + (o.Recent.Except(Recent).FirstOrDefault() is { } began ? "+" + began : "-" + Recent.Except(o.Recent).FirstOrDefault())
            : Strength != o.Strength || Dexterity != o.Dexterity || Intelligence != o.Intelligence ? "attributes"
            : Life != o.Life || Mana != o.Mana || EnergyShield != o.EnergyShield ? "pools"
            : PoisonStacks != o.PoisonStacks ? "poison stacks"
            : Flags is not null && o.Flags is not null && o.Flags.FirstOrDefault(p => !Flags.TryGetValue(p.Key, out bool was) || was != p.Value) is { Key: { } flag } ? "flag " + flag
            : Amounts is not null && o.Amounts is not null && !Amounts.ReadSame(o.Amounts) ? "recent amounts"
            : "other";
    }

    /// <summary>The named amounts of one stat-sources build (kills and mana spent recently, living minions and totems),
    /// remembering which of them the build read. Its result depends on those alone, so a kill or a paid cost does not
    /// discard the cached sum of a character none of whose stats count them (live 2026-11-17: every kill and every cast
    /// rebuilt it, three to four times the stat builds' CPU in a fight).</summary>
    public sealed class ReadAmounts(Dictionary<string, long> values) : IReadOnlyDictionary<string, long>
    {
        readonly Dictionary<string, long> values = values;
        // A resolver kept by a later consumer may read on another thread.
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> read = new();
        public bool TryGetValue(string key, out long value) { read[key] = true; return values.TryGetValue(key, out value); }
        public long this[string key] { get { read[key] = true; return values[key]; } }
        // Every amount is always present (zero when nothing happened), so asking whether one is reads no value.
        public bool ContainsKey(string key) => values.ContainsKey(key);
        public int Count => values.Count;
        public IEnumerable<string> Keys => values.Keys;
        public IEnumerable<long> Values => All().Select(p => p.Value);
        public IEnumerator<KeyValuePair<string, long>> GetEnumerator() => All().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        Dictionary<string, long> All() { foreach (string key in values.Keys) read[key] = true; return values; }
        /// <summary>Whether every amount this build read has the same value in <paramref name="other"/>.</summary>
        public bool ReadSame(ReadAmounts other)
        {
            // Enumerated in place: ConcurrentDictionary.Keys takes every lock and copies (4% of a lookup).
            foreach (var (key, _) in read)
                if (values.TryGetValue(key, out long mine) != other.values.TryGetValue(key, out long theirs) || mine != theirs)
                    return false;
            return true;
        }
    }

    public sealed record StatSourcesResult(StatSourcesKey Key, Dictionary<uint, int> Sources, AttributeModifiers Attributes,
        HashSet<uint> Inactive, List<ItemModStats.Worn> Worn);

    // Only raw attribute keys are inputs. A final pool/panel attribute must never feed the flat bucket on a rebuild.
    public static AttributeModifiers AttributeInputs(IReadOnlyDictionary<uint, long> raw) =>
        AttributeModifiers.FromWide(name => raw.GetValueOrDefault(name));

    public static CharacterStats.Pools PoolsFor(Player player, int level)
    {
        {
            using var snapshot = FortificationOf(player).Capture(AreaTime(player));
            var state = ConditionalItemPools.GetOrCreateValue(player);
            bool building = state.Building;
            state.Building = true;
            try
            {
                var pools = BuildPoolsFor(player, level);
                state.Applied = player.StatSourcesCache;
                return pools;
            }
            finally { state.Building = building; }
        }
    }

    public static CharacterStats.Pools BuildPoolsFor(Player player, int level)
    {
        // The stat sources first: they decide which worn items are inactive, which the weapon profile reads.
        var snapshot = CharacterStatSources(player, level: level);
        // Cat starts its Stealth phase in the same build that first sees its accepted root aura. Re-read sources because
        // Bite's conditional base critical chance becomes qualified only once the exact root owner exists.
        if (CatAspectReconcileLocked(player, AreaTime(player), snapshot.Stats) && CatAspectRootActiveLocked(player))
            snapshot = CharacterStatSources(player, level: level);
        var sources = new Dictionary<uint, int>(snapshot.Stats);
        if (Stat.BaseMovementVelocityPlusPct is var cryMovement)
            sources[cryMovement] = ActorScale.Saturate((long)sources.GetValueOrDefault(cryMovement) + IntimidatingCryMovement(player));
        AvianReconcileLocked(player, AreaTime(player), snapshot.Stats);
        CrabReconcileLocked(player, AreaTime(player), snapshot.Stats);
        foreach (var (key, value) in AvianRawStatsLocked(player, snapshot.Stats, includeReceived: true).Concat(CrabRawStatsLocked(player, snapshot.Stats)))
            sources[key] = ActorScale.Saturate((long)sources.GetValueOrDefault(key) + value);
        player.Reservations.CrabPoolsDirty = false;
        AddGolemStats(player, new PlainStatMap(sources));
        AddSelfOfferingStats(player, new PlainStatMap(sources));
        // Detach forbidden self auras before any pool bonus is sampled. No restoration here: it needs completed pools.
        SuspendRestrictedOwnAuras(player, new(sources));
        var weapons = WeaponProfileFor(player);
        var activeWeapon = WeaponProfileFor(player, activeSet: true);
        var bonus = PassiveBonus(sources, weapons, snapshot.Attributes);
        var map = MapModsOf(player);
        if (map?.Get(Stat.MapPlayerNonCurseAuraEffectPlusPct) is int auraEffect and not 0)
            bonus = bonus with { AuraEffectPct = ActorScale.Saturate((long)bonus.AuraEffectPct + auraEffect) };
        if (map?.Get(Stat.MapPlayersSpellDamagePctSuppressed) is int prevention and not 0)
            bonus = bonus with { SpellSuppressionEffect = ActorScale.Saturate((long)bonus.SpellSuppressionEffect + prevention) };
        if (map?.Get(Stat.MapAdditionalPlayerMaximumResistancesPct) is int maximumResistance and not 0)
            bonus = bonus with
            {
                FireMaxResistance = ActorScale.Saturate((long)bonus.FireMaxResistance + maximumResistance),
                ColdMaxResistance = ActorScale.Saturate((long)bonus.ColdMaxResistance + maximumResistance),
                LightningMaxResistance = ActorScale.Saturate((long)bonus.LightningMaxResistance + maximumResistance),
                ChaosMaxResistance = ActorScale.Saturate((long)bonus.ChaosMaxResistance + maximumResistance),
            };
        if (EquipmentInput(Stat.KeystonePrismaticBulwark) != 0)
            bonus = bonus with
            {
                FireMaxResistance = bonus.FireMaxResistance - 15,
                ColdMaxResistance = bonus.ColdMaxResistance - 15,
                LightningMaxResistance = bonus.LightningMaxResistance - 15,
            };
        int EquipmentInput(uint key) => sources.GetValueOrDefault(key);
        var gear = EquippedBaseDefences(player, EquipmentInput);
        var slots = EquippedArmourDefences(player, EquipmentInput).ToArray();
        var shieldDefence = slots.FirstOrDefault(d => d.Row == PlayerInventories.Offhand1).Defence;
        var body = slots.FirstOrDefault(d => d.Row == PlayerInventories.BodyArmour).Defence;
        var helmet = slots.FirstOrDefault(d => d.Row == PlayerInventories.Helmet).Defence;
        var glovesBoots = slots.Where(d => d.Row is PlayerInventories.Gloves or PlayerInventories.Boots)
            .Aggregate((Armour: 0m, Evasion: 0m), (sum, d) => (sum.Armour + d.Defence.Armour, sum.Evasion + d.Defence.Evasion));
        int EquipmentStat(uint key) => sources.GetValueOrDefault(key);
        var converted = ConvertEquipmentEnergyShield(player, EquipmentStat, gear.EnergyShield);
        bonus = bonus with
        {
            Armour = bonus.Armour + gear.Armour,
            Evasion = bonus.Evasion + gear.Evasion,
            Ward = bonus.Ward + gear.Ward,
            Life = (int)Math.Min(int.MaxValue, (long)bonus.Life + converted.Life),
            EnergyShield = (int)Math.Min(int.MaxValue, (long)bonus.EnergyShield + converted.EnergyShield),
            ShieldArmour = shieldDefence.Armour, ShieldEvasion = shieldDefence.Evasion,
            // ES-to-Life consumes the local item source before any shield/global increases can act on it.
            ShieldEnergyShield = converted.EnergyShield == 0 ? 0 : shieldDefence.EnergyShield,
            BodyArmour = body.Armour, BodyEvasion = body.Evasion,
            BodyEnergyShield = converted.EnergyShield == 0 ? 0 : body.EnergyShield,
            HelmetEnergyShield = converted.EnergyShield == 0 ? 0 : helmet.EnergyShield,
            GlovesBootsArmour = glovesBoots.Armour, GlovesBootsEvasion = glovesBoots.Evasion,
        };
        bonus = WithSlotMasteries(player, bonus, shieldDefence.EnergyShield, EquipmentStat);
        List<AuraCatalog.Aura> auras;
        auras = AffectingAuras(player);
        var minionAuras = SelectMinionAuras(player, player.Id, auras, bonus.AuraEffectPct);
        var aura = AuraCatalog.Bonus(auras.Where(a => a.AffectsSelf), bonus.AuraEffectPct, SkillsFor(player), Stats);
        aura = WithTemporaryAuraBonus(auras, bonus.AuraEffectPct, aura);
        // Commander of Darkness ("You and nearby Allies have +30% to Elemental Resistances"): applies to the caster
        // even with zero active auras, deduplicated against any Commander resistance already counted by AuraCatalog.
        int ownCommanderRes = Math.Max(0, sources.GetValueOrDefault(Stat.ElementalResistancesPlusPctForYouAndAlliesAffectedByYourAuras));
        int auraCommanderRes = auras.Where(a => a.AffectsSelf && AuraCatalog.CommanderEligible(a, SkillsFor(player)))
            .Select(a => a.CommanderElementalResistancePct).DefaultIfEmpty(0).Max();
        int extraCommanderRes = Math.Max(0, ownCommanderRes - auraCommanderRes);
        int sentinelChaos = 0;
        foreach (var sentinel in minionAuras)
            foreach (var (key, value) in sentinel.Stats)
                if (key == Stat.BaseChaosDamageResistancePct) sentinelChaos += value;
        int CatInput(uint key) => sources.GetValueOrDefault(key);
        var cat = CatAspectPhaseStatsLocked(player, bonus.AuraEffectPct, CatInput);
        bonus = bonus with
        {
            LifePerLevel = bonus.LifePerLevel,
            ManaPerLevel = bonus.ManaPerLevel,
            LifePerTenDexterity = bonus.LifePerTenDexterity,
            LifePerTenIntelligence = bonus.LifePerTenIntelligence,
            LifePerFourDexterity = bonus.LifePerFourDexterity,
            LifePerTwoIntelligence = bonus.LifePerTwoIntelligence,
            EnergyShieldPctPerTenStrength = bonus.EnergyShieldPctPerTenStrength,
            EvasionPctPerTenIntelligence = bonus.EvasionPctPerTenIntelligence,
            NoLifeFromStrength = EquipmentStat(InherentAttributeBonuses.NoLifeFromStrength) != 0,
            NoManaFromIntelligence = EquipmentStat(InherentAttributeBonuses.NoManaFromIntelligence) != 0,
            NoStrengthBonuses = EquipmentStat(InherentAttributeBonuses.NoStrengthBonuses) != 0,
            NoDexterityBonuses = EquipmentStat(InherentAttributeBonuses.NoDexterityBonuses) != 0,
            NoIntelligenceBonuses = EquipmentStat(InherentAttributeBonuses.NoIntelligenceBonuses) != 0,
        };
        long RawCriticalSum(uint name)
        {
            if (!(name is var key)) return 0;
            return (long)sources.GetValueOrDefault(key) + (name switch
            {
                Stat.CriticalStrikeChancePlusPct => aura.CriticalStrikeChancePct + cat.CriticalStrikeChancePct,
                Stat.SpellCriticalStrikeChancePlusPct => aura.SpellCriticalStrikeChancePct - aura.CriticalStrikeChancePct,
                _ => 0,
            });
        }
        int RawCritical(uint name) => (int)Math.Clamp(RawCriticalSum(name), int.MinValue, int.MaxValue);
        long attackIncrease = AttackBaseCriticalStrike.IncreasedSum(activeWeapon, RawCriticalSum);
        long spellIncrease = RawCriticalSum(Stat.CriticalStrikeChancePlusPct) + RawCriticalSum(Stat.SpellCriticalStrikeChancePlusPct);
        long RawAccuracy(uint name)
        {
            return (long)sources.GetValueOrDefault(name) + (name == Stat.AccuracyRating ? aura.Accuracy : 0);
        }
        bonus = bonus with
        {
            FireResistance = bonus.FireResistance + aura.FireResistance + extraCommanderRes,
            ColdResistance = bonus.ColdResistance + aura.ColdResistance + extraCommanderRes,
            LightningResistance = bonus.LightningResistance + aura.LightningResistance + extraCommanderRes,
            ChaosResistance = bonus.ChaosResistance + aura.ChaosResistance + sentinelChaos,
            FireMaxResistance = bonus.FireMaxResistance + aura.FireMaxResistance,
            ColdMaxResistance = bonus.ColdMaxResistance + aura.ColdMaxResistance,
            LightningMaxResistance = bonus.LightningMaxResistance + aura.LightningMaxResistance,
            ChaosMaxResistance = bonus.ChaosMaxResistance + aura.ChaosMaxResistance,
            Accuracy = AccuracyRating.Flat(activeWeapon, RawAccuracy),
            AccuracyPct = AccuracyRating.Increased(activeWeapon, RawAccuracy),
            AccuracyPerLevel = (int)RawAccuracy(Stat.AccuracyRatingPerLevel),
            DexterityAccuracyTriple = RawAccuracy(Stat.DexterityAccuracyBonusGrantsAccuracyRatingPlus3PerDexterityInstead) != 0,
            AccuracyEqualsIntelligence = RawAccuracy(Stat.GainAccuracyRatingEqualToIntelligence) != 0
                || (activeWeapon.Wand && RawAccuracy(Stat.GainWandAccuracyRatingEqualToIntelligence) != 0),
            AccuracyFromStrengthPct = (RawAccuracy(Stat.GainAccuracyRatingEqualTo2TimesStrength) != 0 ? 200 : 0)
                + (RawAccuracy(Stat.GainAccuracyRatingEqualToStrength) != 0 ? 100 : 0),
            AttackSpeedPer150Accuracy = (int)RawAccuracy(Stat.AttackSpeedPlusPctPer200AccuracyRating),
            AccuracyMoreVsUniquePct = (int)RawAccuracy(Stat.AccuracyRatingPlusPctFinalVsUniqueEnemiesFromMastery),
            AccuracyMoreInCloseRangePct = (int)RawAccuracy(Stat.AccuracyRatingPlusPctFinalVsEnemiesInCloseRangeFromMastery),
            Armour = bonus.Armour + aura.Armour,
            Evasion = bonus.Evasion + aura.Evasion,
            // Discipline is a global flat ES source, added after equipment ES-to-Life conversion.
            EnergyShield = (int)Math.Min(int.MaxValue, (long)bonus.EnergyShield + aura.EnergyShield),
            ArmourMorePct = (100m + aura.ArmourMorePct) * (100m + EquipmentInput(Stat.ArmourPlusPctFinalFromSeismicCryBuff)) / 100m - 100m,
            EvasionMorePct = aura.EvasionMorePct,
            ExtraDefenceMorePct = EvaluateFortificationLocked(player, AreaTime(player), sources).Above20DefenceMorePct,
            CriticalStrikeChancePct = attackIncrease,
            SpellCriticalStrikeChancePct = spellIncrease,
            AttackSpeedPct = bonus.AttackSpeedPct + aura.AttackSpeedPct + cat.AttackSpeedPct,
            SpellBlockChance = bonus.SpellBlockChance + (activeWeapon.Shield
                ? auras.Where(a => a.EffectId == "TempestShield").Select(a => ReservationStat(player, a, Stat.ShieldSpellBlockPct)).DefaultIfEmpty(0).Max() : 0),
            MovementVelocityPct = bonus.MovementVelocityPct + aura.MovementVelocityPct + ShieldChargeMovementPct(player)
                + FlickerMovementPct(player) + ElusiveMovementPct(player)
        };
        // Running utility flasks and Mageblood's permanent ones combined before one strongest-per-effect pass: the same
        // effect never counts twice.
        int MagebloodGet(uint k) => sources.GetValueOrDefault(k);
        var utilityStats = UtilityStats(player, MagebloodGet, level);
        bonus = ApplyItemUtilityFlaskBonuses(bonus, utilityStats);
        bool noExtraCriticalDamage = EquipmentInput(Stat.NoCriticalStrikeMultiplier) != 0
            || utilityStats.Any(row => row.Stat == Stat.NoCriticalStrikeMultiplier && row.Value != 0);
        // Final movement modifiers (a trial dart's labyrinth_arrow_movement_speed_+%_final): movement_velocity_+% is the
        // whole speed the client walks and the panel shows, so (1 + increased) × (1 + final) − 1 (the original under the
        // darts: +19% became −52%).
        // Every such stat multiplies (a tether's tethered_movement_speed_+%_final, Bear Trap's
        // bear_trap_movement_speed_+%_final, Toxic Rain's vines'); no_movement_speed (Bear Trap's beartrap_debuff) stops it.
        foreach (uint finalKey in BuffRows.FinalMovementKeys)
            if (sources.GetValueOrDefault(finalKey) is int slowFinal and not 0)
                bonus = bonus with { MovementVelocityPct = (int)Math.Round((100.0 + bonus.MovementVelocityPct) * Math.Max(0, 100 + slowFinal) / 100) - 100 };
        if (Stat.NoMovementSpeed is var stopKey && sources.GetValueOrDefault(stopKey) > 0)
            bonus = bonus with { MovementVelocityPct = -100 };
        // "Movement Speed cannot be modified to below Base Value" (PoB CalcDefence: max(mod, 1)). The client bounds
        // movement_velocity_+% below by 0 instead of -100 from the same raw flag (0x142523780).
        if (EquipmentInput(Stat.MovementSpeedCannotBeReducedBelowBase) != 0)
            bonus = bonus with { MovementVelocityPct = Math.Max(0, bonus.MovementVelocityPct) };
        if (player.God)
            bonus = bonus with { Strength = bonus.Strength + 1000, Dexterity = bonus.Dexterity + 1000, Intelligence = bonus.Intelligence + 1000,
                Life = bonus.Life + 10_000, AttackSpeedPct = bonus.AttackSpeedPct + 1000 };
        // The overcapped-resistance flags' consumers (ResistanceOvercap), derived once per pool build from the final
        // raw inputs: the resistances after the Kitava penalty and the per-type maxima. Pools stores the derived
        // increases, so the pools, the panel and the spell consumers read them from one place and nothing counts them
        // twice. The bonus already includes Purity.
        var resistanceSources = new Dictionary<uint, int>(sources);
        foreach (var (key, value) in AuraCatalog.ResistanceStats(auras.Where(a => a.AffectsSelf), bonus.AuraEffectPct, SkillsFor(player), Stats))
            resistanceSources[key] = resistanceSources.GetValueOrDefault(key) + value;
        int mapTemporalChains = 0;
        var hexMap = MapModsOf(player);
        if (HasMapHexes(hexMap))
        {
            int Get(uint key) => resistanceSources.GetValueOrDefault(key);
            int allBefore = Get(Stat.BaseResistAllElementsPct);
            int fireBefore = Get(Stat.BaseFireDamageResistancePct);
            int coldBefore = Get(Stat.BaseColdDamageResistancePct);
            int lightningBefore = Get(Stat.BaseLightningDamageResistancePct);
            int actionBefore = Get(Stat.TemporalChainsActionSpeedPlusPctFinal);
            int curseEffect = utilityStats.Concat(player.FlaskGrounds.PlayerStats())
                .Where(row => row.Stat == Stat.CurseEffectOnSelfPlusPct).Sum(row => row.Value);
            AddMapHexStats(player, hexMap, new PlainStatMap(resistanceSources), auras, curseEffect, includeResistances: true);
            int all = Get(Stat.BaseResistAllElementsPct) - allBefore;
            bonus = bonus with
            {
                FireResistance = ActorScale.Saturate((long)bonus.FireResistance + all + Get(Stat.BaseFireDamageResistancePct) - fireBefore),
                ColdResistance = ActorScale.Saturate((long)bonus.ColdResistance + all + Get(Stat.BaseColdDamageResistancePct) - coldBefore),
                LightningResistance = ActorScale.Saturate((long)bonus.LightningResistance + all + Get(Stat.BaseLightningDamageResistancePct) - lightningBefore),
            };
            mapTemporalChains = Get(Stat.TemporalChainsActionSpeedPlusPctFinal) - actionBefore;
        }
        bonus = ResistanceOvercap.Applied(bonus, resistanceSources, ResistancePenalty(player), Stats);
        var classInfo = SkillsFor(player).ClassFor(player.Character.ClassRow);
        int perDexterity = EquipmentStat(ManaRecoveryRules.PerDexterity);
        var finalAttributes = (bonus.Attributes with
        {
            StrengthPct = bonus.Attributes.StrengthPct + bonus.StrengthPct + (long)bonus.AllAttributesPct,
            DexterityPct = bonus.Attributes.DexterityPct + bonus.DexterityPct + (long)bonus.AllAttributesPct,
            IntelligencePct = bonus.Attributes.IntelligencePct + bonus.IntelligencePct + (long)bonus.AllAttributesPct,
        }).Apply((long)classInfo.Strength + bonus.Strength, (long)classInfo.Dexterity + bonus.Dexterity,
            (long)classInfo.Intelligence + bonus.Intelligence);
        bonus = bonus with { AdditionalMana = (long)finalAttributes.Dexterity * perDexterity };
        int areaAccuracy = map?.Get(Stat.MapPlayerAccuracyRatingPlusPctFinal) ?? 0;
        int weaponAccuracy = (int)RawAccuracy(Stat.GladiatorAccuracyRatingPlusPctFinalWhileWieldingSword);
        if (weaponAccuracy != 0 && !activeWeapon.Sword) weaponAccuracy = 0;
        // Deadeye's Avidity: "5% more Accuracy Rating per Frenzy Charge" (PassiveSkills AscendancyDeadeye10, Stats row
        // 20753; the charge count is already in the sources through WithCharges). The per-charge value
        // times the charges held is a more multiplier of the resolved rating, so it reaches every attack through
        // PlayerAccuracyModifiers -- read once here, once per pool build, not per hit.
        int chargeAccuracyMore = (int)Math.Clamp(RawAccuracy(Stat.DeadeyeAccuracyRatingPlusPctFinalPerFrenzyCharge)
            * sources.GetValueOrDefault(Stat.CurrentFrenzyCharges), int.MinValue, int.MaxValue);
        int ResolveAccuracy(long basis, long increased) => PlayerAccuracyModifiers.Calculate(basis, increased,
            (int)RawAccuracy(PlayerAccuracyModifiers.BootsMore), blindLess: 0, areaMorePct: areaAccuracy,
            weaponMorePct: weaponAccuracy, chargeMorePct: chargeAccuracyMore);
        var pools = CharacterStats.For(classInfo, level, bonus, ResolveAccuracy);
        // Reservation-derived defences are a second, explicit projection from the completed full maximum Life. The
        // added Energy Shield is a global flat source and cannot feed back into Life conversion or reservation.
        int itemReservationPercent = EquipmentStat(LifeReservation.ItemPercentStat);
        var reservedLife = LifeReservation.FromInputs((int)pools.Life, itemReservationPercent,
            pools.LifeReservationEfficiencyPct, player.Auras.Values, AdditionalLifeReservation(player));
        int perHundred = LifeReservation.EnergyShieldPerReservedStat is var reservedEsKey
            ? sources.GetValueOrDefault(reservedEsKey) : 0;
        int reservationEnergyShield = LifeReservation.EnergyShield(reservedLife.Reserved, perHundred);
        if (reservationEnergyShield != 0)
            pools = CharacterStats.For(classInfo, level, bonus with
            {
                EnergyShield = (int)Math.Min(int.MaxValue, (long)bonus.EnergyShield + reservationEnergyShield)
            }, ResolveAccuracy);
        pools = pools with { MapTemporalChainsActionSpeedPct = mapTemporalChains };
        if (true)
            pools = pools with { SpellCriticalStrikeChancePct = pools.SpellCriticalStrikeChancePct
                + MaximumPoolItemSources.Amount(EquipmentInput(MaximumPoolItemSources.SpellCriticalPerLife), pools.Life) };
        int criticalPerAccuracy = EquipmentInput(Stat.AttackCriticalStrikeChancePlusPctPer200AccuracyRating);
        if (criticalPerAccuracy != 0)
            pools = pools with { CriticalStrikeChancePct = pools.CriticalStrikeChancePct
                + (long)criticalPerAccuracy * (Math.Max(0, pools.Accuracy) / 200) };
        player.PlayerLifeCap = reservedLife.Unreserved;
        // The pure base critical stats (/set_stat too) sit outside the passive increases: the weapon override replaces
        // the armed main hand's base and the unarmed base replaces the empty one, before the global increases.
        pools = pools with
        {
            CriticalStrikeMultiplier = CriticalHitMultiplier(pools.CriticalStrikeMultiplier, noExtraCriticalDamage),
            SpellCriticalStrikeMultiplier = CriticalHitMultiplier(pools.SpellCriticalStrikeMultiplier, noExtraCriticalDamage),
            AttackBlockLucky = pools.AttackBlockLucky || player.ForceAttackBlockLucky,
            SpellBlockLucky = pools.SpellBlockLucky || player.ForceSpellBlockLucky,
            SpellSuppressionLucky = pools.SpellSuppressionLucky || player.ForceSpellSuppressionLucky,
            SpellSuppressionUnlucky = pools.SpellSuppressionUnlucky || player.ForceSpellSuppressionUnlucky,
            // WeaponTypes.dat gives the main hand's base critical chance (permyriad, e.g. 830 = 8.30%); unarmed is 0.
            // A pure base stat takes its place before the global increases: the weapon override (Slayer Overwhelm,
            // Blind Prophet's Sightless) replaces the armed weapon's base, already with its local mods, and unarmed
            // melee uses its own Behemoth base. Neither base is a spell value; the spell route stays on SpellCriticalStrike.
            AttackCriticalStrikeChance = DisplayedCriticalChance(player, AttackBaseCriticalStrike.Chance(!activeWeapon.Unarmed,
                activeWeapon.Critical, pools.CriticalStrikeChancePct, RawCritical), RawCritical),
            SpellCriticalStrikeChance = DisplayedCriticalChance(player, SpellCriticalStrike.Chance(
                DamageModel.BaseSpellCriticalStrikeChancePerMyriad, RawCritical,
                playerIncrease: pools.SpellCriticalStrikeChancePct), RawCritical)
        };
        // Only the active usable shield contributes. The alternate set and unmet requirements cannot grant block.
        int shieldBlock = EquipmentInput(Stat.MinionsGetShieldStatsInsteadOfYou) == 0
            && ActiveOffHand(player) is { Type.Class: "Shield" } shield
            ? EquipmentInput(Stat.BlockChanceFromEquippedShieldIsPct) is > 0 and var replacement
                ? replacement : ItemModStats.ShieldBlock(ItemsFor(player), shield.Item, shield.Type) : 0;
        // The Shield Mastery's "+1% Chance to Block Attack Damage per 5% Chance to Block on Equipped Shield".
        int rawAttack = pools.BlockChanceUncapped + shieldBlock
            + Math.Max(0, shieldBlock) / 5 * EquipmentStat(Stat.ChanceToBlockAttackDamagePer5PctChanceToBlockOnEquippedShieldPlusPct);
        double blockIncrease = bonus.BlockChanceInc + (double)(map?.Get(Stat.MapPlayersBlockChancePlusPct) ?? 0);
        double blockFactor = Math.Max(0, 1 + blockIncrease / 100) * (1 + bonus.BlockChanceMore / 100.0);
        int totalAttack = (int)Math.Round(Math.Max(0, rawAttack) * blockFactor);
        int totalSpell = (int)Math.Round(Math.Max(0, pools.SpellBlockChance) * blockFactor);
        pools = pools with
        {
            BlockChanceBase = shieldBlock,
            BlockChanceUncapped = Math.Max(0, totalAttack),
            AttackBlockAtMaximum = EquipmentInput(Stat.MaxChanceToBlockAttacksIfNotBlockedRecently) > 0,
            SpellBlockAtMaximum = EquipmentInput(Stat.SpellBlockIsMaximumIfNotBlockedRecently) > 0,
            BlockChance = EquipmentInput(Stat.MaxChanceToBlockAttacksIfNotBlockedRecently) > 0
                ? pools.BlockChanceMax : Math.Min(Math.Max(0, totalAttack), pools.BlockChanceMax),
            SpellBlockChanceUncapped = Math.Max(0, totalSpell),
            SpellBlockChance = Math.Min(Math.Max(0, totalSpell), pools.SpellBlockChanceMax)
        };
        // "+1% Chance to Block Spell Damage per 5% Chance to Block Attack Damage": the attack value is the final capped
        // chance, including the shield's base. The effect stacks additively and is capped by the spell block maximum.
        if (pools.SpellBlockPer5AttackBlock != 0 && pools.BlockChance > 0)
            pools = pools with
            {
                SpellBlockChance = Math.Min(pools.SpellBlockChance + pools.BlockChance / 5 * pools.SpellBlockPer5AttackBlock,
                    pools.SpellBlockChanceMax)
            };
        // Versatile Combatant: +2% spell block for each 1% of attack block above its (keystone-reduced) maximum.
        if (pools.VersatileCombatant)
        {
            int overcap = Math.Max(0, pools.BlockChanceUncapped - pools.BlockChanceMax);
            if (overcap > 0)
                pools = pools with
                {
                    SpellBlockChance = Math.Min(pools.SpellBlockChance + 2 * overcap, pools.SpellBlockChanceMax)
                };
        }
        // The source's condition has already been evaluated from the captured blocked history. The Replica's
        // retained value 75 is an unscalable flag, not a chance to add or a replacement for the current maximum.
        if (pools.SpellBlockAtMaximum) pools = pools with { SpellBlockChance = pools.SpellBlockChanceMax };
        if (map?.Get(Stat.MapPlayersArmourPlusPctFinal) is int armourMore and not 0)
            pools = pools with { Armour = ActorScale.Saturate((pools.Armour * Math.Max(0, 100L + armourMore) + 50) / 100) };
        player.AppliedAdrenaline = player.Antiquarian.Adrenaline(AreaTime(player));
        ConfigureFortificationRecovery(player, pools.Life, sources);
        int lifeAsEnergyShield = ActorScale.Saturate((long)EquipmentInput(Stat.MaximumLifePctToAddAsMaximumEnergyShield)
            + (EquipmentInput(Stat.KeystoneCorruptedDefences) != 0 ? 15 : 0));
        pools = pools with { EnergyShield = bonus.NoEnergyShield ? 0u : IncomingResourceDefences.Maximum(pools.EnergyShield,
            pools.Life, pools.Mana, pools.EnergyShieldPct,
            lifeAsEnergyShield,
            EquipmentInput(Stat.ManaPctToAddAsEnergyShield)) };
        if (map?.Get(Stat.MapPlayerGlobalDefencesPlusPct) is int defences and not 0)
        {
            long percent = Math.Max(0, 100L + defences);
            long Scale(long value) => (value * percent + 50) / 100;
            pools = pools with
            {
                Armour = ActorScale.Saturate(Scale(pools.Armour)),
                Evasion = ActorScale.Saturate(Scale(pools.Evasion)),
                EvasionWhileMoving = pools.EvasionWhileMoving is int moving ? ActorScale.Saturate(Scale(moving)) : null,
                EnergyShield = (uint)Math.Min(uint.MaxValue, Scale(pools.EnergyShield)),
                Ward = (uint)Math.Min(uint.MaxValue, Scale(pools.Ward)),
            };
        }
        player.Ward.SetMaximum(player.PlayerLife > 0 ? pools.Ward : 0);
        return pools with
        {
            AccuracyAreaMorePct = areaAccuracy,
            AccuracyWeaponMorePct = weaponAccuracy,
            AccuracyChargeMorePct = chargeAccuracyMore,
            FortificationRevision = FortificationOf(player).Revision,
            AttackerSlowPct = EquipmentInput(AttackerSlow), EnemyRegenerationOnHitPct = EquipmentInput(EnemyRegenerationOnHit),
            VsBlindedCriticalPct = EquipmentInput(VsBlindedCriticalStat), WhileMovingStats = WhileMovingInputs.Any(n => EquipmentInput(n) != 0),
        };
    }

    // What the allocated passives add to the pools: attributes (base_* and additional_*, all attributes and the pairs),
    // flat and increased life, mana and accuracy, flat and increased armour and evasion, energy shield and melee physical
    // damage, the base resistances (base_resist_all_elements_% applies to fire, cold and lightning), the shield-specific
    // block nodes when a shield is equipped, critical strike chance and multiplier, and reservation efficiency and aura
    // effect.

    public static CharacterStats.Bonus PassiveBonus(Dictionary<uint, int> stats, WeaponProfile weapons, AttributeModifiers attributes)
    {
        int Get(uint k) => stats.GetValueOrDefault(k);
        int allRes = Get(Stat.BaseResistAllElementsPct);
        // The item tables also spell resistances without the base_ prefix (The Annihilating Light's -70% elemental,
        // the Cowl uniques' reduced single resistances); both spellings are the same character stat.
        int elementalRes = Get(Stat.ElementalDamageResistancePlusPct);
        // The game's block stats are conditional on the equipped weapons:
        // - dual_wield_or_shield_block_%: dual wielding or a shield;
        // - block_while_dual_wielding_% / _claws: dual wielding (the latter with two claws);
        // - staff_block_% / additional_staff_block_%: a staff;
        // - shield_block_% / block_chance_%_while_holding_shield: a shield.
        int blockWhileDualOrShield = weapons is { Shield: true } or { DualWield: true } ? Get(Stat.DualWieldOrShieldBlockPct) : 0;
        int shieldBlock = weapons.Shield ? Get(Stat.ShieldBlockPct) + Get(Stat.BlockChancePctWhileHoldingShield) : 0;
        int staffBlock = weapons.Staff ? Get(Stat.StaffBlockPct) + Get(Stat.AdditionalStaffBlockPct) : 0;
        // 20 is the inherent dual-wield block chance (PoB Misc.lua); the Dual Wielding Mastery can remove it.
        int dualBlock = weapons.DualWield
            ? Get(Stat.BlockWhileDualWieldingPct) + (weapons.DualClaws ? Get(Stat.BlockWhileDualWieldingClawsPct) : 0)
                + (Get(Stat.NoInherentChanceToBlockWhileDualWielding) != 0 ? 0 : 20)
            : 0;
        int bowBlock = weapons.Bow ? Get(Stat.WithBowAdditionalBlockPct) : 0;
        // Spell block has the matching weapon-specific stats; the staff-or-shield stat is shared.
        int staffOrShieldSpell = weapons is { Staff: true } or { Shield: true } ? Get(Stat.SpellBlockChancePctWhileHoldingStaffOrShield) : 0;
        int shieldSpellBlock = weapons.Shield ? Get(Stat.ShieldSpellBlockPct) + Get(Stat.BlockSpellsChancePctWhileHoldingShield) : 0;
        int staffSpellBlock = weapons.Staff ? Get(Stat.SpellBlockWithStaffPct) : 0;
        int dualSpellBlock = weapons.DualWield ? Get(Stat.SpellBlockWhileDualWieldingPct) : 0;
        int bowSpellBlock = weapons.Bow ? Get(Stat.SpellBlockWithBowPct) : 0;
        // Attack speed passives, conditional on the equipped weapon shape as in PoB's ModFlags: generic, melee,
        // one-/two-handed melee, all one-handed, the weapon families, shields and dual wielding. Minion, totem and
        // timed/conditional stats are not player attack speed here.
        int attackSpeed = Get(Stat.AttackSpeedPlusPct) + Get(Stat.AttackAndCastSpeedPlusPct)
            + (weapons.Melee ? Get(Stat.MeleeAttackSpeedPlusPct) : 0)
            + (weapons.OneHandMelee ? Get(Stat.OneHandedMeleeAttackSpeedPlusPct) : 0)
            + (weapons.TwoHandMelee ? Get(Stat.TwoHandedMeleeAttackSpeedPlusPct) : 0)
            + (weapons.OneHand ? Get(Stat.OneHandedAttackSpeedPlusPct) : 0)
            + (weapons.Unarmed ? Get(Stat.UnarmedMeleeAttackSpeedPlusPct) : 0)
            + (weapons.Shield ? Get(Stat.AttackSpeedPlusPctWhileHoldingShield) + Get(Stat.ShieldAttackSpeedPlusPct) : 0)
            + (weapons.DualWield ? Get(Stat.AttackSpeedWhileDualWieldingPlusPct) : 0)
            + (weapons.Axe ? Get(Stat.AxeAttackSpeedPlusPct) : 0)
            + (weapons.Sword ? Get(Stat.SwordAttackSpeedPlusPct) : 0)
            + (weapons.Mace ? Get(Stat.MaceAttackSpeedPlusPct) : 0)
            + (weapons.Claw ? Get(Stat.ClawAttackSpeedPlusPct) : 0)
            + (weapons.Dagger ? Get(Stat.DaggerAttackSpeedPlusPct) : 0)
            + (weapons.Wand ? Get(Stat.WandAttackSpeedPlusPct) : 0)
            + (weapons.Bow ? Get(Stat.BowAttackSpeedPlusPct) : 0)
            + (weapons.Staff ? Get(Stat.StaffAttackSpeedPlusPct) : 0);
        // PoB Misc.lua: 10% more attack speed while dual wielding; unarmed attacks count as dual wielding.
        // ItemWeaponStats already applies the weapon's local speed to its duration, before character increases.
        // The Claw Mastery doubles it while wielding two claws.
        int attackSpeedMore = weapons.DualWield || weapons.Unarmed
            ? weapons.DualClaws && Get(Stat.DualWieldInherentAttackSpeedIsDoubledWhileDualWieldingClaws) != 0 ? 20 : 10
            : 0;
        // Unconditional critical strike chance and multiplier passives: generic, attack/melee/weapon-shape and spell.
        // Timed or enemy-state conditions are not modelled.
        long criticalChance = AttackBaseCriticalStrike.IncreasedSum(weapons, name => Get(name));
        long spellCriticalChance = (long)Get(Stat.CriticalStrikeChancePlusPct) + Get(Stat.SpellCriticalStrikeChancePlusPct);
        int criticalMultiplier = Get(Stat.BaseCriticalStrikeMultiplierPlus)
            + (weapons.Melee ? Get(Stat.Of("melee_critical_strike_multiplier_+")) + Get(Stat.MeleeWeaponCriticalStrikeMultiplierPlus) : 0)
            + (weapons.OneHandMelee ? Get(Stat.OneHandedMeleeCriticalStrikeMultiplierPlus) : 0)
            + (weapons.TwoHandMelee ? Get(Stat.TwoHandedMeleeCriticalStrikeMultiplierPlus) : 0)
            + (weapons.DualWield ? Get(Stat.CriticalStrikeMultiplierWhileDualWieldingPlus) : 0)
            + (weapons.Axe ? Get(Stat.AxeCriticalStrikeMultiplierPlus) : 0)
            + (weapons.Sword ? Get(Stat.SwordCriticalStrikeMultiplierPlus) : 0)
            + (weapons.Mace ? Get(Stat.MaceCriticalStrikeMultiplierPlus) : 0)
            + (weapons.Claw ? Get(Stat.ClawCriticalStrikeMultiplierPlus) : 0)
            + (weapons.Dagger ? Get(Stat.Of("dagger_critical_strike_multiplier_+")) + Get(Stat.CriticalStrikeMultiplierWithDaggerPlus) : 0)
            + (weapons.Wand ? Get(Stat.WandCriticalStrikeMultiplierPlus) : 0)
            + (weapons.Bow ? Get(Stat.BowCriticalStrikeMultiplierPlus) + Get(Stat.GlobalCriticalStrikeMultiplierPlusWhileHoldingBow) : 0)
            + (weapons.Staff ? Get(Stat.StaffCriticalStrikeMultiplierPlus) + Get(Stat.GlobalCriticalStrikeMultiplierPlusWhileHoldingStaff) : 0)
            + (Get(Stat.TinctureApplied) != 0 ? Get(Stat.CriticalStrikeMultiplierPlusWithTincturedWeapons) : 0);
        int spellCriticalMultiplier = Get(Stat.BaseCriticalStrikeMultiplierPlus)
            + Get(Stat.BaseSpellCriticalStrikeMultiplierPlus) + Get(Stat.SpellCriticalStrikeMultiplierPlus);
        // Overwhelm's per-nearby-enemy multiplier joins the generic one (client 0x14276e1f0), for attacks and spells.
        int nearbyMultiplier = (int)Slayer.CritMultiplierFromNearby(Get);
        criticalMultiplier += nearbyMultiplier;
        spellCriticalMultiplier += nearbyMultiplier;
        // Accuracy passives: generic flat and increased, the weapon-specific flat and increased stats, and the Accuracy
        // Mastery's conversions, plus its two target-conditional "more" effects (below; TargetAccuracy applies them to
        // the hit's own target at the roll). Other conditional accuracy stays unmodelled. Unarmed attacks count as dual
        // wielding, as they do for attack speed.
        long accuracyFlat = AccuracyRating.Flat(weapons, name => Get(name));
        long accuracyPct = AccuracyRating.Increased(weapons, name => Get(name));
        // The Accuracy Mastery's two target-conditional effects (11-skills.md panels): final "more" accuracy for a hit's
        // own target, which TargetAccuracy applies at the roll. Kept out of the additive pools, so the panel and
        // /accuracy keep showing only the unconditional rating and never claim a target condition statically.
        int accuracyMoreVsUnique = Get(Stat.AccuracyRatingPlusPctFinalVsUniqueEnemiesFromMastery);
        int accuracyMoreCloseRange = Get(Stat.AccuracyRatingPlusPctFinalVsEnemiesInCloseRangeFromMastery);
        int allMax = Get(Stat.AdditionalMaximumAllResistancesPct), allElemMax = Get(Stat.AdditionalMaximumAllElementalResistancesPct);
        // The pair-resistance stats: one input adds to both named elements (Cold and Lightning is Sea Legs').
        int pairRes = Get(ResistanceOvercap.ColdAndLightningResistance);
        int fireColdRes = Get(Stat.FireAndColdDamageResistancePct);
        int fireLightningRes = Get(Stat.FireAndLightningDamageResistancePct);
        // The three chaos pairs follow the same rule: each adds to both named resistances.
        int fireChaosRes = Get(Stat.FireAndChaosDamageResistancePct);
        int coldChaosRes = Get(Stat.ColdAndChaosDamageResistancePct);
        int lightningChaosRes = Get(Stat.LightningAndChaosDamageResistancePct);
        // Reservation efficiency: generic efficiency applies to both pools; the mana/life-specific stats only to theirs.
        // Aura effect is the generic non-curse/aura effect; the Reservation mastery's self-aura effect is included.
        int manaReservation = Get(Stat.BaseManaReservationEfficiencyPlusPct) + Get(Stat.BaseReservationEfficiencyPlusPct)
            + Get(Stat.ManaReservationEfficiencyPlusPct) + Get(Stat.ReservationEfficiencyPlusPct)
            - 2 * Get(Stat.ManaReservationEfficiencyMinus2PctPer1);
        int lifeReservation = Get(Stat.BaseLifeReservationEfficiencyPlusPct) + Get(Stat.BaseReservationEfficiencyPlusPct)
            + Get(Stat.LifeReservationEfficiencyPlusPct) + Get(Stat.ReservationEfficiencyPlusPct);
        // Absolution's increase counts while the player has a linked target, as the client's 0x142829960 adds it.
        int auraEffect = Get(Stat.NonCurseAuraEffectPlusPct) + Get(Stat.AuraEffectPlusPct)
            + Get(Stat.AuraEffectOnSelfFromYourSkillsPlusPct)
            + (Get(Stat.NumberOfActiveLinks) > 0 ? Get(Stat.NonCurseAuraEffectPlusPctWhileLinked) : 0);
        int auraArea = Get(Stat.BaseAuraAreaOfEffectPlusPct);
        // Defences: the tree's flat armour/evasion and their increased modifiers. "Defences" and the combined armour and
        // evasion increase apply to both ratings, as in PoB's calcLib.mod.
        int defencesPct = Get(Stat.GlobalDefencesPlusPct) + Get(Stat.CombinedGlobalDefencesPlusPct)
            + Get(Stat.EvasionAndPhysicalDamageReductionRatingPlusPct)
            + Get(Stat.CombinedArmourAndEvasionRatingPlusPct);
        int armourPct = Get(Stat.PhysicalDamageReductionRatingPlusPct) + defencesPct;
        int evasionPct = Get(Stat.EvasionRatingPlusPct) + defencesPct;
        long attackBlockLuck = (long)Get(Stat.BaseBlockLuck) + Get(Stat.AttackBlockLuck) + Get(Stat.BaseAttackBlockLuck);
        long spellBlockLuck = (long)Get(Stat.BaseBlockLuck) + Get(Stat.SpellBlockLuck) + Get(Stat.BaseSpellBlockLuck);
        decimal lifeMorePct = Get(Stat.LifeMasteryCountMaximumLifePlusPctFinal);
        if (Get(Stat.KeystoneBloodMagic) != 0)
            lifeMorePct = (100m + lifeMorePct) * (100m + BloodMagic.LifeMorePct) / 100m - 100m;
        return new CharacterStats.Bonus(
            Attributes: attributes, Ward: Get(Stat.BaseWard), WardPct: Get(Stat.WardPlusPct) + Get(Stat.GlobalDefencesPlusPct) + Get(Stat.CombinedGlobalDefencesPlusPct),
            Life: Get(Stat.BaseMaximumLife), LifePct: Get(Stat.MaximumLifePlusPct), Mana: Get(Stat.BaseMaximumMana), ManaPct: Get(Stat.MaximumManaPlusPct),
            LifeMorePct: lifeMorePct,
            ManaPerFourStrength: Get(StrengthMana.PerFourStrength),
            LifePerLevel: Get(LifeManaPoolSources.LifePerLevel), ManaPerLevel: Get(LifeManaPoolSources.ManaPerLevel),
            LifePerTenDexterity: Get(LifeManaPoolSources.LifePerTenDexterity),
            LifePerTenIntelligence: Get(LifeManaPoolSources.LifePerTenIntelligence),
            LifePerFourDexterity: Get(LifeManaPoolSources.LifePerFourDexterity),
            LifePerTwoIntelligence: Get(LifeManaPoolSources.LifePerTwoIntelligence),
            NoLifeFromStrength: Get(InherentAttributeBonuses.NoLifeFromStrength) != 0,
            NoManaFromIntelligence: Get(InherentAttributeBonuses.NoManaFromIntelligence) != 0,
            NoStrengthBonuses: Get(InherentAttributeBonuses.NoStrengthBonuses) != 0,
            NoDexterityBonuses: Get(InherentAttributeBonuses.NoDexterityBonuses) != 0,
            NoIntelligenceBonuses: Get(InherentAttributeBonuses.NoIntelligenceBonuses) != 0,
            // Magebane: "Dexterity provides no inherent bonus to Evasion Rating; +1% Chance to Suppress Spell Damage
            // per 15 Dexterity" (poewiki /wiki/Magebane).
            NoDexterityEvasion: Get(Stat.KeystoneMagebane) != 0
                || Get(Stat.GainNoInherentEvasionRatingPlusPctFromDexterity) != 0,
            SpellSuppressionPerFifteenDexterity: Get(Stat.KeystoneMagebane) != 0 ? 1 : 0,
            Armour: Get(Stat.BasePhysicalDamageReductionRating), ArmourPct: armourPct,
            Evasion: Get(Stat.BaseEvasionRating), EvasionPct: evasionPct,
            EvasionWhileMovingPct: Get(Stat.EvasionRatingPlusPctWhileMoving),
            EvasionPctPerTenIntelligence: Get(AttributeDefenceSources.EvasionPerTenIntelligence)
                + Get(AttributeDefenceSources.MasteryEvasionPerTenIntelligence),
            SharedDefencesPct: defencesPct, IronReflexes: Get(Stat.KeystoneIronReflexes) != 0,
            MentalConditioning: Get(PowerOfPurpose.RawFlag) != 0,
            ChaosInoculation: Get(Stat.KeystoneChaosInoculation) != 0,
            // Blood Magic (the tree keystone and every item mod grant keystone_blood_magic): "Removes all mana. 10% more
            // maximum Life" (ReminderTextKeystoneBloodMagic). no_mana/base_no_mana ("Removes all Mana") remove it too.
            NoMana: Get(Stat.KeystoneBloodMagic) != 0 || Get(Stat.NoMana) != 0 || Get(Stat.BaseNoMana) != 0,
            ArmourAppliesToChaosDamage: Get(PlayerHitMitigation.ArmourAppliesToChaos) != 0,
            EnergyShield: Get(Stat.BaseMaximumEnergyShield),
            EnergyShieldPctPerTenStrength: Get(AttributeDefenceSources.EnergyShieldPerTenStrength),
            EnergyShieldPct: Get(Stat.MaximumEnergyShieldPlusPct) + Get(Stat.GlobalDefencesPlusPct) + Get(Stat.CombinedGlobalDefencesPlusPct),
            ShieldArmourPct: Get(Stat.ShieldPhysicalDamageReductionRatingPlusPct),
            ShieldEvasionPct: Get(Stat.ShieldEvasionRatingPlusPct),
            ShieldEnergyShieldPct: Get(Stat.ShieldMaximumEnergyShieldPlusPct),
            // Despite its name, shield_armour_+% increases all shield defences (native 0x14272b800).
            // combined_shield_defences_+% is the native derived value, not a second additive source.
            SharedShieldDefencesPct: Get(Stat.ShieldArmourPlusPct),
            NoShieldDefences: Get(Stat.MinionsGetShieldStatsInsteadOfYou) != 0
                || Get(Stat.GainNoDefencesFromEquippedShield) != 0,
            // The Armour and Energy Shield Mastery's body increase reaches here only while its named equipment
            // condition holds (NamedItemConditions); otherwise the condition fold drops it.
            BodyEnergyShieldPct: Get(Stat.MaximumEnergyShieldFromBodyArmourPlusPct)
                + Get(Stat.ArmourAndEnergyShieldPlusPctFromBodyArmourIfGlovesHelmetBootsHaveArmourAndEnergyShield),
            BodyArmourPct: Get(Stat.ArmourAndEnergyShieldPlusPctFromBodyArmourIfGlovesHelmetBootsHaveArmourAndEnergyShield),
            BodyEvasionPct: Get(Stat.BodyArmourEvasionRatingPlusPct),
            HelmetEnergyShieldPct: Get(Stat.EnergyShieldFromHelmetPlusPct),
            GlovesBootsArmourPct: Get(Stat.ArmourFromGlovesAndBootsPlusPct),
            DoubleBodyArmour: Get(Stat.DamageReductionRatingFromBodyArmourDoubled) != 0,
            NoBodyArmour: Get(Stat.GainNoArmourFromBodyArmour) != 0,
            MeleePhysicalDamagePct: Get(Stat.MeleePhysicalDamagePlusPct),
            FireResistance: allRes + Get(Stat.BaseFireDamageResistancePct) + Get(Stat.FireDamageResistancePlusPct) + elementalRes
                + fireColdRes + fireLightningRes + fireChaosRes,
            ColdResistance: allRes + Get(Stat.BaseColdDamageResistancePct) + Get(Stat.ColdDamageResistancePlusPct) + elementalRes
                + pairRes + fireColdRes + coldChaosRes,
            LightningResistance: allRes + Get(Stat.BaseLightningDamageResistancePct) + Get(Stat.LightningDamageResistancePlusPct)
                + elementalRes + pairRes + fireLightningRes + lightningChaosRes,
            ChaosResistance: Get(Stat.BaseChaosDamageResistancePct) + fireChaosRes + coldChaosRes + lightningChaosRes,
            FireMaxResistance: allMax + allElemMax + Get(Stat.BaseMaximumFireDamageResistancePct),
            ColdMaxResistance: allMax + allElemMax + Get(Stat.BaseMaximumColdDamageResistancePct),
            LightningMaxResistance: allMax + allElemMax + Get(Stat.BaseMaximumLightningDamageResistancePct),
            ChaosMaxResistance: allMax + Get(Stat.BaseMaximumChaosDamageResistancePct),
            // The former dodge mods: "150% of value" is the description's multiplier, shown rounded down.
            SpellSuppressionChance: Get(Stat.BaseSpellSuppressionChancePct) + Get(Stat.BaseSpellSuppressionChance150PctOfValue) * 3 / 2,
            SpellSuppressionEffect: Get(Stat.BaseSpellDamagePctSuppressed),
            SpellSuppressionLucky: Get(Stat.SpellSuppressionChanceIsLucky) != 0,
            SpellSuppressionUnlucky: Get(Stat.Of("spell_suppression_chance_is_unlucky")) != 0,
            SpellSuppressionWhileFullLife: Get(Stat.SpellSuppressionChancePctWhileOnFullLife),
            SpellSuppressionWhileMoving: Get(Stat.SpellSuppressionChancePctWhileMoving),
            SpellSuppressionIfHitRecently: Get(Stat.SpellSuppressionChancePctIfEnemyHitRecently),
            SpellSuppressionWhileFullEnergyShield: Get(Stat.SpellSuppressionChancePctWhileOnFullEnergyShield),
            // Item mods state movement as movement_velocity_+% (the conditional "when on low/full life" ones fold onto
            // it); passives and base-speed mods use base_movement_velocity_+%. Both are speed inputs.
            MovementVelocityPct: Get(Stat.BaseMovementVelocityPlusPct) + Get(Stat.MovementVelocityPlusPct),
            BlockChance: Get(Stat.AdditionalBlockPct) + Get(Stat.AttackBlockPct) + Get(Stat.MonsterBaseBlockPct)
                + blockWhileDualOrShield + shieldBlock + staffBlock + dualBlock + bowBlock,
            SpellBlockChance: Get(Stat.BaseSpellBlockPct) + Get(Stat.AdditionalSpellBlockPct) + staffOrShieldSpell
                + shieldSpellBlock + staffSpellBlock + dualSpellBlock + bowSpellBlock,
            BlockChanceMax: Get(Stat.MaximumBlockPct), SpellBlockChanceMax: Get(Stat.BaseMaximumSpellBlockPct),
            // Glancing Blows doubles attack and spell block, a 100% more (PoB ModParser), before the caps.
            BlockChanceInc: Get(Stat.BlockChancePlusPct), BlockChanceMore: Get(Stat.KeystoneGlancingBlows) != 0
                ? 2 * Get(Stat.BlockChancePlusPctFinal) + 100 : Get(Stat.BlockChancePlusPctFinal),
            SpellBlockPer5AttackBlock: Get(Stat.SpellBlockPctPer5AttackBlock),
            LifeGainedOnBlock: Get(Stat.LifeGainedOnBlock) + Get(Stat.RecoverXLifeOnBlock),
            ManaGainedOnBlock: Get(Stat.ManaGainedOnBlock),
            VersatileCombatant: Get(Stat.KeystoneVersatileCombatant) != 0,
            AttackBlockLucky: attackBlockLuck > 0, AttackBlockUnlucky: attackBlockLuck < 0,
            SpellBlockLucky: spellBlockLuck > 0, SpellBlockUnlucky: spellBlockLuck < 0,
            AttackSpeedPct: attackSpeed, AttackSpeedMorePct: attackSpeedMore,
            CriticalStrikeChancePct: criticalChance, SpellCriticalStrikeChancePct: spellCriticalChance,
            CriticalStrikeMultiplier: criticalMultiplier, SpellCriticalStrikeMultiplier: spellCriticalMultiplier,
            Accuracy: accuracyFlat, AccuracyPct: accuracyPct,
            AccuracyMoreVsUniquePct: accuracyMoreVsUnique,
            AccuracyMoreInCloseRangePct: accuracyMoreCloseRange,
            AccuracyPerLevel: Get(Stat.AccuracyRatingPerLevel),
            DexterityAccuracyTriple: Get(Stat.DexterityAccuracyBonusGrantsAccuracyRatingPlus3PerDexterityInstead) != 0,
            AccuracyEqualsIntelligence: Get(Stat.GainAccuracyRatingEqualToIntelligence) != 0,
            ManaReservationEfficiencyPct: manaReservation, LifeReservationEfficiencyPct: lifeReservation,
            AuraEffectPct: auraEffect, AuraAreaPct: auraArea)
        {
            ConvertSuppressionToSpellDodge = Get(Stat.KeystoneAcrobatics) != 0,
            BaseSpellDodgeChance = Get(Stat.ChanceToDodgeSpellsPct),
            NoEnergyShield = Get(Stat.KeystoneMiracleOfThaumaturgy) != 0,
        };
    }

    // The character's passive skill points (one per level after the first, plus the extra points from
    // /add_passive_points; the client computes the same from the level in 0x33 and its extra_skill_points field), the
    // allocated nodes and the selected mastery effects. The client allows that many nodes plus the points the allocated
    // nodes grant themselves.
    public static (ushort Points, ushort RefundPoints, List<ushort> Nodes, Dictionary<ushort, ushort> Masteries) PassiveState(Player player)
    {
        return ((ushort)Math.Min(ushort.MaxValue, Progression.PassivePoints(player.Pools.Level) + player.Character.ExtraPassivePoints + QuestPassivePoints(player)),
            player.Character.RefundPoints, [.. player.Character.Passives], new(player.Character.MasteryEffects));
    }

    // Drops the last allocated passives beyond the level's points (call in the area's frame).
    public static void TrimPassives(Player player, int level)
    {
        int points = Progression.PassivePoints(level) + player.Character.ExtraPassivePoints + QuestPassivePoints(player) + AscendancyOrdinaryPoints(player);
        TrimOrdinaryPassives(player, player.Character.Passives, points);
        // A socket that had to be given up returns its jewel to the inventory (the normal case keeps jeweled sockets).
        NormalizePassiveJewels(player);
    }

    public static void ValidateOrdinaryPassives(Player player, IEnumerable<ushort> nodes, AscendancyProgress ascendancy,
        IReadOnlyDictionary<ushort, HashSet<ushort>>? bypass = null)
    {
        var candidate = nodes as IReadOnlyCollection<ushort> ?? nodes.ToArray();
        bypass ??= AllocationBypassNodes(player, candidate);
        OrdinaryPassives.Validate((byte)SkillsFor(player).ClassFor(player.Character.ClassRow).IntegerId,
            ascendancy.Selection?.Family ?? AscendancyFamily.Normal, candidate, ascendancy.Allocated,
            bypass.Keys.ToHashSet(), bypass);
    }

    // Allocated passives that Intuitive Leap, Thread of Hope or Impossible Escape lets stand without a path to the class start,
    // with the socket(s) of the jewels that grant each one. The candidate set decides whether the jewel's own socket
    // is still allocated; skipItemId simulates taking that jewel out before the item move validates the remaining
    // tree.
    public static Dictionary<ushort, HashSet<ushort>> AllocationBypassNodes(Player player, IEnumerable<ushort> candidate,
        uint? skipItemId = null)
    {
        var allocated = candidate.ToHashSet();
        return AllocationBypassNodes(allocated, ItemsOf(player).Items(PlayerInventories.PassiveJewels)
            .Where(item => item.Id != skipItemId).Select(item => (item.X, item.Item)), ItemsFor(player).Mods, PassivesFor(player));
    }

    public static Dictionary<ushort, HashSet<ushort>> AllocationBypassNodes(IReadOnlySet<ushort> allocated,
        IEnumerable<(byte X, Item Item)> jewels, ModCatalog mods, PassiveTree tree)
    {
        var result = new Dictionary<ushort, HashSet<ushort>>();
        // No socket is allocated, so no jewel counts. A character with no save gets here from the read-only admission
        // preflight, and its first item load would build and save its start kit.
        if (allocated.Count == 0) return result;
        foreach (var item in jewels)
        {
            if (tree.ClusterNodeForSlot(item.X) is not null) continue;
            if (tree.JewelNodeForSlot(item.X) is not ushort socket || !allocated.Contains(socket)) continue;
            if (ImpossibleEscapeCatalogue.Read(item.Item, mods) is { } escape)
            {
                if (ImpossibleEscapeCatalogue.ForStoredValue(escape.Value) is { GraphId: var keystone })
                    foreach (ushort node in tree.NodesInRadius(keystone, escape.Radius))
                        if (node != keystone && tree.Find(node) is { IsAscendancy: false, IsStart: false,
                            IsMastery: false, IsProxy: false, IsExpansion: false })
                        {
                            if (!result.TryGetValue(node, out var providers)) result[node] = providers = [];
                            providers.Add(socket);
                        }
                continue;
            }
            if (RadiusJewelCatalogue.ReadConversion(item.Item, mods) is not { AllocateDisconnected: true } conversion)
                continue;
            var (inner, outer) = RadiusJewelCatalogue.RingRadii(conversion.Ring);
            var nodes = outer > 0
                ? tree.NodesInRing(socket, inner, outer)
                : tree.NodesInRadius(socket, conversion.Radius);
            foreach (ushort node in nodes)
                if (tree.Find(node) is { IsAscendancy: false, IsStart: false, IsMastery: false,
                    IsProxy: false, IsExpansion: false } passive
                    && RadiusJewelCatalogue.BypassIncludes(conversion, passive))
                {
                    if (!result.TryGetValue(node, out var providers)) result[node] = providers = [];
                    providers.Add(socket);
                }
        }
        return result;
    }

    // A batch may list its leaf before its prerequisite. Removing the last allocation alone could orphan that leaf.
    // Keep the historical last-first preference, but only remove a node whose remaining tree is still connected.
    public static void TrimOrdinaryPassives(Player player, List<ushort> nodes, int points,
        AscendancyProgress? ascendancy = null)
    {
        ascendancy ??= player.Character.MainAscendancy;
        ValidateOrdinaryPassives(player, nodes, ascendancy);
        var items = ItemsOf(player);
        // Prefer leaves that do not hold a jewel; an emptied socket is a normal allocation choice, while taking a
        // socketed jewel away would need an item move. If only jewel sockets remain, the next pass still removes one
        // (NormalizePassiveJewels then returns the jewel to the inventory).
        var jeweled = nodes.Where(n => PassivesFor(player).JewelSlotForNode(n) is int slot
            && items.Items(PlayerInventories.PassiveJewels).Any(i => i.X == slot)).ToHashSet();
        while (nodes.Count > points + PassivesFor(player).PointsGranted(nodes))
        {
            int removable = -1;
            for (int pass = 0; pass < 2 && removable < 0; pass++)
                for (int i = nodes.Count - 1; i >= 0; i--)
                {
                    if (pass == 0 && jeweled.Contains(nodes[i])) continue;
                    try
                    {
                        ValidateOrdinaryPassives(player, nodes.Where((_, index) => index != i), ascendancy);
                        removable = i;
                        break;
                    }
                    catch (AscendancyRuleException) { }
                }
            if (removable < 0) throw new AscendancyRuleException("Ordinary passives cannot be reduced to the level's point budget");
            nodes.RemoveAt(removable);
        }
    }

    // Chooses the main ascendancy (/ascendancy, the Ascendancy Chamber's class selector) and saves it; the caller reloads
    // the area to show its tree. An AscendancyRuleException leaves everything as it was.
    public static void SelectMainAscendancy(Player player, AscendancyCatalog.Definition chosen)
    {
        byte baseClass = (byte)Skills.ClassFor(player.Character.ClassRow).IntegerId;
        AscendancyClientCompatibility.RequirePlayable(player, chosen.Identity);
        {
            using var availability = AscendancyPolicies.Enter(player, chosen.Identity);
            var next = Saves.Atomically(() =>
            {
                var saved = CharacterSaves.Load(CharacterName(player));
                return CharacterSaves.ChangeMainAscendancy(CharacterName(player), latest =>
                {
                    var candidate = AscendancyRules.Select(baseClass, latest, chosen.Identity);
                    ValidateOrdinaryPassives(player, saved?.Passives ?? [], candidate);
                    return candidate;
                });
            });
            player.Character.MainAscendancy = next;
            AscendancyPolicies.SelectionChanged(player);
            player.Character.SavedPassiveProgress = new(next, player.Character.Passives, player.Character.MasteryEffects, player.Character.RefundPoints);
        }
    }

    // Earns the Labyrinth tiers' ascendancy points (two each, once: AscendancyRules.AwardKeys), saved, and sends the tree.
    public static async Task<int> AwardAscendancyPoints(Player player,
        IEnumerable<string> awards)
    {
        byte baseClass = (byte)Skills.ClassFor(player.Character.ClassRow).IntegerId;
        AscendancyProgress next;
        {
            using var availability = AscendancyPolicies.Enter(player, player.Character.MainAscendancy.Selection);
            next = CharacterSaves.ChangeMainAscendancy(CharacterName(player), latest =>
            {
                AscendancyPolicies.RequireCurrentSelection(player.Character.MainAscendancy.Selection, latest.Selection);
                foreach (string award in awards) latest = AscendancyRules.Award(baseClass, latest, award);
                return latest;
            });
            player.Character.MainAscendancy = next;
            AscendancyPolicies.SelectionChanged(player);
            player.Character.SavedPassiveProgress = new(next, player.Character.Passives, player.Character.MasteryEffects, player.Character.RefundPoints);
        }
        await SendPassives(player);
        await CheckAscendancyAwardAchievements(player, awards);
        return next.EarnedPoints;
    }

    // The second ascendancy layer (Descendancy.dat): Affliction's Wildwood specialisations and the 3.29 Bloodline
    // classes. Its row is 0x33's spare byte, so the client draws that row's tree beside the main ascendancy; the
    // selection saves like the main one. The client's point budget sums both layers against the earned ascendancy points.
    /// <summary>Whether the game may switch to <paramref name="row"/> now. A selector the server opened (0x7b kind 0x4c)
    /// must still be active in this area; the client may choose any row it shows, so the row itself is checked for its
    /// unlock flag and for a refunded layer. The GM mod's /descendancy bypasses this through
    /// <see cref="SelectDescendancyForDevelopment"/>.
    /// </summary>
    public static bool CanSelectDescendancy(Player player, byte row, out string? reason)
    {
        var definition = Descendancies.Rows.FirstOrDefault(r => r.Index == row);
        if (row == 0 || definition is null) { reason = $"no Descendancy row {row}"; return false; }
        if (player.Character.OfferedDescendancyRow == 0 || player.Character.OfferedDescendancyArea != player.Layout.Hash)
        {
            reason = "No Descendancy selector is open in this area";
            return false;
        }
        if (row != player.Character.DescendancyRow && player.Character.DescendancyNodes.Count > 0)
        {
            reason = "Refund the current Descendancy's points before choosing another";
            return false;
        }
        if (DescendancyCatalog.UnlockFlag(row) is { } flag && !player.Character.HasQuestFlag(flag))
        {
            reason = $"{definition.Name} is locked";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>Chooses the second ascendancy layer after an offer from the server; the caller reloads the area.</summary>
    public static void SelectDescendancy(Player player, byte row)
    {
        if (!CanSelectDescendancy(player, row, out string? reason)) throw new AscendancyRuleException(reason!);
        ApplyDescendancy(player, row);
    }

    /// <summary>The GM mod's unrestricted selection: any existing row, without an offer or its unlock flag, and a switch
    /// clears the old layer's nodes. Never used by a client packet.</summary>
    public static void SelectDescendancyForDevelopment(Player player, byte row)
    {
        if (row != 0 && Descendancies.Rows.All(r => r.Index != row))
            throw new ArgumentOutOfRangeException(nameof(row), $"no Descendancy row {row}");
        ApplyDescendancy(player, row);
    }

    // Starts are implicit for both layers; an explicit start node would count as an allocated node and cost a point.
    // The caller holds conn.Monsters: a switch saves the row and the cleared nodes before publishing them.
    static void ApplyDescendancy(Player player, byte row)
    {
        if (row == player.Character.DescendancyRow) return;
        if (CharacterName(player) is { Length: > 0 } character)
            CharacterSaves.SetDescendancy(character, row, expectedRow: player.Character.DescendancyRow, expectedNodes: player.Character.DescendancyNodes);
        player.Character.DescendancyRow = row;
        player.Character.DescendancyNodes.Clear();
    }

    /// <summary>Offers a Descendancy row to the client (S→C 0x7b kind 0x4c, DescendancyClassSelectorWindow) and records
    /// it as the only row C→S 0x3a may accept in this area.</summary>
    public static async Task OpenDescendancySelector(Player player, byte row)
    {
        var definition = Descendancies.Rows.FirstOrDefault(r => r.Index == row)
            ?? throw new ArgumentOutOfRangeException(nameof(row), $"no Descendancy row {row}");
        if (row == 0) throw new ArgumentOutOfRangeException(nameof(row), "no Descendancy row to offer");
        player.Character.OfferedDescendancyRow = row;
        player.Character.OfferedDescendancyArea = player.Layout.Hash;
        await Send(player, InstanceWaypoints.OpenUi(InstanceWaypoints.DescendancySelectUi, row),
            $"0x007b kind 0x4c row {row} ({definition.Name})");
    }

    // C→S 0x3a: the Descendancy class selector (S→C 0x7b kind 0x4c, DescendancyClassSelectorWindow) accepted a
    // Descendancy.dat row. The second ascendancy layer: selects it, saves, and reloads so the client builds its tree.
    internal static async Task ChooseDescendancy(Player player, byte row)
    {
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x003a descendancy row {row} chosen");
        if (!CanSelectDescendancy(player, row, out string? reason))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} 0x003a descendancy row {row} refused: {reason}");
            return;
        }
        try { SelectDescendancy(player, row); }
        catch (Exception ex) when (ex is AscendancyRuleException or PassiveProgressConflictException)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} descendancy row {row} refused: {ex.Message}");
            return;
        }
        player.Character.OfferedDescendancyRow = 0;
        await ChangeArea(player, AscendancyReload(player), $"descendancy row {row} chosen");
    }

    // C→S 0x39: the Ascendancy Chamber's class selector (opened by its font, InstanceWaypoints.AscendancySelectUi) chose an
    // Ascendancy.dat row. Only in a Labyrinth's last room, for an unascended character; the area reloads with the new class.
    public static async Task ChooseAscendancyClass(Player player, byte row)
    {
        byte baseClass = (byte)Skills.ClassFor(player.Character.ClassRow).IntegerId;
        var chosen = Ascendancies.Choices(baseClass).FirstOrDefault(d => d.Identity.Family == AscendancyFamily.Normal && d.TableRow == row);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0039 ascendancy row {row} chosen: {chosen?.Name ?? "not one of this class's"}");
        if (chosen is null || !LabyrinthDay.IsChamber(player.Layout.Id) || player.Character.MainAscendancy.Selection is not null) return;
        try { SelectMainAscendancy(player, chosen); }
        catch (Exception ex) when (ex is AscendancyRuleException or PassiveProgressConflictException)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} {chosen.Name} refused: {ex.Message}");
            return;
        }
        await AwardAchievementItem(player, "ChooseAscendancyClass");
        await ChangeArea(player, AscendancyReload(player), $"ascendancy {chosen.Name} chosen at the font");
    }

    public static PendingTransfer AscendancyReload(Player player)
    {
        return new(player.Layout.Hash, player.InstanceNumber, false, ArriveAt: player.Walk.Position(AreaTime(player)));
    }

    // The character's passive skill tree (0x33): its level (from which the client counts the points), its class (the
    // tree's start) and the allocated nodes.
    public static async Task SendPassives(Player player)
    {
        if (QuestKillCommitOutput is { } output)
        {
            output.Add(new([], "current quest reward tree", Kind: PlayerMinions.OutputKind.Passives));
            return;
        }
        // A queued publication must not restore a tree captured before a later allocation/refund or level change.
        await Send(player, [], "0x0033 current passive skill tree",
            atGate: () => PassivesMessage(player).Bytes);
    }

    /// <summary>
    /// Allocates or refunds one cluster jewel node: the client's own expansion id, sent with the expansion flag
    /// (C->S 0x34/0x36). The client's layout decides which ids it offers, so the server keeps the set and answers with
    /// the tree, which carries them in its third list. Any socketed cluster jewel has such nodes (a large jewel in a
    /// tree socket, a medium one in a large tree socket or in a large jewel's socket, and so on), and their ids are the
    /// client's layout indices (202, 206 live), never a PassiveSkillGraphId.
    /// </summary>
    public static async Task ChangeClusterPassive(Player player,
        ushort node, bool allocate)
    {
        bool changed = false;
        string? refusal = null;
        if (!ClusterJewelSocketed(player)) refusal = "no cluster jewel socketed";
        else if (node > MaxClusterNodeId) refusal = "not a cluster node id";
        else
        {
            bool willChange = allocate ? !player.Character.ClusterPassives.Contains(node) : player.Character.ClusterPassives.Contains(node);
            if (allocate && willChange)
            {
                var (points, _, nodes, _) = PassiveState(player);
                int unspent = points + PassivesFor(player).PointsGranted(nodes) + AscendancyOrdinaryPoints(player)
                    - nodes.Count - player.Character.ClusterPassives.Count;
                if (unspent <= 0) refusal = "no passive skill points remaining";
                else if (!ClusterNodeExists(player, node)) refusal = "node absent from socketed cluster jewels";
                else if (player.Character.ClusterPassives.Count >= ClusterNodeBudget(player)) refusal = "cluster jewel passive budget reached";
            }
            if (refusal is null)
            {
                IReadOnlyList<PlayerSkillSource> sourcesBefore = willChange
                    ? CaptureGemSourceRowsLocked(player) : [];
                changed = allocate ? player.Character.ClusterPassives.Add(node) : player.Character.ClusterPassives.Remove(node);
                if (changed)
                {
                    Saves.Atomically(() =>
                    {
                        CharacterSaves.SetClusterPassives(CharacterName(player), player.Character.ClusterPassives);
                        if (allocate && HasPassiveFreeReset(player)) SetPassiveFreeReset(player, false);
                    });
                    CompletePassiveJewelMutationLocked(player, sourcesBefore);
                }
            }
        }
        if (refusal is not null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} cluster jewel node {node} {(allocate ? "allocation" : "refund")} refused: {refusal}");
            await SendPassives(player);
            return;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} cluster jewel node {node} {(allocate ? "allocated" : "refunded")}" +
            $"{(changed ? "" : " (no change)")}, {player.Character.ClusterPassives.Count} cluster node(s)");
        await SendPassives(player);
        if (!changed) return;
        // A cluster's passives give their stats only when the whole cluster is allocated (ClusterJewelStats), so the
        // node that completes it must refresh the character's stats as well as the tree.
        await SendPassiveJewelStats(player);
    }

    public static bool ClusterNodeExists(Player player, ushort node)
    {
        foreach (var jewel in ItemsOf(player).Items(PlayerInventories.PassiveJewels))
        {
            if (ClusterSize(jewel.Item) is null) continue;
            if (Passives.JewelNodeForSlot(jewel.X) is null && !ClusterSlotOpen(player, jewel.X)) continue;
            // An unmeasured layout still uses the existing point and jewel budget checks.
            if (ClusterJewelLayout.For(jewel.X, jewel.Item) is not { } layout
                || layout.ByOffset.ContainsKey(node - layout.Base)) return true;
        }
        return false;
    }

    /// <summary>The largest cluster jewel node id the client sends with the expansion flag: its own layout index, well
    /// below the PassiveSkillGraphId range of the tree's nodes.</summary>
    public const ushort MaxClusterNodeId = 0xFFF;

    /// <summary>Module data key for an unused one-off free full passive skill tree reset (S→C 0x33 flags bit 0, C→S 0x38).</summary>
    public const string PassiveFreeResetKey = "passives.free_reset";

    public static bool HasPassiveFreeReset(Player player) =>
        CharacterSaves.ModuleData(CharacterName(player), PassiveFreeResetKey) == "1";

    public static void SetPassiveFreeReset(Player player, bool available) =>
        CharacterSaves.ChangeModuleData(CharacterName(player), PassiveFreeResetKey, _ => available ? "1" : "");

    /// <summary>C→S 0x38: consumes an unused one-off free passive tree reset to refund all ordinary passives free.</summary>
    public static async Task ResetPassivesFree(Player player)
    {
        if (!HasPassiveFreeReset(player))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0038 free passive reset refused: none available");
            await SendPassives(player);
            return;
        }
        var nodes = PassiveState(player).Nodes.Where(n => PassivesFor(player).Find(n) is not { IsAscendancy: true }).ToList();
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0038 free passive reset ({nodes.Count})");
        if (nodes.Count == 0)
        {
            await SendPassives(player);
            return;
        }
        string? error = await ChangePassives(player, nodes, allocate: false, "Free passive reset",
            consumeFreeReset: true);
        if (error is not null && player.Commands is { } commands) await commands.Reply(error);
    }

    public static (byte[] Bytes, string Label) PassivesMessage(Player player)
    {
        var (points, refundPoints, nodes, masteries) = PassiveState(player);
        // The client derives one point per level from the level byte. Any points beyond that (the dev extra
        // points and the part of the configured per-level value above one) must travel as questPoints.
        ushort clientExtra = (ushort)Math.Min(ushort.MaxValue,
            Math.Max(0, points - Math.Max(0, player.Pools.Level - 1)));
        int cluster = player.Character.ClusterPassives.Count;
        int unspent = points + PassivesFor(player).PointsGranted(nodes) + AscendancyOrdinaryPoints(player) - nodes.Count - cluster;
        var tattoos = player.Character.Tattoos
            .Select(kv =>
            {
                var entry = TattooCatalogue.Find(kv.Value);
                ushort hash = (ushort)(entry?.OverrideHash ?? 0);
                if (kv.Value == "Journey Tattoo of Makanui"
                    && player.Character.TattooKeystones.TryGetValue(kv.Key, out ushort chosen)
                    && JourneyMakanuiCatalogue.FindByGraphId(chosen) is { } keystone)
                    hash = keystone.OverrideHash;
                // A Forbidden tattoo sends the granted ascendancy notable's own HASH16 (its PassiveSkillGraphId),
                // the same value the client's random override row carries.
                else if (ForbiddenTattoo.IsForbidden(kv.Value) && player.Character.TattooKeystones.TryGetValue(kv.Key, out ushort notable))
                    hash = notable;
                return (Node: kv.Key, Hash: hash, Entry: entry);
            })
            .Where(pair => pair.Entry is not null)
            .ToDictionary(pair => pair.Node, pair => pair.Hash);
        int descendancyPoints = DescendancyCatalog.BonusPoints(player.Character.DescendancyRow);
        return (InstanceEntities.CharacterPassives((byte)player.Pools.Level, PackedClass(player), [.. nodes, .. player.Character.MainAscendancy.Allocated, .. player.Character.DescendancyNodes],
                questPoints: clientExtra, refundPoints: refundPoints,
                ascendancyPoints: (ushort)(player.Character.MainAscendancy.EarnedPoints + descendancyPoints), masteryEffects: masteries,
                clusterNodes: [.. player.Character.ClusterPassives], tattoos: tattoos, descendancy: player.Character.DescendancyRow,
                freeReset: HasPassiveFreeReset(player)),
            $"0x0033 passive skill tree: level {player.Pools.Level}, {points} skill point(s) ({clientExtra} extra), " +
            $"{refundPoints} refund point(s), {nodes.Count} allocated, {cluster} cluster, {unspent} unspent; " +
            $"ascendancy {player.Character.MainAscendancy.Selection?.Family}/{player.Character.MainAscendancy.Selection?.Id}, {player.Character.MainAscendancy.EarnedPoints} earned, {player.Character.MainAscendancy.Allocated.Count} nodes" +
            (descendancyPoints > 0 ? $" + {descendancyPoints} descendancy points (row {player.Character.DescendancyRow})" : ""));
    }

    // The native family selector is a world stat, independently of the character's stored league.
    public static IReadOnlyDictionary<uint, int>? LeagueStatsFor(Player player) =>
        AscendancyPolicies.AreaStats(player, player.Character.MainAscendancy.Selection);

    public static int AscendancyOrdinaryPoints(Player player) =>
        Game.AscendancyRules.OrdinaryPointsGranted((byte)SkillsFor(player).ClassFor(player.Character.ClassRow).IntegerId, player.Character.MainAscendancy);

    public static byte PackedClass(Player player)
    {
        byte baseClass = (byte)SkillsFor(player).ClassFor(player.Character.ClassRow).IntegerId;
        byte number = Game.AscendancyRules.Validate(baseClass, player.Character.MainAscendancy)?.ClassNo ?? 0;
        return CharacterClassByte.Encode(baseClass, number);
    }

    // Keep the signature used by mods built against 0.6.0-beta.
    public static Task<string?> ChangePassives(Player player, IReadOnlyList<ushort> nodes, bool allocate, string why,
        ushort? masteryEffect, bool goldRespec, bool refundPoints) =>
        ChangePassives(player, nodes, allocate, why, masteryEffect, goldRespec, refundPoints, consumeFreeReset: false);

    // Allocates (or, with allocate false, refunds) passive nodes: checked against the tree's nodes and the character's
    // points and complete ordinary/ascendancy connectivity, then saved, applied to the stats and sent back (0x33).
    // A gold respec (goldRespec) refunds only allocated nodes and charges their cost (PassiveTree.GoldRespecCost); a
    // refund-point refund (refundPoints) spends one saved point per node. /reset_passives sets neither.
    public static async Task<string?> ChangePassives(Player player,
        IReadOnlyList<ushort> nodes, bool allocate, string why, ushort? masteryEffect = null, bool goldRespec = false,
        bool refundPoints = false, bool consumeFreeReset = false)
    {
        string? error = null;
        List<ushort> now;
        Dictionary<ushort, ushort> effectsNow;
        string? effectLabel = null;
        long cost = 0;
        uint gold = 0;
        ushort refundsNow = 0;
        int refundsSpent = 0;
        AscendancyProgress ascendancyNow;
        CharacterSaves.PassiveProgressSnapshot? expectedProgress;
        AscendancyIdentity? selectionBefore = null;
        IReadOnlyList<PlayerSkillSource> sourcesBefore = [];
        // Validate, save and publish under one connection lock; level changes cannot split these phases.
        // All network sends remain outside it (connection lock -> database, never connection lock -> cipher gate).
        {
            using var availability = AscendancyPolicies.Enter(player, player.Character.MainAscendancy.Selection);
            var next = new List<ushort>(player.Character.Passives);
            var effects = new Dictionary<ushort, ushort>(player.Character.MasteryEffects);
            byte baseClass = (byte)SkillsFor(player).ClassFor(player.Character.ClassRow).IntegerId;
            var ascendancy = player.Character.MainAscendancy;
            expectedProgress = player.Character.SavedPassiveProgress;
            var ascendancyNodes = new List<ushort>();
            var descendancyChanges = new List<ushort>();
            foreach (var n in nodes)
            {
                if (PassivesFor(player).Find(n) is not { } node) { error = $"unknown passive {n}"; break; }
                if (node.IsAscendancy)
                {
                    if (goldRespec || !allocate && !refundPoints) { error = "Ascendancy passives require refund points, not gold or a free reset"; break; }
                    if (masteryEffect is not null) { error = "An ascendancy passive is not an ordinary mastery"; break; }
                    if (node.DescendancyKey > 0) { descendancyChanges.Add(n); continue; }
                    ascendancyNodes.Add(n);
                    continue;
                }
                if (masteryEffect is { } raw)
                {
                    if (!allocate) { error = "a mastery effect cannot be refunded on its own"; break; }
                    var effect = PassivesFor(player).EffectsFor(n).FirstOrDefault(e => e.Hash == raw || e.Row == raw);
                    if (effect is null) { error = $"{node.Name} ({n}) has no mastery effect {raw}"; break; }
                    if (!next.Contains(n)) next.Add(n);
                    effects[n] = effect.Hash;
                    effectLabel = $"{effect.Id} ({effect.Hash})";
                }
                else if (allocate) { if (!next.Contains(n)) next.Add(n); }
                else if ((goldRespec || refundPoints) && !next.Contains(n)) { error = $"{node.Name} ({n}) is not allocated"; break; }
                else
                {
                    next.Remove(n);
                    effects.Remove(n);
                    if (refundPoints) refundsSpent++;
                }
            }
            int ordinaryBudget = Progression.PassivePoints(player.Pools.Level) + player.Character.ExtraPassivePoints + QuestPassivePoints(player) + PassivesFor(player).PointsGranted(next);
            if (error is null && ascendancyNodes.Count > 0)
            {
                try
                {
                    if (allocate)
                        ascendancy = Game.AscendancyRules.Allocate(baseClass, ascendancy, ascendancyNodes);
                    else
                    {
                        var result = Game.AscendancyRules.Refund(baseClass, ascendancy, ascendancyNodes, next.Count, ordinaryBudget);
                        ascendancy = result.Progress;
                        if (refundPoints) refundsSpent += result.Cost;
                    }
                }
                catch (AscendancyRuleException e) { error = e.Message; }
            }
            if (error is null)
            {
                try { ValidateOrdinaryPassives(player, next, ascendancy); }
                catch (AscendancyRuleException e) { error = e.Message; }
            }
            // The second ascendancy layer shares the same earned points; its nodes count against them.
            var descendancyNext = new HashSet<ushort>(player.Character.DescendancyNodes);
            if (error is null && descendancyChanges.Count > 0)
            {
                foreach (ushort n in descendancyChanges)
                {
                    if (allocate) descendancyNext.Add(n);
                    else if (!descendancyNext.Remove(n)) { error = $"descendancy passive {n} is not allocated"; break; }
                }
                if (error is null)
                {
                    try
                    {
                        int mainSpent = Game.AscendancyRules.SpentPoints(baseClass, ascendancy);
                        Game.Descendancies.Validate(player.Character.DescendancyRow, descendancyNext, mainSpent, ascendancy.EarnedPoints,
                            DescendancyCatalog.BonusPoints(player.Character.DescendancyRow));
                        if (refundPoints && !allocate)
                            refundsSpent += descendancyChanges.Count * AscendancyRules.RefundPointsPerAscendancyPoint;
                    }
                    catch (AscendancyRuleException e) { error = e.Message; }
                }
            }
            // A socketed jewel would be left behind by a refund: the client has no automatic return, so it must be taken
            // out (into the cursor or the inventory) first.
            if (error is null && !allocate
                && PassiveJewelRemovalRefusal(player, player.Character.Passives.Where(n => !next.Contains(n))) is { } jewelError)
                error = jewelError;
            if (error is null && next.Count + player.Character.ClusterPassives.Count > ordinaryBudget + Game.AscendancyRules.OrdinaryPointsGranted(baseClass, ascendancy))
                error = $"not enough passive skill points ({Progression.PassivePoints(player.Pools.Level) + player.Character.ExtraPassivePoints + QuestPassivePoints(player)} at level {player.Pools.Level}, {next.Count} wanted)";
            if (error is null && refundPoints && refundsSpent > player.Character.RefundPoints)
                error = $"not enough refund points ({refundsSpent} needed, {player.Character.RefundPoints} held)";
            if (goldRespec) cost = PassivesFor(player).GoldRespecCost(nodes, player.Pools.Level);
            now = next;
            ascendancyNow = ascendancy;
            effectsNow = effects;
            refundsNow = (ushort)(player.Character.RefundPoints - (refundPoints && error is null ? refundsSpent : 0));
            if (error is null)
            {
                selectionBefore = player.Character.MainAscendancy.Selection;
                sourcesBefore = CaptureGemSourceRowsLocked(player);
            }
            // Saved before it applies, in one transaction: the gold (paid last, from the account's gold: nothing is taken when the
            // change is refused), the passives, their mastery effects and the refund points.
            if (error is null)
                error = Saves.Atomically(() =>
                {
                    if (goldRespec)
                    {
                        if (PersonalStashOf(player).TrySpendGold(cost) is not uint left) return $"not enough gold ({cost} needed, {PersonalStashOf(player).Gold} held)";
                        gold = left;
                    }
                    CharacterSaves.SetMainAscendancy(CharacterName(player), ascendancyNow, now, effectsNow, refundPoints ? refundsNow : null,
                        expected: expectedProgress);
                    if (player.Character.DescendancyRow != 0 || descendancyNext.Count > 0)
                        CharacterSaves.SetDescendancy(CharacterName(player), player.Character.DescendancyRow, descendancyNext,
                            expectedRow: player.Character.DescendancyRow, expectedNodes: player.Character.DescendancyNodes);
                    if ((consumeFreeReset || allocate) && HasPassiveFreeReset(player))
                        SetPassiveFreeReset(player, false);
                    return null;
                });
            if (error is null)
            {
                player.Character.Passives = now;
                player.Character.MainAscendancy = ascendancyNow;
                player.Character.DescendancyNodes = descendancyNext;
                AscendancyPolicies.SelectionChanged(player);
                player.Character.MasteryEffects = effectsNow;
                player.Character.RefundPoints = refundsNow;
                player.Character.SavedPassiveProgress = new(player.Character.MainAscendancy, now, effectsNow, refundsNow);
                // Also reconciles the ailments on the player with the new stats: a gained or refunded action-speed floor,
                // Changing Seasons reversing a running chill.
                using (new PlayerStatScope(player, fresh: true))
                    RecomputePoolsAndSpeed(player);
                // A passive can change gem qualification without changing the ascendancy provider. Reuse the actual
                // equipment-source reconciliation so invalid direct owners and their durable activation end before the
                // first output can resume. Selection changes keep their separate provider-retirement matrix.
                if (selectionBefore == player.Character.MainAscendancy.Selection)
                    ReconcileEquipmentSourceLifetimesLocked(player, sourcesBefore);
                // Retirement can remove an aura or profile that supplied the pools. Clamp from a new snapshot after it.
                using (new PlayerStatScope(player, fresh: true))
                {
                    player.PlayerLife = LifeReservation.Clamp(player.PlayerLife, LifeReservationOf(player));
                    player.PlayerMana = Math.Min(player.PlayerMana, (uint)UnreservedMana(player));
                }
            }
            now = [.. player.Character.Passives];
            effectsNow = new(player.Character.MasteryEffects);
            refundsNow = player.Character.RefundPoints;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} passives {(allocate ? "allocate" : "refund")} [{string.Join(", ", nodes.Select(n => $"{n} {PassivesFor(player).Find(n)?.Name}"))}]" +
            (effectLabel is null ? "" : $" mastery {effectLabel}") + $" ({why}): " +
            (error ?? $"{now.Count} allocated, life {player.Pools.Life}, mana {player.Pools.Mana}" +
                (goldRespec ? $", {cost} gold paid, {gold} left" : "") +
                (refundPoints ? $", {refundsSpent} refund point(s) spent, {refundsNow} left" : "")));
        if (error is null)
        {
            if (goldRespec)
            {
                await Send(player, InstanceEntities.Gold(gold), $"0x02aa gold: {gold} (respec, {cost} paid)");
            }
            await SendCharacterStats(player, PlayerStatChange.Build);
            await Send(player, [], "0x01c1/0x01c2 passive buffs (passives)", atGate: () => PassiveBuffAuras.Presentation(player));
            await SendFortification(player);
            // A refund can lower the maximum life: the current life needs its own 0x1b1 (0x1b3 only carries the maximum).
            Note($"0x01b1 player life {player.PlayerLife} (passives)");
            await SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana} (passives)");
        }
        // Sent either way: on a refusal it puts the client's tree back to the server's.
        await SendPassives(player);
        if (error is null && allocate) await CheckPassiveAchievements(player, now);
        return error;
    }

    // Experience from a kill: added (up to the last level), sent as the Player component's experience (0x2bc; the client
    // derives the level and plays the level up from it), saved, and at a new level the level's stats, life and passive
    // points from current experience and the account's level (Auth set_level, for the character list).
    public static async Task GainExperience(Player player, uint gain, string why, Func<ControlRequest, Task<ControlResponse>>? control = null)
    {
        if (RecordExperienceGain(player, gain) is { } change)
            await CompleteExperienceGain(player, change, why, control);
    }

    // A kill's experience goes to the gems too: every one socketed in an equipped item, in either weapon set, gains 10% of the
    // player's gain (support gems too, a gem in an inactive item, at its last level or over its cap nothing), up to the
    // threshold of the first level whose requirement the character does not meet â€” the gem's level stalls there, as the
    // game's does (the client would level the gem to whatever its experience says, so the experience stops just below that
    // threshold; a gem already past it, after /set_level down, keeps its level and experience and is disabled instead) and
    // the last level's threshold. Written into the gem item inside the socket list â€” the equipped
    // item's own component has no skill gems. Sent as the socket's equipment row 0x00bf item change, the same item id removed
    // and added again (the replace idiom: an addition of a known id alone is dropped as a duplicate, so the client would keep
    // the stale gem), so the client's gem XP bar and tooltip follow; the next save keeps it.
    public static async Task GainGemExperience(Player player, uint gain)
    {
        var (packets, leveled) = CommitGemExperience(player, gain);
        foreach (var packet in packets) await Send(player, packet, "gem experience");
        if (leveled) await SendSkills(player);
    }

    public static (List<byte[]> Packets, bool Leveled) CommitGemExperience(Player player, uint gain,
        List<(byte Row, uint[] Items)>? projectRows = null)
    {
        if (gain == 0 || HardcoreProgressBlocked(player)) return ([], false);
        double gemShare = LeagueFor(player).Rules.GemExperienceMultiplier ?? 1.0;
        int share = LeagueGameplay.GemExperience(gain, gemShare, ref player.Character.GemExperienceCarry);
        if (share == 0) return ([], false);
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        var skills = SkillsFor(player);
        int playerLevel = player.Pools.Level;
        int? NextExperience(uint itemId, Item gem, out bool levels)
        {
            levels = false;
            if (IsInactive(player, itemId) || catalog.ByHash(gem.TypeHash) is not { } baseType || catalog.Gem(baseType) is not { } type) return null;
            // The experience stops one point below the next level's threshold, where the client draws the "+", so a
            // kill never levels a gem: only the player's click (0x31, or Level Up All's 0x32) writes the threshold.
            // A gem already above the highest level the character meets keeps its experience and gains nothing.
            int level = type.Level(gem.GemExperience);
            if (level < 0) return null; // -1 is a corrupted gem over its cap (GemType.Level)
            int allowed = Math.Min(skills.MaxLevelAt(type.Variant(gem.GemVariant).Effect, playerLevel), type.MaxLevel);
            int cap = type.ExperienceFor(Math.Min(level + 1, allowed + 1)) - 1;
            if (gem.GemExperience < 0 || gem.GemExperience >= cap) return null;
            int nextXp = (int)Math.Min((long)gem.GemExperience + share, cap);
            levels = type.Level(nextXp) > level;
            return nextXp;
        }
        // AddExperience caps at one point below the next level's threshold, so kills almost never level a gem;
        // scan without allocating first and capture source rows outside PlayerInventories._lock only when needed.
        bool mayLevel = items.AnySocketedGem((itemId, gem) => NextExperience(itemId, gem, out bool levels) is not null && levels);
        IReadOnlyList<PlayerSkillSource> sourcesBefore = mayLevel ? CaptureGemSourceRowsLocked(player) : [];
        bool leveled = false;
        var changed = items.ChangeSocketedGems((itemId, gem) =>
        {
            if (NextExperience(itemId, gem, out bool levels) is not int nextXp) return null;
            if (levels) leveled = true;
            return gem with { GemExperience = nextXp };
        });
        if (changed.Count == 0) return ([], false);
        foreach (var (before, after) in changed.SelectMany(c => c.Gems))
        {
            int afterLevel = catalog.GemLevel(after);
            if (afterLevel > catalog.GemLevel(before))
                Console.WriteLine($"[gateway] {CharacterName(player)}'s {catalog.GemName(after)} levels to {afterLevel}");
            // A plain gain is logged only with the projectile trace: three lines a kill per gem were written under the
            // Monsters lock for every monster an explosion killed.
            else if (GameServer.Core.Diagnostics.ServerTrace.Projectiles)
                Console.WriteLine($"[gateway] {CharacterName(player)}'s {catalog.GemName(after)} gains experience " +
                    $"+{after.GemExperience - before.GemExperience} ({after.GemExperience}, level {afterLevel})");
        }
        if (leveled)
        {
            using (new PlayerStatScope(player, fresh: true))
            {
                RecomputePoolsAndSpeed(player);
                ReconcileBeastCompanionsLocked(player);
                ReconcileEquipmentSourceLifetimesLocked(player, sourcesBefore);
            }
        }
        var packets = new List<byte[]>();
        foreach (var row in changed.GroupBy(c => c.Row))
        {
            uint[] ids = [.. row.Select(c => c.Item.Id)];
            // Accepted output may wait behind a hook/refund/item move. Keep ids for a current row projection.
            if (projectRows is not null) projectRows.Add((row.Key, ids));
            else packets.Add(InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key),
                ids, [.. row.Select(c => c.Item)]));
        }
        return (packets, leveled);
    }
}
