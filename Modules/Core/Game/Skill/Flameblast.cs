using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// Flameblast (effects <c>Flameblast</c>, <c>FlameblastAltX</c> of Celerity, <c>FlameblastAltY</c> of Contraction):
/// each paid channel tick adds a stage, up to <c>flameblast_maximum_stages</c> (quality adds to it,
/// <c>flameblast_starts_with_X_additional_stages</c> starts higher). Nothing hits while channelling. Releasing (or running
/// out of mana, or a stun or freeze interrupting it) explodes once at the point aimed when the cast started, pulled back
/// to the last cell in sight of the caster (no casting through walls). The others hold at their maximum until then;
/// Celerity ("released ... automatically at maximum stages") explodes by itself and a held channel starts over.
/// Radius = (<c>flameblast_base_radius_override</c>, else 2) + <c>vaal_flameblast_radius_+_per_stage</c> × stages
/// before area modifiers. Hits take <c>charged_blast_spell_damage_+%_final_per_stack</c> per stage more, ailments
/// <c>flameblast_ailment_damage_+%_final_per_stack</c> per stage. The target marker and the explosion are server art
/// (MiscAnimated ChargedBlast*): the marker starts at the base radius and grows toward the next stage's radius, redrawn
/// every <see cref="FlameblastMarkerStepSeconds"/> (no scale update for an effect object is known). The stage buff
/// (BuffDefinitions 286 <c>charged_blast_counter</c>) is not sent: a live client crashed with it.
/// </summary>
public static partial class Game
{
    const int FlameblastDefaultBaseRadius = 2;
    const double FlameblastExplosionArtSeconds = 1.5;
    const string FlameblastFx = "Metadata/Effects/Spells/charged_blast/";
    const string FlameblastMarkerAo = FlameblastFx + "chargedblast_target.ao";
    const double FlameblastMarkerStepSeconds = 0.05;
    // The replaced marker stays a moment under the new one, so the circle never blinks out between sizes.
    const double FlameblastMarkerOverlapSeconds = 0.08;

    internal sealed class FlameblastRuntime
    {
        public ushort SkillId;
        public (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem;
        public int Action;
        public int ChannelGeneration;
        public int Epoch;
        public object? Rules;
        public int Stages;
        public bool Channelling;
        public (int X, int Y) Aim;
        public uint Marker;
        public int MarkerRadius;
        public int LoggedStages = -1;
        public double LastStageAt;
        public double Interval = 0.2;
    }

    static readonly RuleTable<Player, FlameblastRuntime> FlameblastRuntimes = new();

    /// <summary>The ailment more of the release being resolved, read by the ignite/poison/bleed settings (Overheat.cs).</summary>
    static readonly RuleTable<Player, System.Collections.Concurrent.ConcurrentDictionary<ushort, double>> FlameblastAilmentMores = new();

    internal static bool IsFlameblastEffect(string? effectId) => effectId is "Flameblast" or "FlameblastAltX" or "FlameblastAltY";

    internal static bool IsFlameblast(int effect) => IsFlameblastEffect(Skills.EffectId(effect));

    /// <summary>Whether Flameblast is being channelled on <paramref name="skillId"/>: the button holds it, not the aim
    /// updates (a still cursor sends none, and the 1 s aim silence ended it at about 6 stages).</summary>
    internal static bool IsFlameblastChannelling(Player player, ushort skillId) =>
        FlameblastRuntimes.TryGetValue(player, out var state) && state.Channelling && state.SkillId == skillId
        && state.Epoch == player.ProjectileEpoch && ReferenceEquals(state.Rules, CombatLifetime);

    // Read under whichever lock the hit holds: no lock of its own (the table and its map are thread-safe).
    internal static double FlameblastAilmentMore(Player player, ushort skillId) =>
        FlameblastAilmentMores.TryGetValue(player, out var mores) ? mores.GetValueOrDefault(skillId, 1) : 1;

    public static int FlameblastMaxStages(IReadOnlyDictionary<uint, int> stats) =>
        Math.Clamp(GolemStat(stats, Stat.FlameblastMaximumStages) is int max and > 0 ? max : 10, 1, 50);

    /// <summary>Hit and ailment more multipliers at <paramref name="stages"/> (165% per stage: ×2.65 at one stage).</summary>
    public static (double Hit, double Ailment) FlameblastStageMore(IReadOnlyDictionary<uint, int> stats, int stages)
    {
        int n = Math.Max(0, stages);
        return (Math.Max(0, 1 + n * GolemStat(stats, Stat.ChargedBlastSpellDamagePlusPctFinalPerStack) / 100.0),
            Math.Max(0, 1 + n * GolemStat(stats, Stat.FlameblastAilmentDamagePlusPctFinalPerStack) / 100.0));
    }

    /// <summary>The explosion's radius in cells at <paramref name="stages"/>, grown by the player's area stats.</summary>
    public static int FlameblastRadius(Player player, IReadOnlyDictionary<uint, int> stats, int effect, double stages)
    {
        int baseRadius = GolemStat(stats, Stat.FlameblastBaseRadiusOverride) is int over and > 0 ? over : FlameblastDefaultBaseRadius;
        baseRadius = Math.Max(1, (int)Math.Round(baseRadius + Math.Max(0, stages) * GolemStat(stats, Stat.VaalFlameblastRadiusPlusPerStage)));
        var areaStats = SkillAreaStats(player, stats, effect);
        int Get(uint k) => areaStats.GetValueOrDefault(k);
        int radius = SkillArea.Radius(baseRadius, Get, spell: true);
        if (GolemStat(stats, Stat.FlameblastAreaPlusPctFinalPerStage) is int perStage and not 0)
            radius = (int)Math.Floor(radius * SkillArea.RadiusMultiplier((int)Math.Round(perStage * Math.Max(0, stages))));
        return Math.Max(1, radius);
    }

    internal static async Task FlameblastStart(Player player, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, int action, (int X, int Y) aim)
    {
        uint staleMarker = 0;
        int start = Math.Max(0, GolemStat(SupportedSkill(player, skillId).Stats, Stat.FlameblastStartsWithXAdditionalStages));
        double interval = SupportedChannelSeconds(player, skillId) ?? 0.2;
        var from = player.Walk.Position(AreaTime(player));
        if (player.Layout.Sight is { } sight && GameServer.Core.World.ClientPathfinder.LineBelow(from, aim, sight, 1, out var seen))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} flameblast aim ({aim.X}, {aim.Y}) is behind a wall: pulled back to ({seen.X}, {seen.Y})");
            aim = seen;
        }
        FlameblastRuntime runtime;
        
        {
            if (FlameblastRuntimes.TryGetValue(player, out var old))
            {
                staleMarker = old.Marker;
                old.Marker = 0;
                old.Channelling = false;
            }
            runtime = new FlameblastRuntime
            {
                SkillId = skillId, Gem = gem, Action = action, ChannelGeneration = player.ChannelGeneration,
                Epoch = player.ProjectileEpoch, Rules = CombatLifetime, Channelling = true, Aim = aim, Stages = start,
                LastStageAt = AreaTime(player), Interval = interval,
            };
            FlameblastRuntimes.AddOrUpdate(player, runtime);
        }
        if (staleMarker != 0) await SendWorldRemoval(player, staleMarker);
        Console.WriteLine($"[gateway] {player.Connection.Remote} flameblast 0x{skillId:x4} {gem.Name} channel start at ({aim.X}, {aim.Y}), {start} stage(s)");
        await FlameblastDrawMarker(player, runtime);
        _ = AreaFrames.Start(() => FlameblastMarkerLoop(player, runtime));
    }

    /// <summary>One paid channel tick: one stage more (held at the maximum); Celerity explodes at its maximum and starts over.</summary>
    internal static async Task FlameblastStageStep(Player player, int generation, int action)
    {
        ushort skillId;
        
        {
            if (!FlameblastRuntimes.TryGetValue(player, out var runtime) || !runtime.Channelling
                || runtime.ChannelGeneration != generation || runtime.Action != action) return;
            skillId = runtime.SkillId;
        }
        int max = FlameblastMaxStages(SupportedSkill(player, skillId).Stats);
        double interval = SupportedChannelSeconds(player, skillId) ?? 0.2;
        bool autoRelease;
        int stages;
        FlameblastRuntime current;
        
        {
            if (!FlameblastRuntimes.TryGetValue(player, out var runtime) || !runtime.Channelling
                || runtime.ChannelGeneration != generation || runtime.Action != action) return;
            if (runtime.Stages < max) runtime.Stages++;
            stages = runtime.Stages;
            runtime.LastStageAt = AreaTime(player);
            runtime.Interval = interval;
            autoRelease = stages >= max && Skills.EffectId(runtime.Gem.Effect) == "FlameblastAltX";
            current = runtime;
        }
        if (!autoRelease) { await FlameblastDrawMarker(player, current); return; }
        // Flameblast of Celerity: "released ... automatically at maximum stages"; the held channel starts over.
        (int X, int Y) aim;
        uint marker;
        
        {
            aim = current.Aim;
            marker = current.Marker;
            current.Marker = 0;
            current.MarkerRadius = 0;
            current.Stages = 0;
        }
        FlameblastRemoveMarkerLater(player, marker, current.Epoch);
        await FlameblastExplode(player, current.SkillId, current.Gem, stages, aim, "maximum stages");
        await FlameblastDrawMarker(player, current);
    }

    /// <summary>Redraws the marker while the channel lasts, so it grows between the stage ticks.</summary>
    static async Task FlameblastMarkerLoop(Player player,
        FlameblastRuntime runtime)
    {
        try
        {
            while (true)
            {
                await AreaDelay(player, FlameblastMarkerStepSeconds);
                if (!runtime.Channelling || player.Closed || player.LeavingArea || player.ProjectileEpoch != runtime.Epoch) return;
                if (player.Paused) continue;
                await FlameblastDrawMarker(player, runtime);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    /// <summary>The marker at the aim, sized between the built stages and the next (the next stage's radius is reached
    /// as its tick lands); redrawn only when its radius changed.</summary>
    static async Task FlameblastDrawMarker(Player player,
        FlameblastRuntime runtime)
    {
        int stages, max;
        double shown;
        ushort skillId;
        int effect;
        
        {
            if (!runtime.Channelling) return;
            (stages, skillId, effect) = (runtime.Stages, runtime.SkillId, runtime.Gem.Effect);
            double progress = runtime.Interval > 0
                ? Math.Clamp((AreaTime(player) - runtime.LastStageAt) / runtime.Interval, 0, 1) : 0;
            shown = stages + progress;
        }
        var stats = SupportedSkill(player, skillId).Stats;
        max = FlameblastMaxStages(stats);
        int radius = FlameblastRadius(player, stats, effect, Math.Min(shown, max));
        uint oldMarker, newMarker;
        (int X, int Y) aim;
        bool log;
        
        {
            if (!runtime.Channelling || player.Closed || player.LeavingArea || runtime.MarkerRadius == radius) return;
            (aim, oldMarker) = (runtime.Aim, runtime.Marker);
            runtime.Marker = newMarker = player.TakeObjectId();
            runtime.MarkerRadius = radius;
            log = runtime.LoggedStages != stages;
            runtime.LoggedStages = stages;
        }
        float scale = (float)(radius * (250.0 / 23) / (Art.ArtRadius(FlameblastMarkerAo) ?? 500));
        await SendWorldCreate(player, newMarker, InstanceEntities.CreateObject(newMarker, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(aim.X, aim.Y, FlameblastMarkerAo) { Flags0 = EffectObject.FixedRotation, Scale = scale }.Blob()),
            log ? $"0x02bb create effect 0x{newMarker:x} flameblast marker at ({aim.X}, {aim.Y}), {stages}/{max} stage(s), scale {scale:0.###} for radius {radius}" : null);
        await SendWorldRecord(player, newMarker, InstanceProtocol.EnterWakeZone(newMarker), null);
        FlameblastRemoveMarkerLater(player, oldMarker, runtime.Epoch);
    }

    // Removed a moment later: the next marker is drawn under it first, and a create still being written by the redraw
    // loop always reaches the client before its removal.
    static void FlameblastRemoveMarkerLater(Player player,
        uint marker, int epoch)
    {
        if (marker == 0) return;
        SendAfter(player, FlameblastMarkerOverlapSeconds, async () =>
        {
            if (player.Closed || player.LeavingArea || player.ProjectileEpoch != epoch) return;
            await SendWorldRemoval(player, marker);
        });
    }

    /// <summary>Death or rules retirement: drops the state without exploding; the icon is removed on respawn.</summary>
    internal static void FlameblastRetire(Player player)
    {
        
        {
            if (!FlameblastRuntimes.TryGetValue(player, out var runtime)) return;
            runtime.Channelling = false;
            FlameblastRuntimes.Remove(player);
        }
    }

    /// <summary>The channel ended (button let go, out of mana, gem removed): explode at the built stages.</summary>
    internal static async Task FlameblastRelease(Player player, int generation, int action)
    {
        FlameblastRuntime? runtime;
        bool explode;
        
        {
            if (!FlameblastRuntimes.TryGetValue(player, out runtime)
                || runtime.ChannelGeneration != generation || runtime.Action != action) return;
            FlameblastRuntimes.Remove(player);
            runtime.Channelling = false;
            explode = CurrentGameRules && ReferenceEquals(runtime.Rules, CombatLifetime)
                && runtime.Epoch == player.ProjectileEpoch && !player.Closed && !player.LeavingArea
                && !player.RageRetired && player.PlayerLife > 0 && !InTown(player) && runtime.Stages > 0;
        }
        uint marker;
        { marker = runtime.Marker; runtime.Marker = 0; }
        FlameblastRemoveMarkerLater(player, marker, runtime.Epoch);
        if (!explode)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} flameblast 0x{runtime.SkillId:x4} ends without a blast ({runtime.Stages} stage(s))");
            return;
        }
        await FlameblastExplode(player, runtime.SkillId, runtime.Gem, runtime.Stages, runtime.Aim, "released");
    }

    /// <summary>One explosion of <paramref name="stages"/> at <paramref name="aim"/>: its art, then one area hit.</summary>
    public static async Task FlameblastExplode(Player player,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, int stages, (int X, int Y) aim,
        string why)
    {
        var supported = SupportedSkill(player, skillId);
        if (supported.Effect != gem.Effect || stages <= 0) return;
        var stats = supported.Stats;
        int max = FlameblastMaxStages(stats);
        int radius = FlameblastRadius(player, stats, gem.Effect, stages);
        var (hitMore, ailmentMore) = FlameblastStageMore(stats, stages);
        double fraction = (double)stages / max;
        string ao = FlameblastFx + (fraction <= 0.34 ? "chargedblast_explode_small.ao"
            : fraction <= 0.7 ? "chargedblast_explode_medium.ao" : "chargedblast_explode_large.ao");
        uint effect;
        int epoch;
        
        {
            if (player.Closed || player.LeavingArea) return;
            effect = player.TakeObjectId();
            epoch = player.ProjectileEpoch;
        }
        float scale = (float)(radius * (250.0 / 23) / (Art.ArtRadius(ao) ?? 325));
        await SendWorldCreate(player, effect, InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(aim.X, aim.Y, ao) { Flags0 = EffectObject.FixedRotation, Scale = scale }.Blob()),
            $"0x02bb create effect 0x{effect:x} flameblast explosion {ao} at ({aim.X}, {aim.Y}), scale {scale:0.###}");
        await SendWorldRecord(player, effect, InstanceProtocol.EnterWakeZone(effect), null);
        SendAfter(player, FlameblastExplosionArtSeconds, async () =>
        {
            if (player.Closed || player.LeavingArea || player.ProjectileEpoch != epoch) return;
            await SendWorldRemoval(player, effect);
        });
        Console.WriteLine($"[gateway] {player.Connection.Remote} flameblast 0x{skillId:x4} {gem.Name} blast ({why}): {stages}/{max} stage(s), " +
            $"radius {radius}, hit x{hitMore:0.##}, ailment x{ailmentMore:0.##} at ({aim.X}, {aim.Y})");
        
        {
            if (!FlameblastAilmentMores.TryGetValue(player, out var mores)) FlameblastAilmentMores.AddOrUpdate(player, mores = []);
            mores[skillId] = ailmentMore;
        }
        try
        {
            var from = player.Walk.Position(AreaTime(player));
            await AreaSkillHit(player, skillId, gem, from, aim, immediate: true,
                damageMore: hitMore, radiusOverride: radius,
                triggeredCurrent: () => !player.Closed && !player.LeavingArea && player.ProjectileEpoch == epoch && player.PlayerLife > 0);
        }
        finally
        {
            if (FlameblastAilmentMores.TryGetValue(player, out var mores)) mores.TryRemove(skillId, out _);
        }
    }
}
