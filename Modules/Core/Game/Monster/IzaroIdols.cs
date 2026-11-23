using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// An idol of Izaro's Chrono Statues (IzaroChronos) while it is up: it channels ChronosStatuesCast while its life fills
/// (the client poses it and lights its glow by that life), then casts its circles, which burst <see cref="IzaroChronos.FuseSeconds"/>
/// later, and starts again from 1 life. It cannot die: its life stops at 1 % (LifePhaseGate).
/// </summary>
static class IzaroIdols
{
    // The idol's actor skill for its channel (the official monsters' ids start at 0x4000, KingInMists.Kit).
    const ushort ChannelSkillId = 0x4000;

    // How often its life is filled and sent.
    const double TickSeconds = 0.2;

    public static bool IsIdol(MonsterCatalog.Variety? variety) => variety is not null && IzaroChronos.IsIdol(variety.Id);

    // Its effects: the beacons (its first, the actor's default) and the channel (ChronosStatuesCast).
    static int ChannelEffect(MonsterCatalog.Variety variety) => variety.GrantedEffects is [_, int channel, ..] ? channel : -1;

    /// <summary>The actor's skill list: its default skill, then the channel, with the 0x1ab id it names.</summary>
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(Player player, MonsterCatalog.Variety variety, byte level, MonsterAttack attack, GrantedSkill? skill)
    {
        if (!IsIdol(variety) || skill is null || ChannelEffect(variety) is not (>= 0 and var effect)) return null;
        var catalog = Game.SkillsFor(player);
        // A spell's cast-time stats: the client's cast-duration path crashed on an actor record with only attack stats
        // (Monsters.cs, the King's Death Effigy).
        var stats = (attack with { SkillId = ChannelSkillId, Spell = true, Seconds = catalog.CastTimeMs(effect) / 1000.0 }).SkillStats();
        return catalog.FindByRow(ChannelSkillId, effect, catalog.MonsterEffectLevel(effect, level), stats) is { } channel ? [skill, channel] : null;
    }

    /// <summary>Its fill and cast, until <paramref name="up"/> says it is no longer up.</summary>
    internal static async Task Run(Player player, uint id, SpawnedMonster idol,
        int element, double fillSeconds, Func<bool> up)
    {
        if (idol.Attack is not { } plain) return;
        // beacon_izaro's damage is its FloatStatsValues (0.8 to 1.2) of the monster damage at the idol's level
        // (StatInterpolations 3), of its element; the row's BaseResolvedValues (14 to 22) are its level 3's (live: no damage
        // seen with them).
        double baseDamage = Monsters.BaseDamage(idol.Level) ?? 0;
        var type = element switch { 0 => DamageType.Fire, 1 => DamageType.Cold, _ => DamageType.Lightning };
        var burst = plain with
        {
            Damage = DamagePacket.Of(type, baseDamage * IzaroChronos.DamageLow, baseDamage * IzaroChronos.DamageHigh),
            Spell = true, Scripted = true, Projectile = null, Release = null,
        };
        try
        {
            while (up())
            {
                int action;
                uint life;
                (int X, int Y) at;
                action = ++idol.ActionGeneration;
                idol.Life = life = Math.Min(idol.Life, 1u);
                at = idol.Walk.Position(AreaTime(player));
                if (!await SendMonsterAction(player, id, idol, action, () =>
                    InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, ChannelSkillId, player.NextActionSequence(), player.MonsterOrderFlags),
                    $"0x01ab idol 0x{id:x} fills")) return;
                // Its life fills; hits take it down meanwhile.
                while (true)
                {
                    await AreaDelay(player, TickSeconds);
                    if (!up()) return;
                    bool full;
                    if (idol.Dead) return;
                    idol.Life = life = (uint)Math.Min(idol.MaxLife, idol.Life + Math.Ceiling(idol.MaxLife * TickSeconds / fillSeconds));
                    full = life >= idol.MaxLife;
                    if (full) break;
                }
                // Full: it casts its circles (its act's SpellAreaOfEffect: Deactivate_cast and the gold nova) and its life goes to 1.
                List<(int X, int Y)> spots;
                idol.Life = 1;
                spots = Spots(player);
                await Send(player, [.. InstanceProtocol.ActionEnded(id), .. InstanceProtocol.ActorAnimation(id, 1, SummonAnimation)], $"idol 0x{id:x} casts {spots.Count} circles");
                _ = Circles(player, id, idol, burst, element, spots);
                await AreaDelay(player, IzaroChronos.SitSeconds);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!player.Closed) Console.WriteLine($"[gateway] {player.Connection.Remote} idol 0x{id:x} stopped: {ServerLog.Describe(ex)}"); }
    }

    // Where its circles go: one on the player, the rest at walkable cells round the player within the placement radius,
    // apart from each other. In the area's frame.
    static List<(int X, int Y)> Spots(Player player)
    {
        var playerAt = player.Walk.Position(AreaTime(player));
        var spots = new List<(int X, int Y)> { playerAt };
        for (int tries = 0; spots.Count < IzaroChronos.Circles && tries < 200; tries++)
        {
            double angle = AreaRng(player).NextDouble() * Math.PI * 2, reach = Math.Sqrt(AreaRng(player).NextDouble()) * IzaroChronos.PlacementRadius;
            (int X, int Y) cell = ((int)Math.Round(playerAt.X + Math.Cos(angle) * reach), (int)Math.Round(playerAt.Y + Math.Sin(angle) * reach));
            if (!player.Layout.WalkableFor(cell.X, cell.Y, 0) || player.Barrier.Contains(cell)) continue;
            if (spots.Any(s => Distance(s, cell) < IzaroChronos.CircleSpacing)) continue;
            spots.Add(cell);
        }
        return spots;
    }

    static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    // The circles show for their fuse, each with its beacon's orb in the middle (beacon.ot: rig.ao, its start then its
    // loop; at the end its end clip), then burst: the player inside one takes its hit (one hit, however many).
    static async Task Circles(Player player, uint id, SpawnedMonster idol,
        MonsterAttack burst, int element, List<(int X, int Y)> spots)
    {
        var layout = player.Layout;
        bool Gone() => player.Closed || player.LeavingArea || !ReferenceEquals(player.Layout, layout);
        try
        {
            var circles = new List<uint>();
            var orbs = new List<uint>();
            List<byte> show = [];
            foreach (var s in spots)
            {
                uint effect = player.NextObjectId++, orb = player.NextObjectId++;
                circles.Add(effect);
                orbs.Add(orb);
                show.AddRange([.. InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(s.X, s.Y, IzaroChronos.CircleArt(element)).Blob()), .. InstanceProtocol.EnterWakeZone(effect),
                    .. InstanceEntities.CreateObject(orb, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(s.X, s.Y, IzaroChronos.OrbArt(element)).Blob()), .. InstanceProtocol.EnterWakeZone(orb)]);
            }
            await Send(player, [.. show], null);
            await AreaDelay(player, IzaroChronos.OrbStartSeconds);
            if (Gone()) return;
            await Send(player, [.. orbs.SelectMany(o => InstanceProtocol.AnimationUpdate(o, 0, IzaroChronos.OrbLoopClip, 1))], null);
            await AreaDelay(player, IzaroChronos.FuseSeconds - IzaroChronos.OrbStartSeconds);
            if (Gone()) return;
            var bursts = new List<uint>();
            List<byte> burstBytes = [.. circles.SelectMany(InstanceProtocol.RemoveObject),
                .. orbs.SelectMany(o => InstanceProtocol.AnimationUpdate(o, 0, IzaroChronos.OrbEndClip, 1))];
            foreach (var s in spots)
            {
                uint effect = player.NextObjectId++;
                bursts.Add(effect);
                burstBytes.AddRange([.. InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(s.X, s.Y, IzaroChronos.BurstArt(element)).Blob()), .. InstanceProtocol.EnterWakeZone(effect)]);
            }
            await Send(player, [.. burstBytes], null);
            MonsterHitOutcome? outcome = null;
            await Send(player, [], $"idol 0x{id:x}'s circles burst", atGate: () =>
            {
                if (InGracePeriod(player) || !MonsterPresent(player, id, idol)) return null;
                double now = Math.Max(ProjectileWorldTime(player), AreaTime(player));
                var here = player.Walk.Position(now);
                var nearest = spots.MinBy(s => Distance(s, here));
                if (Distance(nearest, here) > IzaroChronos.CircleRadius) return null;
                outcome = CommitMonsterHitLocked(player, id, idol, burst, now, here, nearest, checkRange: false, sequence: player.NextActionSequence());
                return outcome?.HitPacket;
            });
            if (outcome is not null) await PresentMonsterHit(player, id, outcome);
            await AreaDelay(player, IzaroChronos.ExplodeSeconds);
            if (!Gone()) await Send(player, [.. bursts.Concat(orbs).SelectMany(InstanceProtocol.RemoveObject)], null);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!player.Closed) Console.WriteLine($"[gateway] {player.Connection.Remote} idol 0x{id:x}'s circles stopped: {ServerLog.Describe(ex)}"); }
    }
}
