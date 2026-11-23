using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class UniquebeltsRules
{
    /// <summary>Mageblood's flask buffs as they should be now: the packets that end those no longer applied (moved, off the
    /// bar, the belt off, death) and start those missing, with no timer: the effect is permanent.</summary>
    internal static List<byte[]> StepMagebloodBuffs(Player player)
    {
        var items = ItemsFor(player);
        var wanted = new Dictionary<uint, (byte Slot, UtilityFlasks.Utility Utility)>();
        if (player.PlayerLife > 0 && MagebloodBelt(player) is { } belt)
            foreach (var flask in MagebloodFlasks(ItemsOf(player).Items(PlayerInventories.Flask), MagebloodFlaskCount(belt, items), items))
                if (items.ByHash(flask.Item.TypeHash) is { } type && items.UtilityFlaskOf(type) is { } utility)
                    wanted[flask.Id] = ((byte)flask.X, utility);
        List<byte[]> packets = [];
        foreach (var (id, shown) in player.MagebloodBuffs.ToList())
        {
            if (wanted.TryGetValue(id, out var still) && still.Slot == shown.Slot) continue;
            player.Area.Buffs.Clear(player.Id, shown.Row, player.Id, tag: shown.Buff);
            if (shown.Extra is ushort extra) player.Area.Buffs.Clear(player.Id, shown.ExtraRow, player.Id, tag: extra);
            player.MagebloodBuffs.Remove(id);
        }
        foreach (var (id, (slot, utility)) in wanted)
        {
            if (player.MagebloodBuffs.ContainsKey(id)) continue;
            ushort buff = player.NextBuff++;
            ushort? extra = utility.Extra is null ? null : player.NextBuff++;
            player.MagebloodBuffs[id] = (slot, buff, extra, utility.BuffRow, utility.Extra?.BuffRow ?? 0);
            player.Area.Buffs.Set(player.Id, Flasks.Buff(player.Id, buff, utility.BuffRow, slot, seconds: null, values: null, visual: FlaskEffectVisual(player, slot)));
            if (utility.Extra is { } x && extra is ushort second) player.Area.Buffs.Set(player.Id, new(x.BuffRow, player.Id, Tag: second));
        }
        return packets;
    }
}
