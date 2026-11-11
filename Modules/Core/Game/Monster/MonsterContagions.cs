using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    /// <summary>BuffDefinitions row 504 <c>contagion</c> ("You are taking Chaos Damage Over Time").</summary>
    internal const ushort ContagionBuffRow = 504;

    /// <summary>Whether the debuff icon is shown on affected monsters (false: the damage and spread only).</summary>
    const bool ContagionBuffShown = false;

    // The "more" support stats that scale Contagion's damage: an area spell's chaos damage over time.
    static readonly string[] ContagionSupportMore =
    [
        "support_area_concentrate_area_damage_+%_final", "support_controlled_destruction_spell_damage_+%_final",
        "support_void_manipulation_chaos_damage_+%_final", "support_rapid_decay_damage_over_time_+%_final",
        "support_efficacy_spell_damage_+%_final", "support_efficacy_damage_over_time_+%_final",
    ];

    /// <summary>Contagion (GrantedEffects <c>Contagion</c>) or Contagion of Subsiding (<c>ContagionAltX</c>): what one cast
    /// puts on each monster in its area; null for any other skill. Its damage is base_chaos_damage_to_deal_per_minute (the
    /// gem level's resolved value) with the increases a spell's chaos damage over time takes (its implicit
    /// spell_damage_modifiers_apply_to_skill_dot and is_area_damage admit spell and area damage), the supports' more
    /// multipliers and the damage over time multipliers. Contagion of Transference (spread on hit) is not supported yet.</summary>
    internal static MonsterContagion.Cast? ContagionCast(ClientState conn, ushort skillId, int effect)
    {
        string? id = SkillsFor(conn).EffectId(effect);
        if (id is not ("Contagion" or "ContagionAltX")) return null;
        var own = SupportedSkill(conn, skillId).Stats;
        var player = CharacterPlayerStats(conn);
        var merged = MergeSkillStats(player, own);
        int Get(string name) => Stats.Key(name) is uint k ? merged.GetValueOrDefault(k) : 0;
        int Own(string name) => Stats.Key(name) is uint k ? own.GetValueOrDefault(k) : 0;
        int Player(string name) => Stats.Key(name) is uint k ? player.GetValueOrDefault(k) : 0;

        double perSecond = Get("base_chaos_damage_to_deal_per_minute") / 60.0;
        if (!(perSecond > 0) || DamageStats.Prohibited(DamageType.Chaos, Get)) return null;
        double increased = (double)Get("damage_+%") + Get("spell_damage_+%") + Get("area_damage_+%") + Get("chaos_damage_+%")
            + Get("damage_over_time_+%") + Get("chaos_damage_over_time_+%");
        double more = ContagionSupportMore.Aggregate(1.0, (m, name) => m * Math.Max(0, 1 + Own(name) / 100.0));
        double multiplier = 1 + ((double)Get("dot_multiplier_+") + Get("chaos_dot_multiplier_+")) / 100.0;
        double dps = perSecond * Math.Max(0, 1 + increased / 100.0) * more * Math.Max(0, multiplier);
        double seconds = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player, Own);
        int radius = SkillRadius(conn, own, spell: true, effect);
        return new(dps, seconds, radius, id == "ContagionAltX" ? 0.75 : 1, skillId, Caster: conn);
    }

    /// <summary>A Contagion cast: once the area cast clip reaches its contact (as AreaSkillHit times a spell), every live
    /// hostile monster within the radius of the centre is affected. It has no hit, so it deals nothing until it ticks.</summary>
    internal static async Task CastContagion(ClientState conn, int effect, ushort skillId, (int X, int Y) from, (int X, int Y) to,
        MonsterContagion.Cast cast, int repeat, double radiusMore, int action, bool instant, Func<bool> current)
    {
        int? castMs = CastMs(conn, effect, skillId);
        if (castMs is null) return;
        double delay = castMs.Value / 1000.0
            * (repeat + Animations.SpellAreaRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject));
        int radius = (int)Math.Round(cast.Radius * radiusMore);
        var centre = SpellCentre(conn, effect, from, to);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.05, delay)));
        await WhilePaused(conn);
        if ((!instant && conn.ActionCount != action) || conn.Closed || !current()) return;
        int affected = 0;
        using (ProjectileMutation(conn))
        {
            double now = ProjectileWorldTime(conn);
            foreach (var (_, m) in conn.Monsters)
                if (m.Hostile && !m.Dead && m.Life > 0 && m.OwnerId is null && Dist(m.Walk.Position(now), centre) <= radius)
                {
                    m.Contagion.Apply(now, cast with { Radius = radius });
                    affected++;
                }
        }
        Console.WriteLine($"[gateway] contagion: {affected} monster(s) within {radius} of ({centre.X}, {centre.Y}), " +
            $"{cast.DamagePerSecond:0.#}/s for {cast.Seconds:0.##} s");
    }

    /// <summary>The art played where a Contagion spreads from: MiscAnimated <c>ContagionCast</c>, the cast's own circle,
    /// drawn for a radius of 180 world units, so it is scaled to the spread radius as Righteous Fire's ring is.</summary>
    internal const string ContagionSpreadAo = "Metadata/Effects/Spells/contagion/contagion_main.ao";
    const double ContagionSpreadArtSeconds = 1.5;

    // The Contagion skins' cast circles (MiscAnimated ContagionCast<Skin>), by the word in the skin's MtxTypes Id. No art
    // variation table links these skins to their art (SkillArtVariations has no contagion row), so the name picks it.
    // Skins with no MiscAnimated row (Delirium, King in the Mists) keep the default.
    static readonly (string Skin, string Ao)[] ContagionSkinArt =
    [
        ("Arcane", "Metadata/Effects/Microtransactions/Spells/arcane_contagion/arcane_contagion_main.ao"),
        ("Celestial", "Metadata/Effects/Microtransactions/Spells/contagion_celestial/aoe_main.ao"),
        ("Sunprism", "Metadata/Effects/Microtransactions/Spells/SunprismContagion/sunprism_contagion_main.ao"),
        ("Scientist", "Metadata/Effects/Microtransactions/Spells/ScientistContagion/contagion_main.ao"),
        ("Project", "Metadata/Effects/Microtransactions/Spells/contagion/project/contagion_main.ao"),
    ];

    // MtxTypes Ids in row order (an owned record's Id is its row + 1).
    static readonly Lazy<string[]> MtxTypeIds = new(() =>
    {
        string path = Path.Combine("data", "tables", "MtxTypes.json");
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. doc.RootElement.EnumerateArray()
            .Select(r => r.TryGetProperty("Id", out var id) ? id.GetString() ?? "" : "")];
    });

    /// <summary>The spread art of the Contagion skin worn on the gem (its Cosmetics tab slot), or null for none or a skin
    /// without art of its own.</summary>
    internal static string? ContagionSkinAo(ClientState conn, uint gem)
    {
        try
        {
            foreach (var (key, worn) in CharacterSaves.SkillCosmetics(CharacterName(conn)))
            {
                if (CharacterSaves.SkillCosmeticGem(key) != gem || Mtx.Find(worn.Record) is not { } record) continue;
                var ids = MtxTypeIds.Value;
                string type = record.Id >= 1 && record.Id <= ids.Length ? ids[record.Id - 1] : "";
                if (!type.Contains("Contagion", StringComparison.Ordinal)) continue;
                foreach (var (skin, ao) in ContagionSkinArt)
                    if (type.Contains(skin, StringComparison.Ordinal))
                    {
                        Console.WriteLine($"[gateway] contagion skin {type}: spread art {ao}");
                        return ao;
                    }
                Console.WriteLine($"[gateway] contagion skin {type} has no spread art; default");
            }
        }
        catch (Exception e) { Console.WriteLine($"[gateway] contagion skin lookup failed: {e.Message}"); }
        return null;
    }

    /// <summary>A monster died: if it was affected by Contagion with time left, that Contagion goes on every other live hostile
    /// monster within the cast's radius of where it fell, for the time it had left (Subsiding's at three quarters of the
    /// damage), and with it the monster's Essence Drain debuff, at its full damage for the time it had left ("The debuff is
    /// spread by Contagion"). Returns where it spread from, the radius and the art; null when the monster was not affected.</summary>
    internal static ((int X, int Y) At, int Radius, string Ao)? SpreadContagion(ClientState conn, uint id, SpawnedMonster m)
    {
        int spread = 0;
        (int X, int Y) at;
        MonsterContagion.Cast? cast;
        MonsterEssenceDrain.Cast? drain;
        using (ProjectileMutation(conn))
        {
            double now = ProjectileWorldTime(conn);
            drain = m.EssenceDrain.TakeSpread(now);
            // PoE1: the spread carries the time the dead monster's Contagion had left; nothing spreads with none left.
            cast = m.Contagion.TakeSpread(now);
            if (cast is null) return null;
            at = m.Walk.Position(now);
            var next = cast with { DamagePerSecond = cast.DamagePerSecond * cast.SpreadMore };
            foreach (var (otherId, other) in conn.Monsters)
                if (otherId != id && other.Hostile && !other.Dead && other.Life > 0 && other.OwnerId is null
                    && Dist(other.Walk.Position(now), at) <= cast.Radius)
                {
                    other.Contagion.Apply(now, next);
                    if (drain is not null) other.EssenceDrain.Apply(now, drain);
                    spread++;
                }
        }
        Console.WriteLine($"[gateway] contagion spread from 0x{id:x} to {spread} monster(s)" + (drain is null ? ""
            : $", with essence drain {drain.DamagePerSecond:0.#}/s for {drain.Seconds:0.##} s"));
        return (at, cast.Radius, cast.SpreadAo ?? ContagionSpreadAo);
    }

    /// <summary>The player who owns a damage over time's kill: its caster while present in the runner's instance (connected,
    /// not leaving); null when gone. Never the runner merely for running the world.</summary>
    internal static ClientState? DotOwner(ClientState runner, ClientState? caster) =>
        caster is null || caster.Closed || caster.LeavingArea ? null
            : ReferenceEquals(caster, runner) || (caster.Instance is not null && ReferenceEquals(caster.Instance, runner.Instance))
                ? caster : null;

    /// <summary>Plays the Contagion circle (<paramref name="ao"/>: the caster's skin's, else the default) at a spread, sized
    /// to its radius, for <see cref="ContagionSpreadArtSeconds"/>.</summary>
    internal static async Task ShowContagionSpread(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) at, int radius, string ao)
    {
        float scale = (float)(radius * (250.0 / 23) / (Art.ArtRadius(ao) ?? 180));
        // Every player in the instance sees it, each under an object id of its own client and through its own world output:
        // the killer here, the others on their own frame (KillShares, as shared kills are).
        async Task Show(string r, NetworkStream s, ConnectionCiphers c, ClientState viewer)
        {
            uint effect;
            lock (viewer.Monsters) effect = viewer.NextObjectId++;
            await SendWorldCreate(r, s, c, viewer, effect, InstanceEntities.CreateObject(effect,
                InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(at.X, at.Y, ao) { Scale = scale }.Blob()),
                $"0x02b9 create effect 0x{effect:x} contagion spread at ({at.X}, {at.Y}), radius {radius}, art {ao}");
            await SendWorldRecord(r, s, c, viewer, effect, InstanceProtocol.EnterWakeZone(effect), null);
            SendAfter(ContagionSpreadArtSeconds, async () =>
            {
                if (viewer.Closed || viewer.LeavingArea) return;
                await SendWorldRemoval(r, s, c, viewer, effect, null);
            });
        }
        await Show(remote, stream, ciphers, conn);
        foreach (var peer in conn.Instance?.Presence.PlayersExcept(conn.PeerId) ?? [])
            if (peer.Owner is { } other && !other.Closed && !other.LeavingArea && other.PlayerSent)
                other.KillShares.Enqueue((r, s, c) => Show(r, s, c, other));
    }

    internal static async Task TickContagions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage, ClientState? Owner)>();
        var ended = new List<(uint Id, ushort Buff)>();
        var updated = new List<(uint Id, ushort Buff, ushort Skill, double Remaining)>();
        using (ProjectileMutation(conn))
        {
            if (conn.Closed || conn.LeavingArea) return;
            double now = ProjectileWorldTime(conn);
            foreach (var (id, m) in conn.Monsters)
            {
                if (m.Contagion.Idle) continue;
                int damage = 0;
                if (m.Dead || m.Life == 0) m.Contagion.Clear();
                else damage = m.Contagion.Advance(now, MonsterDotTaken(m, DamageType.Chaos, now));
                if (damage > 0)
                {
                    uint lifeBefore = m.Life;
                    double fraction = Math.Min(1.0, (double)lifeBefore / damage);
                    // The kill and its owner-only gains (Rampage, Vaal soul, the kill itself) go to the owner of the
                    // application whose damage landed it, never to this connection for running the world.
                    var owner = DotOwner(conn, m.Contagion.WinnerAt(fraction)?.Caster);
                    (m.Life, bool stageStarts) = owner is null ? TakeDamageLocked(m, damage) : TakeOwnerDamageLocked(owner, m, damage);
                    m.Aggro = m.Life > 0;
                    if (m.Life == 0)
                    {
                        m.Dead = true;
                        m.LastHitBy = owner;
                        if (owner is not null) CollectVaalSoulAtDeathLocked(owner, m);
                        // Its own damage killed it partway through the tick: what affected it then spreads, with the
                        // time it had left then (also when this tick ended it).
                        m.Contagion.Died(fraction);
                    }
                    hits.Add((id, m, m.Life, damage, owner));
                    if (stageStarts) CommitBossStage(remote, stream, ciphers, conn, id, m, now);
                }
                if (m.Dead || m.Life == 0 || m.Contagion.Active is null)
                {
                    if (m.Contagion.Buff is ushort gone)
                    {
                        ended.Add((id, gone));
                        m.Contagion.Buff = null;
                    }
                }
                else if (ContagionBuffShown && m.Contagion.Buff is null)
                {
                    // One buff for the debuff's life: shown once with its remaining time, replaced only when it ends.
                    ushort next = conn.NextBuff++;
                    m.Contagion.Buff = next;
                    updated.Add((id, next, m.Contagion.Active.SkillId, m.Contagion.Until - now));
                }
            }
        }
        foreach (var (id, buff) in ended)
        {
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.RemoveBuff(id, buff), null);
            ToOthers(conn, id, InstanceEntities.RemoveBuff(id, buff));
        }
        foreach (var (id, buff, skill, remaining) in updated)
            await SendWorldRecord(remote, stream, ciphers, conn, id,
                InstanceEntities.AddBuff(id, buff, ContagionBuffRow, PlayerId, skill,
                    u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, remaining))),
                $"0x01c1 monster 0x{id:x} contagion buff 0x{buff:x} from skill 0x{skill:x4}, {remaining:0.##} s");
        foreach (var (id, m, life, damage, owner) in hits)
        {
            if (life == 0)
                await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]), null);
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.LifeUpdate(id, life),
                life == 0 ? $"0x01b1 monster 0x{id:x} ({m.Label}) died of contagion" : null);
            // The owner's kill (on its own frame when it is another player); with the owner gone, the death without any
            // player's on-kill effects.
            if (life == 0 && owner is not null) await DotKilled(remote, stream, ciphers, conn, id, m);
            else if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, id, m, byMinion: true);
        }
    }
}
