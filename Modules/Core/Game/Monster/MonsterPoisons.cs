using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    static MonsterPoisons.Settings PoisonSettingsFrom(IEnumerable<(uint Stat, int Value)> rows, string hand, Func<uint, int> get,
        IReadOnlyDictionary<uint, int>? local = null, bool projectileAttack = false)
    {
        var settings = MonsterPoisons.Settings.From(rows, hand, get, local, projectileAttack);
        double factor = Math.Max(0, 1 + get(Stat.UniqueVolkuursClutchPoisonDurationPlusPctFinal) / 100d);
        return settings with { Seconds = settings.Seconds * factor };
    }

    /// <summary>BuffDefinitions row 10 <c>poison</c>.</summary>
    public const ushort PoisonBuffRow = 10;

    /// <summary>A skill's poison damage and duration, as <see cref="SupportedIgnite"/>: its panel poison rows.</summary>
    public static MonsterPoisons.Settings SupportedPoison(Player player, ushort skillId, bool spell,
        IReadOnlyDictionary<uint, int> playerStats, SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0,
        SupportedHitInputs? capturedInputs = null, int addedChaos = 0)
    {
        var get = SupportedAilmentStats(player, skillId, playerStats, capturedInputs);
        string hand = spell ? "spell" : "main_hand";
        var rows = capturedInputs is null
            ? SupportedAilmentRows(player, skillId, hand, weapon, additionalIncreased, addedChaos: addedChaos)
            : SupportedAilmentRowsFrom(capturedInputs, hand, additionalIncreased);
        return WithOverheat(player, skillId, PoisonSettingsFrom(rows, hand, get,
            spell ? null : weapon is null ? MainHandLocalSources(player) : weapon.LocalSources));
    }

    /// <summary>The default melee attack's poison, from the rows <see cref="DefaultAttackIgnite"/> reads.</summary>
    public static MonsterPoisons.Settings DefaultAttackPoison(Player player, IReadOnlyDictionary<uint, int> playerStats,
        SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0, DamageConversion? conversion = null,
        int? accuracy = null)
    {
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        var sourceWeapon = weapon ?? WeaponFor(player);
        return PoisonSettingsFrom(AilmentPanelStats.Compute(new("main_hand", Spell: false, Melee: true) { WeaponStats = sourceWeapon.LocalSources },
            SupportedAttackDamage.Raw(sourceWeapon, Get, new(accuracy ?? PlayerAccuracy(player, playerStats)), melee: true),
            conversion ?? SupportedAttackDamage.WithWeaponGains(DamageStats.Conversion(playerStats, Stats, attack: true), sourceWeapon, Get),
            Get, additionalIncreased),
            "main_hand", Get, sourceWeapon.LocalSources);
    }

    // A landed hit's poison: the chance the panel shows (virtual_total_{hand}_chance_to_poison_on_hit_%: the caster's and
    // the skill's stats, and for an attack its weapon's own, Settings.WeaponChance), then the monster's avoidance. Its damage
    // per second (an independent roll in the non-critical or critical range) and duration come from the hit's panel poison
    // rows (<paramref name="settings"/>, captured with the hit as its ignite's are), so a poison deals what the panel shows:
    // the hit's own increases and the monster's resistance, already in <paramref name="mitigated"/>, must not reach it.
    public static void PoisonOnHitLocked(Player player, SpawnedMonster target, DamagePacket mitigated,
        bool spell, bool critical, IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int>? skill,
        MonsterPoisons.Settings settings, double now, Random random, SkillPoisonStats poisonSkill = default) =>
        TryPoisonOnHitLocked(player, target, mitigated, spell, critical, playerStats, skill, settings, now, random,
            poisonSkill, out _, out _);
    public static bool TryPoisonOnHitLocked(Player player, SpawnedMonster target, DamagePacket mitigated,
        bool spell, bool critical, IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int>? skill,
        MonsterPoisons.Settings settings, double now, Random random, out double perSecond, out double seconds) =>
        TryPoisonOnHitLocked(player, target, mitigated, spell, critical, playerStats, skill, settings, now, random,
            default, out perSecond, out seconds, playerOwned: true);

    public static bool TryPoisonOnHitLocked(Player player, SpawnedMonster target, DamagePacket mitigated,
        bool spell, bool critical, IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int>? skill,
        MonsterPoisons.Settings settings, double now, Random random, SkillPoisonStats poisonSkill, out double perSecond, out double seconds,
        bool playerOwned = true)
    {
        perSecond = seconds = 0;
        if (target.Dead || target.Life == 0 || settings.Seconds <= 0) return false;
        double phys = mitigated[DamageType.Physical].Min;
        double chaos = mitigated[DamageType.Chaos].Min;

        int Player(uint key) => playerStats.GetValueOrDefault(key);
        int Skill(uint name) => skill is not null && name is var key ? skill.GetValueOrDefault(key) : 0;
        int Get(uint name) => Player(name) + Skill(name);
        bool allDamage = poisonSkill.AllDamage || Get(Stat.AllDamageCanPoison) != 0 || !spell && !settings.Secondary && settings.AllDamageWithWeapon;
        if ((allDamage ? mitigated.MinTotal : phys + chaos) <= 0) return false;

        if (MonsterPoisons.Prohibited(Get)) return false;
        if (Get(Stat.CannotPoisonPoisonedEnemies) != 0 && target.Poisons.Stacks(now) > 0) return false;

        long chance = (long)Get(Stat.BaseChanceToPoisonOnHitPct)
            + (spell
                ? Get(Stat.SpellsChanceToPoisonOnHitPct)
                : settings.Secondary ? 0
                : Get(Stat.ChanceToPoisonOnHitWithAttacksPct) + settings.WeaponChance + settings.ProjectileChance);
        if (chaos > 0) chance += Get(Stat.ChaosDamageChanceToPoisonPct);

        if (chance <= 0 || !RandomRolls.Chance((int)Math.Clamp(chance, 0, 100), 100, random)) return false;
        if (target.Mods.Effects[Stat.CannotBePoisoned] != 0) return false;
        int avoid = target.Mods.Effects[Stat.AvoidPoisonPct];
        if (avoid > 0 && RandomRolls.Chance(avoid, 100, random)) return false;

        var (min, max) = critical ? (settings.CriticalMin, settings.CriticalMax) : (settings.Min, settings.Max);
        perSecond = min + (Math.Max(min, max) - min) * random.NextDouble();
        seconds = settings.Seconds * MonsterPoisons.SelfDuration(name => target.Mods.Effects[name]);
        // Master Toxicist: "Poisons you inflict during any Flask Effect have N% chance to deal 100% more Damage", rolled for
        // each poison applied.
        int doubleChance = Get(Stat.PathfinderPoisonDamagePlus100PctFinalChanceDuringFlaskEffect) is var finalChance and > 0 && AnyFlaskEffect(player)
            ? Math.Min(finalChance, 100) : 0;
        bool additionalAllowed = Get(Stat.CannotInflictAdditionalPoisons) == 0;
        int extraOnFreshTarget = additionalAllowed ? Get(Stat.AdditionalPoisonsPlusToApplyVsNonPoisonedEnemies) : 0;
        int extraWithWeapon = additionalAllowed && !spell && !settings.Secondary ? settings.AdditionalWithWeapon : 0;
        if (!ApplyHitPoisonsLocked(target.Poisons, now, perSecond, seconds, extraOnFreshTarget, doubleChance, random, extraWithWeapon, poisonSkill)) return false;
        if (playerOwned) InflictedLocked(player, target, AilmentType.Poison, now + seconds / target.RecipientDebuffRate);
        ReservationPoisonLocked(player, now);
        return true;
    }

    static bool ApplyHitPoisonsLocked(MonsterPoisons target, double now, double perSecond, double seconds, int additional,
        int doubleChance = 0, Random? random = null, int additionalAlways = 0, SkillPoisonStats poisonSkill = default)
    {
        // Decide whether Lethal Dose applies before inserting any poisons from this hit.
        int extra = additional > 0 && target.Stacks(now) == 0 ? additional : 0;
        extra = ActorScale.Saturate((long)extra + Math.Max(0, additionalAlways));
        int totalDouble = Math.Clamp(doubleChance + poisonSkill.DoubleChance, 0, 100);
        bool AddPoison() => target.Add(now,
            totalDouble > 0 && random is not null && RandomRolls.Chance(totalDouble, 100, random) ? perSecond * 2 : perSecond, seconds);
        bool applied = AddPoison();
        for (int i = 0; i < extra; i++) applied |= AddPoison();
        return applied;
    }

    /// <summary>The 0x1c1 for the poison on this monster now, built exactly as the tick builds it: the stacks and
    /// the countdown to the last poison's end, so a hit that shows it and the tick that follows agree on one record.
    /// Only call after a hit applied a poison (the stacks are what the hit added).</summary>
    public static Buff PoisonBuffToShow(Player player, SpawnedMonster m, double now) => new(PoisonBuffRow, player.Id,
        Stacks: (ushort)Math.Clamp(m.Poisons.Stacks(now), 1, ushort.MaxValue),
        U68: Buff.Seconds(m.Poisons.Until - now), Visual: AilmentArtVisual(player, "poison"));

    /// <summary>Stage a hit's poison buff in the area's frame under lock, with the same record the tick shows: the
    /// tick then finds the same buff already shown and sends nothing again. No stat is read here; the stacks and the
    /// countdown are the monster's own counters.</summary>
    public static void ShowPoisonBuff(Player player, uint id, SpawnedMonster m, Buff buff) =>
        ShowDot(player, id, ref m.Poisons.Buff, ref m.Poisons.BuffSource, buff);

    public static async Task TickPoisons(Player player)
    {
        var active = player.Area.ActivePoisonMonsters;
        if (active.Count == 0) return;
        var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage)>();
        using (ProjectileMutation(player))
        {
            if (player.Closed || player.LeavingArea || active.Count == 0) return;
            double now = ProjectileWorldTime(player);
            List<uint>? done = null;
            foreach (uint id in active)
            {
                if (!player.Area.Monsters.TryGetValue(id, out var m)) { (done ??= []).Add(id); continue; }
                if (OwnedGolemActor(player, id, m)) { (done ??= []).Add(id); continue; }
                if (m.Poisons.Idle) { (done ??= []).Add(id); continue; }
                int damage = 0;
                if (m.Dead || m.Life == 0) m.Poisons.ClearAtDeath(now);
                else
                {
                    uint damageToKill = m.Life;
                    if (m.Effects[Stat.ChaosDamageDoesNotBypassEnergyShield] != 0
                        || m.Effects[Stat.VirtualChaosDamageDoesNotBypassEnergyShield] != 0)
                        damageToKill = (uint)Math.Min(uint.MaxValue, (long)m.Life + MonsterEnergyShieldOf(m).Current);
                    if (MaimDotTaken.TryGetValue(m, out var maim) && maim.Overlaps(m.Poisons.AccruedThrough, now))
                        damage = m.Poisons.Advance(now, maim.Boundaries(m.Poisons.AccruedThrough, now),
                            at => MonsterDotTaken(m, DamageType.Chaos, at), damageToKill);
                    else damage = m.Poisons.Advance(now, MonsterDotTaken(m, DamageType.Chaos, now, maimTaken: 0), damageToKill);
                }
                if (damage > 0)
                {
                    int lifeDamage = ApplyMonsterDotEnergyShieldLocked(player.Area, m, damage, chaos: true, AreaTime(player));
                    bool stageStarts = false;
                    if (lifeDamage > 0) (m.Life, stageStarts) = TakeOwnerDamageLocked(player, m, lifeDamage, poisonDeath: true);
                    m.Aggro = m.Life > 0;
                    if (m.Life == 0)
                    {
                        m.Dead = true;
                        CleanupDeadMonsterLocked(player.Area, id, m);
                        CollectVaalSoulAtDeathLocked(player, m);
                        m.Poisons.ClearAtDeath(now, poisonDeath: true);
                    }
                    hits.Add((id, m, m.Life, damage));
                    if (stageStarts) CommitBossStage(player, id, m, now);
                }
                if (m.Dead || m.Life == 0 || !m.Poisons.Poisoned)
                {
                    EndDot(player, id, ref m.Poisons.Buff, m.Poisons.BuffSource);
                    if (m.Poisons.Idle) (done ??= []).Add(id);
                }
                else
                {
                    // The stacks and the countdown to the last poison's end; a new poison moves both.
                    ShowDot(player, id, ref m.Poisons.Buff, ref m.Poisons.BuffSource, PoisonBuffToShow(player, m, now));
                }
            }
            if (done is not null) foreach (uint id in done) active.Remove(id);
        }
        foreach (var (id, m, life, damage) in hits)
        {
            if (life == 0)
                SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
            if (life == 0 && DotDeathKind(m, life) is var deathKind && deathKind != InstanceProtocol.DeathKindNormal)
                await SendWorldRecord(player, id, InstanceProtocol.LifeUpdate(id, life, u2c: deathKind,
                        orderKey: player.Connection.LockstepClient ? uint.MaxValue : 0u),
                    $"0x01b1 monster 0x{id:x} ({m.Label}) died of poison");
            else
                Note(life == 0 ? $"0x01b1 monster 0x{id:x} ({m.Label}) died of poison" : null);
            if (life == 0) await DotKilled(player, id, m);
        }
    }
}