using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.CosmeticsAndPets;

/// <summary>Microtransaction lists, picking cosmetics, and pets.</summary>
public sealed class CosmeticsModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.PlayerEntered.Add(module.Manifest.Id, player =>
        {
            return CosmeticsFeature.SpawnPets(player);
        });
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) =>
        {
            // A transfer request can fail. Remove the old area's pets once the connection actually closes.
            if (player.Closed) CosmeticsFeature.RemoveDepartedPets(player);
            return Task.CompletedTask;
        });
        GameEvents.LocalGateUsed.Add(module.Manifest.Id, CosmeticsFeature.FollowThroughGate);
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((player, _) => CosmeticsFeature.StepPetFollow(player)));
        GameEvents.Packet(InstanceMicrotransactions.RequestId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            var asked = InstanceMicrotransactions.ReadRequest(bytes);
            module.Log($"{PlayerOf(player).Character}: microtransaction lists asked: {asked}");
            await CosmeticsFeature.AnswerMtxRequest(player, asked);
            return true;
        });
        GameEvents.Packet(InstanceMicrotransactions.SlotId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await CosmeticsFeature.PickCosmetic(player, InstanceMicrotransactions.ReadSlot(bytes));
            return true;
        });
        GameEvents.Packet(InstanceMicrotransactions.SkillPickId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await CosmeticsFeature.PickSkillCosmetic(player, InstanceMicrotransactions.ReadSkillPick(bytes));
            return true;
        });
    }
}

public static class CosmeticsFeature
{
    // The Microtransactions panel, the store and the hideout picker ask for their lists with C→S 0x1f2 and show "Loading…"
    // until each one arrives (23-microtransactions.md). Every account owns every microtransaction; the hideout and guild
    // personal hideout list includes all exported templates; the guild list stays empty.
    internal static async Task AnswerMtxRequest(Player player, InstanceMicrotransactions.Lists asked)
    {
        var owned = Mtx.Owned;
        if (asked.HasFlag(InstanceMicrotransactions.Lists.Records))
            await Send(player, InstanceMicrotransactions.Added(owned, InstanceMicrotransactions.Records),
                $"0x01f4 microtransactions: {owned.Count} owned (the panel's records)");
        if (asked.HasFlag(InstanceMicrotransactions.Lists.StoreOwned))
            await Send(player, InstanceMicrotransactions.Added(owned, InstanceMicrotransactions.StoreOwned),
                $"0x01f4 microtransactions: {owned.Count} owned (the store's list)");
        if (asked.HasFlag(InstanceMicrotransactions.Lists.GuildStore))
            await Send(player, InstanceMicrotransactions.Added([], InstanceMicrotransactions.StoreSecond),
                "0x01f4 microtransactions: the guild's list, empty");
        if (asked.HasFlag(InstanceMicrotransactions.Lists.Hideouts))
            await Send(player, InstanceMicrotransactions.Hideouts(HideoutCatalog.OwnedRows),
                $"0x01f5 owned hideouts: all {HideoutCatalog.OwnedRows.Length} templates including MTX");
        if (asked.HasFlag(InstanceMicrotransactions.Lists.GuildHideouts))
            await Send(player, InstanceMicrotransactions.Hideouts([], guild: true), "0x01f5 guild hideouts: none");
    }

    // C→S 0x1fa: a pick in one of the Cosmetics tab's skill slots (record 0 empties it). Saved for the slot's gem type and
    // effect slot type, then the skill slots again (what the client shows worn) and the skill list, so the gem's skill
    // carries the effect's stats (skill_art_variation: the art the client draws from the skill, and Righteous Fire's
    // server-made art the next time it is cast).
    internal static async Task PickSkillCosmetic(Player player, (uint Key, uint RecordId) pick)
    {
        InstanceMicrotransactions.SkillSlot slot;
        InstanceMicrotransactions.Record? record;
        RighteousFireState? expectedRelight;
        using (ProjectileMutation(player, advance: false, phase: "skill cosmetic pick"))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea)
                return;
            record = pick.RecordId == 0 ? null : Mtx.Find(pick.RecordId);
            if (!player.Character.SkillSlots.TryGetValue(pick.Key, out var current))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x01fa skill cosmetic pick: key {pick.Key}, record {pick.RecordId}: stale target or incompatible record, ignored");
                return;
            }
            slot = current!;
            if (!SkillCosmeticPlaces(player).Contains((slot.Inventory, slot.Socket, slot.GemHash, slot.SlotType))
                || (pick.RecordId != 0 && (record is null || ItemsFor(player).ByHash(slot.GemHash) is not { } gem
                    || !MtxEffects.FitsSkill(record.MtxTypeHash, gem, slot.SlotType))))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x01fa skill cosmetic pick: key {pick.Key}, record {pick.RecordId}: stale target or incompatible record, ignored");
                return;
            }
            expectedRelight = player.RighteousFire;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x01fa skill cosmetic pick: key {pick.Key} (gem 0x{slot.GemHash:x8}, slot type {slot.SlotType}), " +
            $"record {pick.RecordId} (hash 0x{record?.MtxTypeHash ?? 0:x8}, stats {string.Join(", ", (record is null ? null : MtxEffects.Of(record.MtxTypeHash))?.Stats.Select(st => $"{Stats.Name(st.Stat)} {st.Value}") ?? [])})");
        CharacterSaves.SetSkillCosmetic(CharacterName(player), slot.GemHash, slot.SlotType, record is null ? null : new CharacterSaves.SavedCosmetic(record.Id, 0, record.MtxTypeHash));
        await SendWornCosmetics(player, "after the skill pick");
        await SendSkills(player);
        // A Righteous Fire already burning from this gem takes the new art at once: casting again while it burns does
        // nothing, so it is lit again with the new ring and buff visuals only while the exact pick-time owner still exists.
        await RelightRighteousFire(player, "restyled by the skill pick", expectedRelight, slot.GemHash);
        // Active reservation skills (Tempest Shield, auras, heralds, Arctic Armour) likewise re-emit their buffs with
        // the new art variation / hide flag so the pick applies immediately.
        await RefreshReservationCosmetics(player, slot.GemHash);
    }

    // C→S 0x1f9: a pick in the Cosmetics tab's slot flyout (record 0 empties the slot). Saved per character, then the whole
    // worn table goes back in 0x1f3, which is what the client wears. The Portal slot also sets the portals' own variation
    // (the same one /cosmetic portal sets), so the map device's portals show the picked effect.
    internal static async Task PickCosmetic(Player player, (byte Slot, uint RecordId, byte Option) pick)
    {
        var record = pick.RecordId == 0 ? null : Mtx.Find(pick.RecordId);
        if (pick.Slot == 0 || pick.Slot >= InstanceMicrotransactions.SlotCount || (pick.RecordId != 0 && record is null))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x01f9 cosmetic pick: slot {pick.Slot}, record {pick.RecordId}: not a slot or not a record, ignored");
            return;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x01f9 cosmetic pick: slot {pick.Slot}, record {pick.RecordId} (hash 0x{record?.MtxTypeHash ?? 0:x8}), option {pick.Option}");
        string character = CharacterName(player);
        var morph = WornMorph(player);
        // The Portal slot's two choices that are no cosmetic (Game.PortalVariation): record 0 with option 1 or 2.
        bool randomPortals = record is null && pick.Slot == InstanceMicrotransactions.PortalSlot && pick.Option is 1 or 2;
        CharacterSaves.SetCosmetic(character, pick.Slot, randomPortals ? new CharacterSaves.SavedCosmetic(0, pick.Option)
            : record is null ? null : new CharacterSaves.SavedCosmetic(record.Id, pick.Option, record.MtxTypeHash));
        player.Character.WornCosmetics = null;
        if (pick.Slot == InstanceMicrotransactions.PortalSlot)
        {
            ushort variation = record is not null && Cosmetics.Portals.FirstOrDefault(p => p.Hash == record.MtxTypeHash) is { } portal ? (ushort)portal.Index : (ushort)0;
            player.Character.CosmeticPortalVariation = variation;
            CharacterSaves.SetPortalCosmetic(character, variation);
            player.Character.WornCosmetics = null;
            await ResendMapPortals(player);
        }
        await SendWornCosmetics(player, "after the pick");
        // The model and the stats: skins, attachments, character effects, footprints, apparitions.
        await SendEquipment(player, ItemsOf(player));
        // A skin that turns the character into another creature in towns and hideouts (the Goblin Shaman Skin).
        await SendMorph(player, morph);
        RefreshFlaskEffectCosmetic(player, pick.Slot);
        if (pick.Slot is 18 or 19) await SpawnPets(player);
        if (pick.Slot == PortraitSlot)
        {
            ushort portrait = WornPortrait(player);
            await global::Game.SendWorldRecord(player, player.Id, InstanceEntities.PlayerPortraitUpdate(player.Id, portrait), $"0x02bc player portrait {portrait}");
            // The character screen's list comes from Auth's account store.
            var told = await InstanceControl.CallAsync(new ControlRequest { Op = "set_portrait", Ticket = player.Ticket, Portrait = portrait });
            if (!told.Ok) Console.WriteLine($"[gateway] {player.Connection.Remote} set_portrait {portrait}: {told.Error}");
        }
        if (pick.Slot == SocialFrameSlot)
        {
            // Unlike the portrait this is not sent to the client now: the frame applies at the next login, as the line the
            // client prints for the pick says (0x140cdd510).
            ushort frame = WornFrame(player);
            var told = await InstanceControl.CallAsync(new ControlRequest { Op = "set_portrait_frame", Ticket = player.Ticket, PortraitFrame = frame });
            if (!told.Ok) Console.WriteLine($"[gateway] {player.Connection.Remote} set_portrait_frame {frame}: {told.Error}");
        }
    }

    // The worn pets (Pet slots 18 and 19): one PetObject each beside the player, owned by it (PetAi). A pick removes the
    // old ones first. StepPetFollow walks them after the player.
    internal static async Task SpawnPets(Player player)
    {
        foreach (uint id in TakePetIds(player))
            await global::Game.SendWorldRemoval(player, id, $"0x{InstanceProtocol.RemoveObjectId:x4} remove pet 0x{id:x}");
        double now = AreaTime(player);
        var (px, py) = player.Walk.Position(now);
        foreach (var worn in WornCosmetics(player).Where(w => w.Slot is 18 or 19))
        {
            if (player.Closed) break;
            if (MtxEffects.Of(worn.MtxTypeHash) is not { PetType: { Length: > 0 } type } fx) continue;
            if (!PetObject.CanSerialize(fx.PetComponents, fx.PetStates is not null))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} pet {type}: components {string.Join(", ", fx.PetComponents ?? [])} not supported, not spawned");
                continue;
            }
            var (x, y) = PetSpot(player, (px, py), worn.Slot == 18 ? 1 : -1);
            uint id = player.TakeObjectId();
            var pet = new PetObject(x, y, player.Id, fx.PetComponents, fx.PetStates);
            if (!await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), pet.Blob()),
                $"0x02bb create pet 0x{id:x} {type} at ({x}, {y})")) continue;
            if (player.Closed) { RemoveDepartedPet(player, id); break; }
            lock (player.Pets) player.Pets[id] = new SpawnedPet(player.NewWalk(x, y, player.Walk.Speed), worn.Slot == 18 ? 1 : -1);
            await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), $"0x01a9 pet 0x{id:x} enters wake zone");
        }
    }

    static uint[] TakePetIds(Player player)
    {
        lock (player.Pets)
        {
            uint[] ids = [.. player.Pets.Keys];
            player.Pets.Clear();
            return ids;
        }
    }

    static void RemoveDepartedPet(Player player, uint id)
    {
        var remove = InstanceProtocol.RemoveObject(id);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(remove.AsSpan(6), uint.MaxValue);
        player.Area.Delta.RemoveSharedObject(id, remove, $"0x02bd remove pet 0x{id:x} (owner left the area)");
    }

    internal static void RemoveDepartedPets(Player player)
    {
        foreach (uint id in TakePetIds(player)) RemoveDepartedPet(player, id);
    }

    // Where a pet stands by the player: PetDistance cells to the player's side (side +1 or −1, the two Pet slots), on the
    // walk map; the player's own cell if that side is blocked.
    internal static (int X, int Y) PetSpot(Player player, (int X, int Y) playerAt, int side)
    {
        (int X, int Y) spot = (playerAt.X + side * PetDistance, playerAt.Y + PetDistance / 2);
        return player.Layout.Walkable is { } w && !w(spot.X, spot.Y) ? playerAt : spot;
    }

    internal static async Task FollowThroughGate(Player player)
    {
        uint[] ids;
        lock (player.Pets) ids = [.. player.Pets.Keys];
        foreach (var id in ids)
            await global::Game.SendWorldRecord(player, id, () =>
            {
                lock (player.Pets)
                {
                    if (player.Closed || player.LeavingArea || !player.Pets.TryGetValue(id, out var pet)) return null;
                    double now = AreaTime(player);
                    var to = PetSpot(player, player.Walk.Position(now), pet.Side);
                    pet.Walk.Teleport(to.X, to.Y, now);
                    pet.Target = to;
                    return InstanceProtocol.Teleport(id, to.X, to.Y);
                }
            }, "pet follows owner through local gate");
    }

    // One finite walk. Ordinary following resumes after arrival, including when the caller has gone away.
    public static Task<bool> WalkPetTo(Player player, uint id, (int X, int Y) to, Func<bool>? still = null) =>
        global::Game.SendWorldRecord(player, id, () =>
        {
            lock (player.Pets)
            {
                if (player.Closed || player.LeavingArea || player.Paused || !player.Pets.TryGetValue(id, out var pet)) return null;
                pet.Target = null; // No owner-follow destination while this walk is running.
                return PetMove(player, id, pet, to, AreaTime(player));
            }
        }, still: still);

    internal static byte[] PetMove(Player player, uint id, SpawnedPet pet, (int X, int Y) to, double now, bool following = false)
    {
        int percent = ApplyPetSpeed(player, id, pet, now);
        var from = pet.Walk.Order(to.X, to.Y, now);
        // Local gates can put the owner in a separate walkable component. A finite loot walk must stop
        // where its path ends, but a following pet rejoins its owner when that path falls short.
        if (following && Dist(pet.Walk.Destination, to) > PetFollowRange)
        {
            pet.Walk.Teleport(to.X, to.Y, now);
            global::Game.SetStats(player, id, [(Stat.MovementVelocityPlusPct, percent)]);
            return InstanceProtocol.Teleport(id, to.X, to.Y);
        }
        global::Game.SetStats(player, id, [(Stat.MovementVelocityPlusPct, percent)]);
        return [.. InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y, 0x2909, 0xffff, 0x0408)];
    }

    static int ApplyPetSpeed(Player player, uint id, SpawnedPet pet, double now)
    {
        double speed = GameEvents.ResolveMovementSpeed(player, id, Math.Max(player.Walk.Speed, 1));
        // Metadata/Pet/Pet.ot has Pathfinding base_speed 37. The native final stat is an integer percent;
        // quantize the estimate to that same speed rather than letting pickup run ahead of the visible pet.
        int percent = (int)Math.Round(Math.Clamp((speed / 37 - 1) * 100, -99, int.MaxValue - 100d));
        double resolved = 37 * (1 + percent / 100d);
        if (pet.Walk.Speed != resolved) pet.Walk.SetSpeed(resolved, now);
        return percent;
    }

    internal static void PetSpeedUpdate(Player player, uint id, SpawnedPet pet, double now)
    {
        double previous = pet.Walk.Speed;
        int percent = ApplyPetSpeed(player, id, pet, now);
        if (previous != pet.Walk.Speed) global::Game.SetStats(player, id, [(Stat.MovementVelocityPlusPct, percent)]);
    }

    // Pets follow the player, every frame (GameEvents.AreaTick): when one is farther than PetFollowRange from the player
    // it walks (0x1ab Move, as monsters do) to its spot beside the player, which keeps it near but never on top of the
    // player. On the tick rather than a loop of the module's own, so a reload's new module walks them and the old one stops.
    internal static async Task StepPetFollow(Player player)
    {
        try
        {
            lock (player.Pets)
                if (!player.Closed && !player.LeavingArea)
                    foreach (var (id, pet) in player.Pets) PetSpeedUpdate(player, id, pet, AreaTime(player));
            double now = AreaTime(player);
            var playerAt = player.Walk.Position(now);
            var orders = new List<(uint Id, (int X, int Y) To, byte[] Move)>();
            lock (player.Pets)
                foreach (var (id, pet) in player.Pets)
                {
                    if (pet.Target is null && now <= pet.Walk.ArrivesAt + PlayerTickMs / 1000d) continue;
                    var here = pet.Walk.Position(now);
                    if (Dist(here, playerAt) <= PetFollowRange) continue;
                    var to = PetSpot(player, playerAt, pet.Side);
                    if (pet.Target is { } last && Dist(last, to) < 3) continue;   // already on its way there
                    pet.Target = to;
                    orders.Add((id, to, PetMove(player, id, pet, to, now, following: true)));
                }
            foreach (var (id, to, move) in orders)
                await global::Game.SendWorldRecord(player, id, [], null, atGate: () =>
                {
                    lock (player.Pets) return player.Pets.TryGetValue(id, out var pet) && pet.Target == to ? move : null;
                });
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection closed while sending.
        }
    }
}
