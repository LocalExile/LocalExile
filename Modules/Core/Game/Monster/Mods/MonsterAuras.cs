using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using AuraState = (bool Emits, uint Source, int Rate, int Extra);
using AuraView = (ushort EmitterBuff, ushort ReceiverBuff, uint Source, int Rate);

// BuffTemplates expresses the radius in cells and names its recipients in __col4.
internal sealed record MonsterAuraTemplate(ushort Buff, int Radius, bool Self, bool Allies, bool Enemies)
{
    public static MonsterAuraTemplate? Read(ModCatalog.Mod mod)
    {
        if (mod.BuffTemplate is not int row || ClientTables.Table("BuffTemplates").Row(row) is not { } template ||
            template.IntOrNull("BuffDefinitionsKey") is not int buff || buff <= 0 || template.Int("AuraRadius") <= 0)
            return null;
        var targets = template.Ints("__col4");
        return new(checked((ushort)buff), template.Int("AuraRadius"), targets.Contains(1), targets.Contains(2), targets.Contains(3));
    }
}

public static partial class Game
{
    const string LifeAuraStat = "life_regeneration_rate_per_minute_%";
    const string PhysicalAuraStat = "physical_damage_+%";
    const string AttackSpeedStat = "attack_speed_+%", CastSpeedStat = "base_cast_speed_+%", MoveSpeedStat = "base_movement_velocity_+%";
    const ushort LifeAuraBuff = 90, PhysicalAuraBuff = 88, SpeedAuraBuff = 89, EnemySlowBuff = 136;
    sealed record AuraEffect(ushort Buff, string Stat);
    static readonly AuraEffect RecoveryAura = new(LifeAuraBuff, LifeAuraStat);
    static readonly AuraEffect ResistanceAura = new(91, "base_resist_all_elements_%");
    static readonly AuraEffect PhysicalAura = new(PhysicalAuraBuff, PhysicalAuraStat);

    static readonly AuraEffect AttackSpeedAura = new(SpeedAuraBuff, AttackSpeedStat);
    static readonly AuraEffect CastSpeedAura = new(SpeedAuraBuff, CastSpeedStat);
    static readonly AuraEffect MoveSpeedAura = new(SpeedAuraBuff, MoveSpeedStat);
    static readonly AuraEffect EnemySlowAura = new(EnemySlowBuff, MoveSpeedStat);
    static readonly AuraEffect[] GrantedAuraStats = [RecoveryAura, ResistanceAura, PhysicalAura, AttackSpeedAura, CastSpeedAura, MoveSpeedAura];

    sealed record AuraMod(MonsterAuraTemplate Template, (AuraEffect Effect, int Index)[] Stats);
    readonly record struct AuraRoll(MonsterAuraTemplate Template, AuraEffect Effect, int Value);
    readonly record struct AuraField(uint Source, int X, int Y, double RadiusSquared, AuraRoll Roll);
    sealed class AuraFields
    {
        public readonly List<AuraField> Active = [];
        public readonly Dictionary<uint, int> IncludedInMods = [];
    }

    static readonly Lazy<Dictionary<ushort, AuraMod>> MonsterAuraMods = new(() =>
    {
        var rows = new Dictionary<ushort, AuraMod>();
        if (!ClientTables.Has("BuffTemplates")) return rows;
        foreach (var mod in Items.Mods.All)
        {
            if (mod.Domain != MonsterModCatalog.Domain || mod.BuffTemplate is null || MonsterAuraTemplate.Read(mod) is not { } template) continue;
            var granted = (from effect in GrantedAuraStats
                           where effect.Buff == template.Buff || effect == MoveSpeedAura && template is { Buff: EnemySlowBuff, Enemies: true }
                           from i in Enumerable.Range(0, mod.Stats.Count) where mod.Stats[i].Id == effect.Stat
                           select (effect, i)).ToArray();
            if (granted.Length > 0) rows.Add(mod.Hash, new(template, granted));
        }
        return rows;
    });

    static readonly RuleTable<IReadOnlyList<ItemMod>, AuraRoll[]> MonsterAuraRolls = new();

    static AuraRoll[] ReadAuraRolls(IReadOnlyList<ItemMod> mods) =>
        [.. from roll in mods
            let def = MonsterAuraMods.Value.GetValueOrDefault(roll.Hash)
            where def is not null
            from stat in def.Stats
            select new AuraRoll(def.Template, stat.Effect, roll.Values[stat.Index])];

    static AuraRoll[] AuraRolls(IReadOnlyList<ItemMod> mods) =>
        mods.Count == 0 ? [] : MonsterAuraRolls.GetValue(mods, ReadAuraRolls);

    internal readonly record struct AuraSpeeds(int Attack, int Cast, int Movement)
    {
        public MonsterModEffects AddTo(MonsterModEffects effects, int sign = 1) =>
            effects.Plus(AttackSpeedStat, sign * Attack).Plus(CastSpeedStat, sign * Cast).Plus(MoveSpeedStat, sign * Movement);
    }

    // A rolled aura stat is in the owner's permanent mods even when its template grants it only to allies.
    // Remove all such rolls before binding an action, then apply the active grant once.
    internal static (MonsterModEffects Effects, (int Self, int Included) Physical, AuraSpeeds SpeedSelf, AuraSpeeds SpeedIncluded)
        SeparateAttackAuras(MonsterModEffects effects, IReadOnlyList<ItemMod> mods)
    {
        var rolls = AuraRolls(mods);
        if (rolls.Length == 0) return (effects, default, default, default);
        var physical = OwnAura(rolls, PhysicalAura);
        var attack = OwnAura(rolls, AttackSpeedAura);
        var cast = OwnAura(rolls, CastSpeedAura);
        var move = OwnAura(rolls, MoveSpeedAura);
        var included = new AuraSpeeds(attack.Included, cast.Included, move.Included);
        return (included.AddTo(effects.Plus(PhysicalAuraStat, -physical.Included), -1), physical,
            new(attack.Self, cast.Self, move.Self), included);
    }

    static (int Self, int Included) OwnAura(AuraRoll[] rolls, AuraEffect effect)
    {
        int self = 0, included = 0;
        foreach (var roll in rolls)
            if (roll.Effect == effect)
            {
                included += roll.Value;
                if (roll.Template.Self) self = Math.Max(self, roll.Value);
            }
        return (self, included);
    }

    static Dictionary<AuraEffect, AuraFields>? GatherMonsterAuras(IReadOnlyDictionary<uint, SpawnedMonster> monsters, double now)
    {
        Dictionary<AuraEffect, AuraFields>? groups = null;
        EnemySlowSources.TryGetValue(monsters, out var slowSources);
        slowSources?.Clear();
        foreach (var (id, monster) in monsters)
        {
            monster.EmitsEnemySlow = false;
            var rolls = AuraRolls(monster.Mods.Mods);
            if (rolls.Length == 0) continue;
            groups ??= [];
            bool active = monster.Hostile && !monster.WorldHidden && !monster.Dead && monster.Life > 0;
            var at = monster.Walk.Position(now);
            double area = Math.Max(0, 1 + monster.Mods.Effects["base_aura_area_of_effect_+%"] / 100d);
            foreach (var roll in rolls)
            {
                if (!groups.TryGetValue(roll.Effect, out var fields)) groups.Add(roll.Effect, fields = new());
                fields.IncludedInMods[id] = fields.IncludedInMods.GetValueOrDefault(id) + roll.Value;
                if (active)
                {
                    double radius = roll.Template.Radius;
                    if (roll.Template.Enemies && roll.Template.Buff == EnemySlowBuff)
                    {
                        monster.EmitsEnemySlow = true;
                        (slowSources ??= EnemySlowSources.GetOrCreateValue(monsters)).Add(new(id, monster, radius * radius * area, roll.Value));
                    }
                    else fields.Active.Add(new(id, at.X, at.Y, radius * radius * area, roll));
                }
            }
        }
        return groups;
    }

    static bool ApplyMonsterAura(uint id, SpawnedMonster monster, AuraFields? fields, double now, AuraEffect effect, ref AuraState state,
        (uint Source, int Rate) enemy = default)
    {
        if (fields is null && state == default && enemy == default) return false;
        var (source, value) = enemy;
        bool emits = false;
        if (fields is not null && monster.Hostile && !monster.WorldHidden && !monster.Dead && monster.Life > 0)
        {
            var at = monster.Walk.Position(now);
            foreach (var field in fields.Active)
            {
                bool self = field.Source == id;
                emits |= self;
                var aura = field.Roll.Template;
                if (!(self ? aura.Self : aura.Allies) || field.Roll.Value < value ||
                    (field.Roll.Value == value && field.Source >= source)) continue;
                double dx = (double)at.X - field.X, dy = (double)at.Y - field.Y;
                if (dx * dx + dy * dy > field.RadiusSquared) continue;
                source = field.Source;
                value = field.Roll.Value;
            }
        }
        int extra = value - (fields?.IncludedInMods.GetValueOrDefault(id) ?? 0);
        if (state == (emits, source, value, extra)) return false;
        bool magnitudeChanged = state.Rate != value;
        state = (emits, source, value, extra);
        monster.SetGrantedStat(effect.Stat, effect == MoveSpeedAura ? extra + monster.LinkedFlaskMovementPct : extra);
        return magnitudeChanged;
    }

    static void InitializeSpeedAura(uint id, SpawnedMonster monster, AuraSpeeds self, AuraSpeeds included)
    {
        monster.SpeedAura.Attack = InitialAura(id, monster, AttackSpeedAura, self.Attack, included.Attack);
        monster.SpeedAura.Cast = InitialAura(id, monster, CastSpeedAura, self.Cast, included.Cast);
        monster.SpeedAura.Movement = InitialAura(id, monster, MoveSpeedAura, self.Movement, included.Movement);
        monster.SpeedAura.Capture(skills: true);
    }

    static AuraState InitialAura(uint id, SpawnedMonster monster, AuraEffect effect, int granted, int rolled)
    {
        int extra = granted - rolled;
        if (extra != 0) monster.SetGrantedStat(effect.Stat, extra);
        return (monster.Hostile && rolled != 0, granted == 0 ? 0 : id, granted, extra);
    }

    static void ApplySpeedAura(uint id, SpawnedMonster monster, AuraFields? attack, AuraFields? cast, AuraFields? movement, double now, (uint Source, int Rate) enemy)
    {
        ref var speed = ref monster.SpeedAura;
        if (attack is null && cast is null && movement is null && speed.Attack == default && speed.Cast == default && speed.Movement == default) return;
        int previousMove = speed.Movement.Extra;
        bool attackChanged = ApplyMonsterAura(id, monster, attack, now, AttackSpeedAura, ref speed.Attack);
        bool castChanged = ApplyMonsterAura(id, monster, cast, now, CastSpeedAura, ref speed.Cast);
        ApplyMonsterAura(id, monster, movement, now, MoveSpeedAura, ref speed.Movement, enemy);
        if (attackChanged || castChanged)
            BossKits.RefreshSpeedAura(monster, new(speed.Attack.Rate, speed.Cast.Rate, speed.Movement.Rate));
        if (previousMove != speed.Movement.Extra)
            RetimeMonsterMovement(monster, speed.Movement.Extra - previousMove, now);
    }

    static void RetimeMonsterMovement(SpawnedMonster monster, int change, double now)
    {
        int pct = monster.Effects.MovementPct;
        double scale = MonsterWalkSpeedAt(monster, pct) / MonsterWalkSpeedAt(monster, pct - change);
        SetMonsterWalkFactor(monster, MonsterWalkState(monster, now).Factor, now, scale);
    }

    internal static MonsterAttack WithPhysicalAura(MonsterAttack attack, int strength)
    {
        var basis = attack.DamageBasis ?? (Modifiers: null, Unbuffed: attack.Damage);
        var damage = basis.Unbuffed;
        if (strength != 0)
            damage = basis.Modifiers is { } mods
                ? (attack.BaseDamage[DamageType.Physical].IsZero ? damage : mods.Hit(attack.BaseDamage, strength))
                : IncreasePhysical(damage, strength);
        return attack with { Damage = damage, DamageBasis = basis };
    }

    // Ordered attacks carry their damage through flight. A scripted contact can supply its own typed damage packet.
    static DamagePacket MonsterAuraHitDamage(SpawnedMonster monster, MonsterAttack attack, DamagePacket damage) =>
        monster.PhysicalDamageAura.Rate == 0 || attack.DamageBasis is not null || attack.SourceEffects is not null
            ? damage : IncreasePhysical(damage, monster.PhysicalDamageAura.Rate);

    static DamagePacket IncreasePhysical(DamagePacket damage, int strength) => damage[DamageType.Physical].IsZero ? damage
        : damage.Select((type, range) => type == DamageType.Physical ? range.Scale(1 + strength / 100d).Round() : range);

    static List<(uint Key, int Value)> MonsterSpeedStats(SpawnedMonster monster, double now, bool movement, bool timing)
    {
        List<(uint, int)> stats = [];
        if (movement) stats.Add((StatKeys.MovementVelocityPct, MonsterWalkState(monster, now).Pct));
        if (timing && (monster.Kit.Count > 0 ? monster.Kit[0].Attack : monster.Attack) is { } attack)
        {
            if (monster.Target is { } target)
                attack = attack with { Seconds = MonsterSwingSeconds(target, monster, attack, now) };
            // Non-pathing belongs to the skill. Putting it on the actor also disables its Move action.
            stats.AddRange(attack.SkillStats().Where(s => s.Item1 is StatKeys.MainHandAttackDurationMs or StatKeys.MainHandTotalAttackDurationMs
                or StatKeys.OffHandAttackDurationMs or StatKeys.OffHandTotalAttackDurationMs or StatKeys.HundredTimesAttacksPerSecond
                or StatKeys.BaseSpellCastTimeMs or StatKeys.VirtualBaseSpellCastTimeMs or StatKeys.HundredTimesCastsPerSecond));
        }
        return stats;
    }

    static Dictionary<ushort, GrantedSkill> SpeedAuraSkills(SpawnedMonster monster, MonsterObject origin) =>
        !monster.Scripted && monster.Attack is { Scripted: false } && origin.Skills is { } skills
            ? BossKits.UpdatedSkills(monster, skills) : [];

    static bool AuraChanged(AuraState state, AuraView view) =>
        (view.EmitterBuff != 0) != state.Emits || view.Source != state.Source || view.Rate != state.Rate;

    static bool MonsterAurasChanged(SpawnedMonster monster, MonsterView view)
    {
        bool dead = monster.Dead || monster.Life == 0;
        return AuraChanged(dead ? default : monster.LifeAura, view.LifeAura)
            || AuraChanged(dead ? default : monster.ElementalResistanceAura, view.ElementalResistanceAura)
            || AuraChanged(dead ? default : monster.PhysicalDamageAura, view.PhysicalDamageAura)
            || AuraChanged(EnemySlowOn(monster), view.EnemySlow)
            || AuraChanged(dead ? default : monster.SpeedAura.Attack, view.SpeedAura.Buff)
            || (dead ? 0 : monster.SpeedAura.Cast.Rate) != view.SpeedAura.Cast
            || (dead ? 0 : monster.SpeedAura.Movement.Rate) != view.SpeedAura.Movement;
    }

    static MonsterView PresentMonsterAuras(ClientState conn, WorldRecordBuilder packets, uint id, SpawnedMonster monster, MonsterView view)
    {
        bool dead = monster.Dead || monster.Life == 0;
        return view with
        {
            LifeAura = Present(RecoveryAura, dead ? default : monster.LifeAura, view.LifeAura),
            ElementalResistanceAura = Present(ResistanceAura, dead ? default : monster.ElementalResistanceAura, view.ElementalResistanceAura),
            PhysicalDamageAura = Present(PhysicalAura, dead ? default : monster.PhysicalDamageAura, view.PhysicalDamageAura),
            EnemySlow = Present(EnemySlowAura, EnemySlowOn(monster), view.EnemySlow),
            SpeedAura = PresentSpeed(),
        };

        (AuraView Buff, int Cast, int Movement) PresentSpeed()
        {
            var active = dead ? default : monster.SpeedAura;
            var old = view.SpeedAura;
            var buff = Present(AttackSpeedAura, active.Attack, old.Buff);
            if (buff.Source != active.Attack.Source) return old;
            bool movement = active.Movement.Rate != old.Movement;
            bool timing = buff.Rate != old.Buff.Rate || active.Cast.Rate != old.Cast;
            if (!dead && (movement || timing))
            {
                var stats = MonsterSpeedStats(monster, conn.Clock.Elapsed.TotalSeconds, movement, timing);
                if (stats.Count != 0) packets.Presentation(id, InstanceEntities.StatsUpdate(id, stats));
                if (timing)
                {
                    var origin = conn.Instance.World.MonsterOrigins[id].Object;
                    var skills = SpeedAuraSkills(monster, origin);
                    if (skills.Count > 0)
                        packets.Presentation(id, InstanceEntities.ActorSkills(id, checked((byte)origin.Components.ToList().IndexOf("Actor")), [.. skills.Values]));
                }
            }
            return (buff, active.Cast.Rate, active.Movement.Rate);
        }

        AuraView Present(AuraEffect effect, AuraState active, AuraView old)
        {
            if (!AuraChanged(active, old)) return old;
            // A framed observer sees this projection again after the source's birth has been written.
            if (active.Source != 0 && active.Source != id && (!conn.MonsterViews.ContainsKey(active.Source) ||
                conn.LockstepClient && !conn.WorldOutput.PublishedObject(active.Source).IsReady)) return old;
            ushort emitter = old.EmitterBuff, receiver = old.ReceiverBuff;
            if (active.Emits && emitter == 0)
                packets.Presentation(id, InstanceEntities.AddBuff(id, emitter = conn.NextBuff++, effect.Buff, id, auraSource: true, displayState: 0));
            if (!active.Emits && emitter != 0)
            {
                packets.Presentation(id, InstanceEntities.RemoveBuff(id, emitter));
                emitter = 0;
            }
            if (old.Source != active.Source)
            {
                if (receiver != 0) packets.Presentation(id, InstanceEntities.RemoveBuff(id, receiver));
                receiver = 0;
                if (active.Source != 0)
                    packets.Presentation(id, InstanceEntities.AddBuff(id, receiver = conn.NextBuff++, effect.Buff, active.Source));
            }
            if (effect.Buff is not (SpeedAuraBuff or EnemySlowBuff) && old.Rate != active.Rate && !dead)
                packets.Presentation(id, InstanceEntities.StatsUpdate(id, [(Stats.Key(effect.Stat)!.Value, monster.Effects[effect.Stat])]));
            return (emitter, receiver, active.Source, active.Rate);
        }
    }
}
