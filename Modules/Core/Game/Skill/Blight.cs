using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const ushort BlightBuffRow = 813; // new_blight has no per-record recovery values.
    static readonly RuleTable<GameArea, BlightArea> BlightMonsters = new();
    static readonly RuleTable<SpawnedMonster, BlightDebuff> BlightDebuffs = new();

    sealed class BlightArea
    {
        public readonly Dictionary<uint, SpawnedMonster> Monsters = [];
        public readonly List<KeyValuePair<uint, SpawnedMonster>> Tick = [];
    }

    sealed record BlightCast(Player Caster, double Dps, int Limit, bool Spreads);
    readonly record struct BlightLayer(BlightCast Cast, double Until);
    sealed class BlightDebuff(double now)
    {
        public readonly List<BlightLayer> Layers = [];
        public double From = now, Carry;
        public double HinderFrom, HinderUntil;
        public int Slow, Regeneration, ShownCount;
        public uint ShownSource;
        public Player? DamagingCaster;

        public void Accrue(SpawnedMonster monster, double now)
        {
            if (now <= From) return;
            double damage = 0, strongest = 0;
            for (int i = Layers.Count - 1; i >= 0; i--)
            {
                var layer = Layers[i];
                double amount = Math.Max(0, Math.Min(layer.Until, now) - From) * layer.Cast.Dps;
                damage += amount;
                if (amount > strongest)
                {
                    strongest = amount;
                    DamagingCaster = layer.Cast.Caster;
                }
                if (layer.Until <= now) Layers.RemoveAt(i);
            }
            if (damage > 0)
                Carry += damage * MonsterDotTaken(monster, DamageType.Chaos, From);
            From = now;
        }

        public void Add(BlightLayer layer)
        {
            int count = 0, earliest = -1;
            for (int i = 0; i < Layers.Count; i++)
            {
                var current = Layers[i];
                if (ReferenceEquals(current.Cast, layer.Cast)) return;
                if (current.Cast.Spreads != layer.Cast.Spreads) continue;
                count++;
                if (earliest < 0 || current.Until < Layers[earliest].Until) earliest = i;
            }
            if (count < layer.Cast.Limit) Layers.Add(layer);
            else if (earliest >= 0 && layer.Until > Layers[earliest].Until) Layers[earliest] = layer;
        }
    }

    static bool IsBlightChannel(string? effect) => effect is "Blight" or "BlightAltX" or "BlightAltY" or "BlightRoyale";
    static bool HasBlightDamage(GameArea area) =>
        BlightMonsters.TryGetValue(area, out var monsters) && monsters.Monsters.Count != 0;

    static (double Until, int Slow) BlightHinder(SpawnedMonster monster, double now) =>
        BlightDebuffs.TryGetValue(monster, out var state) && state.HinderUntil > now
            ? (state.HinderUntil, state.Slow) : default;

    static double BlightRegenerationFactor(SpawnedMonster monster, double from, double to)
    {
        if (!BlightDebuffs.TryGetValue(monster, out var state) || state.Regeneration == 0 || to <= from) return 1;
        double overlap = Math.Max(0, Math.Min(to, state.HinderUntil) - Math.Max(from, state.HinderFrom));
        return 1 + overlap / (to - from) * Math.Clamp(state.Regeneration / 100d, -1, 0);
    }

    static BlightDebuff BlightOn(GameArea area, uint id, SpawnedMonster monster, double now)
    {
        BlightMonsters.GetOrCreateValue(area).Monsters[id] = monster;
        return BlightDebuffs.GetValue(monster, _ => new(now));
    }

    static void PulseBlight(Player player, AimedPulseChannel channel, int effect, int action, double elapsed, double pact)
    {
        string? effectId = SkillsFor(player).EffectId(effect);
        var own = SupportedSkill(player, channel.Skill).Stats;
        var caster = CharacterPlayerStats(player);
        int Own(uint k) => own.GetValueOrDefault(k);
        int Player(uint k) => caster.GetValueOrDefault(k);
        int Get(uint k) => Own(k) + Player(k);
        int baseRadius = effectId == "BlightAltX" ? 28 : 26;
        int radius = SkillRadius(player, baseRadius, own, spell: true, effect, caster);
        int maximum = Own(Stat.DisplayMaxBlightStacks);
        double seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Player, Own);
        double hinder = SkillDurationModifiers.Seconds(Own(Stat.BaseSecondarySkillEffectDuration), Player, Own);
        if (maximum <= 0 || seconds <= 0 || radius <= 0) return;

        double increased = (double)Get(Stat.DamagePlusPct) + Get(Stat.ChaosDamagePlusPct) + Get(Stat.AreaDamagePlusPct)
            + Get(Stat.DamageOverTimePlusPct) + Get(Stat.ChaosDamageOverTimePlusPct) + Get(Stat.DamageOverTimePlusPctWithSpells);
        if (Own(Stat.SpellDamageModifiersApplyToSkillDot) != 0) increased += Get(Stat.SpellDamagePlusPct);
        double multiplier = 1 + ((double)Get(Stat.DotMultiplierPlus) + Get(Stat.ChaosDotMultiplierPlus)
            + Get(Stat.DamageOverTimeMultiplierPlusWithSpells)) / 100d;
        double more = 1;
        foreach (uint stat in ChaosAreaDotSupportMore)
            more *= Math.Max(0, 1 + Own(stat) / 100d);
        more *= pact * FocusedChannelMore(elapsed, Own(Stat.SupportFocusChannelDamagePlusPctFinalPerSecondChannellingUpTo60Pct));
        double dps = DamageStats.Prohibited(DamageType.Chaos, Get) ? 0
            : Own(Stat.BaseChaosDamageToDealPerMinute) / 60d * Math.Max(0, 1 + increased / 100d) * Math.Max(0, multiplier) * more;
        dps = new SkillDamageContext(SkillHitKind.Spell, caster).OverTime(new(DamageType.Chaos, dps, seconds)).DamagePerSecond;
        var cast = new BlightCast(player, dps, maximum, Spreads: effectId == "BlightAltX");
        int slow = Math.Max(0, -Own(Stat.BaseMovementVelocityPlusPct));
        int regeneration = Get(Stat.EnemiesYouHinderHaveLifeRegenerationRatePlusPct);

        using (ProjectileMutation(player))
        {
            double now = ProjectileWorldTime(player);
            if (!ChannelCurrent(player, channel.Skill, action, channel.Generation) || PlayerActionBlocked(player, now, now))
                return;
            var origin = player.Walk.Position(now);
            var direction = channel.DirectionFrom(origin);
            foreach (var (id, monster) in player.Area.MonstersNear(origin, radius))
            {
                if (monster is not { Hostile: true, Dead: false, Invulnerable: false, Life: > 0, OwnerId: null, WorldHidden: false }
                    || ArenaHazards.Untargetable(player, monster)) continue;
                var at = monster.Walk.Position(now);
                if (!AimedPulseChannel.InFront(origin, direction, at) || Dist(origin, at) > radius) continue;
                var state = BlightOn(player.Area, id, monster, now);
                state.Accrue(monster, now);
                double rate = Math.Max(.25, monster.RecipientDebuffRate);
                if (state.Layers.Count == 0 && state.HinderUntil <= now && slow > 0 && hinder > 0
                    && monster.Effects[Stat.ImmuneToHinder] == 0 && monster.Effects[Stat.YouCannotBeHindered] == 0)
                {
                    state.Slow = Math.Clamp((int)Math.Round(slow * (1 + monster.Effects[Stat.HinderEffectOnSelfPlusPct] / 100d)), 0, 100);
                    if (state.Slow > 0)
                    {
                        state.HinderFrom = now;
                        state.HinderUntil = now + hinder / rate;
                        state.Regeneration = regeneration;
                        monster.PresentationChanged?.Invoke();
                    }
                }
                state.Add(new(cast, now + seconds / rate));
                PresentBlight(player.Area, id, state);
                if (ServerTrace.Skills)
                    Console.WriteLine($"[gateway] {effectId} pulse target 0x{id:x} layers={state.Layers.Count} " +
                        $"dps={dps:F2} duration={seconds / rate:F2} hinderUntil={state.HinderUntil:F3} now={now:F3}");
            }
        }
    }

    static void SpreadBlightLocked(GameArea area, SpawnedMonster source, uint targetId, SpawnedMonster target, double now)
    {
        if (!BlightDebuffs.TryGetValue(source, out var origin)) return;
        BlightDebuff? recipient = null;
        foreach (var layer in origin.Layers)
        {
            if (!layer.Cast.Spreads || layer.Until <= now) continue;
            if (recipient is null)
            {
                recipient = BlightOn(area, targetId, target, now);
                recipient.Accrue(target, now);
            }
            recipient.Add(layer);
        }
        if (recipient is not null) PresentBlight(area, targetId, recipient);
    }

    static void PresentBlight(GameArea area, uint id, BlightDebuff state, bool living = true)
    {
        int count = living ? state.Layers.Count : 0;
        if (count == state.ShownCount) return;
        if (count == 0) area.Buffs.Clear(id, BlightBuffRow, state.ShownSource);
        else
        {
            if (state.ShownCount == 0) state.ShownSource = state.Layers[0].Cast.Caster.Id;
            area.Buffs.Set(id, new(BlightBuffRow, state.ShownSource, Stacks: (ushort)count, KeepInstance: true));
        }
        state.ShownCount = count;
    }

    static void RetimeBlight(SpawnedMonster monster, double now, double oldRate, double rate)
    {
        if (!BlightDebuffs.TryGetValue(monster, out var state)) return;
        state.Accrue(monster, now);
        double scale = Math.Max(.25, oldRate) / Math.Max(.25, rate);
        for (int i = 0; i < state.Layers.Count; i++)
        {
            var layer = state.Layers[i];
            state.Layers[i] = layer with { Until = now + (layer.Until - now) * scale };
        }
        if (state.HinderUntil > now) state.HinderUntil = now + (state.HinderUntil - now) * scale;
    }
}
