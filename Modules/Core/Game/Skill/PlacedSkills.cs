using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    static readonly Lazy<IReadOnlyDictionary<int, string>> TrapVisuals = new(() =>
    {
        using var rows = GameTables.Open(Path.Combine("data", "tables", "SkillTrapVariations.json"));
        return rows.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("Id").GetInt32(), r => r.GetProperty("Metadata").GetString()!);
    });
    internal static (bool Trap, bool Mine) PlacedKind(SupportedGem gem)
    {
        var types = (Supports.Of(gem.Effect)?.Types ?? []).Concat(gem.Supports.SelectMany(s => s.Effect.Added)).ToHashSet();
        return (types.Contains(32), types.Contains(35));
    }

    static async Task<bool> PlaceSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skill, (int X, int Y) aim, int recovery, Func<bool> current)
    {
        var gem = SupportedSkill(conn, skill); var kind = PlacedKind(gem);
        var player = CharacterPlayerStats(conn);
        int Get(string n) => GolemStat(gem.Stats, n) + GolemStat(player, n);
        double release = AreaTime(conn) + Math.Max(.05, (CastMs(conn, gem.Effect, skill) ?? 500) / 1000d);
        var lifetime = CombatLifetime;
        while (true)
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || conn.Closed || conn.LeavingArea
                    || conn.PlayerLife == 0 || recovery != conn.ActionInterruptionGeneration) return false;
                if (!conn.Paused && AreaTime(conn) >= release) break;
            }
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        await Send(remote, stream, ciphers, [], "place armed trap/mine", atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!current() || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || !conn.Layout.WalkableFor(aim.X, aim.Y, 0)) return null;
                var context = ReservationContext(conn, skill);
                var reserve = kind.Mine ? SupportedReservation(conn, skill) : null;
                uint id = conn.NextObjectId++;
                double now = AreaTime(conn);
                var placed = new PlacedSkills.Placed(id, skill, gem.Effect, aim, kind.Mine, now + .5,
                    now + Math.Max(0, Get(kind.Mine ? "base_mine_duration" : "base_trap_duration") is > 0 and var ms ? ms / 1000d : kind.Mine ? 5 : 4)
                        * Math.Max(.01, 1 + Get(kind.Mine ? "mine_duration_+%" : "trap_duration_+%") / 100d),
                    SupportedHitFor(conn, skill, suppress: false), new Dictionary<uint, int>(gem.Stats),
                    reserve?.Flat ?? 0, reserve?.Percent ?? 0, context.Life, context.Efficiency);
                conn.Placed.All.Add(id, placed);
                if (!ReservationBudgetFits(conn)) { conn.Placed.All.Remove(id); return null; }
                var bytes = new List<byte>();
                int limit = Math.Max(1, (kind.Mine ? 15 : 15) + Get(kind.Mine ? "number_of_additional_remote_mines_allowed" : "number_of_additional_traps_allowed"));
                while (conn.Placed.All.Values.Count(p => p.Mine == kind.Mine) > limit)
                    RemovePlacedLocked(conn, conn.Placed.All.Values.Where(p => p.Mine == kind.Mine).MinBy(p => p.Armed)!, bytes);
                string path = kind.Mine ? "Metadata/MiscellaneousObjects/RemoteMine" : TrapVisuals.Value.GetValueOrDefault(Get("trap_variation"), "Metadata/MiscellaneousObjects/LightningTrap");
                var proxy = SkillsFor(conn).FindByRow(0x4000, gem.Effect, gem.Level, [.. gem.Stats.Select(p => (p.Key, p.Value))]);
                if (proxy is null) { conn.Placed.All.Remove(id); return null; }
                var stats = new List<(uint, int)>();
                foreach (var (name, value) in new[] { ("maximum_life", 1), ("is_trap", kind.Trap ? 1 : 0), ("is_mine", kind.Mine ? 1 : 0), ("parent_object_id", (int)PlayerId) })
                    if (Stats.Key(name) is uint key) stats.Add((key, value));
                bytes.AddRange(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(path), new TrapMineObject(aim.X, aim.Y, kind.Mine, [proxy], stats).Blob()));
                bytes.AddRange(InstanceProtocol.EnterWakeZone(id));
                conn.Placed.Dirty = true;
                ClampReservedResources(conn);
                bytes.AddRange(PlacedResourcesLocked(conn));
                return [.. bytes];
            }
        });
        return true;
    }

    static void RemovePlacedLocked(ClientState conn, PlacedSkills.Placed placed, List<byte> bytes)
    {
        if (!conn.Placed.All.Remove(placed.Id)) return;
        bytes.AddRange(InstanceProtocol.RemoveObject(placed.Id)); conn.Placed.Dirty = true;
    }

    static byte[] PlacedResourcesLocked(ClientState conn)
    {
        conn.Placed.Dirty = false;
        return [.. InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife, conn.PlayerReservedLife),
            .. InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMana, (uint)ReservedMana(conn), pool: ManaPool)];
    }

    internal static void DetonateMinesLocked(ClientState conn, double now)
    {
        conn.Placed.Chain = 0; conn.Placed.NextChain = now;
        foreach (var mine in conn.Placed.All.Values.Where(p => p.Mine && p.Armed <= now).OrderBy(p => p.Armed))
            mine.DetonateAt = now + .2 * conn.Placed.Chain++;
    }

    static void PlacedDamageLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        PlacedSkills.Placed placed, double now, List<byte> bytes)
    {
        var target = conn.Monsters.FirstOrDefault(p => p.Value is { Hostile: true, Dead: false, Life: > 0 }
            && Dist(p.Value.Walk.Position(now), placed.At) <= 60);
        var aim = target.Value?.Walk.Position(now) ?? placed.At;
        bytes.AddRange(InstanceProtocol.ActionStarted(placed.Id, placed.At.X, placed.At.Y, aim.X, aim.Y, 0x4000, 0xffff, 0x0408));
        double radius = SkillRadius(conn, placed.Own, spell: true, placed.Effect);
        double more = ReservationAura(conn, "SummonSkitterbots") is { } skitter
            ? Math.Max(0, 1 + ReservationStat(conn, skitter, "skitterbots_trap_mine_damage_+%_final") / 100d) : 1;
        var player = CharacterPlayerStats(conn);
        foreach (var (id, m) in conn.Monsters)
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false } || Dist(m.Walk.Position(now), placed.At) > radius) continue;
            var rolled = placed.Damage.Scale(more).Roll(Random.Shared);
            var mitigated = DamageModel.Mitigate(rolled, type => MonsterHitResistance(m, type, default, now));
            int damage = MonsterTakesMitigated(m, mitigated, true, now);
            if (ApplyMonsterHitLocked(conn, m, damage, allyAt: placed.At) is not { } hit) continue;
            if (hit.StageStarts) { CommitBossStage(remote, stream, ciphers, conn, id, m, now); hit = hit with { StageStarts = false }; }
            CommitAcceptedHitLocked(conn, id, m, hit, damage, now, "trap/mine", playerKill: false);
            if (!m.Dead)
                m.Ailments.Inflict(mitigated, false, m.MaxLife, now,
                    n => GolemStat(placed.Own, n) + GolemStat(player, n), n => m.Effects[n], Random.Shared);
        }
    }

    static async Task PlacedSkillsTick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double now)
    {
        await Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            using (ProjectileMutation(conn))
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused) return null;
                var bytes = new List<byte>();
                foreach (var placed in conn.Placed.All.Values.ToArray())
                {
                    if (conn.PlayerLife == 0 || now >= placed.Until) { RemovePlacedLocked(conn, placed, bytes); continue; }
                    if (now < placed.Armed) continue;
                    bool skitter = false;
                    if (!placed.SkitterTriggered)
                    {
                        var bot = conn.Reservations.Fighters.Values.Where(g => g.Definition.Kind.StartsWith("Skitter", StringComparison.Ordinal) && now >= g.NextOrb)
                            .MinBy(g => Dist(g.Monster.Walk.Position(now), placed.At));
                        if (bot is not null)
                        {
                            if (Dist(bot.Monster.Walk.Position(now), placed.At) > 8) GolemMoveLocked(conn, bot, placed.At, now);
                            else { skitter = true; bot.NextOrb = now + 3; }
                        }
                    }
                    bool nearby = !placed.Mine && conn.Monsters.Values.Any(m => m is { Hostile: true, Dead: false, Life: > 0 }
                        && Dist(m.Walk.Position(now), placed.At) <= 10);
                    if (!skitter && !nearby && now < placed.DetonateAt) continue;
                    PlacedDamageLocked(remote, stream, ciphers, conn, placed, now, bytes);
                    Console.WriteLine($"[gateway] placed skill 0x{placed.Id:x} {(skitter ? "Skitterbot" : nearby ? "proximity" : "chain")} trigger; rearm={skitter && placed.Mine && SkillsFor(conn).EffectId(placed.Effect) != "SmokeMine"}");
                    if (skitter && placed.Mine && SkillsFor(conn).EffectId(placed.Effect) != "SmokeMine")
                    {
                        placed.SkitterTriggered = true; placed.DetonateAt = double.PositiveInfinity;
                        conn.Placed.All[placed.Id] = placed with { Armed = now + .5 };
                    }
                    else RemovePlacedLocked(conn, placed, bytes);
                }
                if (conn.Placed.Dirty) bytes.AddRange(PlacedResourcesLocked(conn));
                return bytes.Count > 0 ? [.. bytes] : null;
            }
        });
    }
}
