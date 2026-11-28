using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string VoidstormEffect = "TriggeredSupportVoidstorm";
    const string VoidstormArt = "Metadata/Effects/Spells/supports/voidstorm/voidstorm.ao";
    const string VoidstormBurstArt = "Metadata/Effects/Spells/supports/voidstorm/voidstorm_explode.ao";
    const uint VoidstormTrigger = Stat.SupportVoidstormTriggerVoidstormOnRainSkillImpact;
    const int VoidstormMinimumDistance = 30;

    public sealed partial class ProjectileSession
    {
        sealed record Voidstorm(ProjectileUse Use, long Planted, long Lifetime, long StageInterval, int MaximumStage,
            int BaseRadius, int AreaPerStage, int DetonationDamage, int DamagePerStage, int BlindLess)
        {
            public int Stage;
            public int Radius = BaseRadius;
            public Action? RemoveArt;
        }

        List<Voidstorm>? voidstorms;
        Dictionary<SpawnedMonster, int>? voidstormBlind, nextVoidstormBlind;
        GameArea? voidstormBlindArea;
        long nextVoidstormBlindAt;

        internal void RainImpact(ProjectileUse use, ProjectileVector at, ProjectileAreaRecipe recipe)
        {
            Runtime.Impact(use, at, recipe);
            if (use.Payload is ProjectileOffense { VoidstormDriver: { } driver } shot)
                TriggerVoidstorm(shot.SourceSkill, driver, at);
        }

        void TriggerVoidstorm(ushort skill, SupportedGem driver, ProjectileVector at)
        {
            var player = Player;
            if (!Active || player.PlayerLife == 0 || player.Paused || player.LeavingArea) return;
            double now = Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
            var offset = Vector(player.Walk.Position(now)) - at;
            if (offset.Dot(offset) < VoidstormMinimumDistance * VoidstormMinimumDistance
                || !SameTriggerSource(driver, SupportedSkill(player, skill))
                || driver.Supports.FirstOrDefault(s => SupportHas(s, VoidstormTrigger)) is not { } support
                || SupportCompanionSourceLocked(player, driver, support, VoidstormEffect, "HungryLoopSupportedByVoidstorm") is not { } source
                || GemSkillInstanceDisabled(player, source.Effect, source.Id, triggered: true) is not null) return;

            var cooldown = CooldownForSkill(player, source.Id);
            int key = CooldownKey(player, source.Effect, source.Id);
            var state = cooldown is { } plan ? CooldownStateLocked(player, key, plan, now) : null;
            if (state is { Ready: false }) return;

            var gem = SupportedSkill(player, source.Id);
            var playerStats = CharacterPlayerStats(player);
            int Own(uint stat) => gem.Stats.GetValueOrDefault(stat);
            int Caster(uint stat) => playerStats.GetValueOrDefault(stat);
            int Get(uint stat) => ActorScale.Saturate((long)Own(stat) + Caster(stat));
            int radius = SkillRadius(player, gem.Stats, spell: false, source.Effect);
            int limit = Math.Clamp(Get(Stat.WindstormStormLimit), 0, 64);
            int stages = Math.Clamp(Get(Stat.WindstormStormMaximumStages), 0, 64);
            long lifetime = ProjectileRuntime.Time(SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration),
                Caster, Own, DurationKinds(player, source.Effect)));
            int stageMs = Get(Stat.WindstormStormStageGainedPerXMs);
            if (stageMs <= 0) stageMs = Own(Stat.BaseWindstormStormStageGainedPerXMs);
            long interval = ProjectileRuntime.Time(stageMs / 1000d);
            if (limit == 0 || radius <= 0 || lifetime <= 0 || interval <= 0 || stages == 0) return;
            if (state is not null && cooldown is { } recovery)
            {
                if (!state.TryUse(now, recovery)) return;
                player.CooldownSources[source.Effect] = source.Id;
                player.SkillReadyAt[key] = now + state.RemainingSeconds;
                state.ConfirmNativeUse(state.UseVersion, source.Id, synchronizeProgress: true);
                Enqueue(() => SendNativeCooldownRefresh(player));
            }

            var cell = ((int)Math.Round(at.X), (int)Math.Round(at.Y));
            var skillUse = (source.Id, source.Effect, Name: "Voidstorm", gem.Level, gem.Quality, source.Gem);
            var use = CaptureGemProjectile(player, this, source.Id, skillUse, cell,
                CaptureSkillCriticalRoll(player, source.Id), NextLockstepSequence(player), from: cell, capturedSkill: gem);
            use = use with
            {
                Origin = at, Aim = at,
                Payload = ((ProjectileOffense)use.Payload!) with
                {
                    AreaVisual = null, CriticalTrigger = null, MirageSource = null, KineticFluxDriver = null
                }
            };
            var storms = voidstorms ??= [];
            bool idle = storms.Count == 0;
            while (storms.Count >= limit) RemoveVoidstormAt(0, "limit");
            var storm = new Voidstorm(use, Runtime.Now, lifetime, interval, stages, radius,
                Get(Stat.WindstormStormAreaOfEffectPlusPctFinalPerStage),
                Get(Stat.WindstormStormHitDamagePlusPctFinalOnDetonation),
                Get(Stat.WindstormStormHitDamagePlusPctFinalOnDetonationPerStage), BlindEffectLess(playerStats));
            storms.Add(storm);
            DrawVoidstorm(storm);
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[voidstorm] place at={Runtime.Now} cell={cell} distance={offset.Length:F2} radius={radius} count={storms.Count}");
            if (idle) CheckVoidstorms();
        }

        void DrawVoidstorm(Voidstorm storm)
        {
            storm.RemoveArt?.Invoke();
            Effect((int)Math.Round(storm.Use.Aim.X), (int)Math.Round(storm.Use.Aim.Y), VoidstormArt,
                storm.Planted + storm.Lifetime - Runtime.Now, storm.Radius, captureRemoval: remove => storm.RemoveArt = remove);
        }

        void RemoveVoidstormAt(int index, string reason)
        {
            var storms = voidstorms!;
            storms[index].RemoveArt?.Invoke();
            storms.RemoveAt(index);
            if (ServerTrace.Projectiles) Console.WriteLine($"[voidstorm] remove reason={reason} at={Runtime.Now} count={storms.Count}");
        }

        void CheckVoidstorms()
        {
            if (voidstorms is not { Count: > 0 } storms) return;
            if (!Active || Player.PlayerLife == 0) { ClearVoidstorms(); return; }
            long now = Runtime.Now;
            double at = now / (double)ProjectileRuntime.QuantaPerSecond;
            var owner = Vector(Player.Walk.Position(at));
            bool changed = false;
            for (int i = storms.Count - 1; i >= 0; i--)
            {
                var storm = storms[i];
                long age = now - storm.Planted;
                if (age >= storm.Lifetime)
                {
                    RemoveVoidstormAt(i, "expiry");
                    changed = true;
                    continue;
                }
                int stage = (int)Math.Min(storm.MaximumStage, age / storm.StageInterval);
                if (stage != storm.Stage)
                {
                    storm.Stage = stage;
                    int radius = Math.Max(1, (int)Math.Round(storm.BaseRadius
                        * Math.Sqrt(Math.Max(0, 1 + (double)stage * storm.AreaPerStage / 100))));
                    if (radius != storm.Radius)
                    {
                        storm.Radius = radius;
                        DrawVoidstorm(storm);
                        changed = true;
                    }
                    if (ServerTrace.Projectiles) Console.WriteLine($"[voidstorm] stage={stage} radius={radius} at={now}");
                }
                var offset = owner - storm.Use.Aim;
                if (offset.Dot(offset) > (double)storm.Radius * storm.Radius) continue;
                RemoveVoidstormAt(i, "entry");
                changed = true;
                double more = Math.Max(0, 1 + (storm.DetonationDamage + (long)storm.DamagePerStage * stage) / 100d);
                var shot = ((ProjectileOffense)storm.Use.Payload!).ScaleHitAndAilmentDamage(more);
                Runtime.Impact(storm.Use with { Payload = shot }, storm.Use.Aim, new("Voidstorm burst", storm.Radius));
                RainArt(storm.Use.Aim, VoidstormBurstArt, .6, storm.Radius);
                if (ServerTrace.Projectiles) Console.WriteLine($"[voidstorm] burst stage={stage} more={more:F2} at={now}");
            }
            if (changed || now >= nextVoidstormBlindAt)
            {
                UpdateVoidstormBlind(storms, at);
                nextVoidstormBlindAt = now + ProjectileRuntime.Time(.1);
            }
            if (storms.Count > 0) Runtime.Schedule(now + ProjectileRuntime.TickQuanta, CheckVoidstorms);
        }

        void UpdateVoidstormBlind(List<Voidstorm> storms, double at)
        {
            var next = nextVoidstormBlind ??= new(ReferenceEqualityComparer.Instance);
            var prior = voidstormBlind ??= new(ReferenceEqualityComparer.Instance);
            var area = Player.Area;
            foreach (var storm in storms)
            {
                if (storm.BlindLess <= 0) continue;
                var centre = storm.Use.Aim;
                var cell = ((int)Math.Round(centre.X), (int)Math.Round(centre.Y));
                foreach (var (_, monster) in area.MonstersNear(cell, storm.Radius))
                    if (monster is { Hostile: true, Dead: false, Life: > 0, WorldHidden: false }
                        && monster.Mods.Effects[Stat.CannotBeBlinded] == 0
                        && (Vector(monster.Walk.Position(at)) - centre).Length <= storm.Radius)
                        next[monster] = Math.Max(next.GetValueOrDefault(monster), storm.BlindLess);
            }
            ushort row = BuffRows.Find("windstorm_debuff");
            if (row == 0) row = FlaskGrounds.BlindedBuffRow;
            const ushort tag = (ushort)BuffTag.VoidstormBlind;
            bool sameArea = ReferenceEquals(area, voidstormBlindArea);
            foreach (var monster in prior.Keys)
                if (!sameArea || !next.ContainsKey(monster)) SetStormBlind(voidstormBlindArea!, monster, 0, storms, row, tag);
            foreach (var (monster, less) in next)
                if (!sameArea || prior.GetValueOrDefault(monster) != less) SetStormBlind(area, monster, less, storms, row, tag);
            (voidstormBlind, nextVoidstormBlind) = (next, prior);
            voidstormBlindArea = area;
            prior.Clear();
        }

        void ClearVoidstorms()
        {
            if (voidstorms is not { } storms) return;
            while (storms.Count > 0) RemoveVoidstormAt(storms.Count - 1, "retire");
            if (voidstormBlind is { Count: > 0 }) UpdateVoidstormBlind(storms, 0);
            nextVoidstormBlindAt = 0;
        }
    }
}
