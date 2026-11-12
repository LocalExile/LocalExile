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

    static (string? Loop, string? Impact, int Variation) StormCallArt(ClientState conn, uint gem)
    {
        int variation = SkillArtVariation(conn, gem);
        return (Art.Animated("storm_call_loop", variation) ?? Art.Animated("storm_call_loop", 0),
            Art.Animated("storm_call_impact", variation) ?? Art.Animated("storm_call_impact", 0), variation);
    }

    public static async Task PlaceStormCall(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) at,
        int repeat, int? actionToken, CooldownUseTicket? cooldownUse, SkillCriticalRoll? criticalRoll, int addedChaos)
    {
        int? castMs = CastMs(conn, gem.Effect, skillId);
        if (castMs is null) return;
        bool TokenCurrent() => cooldownUse?.Current(conn) ?? (actionToken is null || conn.ActionCount == actionToken);
        double delay;
        lock (conn.Monsters)
            delay = castMs.Value / 1000.0 * (repeat + Animations.SpellAreaRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject));
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
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            marker = new(conn.TakeObjectId(), skillId, at, now + duration, repeat, conn.ProjectileEpoch, addedChaos, criticalRoll);
            conn.StormCall.Place(marker);
        }
        Console.WriteLine($"[gateway] {remote} storm call marker 0x{marker.Object:x} at ({at.X}, {at.Y}), repeat {repeat}, "
            + $"strikes in {duration:0.00}s, radius {radius}; skin variation {variation}: art {loop ?? "none"}");
        if (loop is not null)
        {
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(marker.Object,
                    InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(at.X, at.Y, loop) { Flags0 = EffectObject.FixedRotation }.Blob()),
                $"0x02bb create effect 0x{marker.Object:x} {loop} at ({at.X}, {at.Y})");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(marker.Object), null);
        }
        // Quality: a chance that lightning strikes the new marker as it is set; the marker stays for its own strike.
        int chance = Own("storm_call_chance_to_strike_on_cast_%");
        if (chance > 0 && Random.Shared.Next(100) < chance)
        {
            Console.WriteLine($"[gateway] {remote} storm call strike on cast at marker 0x{marker.Object:x} ({chance}% chance)");
            await StormCallStrike(remote, stream, ciphers, conn, marker, removeMarker: false);
        }
        _ = StrikeStormCallsWhenDue(remote, stream, ciphers, conn, marker.StrikeAt, marker.Epoch);
    }

    static async Task StrikeStormCallsWhenDue(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
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
            Console.WriteLine($"[gateway] {remote} storm call strike: {struck.Count} marker(s) "
                + string.Join(", ", struck.Select(m => $"0x{m.Object:x}")));
            await Task.WhenAll(struck.Select(m => StormCallStrike(remote, stream, ciphers, conn, m, removeMarker: true)));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    // One marker's lightning: the impact art there and the skill's area hit around it, with the cast's critical roll and
    // repeat (Spell Echo's final-repeat damage). A dead player or a gem no longer socketed strikes nothing.
    static async Task StormCallStrike(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        StormCallMarkers.Marker marker, bool removeMarker)
    {
        if (removeMarker) await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(marker.Object), null);
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == marker.Skill);
        if (gem.Name is null || !IsStormCall(SkillsFor(conn).EffectId(gem.Effect)) || conn.PlayerLife == 0)
        {
            Console.WriteLine($"[gateway] {remote} storm call marker 0x{marker.Object:x} strikes nothing (gem gone or player dead)");
            return;
        }
        int radius = SkillRadius(conn, SupportedSkill(conn, marker.Skill).Stats, spell: true, gem.Effect);
        var (_, impact, _) = StormCallArt(conn, gem.Gem);
        if (impact is not null)
        {
            uint effect;
            lock (conn.Monsters) effect = conn.TakeObjectId();
            float scale = (float)(radius * (250.0 / 23) / (Art.ArtRadius(impact) ?? 250));
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(marker.At.X, marker.At.Y, impact) { Flags0 = EffectObject.FixedRotation, Scale = scale }.Blob()),
                $"0x02bb create effect 0x{effect:x} {impact} at ({marker.At.X}, {marker.At.Y}), scale {scale:0.###} for radius {radius}");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(effect), null);
            SendAfter(StormCallImpactArtSeconds, async () =>
            {
                if (conn.Closed || conn.LeavingArea || conn.ProjectileEpoch != marker.Epoch) return;
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(effect), null);
            });
        }
        await AreaSkillHit(remote, stream, ciphers, conn, marker.Skill, gem, marker.At, marker.At, immediate: true,
            criticalRoll: marker.Use as SkillCriticalRoll, repeat: marker.Repeat, addedChaos: marker.AddedChaos,
            triggeredCurrent: () => !conn.Closed && !conn.LeavingArea && conn.ProjectileEpoch == marker.Epoch && conn.PlayerLife > 0);
    }
}
