using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using static Program;
using static VendorsFeature;

namespace GameServer.Modules.M_vendors;

/// <summary>NPC shops: buying, and selling to a vendor.</summary>
public sealed class VendorsModule : IModule
{
    /// <summary>This module's game data, built in a fixed order when it loads: before the Gateway listens, as the
    /// Gateway's own (WorldLoader). Tests that use the module call it once.</summary>
    public static void LoadData()
    {
        global::VendorsRules.LoadRulesNpcsData();
        global::Gambling.LoadGameData();
        // Every gem stock a league of the normal data set can ask for, with and without the support gem rewards (the
        // HardMode ones are built with that data set: Game.LoadHardModeData).
        foreach (bool supports in new[] { true, false })
            GemVendorStock.For(Program.Items, global::Game.Npcs, Program.Skills, false, supports);
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        DivinationFeature.Load(module);
        GameEvents.NpcTalk.Add(module.Manifest.Id, async (player, id, npc) =>
        {
            player.Shop = null;
            return await BeginLillyGemIntroduction(player, id, npc);
        });
        GameEvents.Command(module.Manifest.Id, "vendor_refresh", "/vendor_refresh (refresh the open Kirac shop for its progression-based Gold cost)",
            (player, args) => RefreshKirac(player));
        GameEvents.MonsterKilled.Add(module.Manifest.Id, kill =>
        {
            var player = kill.Player;
            if (kill.Monster.Rarity == GameServer.Core.Protocol.Instance.MonsterRarity.Unique
                && global::Game.MapRunOf(player) is { Atlas: true } map && map.Owner.Equals(CharacterName(player), StringComparison.OrdinalIgnoreCase))
                KiracProgress.Complete(PersonalStashOf(player),map.Area.Id,map.Area.Tier);
            return Task.CompletedTask;
        });
        GameEvents.NpcOption.Add(module.Manifest.Id, async talk =>
        {
            var player = talk.Player;
            SpawnedNpc? npc;
            lock (player.Npcs) npc = player.Npcs.GetValueOrDefault(player.TalkingTo);
            if (npc is null) return false;
            if (talk.Category == "BuyItems" && Gambling.Gambles(npc.Npc.Id)) await OpenGamble(player, player.TalkingTo, npc);
            else if (talk.Category == "BuyItems") await OpenNpcShop(player, player.TalkingTo, npc);
            else if (talk.Category == "SellItems") await OpenNpcSale(player, npc);
            else return false;
            return true;
        });
        GameEvents.NpcDialogueClosing.Add(module.Manifest.Id, async player =>
        {
            await FinishLillyGemIntroduction(player);
            player.Shop = null;
            if (player.Sale is not null) await CloseSale(player, accepted: false);
        });
        GameEvents.InventoryPick.Add(module.Manifest.Id, async (player, pick) =>
        {
            if (player.Sale is not { } sale || pick.InventoryId != sale.Mine) return false;
            await TakeFromSale(player, sale, pick.ItemId);
            return true;
        });
        GameEvents.InventoryPlace.Add(module.Manifest.Id, async (player, place) =>
        {
            if (player.Sale is not { } sale || place.InventoryId != sale.Mine) return false;
            uint cursor = PlayerInventories.InventoryId(PlayerInventories.Cursor);
            var held = ItemsOf(player).Items(PlayerInventories.Cursor).FirstOrDefault();
            if (held is not null) await OfferForSale(player, sale, cursor, held.Id, (byte)place.X, (byte)place.Y);
            return true;
        });
        GameEvents.Packet(InstanceNpcs.TradeOfferId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            // A trade between players has these messages first.
            if (player.Trade is not null) return false;
            var offer = InstanceNpcs.ReadTradeOffer(bytes);
            // Ctrl + click on an inventory item sends x = y = 0xff (0x14030e970 → 0x14216d010): any free offer cell.
            if (player.Sale is { } sale && offer.X == 0xff && offer.Y == 0xff) await OfferFromInventory(player, sale, offer.InventoryId, offer.ItemId);
            else if (player.Sale is { } open) await OfferForSale(player, open, offer.InventoryId, offer.ItemId, offer.X, offer.Y);
            else Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00b2 trade offer {offer}: no sale open");
            return true;
        });
        foreach (ushort id in new[] { InstanceNpcs.TradeTakeBackId, InstanceNpcs.TradeMoveAllId })
            GameEvents.Packet(id).Add(module.Manifest.Id, async (player, bytes) =>
            {
            // A trade between players has these messages first.
            if (player.Trade is not null) return false;
                var (item, inventory) = InstanceNpcs.ReadTradeMove(bytes);
                if (player.Sale is not { } sale) Console.WriteLine($"[gateway] {player.Connection.Remote} trade move: no sale open");
                else if (ItemsOf(player).Offered().Any(i => i.Id == item)) await TakeFromSale(player, sale, item);
                else await OfferFromInventory(player, sale, inventory, item);
                return true;
            });
        GameEvents.Packet(InstanceNpcs.TradeCtrlMoveId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            // A trade between players has these messages first.
            if (player.Trade is not null) return false;
            var (itemId, inventory, _) = InstanceNpcs.ReadTradeCtrlMove(bytes);
            if (player.Sale is not { } sale)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00b4 trade ctrl-move: no sale open");
                return true;
            }
            var items = ItemsOf(player);
            if (items.Offered().FirstOrDefault(i => i.Id == itemId) is { } offeredTarget)
            {
                uint type = offeredTarget.Item.TypeHash;
                uint[] ids = [itemId, .. items.Offered().Where(i => i.Id != itemId && i.Item.TypeHash == type)
                    .OrderBy(i => i.Y).ThenBy(i => i.X).Select(i => i.Id)];
                bool moved = false;
                foreach (uint id in ids)
                {
                    if (player.Sale != sale) break;
                    int before = items.Offered().Count;
                    await TakeFromSale(player, sale, id, sendReward: false);
                    if (items.Offered().Count != before) moved = true;
                }
                if (moved && player.Sale == sale) await SendSaleReward(player, sale);
                return true;
            }
            byte? row = PlayerInventories.Row(inventory);
            var clicked = row is byte r ? items.Items(r).FirstOrDefault(i => i.Id == itemId) : null;
            if (clicked is null) return true;
            uint matchType = clicked.Item.TypeHash;
            uint[] toOffer = [itemId, .. items.Items(row!.Value).Where(i => i.Id != itemId && i.Item.TypeHash == matchType)
                .OrderBy(i => i.Y).ThenBy(i => i.X).Select(i => i.Id)];
            bool offeredAny = false;
            foreach (uint id in toOffer)
            {
                if (player.Sale != sale) break;
                int before = items.Offered().Count;
                await OfferFromInventory(player, sale, inventory, id, sendReward: false);
                if (items.Offered().Count == before) break;
                offeredAny = true;
            }
            if (offeredAny && player.Sale == sale) await SendSaleReward(player, sale);
            return true;
        });
        GameEvents.Packet(InstanceNpcs.TradeAcceptId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            // A trade between players has these messages first.
            if (player.Trade is not null) return false;
            uint revision = InstanceNpcs.ReadTradeAccept(bytes);
            await CloseSale(player, accepted: player.Sale is { } s && s.TheirsRevision == revision);
            return true;
        });
        GameEvents.Packet(InstanceNpcs.TradeAnswerId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            // A trade between players has this message first.
            if (player.Trade is not null || player.TradeRequestTo != 0 || (InstanceNpcs.ReadTradeAnswer(bytes).Other != 0 && player.Sale is null)) return false;
            await AnswerSale(player, InstanceNpcs.ReadTradeAnswer(bytes));
            return true;
        });
        GameEvents.Packet(InstanceNpcs.BuyId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await BuyFromShop(player, InstanceNpcs.ReadBuy(bytes));
            return true;
        });
        GameEvents.Packet(InstanceNpcs.BuyAndSocketId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await BuyAndSocketFromShop(player, InstanceNpcs.ReadBuyAndSocket(bytes));
            return true;
        });
        GameEvents.Packet(InstanceNpcs.CtrlBuyId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await CtrlBuyFromShop(player, InstanceNpcs.ReadCtrlBuy(bytes));
            return true;
        });
        GameEvents.Packet(InstanceNpcs.TakeAllRewardsId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await ClaimAllNpcRewards(player);
            return true;
        });
        GameEvents.VendorQuote.Add(module.Manifest.Id, (player, offered) =>
        {
            try
            {
                var quote = VendorsRules.VendorSales.Evaluate(offered, global::Game.AreaRng(player),
                    basicSaleMultiplier: LeagueFor(player).Rules.VendorRewardMultiplier);
                return new VendorQuoteResult(quote.Rewards, quote.Recipes);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
            {
                return new VendorQuoteResult([], [], ex.Message);
            }
        });
    }
}
