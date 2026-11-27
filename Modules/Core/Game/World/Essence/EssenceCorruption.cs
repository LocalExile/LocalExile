using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

public static partial class MapEssences
{
    public enum CorruptionOutcome { Release, Upgrade, Add, Transform }
    public sealed record Corruption(Monolith Monolith, CorruptionOutcome Outcome);

    /// <summary>Vaal Orb's four corruption outcomes. Stability leaves only upgrade and transform.</summary>
    public static Corruption? Corrupt(Monolith monolith, int level, MapMods mods, Random random)
    {
        if (monolith.Stage >= 4 || monolith.Monster != 0 || monolith.Corrupted || monolith.Essences.Count == 0) return null;
        CorruptionOutcome[] outcomes = mods.Get(Stat.MapStableEssenceCorruption) > 0
            ? [CorruptionOutcome.Upgrade, CorruptionOutcome.Transform]
            : [CorruptionOutcome.Release, CorruptionOutcome.Upgrade, CorruptionOutcome.Add, CorruptionOutcome.Transform];
        // The outcome set is documented; equal weights are the replica's distribution (not a client-table weight).
        var outcome = outcomes[random.Next(outcomes.Length)];
        var essences = monolith.Essences.ToList();
        if (outcome == CorruptionOutcome.Upgrade)
            essences = [.. essences.Select(row => MapEssenceCatalog.Find(row) is { } e ? MapEssenceCatalog.Higher(e).Row : row)];
        else if (outcome == CorruptionOutcome.Transform)
            essences = [.. essences.Select(row => MapEssenceCatalog.Find(row) is { } e ? MapEssenceCatalog.Transform(e, random).Row : row)];
        else if (outcome == CorruptionOutcome.Add && MapEssenceCatalog.Additional(level, essences, mods, random) is { } extra)
            essences.Add(extra.Row);
        // Crystal Resonance (3.29; the 3.30 Commensurate Corruption node) replaces the whole list with one of the
        // essences the monster ALREADY had — an original essence, not a transformed one, keeping the original count.
        // That is why it disables the transform-to-corrupted outcome (the farming guides recommend avoiding the node
        // when after corrupted essences): pick from monolith.Essences, before the outcome's upgrade/transform/add, so a
        // group-E essence transformed to the corrupted group F cannot become the one everything is replaced with.
        if (mods.Get(Stat.MapEssenceRemnantConvert) > 0)
            essences = Enumerable.Repeat(monolith.Essences[random.Next(monolith.Essences.Count)], monolith.Essences.Count).ToList();
        return new(monolith with { Corrupted = true, ForcedRelease = outcome == CorruptionOutcome.Release
            && mods.Get(Stat.MapEssenceCorruptionCannotReleaseMonsters) <= 0, Essences = essences }, outcome);
    }

    /// <summary>The verified, shared item-on-world-object packet; a box handler declines these targets.</summary>
    public static Task<bool> UseItemOn(Player player, byte[] message) =>
        message.Length != InstanceItems.UseItemOnObjectLength
            ? Task.FromResult(false)
            : UseItemOnCore(player, InstanceItems.ReadUseItemOnObject(message));

    static readonly byte[] CurrencyRows = [PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory];

    /// <summary>GameEvents.Packet(0x79): Shift-click / repeat Vaal Orb use by type hash on an essence monolith.</summary>
    public static Task<bool> UseItemTypeOn(Player player, byte[] message)
    {
        if (message.Length < InstanceItems.UseItemTypeOnObjectLength) return Task.FromResult(false);
        var req = InstanceItems.ReadUseItemTypeOnObject(message);
        if (player.Instance is null || !Areas.TryGetValue(player.Instance, out var area)
            || area.Monoliths?.FirstOrDefault(m => m.Id == req.TargetObjectId) is null)
            return Task.FromResult(false);
        var items = ItemsOf(player);
        foreach (byte row in CurrencyRows)
            if (items.Items(row).FirstOrDefault(i => i.Item.TypeHash == req.TypeHash && items.RowOf(i.Id) == row) is { } stack)
                return UseItemOnCore(player, new(PlayerInventories.InventoryId(row), stack.Id, req.TargetObjectId));
        Console.WriteLine($"[map-content] essence 0x{req.TargetObjectId:x}: 0x0079 type 0x{req.TypeHash:x8} not found in inventory");
        return Task.FromResult(true);
    }

    static async Task<bool> UseItemOnCore(Player player, InstanceItems.UseItemOnObject use)
    {
        if (player.Instance is null || !Areas.TryGetValue(player.Instance, out var area)
            || area.Monoliths?.FirstOrDefault(m => m.Id == use.TargetObjectId) is not { } target) return false;
        string Log(string text) => $"[map-content] essence 0x{use.TargetObjectId:x} <= 0x0074 item {use.ItemId} (inventory {use.InventoryId}): {text}";
        var items = ItemsOf(player);
        await area.Gate.WaitAsync();
        try
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0
                || !PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), (target.X, target.Y), 20)) return true;
            // The orb may sit in a character inventory, the cursor or the open stash / currency tab: the client sends 0x74
            // with that inventory's id either way (live: a currency-tab orb clicked on a box). Resolve the source.
            byte row = 0; bool fromChar = false; InventoryItem? used = null;
            if (items.RowOf(use.ItemId) is byte r && PlayerInventories.InventoryId(r) == use.InventoryId
                && items.Items(r).FirstOrDefault(i => i.Id == use.ItemId) is { } ci) { row = r; fromChar = true; used = ci; }
            else if (PlayerInventories.InventoryId(PlayerInventories.Cursor) == use.InventoryId
                && items.Items(PlayerInventories.Cursor).FirstOrDefault(i => i.Id == use.ItemId) is { } hi)
            { row = PlayerInventories.Cursor; fromChar = true; used = hi; }
            AccountStash? stash = null; int stashTab = -1;
            if (used is null && StashOf(player) is { IsTrade: false } s && s.TabOf(use.InventoryId) is int t && s.IsShownTab(t)
                && s.Inventory(t).Items.FirstOrDefault(i => i.Id == use.ItemId) is { } si) { stash = s; stashTab = t; used = si; }
            if (used is null || ItemsFor(player).ByHash(used.Item.TypeHash)?.Id != CurrencyEffects.VaalOrb)
            {
                Console.WriteLine(Log("requires a Vaal Orb in the inventory, the cursor or the open stash; nothing consumed"));
                return true;
            }
            var monolith = area.Monoliths![target.Index];
            int level = Math.Clamp(MapRunOf(player)?.Area.Level ?? ServerLevels.AreaLevel(player.Layout.Id), 1, 100);
            var result = Corrupt(monolith, level, MapModsOf(player) ?? MapMods.None, AreaRng(player));
            if (result is null)
            {
                Console.WriteLine(Log("already corrupted or released; nothing consumed"));
                return true;
            }
            // Consume one orb from whichever source holds it; only then commit the monolith.
            Func<Task> notify;
            if (fromChar)
            {
                if (!CurrentGameWork || items.ConsumeOne(row, used.Id) is not { } taken) return true;
                uint inventory = PlayerInventories.InventoryId(row);
                notify = () => Send(player, InstanceItems.InventoryChange(inventory, items.NextRevision(row), [used.Id], taken.Left is { } left ? [left] : []),
                    $"0x00bf inventory {inventory}: Vaal Orb used on essence monolith 0x{monolith.Id:x}");
            }
            else
            {
                // The clicked stack, not the smallest stack of its type anywhere in the stash.
                if (!CurrentGameWork || stash!.PayOne(stashTab, used, allowPublic: true) is not { } paid) return true;
                notify = () => SendStashPayment(player, stash!, paid);
            }
            monolith = result.Monolith;
            area.Monoliths[monolith.Index] = monolith;
            area.Dirty.Add(monolith.Index);
            Keep(player, area); // commit shared state before any output or another player's request
            await notify();
            Console.WriteLine(Log($"corrupted outcome={result.Outcome} essences=[{string.Join(',', monolith.Essences)}]"));
            // Show the corrupted essences first, so a forced release breaks the new list rather than recreating after it.
            await PresentDirty(player, area);
            if (monolith.ForcedRelease) await Release(player, area, monolith);
        }
        finally { area.Gate.Release(); }
        return true;
    }

    static async Task Recreate(Player player, Area area, Monolith monolith)
    {
        var members = Members(player);
        bool had = area.Shown.Remove(monolith.Id);
        foreach (var member in members)
            if (Presented.TryGetValue(member, out var shown)) had |= shown.Remove(monolith.Id);
        if (had) await SendWorldRemoval(player, monolith.Id, null);
        foreach (var member in members) await Present(member, monolith);
    }

    // The area gate owns release and its exact reward identity. A paid forced release survives a connection leaving.
    static async Task<bool> Release(Player player, Area area, Monolith monolith)
    {
        if (monolith.Stage >= 4 || monolith.Monster != 0 || Monsters.Find(monolith.Variety) is not { Supported: true } variety) return false;
        byte level = (byte)AreaLevel(player);
        var mods = MapEssenceCatalog.Mods(player, variety, level, monolith.Essences);
        if (monolith.ForcedRelease && ItemsFor(player).Mods.ById("RemnantOfCorruptionMonsterStats") is { } empowered)
            mods = MonsterModRolls.Create([.. mods.Mods, empowered.Roll(ModSection.Implicit, AreaRng(player))], mods.NameWords, MonsterRarity.Rare, level);
        // Only the rare casts the essences' spells: they go into its kit as specials with the skill's cooldown.
        var essenceKit = EssenceKit(player, variety, level, monolith.Essences, mods);
        RemoveEncasedPreviews(player, monolith);
        // Established: the rare emerges from the crystal's own cell. The monolith's blocker is still registered until
        // the break is presented, so the proposed placement's nearest-clear search would spawn it offset from the crystal.
        uint monster = await SpawnMonster(player, variety, level, MonsterRarity.Rare,
            monolith.X, monolith.Y, mods: mods, unrisen: false, alone: false, extraKit: essenceKit,
            placement: MonsterPlacement.Established, owned: (id, _) =>
            {
                // Its summons are kept by each summon spell's own limit (MonsterSummon's Summon.Limit): the new API's
                // summoner cap replaces the feature branch's SpawnedMonster.SummonCap.
                area.Monoliths![monolith.Index] = monolith with { Stage = 4, Monster = id, ForcedRelease = false };
                area.Dirty.Add(monolith.Index);
                ApplyReleaseEffects(player, area, monolith.Index);
                Keep(player, area);
            });
        if (monster == 0) return false;
        // The pack's magic monsters release with the rare: each mini crystal breaks and spawns its monster. Essence
        // modifiers can only appear on rare or unique monsters (the wiki), so the minis are ordinary magic monsters of
        // the pack — no essence mods; the essence reward still drops only from the rare, through Drops.
        var current = area.Monoliths![monolith.Index];
        var minis = current.Minis.ToList();
        for (int i = 0; i < minis.Count; i++)
        {
            var mini = minis[i];
            if (mini.Stage >= 2 || mini.Monster != 0 || Monsters.Find(mini.Variety) is not { Supported: true } mv) continue;
            uint mid = await SpawnMonster(player, mv, level, MonsterRarity.Magic,
                mini.X, mini.Y, unrisen: false, alone: false, placement: MonsterPlacement.Established);
            if (mid != 0) minis[i] = mini with { Stage = 2, Monster = mid };
        }
        area.Monoliths![monolith.Index] = current with { Minis = minis };
        Keep(player, area);
        var pending = MapEssenceCatalog.PendingEffects(monolith.Essences);
        if (pending.Count > 0)
            Console.WriteLine($"[map-content] essence source=0x{monolith.Id:x} monster=0x{monster:x}: unsupported behaviours=[{string.Join(',', pending)}]; ordinary stats and essence rewards remain active");
        foreach (var member in Members(player)) await Present(member, area.Monoliths![monolith.Index], true);
        // The transition showed the release; recreating the object would cut its break short. Kept dirty only if a send failed.
        area.Dirty.Remove(monolith.Index);
        await PresentDirty(player, area);
        return true;
    }

    /// <summary>A transferred essence upgrades an existing type, or adds a type the target does not have.</summary>
    public static Monolith? Inherit(Monolith target, int row)
    {
        if (target.Stage >= 4 || target.Monster != 0 || MapEssenceCatalog.Find(row) is not { } incoming) return null;
        int index = target.Essences.ToList().FindIndex(r => MapEssenceCatalog.Find(r)?.Type == incoming.Type);
        if (index >= 0 && MapEssenceCatalog.Find(target.Essences[index])!.Tier >= incoming.Tier) return null;
        var list = target.Essences.ToList();
        if (index < 0) list.Add(row); else list[index] = row;
        return target with { Essences = list };
    }

    static void ApplyReleaseEffects(Player player, Area area, int index)
    {
        var source = area.Monoliths![index];
        if (source.ReleaseEffectsApplied) return;
        area.Monoliths[index] = source with { ReleaseEffectsApplied = true };
        int chance = MapModsOf(player)?.Get(Stat.MapEssenceReleasingImprisonedMonstersGrantsRandomEssenceToOtherImprisonedMonstersChancePct) ?? 0;
        if (chance <= 0 || source.Essences.Count == 0 || AreaRng(player).Next(100) >= chance) return;
        var candidates = area.Monoliths.Where(m => m.Index != index && m.Stage < 4 && m.Monster == 0 && !m.ForcedRelease).ToArray();
        if (candidates.Length == 0) return;
        var target = candidates[AreaRng(player).Next(candidates.Length)];
        int row = source.Essences[AreaRng(player).Next(source.Essences.Count)];
        if (Inherit(target, row) is not { } changed) return;
        area.Monoliths[target.Index] = changed;
        area.Dirty.Add(target.Index);
        Console.WriteLine($"[map-content] essence adaptation source=0x{source.Id:x} target=0x{target.Id:x} essence={row}");
    }

    static async Task PresentDirty(Player player, Area area)
    {
        foreach (int index in area.Dirty.ToArray())
        {
            await Recreate(player, area, area.Monoliths![index]);
            area.Dirty.Remove(index);
        }
    }

    public static async Task Tick(Player player, double now)
    {
        if (player.Closed || player.LeavingArea) return;
        // The essence pulses' player debuffs/degen expire on their own timer, and the released rares cast their ready
        // special spells (pulses/grab) alongside their ordinary AI. Runs before the release/dirty work below.
        TickEffects(player);
        TickBuffs(player);
        TickProximityShields(player);
        TickTrail(player);
        TickRunes(player);
        TickOrbBodies(player);
        TickTotems(player);
        TickHorror(player);
        TickHysteriaBurningGround(player);
        await TickGroundZones(player);
        await CastSpecialSpells(player);
        // Crystal Resonance: apply pending inherited essences to the map boss once it exists (it may be held back far
        // from the player until approached); a cheap check, the Monsters scan runs only while something is pending.
        if (Inherited.TryGetValue(player.Instance, out var inh) && inh.Applied < inh.Essences.Count)
            await ApplyBossEssences(player, inh);
        if (!Areas.TryGetValue(player.Instance, out var area)
            || area.Monoliths is null || area.Dirty.Count == 0 && !area.Monoliths.Any(m => m.ForcedRelease && m.Monster == 0)) return;
        await area.Gate.WaitAsync();
        try
        {
            await PresentDirty(player, area);
            foreach (var m in area.Monoliths!.Where(m => m.ForcedRelease && m.Monster == 0).ToArray())
                await Release(player, area, m);
        }
        finally { area.Gate.Release(); }
    }
}