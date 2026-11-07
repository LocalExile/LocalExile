using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>League-local player orders. Offered currency stays in escrow until matched or collected after cancellation.</summary>
static class CurrencyExchange
{
    public const byte OrderLimit = 10;
    public sealed record Currency(uint Hash, int Fee, bool Divisor, bool Standard, bool Challenge)
    {
        public long Cost(int amount) => Divisor ? ((long)amount + Fee - 1) / Fee : (long)amount * Fee;
    }
    public sealed record Order(uint Id, string Account, string League, uint Want, uint Give, int Wanted, int Offered,
        int RemainingWant, int RemainingGive, int Received, int Collected, long Created, bool Closed = false, string? MarketOwner = null)
    {
        public bool Active => !Closed && RemainingWant > 0 && RemainingGive > 0;
        public int Claim => Received - Collected;
        public bool Empty => !Active && RemainingGive == 0 && Claim == 0;
    }
    public sealed record Book(uint NextId, List<Order> Orders);
    public sealed record Payment(PlayerInventories.Payment? Inventory, AccountStash.Payment? Stash);
    public sealed record Placement(Order Order, Payment Payment, IReadOnlyList<Order> Changed);
    public sealed record Collection(Order Order, byte Row, IReadOnlyList<InventoryItem> Items);
    static readonly Lazy<IReadOnlyDictionary<uint, Currency>> Catalog = new(() =>
    {
        using var bases = GameTables.Open("data/tables/BaseItemTypes.json");
        using var exchange = GameTables.Open("data/tables/CurrencyExchange.json");
        var rows = bases.RootElement.EnumerateArray().ToArray();
        return exchange.RootElement.EnumerateArray().Select(r => new Currency(
            rows[r.GetProperty("Item").GetInt32()].GetProperty("HASH32").GetUInt32(),
            r.GetProperty("GoldPurchaseFee").GetInt32(), r.GetProperty("__col6").GetBoolean(), r.GetProperty("EnabledInStandardLeague").GetBoolean(),
            r.GetProperty("EnabledInChallengeLeague").GetBoolean())).DistinctBy(c => c.Hash).ToDictionary(c => c.Hash);
    });
    public static Currency? Find(uint hash, ItemCatalog items) =>
        Catalog.Value.GetValueOrDefault(hash) is { } c && items.ByHash(hash) is { Supported: true, Stackable: true, BecomesWhenFull: false } ? c : null;

    static string Key(string league) => "currency-exchange:v1:" + league.ToUpperInvariant();
    static Book Read(string league) => Saves.Db.GetMeta(Key(league)) is { } json
        ? JsonSerializer.Deserialize<Book>(json) ?? throw new InvalidDataException("Unreadable currency exchange book") : new(1, []);
    static void Write(string league, Book book) => Saves.Db.SetMeta(Key(league), JsonSerializer.Serialize(book));
    static bool Own(Order o, string account) => o.Account.Equals(account, StringComparison.OrdinalIgnoreCase);
    public static IReadOnlyList<Order> Orders(string league, string? account = null)
    {
        // GetMeta reads one committed JSON value under the database lock; browsing needs no write transaction.
        var owners = ExchangeMarkets.ActiveOwners(league);
        return Read(league).Orders.Where(o => (o.MarketOwner is null || owners.Contains(o.MarketOwner))
            && (account is null || Own(o, account))).ToArray();
    }
    public static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    public static Placement? Place(string account, string league, bool solo, PlayerInventories items, AccountStash stash,
        ItemCatalog catalog, uint want, uint give, int wanted, int offered) => Saves.Atomically<Placement?>(() =>
    {
        if (solo || string.IsNullOrWhiteSpace(account) || want == give || wanted is <= 0 or > 999999
            || offered is <= 0 or > 999999 || Find(want, catalog) is not { Fee: > 0 } currency
            || Find(give, catalog) is null) return null;
        long goldFee = currency.Cost(wanted);
        if (goldFee > uint.MaxValue) return null;
        int gcd = Gcd(wanted, offered);
        if (wanted / gcd > 65000 || offered / gcd > 65000) return null;
        var book = Read(league);
        if (book.Orders.Count(o => Own(o, account)) >= OrderLimit || book.NextId == uint.MaxValue
            || stash.Gold < goldFee || (long)items.Count(PlayerInventories.MainInventory, give) + stash.CountOf(give) < offered) return null;
        int fromInventory = Math.Min(offered, items.Count(PlayerInventories.MainInventory, give));
        var inventory = fromInventory == 0 ? null : items.Pay(PlayerInventories.MainInventory, give, fromInventory)
            ?? throw new InvalidOperationException("Exchange inventory payment changed");
        var fromStash = offered == fromInventory ? null : stash.Pay(give, offered - fromInventory)
            ?? throw new InvalidOperationException("Exchange stash payment changed");
        if (stash.TrySpendGold(goldFee) is null) throw new InvalidOperationException("Exchange gold payment changed");
        var incoming = new Order(book.NextId, account, league, want, give, wanted, offered, wanted, offered, 0, 0,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var changed = Match(book, league, ref incoming);
        book.Orders.Add(incoming);
        changed.Add(incoming);
        Write(league, book with { NextId = book.NextId + 1 });
        return new(incoming, new(inventory, fromStash), changed);
    });

    static List<Order> Match(Book book, string league, ref Order incoming, bool marketsOnly = false)
    {
        var taker = incoming;
        var owners = ExchangeMarkets.ActiveOwners(league);
        var changed = new List<Order>();
        // Price, then time priority. Execute integral lots at the resting order's price; never round away player currency.
        var makers = book.Orders.Select((order, index) => (Order: order, Index: index))
            .Where(entry => entry.Order is var o && o.Active && (!marketsOnly || o.MarketOwner is not null)
                && (o.MarketOwner is null || owners.Contains(o.MarketOwner)) && !Own(o, taker.Account)
                && o.Want == taker.Give && o.Give == taker.Want
                && (long)o.Wanted * taker.Wanted <= (long)o.Offered * taker.Offered)
            .OrderBy(entry => (decimal)entry.Order.Wanted / entry.Order.Offered).ThenBy(entry => entry.Order.Id).ToArray();
        foreach (var (maker, index) in makers)
        {
            int divisor = Gcd(maker.Wanted, maker.Offered), pay = maker.Wanted / divisor, receive = maker.Offered / divisor;
            int lots = Math.Min(Math.Min(incoming.RemainingGive / pay, incoming.RemainingWant / receive),
                Math.Min(maker.RemainingGive / receive, maker.RemainingWant / pay));
            if (lots == 0) continue;
            int paid = lots * pay, received = lots * receive;
            incoming = incoming with { RemainingGive = incoming.RemainingGive - paid,
                RemainingWant = incoming.RemainingWant - received, Received = incoming.Received + received };
            var filled = maker with { RemainingGive = maker.RemainingGive - received,
                RemainingWant = maker.RemainingWant - paid, Received = maker.Received + paid };
            book.Orders[index] = filled;
            changed.Add(filled);
            if (!incoming.Active) break;
        }
        return changed;
    }

    public static IReadOnlyList<Order> SyncMarkets(string league, ItemCatalog catalog)
    {
        string Marker(string owner) => Key(league) + ":market:" + owner;
        var updates = ExchangeMarkets.Current(league)
            .Where(m => Saves.Db.GetMeta(Marker(m.Owner)) != m.Key).ToArray();
        if (updates.Length == 0) return [];
        // Exact names only. Resolve once outside the save transaction, rather than fuzzy-scanning every quote.
        var types = catalog.All.Where(t => Find(t.Hash, catalog) is not null)
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Hash, StringComparer.OrdinalIgnoreCase);
        var prepared = updates.Select(m => (Market: m, Orders: m.Quotes.Select(q =>
        {
            if (!types.TryGetValue(q.Want, out uint want) || !types.TryGetValue(q.Give, out uint give) || want == give) return null;
            int gcd = Gcd(q.Wanted, q.Offered);
            return q.Wanted / gcd > 65000 || q.Offered / gcd > 65000 ? null
                : new Order(0, "", league, want, give, q.Wanted, q.Offered, q.Wanted, q.Offered,
                    0, 0, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), MarketOwner: m.Owner);
        }).OfType<Order>().ToArray())).ToArray();
        return Saves.Atomically<IReadOnlyList<Order>>(() =>
        {
            // Another connection may have installed the same snapshot while names were being resolved.
            var pending = prepared.Where(p => ExchangeMarkets.IsCurrent(p.Market)
                && Saves.Db.GetMeta(Marker(p.Market.Owner)) != p.Market.Key).ToArray();
            if (pending.Length == 0) return [];
            var book = Read(league);
            foreach (var (market, orders) in pending)
            {
                if ((ulong)book.NextId + (ulong)orders.Length > uint.MaxValue)
                    throw new InvalidOperationException("Currency exchange order ids exhausted");
                book.Orders.RemoveAll(o => o.MarketOwner == market.Owner);
                foreach (var order in orders)
                {
                    book.Orders.Add(order with { Id = book.NextId });
                    book = book with { NextId = book.NextId + 1 };
                }
                Saves.Db.SetMeta(Marker(market.Owner), market.Key);
            }
            var changed = new List<Order>();
            for (int i = 0; i < book.Orders.Count; i++)
            {
                var old = book.Orders[i];
                if (!old.Active || old.MarketOwner is not null) continue;
                var incoming = old;
                changed.AddRange(Match(book, league, ref incoming, marketsOnly: true));
                book.Orders[i] = incoming;
                if (incoming != old) changed.Add(incoming);
            }
            Write(league, book);
            return changed;
        });
    }

    public static Order? Cancel(string league, string account, uint id) => Saves.Atomically(() =>
    {
        var book = Read(league);
        var old = book.Orders.FirstOrDefault(o => o.Id == id && Own(o, account));
        if (old is null) return null;
        var cancelled = old with { Closed = true };
        book.Orders[book.Orders.IndexOf(old)] = cancelled;
        Write(league, book);
        return cancelled;
    });

    public static Collection? Collect(string league, string account, uint id, bool refund, bool toInventory,
        PlayerInventories items, ItemCatalog catalog, Func<Item, PlayerInventories.Shape?> shapeOf,
        bool singleStack = false) => Saves.Atomically<Collection?>(() =>
    {
        var book = Read(league);
        var old = book.Orders.FirstOrDefault(o => o.Id == id && Own(o, account));
        if (old is null || old.Active) return null;
        int available = refund ? old.RemainingGive : old.Claim;
        if (available <= 0 || catalog.ByHash(refund ? old.Give : old.Want) is not { } type) return null;
        byte row = toInventory ? PlayerInventories.MainInventory : PlayerInventories.Cursor;
        var item = ItemCatalog.Create(type) with { StackSize = (ushort)Math.Min(available,
            toInventory && !singleStack ? ushort.MaxValue : type.MaxStack) };
        IReadOnlyList<InventoryItem> added;
        int received;
        if (toInventory)
        {
            var stacked = items.AddStacked(row, item, shapeOf);
            received = item.StackSize - stacked.Left;
            added = stacked.AddedItems;
        }
        else
        {
            if (items.Items(row).Count != 0) return null;
            added = [items.Add(row, 0, 0, item)];
            received = item.StackSize;
        }
        if (received == 0) return null;
        var next = refund ? old with { RemainingGive = old.RemainingGive - received }
            : old with { Collected = old.Collected + received };
        if (next.Empty) book.Orders.Remove(old);
        else book.Orders[book.Orders.IndexOf(old)] = next;
        Write(league, book);
        return new(next, row, added);
    });
}
