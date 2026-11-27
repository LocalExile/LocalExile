using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string VenomGyreProjectile = "Metadata/Projectiles/SnappingAdder";
    const string VenomGyreReleased = "Metadata/Projectiles/SnappingAdderReleased";
    const string VaalVenomGyreEffect = "VaalVenomGyre";

    internal static bool VenomGyreEffect(string? effect) => effect is
        "VenomGyre" or "VenomGyreAltX" or "VenomGyreAltY" or "VenomGyreRoyale" or "VenomGyreMercenary" or VaalVenomGyreEffect;

    internal sealed record VenomGyreCapture(int Capacity, int ReleaseLimit, int Retain, long Lifetime,
        double ReleaseMore, int Art, Func<bool> Current, int VaalRate, long VaalLifetime);

    static VenomGyreCapture CaptureVenomGyre(Player player, int effect, Func<uint, int> get, Func<uint, int> caster,
        Func<uint, int> own, int art, SacrificeUse source)
    {
        double Duration(uint stat) => SkillDurationModifiers.Seconds(own(stat), caster, own, DurationKinds(player, effect));
        return new(Math.Clamp(get(Stat.MaximumNumberOfSnappingAdderProjectiles), 0, 256),
            Math.Clamp(get(Stat.SnappingAdderMaximumProjectilesReleased), 0, 256),
            Math.Clamp(get(Stat.SnappingAdderPctChanceToRetainProjectileOnRelease), 0, 100),
            ProjectileRuntime.Time(Duration(Stat.BaseSkillEffectDuration)),
            Math.Max(0, 1 + get(Stat.SnappingAdderReleasedProjectileDamagePlusPctFinal) / 100d),
            art, () => source.Current, Math.Clamp(get(Stat.VaalVenomGyreCaptureXProjectilesPerSecond), 0, 256),
            ProjectileRuntime.Time(Duration(Stat.BaseSecondarySkillEffectDuration)));
    }

    public sealed partial class ProjectileSession
    {
        sealed class VenomBlade(ProjectileUse use, VenomGyreCapture capture, long expires)
        {
            public ProjectileUse Use { get; } = use;
            public VenomGyreCapture Capture { get; } = capture;
            public long Expires { get; } = expires;
        }

        List<VenomBlade>? venomBlades;
        long venomWake;
        long venomVaalToken;

        void CatchVenomGyre(ProjectileUse use, VenomGyreCapture capture)
        {
            if (!Active || Player.PlayerLife == 0 || capture.Lifetime <= 0 || !capture.Current()) return;
            PruneVenomGyre();
            var blades = venomBlades ??= [];
            if (blades.Count >= capture.Capacity) return;
            blades.Add(new(use, capture, Runtime.Now + capture.Lifetime));
            ShowVenomGyre();
        }

        internal void StartVaalVenomGyre(ProjectileUse use)
        {
            if (use.Payload is not ProjectileOffense { VenomGyre: { } capture } shot || capture.Lifetime <= 0) return;
            PruneVenomGyre();
            var blades = venomBlades ??= [];
            while (blades.Count < capture.Capacity)
                blades.Add(new(use, capture, Runtime.Now + capture.Lifetime));
            ShowVenomGyre();
            if (capture.VaalRate == 0 || capture.VaalLifetime == 0) return;

            ushort row = BuffRows.Find("vaal_snapping_adder");
            var buff = BuffRows.Spec(row, Player.Id, null, capture.VaalLifetime / (double)ProjectileRuntime.QuantaPerSecond);
            if (row != 0) Player.Area.Buffs.Set(Player.Id, buff with { SourceSkill = shot.SourceSkill });

            long began = Runtime.Now;
            long token = ++venomVaalToken;
            ScheduleRefill(1);
            void ScheduleRefill(int index)
            {
                long delay = ProjectileRuntime.Time(index / (double)capture.VaalRate);
                if (delay >= capture.VaalLifetime) return;
                ScheduleContact(began + delay, () => token == venomVaalToken && Player.PlayerLife > 0, () =>
                {
                    if (!capture.Current())
                    {
                        if (row != 0) Player.Area.Buffs.Clear(Player.Id, row, Player.Id);
                        return;
                    }
                    CatchVenomGyre(use, capture);
                    ScheduleRefill(index + 1);
                });
            }
        }

        internal void ScheduleVenomGyreRelease((int X, int Y) start, (int X, int Y) landing, double takeoff,
            double travel, Func<bool> current)
        {
            if (venomBlades is not { Count: > 0 } blades) return;
            ScheduleContact(ProjectileRuntime.Time(takeoff), current, () =>
            {
                PruneVenomGyre();
                blades.RemoveAll(blade => !blade.Capture.Current());
                ShowVenomGyre();
                if (blades.Count == 0) return;

                var selected = blades.Take(blades[0].Capture.ReleaseLimit).ToArray();
                var origin = Vector(start);
                var path = Vector(landing) - origin;
                for (int i = 0; i < selected.Length; i++)
                {
                    var blade = selected[i];
                    double progress = i / (double)selected.Length;
                    // Spread the release over the paid slide. The exact native spiral angle is unconfirmed.
                    var direction = path.Unit.Rotate(progress * Math.Tau);
                    ScheduleContact(Runtime.Now + ProjectileRuntime.Time(travel * progress),
                        () => current() && blade.Expires > Runtime.Now && blade.Capture.Current(),
                        () => ReleaseVenomBlade(blade, origin + path * progress, direction));
                }
            });
        }

        void ReleaseVenomBlade(VenomBlade blade, ProjectileVector origin, ProjectileVector direction)
        {
            if (venomBlades is not { } blades || !blades.Contains(blade)) return;
            if (blade.Capture.Retain < 100 && (blade.Capture.Retain == 0 || AreaRng(Player).Next(100) >= blade.Capture.Retain))
            {
                blades.Remove(blade);
                ShowVenomGyre();
            }

            var shot = (ProjectileOffense)blade.Use.Payload!;
            var profile = blade.Use.Profile with
            {
                Id = Art.Projectile(VenomGyreReleased, blade.Capture.Art), Count = 1,
                Returns = false, CannotReturn = true, ReturnChancePercent = 0, ReturnRolls = null,
                ParallelOrigins = null, NativeVolley = null, SequentialDelay = 0
            };
            ReleaseNow(blade.Use with
            {
                Profile = profile, Origin = origin, Aim = origin + direction * profile.Range,
                Payload = shot.ScaleHitAndAilmentDamage(blade.Capture.ReleaseMore) with
                {
                    Label = "Venom Gyre released blade", VenomGyre = null, ClientPredicted = false
                },
                CanEmit = null, ReturnAnchor = null, RadialLaunchOffset = 0,
                ProjectileSpeeds = null, ProjectileTargets = null, ProjectilePayloads = null
            });
        }

        void PruneVenomGyre() => venomBlades?.RemoveAll(blade => blade.Expires <= Runtime.Now);

        void ShowVenomGyre()
        {
            ushort row = BuffRows.Find("snapping_adder_projectile");
            if (venomBlades is not { Count: > 0 } blades)
            {
                if (row != 0) Player.Area.Buffs.Clear(Player.Id, row, Player.Id);
                venomWake = 0;
                return;
            }

            long next = blades.Min(blade => blade.Expires);
            if (venomWake != next)
            {
                venomWake = next;
                Runtime.Schedule(next, () =>
                {
                    if (!Active || next != venomWake) return;
                    venomWake = 0;
                    PruneVenomGyre();
                    ShowVenomGyre();
                });
            }

            if (row == 0) return;
            double remaining = (blades.Max(blade => blade.Expires) - Runtime.Now) / (double)ProjectileRuntime.QuantaPerSecond;
            var buff = BuffRows.Spec(row, Player.Id, null, remaining);
            Player.Area.Buffs.Set(Player.Id, buff with
            {
                SourceSkill = ((ProjectileOffense)blades[0].Use.Payload!).SourceSkill,
                Stacks = (ushort)blades.Count
            });
        }

        void ClearVenomGyre()
        {
            if (venomBlades is null) return;
            venomBlades.Clear();
            venomWake = 0;
            venomVaalToken++;
            foreach (string id in new[] { "snapping_adder_projectile", "vaal_snapping_adder" })
                if (BuffRows.Find(id) is > 0 and var row)
                    Player.Area.Buffs.Clear(Player.Id, row, Player.Id);
        }
    }
}
