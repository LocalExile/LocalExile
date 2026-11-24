using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    public sealed partial class ProjectileSession
    {
        const string SacredWispVariety = "Metadata/Monsters/SacredWisp/SacredWisp_";
        const string SacredWispSupport = "SupportSacredWisps";

        sealed record SacredWispStats(int BaseLimit, int DurationMs, int AttackChance, int PresenceChance);

        sealed class SacredWisps(ushort skill, SupportedGem source, int cap, SacredWispStats native)
        {
            public readonly ushort Skill = skill;
            public readonly SupportedGem Source = source;
            public readonly List<(uint Id, SpawnedMonster Body)> Actors = new(cap);
            public readonly int Limit = cap;
            public readonly SacredWispStats Native = native;
            public long Expires;
        }
        SacredWisps? sacredWisps;

        bool CurrentSacredWisps(SacredWisps wisps) => Active && ReferenceEquals(sacredWisps, wisps)
            && Player.PlayerLife > 0 && WeaponProfileFor(Player, activeSet: true).Wand
            && SameTriggerSource(wisps.Source, SupportedSkill(Player, wisps.Skill));

        void SacredWispsOnHit(ProjectileOffense shot)
        {
            if (!Active || Player.PlayerLife == 0 || !WeaponProfileFor(Player, activeSet: true).Wand) return;
            var source = SupportedSkill(Player, shot.SourceSkill);
            if (!source.RequirementsMet || source.Effect != shot.Effect
                || source.Supports.FirstOrDefault(s => s.Effect.Id == SacredWispSupport) is not { } support
                || PlacedKind(source) != (false, false) || GolemStat(source.Stats, Stat.IsTotem) != 0) return;
            var active = sacredWisps is { } candidate && candidate.Skill == shot.SourceSkill
                && Runtime.Now < candidate.Expires && SameTriggerSource(candidate.Source, source) ? candidate : null;
            var native = active?.Native ?? NativeSacredWispStats(support.Gem.Level);
            const uint capStat = Stat.BaseNumberOfSacredWispsAllowed;
            var stats = PlayerStatValues(Player);
            int cap = (int)Math.Clamp(native.BaseLimit + (long)source.Stats.GetValueOrDefault(capStat)
                + stats.GetValueOrDefault(capStat), 0, 200);
            double duration = SkillDurationModifiers.Seconds(native.DurationMs,
                key => GolemStat(stats, key), key => GolemStat(source.Stats, key));
            if (cap <= 0 || duration <= 0) return;
            long until = Runtime.Now + ProjectileRuntime.Time(duration);
            if (active is not null && active.Limit == cap)
            {
                active.Expires = until;
                return;
            }

            EndSacredWisps();
            var current = sacredWisps = new(shot.SourceSkill, source, cap, native) { Expires = until };
            Enqueue(() => SpawnSacredWisps(current));

            void Check()
            {
                if (!ReferenceEquals(sacredWisps, current)) return;
                if (Runtime.Now >= current.Expires || !CurrentSacredWisps(current)) { EndSacredWisps(); return; }
                Runtime.Schedule(Math.Min(current.Expires, Runtime.Now + ProjectileRuntime.Time(.25)), Check);
            }
            Runtime.Schedule(Math.Min(until, Runtime.Now + ProjectileRuntime.Time(.25)), Check);
        }

        SacredWispStats NativeSacredWispStats(int level)
        {
            var catalog = SkillsFor(Player);
            var native = catalog.EffectStats(catalog.EffectRow("SummonSacredWisp"), level)
                .ToDictionary(pair => pair.Item1, pair => pair.Item2);
            return new(native.GetValueOrDefault(Stat.BaseNumberOfSacredWispsAllowed),
                native.GetValueOrDefault(Stat.BaseSkillEffectDuration),
                native.GetValueOrDefault(Stat.SupportSacredWispsWispPctChanceToAttack),
                native.GetValueOrDefault(Stat.SupportSacredWispsWispAdditionalPctChanceToAttackWhenRareOrUniqueEnemyInPresence));
        }

        async Task SpawnSacredWisps(SacredWisps wisps)
        {
            if (!CurrentSacredWisps(wisps) || Monsters.Find(SacredWispVariety) is not { Supported: true } variety) return;
            var stats = new Dictionary<uint, int>(wisps.Source.Stats) { [Stat.SkillUsedBySacredWisp] = 1 };
            var skill = SkillsFor(Player).FindByRow(wisps.Skill, wisps.Source.Effect, wisps.Source.Level,
                [.. stats.Select(pair => (pair.Key, pair.Value))]);
            if (skill is null) return;
            bool Current() => CurrentSacredWisps(wisps) && Runtime.Now < wisps.Expires;
            for (int i = 0; i < wisps.Limit && Current(); i++)
            {
                var at = NextToPlayer(Player);
                await SpawnMonster(Player, variety, (byte)Math.Clamp(Player.Pools.Level, 1, 100), MonsterRarity.Normal,
                    at.X, at.Y, friendly: true, log: false, ownedSkills: [skill], ownedCurrent: Current,
                    owned: (id, actor) =>
                    {
                        actor.DamageImmune = true;
                        actor.NoRewards = true;
                        actor.Attack = null;
                        wisps.Actors.Add((id, actor));
                    });
            }
        }

        void SacredWispsOnRelease(ProjectileUse use)
        {
            if (sacredWisps is not { } wisps || Runtime.Now >= wisps.Expires || use.Emitter.Id != Player.Id
                || use.Payload is not ProjectileOffense { ActualCaster: null, Spell: false, FromTotem: false, FromTrapOrMine: false } shot
                || shot.SourceSkill != wisps.Skill || !CurrentSacredWisps(wisps)) return;
            double now = Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
            var at = Player.Walk.Position(now);
            bool inPresence = Player.Area.MonstersNear(at, PresenceRadius).Any(pair =>
                pair.Value is { Hostile: true, Dead: false, Life: > 0, Rarity: MonsterRarity.Rare or MonsterRarity.Unique }
                && Dist(at, pair.Value.Walk.Position(now)) <= PresenceRadius);
            int chance = wisps.Native.AttackChance + (inPresence ? wisps.Native.PresenceChance : 0);
            ProjectileOffense? copy = null;
            ProjectileOffense Copy()
            {
                var own = new Dictionary<uint, int>(shot.SkillStats) { [Stat.SkillUsedBySacredWisp] = 1 };
                own.Remove(Stat.SummonLivingLightningOnLightningHit);
                var caster = new Dictionary<uint, int>(PlayerStatValues(Player));
                caster.Remove(Stat.KeystoneAncestralBond);
                caster.Remove(Stat.DealNoDamageYourself);
                return shot.ScaleDamage(Math.Max(0, 1 + own.GetValueOrDefault(Stat.SkillUsedBySacredWispDamagePlusPctFinal) / 100d)) with
                {
                    ActualCaster = caster, ClientPredicted = false, CriticalTrigger = null, MirageSource = null,
                    FirstHit = null, ActionSequence = null, SkillStats = own, TinctureKill = null,
                    Leech = new(DamagePacket.Empty, DamagePacket.Empty, DamagePacket.Empty),
                };
            }
            foreach (var (id, body) in wisps.Actors)
            {
                if (body.Dead || !ReferenceEquals(Player.Area.Monsters.GetValueOrDefault(id), body)
                    || !RandomRolls.Chance(chance, 100, AreaRng(Player))) continue;
                var from = body.Walk.Position(now);
                var actor = ActorKey(id, body);
                WorldRecords(id, () => CurrentSacredWisps(wisps)
                    ? [InstanceProtocol.ActionStarted(id, from.X, from.Y, (int)Math.Round(use.Aim.X), (int)Math.Round(use.Aim.Y),
                        wisps.Skill, 0xffff, Player.MonsterOrderFlags), InstanceProtocol.ActionEnded(id)] : null);
                Runtime.Release(use with
                {
                    Seed = ++seed, Emitter = actor, Origin = Vector(from), ReturnAnchor = actor,
                    Payload = copy ??= Copy(), CanEmit = () => CurrentSacredWisps(wisps) && Runtime.Now < wisps.Expires
                        && !body.Dead && ReferenceEquals(Player.Area.Monsters.GetValueOrDefault(id), body),
                    ProjectilePayloads = use.ProjectilePayloads?.Select(payload => payload is ProjectileOffense offense
                        ? Copy(offense) : payload).ToArray(),
                });
            }
        }

        void EndSacredWisps(bool retire = false)
        {
            if (sacredWisps is not { } wisps) return;
            sacredWisps = null;
            foreach (var (id, actor) in wisps.Actors)
            {
                if (ReferenceEquals(Player.Area.Monsters.GetValueOrDefault(id), actor)) Player.Area.Monsters.Remove(id);
                Player.MinionOrigins.TryRemove(id, out _);
                if (retire) Player.RetiredCombatObjects.Add(id);
                else Enqueue(() => SendWorldRemoval(Player, id));
            }
        }
    }
}
