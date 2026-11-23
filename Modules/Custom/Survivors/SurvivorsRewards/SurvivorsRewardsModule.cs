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
            player => StateOf(player).SurvivorsItemIds.ToArray());
        GameEvents.FinalStatsBuilding.Add(module.Manifest.Id, (player, stats) =>
        {
            if (StateOf(player).SurvivorsItemIds.Count == 0) return;
            foreach (uint key in new[] { Stat.VirtualStrengthForRequirements, Stat.VirtualDexterityForRequirements,
                Stat.VirtualIntelligenceForRequirements })
                stats[key] = Math.Max(stats.GetValueOrDefault(key), 1000);
        });
        GameEvents.Packet(InstanceNpcs.BuyId).Add(module.Manifest.Id, (player, bytes) =>
        {
            return SurvivorsRewards.ChooseSurvivorsSkill(player,
                InstanceNpcs.ReadBuy(bytes.AsSpan()).ItemId);
        });
        GameEvents.Packet(InstanceNpcs.CtrlBuyId).Add(module.Manifest.Id, (player, bytes) =>
        {
            return SurvivorsRewards.ChooseSurvivorsSkill(player,
                InstanceNpcs.ReadCtrlBuy(bytes.AsSpan()).ItemId);
        });
        GameEvents.RewardClosing.Add(module.Manifest.Id, async player =>
        {
            if (StateOf(player) is not { Survivors: { } run, SurvivorsOffer: not null } || run.Offer.Count == 0) return false;
            return await SurvivorsRewards.ApplySurvivorsChoice(player, run.Offer[0]);
        });
    }
}
