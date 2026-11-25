using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.Ritual;

/// <summary>
/// Implements all 16 Ritual Omens (<c>Metadata/Items/Currency/AncestralOmen*</c>):
/// <list type="bullet">
/// <item>Crafting (4): Fortune (Chance→Unique), Jeweller (max sockets), Connections (full links), Trichromatism (3 white sockets).</item>
/// <item>On Death (4): Amelioration (-75% XP loss), Return (town portal at death), Revenge (nearby enemies take heavy burst), Bequeathal (gem XP).</item>
/// <item>Critical / Low Life &lt;= 25% (5): Death-dancing (avoid damage 3s), Refreshment (refill flasks), Resurgence (recover life), Adrenaline (20s Adrenaline), Death's Door (3s phys immunity).</item>
/// <item>Level Up (3): Soul Devourer (60s Soul Eater), Acceleration (45s Acceleration Shrine), Brilliance (180s +100% XP).</item>
/// </list>
/// Enforces retail's 1 Omen consumed per combat area rule (crafting Omens in town/hideout are unlimited).
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
        public double AccelerationUntil;
        public double SoulEaterUntil;
        public int SoulEaterStacks;
        public double BrillianceUntil;
        public long CachedInventoryMutation = -1;
        public bool HasLowLifeOmen;
    }

    static readonly ConditionalWeakTable<Player, PlayerOmenState> States = new();

    static PlayerOmenState StateOf(Player player) => States.GetOrCreateValue(player);

    static bool IsCombatArea(Player player) =>
        !InTown(player) && HideoutCatalog.ByArea(player.Layout.Hash) is null;

    static bool CanConsume(Player player, bool combatLimit)
    {
        if (!combatLimit || !IsCombatArea(player)) return true;
        return !StateOf(player).ConsumedInArea;
    }

    static async Task<bool> TryConsumeOmen(Player player, string omenId, bool combatLimit = true)
    {
        if (!CanConsume(player, combatLimit)) return false;
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        if (catalog.Find(omenId) is not { } type) return false;
        foreach (byte row in BagRows)
        {
            var found = items.Items(row).FirstOrDefault(i => i.Item.TypeHash == type.Hash);
            if (found is null) continue;
            var taken = items.ConsumeOne(row, found.Id);
            if (taken is null) continue;
            if (combatLimit && IsCombatArea(player)) StateOf(player).ConsumedInArea = true;
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
                if (items.Items(row).Any(i => i.Item.TypeHash == type.Hash)) return true;
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

        // Omen of Fortune: Orb of Chance -> guaranteed Unique of that base type.
        if (curr.Id == CurrencyEffects.OrbOfChance && target.Rarity == ItemRarity.Normal)
        {
            var uniques = UniquesFor(player).All.Where(u => u.BaseTypeId.Equals(targetType.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (uniques.Count > 0 && await TryConsumeOmen(player, Fortune, combatLimit: false))
            {
                var def = uniques[random.Next(uniques.Count)];
                if (UniquesFor(player).Create(def, random) is { } created)
                    return created with { ItemLevel = target.ItemLevel, Quality = target.Quality, Sockets = target.Sockets, Links = target.Links };
            }
        }

        // Omen of the Jeweller: Jeweller's Orb -> maximum possible sockets.
        if (curr.Id == CurrencyEffects.JewellersOrb)
        {
            int maxSockets = CurrencyEffects.MaximumSockets(target, targetType);
            if (maxSockets > 0 && target.Sockets.Count < maxSockets && target.Sockets.All(s => s.Gem is null)
                && await TryConsumeOmen(player, Jeweller, combatLimit: false))
            {
                var req = catalog.Requirements(targetType);
                var sockets = Enumerable.Range(0, maxSockets).Select(_ => new ItemSocket(SocketColours.Colour(req, random))).ToArray();
                return target with { Sockets = sockets, Links = [.. Enumerable.Repeat((byte)1, maxSockets)] };
            }
        }

        // Omen of Connections: Orb of Fusing -> all sockets linked.
        if (curr.Id == CurrencyEffects.OrbOfFusing && target.Sockets.Count >= 2)
        {
            bool alreadyFull = target.Links is { Count: 1 } l && l[0] == target.Sockets.Count;
            if (!alreadyFull && await TryConsumeOmen(player, Connections, combatLimit: false))
                return target with { Links = [(byte)target.Sockets.Count] };
        }

        // Omen of Trichromatism: Chromatic Orb -> 3 white sockets (or all sockets if < 3).
        if (curr.Id == CurrencyEffects.ChromaticOrb && target.Sockets.Count >= 1)
        {
            if (await TryConsumeOmen(player, Trichromatism, combatLimit: false))
            {
                var req = catalog.Requirements(targetType);
                int whites = Math.Min(3, target.Sockets.Count);
                var next = new List<ItemSocket>(target.Sockets.Count);
                for (int i = 0; i < target.Sockets.Count; i++)
                {
                    var colour = i < whites ? SocketColour.White : SocketColours.Colour(req, random);
                    next.Add(new ItemSocket(colour, target.Sockets[i].Gem));
                }
                return target with { Sockets = next };
            }
        }

        return null;
    }

    // ---- 2. On-Death Omens (DeathPenaltyPct + PlayerDied) --------------------------------------------------------------

    public static async Task<int> OnDeathPenaltyPct(Player player, int percent)
    {
        if (percent <= 0 || !IsCombatArea(player)) return percent;
        if (await TryConsumeOmen(player, Amelioration))
            return Math.Max(0, percent * 25 / 100); // 75% less XP lost
        return percent;
    }

    public static async Task OnPlayerDied(Player player)
    {
        if (!IsCombatArea(player)) return;
        // Omen of Return: open a town portal at death location.
        if (await TryConsumeOmen(player, Return))
        {
            var at = player.Walk.Position(AreaTime(player));
            await GameEvents.RequestPortal(player, at.X, at.Y, "Omen of Return");
            return;
        }
        // Omen of Revenge: blast nearby enemies around death position.
        if (await TryConsumeOmen(player, Revenge))
        {
            var at = player.Walk.Position(AreaTime(player));
            foreach (var (_, m) in player.Area.MonstersNear(at, 50))
            {
                if (!m.Hostile || m.Dead || m.Life == 0) continue;
                uint dmg = Math.Max(1u, m.MaxLife / 3);
                m.Life = m.Life > dmg ? m.Life - dmg : 1;
            }
            return;
        }
        // Omen of Bequeathal: grant bonus experience to socketed gems.
        if (await TryConsumeOmen(player, Bequeathal))
        {
            uint bonus = (uint)Math.Clamp(player.Pools.Level * 25000L, 10000, 5_000_000);
            await Send(player, InstanceItems.GemExperience(bonus), $"0x00c3 Omen of Bequeathal gem XP +{bonus}");
        }
    }

    // ---- 3. Critical Life (<= 25%) Omens (checked in AreaTick) ---------------------------------------------------------

    public static async Task Tick(Player player)
    {
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0 || !IsCombatArea(player)) return;
        var st = StateOf(player);
        var items = ItemsOf(player);
        if (items.Mutations != st.CachedInventoryMutation)
        {
            st.CachedInventoryMutation = items.Mutations;
            st.HasLowLifeOmen = HasAnyOmen(player, LowLifeOmens);
        }
        if (!st.HasLowLifeOmen || st.ConsumedInArea) return;
        if (player.PlayerMaxLife == 0 || player.PlayerLife * 4 > player.PlayerMaxLife) return;

        double now = AreaTime(player);
        // Priority order: Resurgence -> Refreshment -> Adrenaline -> Death's Door -> Death-dancing.
        if (await TryConsumeOmen(player, Resurgence))
        {
            player.PlayerLife = LifeCap(player);
            await Send(player, InstanceChat.ChatLine("Omen of Resurgence restored your Life!"), "0x0017 Omen of Resurgence");
        }
        else if (await TryConsumeOmen(player, Refreshment))
        {
            var flasks = items.Items(PlayerInventories.Flask);
            var catalog = ItemsFor(player);
            var removed = new List<uint>();
            var added = new List<InventoryItem>();
            foreach (var fl in flasks)
            {
                if (catalog.ByHash(fl.Item.TypeHash) is not { } ft) continue;
                int maxCharges = Math.Max(fl.Item.Charges, 60);
                if (fl.Item.Charges >= maxCharges) continue;
                var refilled = fl.Item with { Charges = maxCharges };
                items.Remove(PlayerInventories.Flask, fl.Id);
                removed.Add(fl.Id);
                added.Add(items.Add(PlayerInventories.Flask, fl.X, fl.Y, refilled));
            }
            if (added.Count > 0)
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Flask),
                    items.NextRevision(PlayerInventories.Flask), removed, added), "0x00bf Omen of Refreshment refilled flasks");
        }
        else if (await TryConsumeOmen(player, Adrenaline))
        {
            st.AdrenalineUntil = now + 20.0;
            player.Area.Buffs.Set(player.Id, new(RitualTables.AdrenalineBuffRow, player.Id, U68: Buff.Seconds(20.0)));
            await SendCharacterStats(player, PlayerStatChange.Build);
        }
        else if (await TryConsumeOmen(player, DeathsDoor))
        {
            player.PhysicalImmunityUntil = Math.Max(player.PhysicalImmunityUntil, now + 3.0);
        }
        else if (await TryConsumeOmen(player, DeathDancing))
        {
            st.DeathDancingUntil = now + 3.0;
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
            st.SoulEaterUntil = now + 60.0;
            st.SoulEaterStacks = 0;
        }
        else if (await TryConsumeOmen(player, Acceleration))
        {
            st.AccelerationUntil = now + 45.0;
            await SendCharacterStats(player, PlayerStatChange.Build);
        }
        else if (await TryConsumeOmen(player, Brilliance))
        {
            st.BrillianceUntil = now + 180.0;
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
        if (now < st.AccelerationUntil)
        {
            sheet.Add(Stat.BaseMovementVelocityPlusPct, 50);
            sheet.Add(Stat.AttackSpeedPlusPct, 50);
            sheet.Add(Stat.BaseCastSpeedPlusPct, 50);
        }
        if (now < st.SoulEaterUntil && st.SoulEaterStacks > 0)
        {
            int bonus = Math.Min(45, st.SoulEaterStacks) * 5;
            sheet.Add(Stat.AttackSpeedPlusPct, bonus);
            sheet.Add(Stat.BaseCastSpeedPlusPct, bonus);
        }
    }

    public static int OnIncomingDamage(Player player, int damage)
    {
        if (damage > 0 && States.TryGetValue(player, out var st) && AreaTime(player) < st.DeathDancingUntil)
            return Math.Max(0, damage / 10); // 90% damage avoidance window
        return damage;
    }

    public static void OnKill(KillContext kill)
    {
        if (!kill.PlayerKill || !States.TryGetValue(kill.Player, out var st)) return;
        if (AreaTime(kill.Player) < st.SoulEaterUntil && st.SoulEaterStacks < 45)
            st.SoulEaterStacks++;
    }

    public static void OnExperience(ExperienceGain gain)
    {
        if (States.TryGetValue(gain.Player, out var st) && AreaTime(gain.Player) < st.BrillianceUntil)
            gain.Experience = (uint)Math.Min(uint.MaxValue, (ulong)gain.Experience * 2UL);
    }
}
