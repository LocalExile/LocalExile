using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static bool IsVaalMinion(string? effect) => effect is "VaalSummonSkeletons" or "VaalAbsolution" or "VaalDomination" or "VaalAnimateWeapon";
    static Dictionary<uint, int> MergeSummonStats(IReadOnlyDictionary<uint,int> source, IReadOnlyDictionary<uint,int> retained)
    {
        var result = new Dictionary<uint,int>(source);
        foreach (var (key,value) in retained) result[key] = result.GetValueOrDefault(key) + value;
        return result;
    }

    static string? VaalMinionProxyDisabled(Player player, int effect, ushort? source)
    {
        if (!IsVaalMinion(SkillsFor(player).EffectId(effect)) || source is not ushort skill) return null;
        var gem = SupportedSkill(player, skill);
        return gem.Effect == effect && (GolemStat(gem.Stats, Stat.IsTotem) != 0 || PlacedKind(gem) is not (false, false))
            ? "Vaal minion skills through totems, traps or mines are not implemented" : null;
    }

    static bool SameSummonGem(Player player, GolemState.Golem minion, SupportedGem vaal)
    {
        var regular = SupportedSkill(player,minion.Source);
        return regular.SourceGem is not null && vaal.SourceGem is not null && regular.Host == vaal.Host
            && (minion.SourceIdentity is null || ReferenceEquals(minion.SourceIdentity, PlayerInventories.GemIdentity(regular.SourceGem)))
            && ReferenceEquals(PlayerInventories.GemIdentity(regular.SourceGem), PlayerInventories.GemIdentity(vaal.SourceGem));
    }

    static (GolemState.Golem? Body, GolemState.Golem? Refresh, string Family, int Limit) VaalAscension(Player player, SupportedGem gem, uint target = 0)
    {
        bool absolution = SkillsFor(player).EffectId(gem.Effect) == "VaalAbsolution";
        string regular = absolution ? "Absolution" : "Dominating";
        string family = absolution ? "VaalAbsolution" : "VaalDomination";
        int limit = GolemStat(gem.Stats, absolution ? Stat.DisplayMaxUpgradedSentinelsOfAbsolution : Stat.DisplayMaxUpgradedSentinelsOfDominance);
        var playerStats = CharacterPlayerStats(player);
        int PlayerStat(uint key) => GolemStat(playerStats, key);
        string cap = absolution ? "absolution_sentinels" : "dominated_monsters";
        limit = ApplyDarkMonarchLimit(Math.Clamp(limit + PlayerStat(Stat.Of("base_max_number_of_vaal_" + cap))
            + PlayerStat(Stat.Of("max_number_of_vaal_" + cap)), 0, 200), PlayerStat, family);
        var living = player.Summons.Fighters.Values.Where(g => !g.Monster.Dead && g.Monster.Life > 0 && SameSummonGem(player,g,gem)).ToArray();
        var ascended = living.Where(g => g.Definition.Kind == family).ToArray();
        return (living.OrderByDescending(g => g.Id == target).FirstOrDefault(g => absolution ? g.Definition.Kind == regular : g.Definition.Kind.StartsWith(regular,StringComparison.Ordinal)),
            ascended.Length >= limit ? ascended.MinBy(g => player.Summons.Until[g.Id]) : null, family,limit);
    }

    static bool VaalMinionAvailable(Player player, ushort source)
    {
        var gem=SupportedSkill(player,source);
        if (SkillsFor(player).EffectId(gem.Effect) is not ("VaalAbsolution" or "VaalDomination")) return true;
        var plan=VaalAscension(player,gem);
        return plan.Limit > 0 && (plan.Body is not null || plan.Refresh is not null);
    }

    static bool VaalMinionTargetAvailable(Player player, ushort source, uint target)
    {
        var gem = SupportedSkill(player, source);
        if (SkillsFor(player).EffectId(gem.Effect) is not ("VaalAbsolution" or "VaalDomination")) return true;
        if (target == 0 || target == player.Id) return VaalMinionAvailable(player, source);
        if (!player.Summons.Fighters.TryGetValue(target, out var minion) || minion.Monster.Dead || minion.Monster.Life == 0
            || !SameSummonGem(player, minion, gem)) return false;
        bool absolution = SkillsFor(player).EffectId(gem.Effect) == "VaalAbsolution";
        return absolution ? minion.Definition.Kind is "Absolution" or "VaalAbsolution"
            : minion.Definition.Kind.StartsWith("Dominating", StringComparison.Ordinal) || minion.Definition.Kind == "VaalDomination";
    }

    internal static async Task<bool> ReleaseVaalMinions(Player player, ushort source, (int X,int Y) aim, Func<bool> current, uint target = 0)
    {
        var gem = SupportedSkill(player, source);
        bool Live() => current() && SameTriggerSource(gem, SupportedSkill(player, source));
        if (!gem.RequirementsMet || !IsVaalMinion(SkillsFor(player).EffectId(gem.Effect))
            || VaalMinionProxyDisabled(player, gem.Effect, source) is not null || !Live()) return false;
        double now = AreaTime(player);
        double duration = SkillDurationModifiers.Seconds(GolemStat(gem.Stats, Stat.BaseSkillEffectDuration),
            n => GolemStat(CharacterPlayerStats(player), n), n => GolemStat(gem.Stats, n), DurationKinds(player, gem.Effect));
        if (SkillsFor(player).EffectId(gem.Effect) == "VaalSummonSkeletons")
        {
            var definition=SummonData.Find("VaalSummonSkeletons")!;
            foreach (var (stat,body,variants) in new[] { ("melee","Melee",2), ("archer","Ranged",2), ("mage","Spellcaster",3), ("leader","General",4) })
                for (int i=0;i<Math.Clamp(GolemStat(gem.Stats,Stat.Of("number_of_"+stat+"_skeletons_to_summon")),0,200)&&Live();i++)
                {
                    string path="Metadata/Monsters/RaisedSkeletons/RaisedSkeleton"+body+AreaRng(player).Next(1,variants+1)+"Army";
                    if (await SummonFighter(player,source,definition,aim,restoredVariety:path,
                        current:Live,remaining:duration,triggered:true)==0) return false;
                }
            return true;
        }
        if (SkillsFor(player).EffectId(gem.Effect) == "VaalAnimateWeapon")
            return await ReleaseVaalWeapons(player, source, gem, aim, duration, Live);
        var plan = VaalAscension(player, gem, target);
        if (plan.Limit<=0) return false;
        if (plan.Refresh is { } refreshed)
        {
            if (!Live() || !GolemCurrent(player,refreshed) || refreshed.Monster.Dead) return false;
            player.Summons.Until[refreshed.Id]=now+duration;
            foreach(var m in player.Minions.All.Where(m=>m.Id==refreshed.Id)) m.Until=now+duration;
            return true;
        }
        if (plan.Body is not { } old) return false;
        var regular=SupportedSkill(player,old.Source);
        var definitionVaal=new SummonCatalog.Definition(SkillsFor(player).EffectId(regular.Effect)!,plan.Family,
            plan.Family=="VaalAbsolution" ? "Metadata/Monsters/TemplarJudge/AbsolutionTemplarJudgeVaal"
                : "Metadata/Monsters/Axis/AxisEliteSoldierDominatingBlowVaal",
            plan.Family=="VaalAbsolution" ? Stat.BaseMaxNumberOfVaalAbsolutionSentinels : Stat.BaseMaxNumberOfVaalDominatedMonsters, 0);
        var upgrade=new Dictionary<uint,int>();
        foreach(uint name in new[]{Stat.VaalUpgradeMinionDamagePlusPctFinal,Stat.VaalUpgradeMinionDamageTakenPlusPctFinal})
            upgrade[name]=GolemStat(gem.Stats,name);
        return await SummonFighterCore(player,old.Source,definitionVaal,old.Monster.Walk.Position(now),
            current:()=>current()&&GolemCurrent(player,old)&&!old.Monster.Dead&&SameTriggerSource(gem,SupportedSkill(player,source)),
            remaining:duration,replacingOld:old,triggered:true,
            inheritedMods:old.InheritedMods,rarity:old.Monster.Rarity,retainedStats:upgrade)!=0;
    }
}
