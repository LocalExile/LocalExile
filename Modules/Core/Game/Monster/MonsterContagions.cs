using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadMonsterContagionsData()
    {
        MtxTypeIds = LoadMtxTypeIds();
    }

    /// <summary>BuffDefinitions row 504 <c>contagion</c> ("You are taking Chaos Damage Over Time").</summary>
    internal const ushort ContagionBuffRow = 504;

    /// <summary>Whether the debuff icon is shown on affected monsters (false: the damage and spread only).</summary>
    const bool ContagionBuffShown = false;

    // More multipliers for an area spell's chaos damage over time.
    static readonly uint[] ChaosAreaDotSupportMore =
    [
        Stat.SupportAreaConcentrateAreaDamagePlusPctFinal, Stat.SupportControlledDestructionSpellDamagePlusPctFinal,
        Stat.SupportVoidManipulationChaosDamagePlusPctFinal, Stat.SupportRapidDecayDamageOverTimePlusPctFinal,
        Stat.SupportEfficacySpellDamagePlusPctFinal, Stat.SupportEfficacyDamageOverTimePlusPctFinal,
        Stat.CrueltyDamageOverTimePlusPctFinal,
    ];

    /// <summary>Contagion (GrantedEffects <c>Contagion</c>) or Contagion of Subsiding (<c>ContagionAltX</c>): what one cast
    /// puts on each monster in its area; null for any other skill. Its damage is base_chaos_damage_to_deal_per_minute (the
    /// gem level's resolved value) with the increases a spell's chaos damage over time takes (its implicit
    /// spell_damage_modifiers_apply_to_skill_dot and is_area_damage admit spell and area damage), the supports' more
    /// multipliers and the damage over time multipliers. Transference spreads on hit.</summary>
    internal static MonsterContagion.Cast? ContagionCast(Player player, ushort skillId, int effect)
    {
        string? id = SkillsFor(player).EffectId(effect);
        if (id is not ("Contagion" or "ContagionAltX" or "ContagionAltY")) return null;
        var own = SupportedSkill(player, skillId).Stats;
        var playerStats = CharacterPlayerStats(player);
        var merged = MergeSkillStats(playerStats, own);
        int Get(uint k) => merged.GetValueOrDefault(k);
        int Own(uint k) => own.GetValueOrDefault(k);
        int Player(uint k) => playerStats.GetValueOrDefault(k);

        double perSecond = Get(Stat.BaseChaosDamageToDealPerMinute) / 60.0;
        if (!(perSecond > 0) || DamageStats.Prohibited(DamageType.Chaos, Get)) return null;
        double increased = (double)Get(Stat.DamagePlusPct) + Get(Stat.SpellDamagePlusPct) + Get(Stat.AreaDamagePlusPct) + Get(Stat.ChaosDamagePlusPct)
            + Get(Stat.DamageOverTimePlusPct) + Get(Stat.ChaosDamageOverTimePlusPct);
        double more = ChaosAreaDotSupportMore.Aggregate(1.0, (m, name) => m * Math.Max(0, 1 + Own(name) / 100.0));
        double multiplier = 1 + ((double)Get(Stat.DotMultiplierPlus) + Get(Stat.ChaosDotMultiplierPlus)) / 100.0;
        double dps = perSecond * Math.Max(0, 1 + increased / 100.0) * more * Math.Max(0, multiplier);
        double seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Player, Own);
        int radius = SkillRadius(player, own, spell: true, effect);
        return new(dps, seconds, radius, id == "ContagionAltX" ? 0.75 : 1, skillId, Caster: player)
        {
            SpreadOnHitChance = id == "ContagionAltY" ? Own(Stat.ContagionSpreadOnHitAffectedEnemyPct) : 0,
        };
    }

    /// <summary>A Contagion cast: once the area cast clip reaches its contact (as AreaSkillHit times a spell), every live
    /// hostile monster within the radius of the centre is affected. It has no hit, so it deals nothing until it ticks.</summary>
    internal static async Task CastContagion(Player player, int effect, ushort skillId, (int X, int Y) from, (int X, int Y) to,
        MonsterContagion.Cast cast, int repeat, double radiusMore, int action, bool instant, Func<bool> current)
    {
        int? castMs = CastMs(player, effect, skillId);
        if (castMs is null) return;
        double delay = castMs.Value / 1000.0
            * (repeat + Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject));
        int radius = (int)Math.Round(cast.Radius * radiusMore);
        var centre = SpellCentre(player, effect, from, to);
        await AreaDelay(player, Math.Max(0.05, delay));
        await WhilePaused(player);
        if ((!instant && player.ActionCount != action) || player.Closed || !current()) return;
        int affected = 0;
        using (ProjectileMutation(player))
        {
            double now = ProjectileWorldTime(player);
            foreach (var (_, m) in player.Area.Monsters)
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
    static string[] MtxTypeIds = default!;

    static string[] LoadMtxTypeIds()
    {
        string path = Path.Combine("data", "tables", "MtxTypes.json");
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. doc.RootElement.EnumerateArray()
            .Select(r => r.TryGetProperty("Id", out var id) ? id.GetString() ?? "" : "")];
    }

    /// <summary>The spread art of the Contagion skin worn on the gem (its Cosmetics tab slot), or null for none or a skin
    /// without art of its own.</summary>
    internal static string? ContagionSkinAo(Player player, uint gem)
    {
        try
        {
            foreach (var (key, worn) in CharacterSaves.SkillCosmetics(CharacterName(player)))
            {
                if (CharacterSaves.SkillCosmeticGem(key) != gem || Mtx.Find(worn.Record) is not { } record) continue;
                var ids = MtxTypeIds;
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
    internal static ((int X, int Y) At, int Radius, string Ao)? SpreadContagion(Player player, uint id, SpawnedMonster m)
    {
        int spread = 0;
        (int X, int Y) at;
        MonsterContagion.Cast? cast;
        MonsterEssenceDrain.Cast? drain;
        using (ProjectileMutation(player))
        {
            double now = ProjectileWorldTime(player);
            drain = m.EssenceDrain.TakeSpread(now);
            // PoE1: the spread carries the time the dead monster's Contagion had left; nothing spreads with none left.
            cast = m.Contagion.TakeSpread(now);
            if (cast is null) return null;
            at = m.Walk.Position(now);
            spread = ApplyContagionSpreadLocked(player, m, at, cast, drain, now);
        }
        Console.WriteLine($"[gateway] contagion spread from 0x{id:x} to {spread} monster(s)" + (drain is null ? ""
            : $", with essence drain {drain.DamagePerSecond:0.#}/s for {drain.Seconds:0.##} s"));
        return (at, cast.Radius, cast.SpreadAo ?? ContagionSpreadAo);
    }

    static void ContagionSpreadOnHitLocked(Player player, SpawnedMonster source, double now)
    {
        if (source.Contagion.ForHitSpread(now) is not { } cast || !RandomRolls.Chance(cast.SpreadOnHitChance, 100, AreaRng(player))) return;
        var at = source.Walk.Position(now);
        if (ApplyContagionSpreadLocked(player, source, at, cast, source.EssenceDrain.ForHitSpread(now), now) > 0
            && player.Connection.PlayerSent)
            player.KillShares.Enqueue(_ => ShowContagionSpread(player, at, cast.Radius, cast.SpreadAo ?? ContagionSpreadAo));
    }

    static int ApplyContagionSpreadLocked(Player player, SpawnedMonster source, (int X, int Y) at,
        MonsterContagion.Cast cast, MonsterEssenceDrain.Cast? drain, double now)
    {
        int spread = 0;
        var next = cast with { DamagePerSecond = cast.DamagePerSecond * cast.SpreadMore };
        foreach (var (_, other) in player.Area.MonstersNear(at, cast.Radius))
            if (!ReferenceEquals(other, source) && other is { Hostile: true, Dead: false, Life: > 0, OwnerId: null }
                && Dist(other.Walk.Position(now), at) <= cast.Radius)
            {
                other.Contagion.Apply(now, next);
                if (drain is not null) other.EssenceDrain.ApplySpread(now, drain);
                spread++;
            }
        return spread;
    }

    /// <summary>The player who owns a damage over time's kill: its caster while present in the runner's instance (connected,
    /// not leaving); null when gone. Never the runner merely for running the world.</summary>
    internal static Player? DotOwner(Player runner, Player? caster) =>
        caster is null || caster.Closed || caster.LeavingArea ? null
            : ReferenceEquals(caster, runner) || (caster.Instance is not null && ReferenceEquals(caster.Instance, runner.Instance))
                ? caster : null;

    /// <summary>Plays the Contagion circle (<paramref name="ao"/>: the caster's skin's, else the default) at a spread, sized
    /// to its radius, for <see cref="ContagionSpreadArtSeconds"/>.</summary>
    internal static async Task ShowContagionSpread(Player player,
        (int X, int Y) at, int radius, string ao)
    {
        float scale = (float)(radius * (250.0 / 23) / (Art.ArtRadius(ao) ?? 180));
        // Every player in the instance sees it, each under an object id of its own client and through its own world output:
        // the killer here, the others on their own frame (KillShares, as shared kills are).
        async Task Show(Player viewer)
        {
            uint effect;
            effect = viewer.TakeObjectId();
            await SendWorldCreate(viewer, effect, InstanceEntities.CreateObject(effect,
                InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(at.X, at.Y, ao) { Scale = scale }.Blob()),
                $"0x02b9 create effect 0x{effect:x} contagion spread at ({at.X}, {at.Y}), radius {radius}, art {ao}");
            await SendWorldRecord(viewer, effect, InstanceProtocol.EnterWakeZone(effect), null);
            SendAfter(viewer, ContagionSpreadArtSeconds, async () =>
            {
                if (viewer.Closed || viewer.LeavingArea) return;
                await SendWorldRemoval(viewer, effect, null);
            });
        }
        await Show(player);
        foreach (var peer in player.Instance?.Presence.PlayersExcept(player.Id) ?? [])
            if (peer is { } other && !other.Closed && !other.LeavingArea && other.Connection.PlayerSent)
                other.KillShares.Enqueue(_ => Show(other));
    }

    internal static async Task TickContagions(Player player)
    {
        if (!MonsterContagion.AnyApplied) return;
        var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage, Player? Owner, string Effect)>();
        using (ProjectileMutation(player))
        {
            if (player.Closed || player.LeavingArea) return;
            double now = ProjectileWorldTime(player);
            foreach (var (id, m) in player.Area.Monsters)
            {
                Tick(id, m, m.Contagion, spreads: true);
                if (m.Bane is { } bane)
                {
                    Tick(id, m, bane, spreads: false);
                    if (bane.Idle) m.Bane = null;
                }
            }

            void Tick(uint id, SpawnedMonster m, MonsterContagion dot, bool spreads)
            {
                if (dot.Idle) return;
                int damage = 0;
                if (m.Dead || m.Life == 0) dot.Clear();
                else damage = dot.Advance(now, MonsterDotTaken(m, DamageType.Chaos, now));
                if (damage > 0)
                {
                    uint lifeBefore = m.Life;
                    double fraction = Math.Min(1.0, (double)lifeBefore / damage);
                    // The kill and its owner-only gains (Rampage, Vaal soul, the kill itself) go to the owner of the
                    // application whose damage landed it, never to this connection for running the world.
                    var owner = DotOwner(player, dot.WinnerAt(fraction)?.Caster);
                    (m.Life, bool stageStarts) = owner is null ? TakeDamageLocked(m, damage) : TakeOwnerDamageLocked(owner, m, damage);
                    m.Aggro = m.Life > 0;
                    if (m.Life == 0)
                    {
                        m.Dead = true;
                        m.LastHitBy = owner;
                        if (owner is not null) CollectVaalSoulAtDeathLocked(owner, m);
                        // Its own damage killed it partway through the tick: what affected it then spreads, with the
                        // time it had left then (also when this tick ended it).
                        dot.Died(fraction);
                    }
                    hits.Add((id, m, m.Life, damage, owner, spreads ? "Contagion" : "Bane"));
                    if (stageStarts) CommitBossStage(player, id, m, now);
                }
                if (!spreads) return;
                if (m.Dead || m.Life == 0 || dot.Active is null)
                {
                    EndDot(player, id, ref dot.Buff, dot.BuffSource);
                }
                else if (ContagionBuffShown && dot.Buff is null)
                {
                    // One buff for the debuff's life: shown once with its remaining time, replaced only when it ends.
                    ShowDot(player, id, ref dot.Buff, ref dot.BuffSource, new(ContagionBuffRow, player.Id,
                        dot.Active.SkillId, U68: Buff.Seconds(dot.Until - now)));
                }
            }
        }
        foreach (var (id, m, life, damage, owner, effect) in hits)
        {
            if (life == 0)
                SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
            if (life == 0 && DotDeathKind(m, life) is var deathKind && deathKind != InstanceProtocol.DeathKindNormal)
                await SendWorldRecord(player, id, InstanceProtocol.LifeUpdate(id, life, u2c: deathKind,
                        orderKey: player.Connection.LockstepClient ? uint.MaxValue : 0u),
                    $"0x01b1 monster 0x{id:x} ({m.Label}) died of {effect}");
            else
                Note(life == 0 ? $"0x01b1 monster 0x{id:x} ({m.Label}) died of {effect}" : null);
            // The owner's kill (on its own frame when it is another player); with the owner gone, the death without any
            // player's on-kill effects.
            if (life == 0 && owner is not null) await DotKilled(player, id, m);
            else if (life == 0) await MonsterKilled(player, id, m, byMinion: true);
        }
    }
}
