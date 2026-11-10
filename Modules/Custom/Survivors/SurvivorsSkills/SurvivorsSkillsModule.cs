using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Survivors;

public sealed class SurvivorsSkillsModule : IModule
{
    public void Load(IModuleContext module)
    {
        SurvivorsSkills.SkillsStopping = module.Lifetime.Stopping;
        module.Lifetime.Own(GameEvents.GrantedSkills.Add("SurvivorsSkills", SurvivorsSkills.GrantedSkills));
        module.Lifetime.Own(GameEvents.SkillBarBuilding.Add("SurvivorsSkills", (conn, ids) =>
        {
            foreach (var skill in SurvivorsSkills.GrantedSkills(conn))
            {
                int free = Array.IndexOf(ids, (ushort)0, 3);
                if (free < 3) break;
                ids[free] = skill.Id;
            }
        }));
        module.OnPacket(InstanceProtocol.UseSkillAtLocationId, packet =>
            Task.FromResult(SurvivorsSkills.SurvivorsOwnsSkill(ConnOf(packet.Player),
                InstanceProtocol.ReadUseSkillAtLocation(packet.Bytes.Span).SkillId)));
        module.OnPacket(InstanceTransfer.UseSkillOnObjectId, packet =>
            Task.FromResult(SurvivorsSkills.SurvivorsOwnsSkill(ConnOf(packet.Player),
                InstanceTransfer.ReadUseSkillOnObject(packet.Bytes.Span).SkillId)));
    }
}

static partial class SurvivorsSkills
{
    internal static CancellationToken SkillsStopping, RewardsStopping;
    static readonly ConditionalWeakTable<ClientState, SurvivorsState> States = new();

    internal static SurvivorsState StateOf(ClientState conn) => States.GetOrCreateValue(conn);

    internal static IReadOnlyList<GrantedSkill> GrantedSkills(ClientState conn) =>
        StateOf(conn) is { Survivors: { } run, SurvivorsCatalog: { } catalog }
            ? SurvivorsGrantedSkills(conn, run, catalog) : [];
}
