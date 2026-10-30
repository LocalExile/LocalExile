using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    /// <summary>
    /// Applies a tattoo to an allocated passive. This is the server-side pilot path used before the client's
    /// tattoo-use packet is traced: the client command names the tattoo and the passive graph id. The tattoo is
    /// saved independently of the allocation, so refunding the passive keeps it.
    /// </summary>
    internal static async Task<string?> ApplyTattoo(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, string name, ushort node, byte? consumeRow = null, uint consumeItemId = 0,
        ushort? chosenKeystone = null)
    {
        var tree = PassivesFor(conn);
        if (TattooCatalogue.Find(name) is not { } entry) return $"no tattoo \"{name}\"";
        if (tree.Find(node) is not { } passive) return $"unknown passive {node}";
        if (!conn.Passives.Contains(node)) return $"{passive.Name} ({node}) is not allocated";
        if (!TargetMatches(passive, entry.Target)) return $"{entry.Name} replaces {entry.Target}, not {passive.Name} ({node})";
        if (conn.Tattoos.GetValueOrDefault(node) == entry.Name) return $"{passive.Name} ({node}) already has {entry.Name}";
        if (TattooStats.For(entry.Name) is null) return $"{entry.Name} has no server effect yet";
        if (!conn.Tattoos.ContainsKey(node) && conn.Tattoos.Count >= 50) return "a character can have at most 50 tattoos";
        if (entry.EventOnly) return $"{entry.Name} is event-only and is not supported yet";
        if (entry.Name.StartsWith("Loyalty ", StringComparison.Ordinal)) return "Loyalty tattoos are not supported yet";
        if (entry.Name.StartsWith("Honoured Tattoo of the ", StringComparison.Ordinal)
            && entry.Target.StartsWith("Notable", StringComparison.Ordinal)) return "Honoured attribute notables are not supported yet";

        var next = new Dictionary<ushort, string>(conn.Tattoos) { [node] = entry.Name };
        if (TattooLimitRefusal(next, node, entry) is { } limitError) return limitError;
        var nextKeystones = new Dictionary<ushort, ushort>(conn.TattooKeystones);
        bool journeyMakanui = entry.Name == "Journey Tattoo of Makanui";
        JourneyMakanuiCatalogue.Entry? chosen = null;
        if (journeyMakanui)
        {
            if (chosenKeystone is ushort requested)
            {
                chosen = JourneyMakanuiCatalogue.FindByGraphId(requested);
                if (chosen is null) return $"unknown keystone {requested}";
            }
            else chosen = JourneyMakanuiCatalogue.Pick(node);
            if (chosen is null) return "no random keystone available";
            nextKeystones[node] = chosen.GraphId;
        }
        else nextKeystones.Remove(node);
        var items = ItemsOf(conn);
        PlayerInventories.Consumption? taken = null;
        if (consumeRow is byte row)
        {
            var used = Saves.Atomically(() =>
            {
                if (items.ConsumeOne(row, consumeItemId) is not { } consumed) return null;
                CharacterSaves.SetTattoos(CharacterName(conn), next, nextKeystones);
                return consumed;
            });
            if (used is not { } consumed) return "the tattoo item disappeared";
            taken = consumed;
        }
        else CharacterSaves.SetTattoos(CharacterName(conn), next, nextKeystones);
        lock (conn.Monsters)
        {
            conn.Tattoos = next;
            conn.TattooKeystones = nextKeystones;
            conn.TattooRevision++;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        if (taken is { } usedStack && consumeRow is byte usedRow)
        {
            uint inventory = PlayerInventories.InventoryId(usedRow);
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventory, items.NextRevision(usedRow), [consumeItemId],
                usedStack.Left is { } left ? [left] : []),
                $"0x00bf inventory {inventory}: tattoo used" + (usedStack.Left is { } now ? $", {now.Item.StackSize} left" : ", stack empty"));
        }
        await SendCharacterStats(remote, stream, ciphers, conn, "tattoo");
        await SendPassives(remote, stream, ciphers, conn);
        Console.WriteLine($"[gateway] {remote} tattoo {entry.Name} on {node} {passive.Name} ({conn.Tattoos.Count} applied" +
            (chosen is { } chosenEntry ? $", keystone {chosenEntry.Name})" : ")"));
        return null;
    }

    /// <summary>The client's tattoo-on-passive packet (C→S 0x77): consume one tattoo from the inventory and install
    /// it on the named allocated passive. The packet carries the inventory row, the item id and the passive graph id.</summary>
    internal static async Task UseTattooOnPassive(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceItems.UseItemOnPassive use, string hex)
    {
        if (PlayerInventories.Row(use.InventoryId) is not byte row)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0077 tattoo use: unknown inventory {use.InventoryId}: {hex}");
            return;
        }
        var items = ItemsOf(conn);
        if (items.Items(row).FirstOrDefault(i => i.Id == use.ItemId) is not { } item)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0077 tattoo use: no item {use.ItemId} in inventory {use.InventoryId}: {hex}");
            return;
        }
        if (ItemsFor(conn).ByHash(item.Item.TypeHash) is not { } type)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0077 unknown item type {use.ItemId}: {hex}");
            return;
        }
        // The same 0x77 packet is used for an Orb of Scouring on a tattooed passive: the item decides whether the
        // passive is tattooed or restored.
        if (type.Id == CurrencyEffects.OrbOfScouring)
        {
            string? removed = await RemoveTattoo(remote, stream, ciphers, conn, use.Node, row, use.ItemId);
            if (removed is not null)
                Console.WriteLine($"[gateway] {remote} <= 0x0077 scouring passive {use.Node} refused: {removed}: {hex}");
            return;
        }
        if (TattooCatalogue.Find(type.Name) is not { } entry)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0077 item {use.ItemId} is not a tattoo: {hex}");
            return;
        }
        string? error = await ApplyTattoo(remote, stream, ciphers, conn, entry.Name, use.Node, row, use.ItemId);
        if (error is not null)
            Console.WriteLine($"[gateway] {remote} <= 0x0077 tattoo {entry.Name} on {use.Node} refused: {error}: {hex}");
    }

    /// <summary>Removes the tattoo from a passive and restores the passive's own stats.</summary>
    internal static async Task<string?> RemoveTattoo(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort node, byte? consumeRow = null, uint consumeItemId = 0)
    {
        if (PassivesFor(conn).Find(node) is not { } passive) return $"unknown passive {node}";
        if (!conn.Tattoos.ContainsKey(node)) return $"{passive.Name} ({node}) has no tattoo";
        var next = new Dictionary<ushort, string>(conn.Tattoos);
        next.Remove(node);
        var nextKeystones = new Dictionary<ushort, ushort>(conn.TattooKeystones);
        nextKeystones.Remove(node);
        var items = ItemsOf(conn);
        PlayerInventories.Consumption? taken = null;
        if (consumeRow is byte row)
        {
            var used = Saves.Atomically(() =>
            {
                if (items.ConsumeOne(row, consumeItemId) is not { } consumed) return null;
                CharacterSaves.SetTattoos(CharacterName(conn), next, nextKeystones);
                return consumed;
            });
            if (used is not { } consumed) return "the currency item disappeared";
            taken = consumed;
        }
        else CharacterSaves.SetTattoos(CharacterName(conn), next, nextKeystones);
        lock (conn.Monsters)
        {
            conn.Tattoos = next;
            conn.TattooKeystones = nextKeystones;
            conn.TattooRevision++;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        if (taken is { } usedStack && consumeRow is byte usedRow)
        {
            uint inventory = PlayerInventories.InventoryId(usedRow);
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventory, items.NextRevision(usedRow), [consumeItemId],
                usedStack.Left is { } left ? [left] : []),
                $"0x00bf inventory {inventory}: tattoo removed" + (usedStack.Left is { } now ? $", {now.Item.StackSize} left" : ", stack empty"));
        }
        await SendCharacterStats(remote, stream, ciphers, conn, "tattoo removed");
        await SendPassives(remote, stream, ciphers, conn);
        Console.WriteLine($"[gateway] {remote} tattoo removed from {node} {passive.Name} ({conn.Tattoos.Count} applied)");
        return null;
    }

    /// <summary>One tattoo per client limit group (Loyalty, Attribute Notable, Ancestral, Keystone, Ascendancy),
    /// apart from the global 50 cap. The same group on the same node is a replacement and is allowed.</summary>
    internal static string? TattooLimitRefusal(IReadOnlyDictionary<ushort, string> tattoos, ushort node,
        TattooCatalogue.Entry entry)
    {
        if (entry.Limit < 0 || TattooCatalogue.LimitName(entry.Limit) is not { } group) return null;
        foreach (var (other, name) in tattoos)
            if (other != node && TattooCatalogue.Find(name) is { } existing && existing.Limit == entry.Limit)
                return $"only one {group} can be applied";
        return null;
    }

    /// <summary>The counting tattoos' aggregate stats: Journey Body/Mind/Soul grant their flat pool per allocated
    /// copy, and Ancestral Bloodlines grants recovery per distinct tribe among the allocated tattoos. The marker
    /// stats on each node are ignored by consumers; this pass emits the real stat keys once per character.</summary>
    internal static Dictionary<uint, int> TattooCountingStats(IReadOnlyDictionary<ushort, string> tattoos,
        IReadOnlySet<ushort> allocated)
    {
        var result = new Dictionary<uint, int>();
        int Count(string name) => tattoos.Count(kv => allocated.Contains(kv.Key) && kv.Value == name);
        void Add(string stat, int value)
        {
            if (Stats.Key(stat) is uint key) result[key] = result.GetValueOrDefault(key) + value;
        }
        int body = Count("Journey Tattoo of the Body");
        if (body > 0) Add("base_maximum_life", 25 * body);
        int mind = Count("Journey Tattoo of the Mind");
        if (mind > 0) Add("base_maximum_mana", 30 * mind);
        int soul = Count("Journey Tattoo of the Soul");
        if (soul > 0) Add("base_maximum_energy_shield", 40 * soul);
        bool ancestral = tattoos.Any(kv => allocated.Contains(kv.Key)
            && kv.Value == "Ancestral Tattoo of Bloodlines");
        int tribes = ancestral
            ? tattoos
                .Where(kv => allocated.Contains(kv.Key))
                .Select(kv => TattooCatalogue.Find(kv.Value)?.Tribe ?? 11)
                .Where(tribe => tribe is >= 0 and <= 10)
                .Distinct()
                .Count()
            : 0;
        if (tribes > 0)
        {
            Add("life_recovery_rate_+%", 2 * tribes);
            Add("mana_recovery_rate_+%", 2 * tribes);
            Add("energy_shield_recovery_rate_+%", 2 * tribes);
        }
        return result;
    }

    internal static bool TargetMatches(PassiveTree.Node node, string target)
    {
        bool Has(string stat, int value) => node.Stats.Any(s => Stats.Name(s.Key) == stat && s.Value == value);
        bool small = !node.IsNotable && !node.IsKeystone && !node.IsAscendancy && !node.IsStart
            && !node.IsJewelSocket && !node.IsMastery && !node.IsProxy && !node.IsExpansion;
        return target switch
        {
            "Small Strength" => small && Has("base_strength", 10),
            "Small Dexterity" => small && Has("base_dexterity", 10),
            "Small Intelligence" => small && Has("base_intelligence", 10),
            "Small Attribute" => small && (Has("base_strength", 10) || Has("base_dexterity", 10) || Has("base_intelligence", 10)),
            "Notable (+30 to Strength)" => node.IsNotable && Has("base_strength", 30),
            "Notable (+30 to Dexterity)" => node.IsNotable && Has("base_dexterity", 30),
            "Notable (+30 to Intelligence)" => node.IsNotable && Has("base_intelligence", 30),
            "Keystone" => node.IsKeystone,
            "Mastery" => node.IsMastery,
            "Ascendancy Notable" => node.IsAscendancy,
            _ => false,
        };
    }
}
