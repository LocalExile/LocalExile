using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // Handles the framed messages of one read, ticks taken out (OnClientData):
    // 0x7d (area generated) → the local player id (0x22), the player object (0x2b9), inventories, passive slots, the area transitions,
    // and with Game_GATEWAY_ENTER=1 enter area (0x21) + wake zone; 0x25 (move) → 0x1ab echo;
    // 0x24 on an area transition → change area (0x2b2, then 0x27 with the new instance) or, with Ctrl, the instance chooser (0xae).
    // Anything else is logged in hex: those are the next messages to map.
    internal static async Task OnClientMessages(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, ClientState conn, long readAt = 0)
    {
        foreach (var (id, bytes) in messages)
        {
            if (conn.Closed) return;
            // Immediate transfer ingress cuts the old epoch off instead of replaying overdue old-world damage.
            // Movement/combat handlers also advance atomically inside their mutation boundaries.
            bool transferIngress = id is InstanceTransfer.ChooseInstanceId or InstanceTransfer.LeaveAreaAckId
                or InstanceWaypoints.TravelId or InstanceWaypoints.VisitHideoutId or InstanceHideouts.SelectId
                || id == InstanceProtocol.ResurrectId && InstanceProtocol.ReadResurrectInTown(bytes)
                || id == InstanceItems.CommandId && InstanceItems.ReadCommand(bytes).Id == InstanceItems.WarpToHideoutCommand;
            if (!transferIngress) AdvanceProjectiles(conn);
            // Native TakeHit blocks predicted action starts too. Buffered normal actions must not restart the server's
            // movement/cast timeline during that same recovery. Flasks and instant skills retain their own paths.
            if (id is InstanceProtocol.UseSkillAtLocationId or InstanceTransfer.UseSkillOnObjectId or InstanceProtocol.ChannelUpdateId)
                lock (conn.Monsters)
                    if (conn.HitRecovery.BlocksInput(conn.ClockAt(readAt), conn.Clock.Elapsed.TotalSeconds)) continue;
            if (id == InstanceProtocol.UseSkillAtLocationId)
            {
                // Echo the action so the client runs it (Move on a ground click); an attack gem is paced and may be movement
                // (below). The client starts the action itself and waits for this echo before sending the next one.
                var use = InstanceProtocol.ReadUseSkillAtLocation(bytes);
                double at = conn.ClockAt(readAt);   // when the client applied the click, not when this handler runs
                // A gem skill cast at the ground (Fireball) stops the player where it stands; anything else (Move) walks there.
                if (InTown(conn) && Skills.ActionName(use.SkillId) != "Move") continue;
                if (Skills.ActionName(use.SkillId) != "Move")
                    await EndGracePeriod(remote, stream, ciphers, conn, action: true);
                bool cast = Skills.IsGemSkill(use.SkillId);
                var gem = cast ? GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == use.SkillId) : default;
                if (cast && gem.Name is not null && !ProjectilePathAvailable(remote, conn, use.SkillId, gem.Effect)) continue;
                // A disabled gem's skill is not confirmed: the client animates the cast and sends the use anyway (it only reds
                // the gem), so without the 0x1ab the action does not run. No projectile, mana or cooldown either.
                if (cast && gem.Name is not null
                    && GemSkillInstanceDisabled(conn, gem.Effect, use.SkillId) is { } disabled)
                {
                    Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name}: {disabled} (disabled, action not started)");
                    // The client paid the cast before the server heard of it: the pool again gives the cost back.
                    await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (disabled skill, cost back)");
                    continue;
                }
                // The ordinary attack on a ranged weapon: clicking the ground with the attack skill bound swings at the point
                // and the client draws an arrow to it, instead of walking there (live 2026-10-25: the arrow stayed in the
                // hand, because every non-gem ground click was movement).
                if (use.SkillId == SkillCatalog.FirstSkillId && WeaponProfileFor(conn, activeSet: true).Bow)
                {
                    double duration = WeaponFor(conn).AttackSeconds(CharacterPlayerStats(conn));
                    if (!TryBeginBowAttack(conn, at, duration, use.SkillId, use.Slot, out var bow)) continue;
                    int projectileEpoch;
                    lock (conn.Monsters) projectileEpoch = conn.ProjectileEpoch;
                    // Release work must not block serial input dispatch: resume/move can invalidate a queued echo.
                    _ = ConfirmAndLaunchOrdinaryBow(remote, stream, ciphers, conn,
                        InstanceProtocol.ActionStarted(PlayerId, bow.X, bow.Y, use.X, use.Y, use.SkillId, use.Slot, use.Flags),
                        $"0x01ab bow attack at ({use.X}, {use.Y}) from ({bow.X}, {bow.Y}) (t={at:0.000})",
                        (bow.X, bow.Y), (use.X, use.Y), duration, bow.Action, projectileEpoch);
                    continue;
                }
                // An attack gem (Holy Strike, Ground Slam …) used on the ground with "Always Attack Without Moving" off is an
                // attack-move: the client walks to the aimed point itself and sends no C→S 0x25 Move for that walk, so the
                // server walks its own estimate after it and echoes from there. A point with a living monster on it is a
                // swing (the client aims at the cell, not at the object); an empty point is movement only, so running costs
                // no mana. Before 2026-10-25 every use swung, which paid the cost at the click and echoed the server's stale
                // position, and the client snapped the player back onto it (live trace: up to 98 cells).
                // A ranged weapon (a bow) fires at the point instead: arrows flew nowhere while this branch walked the
                // player, since the projectile is launched by the cast below (live 2026-10-25).
                bool attack = cast && gem.Name is not null && Skills.IsAttack(gem.Effect);
                if (attack && (use.Flags & 0x1) == 0 && !Skills.IsMovement(gem.Effect)
                    && !Skills.IsProjectile(gem.Effect) && WeaponProfileFor(conn).Melee)
                {
                    var here = conn.Walk.Position(at);
                    if (!MonsterAtAim(conn, gem, use.X, use.Y, at) || Dist(here, (use.X, use.Y)) > WeaponFor(conn).Range)
                    {
                        int walkAction = 0;
                        if (!TryBeginSkillCooldownUse(conn, use.SkillId, gem.Effect,
                            CooldownFor(conn, gem.Effect, gem.Level, gem.Quality), false,
                            () => { at = Math.Max(at, ProjectileWorldTime(conn)); walkAction = Interlocked.Increment(ref conn.ActionCount); conn.Walk.Order(use.X, use.Y, at); conn.ChannelSkill = 0; },
                            out var approachTicket, reserve: false, requestedAt: at)) continue;
                        await Send(remote, stream, ciphers,
                            InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, use.X, use.Y, use.SkillId, use.Slot, use.Flags),
                            $"0x01ab {gem.Name}: attack-move to ({use.X}, {use.Y}) from ({here.X}, {here.Y}) (t={at:0.000})",
                            still: () => approachTicket?.Current(conn) ?? PlayerActionCurrent(conn, walkAction));
                        continue;
                    }
                }
                // An attack gem swings with the weapon's attack time, while the client keeps aiming it for as long as the
                // button is held. A use before the next attack is not a new action: it must not cancel the swing in progress
                // (its pending hit), pay its cost or get a 0x1ab (live 2026-10-25).
                if (attack && !AttackReady(conn, gem.Effect, gem.Level, out double attackSeconds))
                {
                    Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name}: ignored, the previous attack is still running (attack time {attackSeconds:0.00} s) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
                    continue;
                }
                int action = 0;
                (int px, int py) = (0, 0);
                CooldownUseTicket? cooldownUse = null;
                void StartAction()
                {
                    at = Math.Max(at, conn.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
                    action = Interlocked.Increment(ref conn.ActionCount);
                    (px, py) = cast ? conn.Walk.Position(at) : conn.Walk.Order(use.X, use.Y, at);
                    if (cast) conn.Walk.Order(px, py, at);
                    // Readiness and reservation precede movement: a raw early Dash cannot teleport anyway.
                    if (gem.Name is not null && Skills.IsMovement(gem.Effect)
                        && (conn.Layout.Walkable is not { } canWalk || canWalk(use.X, use.Y)))
                        conn.Walk.Teleport(use.X, use.Y, at);
                    conn.ChannelSkill = cast ? use.SkillId : (ushort)0;
                    conn.ChannelSkillSequence = use.Slot;
                }
                if (cast && gem.Name is not null)
                {
                    if (!TryBeginSkillCooldownUse(conn, use.SkillId, gem.Effect,
                        CooldownFor(conn, gem.Effect, gem.Level, gem.Quality),
                        false, StartAction, out cooldownUse, requestedAt: at)) continue;
                }
                else
                {
                    using (ProjectileMutation(conn))
                    {
                        if (conn.HitRecovery.BlocksInput(at, conn.Clock.Elapsed.TotalSeconds)) continue;
                        StartAction();
                    }
                }
                bool projectileRuntime = cast && gem.Name is not null && UsesProjectileRuntime(gem.Effect);
                if (projectileRuntime) _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId,
                    (px, py), (use.X, use.Y), sequence: use.Slot, actionToken: cooldownUse?.ActionToken ?? action, cooldownUse: cooldownUse);
                bool acknowledged = false;
                Func<Task> echo = () => Send(remote, stream, ciphers,
                    InstanceProtocol.ActionStarted(PlayerId, px, py, use.X, use.Y, use.SkillId, use.Slot, use.Flags),
                    $"0x01ab action started {use} at ({px}, {py}){(cast ? " (gem skill cast)" : $", walk ends at {conn.Walk.Destination} in {conn.Walk.ArrivesAt - at:0.00} s")} (t={at:0.000})",
                    still: () => acknowledged = OwnAuraAcknowledgementAllowed(conn, gem.Effect, use.SkillId,
                        () => cooldownUse?.CommitNativeUse(conn, use.SkillId) ?? PlayerActionCurrent(conn, action)));
                if (projectileRuntime) ProjectilesFor(remote, stream, ciphers, conn).Enqueue(echo);
                else await echo();
                if (!projectileRuntime && !acknowledged) continue;
                if (cooldownUse?.Current(conn) == false) continue;
                if (cast && !projectileRuntime) _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, (px, py), (use.X, use.Y), sequence: use.Slot,
                    actionToken: cooldownUse?.ActionToken ?? action, cooldownUse: cooldownUse);
                else if (!cast) await FollowRighteousFire(remote, stream, ciphers, conn);
                if (!PlayerActionCurrent(conn, action)) continue;
                string mode = Environment.GetEnvironmentVariable("Game_MOVE_MODE") ?? "echo";
                if (mode == "teleport")
                    await Send(remote, stream, ciphers, InstanceProtocol.Teleport(PlayerId, use.X, use.Y),
                        $"0x01b7 teleport to ({use.X}, {use.Y})", still: () => PlayerActionCurrent(conn, action));
                else if (mode == "move")
                {
                    float speed = float.TryParse(Environment.GetEnvironmentVariable("Game_MOVE_SPEED"), System.Globalization.CultureInfo.InvariantCulture, out var sp) ? sp : 37f;
                    await Send(remote, stream, ciphers, InstanceProtocol.MoveTo(PlayerId, use.X, use.Y, speed),
                        $"0x01ba move to ({use.X}, {use.Y}) speed {speed}", still: () => PlayerActionCurrent(conn, action));
                }
            }
            else if (InstanceProtocol.IsLockstepMovement(id))
            {
                // The client moves this way only in lockstep, which the Gateway does not serve yet (07 §6): say so once per
                // connection.
                if (conn.WarnedLockstep) continue;
                conn.WarnedLockstep = true;
                Console.WriteLine($"[gateway] {remote} <= 0x{id:x4} lockstep movement: the client's Network Mode is lockstep (\"auto\" picks it on localhost); " +
                    "only Predictive is supported, the player cannot move");
                await Send(remote, stream, ciphers, InstanceChat.MessageBox(LockstepWarning), "0x000d message box: lockstep warning");
            }
            else if (id == InstancePause.RequestId)
                await SetPaused(remote, stream, ciphers, conn, InstancePause.ReadRequest(bytes));
            else if (id == InstanceMicrotransactions.RequestId)
            {
                var asked = InstanceMicrotransactions.ReadRequest(bytes);
                Console.WriteLine($"[gateway] {remote} <= 0x01f2 microtransaction lists asked: {asked}");
                await AnswerMtxRequest(remote, stream, ciphers, asked);
            }
            else if (id == InstanceMicrotransactions.SlotId)
                await PickCosmetic(remote, stream, ciphers, conn, InstanceMicrotransactions.ReadSlot(bytes));
            else if (id == InstanceMicrotransactions.SkillPickId)
                await PickSkillCosmetic(remote, stream, ciphers, conn, InstanceMicrotransactions.ReadSkillPick(bytes));
            else if (id == InstanceProtocol.UseInstantSkillAtLocationId)
            {
                var (x, y, skill) = InstanceProtocol.ReadUseInstantSkillAtLocation(bytes);
                Console.WriteLine($"[gateway] {remote} <= 0x0190 instant skill 0x{skill:x4} at ({x}, {y})");
                if (Skills.IsGemSkill(skill)) _ = UseInstantSkillAtLocation(remote, stream, ciphers, conn, skill, (x, y));
            }
            else if (id == InstanceProtocol.UseInstantSkillId)
            {
                // An instant skill (skill_is_instant: Haste, Righteous Fire) has no action: the walk and a pending hit go on,
                // and the client waits for no echo.
                var (target, skill) = InstanceProtocol.ReadUseInstantSkill(bytes);
                var spot = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                Console.WriteLine($"[gateway] {remote} <= 0x0191 instant skill 0x{skill:x4} on 0x{target:x} at ({spot.X}, {spot.Y})");
                if (Skills.IsGemSkill(skill)) _ = UseInstantSkill(remote, stream, ciphers, conn, skill, target, spot);
            }
            else if (id == InstanceTransfer.UseSkillOnObjectId)
            {
                var use = InstanceTransfer.ReadUseSkillOnObject(bytes);
                // In the background: the player may first walk to the target, and the read loop must keep answering ticks.
                _ = OnUseObject(remote, stream, ciphers, conn, use, conn.ClockAt(readAt));
            }
            else if (id == InstanceProtocol.BindSkillId)
            {
                var (slot, skill) = InstanceProtocol.ReadBindSkill(bytes);
                string? entry = SkillBarEntry(conn, skill);
                Console.WriteLine($"[gateway] {remote} <= 0x004e bind skill 0x{skill:x4} ({(entry is null ? "unknown, not saved" : entry == "" ? "empty" : entry)}) to slot {slot}: {Convert.ToHexString(bytes)}");
                if (entry is not null) CharacterSaves.SetSkillBar(CharacterName(conn), slot, entry);
            }
            else if (id == InstanceEntities.AllocatePassiveId)
            {
                var (u2, node, u6) = InstanceEntities.ReadAllocatePassive(bytes);
                Console.WriteLine($"[gateway] {remote} <= 0x0034 allocate passive {node} ({Passives.Find(node)?.Name ?? "unknown"}), " +
                    $"node high16 {u2}, expansion {u6}");
                if (u2 == 0 && u6 == 0)
                    await ChangePassives(remote, stream, ciphers, conn, [node], allocate: true, "Passive Skill Tree");
                else
                {
                    Console.WriteLine($"[gateway] {remote} allocation refused: expanded/32-bit passive ids are not supported");
                    await SendPassives(remote, stream, ciphers, conn);
                }
            }
            else if (id == InstanceEntities.MasteryEffectId)
            {
                var (node, effect) = InstanceEntities.ReadMasteryEffect(bytes);
                await ChangePassives(remote, stream, ciphers, conn, [node], allocate: true, "Passive Skill Tree mastery", effect);
            }
            else if (id == InstanceEntities.RefundPassiveId)
            {
                var (node, expansion, gold) = InstanceEntities.ReadRefundPassive(bytes);
                Console.WriteLine($"[gateway] {remote} <= 0x0036 refund passive {node} ({(node <= ushort.MaxValue ? Passives.Find((ushort)node)?.Name : null) ?? "unknown"})" +
                    $"{(expansion ? ", expansion node" : "")}, {(gold ? "gold respec" : "refund points")}: {Convert.ToHexString(bytes)}");
                if (!expansion && node <= ushort.MaxValue)
                {
                    if (gold)
                        await ChangePassives(remote, stream, ciphers, conn, [(ushort)node], allocate: false, "Respecialisation", goldRespec: true);
                    else
                        await ChangePassives(remote, stream, ciphers, conn, [(ushort)node], allocate: false, "Refund points", refundPoints: true);
                }
                else
                {
                    // Cluster jewel nodes do not exist here: put the tree back.
                    Console.WriteLine($"[gateway] {remote} refund refused: expansion nodes are not supported");
                    await SendPassives(remote, stream, ciphers, conn);
                }
            }
            else if (id == InstanceEntities.RefundAllPassivesId)
            {
                // "Refund All" (after the client's Yes): every passive but the ascendancy ones, for their full gold cost.
                var nodes = PassiveState(conn).Nodes.Where(n => Passives.Find(n) is not { IsAscendancy: true }).ToList();
                Console.WriteLine($"[gateway] {remote} <= 0x0037 refund all passives ({nodes.Count})");
                await ChangePassives(remote, stream, ciphers, conn, nodes, allocate: false, "Respecialisation, Refund All", goldRespec: true);
            }
            else if (id == InstanceEntities.PanelToggleId)
                Console.WriteLine($"[gateway] {remote} <= 0x0123 panel {(bytes[2] != 0 ? "opened" : "closed")}");
            else if (id == InstanceItems.DropItemId)
                await DropItem(remote, stream, ciphers, conn);
            else if (id == InstanceItems.CommandId)
            {
                var command = InstanceItems.ReadCommand(bytes);
                if (command.Id == InstanceItems.DestroyCursorItemCommand) await DestroyHeldItem(remote, stream, ciphers, conn);
                else if (command.Id == InstanceItems.WarpToHideoutCommand) await WarpToHideout(remote, stream, ciphers, conn, "/hideout");
                else Console.WriteLine($"[gateway] {remote} <= 0x0016 command {command} (not handled)");
            }
            else if (id == InstanceWaypoints.VisitHideoutId)
            {
                var visit = InstanceWaypoints.ReadVisitHideout(bytes);
                Console.WriteLine($"[gateway] {remote} <= 0x017a visit the hideout of {visit.Character} (from object 0x{visit.Waypoint:x}, u8 {visit.Flag})");
                // Only the player's own hideout exists (no parties or other players' hideouts yet).
                await WarpToHideout(remote, stream, ciphers, conn, "World panel Hideout button");
            }
            else if (id == InstanceItems.UnsocketGemId)
                await UnsocketGem(remote, stream, ciphers, conn, InstanceItems.ReadUnsocketGem(bytes));
            else if (id == InstanceItems.SocketGemId)
                await SocketGem(remote, stream, ciphers, conn, InstanceItems.ReadSocketGem(bytes));
            else if (id == InstanceItems.LevelUpGemId)
                await LevelUpGem(remote, stream, ciphers, conn, InstanceItems.ReadLevelUpGem(bytes));
            else if (id == InstanceItems.LockGemId)
                await LockGem(remote, stream, ciphers, conn, InstanceItems.ReadLockGem(bytes));
            else if (id == InstanceItems.PlaceItemId)
                await PlaceItem(remote, stream, ciphers, conn, InstanceItems.ReadPlaceItem(bytes));
            else if (id == InstanceItems.ItemActionId)
                await HeldOntoItem(remote, stream, ciphers, conn, InstanceItems.ReadItemAction(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceItems.SplitStackId)
                await SplitStack(remote, stream, ciphers, conn, InstanceItems.ReadSplitStack(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceItems.UseItemId)
                await UseItem(remote, stream, ciphers, conn, InstanceItems.ReadUseItem(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceItems.UseFlaskId)
                await UseFlask(remote, stream, ciphers, conn, InstanceItems.ReadUseFlask(bytes));
            else if (id == InstanceItems.UseItemOnId)
                await UseItemOn(remote, stream, ciphers, conn, InstanceItems.ReadUseItemOn(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceItems.UseItemTypeOnId)
            {
                // Shift held: the currency stays on the cursor and is named by type; use a stack of it from the inventories,
                // the main inventory first.
                var again = InstanceItems.ReadUseItemTypeOn(bytes);
                var items = ItemsOf(conn);
                byte[] rows = [PlayerInventories.MainInventory, .. Enumerable.Range(0, InstanceInventories.PlayerDefaults.Length).Select(r => (byte)r)];
                var stack = rows.Select(r => (Row: r, Item: items.Items(r).FirstOrDefault(i => i.Item.TypeHash == again.TypeHash))).FirstOrDefault(s => s.Item is not null);
                // None in the inventories: the currency was taken from the open stash, so use a stack of it from a stash tab.
                var stash = StashOf(conn);
                (int Tab, InventoryItem Item)? inStash = null;
                for (int t = 0; stack.Item is null && inStash is null && t < stash.Count; t++)
                    if (stash.Inventory(t).Items.FirstOrDefault(i => i.Item.TypeHash == again.TypeHash) is { } found) inStash = (t, found);
                if (inStash is var (stashTab, stashItem))
                    await UseItemOn(remote, stream, ciphers, conn, new InstanceItems.UseItemOn(stash.InventoryOf(stashTab), stashItem.Id,
                        again.TargetInventoryId, again.TargetId), Convert.ToHexString(bytes));
                else if (stack.Item is null) Console.WriteLine($"[gateway] {remote} <= 0x0072 use type 0x{again.TypeHash:x8} again: none left");
                else await UseItemOn(remote, stream, ciphers, conn, new InstanceItems.UseItemOn(PlayerInventories.InventoryId(stack.Row), stack.Item.Id,
                    again.TargetInventoryId, again.TargetId), Convert.ToHexString(bytes));
            }
            else if (id == InstanceItems.InventoryRequestId)
                Console.WriteLine($"[gateway] {remote} <= 0x{id:x4} (inventory request, not handled yet): {Convert.ToHexString(bytes)}");
            else if (id == InstanceItems.PickUpItemId)
                await PickUpItem(remote, stream, ciphers, conn, InstanceItems.ReadPickUpItem(bytes));
            else if (id == InstanceProtocol.ExitToCharacterSelectId)
            {
                // Esc → "Exit to Character Selection" or the hardcore death screen's Exit. Closing the instance connection
                // is the answer: the client keeps its login connection and goes back to the character list (09 §6).
                Console.WriteLine($"[gateway] {remote} <= 0x0153 exit to character selection: closing the instance connection");
                lock (conn.Monsters)
                {
                    conn.Closed = true;
                    conn.InvalidateIncomingHits();
                }
                return;
            }
            else if (id == InstanceProtocol.ResurrectId)
                _ = Resurrect(remote, stream, ciphers, conn, InstanceProtocol.ReadResurrectInTown(bytes));
            else if (id == InstanceNpcs.DialogueClosedId)
                await CloseNpcDialogue(remote, stream, ciphers, conn);
            else if (id == InstanceNpcs.TradeOfferId && conn.Trade is { } offerTrade)
            {
                var offer = InstanceNpcs.ReadTradeOffer(bytes);
                await PlayerTradeOffer(remote, conn, offerTrade, offer.InventoryId, offer.ItemId, offer.X, offer.Y);
            }
            else if (id is InstanceNpcs.TradeTakeBackId or InstanceNpcs.TradeMoveAllId && conn.Trade is { } moveTrade)
            {
                var (item, inventory) = InstanceNpcs.ReadTradeMove(bytes);
                await PlayerTradeOffer(remote, conn, moveTrade, inventory, item);
            }
            else if (id == InstanceNpcs.TradeAcceptId && conn.Trade is { } acceptTrade)
                await PlayerTradeAccept(remote, conn, acceptTrade, InstanceNpcs.ReadTradeAccept(bytes));
            else if (id == InstanceNpcs.TradeUnacceptId && conn.Trade is { } unacceptTrade)
                await PlayerTradeUnaccept(conn, unacceptTrade);
            else if (id == InstanceNpcs.TradeAnswerId && conn.Trade is not null)
                await CancelPlayerTrade(conn, "cancelled");
            else if (id == InstanceNpcs.TradeAnswerId && conn.TradeRequestTo != 0)
                await CancelTradeRequest(conn);
            else if (id == InstanceNpcs.TradeAnswerId && InstanceNpcs.ReadTradeAnswer(bytes).Other != 0 && conn.Sale is null)
                await DeclineTradeRequest(conn, InstanceNpcs.ReadTradeAnswer(bytes).Other);
            else if (id == InstanceSocial.TradeRequestAcceptId)
                await AcceptTradeRequest(remote, conn, InstanceSocial.ReadTradeRequestAccept(bytes));
            else if (id == InstanceNpcs.TradeOfferId)
            {
                var offer = InstanceNpcs.ReadTradeOffer(bytes);
                // Ctrl + click on an inventory item sends x = y = 0xff (0x14030e3e0 → 0x142166660): any free offer cell.
                if (conn.Sale is { } sale && offer.X == 0xff && offer.Y == 0xff) await OfferFromInventory(remote, stream, ciphers, conn, sale, offer.InventoryId, offer.ItemId);
                else if (conn.Sale is { } open) await OfferForSale(remote, stream, ciphers, conn, open, offer.InventoryId, offer.ItemId, offer.X, offer.Y);
                else Console.WriteLine($"[gateway] {remote} <= 0x00b2 trade offer {offer}: no sale open");
            }
            else if (id is InstanceNpcs.TradeTakeBackId or InstanceNpcs.TradeMoveAllId)
            {
                var (item, inventory) = InstanceNpcs.ReadTradeMove(bytes);
                Console.WriteLine($"[gateway] {remote} <= 0x{id:x4} trade move item {item} (inventory {inventory})");
                if (conn.Sale is not { } sale) Console.WriteLine($"[gateway] {remote} trade move: no sale open");
                else if (ItemsOf(conn).Offered().Any(i => i.Id == item)) await TakeFromSale(remote, stream, ciphers, conn, sale, item);
                else await OfferFromInventory(remote, stream, ciphers, conn, sale, inventory, item);
            }
            else if (id == InstanceNpcs.TradeAcceptId)
            {
                uint revision = InstanceNpcs.ReadTradeAccept(bytes);
                Console.WriteLine($"[gateway] {remote} <= 0x00b6 trade accept (their offer revision {revision}, ours {conn.Sale?.TheirsRevision})");
                await CloseSale(remote, stream, ciphers, conn, accepted: conn.Sale is { } s && s.TheirsRevision == revision);
            }
            else if (id == InstanceNpcs.TradeUnacceptId)
                Console.WriteLine($"[gateway] {remote} <= 0x00b7 trade un-accept: {Convert.ToHexString(bytes)}");
            else if (id == InstanceNpcs.TradeAnswerId)
                await AnswerSale(remote, stream, ciphers, conn, InstanceNpcs.ReadTradeAnswer(bytes));
            else if (id == InstanceNpcs.BuyId)
                await BuyFromShop(remote, stream, ciphers, conn, InstanceNpcs.ReadBuy(bytes));
            else if (id == InstanceStash.RequestTabId)
            {
                var (tab, kind) = InstanceStash.ReadRequestTab(bytes);
                await SendStashTab(remote, stream, ciphers, conn, tab, kind);
            }
            else if (id == InstanceStash.ReorderTabsId)
            {
                // The tab buttons' order, kept with the stash (personal or guild by kind) and sent back in the tab lists.
                var (kind, order) = InstanceStash.ReadReorderTabs(bytes);
                var target = kind == InstanceStash.Guild ? GuildStashOf(conn) : PersonalStashOf(conn);
                int moved = target.SetPositions(order);
                Console.WriteLine($"[gateway] {remote} <= 0x00cc stash {kind} tab order: {string.Join(", ", order.Select(o => $"{o.Index}->{o.Position}"))} ({moved} moved, saved)");
            }
            else if (id == InstanceProtocol.ChannelUpdateId)
            {
                // A channelled skill (Cyclone) goes on where the player now is: it moves there. Its hits run on their own
                // timer (RunChannel), at the skill's attack rate, wherever the player is.
                var (cx, cy) = InstanceProtocol.ReadChannelUpdate(bytes);
                double t = conn.ClockAt(readAt);
                using (ProjectileMutation(conn))
                {
                    t = Math.Max(t, conn.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
                    if (conn.HitRecovery.BlocksInput(t, conn.Clock.Elapsed.TotalSeconds)) continue;
                    conn.Walk.Teleport(cx, cy, t);
                    conn.LastChannelUpdateAt = conn.Clock.Elapsed.TotalSeconds;
                }
            }
            else if (id == InstanceProtocol.ChannelEndId)
                conn.ChannelSkill = 0;
            else if (id == InstanceProtocol.SwapWeaponsId)
            {
                // The weapon swap: the hands' rows and the other set's trade their items (the client shows the active set in
                // the hands' rows), each of the four inventories gets its new contents (0xbf), S→C 0xde gives the set (which
                // also ends the client's wait), then what the hands hold, with the new set's stats and skills (13-items.md).
                var swapItems = ItemsOf(conn);
                byte wanted = (byte)(bytes[2] == 1 ? 1 : 0);
                if (wanted != swapItems.WeaponSet)
                {
                    var before = new[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1, PlayerInventories.Weapon2, PlayerInventories.Offhand2 }
                        .ToDictionary(r => r, swapItems.Items);
                    swapItems.SwapWeaponSets();
                    swapItems.WeaponSet = wanted;
                    foreach (var (row, old) in before)
                    {
                        uint inv = PlayerInventories.InventoryId(row);
                        var now = swapItems.Items(row);
                        if (old.Count + now.Count == 0) continue;
                        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inv, swapItems.NextRevision(row), [.. old.Select(i => i.Id)], now),
                            $"0x00bf inventory {inv}: weapon swap, {old.Count} out, {now.Count} in");
                    }
                }
                await Send(remote, stream, ciphers, InstanceProtocol.WeaponSet(swapItems.WeaponSet == 1), $"0x00de weapon set {swapItems.WeaponSet + 1}");
                await SendEquipment(remote, stream, ciphers, conn, swapItems);
            }
            else if (id == InstanceStash.RenameTabId)
            {
                // A premium tab renamed in the tab settings; kept with the stash (personal or guild), account-wide.
                var (kind, tab, name) = InstanceStash.ReadRenameTab(bytes);
                bool done = (kind == InstanceStash.Guild ? GuildStashOf(conn) : PersonalStashOf(conn)).Rename(tab, name);
                Console.WriteLine($"[gateway] {remote} <= 0x00cd stash {kind} tab {tab} renamed \"{name}\": {(done ? "saved" : "refused")}");
                await SendTabListAgain(remote, stream, ciphers, conn, kind);
            }
            else if (id == InstanceStash.ColourTabId)
            {
                var (kind, tab, colour) = InstanceStash.ReadColourTab(bytes);
                bool done = (kind == InstanceStash.Guild ? GuildStashOf(conn) : PersonalStashOf(conn)).SetColour(tab, colour);
                Console.WriteLine($"[gateway] {remote} <= 0x00ce stash {kind} tab {tab} colour 0x{colour:x8}: {(done ? "saved" : "refused")} ({Convert.ToHexString(bytes)})");
                await SendTabListAgain(remote, stream, ciphers, conn, kind);
            }
            else if (id == InstanceStash.MoveToTabId)
                await MoveToStash(remote, stream, ciphers, conn, InstanceStash.ReadMoveToTab(bytes));
            else if (id == InstanceStash.MoveToAffinityTabId)
                await MoveToAffinityTab(remote, stream, ciphers, conn, InstanceStash.ReadMoveToAffinityTab(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceStash.SetAffinitiesId)
                await SetTabAffinities(remote, stream, ciphers, conn, InstanceStash.ReadSetAffinities(bytes));
            else if (id == InstanceNpcs.CtrlBuyId)
                await CtrlBuyFromShop(remote, stream, ciphers, conn, InstanceNpcs.ReadCtrlBuy(bytes));
            else if (id == InstanceStash.TakeFromTabId)
            {
                var move = InstanceStash.ReadTakeFromTab(bytes);
                Console.WriteLine($"[gateway] {remote} <= 0x00e3 take item {move.ItemId} from stash tab {move.Tab} " +
                    $"({move.Kind:x2} {move.TokenLo:x2} {move.TokenHi:x2} {move.Bulk:x2}, {(move.Bulk != 0 ? "bulk" : "one stack")})");
                await TakeFromStash(remote, stream, ciphers, conn, move.Tab, move.ItemId, all: move.Bulk != 0);
            }
            else if (id == InstanceMapDevice.ActivateId)
                await ActivateMapDevice(remote, stream, ciphers, conn, InstanceMapDevice.ReadActivate(bytes));
            else if (id == InstanceStash.MoveTabId)
                Console.WriteLine($"[gateway] {remote} <= 0x00d1 stash tab into a folder (not handled): {Convert.ToHexString(bytes)}");
            else if (id == InstanceNpcs.OptionId)
                await ChooseNpcOption(remote, stream, ciphers, conn, InstanceNpcs.ReadOption(bytes));
            else if (id == InstanceHideoutEditing.MoveId)
                await MoveHideoutObject(remote, stream, ciphers, conn, InstanceHideoutEditing.ReadMove(bytes));
            else if (id == InstanceHideouts.SelectId)
                await SelectHideout(remote, stream, ciphers, conn, InstanceHideouts.ReadSelect(bytes));
            else if (id == InstanceBench.CraftId)
                await CraftBench(remote, stream, ciphers, conn, InstanceBench.ReadCraft(bytes));
            else if (id == InstanceHarvest.CraftId)
                await CraftHarvest(remote, stream, ciphers, conn, InstanceHarvest.ReadCraft(bytes));
            else if (id == InstanceChat.ChatId)
            {
                var chat = InstanceChat.ReadChat(bytes);
                Console.WriteLine($"[gateway] {remote} <= 0x0015 chat \"{chat.Text}\" (u8 {chat.U8})");
                if (chat.IsCommand) await conn.Commands!.RunAsync(chat.Text);
                else await EchoChat(remote, stream, ciphers, conn, chat.Text);
            }
            else if (id == InstanceChat.JoinChannelId)
            {
                if (InstanceChat.ReadJoinChannel(bytes) is { } join) await JoinChatChannel(remote, stream, ciphers, conn, join);
                else Console.WriteLine($"[gateway] {remote} <= 0x0134 join with an unknown channel kind, ignored: {Convert.ToHexString(bytes)}");
            }
            else if (id == InstanceTransfer.ChooseInstanceId)
            {
                byte row = bytes[2];
                if (conn.Chooser is not { } chooser) { Console.WriteLine($"[gateway] {remote} <= 0x00af row {row} without a list"); continue; }
                conn.Chooser = null;
                bool isNew = row == InstanceTransfer.NewInstanceRow;
                if (!isNew && row >= chooser.Ids.Count) { Console.WriteLine($"[gateway] {remote} <= 0x00af row {row} out of range"); continue; }
                ulong instance = isNew ? 0 : chooser.Ids[row];
                Console.WriteLine($"[gateway] {remote} <= 0x00af chose {(isNew ? "a new instance" : $"instance {instance}")} of 0x{chooser.Area:x4}");
                await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(chooser.Area, instance, isNew, AtWaypoint: chooser.AtWaypoint), $"chooser row {row}");
            }
            else if (id == InstanceWaypoints.TravelId)
                _ = TravelByWaypoint(remote, stream, ciphers, conn, InstanceWaypoints.ReadTravel(bytes));
            else if (id == InstanceSocial.TradeRequestId)
                await RequestTrade(remote, stream, ciphers, conn, InstanceSocial.ReadTradeRequest(bytes));
            else if (id == InstanceSocial.PartyInviteId)
                Console.WriteLine($"[gateway] {remote} <= 0x0093 party invite to \"{InstanceSocial.ReadPartyInvite(bytes)}\" (not handled yet)");
            else if (id == InstanceSocial.FriendAddId)
                Console.WriteLine($"[gateway] {remote} <= 0x0080 add friend \"{InstanceSocial.ReadFriendAdd(bytes).Name}\" (not handled yet)");
            else if (id == InstanceProtocol.AreaGeneratedId)
            {
                Console.WriteLine($"[gateway] {remote} <= 0x007d area generated: {Convert.ToHexString(bytes)}");
                if (!conn.PlayerSent)
                {
                    // First visit with scripts/walkmaps running: the map is written while the client generates the area.
                    if (conn.Layout.Walkable is null && await AreaLayouts.WaitFor(conn.Layout, conn.Seed) is { } fresh)
                    {
                        conn.Use(fresh);
                        Console.WriteLine($"[gateway] {remote} walk map captured: {fresh.Describe()}");
                    }
                    if (conn.Layout.AreaTransitions.Length == 0 && await AreaLayouts.WaitForTransitions(conn.Layout, conn.Seed) is { } withTransitions)
                    {
                        conn.Use(withTransitions);
                        Console.WriteLine($"[gateway] {remote} area transitions captured: {withTransitions.Describe()}");
                    }
                    // Waypoint travel: next to the destination's waypoint instead of the area's spawn. A generated area's
                    // waypoint is only known from the walk-map recorder's capture for this seed (live.ps1 -Walkmaps).
                    // Back through a town portal: where the portal stands (OpenTownPortal).
                    if (conn.ArriveAt is var (sx, sy))
                    {
                        conn.Use(conn.Layout with { SpawnX = sx, SpawnY = sy });
                        Console.WriteLine($"[gateway] {remote} back at the portal ({sx}, {sy})");
                    }
                    else if (conn.ArriveAtWaypoint && conn.Layout.Waypoint is { } arrival)
                    {
                        var (ax, ay) = conn.Layout.Near((arrival.GridX, arrival.GridY));
                        conn.Use(conn.Layout with { SpawnX = ax, SpawnY = ay });
                        Console.WriteLine($"[gateway] {remote} arrived by waypoint: spawn ({ax}, {ay}) next to the waypoint at ({arrival.GridX}, {arrival.GridY})");
                    }
                    // Otherwise next to the exit back; Resurrect at Checkpoint and the monster packs' clearance use it too.
                    else if (conn.Layout.Arrival(conn.CameFrom) is var (ex, ey) && (ex, ey) != (conn.Layout.SpawnX, conn.Layout.SpawnY))
                    {
                        conn.Use(conn.Layout with { SpawnX = ex, SpawnY = ey });
                        Console.WriteLine($"[gateway] {remote} arrival from 0x{conn.CameFrom:x4}: ({ex}, {ey})");
                    }
                    else if (conn.ArriveAtWaypoint)
                        Console.WriteLine($"[gateway] {remote} arrived by waypoint, but {conn.Layout.Id} seed {conn.Seed} has no captured waypoint " +
                            $"(run live.ps1 -Walkmaps): spawn ({conn.Layout.SpawnX}, {conn.Layout.SpawnY}){(conn.Layout.Walkable is null ? ", no walk map either: the player may stand outside the map" : "")}");
                    RecordVisit(conn);
                    await SendPlayer(remote, stream, ciphers, conn);
                    conn.PlayerSent = true;
                    await StartGracePeriod(remote, stream, ciphers, conn);
                    await SendWaypoints(remote, stream, ciphers, conn);
                    uint gold = PersonalStashOf(conn).Gold;
                    await Send(remote, stream, ciphers, InstanceEntities.Gold(gold), $"0x02a9 gold: {gold} (account, {conn.League ?? "Standard"})");
                    // The worn cosmetics (S→C 0x1f3): the client's worn table starts empty with every instance connection.
                    await SendWornCosmetics(remote, stream, ciphers, conn, "entering the area");
                    // The saved skill bar (S→C 0x53), right after the player and the area.
                    await SendSkillBar(remote, stream, ciphers, conn);
                    // The chat channels joined earlier in the session (Program.Chat.cs).
                    await SendChatChannels(remote, stream, ciphers, conn);
                    // The equipment again (S→C 0x02ba), as after an equip: the player object's own list does not set how a
                    // two-handed weapon is held, the update does (live 2026-10-22: it was held one-handed after every load).
                    var worn = Looks(conn, ItemsOf(conn));
                    await Send(remote, stream, ciphers, InstanceEntities.PlayerEquipmentUpdate(PlayerId, worn),
                        $"0x02ba player equipment on entering: {string.Join(", ", worn.Select(e => $"slot {e.Slot} stance {e.Stance}"))}");
                    await PopulateArea(remote, stream, ciphers, conn);
                    await SpawnPets(remote, stream, ciphers, conn);
                    await SendGroundItems(remote, stream, ciphers, conn);
                    await PlaceStartWeapon(remote, stream, ciphers, conn);
                    await SpawnTownNpcs(remote, stream, ciphers, conn);
                    await SpawnTownStash(remote, stream, ciphers, conn);
                    await SpawnQuestRoomObjects(remote, stream, ciphers, conn);
                    await SpawnOpeningTutorial(remote, stream, ciphers, conn);
                    await SpawnGlyphQuest(remote, stream, ciphers, conn);
                    await SpawnHideoutDoodads(remote, stream, ciphers, conn);
                    // All stationary objects must exist before validating the arrival cell.
                    await PushOutOfCollision(remote, stream, ciphers, conn);
                    await SpawnMapReturnPortal(remote, stream, ciphers, conn);
                    await SpawnTownPortals(remote, stream, ciphers, conn);
                    _ = RunMonsterRegeneration(remote, stream, ciphers, conn);
                    await SyncQuestItems(remote, stream, ciphers, conn, removeItems: true);   // a1q5: the Medicine Chest's flag
                    _ = RunMercyMission(remote, stream, ciphers, conn);
                    _ = RunServerFrames(remote, stream, ciphers, conn);
                    _ = RunMonsterAi(remote, stream, ciphers, conn);
                    _ = RunLivingCollisions(remote, stream, ciphers, conn);
                    _ = RunPlayerTick(remote, stream, ciphers, conn);
                    _ = RunResourceRecovery(remote, stream, ciphers, conn);
                    _ = RunCharges(remote, stream, ciphers, conn);
                    _ = RunFortification(remote, stream, ciphers, conn);
                    _ = RunRage(remote, stream, ciphers, conn);
                    _ = RunFlaskChargeRegen(remote, stream, ciphers, conn);
                }
            }
        }
        // a1q5: after a read that moved items, the Medicine Chest's flag (and its removal once Nessa has one), here on the
        // message path so the inventory revisions stay in order.
        if (conn.PlayerSent && messages.Any(m => MercyMission.MovesItems(m.Id))) await SyncQuestItems(remote, stream, ciphers, conn, removeItems: true);
        if (unknown.Length > 0)
        {
            ushort uid = unknown.Length > 1 ? (ushort)(unknown[0] << 8 | unknown[1]) : unknown[0];
            Console.WriteLine($"[gateway] {remote} <= unframed 0x{uid:x4} ({unknown.Length} bytes): {Convert.ToHexString(unknown.AsSpan(0, Math.Min(unknown.Length, 256)))}");
            // C→S 0x23f: the Delve chart (opened by Niko) asks for its data; S→C 0x240 answers (an empty chart for now).
            if (uid == InstanceDelve.ChartRequestId && unknown.Length == 2)
            {
                var (a, b, cells) = conn.DelveChartProbe;
                await Send(remote, stream, ciphers, InstanceDelve.Chart(a, b, cells),
                    $"0x0240 Delve chart: {a}, {b}, {cells.Count} cell(s) {string.Join(" ", cells)}");
            }
            // The client's answer to 0x2b2 (0x142148690): it is ready to leave, so move it now.
            if (uid == InstanceTransfer.LeaveAreaAckId && conn.PendingTransfer is { } t) await Transfer(remote, stream, ciphers, conn, t);
        }
    }
}
