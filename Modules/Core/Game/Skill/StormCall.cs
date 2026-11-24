using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// Storm Call: each landing of the cast (the aim, Spell Cascade's areas, every Spell Echo repeat) sets a marker
/// instead of hitting. A marker strikes when its skill duration ends (base_skill_effect_duration with skill effect
/// duration), and the strike also calls lightning down on every other marker the player has out (StormCallMarkers).
/// Quality's storm_call_chance_to_strike_on_cast_% strikes a new marker once as it is set, which leaves it in place.
/// The markers and strikes are server art: the worn skin's storm_call_loop and storm_call_impact
/// (MiscAnimatedArtVariations by skill_art_variation), the impact scaled to the strike's radius.
/// </summary>
public static partial class Game
{
    public static bool IsStormCall(string? effectId) => effectId == "StormCall";

    const double StormCallImpactArtSeconds = 1.5;

    static (string? Loop, string? Impact, int Variation) StormCallArt(Player player, uint gem)
    {
        int variation = SkillArtVariation(player, gem);
        var (loop, impact) = StormCallArt(variation);
        return (loop, impact, variation);
    }

    // The skin's marker and impact art (MiscAnimatedArtVariations), the base skin's where the skin has none.
    static (string? Loop, string? Impact) StormCallArt(int variation) =>
        (Art.Animated("storm_call_loop", variation) ?? Art.Animated("storm_call_loop", 0),
            Art.Animated("storm_call_impact", variation) ?? Art.Animated("storm_call_impact", 0));

    /// <summary>The impact art's scale for a strike of the radius (cells): the art drawn at the area's size.</summary>
    public static float AreaArtScale(double radius, string art) => (float)(radius * (250.0 / 23) / (Art.ArtRadius(art) ?? 250));

    /// <summary>Storm Call as any caster's skill plays it (a monster's: BossKits.Shaped): a marker that strikes after the
    /// skill's duration (base_skill_effect_duration with its own duration modifiers) and calls down the caster's other
    /// markers with it, in the skill_art_variation skin.</summary>
    public static MonsterDelayedStrike? StormCallStrikeOf(SkillCatalog catalog, int effect, byte level)
    {
        var stats = GolemData.Skill(catalog, effect, level).Stats;
        int Get(uint key) => stats.GetValueOrDefault(key);
        if (Get(Stat.BaseSkillEffectDuration) <= 0) return null;
        var (loop, impact) = StormCallArt(Get(Stat.SkillArtVariation));
        return new(SkillDurationModifiers.Seconds(Get(Stat.BaseSkillEffectDuration), _ => 0, Get), loop, impact,
            StormCallImpactArtSeconds, StrikesTogether: true);
    }

    sealed record StormCallUse(SkillCriticalRoll? Critical, SacrificeUse Sacrifice, double DamageMore, IntensityCast? Intensity);

    public static async Task PlaceStormCall(Player player,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) at,
        int repeat, int? actionToken, CooldownUseTicket? cooldownUse, SkillCriticalRoll? criticalRoll, SacrificeUse sacrifice,
        double sealDelay = 0, double damageMore = 1)
    {
        int? castMs = CastMs(player, gem.Effect, skillId);
        if (castMs is null) return;
        bool TokenCurrent() => cooldownUse?.Current(player) ?? (actionToken is null || player.ActionCount == actionToken);
        double delay;
        delay = sealDelay + castMs.Value / 1000.0 * (repeat + Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject));
        await AreaDelay(player, Math.Max(0.05, delay));
        await WhilePaused(player);
        await PlaceStormCallMarker(player, skillId, gem, at, repeat, criticalRoll, sacrifice, damageMore, TokenCurrent);
    }

    static async Task TriggerStormCall(Player player, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        (int X, int Y)? origin, SkillCriticalRoll critical, SacrificeUse sacrifice, Func<bool> current)
    {
        if (!current()) return;
        var from = origin ?? player.Walk.Position(AreaTime(player));
        var areas = SpellCascadeAreas(player, gem.Effect, SupportedSkill(player, skillId).Stats, from, aim);
        await PlaceStormCallMarker(player, skillId, gem, aim, 0, critical, sacrifice, 1, current);
        foreach (var area in areas)
            await PlaceStormCallMarker(player, skillId, gem, area, 0, critical, sacrifice, 1, current);
    }

    static async Task PlaceStormCallMarker(Player player, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) at, int repeat,
        SkillCriticalRoll? criticalRoll, SacrificeUse sacrifice, double damageMore, Func<bool> current)
    {
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0 || !current()) return;
        var own = SupportedSkill(player, skillId).Stats;
        var playerStats = CharacterPlayerStats(player);
        int Own(uint k) => own.GetValueOrDefault(k);
        int Player(uint k) => playerStats.GetValueOrDefault(k);
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Player, Own);
        int radius = SkillRadius(player, own, spell: true, gem.Effect);
        var (loop, _, variation) = StormCallArt(player, gem.Gem);
        StormCallMarkers.Marker marker;
        if (!current() || !sacrifice.Current) return;
        double now = AreaTime(player);
        var intensity = CastingIntensity(player, skillId);
        marker = new(player.TakeObjectId(), skillId, at, now + duration, repeat, player.ProjectileEpoch,
            sacrifice.AddedChaos(), new StormCallUse(criticalRoll, sacrifice, damageMore, intensity));
        var markers = player.StormCall;
        lock (markers) markers.Place(marker);
        intensity?.Complete();
        Console.WriteLine($"[gateway] {player.Connection.Remote} storm call marker 0x{marker.Object:x} at ({at.X}, {at.Y}), repeat {repeat}, "
            + $"strikes in {duration:0.00}s, radius {radius}; skin variation {variation}: art {loop ?? "none"}");
        if (loop is not null)
        {
            await Send(player, InstanceEntities.CreateObject(marker.Object,
                    InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(at.X, at.Y, loop) { Flags0 = EffectObject.FixedRotation }.Blob()),
                $"0x02bb create effect 0x{marker.Object:x} {loop} at ({at.X}, {at.Y})");
            await Send(player, InstanceProtocol.EnterWakeZone(marker.Object), null);
        }
        // Quality: a chance that lightning strikes the new marker as it is set; the marker stays for its own strike.
        int chance = Own(Stat.StormCallChanceToStrikeOnCastPct);
        if (chance > 0 && AreaRng(player).Next(100) < chance)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} storm call strike on cast at marker 0x{marker.Object:x} ({chance}% chance)");
            await StormCallStrike(player, marker, removeMarker: false);
        }
        _ = StrikeStormCallsWhenDue(player, marker.StrikeAt, marker.Epoch);
    }

    static async Task StrikeStormCallsWhenDue(Player player,
        double at, int epoch)
    {
        try
        {
            while (true)
            {
                double wait = at - AreaTime(player);
                if (wait > 0) await AreaDelay(player, wait);
                await WhilePaused(player);
                if (player.Closed) return;
                if (AreaTime(player) >= at) break;
            }
            await StrikeDueStormCalls(player, AreaTime(player), epoch);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    internal static async Task StrikeDueStormCalls(Player player, double now, int epoch)
    {
        if (player.Closed || player.LeavingArea || player.ProjectileEpoch != epoch) return;
        var markers = player.StormCall;
        IReadOnlyList<StormCallMarkers.Marker> struck;
        lock (markers) struck = markers.TakeStrikes(now, epoch);
        if (struck.Count == 0) return;
        Console.WriteLine($"[gateway] {player.Connection.Remote} storm call strike: {struck.Count} marker(s) "
            + string.Join(", ", struck.Select(m => $"0x{m.Object:x}")));
        await Task.WhenAll(struck.Select(m => StormCallStrike(player, m, removeMarker: true)));
    }

    // One marker's lightning: the impact art there and the skill's area hit around it, with the cast's critical roll and
    // repeat (Spell Echo's final-repeat damage). A dead player or a gem no longer socketed strikes nothing.
    static async Task StormCallStrike(Player player,
        StormCallMarkers.Marker marker, bool removeMarker)
    {
        if (removeMarker) await Send(player, InstanceProtocol.RemoveObject(marker.Object), null);
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == marker.Skill);
        if (marker.Use is not StormCallUse use || !use.Sacrifice.Current || gem.Name is null || !IsStormCall(SkillsFor(player).EffectId(gem.Effect)) || player.PlayerLife == 0)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} storm call marker 0x{marker.Object:x} strikes nothing (gem gone or player dead)");
            return;
        }
        using var intensity = use.Intensity is { } cast ? new IntensityScope(cast) : null;
        int radius = SkillRadius(player, SupportedSkill(player, marker.Skill).Stats, spell: true, gem.Effect);
        var (_, impact, _) = StormCallArt(player, gem.Gem);
        if (impact is not null)
        {
            uint effect;
            effect = player.TakeObjectId();
            float scale = AreaArtScale(radius, impact);
            await Send(player, InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(marker.At.X, marker.At.Y, impact) { Flags0 = EffectObject.FixedRotation, Scale = scale }.Blob()),
                $"0x02bb create effect 0x{effect:x} {impact} at ({marker.At.X}, {marker.At.Y}), scale {scale:0.###} for radius {radius}");
            await Send(player, InstanceProtocol.EnterWakeZone(effect), null);
            SendAfter(player, StormCallImpactArtSeconds, async () =>
            {
                if (player.Closed || player.LeavingArea || player.ProjectileEpoch != marker.Epoch) return;
                await Send(player, InstanceProtocol.RemoveObject(effect), null);
            });
        }
        await AreaSkillHit(player, marker.Skill, gem, marker.At, marker.At, immediate: true,
            criticalRoll: use.Critical, repeat: marker.Repeat, sacrifice: use.Sacrifice, damageMore: use.DamageMore,
            triggeredCurrent: () => !player.Closed && !player.LeavingArea && player.ProjectileEpoch == marker.Epoch && player.PlayerLife > 0);
    }
}
