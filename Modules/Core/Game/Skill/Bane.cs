using GameServer.Core.World;

public static partial class Game
{
    static bool IsBane(ClientState conn, int effect) => SkillsFor(conn).EffectId(effect) is "Bane" or "BaneAltX";

    static bool IsBaneLinkedHex(ClientState conn, SupportedGem hex)
    {
        if (hex.SourceGem is null || hex.Host == 0 || hex.Group < 0 || hex.Supports.Any(s => IsBlasphemySupport(s.Effect.Id)))
            return false;
        var catalog = SkillsFor(conn);
        int requirement = catalog.PlayerLevelReq(hex.Effect, ItemsFor(conn).GemLevel(hex.SourceGem));
        foreach (var source in PlayerSkillSourcesLocked(conn))
        {
            if (!source.Active || source.SourceGem is null || source.Identity.Host != hex.Host || !IsBane(conn, source.Effect)) continue;
            var bane = SupportedSkill(conn, source.Id);
            if (bane.Group == hex.Group && bane.RequirementsMet && requirement <= catalog.PlayerLevelReq(bane.Effect, bane.Level))
                return true;
        }
        return false;
    }

    static async Task CastBane(ClientState conn, ushort skill, SupportedGem gem, (int X, int Y) from, (int X, int Y) to,
        int repeat, double radiusMore, Func<bool> current)
    {
        if (CastMs(conn, gem.Effect, skill) is not int milliseconds) return;
        double delay = milliseconds / 1000d
            * (repeat + Animations.SpellAreaRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject));
        double contact = AreaTime(conn) + delay;
        while (conn.Paused || AreaTime(conn) < contact)
        {
            if (conn.Closed || conn.LeavingArea || !current()) return;
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        using (ProjectileMutation(conn))
        {
            var live = SupportedSkill(conn, skill);
            if (conn.Closed || conn.LeavingArea || !current() || !SameSocketedSkillSource(gem, live)) return;
            BaneContactLocked(conn, skill, live, SpellCentre(conn, gem.Effect, from, to), radiusMore, ProjectileWorldTime(conn));
        }
    }

    internal static void BaneContactLocked(ClientState conn, ushort skill, SupportedGem gem, (int X, int Y) centre,
        double radiusMore, double now)
    {
        var player = CharacterPlayerStats(conn);
        int Own(string name) => GolemStat(gem.Stats, name);
        int Player(string name) => GolemStat(player, name);
        int Get(string name) => Own(name) + Player(name);
        double baseDps = Own("base_chaos_damage_to_deal_per_minute") / 60d;
        double increased = (double)Get("damage_+%") + Get("spell_damage_+%") + Get("area_damage_+%") + Get("chaos_damage_+%")
            + Get("damage_over_time_+%") + Get("chaos_damage_over_time_+%") + Get("dark_ritual_damage_+%");
        double more = ChaosAreaDotSupportMore.Aggregate(1d, (m, name) => m * Math.Max(0, 1 + Own(name) / 100d));
        double dps = DamageStats.Prohibited(DamageType.Chaos, Get) ? 0 : baseDps * Math.Max(0, 1 + increased / 100d)
            * more * Math.Max(0, 1 + ((double)Get("dot_multiplier_+") + Get("chaos_dot_multiplier_+")) / 100d);
        int radius = (int)Math.Round(SkillRadius(conn, gem.Stats, spell: true, gem.Effect) * radiusMore);
        var hexes = BaneHexes(conn, gem);
        int addedEffect = Own("dark_ritual_linked_curse_effect_+%");
        var catalog = SkillsFor(conn);
        int support = catalog.EffectRow(catalog.EffectId(gem.Effect) == "BaneAltX" ? "SupportDarkRitualAltX" : "SupportDarkRitual");
        int curseEffect = Stats.Key("support_bane_curse_effect_+%_final") is uint key
            ? catalog.EffectStats(support, gem.Level).FirstOrDefault(s => s.Item1 == key).Item2 : 0;
        double curseMultiplier = Math.Max(0, 1 + curseEffect / 100d);
        foreach (var monster in conn.Monsters.Values)
        {
            if (!monster.Hostile || monster.Dead || monster.Life == 0 || monster.OwnerId is not null
                || Dist(monster.Walk.Position(now), centre) > radius) continue;

            int count = 0;
            foreach (var (id, hex, _) in hexes)
                ApplyCurseLocked(conn, monster, id, now, false, hex, player, addedEffect, curseMultiplier);
            foreach (var (id, _, effect) in hexes)
                if (monster.Curses.All.TryGetValue((conn.PeerId, effect), out var applied)
                    && applied.Skill == id && applied.Applied == now && !applied.Aura) count++;

            double damage = dps * Math.Max(0, 1 + count * Own("dark_ritual_damage_+%_final_per_curse_applied") / 100d);
            int ExtraDuration(string name) => Own(name) + (name == SkillDurationModifiers.IncreasedDuration
                ? count * Own("dark_ritual_skill_effect_duration_+%_per_curse_applied") : 0);
            double duration = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player, ExtraDuration);
            if (damage > 0 && duration > 0)
                (monster.Bane ??= new()).Apply(now, new(damage, duration, radius, SpreadMore: 0, SkillId: skill, Caster: conn));
        }
    }

    static List<(ushort Id, SupportedGem Hex, string Effect)> BaneHexes(ClientState conn, SupportedGem bane)
    {
        var result = new List<(ushort, SupportedGem, string)>();
        int levelCap = SkillsFor(conn).PlayerLevelReq(bane.Effect, bane.Level);
        if (bane.Host == 0 || bane.Group < 0 || levelCap == 0) return result;
        var catalog = SkillsFor(conn);
        foreach (var source in PlayerSkillSourcesLocked(conn).Where(s => s.Active && s.SourceGem is not null
            && s.Identity.Host == bane.Host).OrderBy(s => s.Identity.Socket))
        {
            if (catalog.EffectId(source.Effect) is not { } effect || !CurseCatalog.Hex(effect)
                || catalog.PlayerLevelReq(source.Effect, source.Level) > levelCap) continue;
            var linked = SupportedSkill(conn, source.Id);
            if (linked.Group != bane.Group || !linked.RequirementsMet || GolemStat(linked.Stats, "cannot_cast_curses") != 0
                || linked.Supports.Any(s => IsBlasphemySupport(s.Effect.Id)) || result.Any(r => r.Item3 == effect)) continue;
            result.Add((source.Id, linked, effect));
        }
        return result;
    }
}
