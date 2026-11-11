using GameServer.Modding;
using GameServer.Core.Protocol.Instance;
using static GameServer.Modules.Survivors.SurvivorsSkills;

namespace GameServer.Modules.Survivors;

public sealed class SurvivorsRewardsModule : IModule
{
    public void Load(IModuleContext module)
    {
        RewardsStopping = module.Lifetime.Stopping;
        GameEvents.RequirementExemptions.Add(module.Manifest.Id,
            conn => StateOf(conn).SurvivorsItemIds.ToArray());
        GameEvents.FinalStatsBuilding.Add(module.Manifest.Id, (conn, stats) =>
        {
            if (StateOf(conn).SurvivorsItemIds.Count == 0) return;
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" })
                if (Stats.Key(name) is uint key) stats[key] = Math.Max(stats.GetValueOrDefault(key), 1000);
        });
        GameEvents.Packet(InstanceNpcs.BuyId).Add(module.Manifest.Id, (conn, bytes) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            return SurvivorsRewards.ChooseSurvivorsSkill(remote, stream, ciphers, conn,
                InstanceNpcs.ReadBuy(bytes.AsSpan()).ItemId);
        });
        GameEvents.Packet(InstanceNpcs.CtrlBuyId).Add(module.Manifest.Id, (conn, bytes) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            return SurvivorsRewards.ChooseSurvivorsSkill(remote, stream, ciphers, conn,
                InstanceNpcs.ReadCtrlBuy(bytes.AsSpan()).ItemId);
        });
        GameEvents.RewardClosing.Add(module.Manifest.Id, async conn =>
        {
            if (StateOf(conn) is not { Survivors: { } run, SurvivorsOffer: not null } || run.Offer.Count == 0) return false;
            var (remote, stream, ciphers) = conn.Wire;
            return await SurvivorsRewards.ApplySurvivorsChoice(remote, stream, ciphers, conn, run.Offer[0]);
        });
    }
}
