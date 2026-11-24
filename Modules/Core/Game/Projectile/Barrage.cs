using GameServer.Core.Projectiles;

public static partial class Game
{
    static bool IsBarrageGem(string? effect) => effect is "Barrage" or "BarrageAltX";

    static void ScheduleBarrage(Player player, ProjectileSession session, ushort skill, int effect,
        double startedAt, double attackSeconds, ushort sequence, Func<bool> valid, Func<double, ProjectileUse> capture)
    {
        if (ResolveProjectileDefinition(effect) is not { } definition) return;
        var stats = MergeSkillStats(CharacterPlayerStats(player), SupportedSkill(player, skill).Stats);
        int Get(uint key) => stats.GetValueOrDefault(key);
        int count = (int)Math.Clamp((long)definition.Parameters.Resolve(Get, 1, ArrowsFor(player, effect)).Count
            + Get(Stat.BarrageNumOfAdditionalProjectiles), 1, 256);
        int extra = Math.Clamp(Get(Stat.BarrageFinalVolleyFiresXAdditionalProjectilesSimultaneously), 0, 255);
        bool spread = Get(Stat.NoBarrageProjectileSpread) == 0;
        var (first, interval) = BarrageReleaseTiming(player, attackSeconds, count);
        ProjectileUse? captured = null;
        for (int i = 0; i < count; i++)
        {
            int ordinal = i;
            int groupCount = i == 0 || i == count - 1 ? 1 + extra : 1;
            session.Schedule(ProjectileRuntime.Time(startedAt + first + i * interval), valid, () =>
            {
                var use = captured ??= capture(first);
                var profile = use.Profile with { Count = groupCount };
                if (profile.NativeVolley is { Angles: not null })
                    profile = profile with { NativeVolley = profile.NativeVolley with
                    {
                        Angles = RandomVolleyAngles(sequence, groupCount, use.Emitter.Id, use.Aim - use.Origin, (uint)ordinal),
                    } };
                if (profile.ReturnRolls is not null)
                    profile = profile with { ReturnRolls = ReturnRolls(sequence, groupCount, use.Emitter.Id, (uint)ordinal) };
                // A group shares its contact history. Later launches are independent and may hit that target again.
                if (!spread) return use with { Profile = profile };
                var rng = new AnimationVariantSelector.NativeRandom([sequence, use.Emitter.Id, (uint)ordinal]);
                float offset = rng.Range(-MathF.PI / 72, MathF.PI / 72);
                return use with { Profile = profile, Aim = use.Origin + (use.Aim - use.Origin).Rotate(offset) };
            });
        }
    }

    static (double First, double Interval) BarrageReleaseTiming(Player player, double attackSeconds, int projectiles)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO")
            ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
        var weapon = AnimationWeaponProfileFor(player);
        string stance = weapon.Bow ? "bow" : weapon.Shield ? "wand_shield" : weapon.DualWield ? "wand_wand" : "wand";
        string loop = $"barrage_{stance}";
        int windup = Animations.ClipLengthMs(ao, loop + "_start")
            ?? Animations.ClipLengthMs(ao, $"barrage_start_{stance}") ?? (weapon.Bow ? 800 : 633);
        int repeat = Animations.ClipLengthMs(ao, loop) ?? 200;
        double contact = Animations.SkillAttackContact(ao, weapon, loop)?.Contact ?? .33;
        // Each rig has a separate windup and loop. Scale their combined timeline to the paid attack duration.
        double scale = attackSeconds / Math.Max(1, windup + repeat * projectiles);
        return ((windup + repeat * contact) * scale, repeat * scale);
    }
}
