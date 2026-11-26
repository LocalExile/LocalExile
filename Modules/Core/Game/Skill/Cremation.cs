using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static bool CremationEffect(string? effect) => effect is "Cremation" or "CremationAltX" or "CremationAltY" or "CremationRoyale";

    public sealed partial class ProjectileSession
    {
        const string GeyserArt = "Metadata/Effects/Spells/corpse_eruption/CorpseEruptionGroundFX.ao";
        const string GeyserImpactArt = "Metadata/Effects/Spells/corpse_eruption/CorpseEruptionMortarExplode_01.ao";
        const string GeyserProjectile = "Metadata/Projectiles/CorpseEruptionProjectile";
        const int GeyserSpread = 21;
        readonly LinkedList<uint> cremationGeysers = new();

        void ReleaseCremation(ushort skill, (int X, int Y) aim, uint target, bool volcano, double now,
            Func<bool> current, Action<(int X, int Y), int, double> hit, double more)
        {
            var gem = SupportedSkill(Player, skill);
            var stats = CharacterPlayerStats(Player);
            var combined = MergeSkillStats(stats, gem.Stats);
            int Own(uint key) => gem.Stats.GetValueOrDefault(key);
            int Owner(uint key) => stats.GetValueOrDefault(key);
            int Get(uint key) => combined.GetValueOrDefault(key);
            int limit = Math.Max(0, Get(Stat.CorpseErruptionBaseMaximumNumberOfGeyers));
            if (limit == 0) return;
            double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Owner, Own);
            double rate = 1 + Get(Stat.CremationFiresProjectilesFasterPlusPctFinal) / 100d;
            double interval = rate > 0 ? Own(Stat.CremationBaseFiresProjectileEveryXMs) / (1000 * rate) : 0;
            if (duration <= 0 || interval <= 0) return;

            var at = aim;
            int radius = SkillRadius(Player, gem.Stats, spell: true, gem.Effect);
            if (!volcano)
            {
                var corpse = target == 0 ? PlacedSpellCorpses(aim, PlacedCorpseSearchRadius, now).FirstOrDefault()
                    : new(target, Player.Area.Monsters.GetValueOrDefault(target)!);
                if (corpse.Value is not { } victim || !IsCorpse(victim) || !victim.Hostile || victim.Ailments.Shattered) return;
                at = victim.Walk.Position(now);
                ConsumeSpellCorpse(corpse.Key, victim, now);
                Effect(at.X, at.Y, DetonateDeadArt, ProjectileRuntime.Time(.5), radius);
                ApplyCorpseExplosion(skill, victim.MaxLife, at, radius, now, more: more);
            }
            while (cremationGeysers.Count >= limit) RemoveCremationGeyser(cremationGeysers.First!);
            uint id = Player.TakeObjectId();
            var geyser = cremationGeysers.AddLast(id);
            var art = new EffectObject(at.X, at.Y, GeyserArt);
            Player.TrackProjectileObject(id);
            Create(id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), art.Blob()), wake: true);

            int count = GatewayProjectileDefaults.Resolve(GeyserProjectile, Get, 1, attack: false).Count;
            count = Math.Clamp(count, 1, 32);
            int chance = Own(Stat.CremationChanceToExplodeNearbyCorpseWhenFiringProjectiles);
            int spread = SkillRadius(Player, GeyserSpread, gem.Stats, spell: true, gem.Effect, stats);
            long end = Runtime.Now + ProjectileRuntime.Time(duration);
            long spacing = Math.Max(1, ProjectileRuntime.Time(interval / count));
            Runtime.Schedule(end, () => { if (geyser.List is not null) RemoveCremationGeyser(geyser); });
            Runtime.Schedule(Runtime.Now + spacing, Fire);

            void Fire()
            {
                if (geyser.List is null || !Active || !current() || Runtime.Now >= end) return;
                double shotAt = ProjectileWorldTime(Player);
                var random = AreaRng(Player);
                if (chance > 0 && random.Next(100) < chance)
                {
                    var fresh = PlacedSpellCorpses(at, spread, shotAt).FirstOrDefault();
                    if (fresh.Value is { } consumed)
                    {
                        var pos = consumed.Walk.Position(shotAt);
                        ConsumeSpellCorpse(fresh.Key, consumed, shotAt);
                        Effect(pos.X, pos.Y, DetonateDeadArt, ProjectileRuntime.Time(.5), radius);
                        ApplyCorpseExplosion(skill, consumed.MaxLife, pos, radius, shotAt, more: more);
                    }
                }
                double angle = random.NextDouble() * Math.Tau;
                double distance = Math.Sqrt(random.NextDouble()) * spread;
                var destination = (X: at.X + (int)Math.Round(Math.Cos(angle) * distance),
                    Y: at.Y + (int)Math.Round(Math.Sin(angle) * distance));
                double speed = Math.Max(1, 90 * (1 + Get(Stat.ProjectileSpeedPlusPct) / 100d));
                Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(.17 + distance / speed), () =>
                {
                    if (!Active || !current()) return;
                    Effect(destination.X, destination.Y, GeyserImpactArt, ProjectileRuntime.Time(.5), radius);
                    hit(destination, radius, ProjectileWorldTime(Player));
                });
                Runtime.Schedule(Runtime.Now + spacing, Fire);
            }
        }

        void RemoveCremationGeyser(LinkedListNode<uint> geyser)
        {
            uint id = geyser.Value;
            cremationGeysers.Remove(geyser);
            Player.UntrackProjectileObject(id);
            WorldRecords(id, () => [InstanceProtocol.RemoveObject(id)], remove: true);
        }

        void ClearCremationGeysers()
        {
            foreach (uint id in cremationGeysers) RetireSkillEffectObject(id);
            cremationGeysers.Clear();
        }
    }
}
