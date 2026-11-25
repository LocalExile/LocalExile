using System.Numerics;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Trade;
using GameServer.Modding;
using GameServer.Core;
using Wire = GameServer.Core.Protocol.Instance.InstanceCurrencyExchange;

namespace GameServer.Modules.CurrencyExchange;

/// <summary>Faustus's Currency Exchange at the official leagues' prices (Agents/docs/protocol/29-currency-exchange.md): the module only
/// reads the prices from <c>data/</c>, and never connects to anything itself.</summary>
public sealed class CurrencyExchangeModule : IModule
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadCurrencyExchangeModuleData()
    {
        FaustusLine = LoadFaustusLine();
    }

    const string SaveKey = "currency-exchange", Window = "currency-exchange", RatesFile = ExchangeRateStore.File;
    const double SettleEverySeconds = 30;

    static ExchangeOptions Options => ExchangeRateStore.Options;
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Starts full: the first tick in an area settles, so what filled while the player was away shows as they arrive.</summary>
    sealed class Timer { public double Seconds = SettleEverySeconds; public bool Arrived; }

    readonly ConditionalWeakTable<IPlayer, Timer> _timers = new();
    readonly object _ratesLock = new();
    ExchangeRates? _rates;
    Dictionary<string, ExchangePrice>? _baselinePrices;
    DateTime _ratesWritten;
    IModuleContext _module = null!;

    /// <summary>This module's game data, built in a fixed order when it loads: before the Gateway listens, as the
    /// Gateway's own (WorldLoader). Tests that use the module call it once.</summary>
    public static void LoadData()
    {
        LoadCurrencyExchangeModuleData();
        ExchangeItems.LoadExchangeMarketData();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        _module = module;
        GameEvents.NpcOption.Add(module.Manifest.Id, async talk =>
        {
            if (!talk.Script.Contains("CurrencyExchangeWindow", StringComparison.Ordinal)) return false;
            await WorldOf(talk.Player).NpcClose("0x0084 close NPC dialogue for the Currency Exchange");
            await Open(talk.ModPlayer);
            return true;
        });
        GameEvents.Command(module.Manifest.Id, "exchange", "/exchange (opens the Currency Exchange)", (player, args) => Open(PlayerOf(player)));
        Handle(Wire.RequestOrdersId, (player, _) => SendOrders(player));
        Handle(Wire.RequestFeeId, (player, m) => SendFee(player, Wire.ReadU32(m.Span)));
        Handle(Wire.RequestPartnersId, (player, m) => SendPartners(player, Wire.ReadU32(m.Span)));
        Handle(Wire.RequestMarketId, (player, m) => SendMarket(player, Wire.ReadRequestMarket(m.Span)));
        Handle(Wire.PlaceOrderId, (player, m) => Place(player, Wire.ReadPlaceOrder(m.Span)));
        Handle(Wire.CloseOrderId, (player, m) => Close(player, Wire.ReadU32(m.Span)));
        Handle(Wire.ClaimId, (player, m) => Claim(player, Wire.ReadClaim(m.Span)));
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(async (player, seconds) =>
        {
            var modPlayer = PlayerOf(player);
            var timer = _timers.GetOrCreateValue(modPlayer);
            if ((timer.Seconds += seconds) < SettleEverySeconds) return;
            timer.Seconds = 0;
            await Settle(modPlayer);
            // Faustus's bag does not outlast an area: it is lit again on arriving, if anything waits.
            if (timer.Arrived || !WorldOf(modPlayer).PlayerReady) return;
            timer.Arrived = true;
            if (!BookOf(modPlayer).HasClaims) return;
            await SendOrders(modPlayer);
            await Light(modPlayer);
        }));
    }

    void Handle(ushort opcode, Func<IPlayer, ReadOnlyMemory<byte>, Task> run) => GameEvents.Packet(opcode).Add(_module.Manifest.Id, async (player, bytes) =>
    {
        await run(PlayerOf(player), bytes);
        return true;
    });

    Task Light(IPlayer player) => player.Send(Wire.OrdersWaiting(), "0x02fa orders wait to be claimed");

    Task Open(IPlayer player) => WorldOf(player).OpenWindow(Window, Wire.WindowUi, "0x007b open the Currency Exchange");

    ExchangeItems ItemsOf(IPlayer player) => ExchangeItems.For(WorldOf(player).Crafting);

    // Baseline chaos values for stock usability when data/currency-exchange-rates.json (liveeconomy) is absent.
    // Whenever data/currency-exchange-rates.json is present, it always takes priority.
    static readonly Dictionary<string, double> CoreBaselineChaos = new(StringComparer.Ordinal)
    {
        ["Metadata/Items/Currency/CurrencyRerollRare"] = 1.0,                 // Chaos Orb
        ["Metadata/Items/Currency/CurrencyModValues"] = 150.0,                // Divine Orb
        ["Metadata/Items/Currency/CurrencyAddModToRare"] = 15.0,              // Exalted Orb
        ["Metadata/Items/Currency/CurrencyDuplicate"] = 120_000.0,            // Mirror of Kalandra
        ["Metadata/Items/Currency/CurrencyDuplicateShard"] = 6_000.0,         // Mirror Shard
        ["Metadata/Items/Currency/CurrencyHinekorasLock"] = 12_000.0,         // Hinekora's Lock
        ["Metadata/Items/Currency/CurrencyImprintOrb"] = 50_000.0,            // Eternal Orb
        ["Metadata/Items/Currency/CurrencyAddModToMagic"] = 0.03,             // Orb of Augmentation
        ["Metadata/Items/Currency/CurrencyUpgradeToMagic"] = 0.02,            // Orb of Transmutation
        ["Metadata/Items/Currency/CurrencyRerollMagic"] = 0.1,                // Orb of Alteration
        ["Metadata/Items/Currency/CurrencyUpgradeMagicToRare"] = 0.5,         // Regal Orb
        ["Metadata/Items/Currency/CurrencyUpgradeToRare"] = 0.2,              // Orb of Alchemy
        ["Metadata/Items/Currency/CurrencyUpgradeRandomly"] = 0.1,            // Orb of Chance
        ["Metadata/Items/Currency/CurrencyConvertToNormal"] = 0.3,            // Orb of Scouring
        ["Metadata/Items/Currency/CurrencyCorrupt"] = 0.5,                    // Vaal Orb
        ["Metadata/Items/Currency/CurrencyRerollSocketLinks"] = 0.2,          // Orb of Fusing
        ["Metadata/Items/Currency/CurrencyRerollSocketNumbers"] = 0.05,       // Jeweller's Orb
        ["Metadata/Items/Currency/CurrencyRerollSocketColours"] = 0.08,       // Chromatic Orb
        ["Metadata/Items/Currency/CurrencyPassiveRefund"] = 0.4,              // Orb of Regret
        ["Metadata/Items/Currency/CurrencyAtlasPassiveRefund"] = 0.5,         // Orb of Unmaking
        ["Metadata/Items/Currency/CurrencyRemoveMod"] = 10.0,                 // Orb of Annulment
        ["Metadata/Items/Currency/CurrencyRerollImplicit"] = 0.2,             // Blessed Orb
        ["Metadata/Items/Currency/CurrencyGemQuality"] = 1.0,                 // Gemcutter's Prism
        ["Metadata/Items/Currency/CurrencyFlaskQuality"] = 0.35,              // Glassblower's Bauble
        ["Metadata/Items/Currency/CurrencyMapQuality"] = 0.4,                 // Cartographer's Chisel
        ["Metadata/Items/Currency/CurrencyArmourQuality"] = 0.02,             // Armourer's Scrap
        ["Metadata/Items/Currency/CurrencyWeaponQuality"] = 0.05,             // Blacksmith's Whetstone
        ["Metadata/Items/Currency/CurrencyIdentification"] = 0.005,           // Scroll of Wisdom
        ["Metadata/Items/Currency/CurrencyPortal"] = 0.01,                    // Portal Scroll
        ["Metadata/Items/Currency/CurrencyRerollMapType"] = 0.2,              // Orb of Horizons
        ["Metadata/Items/Currency/CurrencyUpgradeMapTier"] = 2.0,             // Harbinger's Orb
        ["Metadata/Items/Currency/CurrencyRerollUnique"] = 12.0,              // Ancient Orb
        ["Metadata/Items/Currency/CurrencyFractureOrb"] = 600.0,              // Fracturing Orb
        ["Metadata/Items/Currency/CurrencyVeiledOrb"] = 800.0,                // Veiled Orb
        ["Metadata/Items/Currency/CurrencyRerollDefences"] = 50.0,            // Sacred Orb
        ["Metadata/Items/Currency/CurrencyConflictOrb"] = 80.0,               // Orb of Conflict
        ["Metadata/Items/Currency/CurrencyUpgradeInfluenceMod"] = 300.0,      // Orb of Dominance
        ["Metadata/Items/Currency/CurrencyEnkindlingOrb"] = 0.3,              // Enkindling Orb
        ["Metadata/Items/Currency/CurrencyInstillingOrb"] = 0.3,              // Instilling Orb
        ["Metadata/Items/Currency/CurrencyStrongboxQuality"] = 0.25,          // Engineer's Orb
    };

    const string BaselineLeague = "Baseline";

    ExchangeRates BaselineRates(ExchangeItems items)
    {
        lock (_ratesLock)
        {
            if (_baselinePrices is null)
            {
                var prices = new Dictionary<string, ExchangePrice>(StringComparer.Ordinal);
                foreach (var row in items.ByHash.Values)
                {
                    double chaos = CoreBaselineChaos.TryGetValue(row.Id, out double fixedChaos)
                        ? fixedChaos
                        : row.ItemsPerGold
                            ? Math.Max(0.01, 0.25 / Math.Max(1u, row.Fee))
                            : Math.Clamp(row.Fee / 50.0, 0.25, 2_000.0);
                    double volume = Math.Max(5_000.0, chaos * 50.0);
                    prices[row.Id] = new ExchangePrice(chaos, volume);
                }
                _baselinePrices = prices;
            }
            long hour = ExchangeRates.QuantizeHour(Now);
            return new ExchangeRates(hour, BaselineLeague, new Dictionary<string, Dictionary<string, ExchangePrice>>(StringComparer.Ordinal)
            {
                [ExchangeRates.Standard] = _baselinePrices,
                [BaselineLeague] = _baselinePrices,
            }, Hours: 1);
        }
    }

    ExchangeRates? Rates(ExchangeItems items)
    {
        lock (_ratesLock)
        {
            var file = new FileInfo(RatesFile);
            if (!file.Exists)
            {
                _rates = null;
                _ratesWritten = default;
                return BaselineRates(items);
            }
            if (file.LastWriteTimeUtc == _ratesWritten && _rates is not null) return _rates;
            try
            {
                _rates = ExchangeRates.Parse(File.ReadAllText(RatesFile));
                _ratesWritten = file.LastWriteTimeUtc;
                _module.Log($"rates of {DateTimeOffset.FromUnixTimeSeconds(_rates?.SourceHour ?? 0):u}, current league {_rates?.Current}");
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                _module.Log($"{RatesFile} cannot be read, the rates stay as they were: {e.Message}");
            }
            return _rates ?? BaselineRates(items);
        }
    }

    /// <summary>The market this player trades on; falls back to built-in baseline rates when no rates file is present.</summary>
    ExchangeMarket? MarketOf(IPlayer player)
    {
        var items = ItemsOf(player);
        if (Rates(items) is not { } rates) return null;
        var options = Options;
        if (rates.Current != BaselineLeague && Now - rates.SourceHour > options.MaxRateAgeDays * 86400) return null;
        var league = player.League == ExchangeRates.Standard ? options.StandardPrices : options.LeaguePrices;
        return rates.Prices(league == ExchangeRatesLeague.Standard) is { } prices
            ? new(rates, prices, hash => items.ByHash.TryGetValue(hash, out var row) ? row.Id : null, options.SpreadScale, options.VolumeShare,
                options.PatientRange, options.PatientSpeed, options.CatchUpHours) : null;
    }

    ExchangeBook BookOf(IPlayer player) => ExchangeBook.Parse(WorldOf(player).AccountData.Get(SaveKey));

    bool Save(IPlayer player, Action<ExchangeBook> change) => WorldOf(player).AccountData.Change(SaveKey, json =>
    {
        var book = ExchangeBook.Parse(json);
        change(book);
        return book.Serialize();
    });

    static Wire.Order ToWire(ExchangeOrder o, string league, ExchangeMarket? market)
    {
        // The row's market ratio is the cheapest competing order: Faustus selling what this order sells.
        var (give, get) = market?.Trade(o.Have, o.Want) ?? ((ushort)0, (ushort)0);
        return new(o.Id, league, o.Have, o.Want, o.HaveCount, o.ClaimHave, o.ClaimWant, o.Remaining, o.RatioHave, o.RatioWant, give, get, o.State);
    }

    Task SendOrder(IPlayer player, ExchangeOrder order, string why, bool placed = false) =>
        player.Send(Wire.OrderChanged(ToWire(order, player.League, MarketOf(player)), placed), $"0x02f3 order {order.Id} {order.State}: {why}");

    Task SendOrders(IPlayer player)
    {
        var market = MarketOf(player);
        var orders = BookOf(player).Orders;
        return player.Send(Wire.Orders([.. orders.Select(o => ToWire(o, player.League, market))], (byte)Math.Clamp(Options.OrderSlots, 1, 255)),
            $"0x02e9 {orders.Count} Currency Exchange orders");
    }

    /// <summary>The table's fee by the option: gold per item, or for a cheap item items per gold, which a dearer fee makes fewer.</summary>
    static uint FeeOf(ExchangeItems.Row row) =>
        (uint)Math.Max(1, Math.Round(row.ItemsPerGold ? row.Fee / Math.Max(Options.GoldFee, .01) : row.Fee * Options.GoldFee));

    Task SendFee(IPlayer player, uint item) => ItemsOf(player).ByHash.TryGetValue(item, out var row)
        ? player.Send(Wire.Fee(item, FeeOf(row), row.ItemsPerGold), $"0x02f7 gold fee of {row.Id}: {FeeOf(row)}") : Task.CompletedTask;

    Task SendPartners(IPlayer player, uint item)
    {
        var market = MarketOf(player);
        uint[] partners = market is null ? [] : [.. ItemsOf(player).ByHash.Keys.Where(hash => hash != item && market.Trades(hash))];
        return player.Send(Wire.Partners(item, partners), "0x02f2 what trades against an item");
    }

    Task SendMarket(IPlayer player, (uint Low, uint High) pair)
    {
        var market = MarketOf(player);
        var used = market is null ? null : BookOf(player).UsedAt(market.Hour);
        return player.Send(Wire.Market(pair.Low, pair.High, market?.Levels(pair.Low, pair.High, used: used) ?? [], market?.Levels(pair.High, pair.Low, used: used) ?? []),
            $"0x02f4 market {pair.Low:x8} / {pair.High:x8}");
    }

    async Task Place(IPlayer player, Wire.PlaceOrder request)
    {
        var items = ItemsOf(player);
        if (request.Want == request.Have || request.WantCount is 0 or >= 1_000_000 || request.HaveCount is 0 or >= 1_000_000
            || !items.ByHash.TryGetValue(request.Want, out var want) || !items.ByHash.ContainsKey(request.Have))
        {
            await player.Send(Wire.Error(Wire.ErrorInvalidRequest), "0x02f9 Currency Exchange invalid request");
            return;
        }
        if (MarketOf(player) is not { } market || market.Trade(request.Want, request.Have) is null)
        {
            await player.Send(Wire.Error(Wire.ErrorMarketDisabled), "0x02f9 Currency Exchange market unavailable");
            await player.Tell("Currency Exchange: there is no market for these two items.");
            return;
        }
        uint divisor = (uint)BigInteger.GreatestCommonDivisor(request.WantCount, request.HaveCount);
        uint ratioWant = request.WantCount / divisor, ratioHave = request.HaveCount / divisor;
        if (ratioWant > ExchangeRatio.Max || ratioHave > ExchangeRatio.Max)
        {
            await player.Send(Wire.Error(Wire.ErrorInvalidRequest), "0x02f9 Currency Exchange ratio out of range");
            return;
        }
        if (BookOf(player).Orders.Count >= Options.OrderSlots)
        {
            await player.Send(Wire.Error(Wire.ErrorTooManyOrders), "0x02f9 Currency Exchange too many orders");
            await player.Tell("Currency Exchange: maximum number of active orders reached.");
            return;
        }
        ulong fee = FeeOf(want), gold = want.ItemsPerGold ? (request.WantCount + fee - 1) / fee : request.WantCount * fee;
        if (gold > uint.MaxValue)
        {
            await player.Send(Wire.Error(Wire.ErrorGenericOrderFailure), "0x02f9 Currency Exchange gold fee overflow");
            await player.Tell("Currency Exchange: not enough gold.");
            return;
        }

        ExchangeOrder? placed = null;
        var filled = new List<ExchangeOrder>();
        bool paid = await WorldOf(player).PayCurrency(request.Have, (int)request.HaveCount, (uint)gold, () => Save(player, book =>
        {
            // It meets this hour's market at once, with what the book's other orders left of it.
            placed = new ExchangeOrder
            {
                Id = book.NextId++, Want = request.Want, Have = request.Have, WantCount = request.WantCount, HaveCount = request.HaveCount,
                Remaining = request.HaveCount, RatioWant = (ushort)ratioWant, RatioHave = (ushort)ratioHave, State = Wire.OrderState.Listed,
                LastHour = market.Hour - 3600, PlacedAt = Now,
            };
            book.Orders.Add(placed);
            filled = book.Settle(market, Now);
        }), $"Currency Exchange order, {gold} gold");
        if (!paid)
        {
            var owner = PlayerOf(player);
            var stash = PersonalStashOf(owner);
            var inv = global::Game.ItemsOf(owner);
            bool enoughGold = stash.Gold >= gold;
            long ownedHave = (long)inv.Count(PlayerInventories.MainInventory, request.Have) + stash.CountOf(request.Have);
            bool enoughCurrency = ownedHave >= request.HaveCount;
            if (!enoughCurrency && enoughGold)
            {
                await player.Send(Wire.Error(Wire.ErrorCouldNotFindSellItems), "0x02f9 Currency Exchange insufficient currency");
                await player.Tell("Currency Exchange: not enough currency.");
            }
            else if (!enoughGold && enoughCurrency)
            {
                await player.Send(Wire.Error(Wire.ErrorGenericOrderFailure), "0x02f9 Currency Exchange insufficient gold");
                await player.Tell($"Currency Exchange: not enough gold (need {gold}, have {stash.Gold}).");
            }
            else
            {
                await player.Send(Wire.Error(Wire.ErrorGenericOrderFailure), "0x02f9 Currency Exchange insufficient currency and gold");
                await player.Tell("Currency Exchange: not enough currency or gold.");
            }
            return;
        }
        // He is already speaking, of the placing: what completed now only has its notification.
        await SendOrder(player, placed!, "placed", placed: Options.FaustusSpeaks);
        await player.Send(Wire.QueryOrderResult(placed!.Id, 0, placed.ClaimWant, placed.HaveCount - placed.Remaining),
            $"0x02ef Currency Exchange query order {placed.Id}: {placed.ClaimWant} bought, {placed.HaveCount - placed.Remaining} sold");
        await SendFilled(player, filled.Where(o => o != placed), speak: false);
        if (placed!.State == Wire.OrderState.Completed) await player.Send(Wire.OrderCompleted(), "0x02f8 an order completed");
    }

    /// <summary>What filled, and for what completed one notification and one of Faustus's lines, however many: after a night away they would overlap.</summary>
    async Task SendFilled(IPlayer player, IEnumerable<ExchangeOrder> filled, bool speak)
    {
        bool completed = false;
        foreach (var order in filled)
        {
            await SendOrder(player, order, "filled");
            completed |= order.State == Wire.OrderState.Completed;
        }
        if (!completed) return;
        await player.Send(Wire.OrderCompleted(), "0x02f8 an order completed");
        if (speak && Options.FaustusSpeaks && FaustusLine is var (npc, line))
            await player.Send(InstanceNpcs.TextAudio(0, npc, line), "0x01c7 Faustus: an order is filled");
    }

    /// <summary>What he says of a filled order, from anywhere (S→C 0x1c7, object 0): the client picks among the row's group. Nothing in the client asks for it.</summary>
    static (ushort Npc, ushort Line)? FaustusLine = default!;

    static (ushort Npc, ushort Line)? LoadFaustusLine()
    {
        if (Npcs.Find("Metadata/NPC/League/Kalguur/VillageFaustusHideout") is not { } npc) return null;
        foreach (var line in ClientTables.Table("NPCTextAudio"))
            if (line.TextOrNull("Id") == "FaustusSettlersOrderFilled_Random") return ((ushort)npc.Row, (ushort)line.Index);
        return null;
    }

    /// <summary>The player's X on an order: what is left of it can be claimed. The gold stays paid, as the window warns.</summary>
    async Task Close(IPlayer player, uint id)
    {
        ExchangeOrder? closed = null;
        Save(player, book =>
        {
            if (book.Orders.Find(o => o.Id == id) is not { } order) return;
            closed = order;
            if (order.State == Wire.OrderState.Listed)
            {
                order.ClaimHave = order.Remaining;
                order.Remaining = 0;
                order.State = Wire.OrderState.Cancelled;
            }
            Forget(book, order);
        });
        if (closed is null) return;
        await SendOrder(player, closed, "closed");
        if (BookOf(player).HasClaims) await Light(player);
    }

    /// <summary>An order with nothing left to claim leaves the book and the window.</summary>
    static void Forget(ExchangeBook book, ExchangeOrder order)
    {
        if (order.State == Wire.OrderState.Listed || order.ClaimWant + order.ClaimHave + order.Remaining > 0) return;
        order.State = Wire.OrderState.Removed;
        book.Orders.Remove(order);
    }

    async Task Claim(IPlayer player, Wire.Claim claim)
    {
        // As the window says, nothing is claimed until the order is completed or closed.
        bool bought = claim.Side == 1;
        var owner = PlayerOf(player);
        var inv = global::Game.ItemsOf(owner);

        if (claim.ToCursor)
        {
            InventoryItem? removedCursor = null, addedCursor = null;
            ExchangeOrder? cursorOrder = null;
            bool committed = Saves.Atomically(() =>
            {
                if (BookOf(player).Orders.Find(o => o.Id == claim.Order) is not { } current
                    || current.State == Wire.OrderState.Listed) return false;
                uint item = bought ? current.Want : current.Have;
                uint available = bought ? current.ClaimWant : current.ClaimHave;
                if (available == 0 || ItemsFor(owner).ByHash(item) is not { } catalogType) return false;
                int maxStack = Math.Max(1, (int)catalogType.MaxStack);
                var held = inv.Items(PlayerInventories.Cursor).FirstOrDefault();
                if (held is not null && (held.Item.TypeHash != item || held.Item.StackSize >= maxStack)) return false;
                int room = held is null ? maxStack : maxStack - held.Item.StackSize;
                if (room <= 0) return false;

                uint taken = 0;
                bool savedOk = WorldOf(player).AccountData.Change(SaveKey, json =>
                {
                    var book = ExchangeBook.Parse(json);
                    if (book.TryClaim(claim.Order, bought, (uint)room, out taken) is not { } updated) return null;
                    cursorOrder = updated;
                    return book.Serialize();
                });
                if (!savedOk || taken == 0 || cursorOrder is null) return false;

                var prototype = ItemCatalog.Create(catalogType);
                var shape = global::Game.ShapeOf(prototype);
                if (held is null)
                {
                    var created = prototype with { StackSize = (ushort)taken };
                    if (shape is { } s) created = PlayerInventories.Completed(new InventoryItem(0, 0, 0, created), s).Item;
                    addedCursor = inv.Add(PlayerInventories.Cursor, 0, 0, created);
                }
                else
                {
                    removedCursor = held;
                    addedCursor = inv.Change(PlayerInventories.Cursor, held.Id, cur =>
                    {
                        var grown = cur with { StackSize = (ushort)(cur.StackSize + taken) };
                        return shape is { } s ? PlayerInventories.Completed(held with { Item = grown }, s).Item : grown;
                    });
                }
                if (addedCursor is null)
                    throw new InvalidOperationException("Currency Exchange cursor item update failed after debiting claim");
                return true;
            });
            if (!committed || addedCursor is null) return;
            await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                inv.NextRevision(PlayerInventories.Cursor), removedCursor is null ? [] : [removedCursor.Id], [addedCursor]),
                $"0x00bf cursor: Currency Exchange claim x{addedCursor.Item.StackSize}");
            if (cursorOrder is not null) await SendOrder(player, cursorOrder, "claimed to cursor");
            return;
        }

        // Ctrl+left-click (PressKind == 1) takes one stack to inventory; Ctrl+right-click (PressKind == 0) takes all stacks.
        ExchangeOrder? changed = null;
        ItemCatalog.BaseType? receivedType = null;
        uint wanted = 0;
        var (placed, stacks) = Saves.Atomically(() =>
        {
            if (BookOf(player).Orders.Find(o => o.Id == claim.Order) is not { } current
                || current.State == Wire.OrderState.Listed) return (0, (IReadOnlyList<PlayerInventories.Stacking>)[]);
            uint item = bought ? current.Want : current.Have;
            uint available = bought ? current.ClaimWant : current.ClaimHave;
            if (available == 0 || ItemsFor(owner).ByHash(item) is not { } catalogType)
                return (0, (IReadOnlyList<PlayerInventories.Stacking>)[]);
            receivedType = catalogType;
            int maxStack = Math.Max(1, (int)catalogType.MaxStack);
            wanted = claim.PressKind == 0 ? available : Math.Min(available, (uint)maxStack);
            return ItemMoves.ReceiveCurrency(inv, ItemCatalog.Create(catalogType), (int)wanted, global::Game.ShapeOf, into =>
                WorldOf(player).AccountData.Change(SaveKey, json =>
                {
                    var book = ExchangeBook.Parse(json);
                    if (book.TryClaim(claim.Order, bought, (uint)into, out uint debited) is not { } updated
                        || debited != (uint)into) return null;
                    changed = updated;
                    return book.Serialize();
                }));
        });
        foreach (var stack in stacks)
            await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                inv.NextRevision(PlayerInventories.MainInventory), stack.RemovedIds, stack.AddedItems),
                $"Currency Exchange claim: {placed} {receivedType?.Name}");
        if (wanted > 0 && placed < wanted) await player.Tell("Currency Exchange: not enough room in the inventory.");
        if (changed is not null) await SendOrder(player, changed, "claimed");
    }

    /// <summary>A listed order fills when the prices come to it, wherever the player is, and through the hours they were away.</summary>
    async Task Settle(IPlayer player)
    {
        if (!WorldOf(player).PlayerReady || MarketOf(player) is not { } market) return;
        if (!BookOf(player).SettleDue(market, Now)) return;
        var filled = new List<ExchangeOrder>();
        Save(player, saved => filled = saved.Settle(market, Now));
        await SendFilled(player, filled, speak: true);
    }
}
