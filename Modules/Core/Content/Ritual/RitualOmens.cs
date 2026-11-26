using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.Ritual;

/// <summary>
/// Implements all 16 Ritual Omens (<c>Metadata/Items/Currency/AncestralOmen*</c>):
/// Each character can consume one Omen per combat instance.
/// </summary>
public static class RitualOmens
{
    public const string Amelioration = "Metadata/Items/Currency/AncestralOmenOnDeathPreventExpLoss";
    public const string Return = "Metadata/Items/Currency/AncestralOmenOnDeathCreatePortal";
    public const string DeathDancing = "Metadata/Items/Currency/AncestralOmenOnCriticalLifeAvoidDamage";
    public const string Refreshment = "Metadata/Items/Currency/AncestralOmenOnCriticalLifeRecoverFlaskCharges";
    public const string SoulDevourer = "Metadata/Items/Currency/AncestralOmenOnLevelingUpGainSoulEater";
    public const string Acceleration = "Metadata/Items/Currency/AncestralOmenOnLevelingUpSpawnAccelerationShrine";
    public const string Fortune = "Metadata/Items/Currency/AncestralOmenOnChanceMakeUnique";
    public const string Jeweller = "Metadata/Items/Currency/AncestralOmenOnJewellersMakeFullSockets";
    public const string Connections = "Metadata/Items/Currency/AncestralOmenOnFusingMakeFullLinks";
    public const string Trichromatism = "Metadata/Items/Currency/AncestralOmenOnChromaticAddWhiteSockets";
    public const string Revenge = "Metadata/Items/Currency/AncestralOmenOnDeathNearbyEnemiesMoreDamage";
    public const string Bequeathal = "Metadata/Items/Currency/AncestralOmenOnDeathGemsGainExperience";
    public const string Resurgence = "Metadata/Items/Currency/AncestralOmenOnCriticalLifeRecoupLife";
    public const string Adrenaline = "Metadata/Items/Currency/AncestralOmenOnCriticalLifeGainAdrenaline";
    public const string DeathsDoor = "Metadata/Items/Currency/AncestralOmenOnCriticalLifeGainShadeForm";
    public const string Brilliance = "Metadata/Items/Currency/AncestralOmenOnLevelingUpGainExperienceBuff";

    static readonly byte[] BagRows = [PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory];
    static readonly string[] LowLifeOmens = [DeathDancing, Resurgence, Refreshment, Adrenaline, DeathsDoor];

    sealed class PlayerOmenState
    {
        public bool ConsumedInArea;
        public double DeathDancingUntil;
        public double AdrenalineUntil;
        public double ResurgenceUntil;
        public double ShadeFormUntil;
        public double NextStatExpiry;
        public double SoulEaterUntil;
        public double BrillianceUntil;
        public long CachedInventoryMutation = -1;
        public bool HasLowLifeOmen;
    }

    static readonly ConditionalWeakTable<Player, PlayerOmenState> States = new();

    public static void Clear() => States.Clear();

    static PlayerOmenState StateOf(Player player) => States.GetOrCreateValue(player);

    static bool IsCombatArea(Player player) =>
        !InTown(player) && HideoutCatalog.ByArea(player.Layout.Hash) is null;

    static bool HasOmen(Player player, string omenId)
    {
        var items = ItemsOf(player);
        if (ItemsFor(player).Find(omenId) is not { } type) return false;
        foreach (byte row in BagRows)
            foreach (var item in items.Items(row))
                if (item.Item.TypeHash == type.Hash) return true;
        return false;
    }

    static async Task<bool> TryConsumeOmen(Player player, string omenId)
    {
        bool limited = IsCombatArea(player);
        var state = StateOf(player);
        if (limited && state.ConsumedInArea) return false;
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        if (catalog.Find(omenId) is not { } type) return false;
        foreach (byte row in BagRows)
        {
            if (row == PlayerInventories.ExpandedInventory && !ExpandedInventoryGranted(player)) continue;
            var found = items.Items(row).FirstOrDefault(i => i.Item.TypeHash == type.Hash);
            if (found is null) continue;
            string key = $"Ritual/omen/{CharacterName(player)}";
            var instance = limited ? player.Instance : null;
            if (instance is not null && !instance.ContentState.TryAdd(key, "1"))
            {
                state.ConsumedInArea = true;
                return false;
            }
            var taken = items.ConsumeOne(row, found.Id);
            if (taken is null)
            {
                instance?.ContentState.TryRemove(key, out _);
                continue;
            }
            if (limited) state.ConsumedInArea = true;
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row),
                items.NextRevision(row), [found.Id], taken.Left is { } left ? [left] : []),
                $"0x00bf Omen consumed: {type.Name}");
            await Send(player, InstanceChat.ChatLine($"Your {type.Name} has been consumed."),
                $"0x0017 Omen consumed chat ({type.Name})");
            return true;
        }
        return false;
    }

    static bool HasAnyOmen(Player player, string[] omenIds)
    {
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        foreach (string id in omenIds)
        {
            if (catalog.Find(id) is not { } type) continue;
            foreach (byte row in BagRows)
                foreach (var item in items.Items(row))
                    if (item.Item.TypeHash == type.Hash) return true;
        }
        return false;
    }

    // ---- 1. Crafting Omens (GameEvents.CurrencyCrafting) ---------------------------------------------------------------

    public static async Task<Item?> OnCurrencyCrafting(Player player, Item target, uint currencyTypeHash, Random random)
    {
        var catalog = ItemsFor(player);
        var curr = catalog.ByHash(currencyTypeHash);
        var targetType = catalog.ByHash(target.TypeHash);
        if (curr is null || targetType is null || target.Corrupted || target.Mirrored || target.Vestigial) return null;

        // Omen of Fortune: Orb of Chance -> guaranteed chanceable Unique of that base type (excludes EncounterOnly,
        // Corrupted, Mirrored, and Replica uniques; weighted by UniqueWeight when available; creates before consuming).
        if (curr.Id == CurrencyEffects.OrbOfChance && target.Rarity == ItemRarity.Normal && HasOmen(player, Fortune))
        {
            var drops = DropsFor(player);
            var candidates = new List<(UniqueCatalog.Definition Def, double Weight)>();
            double totalWeight = 0;
            foreach (var u in UniquesFor(player).All)
            {
                if (u.EncounterOnly || u.Corrupted || u.Mirrored
                    || u.Name.StartsWith("Replica ", StringComparison.Ordinal)
                    || !u.BaseTypeId.Equals(targetType.Id, StringComparison.OrdinalIgnoreCase)) continue;
                double w = drops.UniqueWeight(u.Name);
                candidates.Add((u, w));
                if (w > 0) totalWeight += w;
            }
            if (candidates.Count > 0)
            {
                UniqueCatalog.Definition? def = null;
                if (totalWeight > 0)
                {
                    double pick = random.NextDouble() * totalWeight;
                    foreach (var (u, w) in candidates)
                    {
                        if (w <= 0) continue;
                        def = u;
                        if (pick < w) break;
                        pick -= w;
                    }
                }
                def ??= candidates[random.Next(candidates.Count)].Def;
                if (UniquesFor(player).Create(def, random) is { } created
                    && await TryConsumeOmen(player, Fortune))
                    return created with { ItemLevel = target.ItemLevel, Quality = target.Quality, Sockets = target.Sockets, Links = target.Links };
            }
        }

        // Omen of the Jeweller: Jeweller's Orb -> maximum possible sockets (preserving any Abyssal sockets).
        if (curr.Id == CurrencyEffects.JewellersOrb)
        {
            int maxSockets = CurrencyEffects.MaximumSockets(target, targetType);
            int capacity = CurrencyEffects.SocketCapacity(target, targetType);
            var abyss = target.Sockets.Where(s => s.Colour == SocketColour.Abyss).ToArray();
            bool blockedByAbyssOnly = abyss.Length > 0
                && ItemModStats.Local(target, catalog.Mods).ContainsKey(Stat.LocalCannotHaveNonAbyssalSockets);
            if (!blockedByAbyssOnly && maxSockets > 0 && target.Sockets.Count < Math.Min(maxSockets, capacity)
                && target.Sockets.All(s => s.Gem is null)
                && await TryConsumeOmen(player, Jeweller))
            {
                var req = catalog.Requirements(targetType);
                int ordinaryCount = Math.Max(0, maxSockets - abyss.Length);
                var sockets = new ItemSocket[maxSockets];
                for (int i = 0; i < ordinaryCount; i++)
                    sockets[i] = new ItemSocket(SocketColours.Colour(req, random));
                for (int i = 0; i < abyss.Length && ordinaryCount + i < maxSockets; i++)
                    sockets[ordinaryCount + i] = abyss[i];
                return target with { Sockets = sockets, Links = [.. Enumerable.Repeat((byte)1, maxSockets)] };
            }
        }

        // Omen of Connections: Orb of Fusing -> all linkable (non-Abyssal) sockets linked.
        if (curr.Id == CurrencyEffects.OrbOfFusing && target.Sockets.Count >= 2
            && target.Sockets.Count <= CurrencyEffects.SocketCapacity(target, targetType))
        {
            var fullLinks = MaxLinkLayout(target.Sockets);
            bool alreadyFull = target.Links is { } curLinks && curLinks.SequenceEqual(fullLinks);
            bool hasLinkablePair = fullLinks.Any(g => g >= 2);
            if (hasLinkablePair && !alreadyFull && await TryConsumeOmen(player, Connections))
                return target with { Links = fullLinks };
        }

        if (curr.Id == CurrencyEffects.ChromaticOrb)
        {
            var sockets = target.Sockets.ToArray();
            var ordinary = Enumerable.Range(0, sockets.Length)
                .Where(i => sockets[i].Colour is SocketColour.Red or SocketColour.Green or SocketColour.Blue or SocketColour.White).ToArray();
            if (ordinary.Length >= 3 && await TryConsumeOmen(player, Trichromatism))
            {
                random.Shuffle(ordinary);
                var req = catalog.Requirements(targetType);
                for (int i = 0; i < ordinary.Length; i++)
                {
                    var colour = i switch
                    {
                        0 => SocketColour.Red, 1 => SocketColour.Green, 2 => SocketColour.Blue,
                        _ => SocketColours.Colour(req, random)
                    };
                    int index = ordinary[i];
                    sockets[index] = sockets[index] with { Colour = colour };
                }
                return target with { Sockets = sockets };
            }
        }

        return null;
    }

    static byte[] MaxLinkLayout(IReadOnlyList<ItemSocket> sockets)
    {
        var groups = new List<byte>();
        byte run = 0;
        foreach (var socket in sockets)
        {
            if (socket.Colour == SocketColour.Abyss)
            {
                if (run > 0) { groups.Add(run); run = 0; }
                groups.Add(1);
            }
            else
            {
                run++;
            }
        }
        if (run > 0) groups.Add(run);
        return [.. groups];
    }

    // ---- 2. On-Death Omens (DeathPenaltyPct + PlayerDied) --------------------------------------------------------------

    public static async Task<decimal> OnDeathPenaltyPct(Player player, decimal percent)
    {
        if (percent <= 0 || !IsCombatArea(player)) return percent;
        if (await TryConsumeOmen(player, Amelioration))
            return percent / 4; // 75% less XP lost
        return percent;
    }

    public static async Task OnPlayerDied(Player player, uint experienceLost)
    {
        if (!IsCombatArea(player)) return;
        // Omen of Return: open a town portal at death location.
        if (await TryConsumeOmen(player, Return))
        {
            var at = player.Walk.Position(AreaTime(player));
            await GameEvents.RequestPortal(player, at.X, at.Y, "Omen of Return");
            return;
        }
        if (await TryConsumeOmen(player, Revenge))
        {
            var at = player.Walk.Position(AreaTime(player));
            foreach (var (_, monster) in player.Area.MonstersNear(at, 50))
                if (monster.Hostile && !monster.Dead && monster.Life > 0 && Dist(at, monster.Walk.Position(AreaTime(player))) <= 50)
                    monster.SetTemporaryStats("Ritual/Revenge", RevengeStats);
            return;
        }
        if (experienceLost > 0 && await TryConsumeOmen(player, Bequeathal))
            await GrantGemExperience(player, (int)Math.Min(int.MaxValue, 3L * experienceLost));
    }

    static readonly IReadOnlyDictionary<uint, int> RevengeStats =
        new Dictionary<uint, int> { [Stat.DamageTakenPlusPctFinalFromAncestralOmen] = 25 };

    // ---- 3. Critical Life (<= 25%) Omens & Timed Buff Refresh (checked in AreaTick) ------------------------------------

    public static async Task Tick(Player player)
    {
        if (player.Closed || player.LeavingArea || !IsCombatArea(player)) return;
        var st = StateOf(player);
        double now = AreaTime(player);

        if (st.NextStatExpiry > 0 && now >= st.NextStatExpiry)
        {
            st.NextStatExpiry = 0;
            await SendCharacterStats(player, PlayerStatChange.Build);
        }

        if (player.PlayerLife == 0) return;
        if (st.ConsumedInArea) return;
        var items = ItemsOf(player);
        if (items.Mutations != st.CachedInventoryMutation)
        {
            st.CachedInventoryMutation = items.Mutations;
            st.HasLowLifeOmen = HasAnyOmen(player, LowLifeOmens);
        }
        if (!st.HasLowLifeOmen) return;
        if (player.PlayerMaxLife == 0 || player.PlayerLife * 4 > player.PlayerMaxLife) return;

        // Priority order: Resurgence -> Refreshment -> Adrenaline -> Death's Door -> Death-dancing.
        if (await TryConsumeOmen(player, Resurgence))
        {
            st.NextStatExpiry = st.ResurgenceUntil = now + 4;
            await SendCharacterStats(player, PlayerStatChange.Build);
        }
        else if (await TryConsumeOmen(player, Refreshment))
        {
            await GainFlaskCharges(player, int.MaxValue, "Omen of Refreshment");
        }
        else if (await TryConsumeOmen(player, Adrenaline))
        {
            st.NextStatExpiry = st.AdrenalineUntil = now + 10;
            player.Area.Buffs.Set(player.Id, new(RitualTables.AdrenalineBuffRow, player.Id, U68: Buff.Seconds(10)));
            await SendCharacterStats(player, PlayerStatChange.Build);
        }
        else if (await TryConsumeOmen(player, DeathsDoor))
        {
            st.NextStatExpiry = st.ShadeFormUntil = now + 3;
            player.Area.Buffs.Set(player.Id, new(1018, player.Id, U68: Buff.Seconds(3)));
            await SendCharacterStats(player, PlayerStatChange.Build);
            player.PhysicalImmunityUntil = Math.Max(player.PhysicalImmunityUntil, now + 3.0);
        }
        else if (await TryConsumeOmen(player, DeathDancing))
        {
            st.NextStatExpiry = st.DeathDancingUntil = now + 4;
            await SendCharacterStats(player, PlayerStatChange.Build);
        }
    }

    // ---- 4. Level-Up Omens (GameEvents.PlayerLevelUp) ------------------------------------------------------------------

    public static async Task OnLevelUp(Player player, int level)
    {
        if (!IsCombatArea(player)) return;
        double now = AreaTime(player);
        var st = StateOf(player);
        if (await TryConsumeOmen(player, SoulDevourer))
        {
            st.NextStatExpiry = st.SoulEaterUntil = now + 300;
            await SendCharacterStats(player, PlayerStatChange.Build);
        }
        else if (await TryConsumeOmen(player, Acceleration))
            await GameServer.Modules.Shrines.ShrineManager.SpawnReward(player, "Acceleration");
        else if (await TryConsumeOmen(player, Brilliance))
        {
            st.NextStatExpiry = st.BrillianceUntil = now + 60;
            await SendCharacterStats(player, PlayerStatChange.Build);
        }
    }

    // ---- 5. Ongoing Buff Stat / Damage / Kill / XP Hooks ---------------------------------------------------------------

    public static void ApplyStats(StatSheet sheet)
    {
        if (!States.TryGetValue(sheet.Player, out var st)) return;
        double now = AreaTime(sheet.Player);
        if (now < st.AdrenalineUntil)
        {
            sheet.Add(Stat.DamagePlusPct, 100);
            sheet.Add(Stat.AttackSpeedPlusPct, 25);
            sheet.Add(Stat.BaseCastSpeedPlusPct, 25);
            sheet.Add(Stat.BaseMovementVelocityPlusPct, 25);
            sheet.Add(Stat.AdditionalPhysicalDamageReductionPct, 10);
        }
        if (now < st.SoulEaterUntil) sheet.Add(Stat.MapPlayerConsumeNearbySouls, 1);
        if (now < st.BrillianceUntil) sheet.Add(Stat.ExperienceGainPlusPct, 100);
        if (now < st.ResurgenceUntil) sheet.Add(Stat.DamageTakenGoesToLifeOver4SecondsPct, 100);
        if (now < st.DeathDancingUntil) sheet.Add(Stat.AvoidDamagePct, 75);
        if (now < st.ShadeFormUntil) sheet.Add(Stat.StealthPlusPct, 100);
    }
}
