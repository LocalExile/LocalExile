using GameServer.Core.World;
using static Game;
using static Program;

public static partial class MapStrongboxes
{
    const string FissureTrigger = "Metadata/Monsters/Daemon/ChestDaemonVaalTriggerVolcanicFissure_";
    const string FissureCaster = "Metadata/Monsters/Daemon/ChestDaemonVaalUseVolcanicFissure";
    const string FissureEffect = "ChestVaalVolcanicFissure";

    public sealed record FissurePlayer(uint Peer, int Epoch);
    public sealed record VaalFissure(long Started, string Daemon, int AimX, int AimY, int EndX, int EndY) : VesselCast(Started, Daemon)
    {
        public HashSet<FissurePlayer> Swept { get; init; } = [];
        internal override (int X, int Y) Target(Box box) => (AimX, AimY);
        internal (int X, int Y) End => (EndX, EndY);
        internal double Length(Box box) => Dist((box.X, box.Y), End);
        internal int Samples(Box box) => 1 + (int)Math.Ceiling(Length(box));
    }

    sealed record FissureSkill(VesselSkill Slam, int Radius, long ReleaseTicks)
    {
        public VesselImpact Ground { get; } = new(Slam.ActionRecord, Slam.ActionRecord.Damage);
        public long Sample(VaalFissure cast, Box box, int step) => cast.Started + ReleaseTicks
            + TimeSpan.FromSeconds(GroundSlamWave.AdvanceSeconds(Math.Min(step, cast.Length(box)))).Ticks;
    }

    static FissureSkill ReadFissure(ClientState c)
    {
        var catalog = SkillsFor(c);
        var variety = Monsters.Find(FissureCaster)!;
        int effect = catalog.EffectRow(FissureEffect);
        byte level = (byte)AreaLevel(c);
        var native = GolemData.Skill(catalog, effect, level);
        int Get(string id) => Stats.Key(id) is uint key ? native.Stats.GetValueOrDefault(key) : 0;
        var (min, max) = Monsters.BaseDamage(variety, level)!.Value;
        var conversion = new DamageConversion();
        conversion.AddSkill(DamageType.Physical, DamageType.Fire, native.Stats[Stats.Key("skill_physical_damage_%_to_convert_to_fire")!.Value]);
        var damage = DamagePacket.Of(DamageType.Physical, min, max).Scale(native.Multiplier).Convert(conversion);
        double seconds = variety.AttackMs / (1000d * (1 + catalog.AttackSpeedMultiplier(effect, native.Level) / 100d));
        ushort action = (ushort)(catalog.ActionHash(effect)!.Value & 0x3fff);
        int radius = SkillArea.Radius(SkillArea.BaseRadius(FissureEffect, Get), Get, spell: false);
        var attack = new MonsterAttack(action, seconds, radius, damage) { Ailments = IncomingAilmentSettings(Get) };
        var slam = new VesselSkill(effect, action, level, native.Stats, attack, native.Crit);
        // The daemon rig has no VolcanicSlam contact measurement. Use the ordinary monster attack midpoint.
        double contact = catalog.MonsterContact(variety.Id, "VolcanicSlam", MonsterActionSequence)?.ContactFraction ?? .5;
        return new(slam, radius, TimeSpan.FromSeconds(seconds * contact).Ticks);
    }

    static VaalFissure PrepareFissure(ClientState c, Box box, FissureSkill skill)
    {
        var now = c.Clock.Elapsed;
        var aim = c.Walk.Position(now.TotalSeconds);
        var end = VolcanicFissureSweep.End(c.Layout, (box.X, box.Y), aim, skill.Radius);
        return new(now.Ticks, FissureCaster, aim.X, aim.Y, end.X, end.Y);
    }

    static async Task TickFissures(ClientState conn, Area area, Box box, TimeSpan now)
    {
        var skill = area.FissureSkill ??= ReadFissure(conn);
        foreach (var cast in box.Fissures.ToArray())
        {
            if (!await PublishVesselHits(conn, area, cast)) return;
            if (now.Ticks < cast.Started + skill.ReleaseTicks)
            {
                await ShowVesselCast(conn, area, box, cast, skill.Slam);
                continue;
            }

            var from = (X: box.X, Y: box.Y);
            var direction = GroundSlamWave.Direction(from, cast.End);
            int samples = cast.Samples(box);
            while (cast.Landed < samples)
            {
                int step = cast.Landed;
                long due = skill.Sample(cast, box, step);
                if (now.Ticks < due) break;
                if (!await LandVesselImpact(conn, area, box, cast, skill.Slam, skill.Ground, from, due, Contact)) return;

                (int X, int Y)? Contact(ClientState player, (int X, int Y) here)
                {
                    double along = VolcanicFissureSweep.ContactDistance(from, cast.End, here, skill.Radius);
                    if (along > step || (step > 0 && along <= step - 1)) return null;
                    if (!cast.Swept.Add(new(player.PeerId, player.IncomingHitEpoch))) return null;
                    return (from.X + (int)Math.Round(direction.X * along), from.Y + (int)Math.Round(direction.Y * along));
                }
            }
            if (cast.Landed < samples) continue;
            if (cast.Actor != 0 && !await RemoveVesselActor(conn, area, box, cast)) return;
            box.Fissures.Remove(cast);
            Keep(conn, area);
        }
    }
}
