using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

internal sealed partial class HarbingerGameplay
{
    sealed record CurseCast(ushort Skill, double At, (int X, int Y) Aim, Func<bool> Current);
    readonly HarbingerCurseSources curseSources = new(new ModCatalog());
    bool? CastCurse(ClientState conn, ushort skill, (int X, int Y) aim, Func<bool> current)
    {
        lock (conn.Monsters)
        {
            var gem = SupportedSkill(conn, skill);
            int? duration = CastMs(conn, gem.Effect, skill);
            if (duration is null || !current()) return false;
            Get(conn).CurseCast = new(skill, Now(conn) + duration.Value / 1000d, aim, current);
            return true;
        }
    }
    HarbingerCurses.Target Target(uint id, SpawnedMonster m) => new(id, m,
        m.Rarity is MonsterRarity.Rare or MonsterRarity.Unique, curseSources.Get(m, "immune_to_curses") != 0,
        curseSources.Get(m, "hexproof") != 0, curseSources.Get(m, "reflect_curses") != 0)
        { Rate = RecipientEffectTime.Rate(5, name => m.Mods.Effects[name], temporalChains: true) };

    static void WithSelfCurse(ClientState conn, State state, Dictionary<uint, int> raw)
    {
        int Get(string name) => Stats.Key(name) is uint k ? conn.PlayerStats.GetValueOrDefault(k, raw.GetValueOrDefault(k)) : 0;
        var self = state.Curses.SelfStats(Now(conn), conn.PlayerLife > 0, Get);
        if (Stats.Key("temporal_chains_action_speed_+%_final") is uint slow) raw[slow] = raw.GetValueOrDefault(slow) + self.Slow;
        if (Stats.Key("buff_time_passed_+%_other_than_temporal_chains") is uint other) raw[other] = raw.GetValueOrDefault(other) + self.OtherEffects;
    }
    void ReconcileCurses(ClientState conn, State state, double now, List<byte> bytes)
    {
        if (conn.PlayerLife == 0) { state.Curses.Clear(); state.CurseCast = null; }
        var raw = CharacterStatSources(conn).Stats;
        int Player(string name) => Stats.Key(name) is uint k ? conn.PlayerStats.GetValueOrDefault(k, raw.GetValueOrDefault(k)) : 0;
        var living = conn.Monsters.Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.Life > 0)
            .Select(p => Target(p.Key, p.Value)).ToArray();
        HarbingerTemporalChains.Source Source(ushort skill, bool self)
        {
            var own = SupportedSkill(conn, skill).Stats;
            int Own(string name) => Stats.Key(name) is uint k ? own.GetValueOrDefault(k) : 0;
            return HarbingerTemporalChains.From(Player, Own, self);
        }
        int Radius(ushort skill)
        {
            var own = SupportedSkill(conn, skill).Stats;
            int Get(string name) => Stats.Key(name) is uint k ? Player(name) + own.GetValueOrDefault(k) : 0;
            return SkillArea.Radius(OwnAuraRestriction.IsAura(own) ? AuraCatalog.BaseRadius : 16,
                name => name == SkillArea.BaseIncreased ? Get(name) + Player("curse_area_of_effect_+%") : Get(name), spell: true);
        }
        var changes = new List<HarbingerCurses.Change>();
        ushort? previousSelf = state.PresentedSelf;
        if (state.CurseCast is { } cast && now >= cast.At)
        {
            state.CurseCast = null;
            var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == cast.Skill);
            if (cast.Current() && SkillsFor(conn).EffectId(gem.Effect) == "TemporalChains"
                && GemSkillInstanceDisabled(conn, gem.Effect, cast.Skill) is null)
                changes.AddRange(state.Curses.Apply(living.Where(t => Dist(((SpawnedMonster)t.Identity).Walk.Position(now), cast.Aim) <= Radius(cast.Skill)),
                    cast.Skill, now, Source(cast.Skill, false), Player, () => conn.NextBuff++, Source(cast.Skill, true), Player("reflect_curses") != 0));
        }
        var aura = conn.Auras.Values.FirstOrDefault(a => a.EffectId == "TemporalChains");
        if (aura is not null && GemSkillInstanceDisabled(conn, aura.Effect, aura.SkillId) is not null) aura = null;
        var inside = aura is null ? [] : living.Where(t => Dist(((SpawnedMonster)t.Identity).Walk.Position(now), conn.Walk.Position(now)) <= Radius(aura.SkillId)).ToArray();
        changes.AddRange(state.Curses.Reconcile(living, inside, aura?.SkillId,
            aura is null ? null : Source(aura.SkillId, false), now, Player, () => conn.NextBuff++));
        foreach (var change in changes)
        {
            var m = (SpawnedMonster)change.Target.Identity;
            double oldRate = RecipientEffectTime.Rate(2, name => name == "buff_time_passed_+%_other_than_temporal_chains"
                ? change.Previous?.OtherEffects ?? 0 : m.Mods.Effects[name]);
            double newRate = RecipientEffectTime.Rate(2, name => name == "buff_time_passed_+%_other_than_temporal_chains"
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
            if (change.Previous is { } old) bytes.AddRange(InstanceEntities.RemoveBuff(change.Target.Id, old.Instance));
            var native = new List<(uint, int)>();
            foreach (string name in new[] { "buff_time_passed_+%_other_than_temporal_chains", "temporal_chains_action_speed_+%_final" })
                if (Stats.Key(name) is uint k) native.Add((k, name == "buff_time_passed_+%_other_than_temporal_chains"
                    ? m.Mods.Effects[name] + (change.Current?.OtherEffects ?? 0)
                    : change.Current?.Slow(change.Target.Rare) ?? 0));
            bytes.AddRange(InstanceEntities.StatsUpdate(change.Target.Id, native));
            if (change.Current is { } current)
                bytes.AddRange(HarbingerCurses.Packet(change.Target.Id, current, now));
        }
        var selfStats = state.Curses.SelfStats(now, conn.PlayerLife > 0, Player);
        if (previousSelf != state.Curses.Self?.Instance || selfStats != state.PresentedSelfStats)
        {
            if (previousSelf is ushort old) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, old));
            bytes.AddRange(InstanceEntities.StatsUpdate(PlayerId, PlayerStatsWithCleared(conn)));
            if (state.Curses.Self is { } self) bytes.AddRange(HarbingerCurses.Packet(PlayerId, self, now));
            state.PresentedSelf = state.Curses.Self?.Instance;
            state.PresentedSelfStats = selfStats;
        }
        if (changes.Count > 0)
            foreach (var (packet, _) in MonsterAilmentChangesLocked(conn, now)) bytes.AddRange(packet);
    }
}
