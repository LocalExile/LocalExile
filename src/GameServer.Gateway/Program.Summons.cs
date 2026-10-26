using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

partial class Program
{
    static SummonRoster SummonsOf(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.Summons is null)
            {
                conn.Summons = SummonRoster.For(CharacterName(conn));
                conn.Summons.Attach(conn.SummonLease);
            }
            return conn.Summons;
        }
    }

    static SpectralWolf.Values? WolfValues(ClientState conn, IReadOnlyDictionary<uint, int> owner)
    {
        int Get(string name) => Stats.Key(name) is uint key
            ? conn.PlayerStats.TryGetValue(key, out int replacement) ? replacement : owner.GetValueOrDefault(key) : 0;
        if (Get("trigger_level_20_summon_spectral_wolf_on_crit") == 0) return null;
        var gem = GemSkillEffects(conn).FirstOrDefault(g => Skills.EffectId(g.Effect) == SpectralWolf.Effect);
        return gem.Name is null ? null : SpectralWolf.For(Skills, Stats, gem.Effect, gem.Level, Get);
    }

    static void SummonStats(ClientState conn, Dictionary<uint, int> owner)
    {
        lock (conn.Monsters)
        {
            var values = WolfValues(conn, owner);
            int count = SummonsOf(conn).Read(conn.SummonLease, DateTimeOffset.UtcNow, conn.Paused,
                values is not null, values?.Limit ?? 0).Length;
            foreach (var (name, value) in new[] { ("number_of_active_wolves", count),
                ("virtual_number_of_wolves_allowed", values?.Limit ?? 0),
                ("attack_minimum_added_physical_damage", count * (values?.BuffMin ?? 0)),
                ("attack_maximum_added_physical_damage", count * (values?.BuffMax ?? 0)) })
                if (Stats.Key(name) is uint key)
                    owner[key] = name.StartsWith("attack_") ? owner.GetValueOrDefault(key) + value : value;
        }
    }

    static async Task TriggerWolf(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool critical)
    {
        if (!critical) return;
        bool triggered;
        lock (conn.Monsters)
            triggered = ReserveWolfLocked(conn, DateTimeOffset.UtcNow);
        if (triggered) await RefreshSummons(remote, stream, ciphers, conn);
    }

    static bool ReserveWolfLocked(ClientState conn, DateTimeOffset hitAt)
    {
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || conn.Paused || InTown(conn)) return false;
        return WolfValues(conn, CharacterStatSources(conn)) is { } values
            && SummonsOf(conn).Trigger(conn.SummonLease, hitAt, false,
                values.Limit, values.DurationMs, values.CooldownMs) is not null;
    }

    internal static (uint Life, MonsterAttack Attack, double Movement, int Chance, int Multiplier) WolfProfile(
        ClientState conn, MonsterCatalog.Variety variety, byte level)
    {
        var player = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        int life = Math.Max(1, (int)((Monsters.BaseLife(variety, level) ?? 1)
            * Math.Max(0, 100L + Get("minion_maximum_life_+%")) / 100));
        var damage = Monsters.BaseDamage(variety, level) ?? (1, 1);
        double scale = Math.Max(0, 1 + SpectralWolf.DamageIncrease(Get, conn.Pools.Dexterity) / 100.0);
        double attackTime = variety.AttackMs / 1000.0 / Math.Max(.01, 1 + Get("minion_attack_speed_+%") / 100.0);
        var gem = GemSkillEffects(conn).FirstOrDefault(g => Skills.EffectId(g.Effect) == SpectralWolf.Effect);
        var own = Skills.EffectStats(gem.Effect, gem.Level).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int Skill(string name) => Stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
        var crit = SpectralWolf.Critical(Get, Skill);
        return ((uint)life, new(SkillCatalog.FirstSkillId, attackTime, variety.AttackRange,
            DamagePacket.Of(DamageType.Physical, Math.Floor(damage.Item1 * scale), Math.Floor(damage.Item2 * scale))),
            variety.MovementSpeed * Math.Max(.01, 1 + Get("minion_movement_speed_+%") / 100.0),
            crit.Chance, crit.Multiplier);
    }

    static bool SummonCanActLocked(ClientState conn, SpawnedMonster monster)
    {
        if (monster.Summon is not { } member) return true;
        var values = WolfValues(conn, CharacterStatSources(conn));
        return SummonsOf(conn).Read(conn.SummonLease, DateTimeOffset.UtcNow, conn.Paused,
            values is not null && conn.PlayerLife > 0, values?.Limit ?? 0).Any(m => m.Id == member.Id);
    }

    static async Task RefreshSummons(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        bool sendOwnerStats = true)
    {
        await conn.SummonGate.WaitAsync();
        bool changed = false;
        try
        {
            if (conn.Closed || conn.LeavingArea) return;
            var roster = SummonsOf(conn);
            SpectralWolf.Values? values;
            SummonRoster.Member[] members;
            lock (conn.Monsters)
            {
                values = WolfValues(conn, CharacterStatSources(conn));
                members = roster.Read(conn.SummonLease, DateTimeOffset.UtcNow, conn.Paused,
                    values is not null && conn.PlayerLife > 0, values?.Limit ?? 0);
            }
            foreach (var (serial, entity) in conn.SummonObjects.ToArray())
                if (!members.Any(m => m.Id == serial))
                {
                    lock (conn.Monsters)
                    {
                        if (conn.Monsters.Remove(entity.Object, out var removed)) removed.Dead = true;
                    }
                    conn.SummonObjects.Remove(serial);
                    await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, entity.Buff), "Spectral Pack ends");
                    await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(entity.Object), "owned wolf removed");
                    changed = true;
                }
            if (values is not null && Monsters.Find(SpectralWolf.Variety) is { Supported: true } variety)
                foreach (var member in members)
                {
                    if (!conn.SummonObjects.TryGetValue(member.Id, out var entity))
                    {
                        (int X, int Y) at;
                        lock (conn.Monsters) at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, values.Level, MonsterRarity.Normal,
                            at.X, at.Y, friendly: true, damagedLife: member.Life, summon: member);
                        ushort buff = conn.AllocateBuff();
                        conn.SummonObjects[member.Id] = entity = (id, buff);
                        await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, buff, SpectralWolf.Buff, id),
                            "Spectral Pack: one wolf's added attack physical damage");
                        changed = true;
                    }
                    uint? refreshed = null;
                    SpawnedMonster? revisedWolf = null;
                    IReadOnlyList<(uint, int)>? revised = null;
                    GrantedSkill? revisedSkill = null;
                    lock (conn.Monsters)
                        if (conn.Monsters.GetValueOrDefault(entity.Object) is { Dead: false } wolf)
                        {
                            revisedWolf = wolf;
                            var profile = WolfProfile(conn, variety, values.Level);
                            bool speedChanged = wolf.Walk.Speed != profile.Movement;
                            if (wolf.MaxLife != profile.Life || wolf.Attack?.Seconds != profile.Attack.Seconds || speedChanged
                                || wolf.Attack.Damage[DamageType.Physical] != profile.Attack.Damage[DamageType.Physical])
                            {
                                wolf.Life = (uint)Math.Min(profile.Life, Math.Max(1, (long)wolf.Life * profile.Life / wolf.MaxLife));
                                wolf.MaxLife = profile.Life;
                                wolf.Attack = profile.Attack;
                                int movement = Stats.Key("minion_movement_speed_+%") is uint movementKey
                                    ? CharacterPlayerStats(conn).GetValueOrDefault(movementKey) : 0;
                                revised = [.. profile.Attack.Stats(), (StatKeys.MaximumLife, (int)profile.Life),
                                    (StatKeys.MovementVelocityPct, movement)];
                                revisedSkill = variety.GrantedEffects is [int effect, ..]
                                    ? Skills.FindByRow(profile.Attack.SkillId, effect, values.Level, profile.Attack.SkillStats()) : null;
                                refreshed = wolf.Life;
                            }
                            wolf.Walk.SetSpeed(profile.Movement, conn.Clock.Elapsed.TotalSeconds);
                            if (roster.TakeRefresh(conn.SummonLease, member.Id))
                            {
                                wolf.Life = wolf.MaxLife;
                                wolf.Leech.Clear();
                                refreshed = wolf.Life;
                            }
                            if (refreshed is not null) roster.SetLife(conn.SummonLease, member.Id, wolf.Life);
                        }
                    if (revised is not null)
                        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(entity.Object, revised), "minion modifiers changed");
                    if (revisedSkill is not null)
                        await Send(remote, stream, ciphers, InstanceEntities.ActorSkillsChange(entity.Object, 9, [revisedSkill], []), "minion skill modifiers changed");
                    if (refreshed is not null && revisedWolf is not null)
                        await SendMonsterLife(remote, stream, ciphers, conn, entity.Object, revisedWolf, "wolf life refreshed");
                }
            changed |= conn.SentWolves != members.Length;
            conn.SentWolves = members.Length;
        }
        finally { conn.SummonGate.Release(); }
        if (changed && sendOwnerStats) await SendCharacterStats(remote, stream, ciphers, conn, "owned summon count changed");
    }

    // Summons choose living hostiles; without a target they follow their owner. Ordinary friendly /spawn_mob objects
    // retain their old follower behaviour. Damage and crit read minion sources, never the owner's Rage/Gambler/penetration.
    static async Task RunSummonAi(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var orders = new List<(uint Id, (int X, int Y) From, (int X, int Y) To)>();
        var recovery = new List<(uint Id, SpawnedMonster Wolf)>();
        var swings = new List<(uint Id, SpawnedMonster Minion, uint TargetId, SpawnedMonster Target, (int X, int Y) From)>();
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters)
        {
            foreach (var (id, wolf) in conn.Monsters)
            {
                if (wolf is not { Summon: not null, Dead: false, Life: > 0, Attack: not null } || !SummonCanActLocked(conn, wolf)) continue;
                uint life = wolf.Leech.Recover(wolf.LastLeechAt, now, wolf.MaxLife, wolf.Life);
                wolf.LastLeechAt = now;
                if (wolf.Life != life)
                {
                    wolf.Life = life;
                    SummonsOf(conn).SetLife(conn.SummonLease, wolf.Summon.Id, life);
                    recovery.Add((id, wolf));
                }
                if (now < wolf.BusyUntil) continue;
                var from = wolf.Walk.Position(now);
                var owner = conn.Walk.Position(now);
                var target = conn.Monsters.Where(kv => kv.Value is { Hostile: true, Dead: false, Life: > 0 }
                    && Dist(owner, kv.Value.Walk.Position(now)) <= wolf.AggroRange)
                    .OrderBy(kv => Dist(from, kv.Value.Walk.Position(now))).FirstOrDefault();
                var to = target.Value is null ? owner : target.Value.Walk.Position(now);
                double range = target.Value is null ? wolf.StopRange : wolf.Attack.Range;
                if (target.Value is not null && Dist(from, to) <= range)
                {
                    wolf.BusyUntil = now + wolf.Attack.Seconds;
                    wolf.Walk.Order(from.X, from.Y, now);
                    wolf.LastTarget = null;
                    swings.Add((id, wolf, target.Key, target.Value, from));
                }
                else if (Dist(from, to) > range && (wolf.LastTarget is not { } last || Dist(last, to) >= 3))
                {
                    wolf.LastTarget = to;
                    orders.Add((id, wolf.Walk.Order(to.X, to.Y, now), to));
                }
            }
        }
        foreach (var order in orders)
            await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(order.Id, order.From.X, order.From.Y,
                order.To.X, order.To.Y, 0x2909, 0xffff, 0x0408), "owned wolf moves");
        foreach (var heal in recovery)
            await SendMonsterLife(remote, stream, ciphers, conn, heal.Id, heal.Wolf, "owned wolf leeches life");
        foreach (var swing in swings)
            _ = CreatureSwing(remote, stream, ciphers, conn, swing.Id, swing.Minion, swing.TargetId, swing.Target, swing.From);
    }

    static async Task CreatureSwing(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint id, SpawnedMonster attacker, uint targetId, SpawnedMonster target, (int X, int Y) from)
    {
        var attack = attacker.Attack!;
        var to = target.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y,
            attack.SkillId, 0xffff, 0x0408, targetObjectId: targetId), $"creature 0x{id:x} attacks 0x{targetId:x}");
        double contactAt = conn.Clock.Elapsed.TotalSeconds + attack.Seconds / 2;
        int epoch;
        lock (conn.Monsters) epoch = conn.ProjectileEpoch;
        if (!await WaitProjectileTime(conn, contactAt, epoch)) return;
        while (!conn.Closed)
        {
            await WhilePaused(conn);
            MonsterHit? applied;
            int damage;
            lock (conn.Monsters)
            {
                if (conn.Paused) continue;
                if (conn.Clock.Elapsed.TotalSeconds < contactAt) continue;
                if (conn.Closed || conn.LeavingArea || attacker.Dead || target.Dead || conn.PlayerLife == 0
                    || !conn.Monsters.ContainsKey(id) || !conn.Monsters.ContainsKey(targetId)
                    || !SummonCanActLocked(conn, attacker) || !SummonCanActLocked(conn, target)) return;
                double now = conn.Clock.Elapsed.TotalSeconds;
                if (Dist(attacker.Walk.Position(now), target.Walk.Position(now)) > attack.Range + 2) return;
                var hit = attack.Damage.Roll(Random.Shared);
                if (attacker.Summon is not null && attacker.Variety is { } variety)
                {
                    // Character.ot supplies minion_global_always_hit=1 in this client. The owner's own accuracy and
                    // ordinary attack evasion rolls do not transfer to its minions (PoB CalcPerform's minion setup).
                    var profile = WolfProfile(conn, variety, attacker.Level);
                    hit = profile.Attack.Damage.Roll(Random.Shared);
                    if (Random.Shared.Next(10000) < profile.Chance) hit = hit.Critical(profile.Multiplier);
                    hit = ApplyPassiveDamageTaken(conn, target, DamageModel.Mitigate(hit, type => MonsterResistance(conn, target, type)));
                    attacker.Leech.Add(now, attacker.MaxLife, attacker.Life, hit[DamageType.Physical].Min);
                }
                else if (target.Summon is not null && target.Variety is { } minionType)
                {
                    int chance = DamageModel.AccuracyHitChance(Monsters.BaseEvasion(target.Level) ?? 0,
                        Monsters.BaseAccuracy(attacker.Level) ?? 0);
                    if (target.EvasionEntropy < 0 || now - target.LastEvasionAt > 3.33)
                        target.EvasionEntropy = Random.Shared.Next(100);
                    target.LastEvasionAt = now;
                    target.EvasionEntropy += chance;
                    if (target.EvasionEntropy < 100) return;
                    target.EvasionEntropy -= 100;
                    int conversion = PassivePresenceStat(conn, attacker, "base_physical_damage_%_to_convert_to_fire");
                    if (conversion != 0)
                        hit = hit.Convert(new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, conversion));
                    hit = DamageModel.Mitigate(hit, type => new(Monsters.BaseResistance(minionType, target.Level, type) ?? 0));
                    hit = hit.Select((type, range) => type == DamageType.Physical
                        ? range.Scale(1 - Math.Min(.75, (Monsters.BaseArmour(target.Level) ?? 0)
                            / Math.Max(1, (Monsters.BaseArmour(target.Level) ?? 0) + 5 * range.Min))) : range);
                }
                damage = Math.Max(0, (int)Math.Round(hit.MinTotal));
                applied = ApplyMonsterHitLocked(conn, target, damage, playerHit: false);
                if (target.Summon is { } hurt)
                    SummonsOf(conn).SetLife(conn.SummonLease, hurt.Id, target.Life);
            }
            if (applied is { } result)
                await NotifyMonsterHit(remote, stream, ciphers, conn, targetId, target, result, damage,
                    $"owned summon 0x{id:x}", playerKill: false);
            return;
        }
    }
}
