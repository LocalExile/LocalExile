using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const ushort FortificationBuffRow = 1943;
    static readonly RuleTable<GolemState.Golem, FortificationSession> MinionFortification = new();

    static void MinionFortifyOnHitLocked(Player player, GolemState.Golem fighter, SpawnedMonster target, DamagePacket rolled,
        IReadOnlyDictionary<uint, int> skill, double now, Random random)
    {
        var effects = fighter.Monster.Effects;
        int Get(uint stat) => effects[stat] + GolemStat(skill, stat);
        if (Get(FortificationSession.OnMeleeHit) == 0 && Get(FortificationSession.ChanceOnMeleeHit) <= 0
            && Get(FortificationSession.DirectMeleeHitDurationMilliseconds) <= 0) return;
        var session = MinionFortification.GetOrCreateValue(fighter);
        if (session.Hit(now, RawFortificationDamage(rolled, critical: false, 100), target.MaxLife, target.Rarity, Get, random))
            MinionFortificationAt(player, fighter, now);
    }

    static double MinionFortificationHitFactor(Player player, GolemState.Golem fighter, double now)
    {
        int shared = MinionFortificationStacksLocked(player, fighter, fighter.Monster.Walk.Position(now), now);
        return Math.Min(MinionFortificationAt(player, fighter, now), Math.Max(0, 1 - shared / 100d));
    }

    static double MinionFortificationAt(Player player, GolemState.Golem fighter, double now)
    {
        if (!MinionFortification.TryGetValue(fighter, out var session)) return 1;
        var value = session.Read(now);
        if (session.PresentedRevision != session.Revision)
        {
            if (value.Current > 0 && value.LatestExpiry is double expires)
                player.Area.Buffs.Set(fighter.Id, new(FortificationBuffRow, fighter.Id,
                    U68: Buff.Seconds(expires - now), Stacks: (ushort)Math.Min(ushort.MaxValue, value.Current)));
            else player.Area.Buffs.Clear(fighter.Id, FortificationBuffRow, fighter.Id);
            SetStats(player, fighter.Id, [(Stat.CurrentFortification, value.Current), (Stat.HasFortification, value.Current > 0 ? 1 : 0)]);
            session.PresentedRevision = session.Revision;
        }
        return value.HitDamageFactor;
    }
}
