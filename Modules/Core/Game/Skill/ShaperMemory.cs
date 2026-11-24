using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string ShaperMemoryEffect = "SummonShaperApparition";
    const string ShaperMemoryBody = "Metadata/Monsters/AtlasBosses/TheShaperBossStandaloneMirage";
    const int RemembranceEnergyShield = 200, RemembranceMaximum = 10;
    const int Devastation = 12581, Ire = 12582, Despair = 12583;
    const ushort ShaperMemoryNoChargeBuff = 2638, ShaperMemorySkill = 0x4000;
    const int ShaperMemorySlamRadius = 23, ShaperMemoryBeamHalfWidth = 4;

    sealed class ShaperMemoryState(WornGrantedSkills.Grant grant)
    {
        public readonly WornGrantedSkills.Grant Grant = grant;
        public int EnergyShieldSpent;
        public bool Pending;
        public uint Actor;
        public SpawnedMonster? Body;
        public Action<bool>? RemoveActor;
    }
    static readonly RuleTable<Player, ShaperMemoryState> ShaperMemories = new();

    static int ReadShaperMemoryVariant(IReadOnlyDictionary<uint, int> stats) =>
        GolemStat(stats, Stat.ShaperApparitionAbility);

    static WornGrantedSkills.Grant? CurrentShaperMemoryGrant(Player player) =>
        AllWornGrantedSkillsOf(player).FirstOrDefault(grant => grant.EffectId == ShaperMemoryEffect) is { Host: > 0 } worn
            ? worn : null;

    static void RecordShaperMemoryEnergyShieldLocked(Player player, uint spent)
    {
        if (CurrentShaperMemoryGrant(player) is not { } grant) return;
        if (!ShaperMemories.TryGetValue(player, out var state) || state.Grant != grant)
            ShaperMemories.AddOrUpdate(player, state = new(grant));
        if (state.Pending || state.Actor != 0) return;
        state.EnergyShieldSpent = (int)Math.Min(RemembranceEnergyShield * RemembranceMaximum, state.EnergyShieldSpent + (long)spent);
    }

    static async Task<bool> CastShaperMemory(Player player, ushort source, (int X, int Y) aim, Func<bool> current)
    {
        var gem = SupportedSkill(player, source);
        if (gem.WornGrant is not { } grant || !ShaperMemories.TryGetValue(player, out var state) || state.Grant != grant
            || state.Actor != 0 || state.Pending || state.EnergyShieldSpent < RemembranceEnergyShield * RemembranceMaximum
            || Monsters.Find(ShaperMemoryBody) is not { Supported: true } variety) return false;
        var catalog = SkillsFor(player);
        int effect = ReadShaperMemoryVariant(CharacterPlayerStats(player)) switch { 1 => Devastation, 2 => Ire, 3 => Despair, _ => -1 };
        if (effect < 0) return false;
        int level = gem.Level;
        var local = catalog.EffectStats(effect, level).ToDictionary(p => p.Item1, p => p.Item2);
        if (catalog.FindByRow(ShaperMemorySkill, effect, level, [.. local.Select(p => (p.Key, p.Value))]) is not { } skill) return false;
        int epoch = player.ProjectileEpoch;
        bool SourceCurrent() => CurrentGameWork && player.ProjectileActive(epoch) && player.PlayerLife > 0
            && CurrentShaperMemoryGrant(player) == grant;
        state.Pending = true;
        try
        {
            double release = AreaTime(player) + (CastMs(player, gem.Effect, source) ?? 1000) / 1000d;
            while (AreaTime(player) < release && current() && SourceCurrent())
                await AreaWait(PlayerUpdateRole.Minions, 25);
            if (!current() || !SourceCurrent()) return false;
            var from = NextToPlayer(player);
            await SpawnMonster(player, variety, (byte)Math.Clamp(player.Pools.Level, 1, 100), MonsterRarity.Normal,
                from.X, from.Y, friendly: true, log: false, ownedSkills: [skill], ownedCurrent: () => current() && SourceCurrent(),
                owned: (id, body) =>
                {
                    body.DamageImmune = true;
                    body.NoRewards = true;
                    body.Attack = null;
                    state.Actor = id;
                    state.Body = body;
                    state.EnergyShieldSpent = 0;
                });
            if (state.Actor == 0 || state.Body is null) return false;
            var session = ProjectilesFor(player);
            player.Area.Buffs.Set(player.Id, new(ShaperMemoryNoChargeBuff, state.Actor));
            ScheduleShaperMemory(player, session, state, local, effect, level, from, aim, SourceCurrent);
            return true;
        }
        finally { state.Pending = false; }
    }

    static void ScheduleShaperMemory(Player player, ProjectileSession session, ShaperMemoryState state,
        IReadOnlyDictionary<uint, int> own, int effect, int level, (int X, int Y) from, (int X, int Y) aim, Func<bool> sourceCurrent)
    {
        uint id = state.Actor;
        var body = state.Body!;
        var area = player.Area;
        bool Alive() => sourceCurrent() && ReferenceEquals(area.Monsters.GetValueOrDefault(id), body);
        var stats = MergeSkillStats(CharacterPlayerStats(player), own);
        int Get(uint key) => GolemStat(stats, key);
        var catalog = SkillsFor(player);
        int castMs = Math.Max(100, catalog.CastMs(effect));
        var target = area.MonstersNear(aim, 24)
            .Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                && Dist(p.Value.Walk.Position(AreaTime(player)), aim) <= 24)
            .OrderBy(p => Dist(p.Value.Walk.Position(AreaTime(player)), aim)).ThenBy(p => p.Key).FirstOrDefault();
        if (target.Value is not null) aim = target.Value.Walk.Position(AreaTime(player));
        session.World(id, InstanceProtocol.ActionStarted(id, from.X, from.Y, aim.X, aim.Y, ShaperMemorySkill, 0xffff, player.MonsterOrderFlags));
        long start = Math.Max(session.Runtime.Now, ProjectileRuntime.Time(AreaTime(player)));
        long finish = start + ProjectileRuntime.Time(castMs / 1000d);
        state.RemoveActor = Remove;
        if (effect == Devastation)
        {
            var weapon = HandWeapon(player, AttackHand.Main);
            var (coefficient, effectiveness) = catalog.DamageCoefficients(effect, level);
            var raw = SupportedAttackDamage.Raw(weapon, Get, default, coefficient, effectiveness, melee: true);
            var hit = PlayerHitDamage.Resolve(raw, Get, SupportedAttackDamage.WithWeaponGains(new(), weapon), spell: false,
                melee: true, area: true, additionalIncreased: SupportedAttackDamage.WeaponIncrease(weapon, Get));
            session.Runtime.Schedule(finish, () =>
            {
                if (Alive())
                {
                    session.World(id, InstanceProtocol.Teleport(id, aim.X, aim.Y));
                    HitArea(hit, aim, ShaperMemorySlamRadius, false);
                }
                Remove();
            });
        }
        else if (effect == Ire)
        {
            int ms = GolemStat(own, Stat.BaseSkillEffectDuration);
            long until = finish + ProjectileRuntime.Time(ms / 1000d);
            long step = ProjectileRuntime.Time(.1);
            var packet = DamagePacket.Empty;
            foreach (var (type, term) in new[]
            {
                (DamageType.Fire, Stat.BaseFireDamageToDealPerMinute),
                (DamageType.Cold, Stat.BaseColdDamageToDealPerMinute),
                (DamageType.Lightning, Stat.BaseLightningDamageToDealPerMinute),
            })
            {
                double amount = GolemStat(own, term) / 600d * Math.Max(0, 1 + (Get(Stat.DamagePlusPct) + (double)Get(Stat.SpellDamagePlusPct)
                    + Get(Stat.DamageOverTimePlusPct) + Get(Stat.ElementalDamagePlusPct)) / 100d)
                    * Math.Max(0, 1 + Get(Stat.DotMultiplierPlus) / 100d);
                packet = packet.Add(type, new(amount, amount));
            }
            for (long at = finish + step; at <= until; at += step)
                session.Runtime.Schedule(at, () =>
                {
                    if (!Alive()) return;
                    double now = AreaTime(player);
                    foreach (var (enemyId, enemy) in area.Monsters.ToArray())
                        if (enemy.Hostile && !enemy.Dead && InsideFrigidBondBeam(enemy.Walk.Position(now), from, aim, ShaperMemoryBeamHalfWidth))
                        {
                            int damage = ActorScale.Saturate((long)packet.Components.Sum(p => p.Range.Min * MonsterDotTaken(enemy, p.Type, now)));
                            Damage(enemyId, enemy, damage, true, now);
                        }
                });
            session.Runtime.Schedule(until, () => Remove());
        }
        else
        {
            int repeatCount = 1 + Math.Max(0, GolemStat(own, Stat.SkillRepeatCount));
            var raw = SpellHit.Resolve(SkillHitPlan.ForSkill(catalog, effect, level, 0), Stats, stats, own, new DamageConversion(),
                AddedEffectiveness(player, effect, level));
            const string projectile = "Metadata/Projectiles/ShaperProjectileLegacyRose";
            var profile = new ProjectileProfile(projectile) { Speed = 90, Radius = 2, Range = 100, Pierce = -1, DirectDamage = true };
            var key = session.ActorKey(id, body);
            for (int i = 0; i < repeatCount; i++)
                session.Schedule(start + ProjectileRuntime.Time((i + 1d) * castMs / (1000d * repeatCount)), Alive, () =>
                    new(profile, key, key, 1, ProjectileSession.Vector(from), ProjectileSession.Vector(aim))
                    {
                        Payload = new ObjectProjectileOffense("Shaper's Despair", (_, contact) =>
                        {
                            if (!sourceCurrent() || area.Monsters.GetValueOrDefault(contact.Target.Id) is not { Hostile: true, Dead: false } enemy)
                                return ProjectileHitResult.Miss;
                            double now = contact.At / (double)ProjectileRuntime.QuantaPerSecond;
                            var pen = new Penetration.PerType(0, GolemStat(own, Stat.BaseReduceEnemyColdResistancePct), 0, 0);
                            int damage = MonsterTakes(enemy, raw, spell: true, AreaRng(player), pen, now);
                            return Damage(contact.Target.Id, enemy, damage, false, now) ? ProjectileHitResult.Hit : ProjectileHitResult.Miss;
                        }),
                    });
            session.Runtime.Schedule(finish + ProjectileRuntime.Time(.1), () => Remove());
        }
        session.Runtime.Schedule(start + ProjectileRuntime.Time(.1), Check);

        void Check()
        {
            if (state.Actor != id) return;
            if (!Alive()) { Remove(); return; }
            session.Runtime.Schedule(session.Runtime.Now + ProjectileRuntime.Time(.1), Check);
        }

        void HitArea(DamagePacket packet, (int X, int Y) at, int radius, bool spell)
        {
            double now = AreaTime(player);
            foreach (var (enemyId, enemy) in area.MonstersNear(at, radius).ToArray())
                if (enemy.Hostile && !enemy.Dead && Dist(enemy.Walk.Position(now), at) <= radius)
                    Damage(enemyId, enemy, MonsterTakes(enemy, packet, spell, AreaRng(player), now: now), false, now);
        }

        bool Damage(uint enemyId, SpawnedMonster enemy, int amount, bool dot, double now)
        {
            if (amount <= 0 || ApplyTypedMonsterHitLocked(player, enemy, amount, 0, allyAt: from) is not { } hit) return false;
            if (hit.StageStarts)
            {
                CommitBossStage(player, enemyId, enemy, now);
                hit = hit with { StageStarts = false };
            }
            CommitMinionHitLocked(player, enemyId, enemy, hit, amount, now, "Shaper Memory", source: body, damageOverTime: dot);
            session.Enqueue(() => PublishMinionOutcomes(player));
            return true;
        }

        void Remove(bool retire = false)
        {
            if (state.Actor != id) return;
            state.Actor = 0;
            state.Body = null;
            state.RemoveActor = null;
            if (ReferenceEquals(area.Monsters.GetValueOrDefault(id), body)) area.Monsters.Remove(id);
            player.MinionOrigins.TryRemove(id, out _);
            area.Buffs.Clear(player.Id, ShaperMemoryNoChargeBuff, id);
            if (retire) player.RetiredCombatObjects.Add(id);
            else session.Enqueue(() => SendWorldRemoval(player, id));
        }
    }
}
