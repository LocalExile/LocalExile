using System.Collections.Concurrent;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const ushort InfernalBlowDebuffRow = 1128;
    const int InfernalBlowChargeLimit = 6;
    const string InfernalBlowChargeArt = "Metadata/Effects/Spells/infernal_blow/new_explosion_big.ao";
    const string InfernalBlowCorpseArt = "Metadata/Effects/Spells/infernal_blow/new_explosion_small_blood.ao";

    sealed record InfernalBlowUse(ushort SkillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem,
        SupportedGem Source, SkillCriticalRoll? Critical, ushort Sequence, double Ruthless, int Repeat, bool Exerted);

    sealed class InfernalBlowMark(ProjectileSession session, InfernalBlowUse first, uint entity, SpawnedMonster? target)
    {
        public readonly ProjectileSession Session = session;
        public readonly InfernalBlowUse First = first;
        public readonly uint Entity = entity;
        public readonly SpawnedMonster? Target = target;
        public ushort Charges;
        public long Expires;
        public bool Retired;
    }

    static readonly RuleTable<SpawnedMonster, InfernalBlowMark> InfernalBlowMarks = new();
    static readonly RuleTable<Player, InfernalBlowMark> ImmolationMarks = new();
    static readonly RuleTable<ProjectileSession, ConcurrentQueue<(InfernalBlowMark Mark, long At)>> InfernalBlowDeaths = new();

    static bool IsInfernalBlow(string? effect) =>
        effect is "InfernalBlow" or "InfernalBlowAltX" or "InfernalBlowAltY" or "InfernalBlowRoyale";

    static void ApplyInfernalBlowContact(Player player, uint id, SpawnedMonster target, InfernalBlowUse use,
        bool immolation, bool splash, IReadOnlyDictionary<uint, int> playerStats, double now)
    {
        var session = ProjectilesFor(player);
        if (!session.Active) return;
        var stats = use.Source.Stats;
        double duration = SkillDurationModifiers.Seconds(stats.GetValueOrDefault(Stat.BaseSkillEffectDuration),
            playerStats.GetValueOrDefault, stats.GetValueOrDefault);
        long time = ProjectileRuntime.Time(now);
        bool found = immolation ? ImmolationMarks.TryGetValue(player, out var mark) : InfernalBlowMarks.TryGetValue(target, out mark);
        bool starts = !found || mark is null || mark.Retired || !mark.Session.Active || mark.Expires <= time;
        if (starts)
        {
            mark = new(session, use, immolation ? player.Id : id, immolation ? null : target);
            if (immolation) ImmolationMarks.AddOrUpdate(player, mark);
            else InfernalBlowMarks.AddOrUpdate(target, mark);
        }
        if (mark is null) return;
        if (!splash) mark.Charges++;
        mark.Expires = ProjectileRuntime.Time(now + duration);
        if (mark.Charges == InfernalBlowChargeLimit && (immolation || target.Life > 0))
        {
            // A full stack uses the strike that reached the cap. Expiry uses the first strike of that stack.
            DetonateInfernalBlow(mark, session, use);
            return;
        }
        player.Area.Buffs.Set(mark.Entity, new(InfernalBlowDebuffRow, mark.Session.Player.Id,
            Values: [immolation ? 0u : 1u], U68: Buff.Seconds(duration), Stacks: mark.Charges));
        if (starts) ScheduleInfernalBlowExpiry(mark);
    }

    static void ScheduleInfernalBlowExpiry(InfernalBlowMark mark)
    {
        var session = mark.Session;
        session.Runtime.Schedule(mark.Expires, () =>
        {
            if (mark.Retired || !session.Active || mark.Target is { Life: 0 }) return;
            if (session.Runtime.Now < mark.Expires)
            {
                ScheduleInfernalBlowExpiry(mark);
                return;
            }
            DetonateInfernalBlow(mark, session, mark.First);
        });
    }

    static void QueueInfernalBlowDeath(SpawnedMonster monster, double now, ProjectileSession? current = null)
    {
        if (!InfernalBlowMarks.TryGetValue(monster, out var mark) || mark.Retired || !mark.Session.Active)
            return;
        long at = ProjectileRuntime.Time(now);
        if (at > mark.Expires) return;
        if (ReferenceEquals(current, mark.Session))
            DetonateInfernalBlow(mark, mark.Session, mark.First, death: true);
        else
            InfernalBlowDeaths.GetOrCreateValue(mark.Session).Enqueue((mark, at));
    }

    static void DetonateInfernalBlow(InfernalBlowMark mark, ProjectileSession session, InfernalBlowUse use, bool death = false)
    {
        if (mark.Retired) return;
        mark.Retired = true;
        var player = session.Player;
        player.Area.Buffs.Clear(mark.Entity, InfernalBlowDebuffRow, mark.Session.Player.Id);
        if (mark.Target is { } enemy && InfernalBlowMarks.TryGetValue(enemy, out var existing) && ReferenceEquals(existing, mark))
            InfernalBlowMarks.Remove(enemy);
        else if (mark.Target is null && ImmolationMarks.TryGetValue(player, out existing) && ReferenceEquals(existing, mark))
            ImmolationMarks.Remove(player);
        if (!session.Active || mark.Charges == 0 && !death) return;
        // End the current strike before either explosion can hit its other victims.
        session.Runtime.Schedule(session.Runtime.Now, () => ReleaseInfernalBlow(mark, session, use, death));
    }

    static void ReleaseInfernalBlow(InfernalBlowMark mark, ProjectileSession session, InfernalBlowUse use, bool death)
    {
        var player = session.Player;
        if (!session.Active || player.PlayerLife == 0 || player.Closed || player.LeavingArea) return;
        if (mark.Target is { } target)
        {
            var current = player.Area.Monsters.GetValueOrDefault(mark.Entity);
            if (current != target && (!death || current is not null || !target.CorpseConsumed)) return;
        }
        double now = ProjectileWorldTime(player);
        var from = player.Walk.Position(now);
        var at = mark.Target?.Walk.Position(now) ?? from;
        var stats = use.Source.Stats;
        int percent = stats.GetValueOrDefault(Stat.InfernalBlowExplosionDamagePctOfTotalPerStack)
            + CharacterPlayerStats(player).GetValueOrDefault(Stat.InfernalBlowInfernalBlowExplosionDamagePctOfTotalPerStack);
        if (mark.Charges != 0 && percent > 0)
        {
            int radius = SkillRadius(player, stats, spell: false, use.Gem.Effect);
            session.ShowInfernalBlowExplosion(at, radius, corpse: false);
            var hit = AreaSkillHitForUse(use.Ruthless, player, use.SkillId, use.Gem, from, at,
                immediate: true, sequence: use.Sequence, melee: false, damageMore: (double)percent * mark.Charges / 100,
                criticalRoll: use.Critical, repeat: use.Repeat, radiusOverride: radius, scheduledSession: session,
                cryExerted: use.Exerted, infernalBlast: use);
            if (!hit.IsCompleted) throw new InvalidOperationException("Scheduled Infernal Blow explosion must not yield.");
            hit.GetAwaiter().GetResult();
        }
        if (death && mark.Target is { } corpse)
            ReleaseInfernalBlowCorpse(session, mark.Entity, corpse, use, at, now);
    }

    static void ReleaseInfernalBlowCorpse(ProjectileSession session, uint corpseId, SpawnedMonster corpse,
        InfernalBlowUse use, (int X, int Y) at, double now)
    {
        var player = session.Player;
        var own = use.Source.Stats;
        int percent = own.GetValueOrDefault(Stat.CorpseExplosionMonsterLifePct);
        if (percent <= 0) return;
        int radius = SkillRadius(player, own.GetValueOrDefault(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius),
            own, spell: false, use.Gem.Effect);
        session.ShowInfernalBlowExplosion(at, radius, corpse: true);
        if (!corpse.CorpseConsumed)
        {
            corpse.CorpseConsumed = true;
            player.Area.Monsters.Remove(corpseId);
            session.WorldRecords(corpseId, () => [InstanceProtocol.RemoveObject(corpseId)],
                "Infernal Blow consumes corpse", remove: true);
        }

        var stats = CharacterPlayerStats(player);
        int Get(uint key) => stats.GetValueOrDefault(key) + own.GetValueOrDefault(key);
        int ExplosionStat(uint key) => key == Stat.ElementalDamagePlusPct
            ? Get(key) + Get(Stat.ElementalDamageWithAttackSkillsPlusPct) : Get(key);
        double raw = (corpse.MaxLifeWithoutParty ?? corpse.MaxLife) * (double)percent / 100;
        var packet = PlayerHitDamage.Resolve(DamagePacket.Of(DamageType.Fire, raw, raw), ExplosionStat,
            DamageStats.Conversion(stats, Stats, attack: null), spell: false, area: true, secondary: true,
            supportModifiers: use.Source.Supports.SelectMany(s => s.Stats).Select(s => (s.Key, s.Value)));
        var pen = Penetration.Of(stats, Stats, attack: false);
        foreach (var (id, enemy) in player.Area.MonstersNear(at, radius))
        {
            if (!enemy.Hostile || enemy.Dead || enemy.Life == 0 || Dist(at, enemy.Walk.Position(now)) > radius)
                continue;
            var resisted = MitigateMonsterHit(enemy, packet, now, pen, Get(Stat.CannotPenetrateOrIgnoreElementalResistances));
            int damage = MonsterTakesMitigated(enemy, resisted, false, now);
            if (ApplyTypedMonsterHitLocked(player, enemy, damage,
                MonsterHitChaosDamage(enemy, resisted, spell: false, now, damage), allyAt: at) is not { } hit)
                continue;
            if (hit.StageStarts) CommitBossStage(player, id, enemy, now);
            if (hit.Life == 0) SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
            session.World(id, InstanceProtocol.LifeUpdate(id, hit.Life),
                $"Infernal Blow corpse explosion target=0x{id:x} damage={damage} life={hit.Life}/{enemy.MaxLife}");
            if (hit.Life == 0) CommitProjectileKill(session, id, enemy, session.Runtime.Now, AreaRng(player));
            if (enemy.ThresholdReached) session.Enqueue(() => QuestThreshold(player, id, enemy));
        }
    }

    public sealed partial class ProjectileSession
    {
        void DrainInfernalBlowDeaths()
        {
            if (!InfernalBlowDeaths.TryGetValue(this, out var deaths)) return;
            while (deaths.TryDequeue(out var item))
                Runtime.Schedule(Math.Max(Runtime.Now, item.At), () =>
                    DetonateInfernalBlow(item.Mark, this, item.Mark.First, death: true));
        }

        internal void ShowInfernalBlowExplosion((int X, int Y) at, int radius, bool corpse)
        {
            if (Player.Connection.Connected && TakeExplosionArt(Player, ProjectileWorldTime(Player)))
                Effect(at.X, at.Y, corpse ? InfernalBlowCorpseArt : InfernalBlowChargeArt,
                    ProjectileRuntime.Time(2), radius);
        }
    }
}
