using GameServer.Core.World;

public static partial class Game
{
    static bool IsBane(ClientState conn, int effect) => SkillsFor(conn).EffectId(effect) is "Bane" or "BaneAltX";

    static bool IsBaneLinkedHex(ClientState conn, SupportedGem hex)
    {
        if (hex.SourceGem is null || hex.Host == 0) return false;
        foreach (var source in PlayerSkillSourcesLocked(conn))
            if (source.Active && source.SourceGem is not null && source.Identity.Host == hex.Host && IsBane(conn, source.Effect)
                && BaneSupportsHex(conn, SupportedSkill(conn, source.Id), hex))
                return true;
        return false;
    }

    // Bane's support half (SupportDarkRitual): a linked gem whose types fit its allowed and excluded types (a Hex, not one
    // made a trap, mine, totem or Blasphemy aura) and whose level requirement is no higher than Bane's.
    static bool BaneSupportsHex(ClientState conn, SupportedGem bane, SupportedGem hex)
    {
        var catalog = SkillsFor(conn);
        if (bane.Host == 0 || bane.Group < 0 || hex.Host != bane.Host || hex.Group != bane.Group
            || !bane.RequirementsMet || !hex.RequirementsMet
            || catalog.PlayerLevelReq(hex.Effect, hex.Level) > catalog.PlayerLevelReq(bane.Effect, bane.Level)
            || Supports.Of(BaneSupportRow(conn, bane)) is not { } support || Supports.Of(hex.Effect) is not { } target) return false;
        var types = target.Types.Concat(hex.Supports.SelectMany(s => s.Effect.Added)).ToHashSet();
        return GemSupports.Compatible(support, types, [], null);
    }

    static int BaneSupportRow(ClientState conn, SupportedGem bane) => SkillsFor(conn).EffectRow(
        SkillsFor(conn).EffectId(bane.Effect) == "BaneAltX" ? "SupportDarkRitualAltX" : "SupportDarkRitual");

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
        // The support half's stats at Bane's level and quality: its less effect, and its quality's increased curse effect.
        var support = SkillsFor(conn).EffectStats(BaneSupportRow(conn, gem), gem.Level, gem.Quality)
            .GroupBy(s => s.Item1).ToDictionary(g => g.Key, g => g.Sum(s => s.Item2));
        int Support(string name) => GolemStat(support, name);
        int addedEffect = Get("dark_ritual_linked_curse_effect_+%") + Support("curse_effect_+%");
        double curseMultiplier = Math.Max(0, 1 + Support("support_bane_curse_effect_+%_final") / 100d);
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
        if (bane.Host == 0 || bane.Group < 0) return result;
        var catalog = SkillsFor(conn);
        foreach (var source in PlayerSkillSourcesLocked(conn).Where(s => s.Active && s.SourceGem is not null
            && s.Identity.Host == bane.Host).OrderBy(s => s.Identity.Socket))
        {
            if (catalog.EffectId(source.Effect) is not { } effect || !CurseCatalog.Hex(effect) || result.Any(r => r.Item3 == effect))
                continue;
            var linked = SupportedSkill(conn, source.Id);
            if (GolemStat(linked.Stats, "cannot_cast_curses") == 0 && BaneSupportsHex(conn, bane, linked))
                result.Add((source.Id, linked, effect));
        }
        return result;
    }
}
