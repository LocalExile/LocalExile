using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Abyss;

public sealed partial class AbyssEncounter
{
    static AreaTransitionObject DepthGate(State state) => new(state.PitCell.X, state.PitCell.Y,
        AreaTransitionCatalog.HashOf(state.DepthArea)!.Value, "Metadata/Effects/Environment/abyss_chasm/abyss_circle.ao") { Kind = 2 };

    static async Task ShowDepthEntrance(Player player, State state)
    {
        var gate = DepthGate(state);
        foreach (var member in player.Instance.PlayersExcept(player.Id).Prepend(player).Distinct())
            if (!member.Layout.AreaTransitions.Any(t => t.Id == state.DepthEntrance))
                member.AddAreaTransition(state.DepthEntrance, gate);
        await Create(player, state.DepthEntrance, AreaTransitionObject.MetadataPath, gate.Blob(), "Abyssal Depths entrance");
    }

    static async Task EnterDepthReturn(Player player)
    {
        if (player.Layout.Id is not ("AbyssLeague" or "AbyssLeague2" or "AbyssLeagueBoss" or "AbyssLeagueBoss2")
            || MapRunOf(player)?.ReturnTo is not { } parent) return;
        if (!player.Instance.ContentState.TryGetValue(ReturnKey, out var text))
            player.Instance.ContentState[ReturnKey] = text = player.TakeObjectId().ToString();
        uint id = uint.Parse(text);
        var cell = player.Layout.Near((player.Layout.SpawnX + 6, player.Layout.SpawnY));
        var gate = new AreaTransitionObject(cell.X, cell.Y, parent.Area) { Kind = 3 };
        player.AddAreaTransition(id, gate);
        await Create(player, id, AreaTransitionObject.MetadataPath, gate.Blob(), "Abyssal Depths return");
    }

    async Task<bool> UseDepth(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        var state = AllOf(player).FirstOrDefault(s => s.DepthEntrance != 0 && s.DepthEntrance == use.TargetId);
        bool returning = player.Instance.ContentState.TryGetValue(ReturnKey, out var value) && uint.TryParse(value, out uint id) && id == use.TargetId;
        if (state is null && !returning) return false;
        var shown = player.Layout.AreaTransitions.FirstOrDefault(t => t.Id == use.TargetId).Transition;
        var at = state?.PitCell ?? (shown!.GridX, shown.GridY);
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea || !TryBeginObjectWalk(player, requestedAt, at.X, at.Y, 3, out var walk)) return true;
        if (walk.Arrive > walk.Now) await AreaDelay(player, walk.Arrive - walk.Now);
        if (!PlayerActionCurrent(player, walk.Action)) return true;
        if (returning && MapRunOf(player)?.ReturnTo is { } parent)
            await ChangeArea(player, new PendingTransfer(parent.Area, parent.Instance, false, ArriveAt: (parent.X, parent.Y)), "Abyssal Depths return");
        else if (state is not null)
        {
            await state.Gate.WaitAsync();
            try
            {
            var origin = player.Instance;
            var pits = AllOf(player);
            var area = new MapDevice.MapArea(AreaTransitionCatalog.HashOf(state.DepthArea)!.Value, state.DepthArea, "Abyssal Depths", 0) { Level = state.Level };
            var run = new MapDevice.Run(player.Character.Name!, area, MapMods.None, ReturnTo: new(player.Layout.Hash, player.InstanceNumber, at.X, at.Y)) { Atlas = false };
            await ChangeArea(player, new PendingTransfer(area.Hash16, state.DepthInstance, state.DepthInstance == 0, AtSpawn: true)
            {
                Map = run,
                Done = response => { if (response.Ok) { state.DepthInstance = response.Instance; SaveSnapshot(origin, pits, state); } },
            }, "Abyssal Depths entrance");
            }
            finally { state.Gate.Release(); }
        }
        return true;
    }
}
