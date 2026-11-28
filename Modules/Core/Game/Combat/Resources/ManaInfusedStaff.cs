using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string ManaInfusedStaffEffect = "ManaInfusedStaff";
    const string ManaStaffWaveArt = "Metadata/Effects/Spells/mana_infused_staff/arcane_moving_cone.ao";
    const string ManaStaffStartArt = "Metadata/Effects/Spells/mana_infused_staff/start_sound.ao";
    // MiscAnimated gives the full-size wave a radius of 925 world units.
    const int ManaStaffBaseRadius = 85;

    sealed class ManaStaffBlockCooldown
    {
        public double ReadyAt;
    }

    static readonly Store<Player, ManaStaffBlockCooldown> ManaStaffBlockCooldowns = new();

    static AuraCatalog.Aura? ManaStaffAura(Player player)
    {
        if (player.Auras.Count == 0 || player.PlayerLife == 0) return null;
        foreach (var aura in player.Auras.Values)
            if (aura is { EffectId: ManaInfusedStaffEffect, AffectsSelf: true })
                return aura;
        return null;
    }

    static (int ManaCost, int Block) ManaStaffGrants(AuraCatalog.Aura aura)
    {
        int manaCost = 0, block = 0;
        if (aura.SkillStats is { } stats)
            foreach (var (key, value) in stats)
            {
                if (key == Stat.ActiveSkillBuffManaCostPlusPctFinalToGrant) manaCost = value;
                else if (key == Stat.ActiveSkillBuffBlockChancePlusPctFinalToGrant) block = value;
            }
        return (manaCost, block);
    }

    static Buff WithManaStaffBuffValues(Buff buff, AuraCatalog.Aura aura)
    {
        var (manaCost, block) = ManaStaffGrants(aura);
        var native = BuffRows.Spec(buff.Row, buff.Source, new Dictionary<uint, int>
        {
            [Stat.SkillManaCostPlusPctFinalFromManaInfusedStaff] = manaCost,
            [Stat.BlockChancePlusPctFinalFromManaInfusedStaff] = block
        });
        return buff with { Values = native.Values, U82 = native.U82 };
    }

    static SkillHitPlan ManaStaffHitPlan(Player player, SkillHitPlan plan, IReadOnlyDictionary<uint, int> skillStats)
    {
        if (plan.Kind != SkillHitKind.Spell
            || !skillStats.TryGetValue(Stat.SkillHasAddedLightningDamageEqualToPctOfMaximumMana, out int percent)
            || percent <= 0) return plan;
        double lightning = Math.Floor(player.Pools.Mana * (double)percent / 100);
        return plan with { SpellBase = plan.SpellBase.Add(DamageType.Lightning, new(lightning, lightning)) };
    }

    static void TryQueueManaStaffWave(Player player, double now)
    {
        if (player.Auras.Count == 0 || !CurrentGameWork || player.Closed || player.LeavingArea || player.Paused
            || player.PendingTransfer is not null || ManaStaffAura(player) is not { } aura) return;
        var cooldown = ManaStaffBlockCooldowns.GetOrCreateValue(player);
        if (now < cooldown.ReadyAt || WeaponRestricted(player, aura.Effect) is not null) return;
        var gem = GemSkillEffects(player).FirstOrDefault(gem => gem.Id == aura.SkillId && gem.Effect == aura.Effect);
        if (gem.Name is null) return;
        var source = SupportedSkill(player, aura.SkillId);
        if (!source.RequirementsMet) return;
        int interval = Math.Max(1000, source.Stats.GetValueOrDefault(Stat.ManaInfusedStaffBaseTriggerIntervalMs));
        cooldown.ReadyAt = now + interval / 1000.0;
        var session = ProjectilesFor(player);
        var origin = player.Walk.Position(now);
        bool Current() => CurrentGameWork && !player.Paused && !player.Closed && !player.LeavingArea
            && player.PendingTransfer is null && player.PlayerLife > 0 && player.Auras.TryGetValue(aura.SkillId, out var current)
            && current.Buff == aura.Buff && WeaponRestricted(player, aura.Effect) is null
            && SameTriggerSource(source, SupportedSkill(player, aura.SkillId));
        session.ScheduleContact(ProjectileRuntime.Time(now), Current,
            () => session.StartManaStaffWave(aura, source, gem, origin, Current));
    }

    sealed partial class ProjectileSession
    {
        internal void StartManaStaffWave(AuraCatalog.Aura aura, SupportedGem source,
            (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
            (int X, int Y) origin, Func<bool> current)
        {
            double duration = ReservationDuration(Player, aura, Stat.BaseSkillEffectDuration, 350);
            int radius = SkillRadius(Player, ManaStaffBaseRadius, source.Stats, spell: true, aura.Effect);
            long lifetime = ProjectileRuntime.Time(duration);
            if (lifetime <= 0 || radius <= 0) return;

            Effect(origin.X, origin.Y, ManaStaffStartArt, lifetime);
            Effect(origin.X, origin.Y, ManaStaffWaveArt, lifetime, radius);
            var hit = new HashSet<uint>();
            var critical = CaptureSkillCriticalRoll(Player, aura.SkillId);
            long started = Runtime.Now;
            // Advance the leading edge once per world-frame-sized slice. The once set covers adjacent slices.
            int steps = Math.Max(1, (int)Math.Min(radius, Math.Ceiling(duration * 30)));
            int index = 0;
            ScheduleNext();

            void ScheduleNext() => ScheduleContact(started + lifetime * (index + 1) / steps, current, Contact);

            void Contact()
            {
                double inner = radius * (double)index / steps;
                double outer = radius * (double)++index / steps;
                var pending = AreaSkillHitForUse(null, Player, aura.SkillId, gem, origin, origin, immediate: true,
                    radiusOverride: (int)Math.Ceiling(outer), once: hit, criticalRoll: critical, nearbyOnly: true,
                    scheduledSession: this, triggeredCurrent: current,
                    targetFilter: (id, at) =>
                    {
                        double dx = at.X - origin.X, dy = at.Y - origin.Y;
                        double distanceSquared = dx * dx + dy * dy;
                        return distanceSquared >= inner * inner && distanceSquared <= outer * outer
                            && Player.Area.Monsters.TryGetValue(id, out var monster) && !monster.WorldHidden;
                    });
                if (!pending.IsCompleted) throw new InvalidOperationException("Mana-Infused Staff wave contact must not yield.");
                pending.GetAwaiter().GetResult();
                if (index < steps) ScheduleNext();
            }
        }
    }
}
