# Currency Exchange

Faustus's option (`NPCTalk` `SendOpenScreen( CurrencyExchangeWindow ); FinishNPCChat()`) opens the window with S→C 0x7b **kind 0x5e**: the
value after the name in the screen table at `0x142fd80f0` (24-byte rows: name, 0x16, kind), not the row's index. The window is built by
`0x140b87d10`; `0x140b8ef60` hooks it to the session's lists. Ctrl + click on him is C→S 0x24 with flag 0x0004 (live 2026-11-08: flags 0x040c), and the server opens it at once.

The window asks for everything it shows. No market lives in the client: it computes only the order's gold from 0x2f7.

| C→S | Bytes | Asks | S→C |
|---|---|---|---|
| 0x2e8 | | the orders, once per area, on opening (`0x140b91400`) | 0x2e9 |
| 0x2f6 | u32 item | the gold fee of the "I Want" item | 0x2f7 |
| 0x2f1 | u32 item | what trades against "I Want", while "I Have" is empty | 0x2f2 |
| 0x2f0 | u32 low, u32 high | the market of the two items | 0x2f4 |
| 0x2ea | u32 want, u32 have, u32 want count, u32 have count | places an order | 0x2f3 |
| 0x2eb | u32 order | the X on an order | 0x2f3 |
| 0x2ed | u32 order, u8 side, u8 all, u8, u8, u8 | moves a stack to the inventory (side 1 what was bought, 0 what was sold; 0xfe 0xff: no cell) | 0x2f3 |

All u32 are big-endian, and items are `BaseItemTypes` hashes. Counts are under 1,000,000 and the reduced ratio at most 65000 : 65000: the window checks both.

| S→C | Case | Layout |
|---|---|---|
| 0x2e9 | 0xf2 | compact count, orders, **u8 order slots** (0 reads "No Order Slots Left") |
| 0x2f3 | 0xf5 | order, bool just placed (Faustus says `FaustusSettlersPlaceOrder_Random`, `0x141628180`) |
| 0x2f8 | 0xf9 | empty: an order completed, the notification and its sound (`0x140d96cf0`) |
| 0x2fa | 0xfb | empty: the flag 0x2f8 sets (`+0xd6ca`), with no notification; also a state on an object of the area (`0x1416280e0`). Sent on arriving with orders to claim, for Faustus's bag |
| 0x2f2 | 0xf4 | u32 item, compact count, u32 items **little-endian** (copied from memory) |
| 0x2f4 | 0xf6 | u32 low, u32 high, levels of those selling low, levels of those selling high |
| 0x2f5 | 0xf7 | u8: the order slots again |
| 0x2f7 | 0xf8 | u32 item, u32 fee, bool: gold is want count × fee, or with the bool the count ÷ fee rounded up |
| 0x2f9 | 0xfa | u16: an error; 0x261 is "The Currency Exchange is closed" (the others not mapped) |
| 0x2ef | 0xf3 | debug log only ("CX query order returned …") |

Until 0x2e9 arrives the window reads "The Currency Exchange is closed" (`+0x570`, `0x140b92550`).

A **level** (`0x1421c23c0`) is u16 give, u16 get, u64 little-endian stock, best first, of which the window shows 6: those selling low give that many low for that many high. The
window's market ratio is the first level of those selling "I Want", as want : have (`0x140b94550`). A chosen ratio above it is dimmed: it would not fill.

An **order** (`0x1429adaf0`, 0x58 bytes in memory; offsets are the row's):

| Wire | Offset | Is |
|---|---|---|
| u64 LE | +0x00 | nothing reads it |
| u32 | +0x28 | order id, the key of 0x2eb and 0x2ed |
| wstr | +0x08 | its league, shown when the next bool is false ("Order in a different League") |
| u32 LE | +0x44 | nothing reads it |
| bool | +0x51 | placed in this league |
| u32 | +0x2c | the item sold, with +0x38 |
| u32 | +0x30 | the item bought, with +0x3c |
| u32 | +0x34 | how many sold in all; the row shows (this ÷ +0x48) × +0x4a as bought in all |
| u32 | +0x38, +0x3c | claimable of each |
| u32 | +0x40 | still on the market |
| u16 × 2 | +0x48, +0x4a | its ratio, sold : bought |
| u16 × 2 | +0x4c, +0x4e | the cheapest competing order, sold : bought; 0 hides it |
| u8 | +0x50 | 0 listed, 1 and 2 completed, 3 cancelled, 4 with all three counts 0 removes the row |

A status string's id in the client is its `ClientStrings` row + 1. `FaustusSettlersOrderFilled_Random` is named nowhere in the client: the server says it (S→C 0x1c7, [17](17-npcs.md)).

## Rates

GGG's public `GET https://web.poecdn.com/api/currency-exchange/<hour>` has hourly digests per league and pair, none of the current hour. A digest's lowest and
highest ratios swing widely in one hour (Standard, one hour: 555 to 738 chaos a divine), and an average by chaos follows the dear trades (The Forward Gaze: 4 at 1 chaos, 4 at about 133), so a price is the median by items traded, each hour's chaos and divine trades at their own price. Prices of direct pairs
contradict each other (8 of 133 loops chaos → divine → X → chaos paid over 20% in one hour), so everything is priced in chaos and a trade goes at the two items'
values less the spread, which leaves no loop to profit from. Over a day about half of the 461 divination cards trade at all, so a rare item takes its price from the last
week; the rest show no stock.

Faustus trades at those prices less a spread by the item's weekly volume, 1% to 10%. Hourly prices cannot drive the orders: a week's replay filled 75% of orders to sell a divine at 20% above
the market within a day when any trade touching the price counted, because stray trades reach every price, so buying low and selling high would pay for nothing. Against the 24-hour median, none did.
An hour offers about 10% of the item's average hourly volume on 6 steps, each worse than the last and each with room for at least one of the dearer item, and an order buys down them as far as its ratio allows. Every hour after an order was placed counts, whether or not the player was there: the file
has each hour's price, and the server replays the hours it missed when the player returns.

Nothing in the Gateway contacts GGG: `src/GameServer.Rates` runs elsewhere, and a mod names it with `UseExchangeRates`, with the official league to trade at:
Standard or the current challenge league, the one with the most priced items among the others. Hardcore leagues have too few markets to price on their own.
