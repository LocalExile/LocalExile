using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    // PoB other.lua EnemyExplode uses 22 cells. This separate profile has not had its retail radius/art measured;
    // the measured Crusader physical explosion's fixed 25-cell radius does not establish this mechanic's geometry.
    public const int MadnessExplosionBaseRadius = 22;
    const uint MadnessExplosionChance =
        Stat.ExplodeEnemiesFor25PctLifeAsChaosOnKillWhileAffectedByGloriousMadnessChancePct;

    static void CaptureMadnessExplosionLocked(Player player, SpawnedMonster corpse)
    {
        if (!corpse.Hostile || player.PlayerLife == 0 || !CurrentGameRules || !MadnessAt(player).Active) return;
        using var scope = new PlayerStatScope(player, fresh: true);
        var playerStats = new Dictionary<uint, int>(CharacterPlayerStats(player));
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        if (!RandomRolls.Chance(Get(MadnessExplosionChance), 100, player.Madness.Random)) return;
        var raw = DamagePacket.Of(DamageType.Chaos, corpse.MaxLife * .25, corpse.MaxLife * .25);
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            raw = raw.Add(type, new(Get(Stat.Of($"global_minimum_added_{slug}_damage")), Get(Stat.Of($"global_maximum_added_{slug}_damage"))));
        }
        corpse.MadnessExplosion = new(CombatLifetime!, player, playerStats, raw,
            SkillArea.Radius(MadnessExplosionBaseRadius, Get, spell: false), corpse.Walk.Position(ProjectileWorldTime(player)));
    }

    public static void QueueMadnessExplosionLocked(Player player, uint id, SpawnedMonster corpse)
    {
        var receipt = corpse.MadnessExplosion;
        corpse.MadnessExplosion = null;
        if (receipt is not null && ReferenceEquals(receipt.Killer, player)
            && ReferenceEquals(receipt.Owner, CombatLifetime) && CurrentGameRules
            && player.PlayerLife > 0 && !player.Closed && !player.LeavingArea
            && corpse.Hostile && corpse.Life == 0 && corpse.Variety is { } variety && !KingInMists.IsFetish(variety))
            player.Madness.Explosions.Enqueue((id, corpse, receipt));
    }

    public static void MadnessExplosionsLocked(Player player, double now)
    {
        if (!CurrentGameRules || player.Closed || player.LeavingArea || player.PlayerLife == 0 || player.Paused) return;
        // Chained explosions enqueue more primitive receipts. Iteration avoids a recursive kill/notification stack.
        while (player.Madness.Explosions.TryDequeue(out var pending))
        {
            var (corpseId, corpse, receipt) = pending;
            if (!ReferenceEquals(receipt.Owner, CombatLifetime) || !ReferenceEquals(receipt.Killer, player)
                || player.Area.Monsters.TryGetValue(corpseId, out var replacement) && !ReferenceEquals(replacement, corpse)) continue;
            var at = receipt.At;
            var playerStats = receipt.Player;
            int Get(uint key) => playerStats.GetValueOrDefault(key);
            var conversion = DamageStats.Conversion(playerStats, Stats, attack: null);
            ReservationDamageProfile Profile(long increased, int webs = 0, bool ignited = false, bool bleeding = false, bool lowLife = false)
            {
                int TargetStat(uint name) => TypedTargetDamageStat(Get, name, ignited, bleeding, lowLife, attack: false);
                increased += SpiderDamageIncrease(Get, webs);
                var woven = SpiderRawDamage(receipt.Raw, Get, webs);
                var hit = PlayerHitDamage.Resolve(woven, TargetStat, conversion, spell: false, area: true,
                    secondary: true, additionalIncreased: increased);
                var rows = AilmentPanelStats.Compute(new("secondary", false), woven, conversion, TargetStat, increased);
                return new(hit, MonsterIgnites.Settings.From(rows, "secondary", TargetStat), MonsterIgnites.Settings.None,
                    MonsterPoisons.Settings.From(rows, "secondary", TargetStat));
            }
            var normal = Profile(0);
            var blinded = Get(PlayerHitDamage.VsBlindedDamageStat) is var increased && increased != 0
                ? Profile(increased) : normal;
            var penetration = Penetration.Of(playerStats, Stats, attack: false);
            var resistanceMods = HitResistMods(playerStats);
            var gambler = GamblerFor(playerStats);
            bool previousSecondary = player.Reservations.Secondary;
            player.Reservations.Secondary = true;
            try
            {
                foreach (var (id, target) in player.Area.Monsters.ToArray())
                {
                    if (!target.Hostile || target.Dead || target.Life == 0 || target.Invulnerable || target.Asleep
                        || MonsterAsleep(player, id) || Dist(at, target.Walk.Position(now)) > receipt.Radius) continue;
                    var profile = target.SpiderWebs.Count(now) > 0 || target.Ignites.Burning || target.Bleeds.Burning
                        || EnemyOnLowLife(target) && Get(DamageVsLowLifePerFrenzy) != 0
                        ? Profile(Blinded(player, id) ? Get(PlayerHitDamage.VsBlindedDamageStat) : 0,
                            target.SpiderWebs.Count(now), target.Ignites.Burning, target.Bleeds.Burning, EnemyOnLowLife(target))
                        : Blinded(player, id) ? blinded : normal;
                    int critChance = Get(Stat.GlobalCannotCrit) != 0 ? 0
                        : CriticalChanceFinal.Project(Get(AddedBaseCriticalStrike.Generic), Get(Stat.CriticalStrikeChancePlusPct), Get, _ => 0);
                    bool critical = RandomRolls.Chance(critChance, 10000, player.Madness.Random);
                    var rolled = GameEvents.WithOutgoingDamage(player, playerStats, target,
                        gambler.RollOutgoingHit(profile.Hit, player.Madness.Random));
                    var strike = new HitResistances.Strike(Critical: critical, Attack: false);
                    bool invert = gambler.InvertsResistance(player.Madness.Random);
                    var mitigated = rolled.Select((type, range) => range.Scale(Math.Max(0, 1 - HitResistances.FinalValue(
                        type, MonsterHitResistance(target, type, penetration, now), strike, resistanceMods, invert) / 100)));
                    int damage = MonsterTakesMitigated(target, mitigated, spell: false, now);
                    if (critical) damage = DamageModel.CriticalDamage(damage,
                        CriticalMultiplierOn(target, CriticalHitMultiplier(150L + Get(Stat.BaseCriticalStrikeMultiplierPlus),
                            playerStats), playerStats));
                    if (ApplyTypedMonsterHitLocked(player, target, damage, MonsterHitChaosDamage(target, mitigated, false, now, damage), playerStats: playerStats) is not { } hit) continue;
                    if (hit.StageStarts)
                    {
                        CommitBossStage(player, id, target, now);
                        hit = hit with { StageStarts = false };
                    }
                    AilmentsOnHitLocked(player, id, target, mitigated, false, critical, 100, playerStats, null, now,
                        player.Madness.Random, poison: profile.Poison, blind: BlindHitInput.Capture(playerStats, null));
                    if (target.Life > 0 && (critical ? profile.Ignite.CriticalMax : profile.Ignite.Max) > 0
                        && IgniteOnHitLocked(player, id, target, profile.Ignite, critical, now) is { } ignite)
                        foreach (var packet in ignite) player.Minions.OutputQueue.Add(new(packet, null, id, target, WorldId: id));
                    // The ordinary accepted-hit path owns rewards, boss phases and further on-kill receipts.
                    CommitAcceptedHitLocked(player, id, target, hit, damage, now, "Madness chaos explosion", playerKill: true);
                }
            }
            finally { player.Reservations.Secondary = previousSecondary; }
            corpse.CorpseConsumed = true;
            player.Area.Monsters.Remove(corpseId);
            player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(corpseId), "Madness explosion consumes corpse",
                TargetId: corpseId, Kind: PlayerMinions.OutputKind.Remove));
        }
    }
}
