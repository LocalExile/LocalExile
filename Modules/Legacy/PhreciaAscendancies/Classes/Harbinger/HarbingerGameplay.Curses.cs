using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

internal sealed partial class HarbingerGameplay
{
    sealed record CurseCast(ushort Skill, double At, (int X, int Y) Aim, Func<bool> Current);
    readonly HarbingerCurseSources curseSources = new(new ModCatalog());
    bool? CastCurse(Player player, ushort skill, (int X, int Y) aim, Func<bool> current)
    {
        var gem = SupportedSkill(player, skill);
        int? duration = CastMs(player, gem.Effect, skill);
        if (duration is null || !current()) return false;
        Get(player).CurseCast = new(skill, Now(player) + duration.Value / 1000d, aim, current);
        return true;
    }
    HarbingerCurses.Target Target(uint id, SpawnedMonster m) => new(id, m,
        m.Rarity is MonsterRarity.Rare or MonsterRarity.Unique, curseSources.Get(m, Stat.ImmuneToCurses) != 0,
        curseSources.Get(m, Stat.Hexproof) != 0, curseSources.Get(m, Stat.ReflectCurses) != 0)
        { Rate = RecipientEffectTime.Rate(5, name => m.Mods.Effects[name], temporalChains: true) };

    static void WithSelfCurse(Player player, State state, Dictionary<uint, int> raw)
    {
        int Get(uint k) => raw.GetValueOrDefault(k);
        var self = state.Curses.SelfStats(Now(player), player.PlayerLife > 0, Get);
        if (Stat.TemporalChainsActionSpeedPlusPctFinal is var slow) raw[slow] = raw.GetValueOrDefault(slow) + self.Slow;
        if (Stat.BuffTimePassedPlusPctOtherThanTemporalChains is var other) raw[other] = raw.GetValueOrDefault(other) + self.OtherEffects;
    }
    void ReconcileCurses(Player player, State state, double now, List<byte> bytes)
    {
        if (player.PlayerLife == 0) { state.Curses.Clear(); state.CurseCast = null; }
        var raw = StatSourceValues(player);
        int Player(uint k) => raw.GetValueOrDefault(k);
        var living = player.Area.Monsters.Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.Life > 0)
            .Select(p => Target(p.Key, p.Value)).ToArray();
        HarbingerTemporalChains.Source Source(ushort skill, bool self)
        {
            var own = SupportedSkill(player, skill).Stats;
            int Own(uint k) => own.GetValueOrDefault(k);
            return HarbingerTemporalChains.From(Player, Own, self);
        }
        int Radius(ushort skill)
        {
            var own = SupportedSkill(player, skill).Stats;
            int Get(uint k) => Player(k) + own.GetValueOrDefault(k);
            return SkillArea.Radius(OwnAuraRestriction.IsAura(own) ? AuraCatalog.BaseRadius : 16,
                name => name == SkillArea.BaseIncreased ? Get(name) + Player(Stat.CurseAreaOfEffectPlusPct) : Get(name), spell: true);
        }
        var changes = new List<HarbingerCurses.Change>();
        ushort? previousSelf = state.PresentedSelf;
        if (state.CurseCast is { } cast && now >= cast.At)
        {
            state.CurseCast = null;
            var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == cast.Skill);
            if (cast.Current() && SkillsFor(player).EffectId(gem.Effect) == "TemporalChains"
                && GemSkillInstanceDisabled(player, gem.Effect, cast.Skill) is null)
                changes.AddRange(state.Curses.Apply(living.Where(t => Dist(((SpawnedMonster)t.Identity).Walk.Position(now), cast.Aim) <= Radius(cast.Skill)),
                    cast.Skill, now, Source(cast.Skill, false), Player, () => player.NextBuff++, Source(cast.Skill, true), Player(Stat.ReflectCurses) != 0));
        }
        var aura = player.Auras.Values.FirstOrDefault(a => a.EffectId == "TemporalChains");
        if (aura is not null && GemSkillInstanceDisabled(player, aura.Effect, aura.SkillId) is not null) aura = null;
        var inside = aura is null ? [] : living.Where(t => Dist(((SpawnedMonster)t.Identity).Walk.Position(now), player.Walk.Position(now)) <= Radius(aura.SkillId)).ToArray();
        changes.AddRange(state.Curses.Reconcile(living, inside, aura?.SkillId,
            aura is null ? null : Source(aura.SkillId, false), now, Player, () => player.NextBuff++));
        foreach (var change in changes)
        {
            var m = (SpawnedMonster)change.Target.Identity;
            double oldRate = RecipientEffectTime.Rate(2, name => name == Stat.BuffTimePassedPlusPctOtherThanTemporalChains
                ? change.Previous?.OtherEffects ?? 0 : m.Mods.Effects[name]);
            double newRate = RecipientEffectTime.Rate(2, name => name == Stat.BuffTimePassedPlusPctOtherThanTemporalChains
                ? change.Current?.OtherEffects ?? 0 : m.Mods.Effects[name]);
            // Keep damage per second and accrued fractions. Only the remaining BASE lifetime changes.
            m.Ignites.SetExpiryRate(now, newRate);
            m.Bleeds.SetExpiryRate(now, newRate);
            m.Poisons.SetExpiryRate(now, newRate);
            m.Ailments.SetExpiryRate(now, newRate);
            m.TauntedUntil = RecipientEffectTime.RetimedDeadline(m.TauntedUntil, now, oldRate, newRate);
            m.RecipientDebuffRate = newRate;
            m.ReceivedActionFactor = HarbingerTemporalChains.Factor(change.Current?.Slow(change.Target.Rare) ?? 0);
            m.ReceivedActionUntil = change.Current?.Clock.Deadline ?? now;
            m.ReceivedActionFloor = curseSources.Get(m, HarbingerActionSpeed.ActionFloor) != 0;
            if (change.Previous is { } old) player.Area.Buffs.ClearTag(change.Target.Id, old.Instance);
            var native = new List<(uint, int)>();
            foreach (uint name in new[] { Stat.BuffTimePassedPlusPctOtherThanTemporalChains, Stat.TemporalChainsActionSpeedPlusPctFinal })
                native.Add((name, name == Stat.BuffTimePassedPlusPctOtherThanTemporalChains
                    ? m.Mods.Effects[name] + (change.Current?.OtherEffects ?? 0)
                    : change.Current?.Slow(change.Target.Rare) ?? 0));
            SetStats(player, change.Target.Id, native);
            if (change.Current is { } current)
                HarbingerCurses.Show(player, change.Target.Id, player.Id, current, now);
        }
        var selfStats = state.Curses.SelfStats(now, player.PlayerLife > 0, Player);
        if (previousSelf != state.Curses.Self?.Instance || selfStats != state.PresentedSelfStats)
        {
            if (previousSelf is ushort old) player.Area.Buffs.ClearTag(player.Id, old);
            PublishPlayerStats(player);
            if (state.Curses.Self is { } self) HarbingerCurses.Show(player, player.Id, player.Id, self, now);
            state.PresentedSelf = state.Curses.Self?.Instance;
            state.PresentedSelfStats = selfStats;
        }
        if (changes.Count > 0)
            foreach (var (packet, _) in MonsterAilmentChangesLocked(player, now)) bytes.AddRange(packet);
    }
}
