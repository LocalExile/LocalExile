using GameServer.Core.World;

public static partial class Game
{
    const uint GuardianRequired = Stat.SupportGuardiansBlessingAuraOnlyEnabledWhileSupportMinionIsSummoned;
    const uint GuardianDamage = Stat.SupportGuardiansBlessingMinionPhysicalDamagePctOfMaximumLifeAndESTakenPerMinute;

    sealed record GuardianBlessingTiming(ushort Skill, ushort Buff, (uint Host, int Socket) Link, double Started);
    sealed class GuardianBlessingDamageCarry { public ushort Buff; public double Fraction; }
    static readonly RuleTable<Player, GuardianBlessingTiming> GuardianBlessingTimes = new();
    static readonly RuleTable<GolemState.Golem, GuardianBlessingDamageCarry> GuardianBlessingCarries = new();

    static (uint Host, int Socket)? GuardianBlessingLink(SupportedGem skill, string effect)
    {
        uint host = skill.WornGrant?.Host ?? skill.Host;
        var support = skill.Supports.FirstOrDefault(s => s.Effect.Id == effect);
        return host != 0 && support is not null ? (host, support.Gem.Socket) : null;
    }

    static bool MatchesGuardianBlessing(Player player, GolemState.Golem minion, (uint Host, int Socket) link)
    {
        if (minion.Monster.Dead || minion.Monster.Life == 0 || !GolemCurrent(player, minion)
            || minion.GemSource is { } lifetime && !player.GemSourceLifetimes.IsCurrent(lifetime)
            || player.Summons.Until.TryGetValue(minion.Id, out double expiry) && expiry <= AreaTime(player)) return false;
        var skill = SupportedSkill(player, minion.Source);
        return skill.RequirementsMet && skill.Effect == minion.Definition.Effect
            && (minion.SourceIdentity is null || skill.SourceGem is { } gem
                && ReferenceEquals(minion.SourceIdentity, PlayerInventories.GemIdentity(gem)))
            && GuardianBlessingLink(skill, "SupportGuardiansBlessingMinion") == link;
    }

    static bool GuardianBlessingAvailableLocked(Player player, SupportedGem skill) =>
        GolemStat(skill.Stats, GuardianRequired) == 0
        || GuardianBlessingLink(skill, "SupportGuardiansBlessing") is { } link
        && player.Golems.All.Values.Concat(player.Summons.Fighters.Values).Any(m => MatchesGuardianBlessing(player, m, link));

    static bool GuardianBlessingDisables(Player player, int effect, ushort skill) =>
        SkillsFor(player).IsAura(effect) && !GuardianBlessingAvailableLocked(player, SupportedSkill(player, skill));

    static void StartGuardianBlessingLocked(Player player, AuraCatalog.Aura aura, SupportedGem skill)
    {
        if (AuraHasStat(aura, GuardianRequired)
            && GuardianBlessingLink(skill, "SupportGuardiansBlessing") is { } link)
            GuardianBlessingTimes.AddOrUpdate(player, new(aura.SkillId, aura.Buff, link, AreaTime(player)));
    }

    static void ReconcileGuardianBlessingLocked(Player player)
    {
        if (player.Closed || player.LeavingArea || player.RageRetired) return;
        if (!GuardianBlessingTimes.TryGetValue(player, out var timing)) return;
        if (!player.Auras.TryGetValue(timing.Skill, out var aura) || aura.Buff != timing.Buff)
        {
            GuardianBlessingTimes.Remove(player);
            return;
        }
        var skill = SupportedSkill(player, aura.SkillId);
        if (skill.Effect == aura.Effect && GolemStat(skill.Stats, GuardianRequired) != 0
            && GuardianBlessingLink(skill, "SupportGuardiansBlessing") == timing.Link
            && GuardianBlessingAvailableLocked(player, skill)) return;

        GuardianBlessingTimes.Remove(player);
        player.Auras.Remove(aura.SkillId);
        EndReservationRuntimeLocked(player, aura);
        CharacterSaves.SetAuraActive(CharacterName(player), aura.EffectId, false);
        player.AuraStatsDirty = true;
    }

    static int GuardianBlessingMinionDamage(Player player, GolemState.Golem minion, double from, double to)
    {
        int rate = GolemStat(minion.Own, GuardianDamage);
        if (rate <= 0 || !GuardianBlessingTimes.TryGetValue(player, out var timing) || to <= timing.Started
            || !player.Auras.TryGetValue(timing.Skill, out var aura) || aura.Buff != timing.Buff || !aura.AffectsSelf
            || !MatchesGuardianBlessing(player, minion, timing.Link)) return 0;

        var carry = GuardianBlessingCarries.GetOrCreateValue(minion);
        if (carry.Buff != aura.Buff) { carry.Buff = aura.Buff; carry.Fraction = 0; }
        double elapsed = to - Math.Max(from, timing.Started);
        double taken = MonsterDotTaken(minion.Monster, DamageType.Physical, to)
            * Math.Max(0, 1 + GolemStat(minion.Own, Stat.SupportMinionDefensiveStanceMinionDamageTakenPlusPctFinal) / 100d);
        double total = carry.Fraction + ((double)minion.Monster.MaxLife + minion.MaximumShield) * rate / 6000 * elapsed * taken;
        double whole = Math.Floor(total);
        carry.Fraction = total - whole;
        return (int)Math.Min(int.MaxValue, whole);
    }
}
