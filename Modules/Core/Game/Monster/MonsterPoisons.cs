using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    /// <summary>BuffDefinitions row 10 <c>poison</c>.</summary>
    public const ushort PoisonBuffRow = 10;

    /// <summary>A skill's poison damage and duration, as <see cref="SupportedIgnite"/>: its panel poison rows.</summary>
    public static MonsterPoisons.Settings SupportedPoison(Player player, ushort skillId, bool spell,
        IReadOnlyDictionary<uint, int> playerStats, SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0,
        SupportedHitInputs? capturedInputs = null, int addedChaos = 0)
    {
        Func<uint, int> get;
        if (capturedInputs is null)
        {
            var merged = MergeSkillStats(playerStats, SupportedSkill(player, skillId).Stats);
            get = name => name is var key ? merged.GetValueOrDefault(key) : 0;
        }
        else
            get = capturedInputs.Get;
        string hand = spell ? "spell" : "main_hand";
        var rows = capturedInputs is null
            ? SupportedAilmentRows(player, skillId, hand, weapon, additionalIncreased, addedChaos: addedChaos)
            : SupportedAilmentRowsFrom(capturedInputs, hand, additionalIncreased);
        return WithOverheat(player, skillId, MonsterPoisons.Settings.From(rows, hand, get,
            spell ? null : weapon is null ? MainHandLocalSources(player) : weapon.LocalSources));
    }

    /// <summary>The default melee attack's poison, from the rows <see cref="DefaultAttackIgnite"/> reads.</summary>
    public static MonsterPoisons.Settings DefaultAttackPoison(Player player, IReadOnlyDictionary<uint, int> playerStats,
        SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0, DamageConversion? conversion = null,
        int? accuracy = null)
    {
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        var sourceWeapon = weapon ?? WeaponFor(player);
        return MonsterPoisons.Settings.From(AilmentPanelStats.Compute(new("main_hand", Spell: false, Melee: true),
            SupportedAttackDamage.Raw(sourceWeapon, Get, new(accuracy ?? PlayerAccuracy(player, playerStats)), melee: true),
            conversion ?? SupportedAttackDamage.WithWeaponGains(DamageStats.Conversion(playerStats, Stats, attack: true), sourceWeapon),
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
        MonsterPoisons.Settings settings, double now, Random random) =>
        TryPoisonOnHitLocked(player, target, mitigated, spell, critical, playerStats, skill, settings, now, random,
            out _, out _);

    public static bool TryPoisonOnHitLocked(Player player, SpawnedMonster target, DamagePacket mitigated,
        bool spell, bool critical, IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int>? skill,
        MonsterPoisons.Settings settings, double now, Random random, out double perSecond, out double seconds)
    {
        perSecond = seconds = 0;
        if (target.Dead || target.Life == 0 || settings.Seconds <= 0) return false;
        double phys = mitigated[DamageType.Physical].Min;
        double chaos = mitigated[DamageType.Chaos].Min;

        int Player(uint key) => playerStats.GetValueOrDefault(key);
        int Skill(uint name) => skill is not null && name is var key ? skill.GetValueOrDefault(key) : 0;
        int Get(uint name) => Player(name) + Skill(name);
        if ((Get(Stat.AllDamageCanPoison) != 0 ? mitigated.MinTotal : phys + chaos) <= 0) return false;

        if (MonsterPoisons.Prohibited(Get)) return false;

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
        // Lethal Dose: a hit that finds the enemy unpoisoned applies additional poisons on top of its own.
        int stacks = 1 + (target.Poisons.Stacks(now) == 0
            ? Math.Max(0, Get(Stat.AdditionalPoisonsPlusToApplyVsNonPoisonedEnemies)) : 0);
        perSecond = min + (Math.Max(min, max) - min) * random.NextDouble();
        seconds = settings.Seconds * MonsterPoisons.SelfDuration(name => target.Mods.Effects[name]);
        int accepted = 0;
        for (int i = 0; i < stacks; i++)
            if (target.Poisons.Add(now, perSecond, seconds)) accepted++;
        if (accepted == 0) return false;
        InflictedLocked(player, target, AilmentType.Poison, now + seconds);
        ReservationPoisonLocked(player, now);
        return true;
    }

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
                else damage = m.Poisons.Advance(now, MonsterDotTaken(m, DamageType.Chaos, now), m.Life);
                if (damage > 0)
                {
                    (m.Life, bool stageStarts) = TakeOwnerDamageLocked(player, m, damage);
                    m.Aggro = m.Life > 0;
                    if (m.Life == 0)
                    {
                        m.Dead = true;
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
                    ShowDot(player, id, ref m.Poisons.Buff, ref m.Poisons.BuffSource, new(PoisonBuffRow, player.Id,
                        Stacks: (ushort)Math.Clamp(m.Poisons.Stacks(now), 1, ushort.MaxValue),
                        U68: Buff.Seconds(m.Poisons.Until - now), Visual: AilmentArtVisual(player, "poison")));
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
