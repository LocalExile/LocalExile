using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string VaalAncestralWarchiefEffect = "VaalAncestralWarchief";

    const ushort VaalWarchiefBuff = 673; // slam_ancestor_totem_melee_damage
    sealed record VaalWarchiefBuffState(int Bonus);
    static readonly RuleTable<Player, VaalWarchiefBuffState> SentVaalWarchiefBuff = new();

    static int VaalWarchiefActivationRange(TotemState.Totem totem) =>
        TotemSources.TryGetValue(totem, out var source)
            && Skills.EffectId(totem.Effect) == VaalAncestralWarchiefEffect
            ? GolemStat(source.Stats, Stat.AncestorTotemParentActiviationRange) : 0;

    static bool VaalWarchiefInRange(Player player, TotemState.Totem totem, double now) =>
        VaalWarchiefActivationRange(totem) is > 0 and var range
            && Dist(totem.At, player.Walk.Position(now)) <= range;

    static int VaalWarchiefMeleeBonus(Player player, double now)
    {
        if (player.PlayerLife == 0) return 0;
        int best = 0;
        foreach (var totem in player.Totems.All)
        {
            if (!totem.Alive(now) || !InZone(player, player.Id, TotemAuraZones, totem.Id, player.Id)
                || VaalWarchiefActivationRange(totem) == 0 || !TotemSources.TryGetValue(totem, out var source)) continue;
            best = Math.Max(best, GolemStat(source.Stats, Stat.SlamAncestorTotemGrantOwnerMeleeDamagePlusPctFinal));
            StatsChangeIn(totem.Until - now);
        }
        return best;
    }

    static void SyncVaalWarchiefBuff(Player player, double now, WorldRecordBuilder bytes)
    {
        int bonus = VaalWarchiefMeleeBonus(player, now);
        if (SentVaalWarchiefBuff.TryGetValue(player, out var sent) && sent.Bonus == bonus) return;
        if (bonus > 0)
        {
            SentVaalWarchiefBuff.AddOrUpdate(player, new(bonus));
            player.Area.Buffs.Set(player.Id, BuffRows.Spec(VaalWarchiefBuff, player.Id,
                new Dictionary<uint, int> { [Stat.SlamAncestorTotemGrantedMeleeDamagePlusPctFinal] = bonus }));
        }
        else
        {
            bool wasSent = SentVaalWarchiefBuff.Remove(player);
            if (!wasSent && !player.Area.Buffs.Has(player.Id, VaalWarchiefBuff, player.Id)) return;
            player.Area.Buffs.Clear(player.Id, VaalWarchiefBuff, player.Id);
        }
        MarkPlayerStats(player, PlayerStatStep.TotemAura);
        bytes.World(player.Id, PlayerStatsOffFrames(player));
    }

    static GameServer.Core.World.DamagePacket WithVaalWarchiefBuff(GameServer.Core.World.DamagePacket hit, Func<uint, int> get)
    {
        int bonus = get(Stat.SlamAncestorTotemGrantedMeleeDamagePlusPctFinal);
        return bonus == 0 ? hit : hit.Scale(1 + bonus / 100d);
    }

    static void ReleaseVaalWarchiefSlam(Player player, ProjectileSession session, TotemState.Totem totem,
        (int X, int Y) aim, ushort sequence)
    {
        double now = ProjectileWorldTime(player);
        if (!VaalWarchiefInRange(player, totem, now)) return;
        var skill = SupportedSkill(player, totem.SkillId);
        int radius = SkillRadius(player, skill.Stats, spell: false, totem.Effect);
        var from = totem.At;
        var direction = GroundSlamWave.Direction(from, aim);
        // The exported stats give the attack's area and target range. Use the strike radius
        // as the leap threshold and the ordinary slam's cone to approximate the cascade.
        double reach = Math.Max(radius, Math.Min(TotemTargetRange(player, totem.SkillId), GroundSlamWave.Length(radius)));
        if (Dist(from, aim) > radius)
        {
            int clearance = Math.Max(2, totem.BodySize + 2);
            var landing = (X: (int)Math.Round(aim.X - direction.X * clearance),
                Y: (int)Math.Round(aim.Y - direction.Y * clearance));
            if (player.Layout.WalkableFor(landing.X, landing.Y, totem.BodySize) && !player.Barrier.Contains(landing))
            {
                totem.Relocate(landing);
                player.SetObjectBlocker(totem.Id, landing.X, landing.Y, totem.BodySize, true);
                session.World(totem.Id, InstanceProtocol.Teleport(totem.Id, landing.X, landing.Y));
            }
        }

        var centre = totem.At;
        double angle = GroundSlamWave.HalfAngle(skill.Stats.GetValueOrDefault);
        TotemNovaLocked(player, session, totem, centre, reach, sequence, covers: at =>
            Dist(centre, at) <= radius || GroundSlamWave.Covers(direction, centre, at, angle, reach));
    }
}
