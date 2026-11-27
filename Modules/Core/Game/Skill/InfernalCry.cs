using System.Collections.Concurrent;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string CombustEffect = "Combust";
    const ushort InfernalCryDebuffRow = 1539;
    const int InfernalCryExplosionRadius = 22;
    const string InfernalCryCorpseArt = "Metadata/Effects/Spells/infernal_blow/new_explosion_big.ao";

    sealed record CombustSource(PlayerSkillSource Skill, GemSourceLifetimes.SourceLifetime Lifetime);

    internal sealed record InfernalCryMark(ProjectileSession Owner, double Until, int Radius, int LifePercent,
        IReadOnlyDictionary<uint, int> Stats, IReadOnlyList<GemSupports.Applied> Supports);

    internal sealed record InfernalCryDeath(InfernalCryMark Mark, uint Entity, SpawnedMonster Corpse, (int X, int Y) At, long Time);

    static readonly RuleTable<SpawnedMonster, InfernalCryMark> InfernalCryMarks = new();
    static readonly RuleTable<ProjectileSession, ConcurrentQueue<InfernalCryDeath>> InfernalCryDeaths = new();

    internal static void ApplyInfernalCryDebuffLocked(Player player, ushort skillId, int effect,
        IReadOnlyDictionary<uint, int> own, (int X, int Y) at, int warcryRadius, double now)
    {
        int percent = own.GetValueOrDefault(Stat.InfernalCryPctMaxLifeAsFireOnDeath);
        if (percent <= 0) return;
        var session = ProjectilesFor(player);
        if (!session.Active) return;
        var playerStats = CharacterPlayerStats(player);
        double duration = SkillDurationModifiers.Seconds(own.GetValueOrDefault(Stat.BaseSecondarySkillEffectDuration),
            playerStats.GetValueOrDefault, own.GetValueOrDefault, DurationKinds(player, effect))
            * SkillDurationModifiers.CallToArmsMore(playerStats.GetValueOrDefault);
        if (duration <= 0) return;
        int radius = SkillRadius(player, InfernalCryExplosionRadius, own, spell: false, effect, playerStats);
        var supports = skillId == 0 ? [] : SupportedSkill(player, skillId).Supports;
        foreach (var (id, enemy) in player.Area.MonstersNear(at, warcryRadius))
        {
            if (enemy is not { Hostile: true, Dead: false, Life: > 0, WorldHidden: false }
                || Dist(at, enemy.Walk.Position(now)) > warcryRadius) continue;
            double remaining = duration / Math.Max(.01, enemy.RecipientDebuffRate);
            if (InfernalCryMarks.TryGetValue(enemy, out var old) && old.Owner.Player.Id != player.Id)
                player.Area.Buffs.Clear(id, InfernalCryDebuffRow, old.Owner.Player.Id);
            InfernalCryMarks.AddOrUpdate(enemy, new(session, now + remaining, radius, percent, own, supports));
            player.Area.Buffs.Set(id, new(InfernalCryDebuffRow, player.Id, skillId, U68: Buff.Seconds(remaining)));
        }
    }

    static void QueueInfernalCryDeath(uint id, SpawnedMonster monster, double now, ProjectileSession? current = null)
    {
        if (!InfernalCryMarks.TryGetValue(monster, out var mark) || !InfernalCryMarks.Remove(monster)) return;
        var owner = mark.Owner;
        if (!owner.Active) return;
        owner.Player.Area.Buffs.Clear(id, InfernalCryDebuffRow, owner.Player.Id);
        if (now >= mark.Until) return;
        var death = new InfernalCryDeath(mark, id, monster, monster.Walk.Position(now), ProjectileRuntime.Time(now));
        if (ReferenceEquals(current, owner))
            owner.ScheduleInfernalCryDeath(death);
        else
            InfernalCryDeaths.GetOrCreateValue(owner).Enqueue(death);
    }

    public sealed partial class ProjectileSession
    {
        void DrainInfernalCryDeaths()
        {
            if (!InfernalCryDeaths.TryGetValue(this, out var deaths)) return;
            while (deaths.TryDequeue(out var death)) ScheduleInfernalCryDeath(death);
        }

        internal void ScheduleInfernalCryDeath(InfernalCryDeath death)
        {
            bool Current() => Player.PlayerLife > 0 && !Player.Closed && !Player.LeavingArea;
            ScheduleContact(death.Time, Current, () =>
            {
                var corpse = death.Corpse;
                var current = Player.Area.Monsters.GetValueOrDefault(death.Entity);
                if (current != corpse && (current is not null || !corpse.CorpseConsumed)) return;
                var mark = death.Mark;
                double now = ProjectileWorldTime(Player);
                if (Player.Connection.Connected && TakeExplosionArt(Player, now))
                    Effect(death.At.X, death.At.Y, InfernalCryCorpseArt, ProjectileRuntime.Time(2), mark.Radius);
                var stats = CharacterPlayerStats(Player);
                int StatValue(uint key) => ActorScale.Add(key, stats.GetValueOrDefault(key), mark.Stats.GetValueOrDefault(key));
                double raw = (corpse.MaxLifeWithoutParty ?? corpse.MaxLife) * (double)mark.LifePercent / 100;
                var packet = PlayerHitDamage.Resolve(DamagePacket.Of(DamageType.Fire, raw, raw), StatValue,
                    DamageStats.Conversion(stats, Stats, attack: null), spell: false, area: true, secondary: true,
                    supportModifiers: mark.Supports.SelectMany(s => s.Stats).Select(s => (s.Key, s.Value)));
                ReleaseSkillCorpseExplosion(this, death.Entity, corpse, death.At, now, mark.Radius, packet,
                    Penetration.Of(stats, Stats, attack: false, skill: mark.Stats),
                    StatValue(Stat.CannotPenetrateOrIgnoreElementalResistances), "Infernal Cry");
            });
        }
    }

    internal static void GrantInfernalCryExertsLocked(Player player, ushort skillId, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> stats)
    {
        var cry = CryOf(player);
        cry.InfernalExerts = WarcryExertionCount(own.GetValueOrDefault(Stat.SkillEmpowersNextXMeleeAttacks), stats, own, AreaRng(player));
        cry.InfernalDisplay = ResolveWarcryDisplaySkill(player, skillId, "InfernalCry");
        cry.Combust = null;
        SyncExertedAttackBuffsLocked(player);
        if (cry.InfernalExerts == 0 || CurrentPlayerSkillSourceLocked(player, skillId) is not { Active: true } warcry)
            return;
        var catalog = SkillsFor(player);
        var source = PlayerSkillSourcesLocked(player).FirstOrDefault(row => row.Active
            && row.Identity.Host == warcry.Identity.Host && row.Identity.Socket == warcry.Identity.Socket
            && row.Identity.Source == warcry.Identity.Source && catalog.EffectId(row.Effect) == CombustEffect);
        if (source is not null)
            cry.Combust = new(source, CaptureGemSourceLifetimeLocked(player, source));
    }

    static void ReleaseCombust(Player player, CombustSource source, (int X, int Y) contact,
        double at)
    {
        var skill = source.Skill;
        var session = ProjectilesFor(player);
        int epoch = player.ProjectileEpoch;
        bool Current() => CurrentGameWork && player.ProjectileActive(epoch) && player.PlayerLife > 0 && !player.Paused
            && GemSourceLifetimeCurrentLocked(player, skill.Id, skill.Effect, source.Lifetime);
        session.ScheduleContact(ProjectileRuntime.Time(at), Current, () =>
        {
            if (CharacterPlayerStats(player).GetValueOrDefault(Stat.CombustIsDisabled) != 0
                || GemSkillInstanceDisabled(player, skill.Effect, skill.Id, triggered: true) is not null) return;
            var supported = SupportedSkill(player, skill.Id);
            bool lockstep = player.Connection.LockstepClient;
            ushort sequence = NextLockstepSequence(player);
            if (!lockstep) sequence &= 0x7fff;
            session.World(player.Id, InstanceProtocol.InstantAction(player.Id, skill.Id, contact.X, contact.Y,
                sequence: sequence, flags: lockstep ? (ushort)0x101 : (ushort)0, triggered: true),
                $"0x01ce Combust at {contact}");
            var use = (skill.Id, skill.Effect, Name: CombustEffect, supported.Level, supported.Quality, skill.Gem);
            var hit = AreaSkillHitForUse(null, player, skill.Id, use, contact, contact, immediate: true,
                sequence: sequence, melee: true, triggeredCurrent: Current, scheduledSession: session,
                cryExerted: false, allowMeleeSplash: false, nearbyOnly: true);
            if (!hit.IsCompleted) throw new InvalidOperationException("A scheduled Combust contact must not yield.");
            hit.GetAwaiter().GetResult();
        });
    }
}
