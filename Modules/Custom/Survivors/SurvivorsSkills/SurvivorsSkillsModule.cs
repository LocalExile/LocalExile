using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Survivors;

public sealed class SurvivorsSkillsModule : IModule
{
    /// <summary>This module's game data, built in a fixed order when it loads: before the Gateway listens, as the
    /// Gateway's own (WorldLoader). Tests that use the module call it once.</summary>
    public static void LoadData()
    {
        SurvivorsSkills.LoadRulesData();
    }

    public void Load(IModuleContext module)
    {
        LoadData();
        SurvivorsSkills.SkillsStopping = module.Lifetime.Stopping;
        GameEvents.GrantedSkills.Add(module.Manifest.Id, SurvivorsSkills.GrantedSkills);
        GameEvents.SkillBarBuilding.Add(module.Manifest.Id, (conn, ids) =>
        {
            foreach (var skill in SurvivorsSkills.GrantedSkills(conn))
            {
                int free = Array.IndexOf(ids, (ushort)0, 3);
                if (free < 3) break;
                ids[free] = skill.Id;
            }
        });
        GameEvents.Packet(InstanceProtocol.UseSkillAtLocationId).Add(module.Manifest.Id, (conn, bytes) =>
            Task.FromResult(SurvivorsSkills.SurvivorsOwnsSkill(conn,
                InstanceProtocol.ReadUseSkillAtLocation(bytes.AsSpan()).SkillId)));
        GameEvents.Packet(InstanceTransfer.UseSkillOnObjectId).Add(module.Manifest.Id, (conn, bytes) =>
            Task.FromResult(SurvivorsSkills.SurvivorsOwnsSkill(conn,
                InstanceTransfer.ReadUseSkillOnObject(bytes.AsSpan()).SkillId)));
    }
}

static partial class SurvivorsSkills
{
    internal static CancellationToken SkillsStopping, RewardsStopping;
    static readonly ConditionalWeakTable<Player, SurvivorsState> States = new();

    internal static SurvivorsState StateOf(Player conn) => States.GetOrCreateValue(conn);

    internal static IReadOnlyList<GrantedSkill> GrantedSkills(Player conn) =>
        StateOf(conn) is { Survivors: { } run, SurvivorsCatalog: { } catalog }
            ? SurvivorsGrantedSkills(conn, run, catalog) : [];
}
