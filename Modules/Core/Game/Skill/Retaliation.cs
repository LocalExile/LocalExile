using GameServer.Core.Projectiles;

public static partial class Game
{
    const string DivineRetributionEffect = "DivineRetribution";
    const string DivineRetributionOpportunity = "divine_retribution_opportunity";
    const string VengefulCryEffect = "VengefulCry";
    const string VengefulCryOpportunity = "vengeful_cry_opportunity";
    readonly record struct RetaliationGate(string Event, ushort Buff, bool OnBlock = true);

    static RetaliationGate? RetaliationOpportunity(string? effect) => effect switch
    {
        DivineRetributionEffect => new(DivineRetributionOpportunity, 2470),
        VengefulCryEffect => new(VengefulCryOpportunity, 2471, OnBlock: false),
        "CrushingFist" => new("crushing_fist_opportunity", 2469),
        "GlacialShieldSwipe" => new("glacial_shield_swipe_opportunity", 2468),
        "Swordstorm" => new("swordstorm_opportunity", 2472),
        "Eviscerate" => new("eviscerate_opportunity", 2473),
        _ => null,
    };

    // The invisible BuffDefinitions rows unlock the native skill buttons for the same window the server validates.
    static void OpenRetaliationWindows(Player player, bool blocked, double now)
    {
        if (player.PlayerLife == 0) return;
        string originalGate = blocked ? DivineRetributionOpportunity : VengefulCryOpportunity;
        player.RecentEvents.Record(originalGate, now);
        var catalog = SkillsFor(player);
        foreach (var gem in GemSkillEffects(player))
        {
            if (RetaliationOpportunity(catalog.EffectId(gem.Effect)) is not { } gate
                || gate.OnBlock != blocked || catalog.ActionHash(gem.Effect) is not ushort action) continue;
            double duration = RetaliationWindowSeconds(player, SupportedSkill(player, gem.Id).Stats);
            if (duration <= 0) continue;
            if (gate.Event != originalGate) player.RecentEvents.Record(gate.Event, now);
            player.Area.Buffs.Set(player.Id, BuffRows.Spec(gate.Buff, player.Id, null, duration, tag: gem.Id) with
            {
                SourceSkill = gem.Id,
                ActiveSkill = (gem.Id, action),
            });
        }
    }

    static void SpendRetaliationWindow(Player player, RetaliationGate gate)
    {
        player.RecentEvents.Forget(gate.Event);
        player.Area.Buffs.ClearRow(player.Id, gate.Buff);
    }

    static bool RetaliationReady(Player player, ushort skillId, string key, double now)
    {
        if (player.RecentEvents.LastWindow(key) is not { } opportunity || now < opportunity.From) return false;
        var skill = SupportedSkill(player, skillId);
        if (key == DivineRetributionOpportunity
            && (PlacedKind(skill) != default || IsTotemSkill(player, skillId))) return false;
        return now < opportunity.From + RetaliationWindowSeconds(player, skill.Stats);
    }

    static double RetaliationWindowSeconds(Player player, IReadOnlyDictionary<uint, int> own)
    {
        long milliseconds = own.GetValueOrDefault(Stat.RetaliationBaseUseWindowDurationMs);
        if (milliseconds <= 0) return 0;
        var character = PlayerStatValues(player);
        milliseconds += (long)own.GetValueOrDefault(Stat.RetaliationSkillAdditionalUseWindowDurationMs)
            + character.GetValueOrDefault(Stat.RetaliationSkillAdditionalUseWindowDurationMs);
        long increased = (long)own.GetValueOrDefault(Stat.RetaliationUseWindowDurationPlusPct)
            + character.GetValueOrDefault(Stat.RetaliationUseWindowDurationPlusPct);
        return Math.Max(0, milliseconds) * Math.Max(0, 100d + increased) / 100000d;
    }

    public sealed partial class ProjectileSession
    {
        // RetaliationHolySpell (3.29.3c) starts the waves eight cells from the aim, then spreads each over
        // a scaled 70-cell reach in one second. Each marker detonates after 800 ms.
        void ScheduleDivineRetribution((int X, int Y) from, (int X, int Y) aim, int waveCount, int burstCount,
            Func<int, int> scale, int baseRadius, Func<bool> current, Action<(int X, int Y), int> contact)
        {
            int waves = Math.Clamp(waveCount, 0, 32), bursts = Math.Clamp(burstCount, 0, 32);
            if (waves == 0 || bursts == 0) return;
            int radius = scale(baseRadius);
            int offset = scale(8), reach = scale(70);
            long first = Runtime.Now + ProjectileRuntime.Time(.8);
            long gap = ProjectileRuntime.Time((1000 / bursts) / 1000d);
            double angle = Math.Atan2(aim.Y - from.Y, aim.X - from.X);
            for (int wave = 0; wave < waves; wave++)
            {
                double bearing = angle + Math.Tau * wave / waves;
                var direction = new ProjectileVector(Math.Cos(bearing), Math.Sin(bearing));
                var start = Cell(new ProjectileVector(aim.X, aim.Y), direction * offset);
                for (int burst = 0; burst < bursts; burst++)
                {
                    var point = Cell(start, direction * (reach * (burst / (double)bursts)));
                    if (TraceTerrain(start, point) is not null) break;
                    var centre = ((int)point.X, (int)point.Y);
                    ScheduleContact(first + gap * burst, current, () => contact(centre, radius));
                }
            }

            static ProjectileVector Cell(ProjectileVector start, ProjectileVector offset) =>
                new(start.X + Math.Round(offset.X), start.Y + Math.Round(offset.Y));
        }
    }
}
