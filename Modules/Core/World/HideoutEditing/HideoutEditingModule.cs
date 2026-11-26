using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static HideoutEditingFeature;

namespace GameServer.Modules.M_hideoutediting;

/// <summary>Moving things in the hideout's editor.</summary>
public sealed class HideoutEditingModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.AreaGenerated.Add(module.Manifest.Id, async player =>
        {
            await SpawnDecorations(player);
            await SpawnMonsterStatues(player);
        });
        GameEvents.PlayerEntered.Add(module.Manifest.Id, SyncAreaDecorations);
        GameEvents.ObjectUse.Add(module.Manifest.Id, (player, use, at) =>
            UseDisplayCase(player, use, at));
        GameEvents.Command(module.Manifest.Id, "save_hideout", "/save_hideout (save and report the current hideout layout)",
            (player, _) => global::Game.SaveHideoutLayout(player));
        foreach (ushort id in new[] { InstanceHideoutEditing.PlaceId, InstanceHideoutEditing.PlaceByHashId })
            GameEvents.Packet(id).Add(module.Manifest.Id, async (player, bytes) =>
            {
                await PlaceDecoration(player, InstanceHideoutEditing.ReadPlace(bytes));
                return true;
            });
        GameEvents.Packet(InstanceHideoutEditing.RemoveId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await ReclaimDecoration(player, InstanceHideoutEditing.ReadRemove(bytes));
            return true;
        });
        GameEvents.Packet(InstanceHideoutEditing.MoveId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await MoveHideoutObject(player, InstanceHideoutEditing.ReadMove(bytes));
            return true;
        });
        GameEvents.Packet(InstanceHideoutEditing.ReclaimId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await ReclaimDecoration(player, InstanceHideoutEditing.ReadReclaim(bytes));
            return true;
        });
        GameEvents.Packet(InstanceHideoutEditing.ResetId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await ResetHideout(player, InstanceHideoutEditing.ReadReset(bytes));
            return true;
        });
        GameEvents.Packet(InstanceHideoutEditing.MusicId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await SetMusic(player, InstanceHideoutEditing.ReadMusic(bytes));
            return true;
        });
        GameEvents.Packet(InstanceHideoutEditing.LayoutId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await ImportLayout(player, InstanceHideoutEditing.ReadLayout(bytes));
            return true;
        });
        GameEvents.Packet(InstanceHideoutEditing.DisplayCasePlaceId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await PlaceDisplayCaseItem(player, InstanceHideoutEditing.ReadDisplayCasePlace(bytes));
            return true;
        });
        GameEvents.Packet(InstanceHideoutEditing.DisplayCaseToggleId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await ToggleDisplayCase(player, InstanceHideoutEditing.ReadDisplayCaseToggle(bytes));
            return true;
        });
        GameEvents.Packet(InstanceMonsterStatue.CreateId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await CreateMonsterStatue(player, InstanceMonsterStatue.ReadCreate(bytes.AsSpan()));
            return true;
        });
    }
}
