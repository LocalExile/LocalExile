using System.Buffers.Binary;
using System.Text;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Settlers of Kalguur / Kingsmarch (<c>Rulesets.dat</c> row 155 = <c>0x9b</c>, <c>VillageRules</c>) and Recombinator
/// wire codecs for 3.29.3c. Outer envelopes are big-endian; all fields inside S→C <c>0x02c9</c> and C→S <c>0x02ca</c>
/// payloads are little-endian.
/// </summary>
public static class InstanceVillage
{
    /// <summary><c>Rulesets.dat</c> row 155 (<c>VillageRules</c>).</summary>
    public const byte RulesetRow = 0x9b;

    /// <summary>Sentinel for "no job / no port / no failure" in Village wire structs.</summary>
    public const byte NoneRow = 0xff;

    /// <summary>Sentinel for an empty farm plot in <see cref="FullVillageState.FarmPlotCropResource"/> (<c>VillageResources</c> count).</summary>
    public const byte EmptyFarmPlot = 21;

    public const int ResourceCount = 21;
    public const int ProductionCount = 15;
    public const int FarmPlotCount = 6;
    public const int ShipCount = 3;
    public const int MapDeviceCount = 3;
    public const int ExportCount = 16;
    public const int QuotasPerPort = 3;

    // S→C 0x007b OpenScreen kinds (screen table 0x142fcd700 / jump table 0x14216bb74).
    public const byte RecombinatorWindowUi = 0x54;
    public const byte RecruitmentWindowUi = 0x55;
    public const byte TownMapWindowUi = 0x56;
    public const byte MineWindowUi = 0x57;
    public const byte PortWindowUi = 0x58;
    public const byte FarmWindowUi = 0x59;
    public const byte BlackSmithWindowUi = 0x5a;
    public const byte DisenchantingWindowUi = 0x5b;
    public const byte MapRunnersWindowUi = 0x5c;
    public const byte FishingWindowUi = 0x5d;
    public const byte CurrencyExchangeWindowUi = InstanceCurrencyExchange.WindowUi;

    // S→C 0x001e RulesetEvent names handled by ClientVillage::OnRulesetEvent (0x14176a8e0).
    public const string EventVillageImGui = "village_imgui";
    public const string EventRerollRecruits = "reroll_recruits";
    public const string EventSuperRerollRecruits = "super_reroll_recruits";
    public const string EventCheckRewardIconVisibility = "check_reward_icon_visibility";
    public const string EventNpcNotificationTriggered = "npc_notification_triggered";

    // Recombinator opcodes (Inventories 92 = RecombinatorInput, 93 = RecombinatorOutput).
    public const ushort RecombinatorQuickMoveId = 0x010b;
    public const int RecombinatorQuickMoveLength = 9;
    public const ushort RecombinateId = 0x028f;
    public const ushort RecombinationResultId = 0x0291;
    public const ushort RecombinationChanceId = 0x0293;

    /// <summary>S→C 0x001f attaching <c>VillageRules</c> (<c>0x9b</c>).</summary>
    public static byte[] AttachRuleset() => InstanceRulesets.Attach(RulesetRow);

    /// <summary>S→C 0x001e named ruleset event.</summary>
    public static byte[] RulesetEvent(string name, uint entityId = 0) => InstanceRulesets.Event(name, entityId);

    // ---- S→C 0x02c9 subtags (ClientVillage::OnServerMessage @ 0x141767f60) ---------------------------------------------

    public enum ServerOp : byte
    {
        FullWorkerList = 0x00,
        RecruitPool = 0x01,
        ShipUpdate = 0x02,
        PortQuotas = 0x03,
        FullVillageState = 0x04,
        AddWorker = 0x05,
        RemoveWorker = 0x06,
        SetWorkerJob = 0x07,
        ActiveRewardNotice = 0x08,
        ShippingRewardItems = 0x09,
        MappingRewardItems = 0x0a,
        ToastNotificationA = 0x0b,
        ToastNotificationB = 0x0c,
        MapDeviceCurrentMapName = 0x0d,
    }

    public readonly record struct WorkerSkill(byte JobTypeRow, byte Rank);

    /// <summary>Shared <c>WorkerCore</c> (<c>0x1424c7ca0</c>): <c>Words.dat</c> keys, hourly wage, and job skills.</summary>
    public sealed record WorkerCore(uint FirstNameWordKey, uint LastNameWordKey, ushort WagePerHour, IReadOnlyList<WorkerSkill> Skills);

    /// <summary>Shared <c>HiredWorker</c> (<c>0x1424c7f80</c>): <see cref="WorkerCore"/> plus <c>VillageJobs</c> row (0..30, or <c>0xFF</c>).</summary>
    public sealed record HiredWorker(WorkerCore Core, byte AssignedJobRow = NoneRow);

    /// <summary>Recruit entry in subtag <c>0x01</c> (<c>0x141773300</c>).</summary>
    public sealed record RecruitEntry(WorkerCore Core, uint HireCost);

    /// <summary>One port quota slot in subtag <c>0x03</c>. Wire layout swaps bits[0..27] and bits[28..55] vs client memory (<c>0x1417685b0</c>).</summary>
    public readonly record struct PortQuotaSlot(byte ExportRow, uint Required, uint Delivered)
    {
        public ulong PackWire() =>
            ((ulong)(ExportRow & 0x0f) << 56) | ((ulong)(Required & 0x0fffffffu) << 28) | (Delivered & 0x0fffffffu);
    }

    public readonly record struct TimerState(uint Elapsed, uint Total);

    /// <summary>Map device state inside subtag <c>0x04</c> (<c>0x1424c8020</c>).</summary>
    public sealed record MapDeviceState(uint Elapsed, uint Total, byte StateFlags1, byte StateFlags2, IReadOnlyList<byte> MapCompletionPercents);

    /// <summary>Full Kingsmarch snapshot for subtag <c>0x04</c> (<c>0x1424c8190</c> + outer tail <c>0x1417683b4</c>).</summary>
    public sealed record VillageStateSnapshot(
        IReadOnlyList<uint> Resources,
        IReadOnlyList<uint> ProductionProgress,
        uint GoldInTreasury,
        uint GoldBurnRemainderOrTicks,
        IReadOnlyList<byte> FarmPlotCropResource,
        IReadOnlyList<TimerState> ShipTimers,
        IReadOnlyList<MapDeviceState> MapDevices,
        TimerState DisenchantTimer,
        byte NibbleLo,
        byte NibbleHi,
        ulong StateTimestamp,
        byte ShipKidnapSlot1Based,
        byte ShipKidnapFlag,
        byte MapKidnapSlot1Based,
        byte MapKidnapFlag,
        ulong ServerNowSeconds);

    static byte[] Wrap(BlobWriter w) => InstanceRulesets.Data(RulesetRow, w.ToArray());

    static void WriteU64(BlobWriter w, ulong value) => w.U32((uint)value).U32((uint)(value >> 32));

    static void WriteWorkerCore(BlobWriter w, WorkerCore worker)
    {
        w.U32(worker.FirstNameWordKey).U32(worker.LastNameWordKey).U16(worker.WagePerHour);
        int count = Math.Min(worker.Skills.Count, byte.MaxValue);
        w.U8((byte)count);
        for (int i = 0; i < count; i++)
            w.U8(worker.Skills[i].JobTypeRow).U8(worker.Skills[i].Rank);
    }

    static void WriteHiredWorker(BlobWriter w, HiredWorker worker)
    {
        WriteWorkerCore(w, worker.Core);
        w.U8(worker.AssignedJobRow);
    }

    /// <summary>S→C 0x02c9 subtag <c>0x00</c> FullWorkerList.</summary>
    public static byte[] FullWorkerList(IReadOnlyList<HiredWorker> workers)
    {
        int count = Math.Min(workers.Count, byte.MaxValue);
        var w = new BlobWriter().U8((byte)ServerOp.FullWorkerList).U8((byte)count);
        for (int i = 0; i < count; i++) WriteHiredWorker(w, workers[i]);
        return Wrap(w);
    }

    /// <summary>S→C 0x02c9 subtag <c>0x01</c> RecruitPool.</summary>
    public static byte[] RecruitPool(byte flag, IReadOnlyList<RecruitEntry> recruits)
    {
        int count = Math.Min(recruits.Count, byte.MaxValue);
        var w = new BlobWriter().U8((byte)ServerOp.RecruitPool).U8(flag).U8((byte)count);
        for (int i = 0; i < count; i++)
        {
            WriteWorkerCore(w, recruits[i].Core);
            w.U32(recruits[i].HireCost);
        }
        return Wrap(w);
    }

    /// <summary>S→C 0x02c9 subtag <c>0x02</c> ShipUpdate.</summary>
    public static byte[] ShipUpdate(byte shipIndex, byte portRow, byte failureRow, IReadOnlyList<uint> cargoExports)
    {
        var w = new BlobWriter().U8((byte)ServerOp.ShipUpdate).U8(shipIndex).U8(portRow).U8(failureRow);
        for (int i = 0; i < ExportCount; i++)
            w.U32(i < cargoExports.Count ? cargoExports[i] : 0u);
        return Wrap(w);
    }

    /// <summary>S→C 0x02c9 subtag <c>0x03</c> PortQuotas (<paramref name="portIndex"/> is 0..5 = portRow - 1).</summary>
    public static byte[] PortQuotas(byte portIndex, IReadOnlyList<PortQuotaSlot> quotas)
    {
        var w = new BlobWriter().U8((byte)ServerOp.PortQuotas).U8(portIndex);
        for (int i = 0; i < QuotasPerPort; i++)
            WriteU64(w, i < quotas.Count ? quotas[i].PackWire() : 0ul);
        return Wrap(w);
    }

    /// <summary>S→C 0x02c9 subtag <c>0x04</c> FullVillageState (including trailing <c>serverNowSeconds</c>).</summary>
    public static byte[] FullVillageState(VillageStateSnapshot state)
    {
        var w = new BlobWriter().U8((byte)ServerOp.FullVillageState);
        for (int i = 0; i < ResourceCount; i++)
            w.U32(i < state.Resources.Count ? state.Resources[i] : 0u);
        for (int i = 0; i < ProductionCount; i++)
            w.U32(i < state.ProductionProgress.Count ? state.ProductionProgress[i] : 0u);
        w.U32(state.GoldInTreasury).U32(state.GoldBurnRemainderOrTicks);
        for (int i = 0; i < FarmPlotCount; i++)
            w.U8(i < state.FarmPlotCropResource.Count ? state.FarmPlotCropResource[i] : EmptyFarmPlot);
        for (int i = 0; i < ShipCount; i++)
        {
            var t = i < state.ShipTimers.Count ? state.ShipTimers[i] : default;
            w.U32(t.Elapsed).U32(t.Total);
        }
        for (int i = 0; i < MapDeviceCount; i++)
        {
            var d = i < state.MapDevices.Count ? state.MapDevices[i] : new MapDeviceState(0, 0, 0, 0, []);
            int mapCount = Math.Min(d.MapCompletionPercents.Count, byte.MaxValue);
            w.U32(d.Elapsed).U32(d.Total).U8((byte)mapCount).U8(d.StateFlags1).U8(d.StateFlags2);
            for (int j = 0; j < mapCount; j++) w.U8(d.MapCompletionPercents[j]);
        }
        w.U32(state.DisenchantTimer.Elapsed).U32(state.DisenchantTimer.Total);
        w.U8(state.NibbleLo).U8(state.NibbleHi);
        WriteU64(w, state.StateTimestamp);
        w.U8(state.ShipKidnapSlot1Based).U8(state.ShipKidnapFlag)
         .U8(state.MapKidnapSlot1Based).U8(state.MapKidnapFlag);
        WriteU64(w, state.ServerNowSeconds);
        return Wrap(w);
    }

    /// <summary>S→C 0x02c9 subtag <c>0x05</c> AddWorker.</summary>
    public static byte[] AddWorker(HiredWorker worker)
    {
        var w = new BlobWriter().U8((byte)ServerOp.AddWorker);
        WriteHiredWorker(w, worker);
        return Wrap(w);
    }

    /// <summary>S→C 0x02c9 subtag <c>0x06</c> RemoveWorker.</summary>
    public static byte[] RemoveWorker(byte workerIndex) =>
        Wrap(new BlobWriter().U8((byte)ServerOp.RemoveWorker).U8(workerIndex));

    /// <summary>S→C 0x02c9 subtag <c>0x07</c> SetWorkerJob.</summary>
    public static byte[] SetWorkerJob(byte workerIndex, byte villageJobRow) =>
        Wrap(new BlobWriter().U8((byte)ServerOp.SetWorkerJob).U8(workerIndex).U8(villageJobRow));

    /// <summary>S→C 0x02c9 subtag <c>0x08</c> ActiveRewardNotice (0 clears; >0 sets active notice to value - 1).</summary>
    public static byte[] ActiveRewardNotice(byte value) =>
        Wrap(new BlobWriter().U8((byte)ServerOp.ActiveRewardNotice).U8(value));

    /// <summary>S→C 0x02c9 subtag <c>0x09</c> ShippingRewardItems.</summary>
    public static byte[] ShippingRewardItems(byte shipSlotIndex, IReadOnlyList<uint> itemIds)
    {
        int count = Math.Min(itemIds.Count, byte.MaxValue);
        var w = new BlobWriter().U8((byte)ServerOp.ShippingRewardItems).U8(shipSlotIndex).U8((byte)count);
        for (int i = 0; i < count; i++) w.U32(itemIds[i]);
        return Wrap(w);
    }

    /// <summary>S→C 0x02c9 subtag <c>0x0a</c> MappingRewardItems.</summary>
    public static byte[] MappingRewardItems(byte mapDeviceIndex, IReadOnlyList<uint> itemIds)
    {
        int count = Math.Min(itemIds.Count, byte.MaxValue);
        var w = new BlobWriter().U8((byte)ServerOp.MappingRewardItems).U8(mapDeviceIndex).U8((byte)count);
        for (int i = 0; i < count; i++) w.U32(itemIds[i]);
        return Wrap(w);
    }

    /// <summary>S→C 0x02c9 subtag <c>0x0b</c> ToastNotificationA.</summary>
    public static byte[] ToastNotificationA(byte a, byte b, byte c) =>
        Wrap(new BlobWriter().U8((byte)ServerOp.ToastNotificationA).U8(a).U8(b).U8(c));

    /// <summary>S→C 0x02c9 subtag <c>0x0c</c> ToastNotificationB.</summary>
    public static byte[] ToastNotificationB(byte a, byte b, byte c) =>
        Wrap(new BlobWriter().U8((byte)ServerOp.ToastNotificationB).U8(a).U8(b).U8(c));

    /// <summary>S→C 0x02c9 subtag <c>0x0d</c> MapDeviceCurrentMapName (<c>u8 mapDeviceIdx, u32le byteLen, utf8[byteLen]</c>).</summary>
    public static byte[] MapDeviceCurrentMapName(byte mapDeviceIndex, string name)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(name ?? "");
        return Wrap(new BlobWriter().U8((byte)ServerOp.MapDeviceCurrentMapName).U8(mapDeviceIndex).U32((uint)utf8.Length).Bytes(utf8));
    }

    // ---- C→S 0x02ca subtags (0x00..0x1d; 0x1a unused; all LE inside payload) -------------------------------------------

    public enum ClientOp : byte
    {
        RerollRecruits = 0x00,
        HireRecruit = 0x01,
        FireWorker = 0x02,
        AssignWorker = 0x03,
        AssignFarm = 0x04,
        BuyUpgrade = 0x05,
        SendShipment = 0x06,
        AbandonOrCancelShipment = 0x07,
        RescueCommandeeredShip = 0x08,
        PayShipRansom = 0x09,
        AbandonCommandeeredShip = 0x0a,
        RerollPortQuota = 0x0b,
        StartMapping = 0x0c,
        StopMappingImmediate = 0x0d,
        SetMappingDelayedStop = 0x0e,
        RescueKidnappedMappers = 0x0f,
        PayMapperRansom = 0x10,
        AbandonKidnappedMappers = 0x11,
        OpenRewardInventory = 0x12,
        TakeRewardCtrlClick = 0x13,
        TakeRewardToSlot = 0x14,
        TakeRewardAndSocket = 0x15,
        TakeAllRewards = 0x16,
        DiscardAllRewards = 0x17,
        DepositGold = 0x18,
        WithdrawGold = 0x19,
        HarbourNpcNotifAck = 0x1b,
        MappingNpcNotifAck = 0x1c,
        AckFailedMapRun = 0x1d,
    }

    public abstract record VillageCommand(ClientOp Op);
    public sealed record RerollRecruitsCommand(bool IsSuperReroll) : VillageCommand(ClientOp.RerollRecruits);
    public sealed record HireRecruitCommand(byte RecruitIndex) : VillageCommand(ClientOp.HireRecruit);
    public sealed record FireWorkerCommand(byte WorkerIndex) : VillageCommand(ClientOp.FireWorker);
    public sealed record AssignWorkerCommand(byte WorkerIndex, byte VillageJobRow) : VillageCommand(ClientOp.AssignWorker);
    public sealed record AssignFarmCommand(byte PlotIndex, byte VillageResourceRow) : VillageCommand(ClientOp.AssignFarm);
    public sealed record BuyUpgradeCommand(byte VillageUpgradeRow) : VillageCommand(ClientOp.BuyUpgrade);
    public sealed record SendShipmentCommand(byte ShipIndex, byte PortRow, byte FailureRow, uint[] ExportAmounts) : VillageCommand(ClientOp.SendShipment);
    public sealed record AbandonOrCancelShipmentCommand(byte ShipIndex) : VillageCommand(ClientOp.AbandonOrCancelShipment);
    public sealed record RescueCommandeeredShipCommand() : VillageCommand(ClientOp.RescueCommandeeredShip);
    public sealed record PayShipRansomCommand() : VillageCommand(ClientOp.PayShipRansom);
    public sealed record AbandonCommandeeredShipCommand() : VillageCommand(ClientOp.AbandonCommandeeredShip);
    public sealed record RerollPortQuotaCommand(byte PortRow) : VillageCommand(ClientOp.RerollPortQuota);
    public sealed record StartMappingCommand(byte MapDeviceIndex) : VillageCommand(ClientOp.StartMapping);
    public sealed record StopMappingImmediateCommand(byte MapDeviceIndex) : VillageCommand(ClientOp.StopMappingImmediate);
    public sealed record SetMappingDelayedStopCommand(byte MapDeviceIndex, bool Suspend) : VillageCommand(ClientOp.SetMappingDelayedStop);
    public sealed record RescueKidnappedMappersCommand() : VillageCommand(ClientOp.RescueKidnappedMappers);
    public sealed record PayMapperRansomCommand() : VillageCommand(ClientOp.PayMapperRansom);
    public sealed record AbandonKidnappedMappersCommand() : VillageCommand(ClientOp.AbandonKidnappedMappers);
    public sealed record OpenRewardInventoryCommand(byte SourceKind, byte SlotIndex) : VillageCommand(ClientOp.OpenRewardInventory);
    public sealed record TakeRewardCtrlClickCommand(uint ItemId, uint FlagsOrTab, uint Extra) : VillageCommand(ClientOp.TakeRewardCtrlClick);
    public sealed record TakeRewardToSlotCommand(uint U8Param, uint ItemId, uint DstInventoryId, uint X, uint Y, uint Extra) : VillageCommand(ClientOp.TakeRewardToSlot);
    public sealed record TakeRewardAndSocketCommand(uint Tab, uint OfferItemId, uint DstInventoryId, uint TargetItemId, uint SocketIndex) : VillageCommand(ClientOp.TakeRewardAndSocket);
    public sealed record TakeAllRewardsCommand(uint BaseItemTypeHash32) : VillageCommand(ClientOp.TakeAllRewards);
    public sealed record DiscardAllRewardsCommand() : VillageCommand(ClientOp.DiscardAllRewards);
    public sealed record DepositGoldCommand(uint Amount) : VillageCommand(ClientOp.DepositGold);
    public sealed record WithdrawGoldCommand(uint Amount) : VillageCommand(ClientOp.WithdrawGold);
    public sealed record HarbourNpcNotifAckCommand(byte Value) : VillageCommand(ClientOp.HarbourNpcNotifAck);
    public sealed record MappingNpcNotifAckCommand(byte Value) : VillageCommand(ClientOp.MappingNpcNotifAck);
    public sealed record AckFailedMapRunCommand(byte MapDeviceIndex) : VillageCommand(ClientOp.AckFailedMapRun);

    /// <summary>Parses a C→S <c>0x02ca</c> payload (without the outer 0x02ca/rulesetId/length header) for ruleset <c>0x9b</c>.</summary>
    public static bool TryReadCommand(ReadOnlySpan<byte> payload, out VillageCommand? command)
    {
        command = null;
        if (payload.Length < 1) return false;
        var op = (ClientOp)payload[0];
        var args = payload[1..];
        switch (op)
        {
            case ClientOp.RerollRecruits when args.Length >= 1:
                command = new RerollRecruitsCommand(args[0] != 0); return true;
            case ClientOp.HireRecruit when args.Length >= 1:
                command = new HireRecruitCommand(args[0]); return true;
            case ClientOp.FireWorker when args.Length >= 1:
                command = new FireWorkerCommand(args[0]); return true;
            case ClientOp.AssignWorker when args.Length >= 2:
                command = new AssignWorkerCommand(args[0], args[1]); return true;
            case ClientOp.AssignFarm when args.Length >= 2:
                command = new AssignFarmCommand(args[0], args[1]); return true;
            case ClientOp.BuyUpgrade when args.Length >= 1:
                command = new BuyUpgradeCommand(args[0]); return true;
            case ClientOp.SendShipment when args.Length >= 3 + ExportCount * 4:
                var exports = new uint[ExportCount];
                for (int i = 0; i < ExportCount; i++)
                    exports[i] = BinaryPrimitives.ReadUInt32LittleEndian(args[(3 + i * 4)..]);
                command = new SendShipmentCommand(args[0], args[1], args[2], exports); return true;
            case ClientOp.AbandonOrCancelShipment when args.Length >= 1:
                command = new AbandonOrCancelShipmentCommand(args[0]); return true;
            case ClientOp.RescueCommandeeredShip:
                command = new RescueCommandeeredShipCommand(); return true;
            case ClientOp.PayShipRansom:
                command = new PayShipRansomCommand(); return true;
            case ClientOp.AbandonCommandeeredShip:
                command = new AbandonCommandeeredShipCommand(); return true;
            case ClientOp.RerollPortQuota when args.Length >= 1:
                command = new RerollPortQuotaCommand(args[0]); return true;
            case ClientOp.StartMapping when args.Length >= 1:
                command = new StartMappingCommand(args[0]); return true;
            case ClientOp.StopMappingImmediate when args.Length >= 1:
                command = new StopMappingImmediateCommand(args[0]); return true;
            case ClientOp.SetMappingDelayedStop when args.Length >= 2:
                command = new SetMappingDelayedStopCommand(args[0], args[1] != 0); return true;
            case ClientOp.RescueKidnappedMappers:
                command = new RescueKidnappedMappersCommand(); return true;
            case ClientOp.PayMapperRansom:
                command = new PayMapperRansomCommand(); return true;
            case ClientOp.AbandonKidnappedMappers:
                command = new AbandonKidnappedMappersCommand(); return true;
            case ClientOp.OpenRewardInventory when args.Length >= 2:
                command = new OpenRewardInventoryCommand(args[0], args[1]); return true;
            case ClientOp.TakeRewardCtrlClick when args.Length >= 12:
                command = new TakeRewardCtrlClickCommand(
                    BinaryPrimitives.ReadUInt32LittleEndian(args),
                    BinaryPrimitives.ReadUInt32LittleEndian(args[4..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(args[8..])); return true;
            case ClientOp.TakeRewardToSlot when args.Length >= 24:
                command = new TakeRewardToSlotCommand(
                    BinaryPrimitives.ReadUInt32LittleEndian(args),
                    BinaryPrimitives.ReadUInt32LittleEndian(args[4..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(args[8..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(args[12..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(args[16..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(args[20..])); return true;
            case ClientOp.TakeRewardAndSocket when args.Length >= 20:
                command = new TakeRewardAndSocketCommand(
                    BinaryPrimitives.ReadUInt32LittleEndian(args),
                    BinaryPrimitives.ReadUInt32LittleEndian(args[4..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(args[8..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(args[12..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(args[16..])); return true;
            case ClientOp.TakeAllRewards when args.Length >= 4:
                command = new TakeAllRewardsCommand(BinaryPrimitives.ReadUInt32LittleEndian(args)); return true;
            case ClientOp.DiscardAllRewards:
                command = new DiscardAllRewardsCommand(); return true;
            case ClientOp.DepositGold when args.Length >= 4:
                command = new DepositGoldCommand(BinaryPrimitives.ReadUInt32LittleEndian(args)); return true;
            case ClientOp.WithdrawGold when args.Length >= 4:
                command = new WithdrawGoldCommand(BinaryPrimitives.ReadUInt32LittleEndian(args)); return true;
            case ClientOp.HarbourNpcNotifAck when args.Length >= 1:
                command = new HarbourNpcNotifAckCommand(args[0]); return true;
            case ClientOp.MappingNpcNotifAck when args.Length >= 1:
                command = new MappingNpcNotifAckCommand(args[0]); return true;
            case ClientOp.AckFailedMapRun when args.Length >= 1:
                command = new AckFailedMapRunCommand(args[0]); return true;
            default:
                return false;
        }
    }

    // ---- Recombinator wire codecs (0x028f, 0x0293, 0x0291, 0x010b) -----------------------------------------------------

    /// <summary>C→S <c>0x028f</c> (writer <c>0x141fa49e0</c>, sender <c>0x142172ca0</c>): query chance (<see cref="Execute"/>=false)
    /// or execute recombination (<see cref="Execute"/>=true).</summary>
    public sealed record RecombinateRequest(
        byte Reserved,
        byte[] Slot0SelectedModIndices,
        byte[] Slot1SelectedModIndices,
        bool PredictableMode,
        bool Execute);

    /// <summary>Parses a framed C→S <c>0x028f</c> packet (including its 2-byte big-endian opcode).</summary>
    public static RecombinateRequest ReadRecombinate(ReadOnlySpan<byte> m)
    {
        if (m.Length < 7 || BinaryPrimitives.ReadUInt16BigEndian(m) != RecombinateId)
            throw new InvalidDataException("not a 0x028f recombinate message");
        byte reserved = m[2];
        int n0 = m[3];
        int n1At = 4 + n0;
        if (n1At >= m.Length) throw new InvalidDataException("0x028f recombinate message is cut off");
        byte[] slot0 = m.Slice(4, n0).ToArray();
        int n1 = m[n1At];
        int tailAt = n1At + 1 + n1;
        if (m.Length < tailAt + 2) throw new InvalidDataException("0x028f recombinate message is cut off");
        byte[] slot1 = m.Slice(n1At + 1, n1).ToArray();
        bool predictable = m[tailAt] != 0;
        bool execute = m[tailAt + 1] != 0;
        return new RecombinateRequest(reserved, slot0, slot1, predictable, execute);
    }

    /// <summary>S→C <c>0x0293</c> (reader <c>0x14085f310</c>, case <c>0xda</c>, 7B): <c>chanceBasisPoints / 100.0</c>%.</summary>
    public static byte[] RecombinationChance(uint chanceBasisPoints, byte mode = 0) =>
        new LoginWriter().U16(RecombinationChanceId).U8(mode).U32(chanceBasisPoints).ToArray();

    /// <summary>S→C <c>0x0291</c> (reader <c>0x1421b72d0</c>, case <c>0xd8</c>, 5B): <c>u16be 0x0291, u8 success, u16le errorOrStatusCode</c>.</summary>
    public static byte[] RecombinationResult(bool success, ushort errorOrStatusCode = 0) =>
        new LoginWriter()
            .U16(RecombinationResultId)
            .U8((byte)(success ? 1 : 0))
            .U8((byte)errorOrStatusCode)
            .U8((byte)(errorOrStatusCode >> 8))
            .ToArray();

    /// <summary>C→S <c>0x010b</c> (writer <c>0x141f9fa30</c>, 9B): Ctrl+click quick-move into/out of Recombinator inventories 92/93.</summary>
    public sealed record RecombinatorQuickMove(uint ItemId, byte SourceFlag, bool IntoRecombinator, bool IsOutputInventory);

    /// <summary>Parses a framed C→S <c>0x010b</c> packet (9 bytes including its 2-byte big-endian opcode).</summary>
    public static RecombinatorQuickMove ReadRecombinatorQuickMove(ReadOnlySpan<byte> m)
    {
        if (m.Length < RecombinatorQuickMoveLength || BinaryPrimitives.ReadUInt16BigEndian(m) != RecombinatorQuickMoveId)
            throw new InvalidDataException("not a 0x010b recombinator quick-move message");
        return new RecombinatorQuickMove(
            BinaryPrimitives.ReadUInt32BigEndian(m[2..]),
            m[6],
            m[7] != 0,
            m[8] != 0);
    }
}
