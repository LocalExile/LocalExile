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

    static string? VaalMinionProxyDisabled(ClientState conn, int effect, ushort? source)
    {
        if (!IsVaalMinion(SkillsFor(conn).EffectId(effect)) || source is not ushort skill) return null;
        var gem = SupportedSkill(conn, skill);
        return gem.Effect == effect && (GolemStat(gem.Stats, "is_totem") != 0 || PlacedKind(gem) is not (false, false))
            ? "Vaal minion skills through totems, traps or mines are not implemented" : null;
    }

    static bool SameSummonGem(ClientState conn, GolemState.Golem minion, SupportedGem vaal)
    {
        var regular = SupportedSkill(conn,minion.Source);
        return regular.SourceGem is not null && vaal.SourceGem is not null && regular.Host == vaal.Host
            && (minion.SourceIdentity is null || ReferenceEquals(minion.SourceIdentity, PlayerInventories.GemIdentity(regular.SourceGem)))
            && ReferenceEquals(PlayerInventories.GemIdentity(regular.SourceGem), PlayerInventories.GemIdentity(vaal.SourceGem));
    }

    static (GolemState.Golem? Body, GolemState.Golem? Refresh, string Family, int Limit) VaalAscension(ClientState conn, SupportedGem gem, uint target = 0)
    {
        lock (conn.Monsters)
        {
            bool absolution = SkillsFor(conn).EffectId(gem.Effect) == "VaalAbsolution";
            string regular = absolution ? "Absolution" : "Dominating";
            string family = absolution ? "VaalAbsolution" : "VaalDomination";
            int limit = GolemStat(gem.Stats, absolution ? "display_max_upgraded_sentinels_of_absolution" : "display_max_upgraded_sentinels_of_dominance");
            var player = CharacterPlayerStats(conn);
            string cap = absolution ? "absolution_sentinels" : "dominated_monsters";
            limit = Math.Clamp(limit + GolemStat(player, "base_max_number_of_vaal_" + cap) + GolemStat(player, "max_number_of_vaal_" + cap), 0, 200);
            var living = conn.Summons.Fighters.Values.Where(g => !g.Monster.Dead && g.Monster.Life > 0 && SameSummonGem(conn,g,gem)).ToArray();
            var ascended = living.Where(g => g.Definition.Kind == family).ToArray();
            return (living.OrderByDescending(g => g.Id == target).FirstOrDefault(g => absolution ? g.Definition.Kind == regular : g.Definition.Kind.StartsWith(regular,StringComparison.Ordinal)),
                ascended.Length >= limit ? ascended.MinBy(g => conn.Summons.Until[g.Id]) : null, family,limit);
        }
    }

    static bool VaalMinionAvailable(ClientState conn, ushort source)
    {
        var gem=SupportedSkill(conn,source);
        if (SkillsFor(conn).EffectId(gem.Effect) is not ("VaalAbsolution" or "VaalDomination")) return true;
        var plan=VaalAscension(conn,gem);
        return plan.Limit > 0 && (plan.Body is not null || plan.Refresh is not null);
    }

    static bool VaalMinionTargetAvailable(ClientState conn, ushort source, uint target)
    {
        var gem = SupportedSkill(conn, source);
        if (SkillsFor(conn).EffectId(gem.Effect) is not ("VaalAbsolution" or "VaalDomination")) return true;
        if (target is 0 or PlayerId) return VaalMinionAvailable(conn, source);
        if (!conn.Summons.Fighters.TryGetValue(target, out var minion) || minion.Monster.Dead || minion.Monster.Life == 0
            || !SameSummonGem(conn, minion, gem)) return false;
        bool absolution = SkillsFor(conn).EffectId(gem.Effect) == "VaalAbsolution";
        return absolution ? minion.Definition.Kind is "Absolution" or "VaalAbsolution"
            : minion.Definition.Kind.StartsWith("Dominating", StringComparison.Ordinal) || minion.Definition.Kind == "VaalDomination";
    }

    internal static async Task<bool> ReleaseVaalMinions(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort source, (int X,int Y) aim, Func<bool> current, uint target = 0)
    {
        var gem=SupportedSkill(conn,source);
        bool Live() => current() && SameTriggerSource(gem, SupportedSkill(conn, source));
        if (!gem.RequirementsMet || !IsVaalMinion(SkillsFor(conn).EffectId(gem.Effect))
            || VaalMinionProxyDisabled(conn, gem.Effect, source) is not null || !Live()) return false;
        double now=AreaTime(conn);
        double duration=SkillDurationModifiers.Seconds(GolemStat(gem.Stats,"base_skill_effect_duration"),
            n=>GolemStat(CharacterPlayerStats(conn),n),n=>GolemStat(gem.Stats,n),DurationKinds(conn,gem.Effect));
        if (SkillsFor(conn).EffectId(gem.Effect)=="VaalSummonSkeletons")
        {
            var definition=SummonData.Find("VaalSummonSkeletons")!;
            foreach (var (stat,body,variants) in new[] { ("melee","Melee",2), ("archer","Ranged",2), ("mage","Spellcaster",3), ("leader","General",4) })
                for (int i=0;i<Math.Clamp(GolemStat(gem.Stats,"number_of_"+stat+"_skeletons_to_summon"),0,200)&&Live();i++)
                {
                    string path="Metadata/Monsters/RaisedSkeletons/RaisedSkeleton"+body+Random.Shared.Next(1,variants+1)+"Army";
                    if (await SummonFighter(remote,stream,ciphers,conn,source,definition,aim,restoredVariety:path,
                        current:Live,remaining:duration,triggered:true)==0) return false;
                }
            return true;
        }
        if (SkillsFor(conn).EffectId(gem.Effect) == "VaalAnimateWeapon")
            return await ReleaseVaalWeapons(remote, stream, ciphers, conn, source, gem, aim, duration, Live);
        var plan=VaalAscension(conn,gem,target);
        if (plan.Limit<=0) return false;
        if (plan.Refresh is { } refreshed)
        {
            lock(conn.Monsters)
            {
                if (!Live() || !GolemCurrent(conn,refreshed) || refreshed.Monster.Dead) return false;
                conn.Summons.Until[refreshed.Id]=now+duration;
                foreach(var m in conn.Minions.All.Where(m=>m.Id==refreshed.Id)) m.Until=now+duration;
            }
            return true;
        }
        if (plan.Body is not { } old) return false;
        var regular=SupportedSkill(conn,old.Source);
        var definitionVaal=new SummonCatalog.Definition(SkillsFor(conn).EffectId(regular.Effect)!,plan.Family,
            plan.Family=="VaalAbsolution" ? "Metadata/Monsters/TemplarJudge/AbsolutionTemplarJudgeVaal"
                : "Metadata/Monsters/Axis/AxisEliteSoldierDominatingBlowVaal",
            plan.Family=="VaalAbsolution" ? "base_max_number_of_vaal_absolution_sentinels" : "base_max_number_of_vaal_dominated_monsters", "");
        var upgrade=new Dictionary<uint,int>();
        foreach(string name in new[]{"vaal_upgrade_minion_damage_+%_final","vaal_upgrade_minion_damage_taken_+%_final"})
            if(Stats.Key(name) is uint key) upgrade[key]=GolemStat(gem.Stats,name);
        return await SummonFighterCore(remote,stream,ciphers,conn,old.Source,definitionVaal,old.Monster.Walk.Position(now),
            current:()=>current()&&GolemCurrent(conn,old)&&!old.Monster.Dead&&SameTriggerSource(gem,SupportedSkill(conn,source)),
            remaining:duration,replacingOld:old,triggered:true,
            inheritedMods:old.InheritedMods,rarity:old.Monster.Rarity,retainedStats:upgrade)!=0;
    }
}
