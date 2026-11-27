using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static bool DetonateDeadEffect(string? effect) => effect is "DetonateDead" or "DetonateDeadAltX" or "DetonateDeadAltY" or "VaalDetonateDead";
    static bool VolatileDeadEffect(string? effect) => effect is "VolatileDead" or "VolatileDeadAltX" or "VolatileDeadAltY";
    static bool CorpseDamageSpell(string? effect) => DetonateDeadEffect(effect) || VolatileDeadEffect(effect);

    public sealed partial class ProjectileSession
    {
        const int PlacedCorpseSearchRadius = 60;
        const string DetonateDeadArt = "Metadata/Effects/Spells/detonate_dead_v2/blast_scalable.ao";

        internal void SchedulePlacedDetonateDead(PlacedAreaHit placement, Func<bool> current)
        {
            Runtime.Schedule(Runtime.Now, () =>
            {
                if (!Active || !current()) return;
                var device = placement.Device;
                double now = ProjectileWorldTime(Player);
                var corpse = PlacedSpellCorpses(device.At, PlacedCorpseSearchRadius, now).FirstOrDefault();
                if (corpse.Value is null) return;
                var at = corpse.Value.Walk.Position(now);
                int radius = SkillRadius(Player, device.Own, spell: true, device.Effect);

                ConsumeSpellCorpse(corpse.Key, corpse.Value, now, playerConsumes: false);
                Effect(at.X, at.Y, DetonateDeadArt, ProjectileRuntime.Time(1), radius);
                placement.Apply(Player, at, radius, now);
                ApplyCorpseExplosion(device.Skill, corpse.Value.MaxLife, at, radius, now, placement);
            });
        }

        IEnumerable<KeyValuePair<uint, SpawnedMonster>> PlacedSpellCorpses((int X, int Y) at, int radius, double now) =>
            Player.Area.Monsters.Where(entry => IsCorpse(entry.Value) && entry.Value.Hostile && !entry.Value.Ailments.Shattered
                && Dist(entry.Value.Walk.Position(now), at) <= radius)
            .OrderBy(entry => Dist(entry.Value.Walk.Position(now), at)).ThenBy(entry => entry.Key);

        void ConsumeSpellCorpse(uint id, SpawnedMonster corpse, double now, bool playerConsumes = true)
        {
            corpse.CorpseConsumed = true;
            if (playerConsumes)
            {
                NoteCorpseConsumedLocked(Player, corpse);
                Player.RecentEvents.Record(StatConditions.ConsumedCorpse, now);
            }
            Player.Area.Monsters.Remove(id);
            Player.Instance.World.MonsterOrigins.TryRemove(id, out _);
            WorldRecords(id, () => [InstanceProtocol.RemoveObject(id)], remove: true);
        }

        internal void ApplyCorpseExplosion(ushort skill, uint life, (int X, int Y) at, int radius, double now,
            PlacedAreaHit? placement = null, double more = 1, bool minionExplosion = false)
        {
            var owner = CharacterPlayerStats(Player);
            var supported = SupportedSkill(Player, skill);
            var device = placement?.Device;
            var caster = device is { } proxy ? proxy.Offense?.Caster ?? TotemCasterStats(owner) : owner;
            var own = device?.Own ?? supported.Stats;
            var combined = MergeSkillStats(caster, own);
            int Get(uint stat) => GolemStat(combined, stat);
            int perMille = Get(Stat.CorpseExplosionMonsterLifePermillageFire);
            // Some effects, including Royale Cremation, store the same base as a whole percent.
            double fraction = minionExplosion ? Get(Stat.SkillMinionExplosionLifePct) / 100d
                : perMille != 0 ? perMille / 1000d : Get(Stat.CorpseExplosionMonsterLifePct) / 100d;
            double basis = life * fraction;
            if (basis <= 0) return;
            var packet = DamagePacket.Of(DamageType.Fire, basis, basis);
            var conversion = DamageStats.Conversion(combined, Stats, attack: null);
            var modifiers = supported.Supports.SelectMany(s => s.Stats).Select(s => (s.Key, s.Value)).ToList();
            var types = Supports.Of(supported.Effect)?.Types.ToHashSet() ?? [];
            double multiplier = placement?.DamageMore ?? more;
            var hit = PlayerHitDamage.Resolve(packet, Get, conversion, spell: false, area: true, projectile: false,
                supportModifiers: modifiers, skillTypes: types, secondary: true).Scale(multiplier);
            if (hit.IsZero) return;
            var rng = AreaRng(Player);
            var penetration = Penetration.Of(caster, Stats, false, own);
            var ailmentRows = AilmentPanelStats.Compute(new("secondary", false), packet, conversion, Get);
            var ignite = RepeatAilment(IgniteSettingsFrom(ailmentRows, "secondary", Get), multiplier);
            var poison = RepeatAilment(PoisonSettingsFrom(ailmentRows, "secondary", Get), multiplier);
            var blind = BlindHitInput.Capture(caster, own);

            foreach (var (id, monster) in Player.Area.MonstersNear(at, radius).ToArray())
            {
                if (monster is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false, OwnerId: null }
                    || MonsterAsleep(Player, id) || Dist(monster.Walk.Position(now), at) > radius
                    || !MonsterPresent(Player, id, monster)) continue;
                var resisted = MitigateMonsterHit(monster, hit.Roll(rng), now, penetration, Get(Stat.CannotPenetrateOrIgnoreElementalResistances));
                int damage = device is null ? MonsterTakesMitigated(monster, resisted, spell: false, now, random: rng)
                    : MonsterTakesTrapOrMineHit(monster, resisted, spell: false, now, rng);
                int chaos = MonsterHitChaosDamage(monster, resisted, spell: false, now, damage,
                    device is null ? 0 : BearTrapDamageTaken(monster, now));
                if (ApplyTypedMonsterHitLocked(Player, monster, damage, chaos,
                        playerStats: device is null ? caster : null, allyAt: device is null ? null : at) is not { } accepted) continue;
                if (device?.Mine == true) LeechPlacedMineHitLocked(Player, monster, resisted, spell: false, damage, now, owner, id);
                if (accepted.StageStarts)
                {
                    CommitProjectileBossStage(this, id, monster, Runtime.Now);
                    accepted = accepted with { StageStarts = false };
                }
                bool playerHit = device is null;
                if (!monster.Dead && damage > 0)
                {
                    bool previousSecondary = Player.Reservations.Secondary;
                    Player.Reservations.Secondary = true;
                    try
                    {
                        AilmentsOnHitLocked(Player, id, monster, resisted, spell: false, critical: false, 100,
                            caster, own, now, rng, sourceEffect: supported.Effect, poison: poison, sourceSkill: skill, blind: blind,
                            hitOrigin: at, playerHit: playerHit, attackerHasReversedStat: playerHit);
                        IgniteOnHitLocked(this, id, monster, ignite, critical: false, now, playerIgnite: playerHit);
                    }
                    finally { Player.Reservations.Secondary = previousSecondary; }
                }
                if (!playerHit) OwnedKillLocked(monster, accepted.Life);
                CommitAcceptedHitLocked(Player, id, monster, accepted, damage, now,
                    minionExplosion ? "Bodyswap minion explosion" : "corpse explosion", playerKill: playerHit);
            }
        }
    }
}
