using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

static partial class Game
{
    // PoB other.lua EnemyExplode uses 22 cells. This separate profile has not had its retail radius/art measured;
    // the measured Crusader physical explosion's fixed 25-cell radius does not establish this mechanic's geometry.
    internal const int MadnessExplosionBaseRadius = 22;
    const string MadnessExplosionChance =
        "explode_enemies_for_25%_life_as_chaos_on_kill_while_affected_by_glorious_madness_chance_%";

    static void CaptureMadnessExplosionLocked(ClientState conn, SpawnedMonster corpse)
    {
        if (!corpse.Hostile || conn.PlayerLife == 0 || !CurrentGameRules || !MadnessAt(conn).Active) return;
        using var scope = new PlayerStatScope(conn, fresh: true);
        var player = new Dictionary<uint, int>(CharacterPlayerStats(conn));
        int Get(string n) => Stats.Key(n) is uint key ? player.GetValueOrDefault(key) : 0;
        if (!RandomRolls.Chance(Get(MadnessExplosionChance), 100, conn.Madness.Random)) return;
        var raw = DamagePacket.Of(DamageType.Chaos, corpse.MaxLife * .25, corpse.MaxLife * .25);
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            raw = raw.Add(type, new(Get($"global_minimum_added_{slug}_damage"), Get($"global_maximum_added_{slug}_damage")));
        }
        corpse.MadnessExplosion = new(CombatLifetime!, conn, player, raw,
            SkillArea.Radius(MadnessExplosionBaseRadius, Get, spell: false), corpse.Walk.Position(ProjectileWorldTime(conn)));
    }

    internal static void QueueMadnessExplosionLocked(ClientState conn, uint id, SpawnedMonster corpse)
    {
        var receipt = corpse.MadnessExplosion;
        corpse.MadnessExplosion = null;
        if (receipt is not null && ReferenceEquals(receipt.Killer, conn)
            && ReferenceEquals(receipt.Owner, CombatLifetime) && CurrentGameRules
            && conn.PlayerLife > 0 && !conn.Closed && !conn.LeavingArea
            && corpse.Hostile && corpse.Life == 0 && corpse.Variety is { } variety && !KingInMists.IsFetish(variety))
            conn.Madness.Explosions.Enqueue((id, corpse, receipt));
    }

    internal static void MadnessExplosionsLocked(ClientState conn, double now)
    {
        if (!CurrentGameRules || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || conn.Paused) return;
        // Chained explosions enqueue more primitive receipts. Iteration avoids a recursive kill/notification stack.
        while (conn.Madness.Explosions.TryDequeue(out var pending))
        {
            var (corpseId, corpse, receipt) = pending;
            if (!ReferenceEquals(receipt.Owner, CombatLifetime) || !ReferenceEquals(receipt.Killer, conn)
                || conn.Monsters.TryGetValue(corpseId, out var replacement) && !ReferenceEquals(replacement, corpse)) continue;
            var at = receipt.At;
            var player = receipt.Player;
            int Get(string n) => Stats.Key(n) is uint key ? player.GetValueOrDefault(key) : 0;
            var conversion = DamageStats.Conversion(player, Stats, attack: null);
            ReservationDamageProfile Profile(long increased)
            {
                var hit = PlayerHitDamage.Resolve(receipt.Raw, Get, conversion, spell: false, area: true,
                    secondary: true, additionalIncreased: increased);
                var rows = AilmentPanelStats.Compute(new("secondary", false), receipt.Raw, conversion, Get, increased);
                return new(hit, MonsterIgnites.Settings.From(rows, "secondary", Get), MonsterIgnites.Settings.None,
                    MonsterPoisons.Settings.From(rows, "secondary", Get));
            }
            var normal = Profile(0);
            var blinded = Get(PlayerHitDamage.VsBlindedDamageStat) is var increased && increased != 0
                ? Profile(increased) : normal;
            var penetration = Penetration.Of(player, Stats, attack: false);
            var resistanceMods = HitResistMods(player);
            var gambler = GamblerFor(player);
            bool previousSecondary = conn.Reservations.Secondary;
            conn.Reservations.Secondary = true;
            try
            {
                foreach (var (id, target) in conn.Monsters.ToArray())
                {
                    if (!target.Hostile || target.Dead || target.Life == 0 || target.Invulnerable || target.Asleep
                        || conn.AsleepMonsters.Contains(id) || Dist(at, target.Walk.Position(now)) > receipt.Radius) continue;
                    var profile = Blinded(conn, id) ? blinded : normal;
                    int critChance = Get("global_cannot_crit") != 0 ? 0
                        : CriticalChanceFinal.Project(Get(AddedBaseCriticalStrike.Generic), Get("critical_strike_chance_+%"), Get, _ => 0);
                    bool critical = RandomRolls.Chance(critChance, 10000, conn.Madness.Random);
                    var rolled = GameEvents.WithOutgoingDamage(conn, player, target,
                        gambler.RollOutgoingHit(profile.Hit, conn.Madness.Random));
                    var strike = new HitResistances.Strike(Critical: critical, Attack: false);
                    bool invert = gambler.InvertsResistance(conn.Madness.Random);
                    var mitigated = rolled.Select((type, range) => range.Scale(Math.Max(0, 1 - HitResistances.FinalValue(
                        type, MonsterHitResistance(target, type, penetration, now), strike, resistanceMods, invert) / 100)));
                    int damage = MonsterTakesMitigated(target, mitigated, spell: false, now);
                    if (critical) damage = DamageModel.CriticalDamage(damage,
                        CriticalMultiplierOn(target, 150 + Get("base_critical_strike_multiplier_+")));
                    if (ApplyMonsterHitLocked(conn, target, damage, player: player) is not { } hit) continue;
                    if (hit.StageStarts)
                    {
                        var wire = conn.Wire;
                        CommitBossStage(wire.Remote, wire.Stream, wire.Ciphers, conn, id, target, now);
                        hit = hit with { StageStarts = false };
                    }
                    AilmentsOnHitLocked(conn, id, target, mitigated, false, critical, 100, player, null, now,
                        conn.Madness.Random, poison: profile.Poison, blind: BlindHitInput.Capture(player, null));
                    if (target.Life > 0 && (critical ? profile.Ignite.CriticalMax : profile.Ignite.Max) > 0
                        && IgniteOnHitLocked(conn, id, target, profile.Ignite, critical, now) is { } ignite)
                        foreach (var packet in ignite) conn.Minions.OutputQueue.Add(new(packet, null, id, target));
                    // The ordinary accepted-hit path owns rewards, boss phases and further on-kill receipts.
                    CommitAcceptedHitLocked(conn, id, target, hit, damage, now, "Madness chaos explosion", playerKill: true);
                }
            }
            finally { conn.Reservations.Secondary = previousSecondary; }
            corpse.CorpseConsumed = true;
            conn.Monsters.Remove(corpseId);
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(corpseId), "Madness explosion consumes corpse",
                TargetId: corpseId, Kind: PlayerMinions.OutputKind.Remove));
        }
    }
}
