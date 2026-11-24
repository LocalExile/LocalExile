using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public sealed partial class ProjectileSession
    {
        const string DivineBlastOrbArt = "Metadata/Effects/Spells/Discus_Slam/Holy_Orb.ao";
        const string DivineBlastExplosionArt = "Metadata/Effects/Spells/Discus_Slam/Holy_Explode.ao";

        sealed record DivineBlastOrb(uint Id, bool Repeating);
        readonly List<DivineBlastOrb> divineBlastOrbs = [];

        internal void PlaceDivineBlastOrb((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
            (int X, int Y) at, SupportedGem source, SkillCriticalRoll? critical)
        {
            if (!Active || CaptureGemSourceLifetimeLocked(Player, gem.Id, gem.Effect) is not { } lifetime) return;
            var stats = CharacterPlayerStats(Player);
            int Own(uint key) => source.Stats.GetValueOrDefault(key);
            double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration),
                key => stats.GetValueOrDefault(key), Own, DurationKinds(Player, gem.Effect));
            if (duration <= 0) return;
            int maximum = Own(Stat.DiscusSlamMaximumOrbCountAllowed);
            int rate = Own(Stat.DiscusSlamOrbActivateBaseRateMs);
            bool repeating = maximum > 0 && rate > 0;
            if (repeating)
            {
                // Two physical gems of Radiance still share the same maximum active orbs.
                while (divineBlastOrbs.Count(o => o.Repeating) >= maximum)
                    RemoveDivineBlastOrb(divineBlastOrbs.First(o => o.Repeating));
            }
            int radius = SkillRadius(Player, source.Stats, spell: false, gem.Effect);

            double spacing = repeating
                ? Math.Max(TickSeconds, rate / 1000d / Math.Max(.01, 1 + SupportedMainHandAttackSpeedPct(Player, gem.Id) / 100d))
                : duration;
            long start = Math.Max(Runtime.Now, ProjectileRuntime.Time(AreaTime(Player)));
            long end = start + ProjectileRuntime.Time(duration);
            long step = Math.Max(1, ProjectileRuntime.Time(spacing));
            var orb = new DivineBlastOrb(Player.TakeObjectId(), repeating);
            divineBlastOrbs.Add(orb);
            Player.ProjectileObjects.Add(orb.Id);
            Create(orb.Id, () => Current() && divineBlastOrbs.Contains(orb)
                ? DivineBlastEffect(orb.Id, DivineBlastOrbArt, at) : null, wake: true);
            Schedule(repeating ? start + step : end);
            Runtime.Schedule(end + 1, () => RemoveDivineBlastOrb(orb));

            bool Current() => Active && Player.PlayerLife > 0
                && GemSourceLifetimeCurrentLocked(Player, gem.Id, gem.Effect, lifetime)
                && SupportedSkill(Player, gem.Id).RequirementsMet;

            void Schedule(long when)
            {
                if (when > end) return;
                Runtime.Schedule(when, () =>
                {
                    if (!divineBlastOrbs.Contains(orb)) return;
                    if (!Current()) { RemoveDivineBlastOrb(orb); return; }
                    ShowDivineBlastExplosion(at, radius);
                    var blast = AreaSkillHitForUse(null, Player, gem.Id, gem, at, at, immediate: true,
                        radiusOverride: radius, criticalRoll: critical, triggeredCurrent: Current, scheduledSession: this,
                        cryExerted: false);
                    if (!blast.IsCompletedSuccessfully) Enqueue(() => blast);
                    if (repeating) Schedule(when + step);
                    else RemoveDivineBlastOrb(orb);
                });
            }
        }

        void RemoveDivineBlastOrb(DivineBlastOrb orb)
        {
            if (!divineBlastOrbs.Remove(orb)) return;
            Player.ProjectileObjects.Remove(orb.Id);
            WorldRecords(orb.Id, () => [InstanceProtocol.RemoveObject(orb.Id)], remove: true);
        }

        void ShowDivineBlastExplosion((int X, int Y) at, int radius)
        {
            uint id = Player.TakeObjectId();
            Player.ProjectileObjects.Add(id);
            divineBlastEffects.Add(id);
            Create(id, () => DivineBlastEffect(id, DivineBlastExplosionArt, at, radius), wake: true);
            Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(.75), () =>
            {
                if (!divineBlastEffects.Remove(id)) return;
                Player.ProjectileObjects.Remove(id);
                WorldRecords(id, () => [InstanceProtocol.RemoveObject(id)], remove: true);
            });
        }

        readonly HashSet<uint> divineBlastEffects = [];

        void ClearDivineBlastOrbs()
        {
            foreach (uint id in divineBlastOrbs.Select(o => o.Id).Concat(divineBlastEffects))
            {
                Player.ProjectileObjects.Remove(id);
                if (!Player.LeavingArea) Player.RetiredCombatObjects.Add(id);
            }
            divineBlastOrbs.Clear();
            divineBlastEffects.Clear();
        }

        static byte[] DivineBlastEffect(uint id, string art, (int X, int Y) at, int radius = 0) =>
            InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(at.X, at.Y, art) { Scale = radius > 0 ? AreaArtScale(radius, art) : 1 }.Blob());
    }
}
