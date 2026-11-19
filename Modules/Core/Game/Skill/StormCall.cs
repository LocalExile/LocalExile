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

    static (string? Loop, string? Impact, int Variation) StormCallArt(Player conn, uint gem)
    {
        int variation = SkillArtVariation(conn, gem);
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
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        if (Get("base_skill_effect_duration") <= 0) return null;
        var (loop, impact) = StormCallArt(Get("skill_art_variation"));
        return new(SkillDurationModifiers.Seconds(Get("base_skill_effect_duration"), _ => 0, Get), loop, impact,
            StormCallImpactArtSeconds, StrikesTogether: true);
    }

    sealed record StormCallUse(SkillCriticalRoll? Critical, SacrificeUse Sacrifice, double DamageMore);

    public static async Task PlaceStormCall(Player conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) at,
        int repeat, int? actionToken, CooldownUseTicket? cooldownUse, SkillCriticalRoll? criticalRoll, SacrificeUse sacrifice,
        double sealDelay = 0, double damageMore = 1)
    {
        int? castMs = CastMs(conn, gem.Effect, skillId);
        if (castMs is null) return;
        bool TokenCurrent() => cooldownUse?.Current(conn) ?? (actionToken is null || conn.ActionCount == actionToken);
        double delay;
        lock (conn.Monsters)
            delay = sealDelay + castMs.Value / 1000.0 * (repeat + Animations.SpellAreaRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject));
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.05, delay)));
        await WhilePaused(conn);
        if (conn.Closed || !TokenCurrent()) return;
        var own = SupportedSkill(conn, skillId).Stats;
        var player = CharacterPlayerStats(conn);
        int Own(string key) => Stats.Key(key) is uint k ? own.GetValueOrDefault(k) : 0;
        int Player(string key) => Stats.Key(key) is uint k ? player.GetValueOrDefault(k) : 0;
        double duration = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player, Own);
        int radius = SkillRadius(conn, own, spell: true, gem.Effect);
        var (loop, _, variation) = StormCallArt(conn, gem.Gem);
        StormCallMarkers.Marker marker;
        bool paid = false;
        lock (conn.Monsters)
        {
            if (!TokenCurrent() || !sacrifice.Current) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            marker = new(conn.TakeObjectId(), skillId, at, now + duration, repeat, conn.ProjectileEpoch,
                sacrifice.AddedChaos(() => paid = true), new StormCallUse(criticalRoll, sacrifice, damageMore));
            conn.StormCall.Place(marker);
        }
        if (paid) await Send(conn, [], null, atGate: sacrifice.LifePacket);
        Console.WriteLine($"[gateway] {conn.Remote} storm call marker 0x{marker.Object:x} at ({at.X}, {at.Y}), repeat {repeat}, "
            + $"strikes in {duration:0.00}s, radius {radius}; skin variation {variation}: art {loop ?? "none"}");
        if (loop is not null)
        {
            await Send(conn, InstanceEntities.CreateObject(marker.Object,
                    InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(at.X, at.Y, loop) { Flags0 = EffectObject.FixedRotation }.Blob()),
                $"0x02bb create effect 0x{marker.Object:x} {loop} at ({at.X}, {at.Y})");
            await Send(conn, InstanceProtocol.EnterWakeZone(marker.Object), null);
        }
        // Quality: a chance that lightning strikes the new marker as it is set; the marker stays for its own strike.
        int chance = Own("storm_call_chance_to_strike_on_cast_%");
        if (chance > 0 && Random.Shared.Next(100) < chance)
        {
            Console.WriteLine($"[gateway] {conn.Remote} storm call strike on cast at marker 0x{marker.Object:x} ({chance}% chance)");
            await StormCallStrike(conn, marker, removeMarker: false);
        }
        _ = StrikeStormCallsWhenDue(conn, marker.StrikeAt, marker.Epoch);
    }

    static async Task StrikeStormCallsWhenDue(Player conn,
        double at, int epoch)
    {
        try
        {
            while (true)
            {
                double wait = at - conn.Clock.Elapsed.TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
                await WhilePaused(conn);
                if (conn.Closed) return;
                if (conn.Clock.Elapsed.TotalSeconds >= at) break;
            }
            IReadOnlyList<StormCallMarkers.Marker> struck;
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea || conn.ProjectileEpoch != epoch) return;
                struck = conn.StormCall.TakeStrikes(conn.Clock.Elapsed.TotalSeconds, epoch);
            }
            if (struck.Count == 0) return;
            Console.WriteLine($"[gateway] {conn.Remote} storm call strike: {struck.Count} marker(s) "
                + string.Join(", ", struck.Select(m => $"0x{m.Object:x}")));
            await Task.WhenAll(struck.Select(m => StormCallStrike(conn, m, removeMarker: true)));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    // One marker's lightning: the impact art there and the skill's area hit around it, with the cast's critical roll and
    // repeat (Spell Echo's final-repeat damage). A dead player or a gem no longer socketed strikes nothing.
    static async Task StormCallStrike(Player conn,
        StormCallMarkers.Marker marker, bool removeMarker)
    {
        if (removeMarker) await Send(conn, InstanceProtocol.RemoveObject(marker.Object), null);
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == marker.Skill);
        if (marker.Use is not StormCallUse use || !use.Sacrifice.Current || gem.Name is null || !IsStormCall(SkillsFor(conn).EffectId(gem.Effect)) || conn.PlayerLife == 0)
        {
            Console.WriteLine($"[gateway] {conn.Remote} storm call marker 0x{marker.Object:x} strikes nothing (gem gone or player dead)");
            return;
        }
        int radius = SkillRadius(conn, SupportedSkill(conn, marker.Skill).Stats, spell: true, gem.Effect);
        var (_, impact, _) = StormCallArt(conn, gem.Gem);
        if (impact is not null)
        {
            uint effect;
            lock (conn.Monsters) effect = conn.TakeObjectId();
            float scale = AreaArtScale(radius, impact);
            await Send(conn, InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(marker.At.X, marker.At.Y, impact) { Flags0 = EffectObject.FixedRotation, Scale = scale }.Blob()),
                $"0x02bb create effect 0x{effect:x} {impact} at ({marker.At.X}, {marker.At.Y}), scale {scale:0.###} for radius {radius}");
            await Send(conn, InstanceProtocol.EnterWakeZone(effect), null);
            SendAfter(StormCallImpactArtSeconds, async () =>
            {
                if (conn.Closed || conn.LeavingArea || conn.ProjectileEpoch != marker.Epoch) return;
                await Send(conn, InstanceProtocol.RemoveObject(effect), null);
            });
        }
        await AreaSkillHit(conn, marker.Skill, gem, marker.At, marker.At, immediate: true,
            criticalRoll: use.Critical, repeat: marker.Repeat, sacrifice: use.Sacrifice, damageMore: use.DamageMore,
            triggeredCurrent: () => !conn.Closed && !conn.LeavingArea && conn.ProjectileEpoch == marker.Epoch && conn.PlayerLife > 0);
    }
}
