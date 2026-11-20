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
        CorruptionOutcome[] outcomes = mods.Get("map_stable_essence_corruption") > 0
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
        if (mods.Get("map_essence_remnant_convert") > 0)
            essences = Enumerable.Repeat(monolith.Essences[random.Next(monolith.Essences.Count)], monolith.Essences.Count).ToList();
        return new(monolith with { Corrupted = true, ForcedRelease = outcome == CorruptionOutcome.Release
            && mods.Get("map_essence_corruption_cannot_release_monsters") <= 0, Essences = essences }, outcome);
    }

    /// <summary>The verified, shared item-on-world-object packet; a box handler declines these targets.</summary>
    public static async Task<bool> UseItemOn(ClientState conn, byte[] message)
    {
        if (message.Length != InstanceItems.UseItemOnObjectLength) return false;
        var use = InstanceItems.ReadUseItemOnObject(message);
        if (!Areas.TryGetValue(conn.Instance, out var area)
            || area.Monoliths?.FirstOrDefault(m => m.Id == use.TargetObjectId) is not { } target) return false;
        string Log(string text) => $"[map-content] essence 0x{use.TargetObjectId:x}: {text}";
        var items = ItemsOf(conn);
        await area.Gate.WaitAsync();
        try
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0
                || !PlayerWalk.InWalkUpRange(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), (target.X, target.Y), 20)) return true;
            if (items.RowOf(use.ItemId) is not byte row || PlayerInventories.InventoryId(row) != use.InventoryId
                || items.Items(row).FirstOrDefault(i => i.Id == use.ItemId) is not { } used
                || ItemsFor(conn).ByHash(used.Item.TypeHash)?.Id != CurrencyEffects.VaalOrb)
            {
                Console.WriteLine(Log("requires a Vaal Orb in the named character inventory; nothing consumed"));
                return true;
            }
            var monolith = area.Monoliths![target.Index];
            int level = Math.Clamp(MapRunOf(conn)?.Area.Level ?? ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);
            var result = Corrupt(monolith, level, MapModsOf(conn) ?? MapMods.None, Random.Shared);
            if (result is null)
            {
                Console.WriteLine(Log("already corrupted or released; nothing consumed"));
                return true;
            }
            if (!CurrentGameWork || items.ConsumeOne(row, used.Id) is not { } taken) return true;
            monolith = result.Monolith;
            area.Monoliths[monolith.Index] = monolith;
            area.Dirty.Add(monolith.Index);
            Keep(conn, area); // commit shared state before any output or another player's request
            var wire = conn.Wire;
            await Send(wire.Remote, wire.Stream, wire.Ciphers,
                InstanceItems.InventoryChange(use.InventoryId, items.NextRevision(row), [used.Id], taken.Left is { } left ? [left] : []),
                $"0x00bf Vaal Orb used on essence monolith 0x{monolith.Id:x}");
            Console.WriteLine(Log($"corrupted outcome={result.Outcome} essences=[{string.Join(',', monolith.Essences)}]"));
            // Show the corrupted essences first, so a forced release breaks the new list rather than recreating after it.
            await PresentDirty(conn, area);
            if (monolith.ForcedRelease) await Release(conn, area, monolith);
        }
        finally { area.Gate.Release(); }
        return true;
    }

    static async Task Recreate(ClientState conn, Monolith monolith)
    {
        if (Presented.TryGetValue(conn, out var shown) && shown.Remove(monolith.Id))
        {
            var w = conn.Wire;
            await SendWorldRemoval(w.Remote, w.Stream, w.Ciphers, conn, monolith.Id, null);
        }
        await Present(conn, monolith);
    }

    // The area gate owns release and its exact reward identity. A paid forced release survives a connection leaving.
    static async Task<bool> Release(ClientState conn, Area area, Monolith monolith)
    {
        if (monolith.Stage >= 4 || monolith.Monster != 0 || Monsters.Find(monolith.Variety) is not { Supported: true } variety) return false;
        byte level = (byte)AreaLevel(conn);
        var mods = MapEssenceCatalog.Mods(conn, variety, level, monolith.Essences);
        if (monolith.ForcedRelease && ItemsFor(conn).Mods.ById("RemnantOfCorruptionMonsterStats") is { } empowered)
            mods = MonsterModRolls.Value.Create([.. mods.Mods, empowered.Roll(ModSection.Implicit, Random.Shared)], mods.NameWords, MonsterRarity.Rare, level);
        // Only the rare casts the essences' spells: they go into its kit as specials with the skill's cooldown.
        var essenceKit = EssenceKit(conn, variety, level, monolith.Essences);
        RemoveEncasedPreviews(conn, monolith);
        var w = conn.Wire;
        // Established: the rare emerges from the crystal's own cell. The monolith's blocker is still registered until
        // the break is presented, so the proposed placement's nearest-clear search would spawn it offset from the crystal.
        uint monster = await SpawnMonster(w.Remote, w.Stream, w.Ciphers, conn, variety, level, MonsterRarity.Rare,
            monolith.X, monolith.Y, mods: mods, unrisen: false, alone: false, extraKit: essenceKit,
            placement: MonsterPlacement.Established, owned: (id, _) =>
            {
                area.Monoliths![monolith.Index] = monolith with { Stage = 4, Monster = id, ForcedRelease = false };
                area.Dirty.Add(monolith.Index);
                ApplyReleaseEffects(conn, area, monolith.Index);
                Keep(conn, area);
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
            uint mid = await SpawnMonster(w.Remote, w.Stream, w.Ciphers, conn, mv, level, MonsterRarity.Magic,
                mini.X, mini.Y, unrisen: false, alone: false, placement: MonsterPlacement.Established);
            if (mid != 0) minis[i] = mini with { Stage = 2, Monster = mid };
        }
        area.Monoliths![monolith.Index] = current with { Minis = minis };
        Keep(conn, area);
        var pending = MapEssenceCatalog.PendingEffects(monolith.Essences);
        if (pending.Count > 0)
            Console.WriteLine($"[map-content] essence source=0x{monolith.Id:x} monster=0x{monster:x}: unsupported behaviours=[{string.Join(',', pending)}]; ordinary stats and essence rewards remain active");
        foreach (var member in Members(conn)) await Present(member, area.Monoliths![monolith.Index], true);
        // The transition showed the release; recreating the object would cut its break short. Kept dirty only if a send failed.
        area.Dirty.Remove(monolith.Index);
        await PresentDirty(conn, area);
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

    static void ApplyReleaseEffects(ClientState conn, Area area, int index)
    {
        var source = area.Monoliths![index];
        if (source.ReleaseEffectsApplied) return;
        area.Monoliths[index] = source with { ReleaseEffectsApplied = true };
        int chance = MapModsOf(conn)?.Get("map_essence_releasing_imprisoned_monsters_grants_random_essence_to_other_imprisoned_monsters_chance_%") ?? 0;
        if (chance <= 0 || source.Essences.Count == 0 || Random.Shared.Next(100) >= chance) return;
        var candidates = area.Monoliths.Where(m => m.Index != index && m.Stage < 4 && m.Monster == 0 && !m.ForcedRelease).ToArray();
        if (candidates.Length == 0) return;
        var target = candidates[Random.Shared.Next(candidates.Length)];
        int row = source.Essences[Random.Shared.Next(source.Essences.Count)];
        if (Inherit(target, row) is not { } changed) return;
        area.Monoliths[target.Index] = changed;
        area.Dirty.Add(target.Index);
        Console.WriteLine($"[map-content] essence adaptation source=0x{source.Id:x} target=0x{target.Id:x} essence={row}");
    }

    static async Task PresentDirty(ClientState conn, Area area)
    {
        foreach (int index in area.Dirty.ToArray())
        {
            foreach (var member in Members(conn)) await Recreate(member, area.Monoliths![index]);
            area.Dirty.Remove(index);
        }
    }

    public static async Task Tick(ClientState conn, double now)
    {
        if (conn.Closed || conn.LeavingArea) return;
        // The essence pulses' player debuffs/degen expire on their own timer, and the released rares cast their ready
        // special spells (pulses/grab) alongside their ordinary AI. Runs before the release/dirty work below.
        TickEffects(conn);
        TickBuffs(conn);
        await CastSpecialSpells(conn);
        // Crystal Resonance: apply pending inherited essences to the map boss once it exists (it may be held back far
        // from the player until approached); a cheap check, the Monsters scan runs only while something is pending.
        if (Inherited.TryGetValue(conn.Instance, out var inh) && inh.Applied < inh.Essences.Count)
            await ApplyBossEssences(conn, inh);
        if (!Areas.TryGetValue(conn.Instance, out var area)
            || area.Monoliths is null || area.Dirty.Count == 0 && !area.Monoliths.Any(m => m.ForcedRelease && m.Monster == 0)) return;
        await area.Gate.WaitAsync();
        try
        {
            await PresentDirty(conn, area);
            foreach (var m in area.Monoliths!.Where(m => m.ForcedRelease && m.Monster == 0).ToArray())
                await Release(conn, area, m);
        }
        finally { area.Gate.Release(); }
    }
}
