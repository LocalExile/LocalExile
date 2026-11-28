using GameServer.Modding;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Crucible (3.21): a weapon's passive tree. This module owns the rules that make and allocate one
/// (<see cref="CrucibleTrees"/>); the item itself already carries the tree — the framework writes it as the Mods
/// component's flag bit 7 record and counts its allocated nodes as hidden mods (Agents/docs/protocol/13-items.md §2g).
/// <para>
/// The encounter that hands a weapon its tree (the Crucible's remnants, the monsters that fill its progress and the
/// screen that lets a player spend a layer) is not implemented yet. Nothing in play calls
/// <see cref="CrucibleTrees.Generate"/> until it is; a mod reaches the same API for development and testing.
/// </para>
/// </summary>
public sealed class CrucibleModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        CrucibleTrees.LoadGameData();
        module.Log($"{CrucibleTrees.Nodes.Count} weapon-tree nodes");
    }
}
