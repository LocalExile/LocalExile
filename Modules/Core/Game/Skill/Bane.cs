using GameServer.Core.World;

public static partial class Game
{
    static bool IsBaneEffect(string? effect) => effect is "Bane" or "BaneAltX";

    static bool IsBane(Player player, int effect) => IsBaneEffect(SkillsFor(player).EffectId(effect));

    static bool IsBaneLinkedHex(Player player, SupportedGem hex)
    {
        if (hex.SourceGem is null || hex.Host == 0) return false;
        foreach (var source in PlayerSkillSourcesLocked(player))
            if (source.Active && source.SourceGem is not null && source.Identity.Host == hex.Host && IsBane(player, source.Effect)
                && BaneSupportsHex(player, SupportedSkill(player, source.Id), hex))
                return true;
        return false;
    }

    // Bane's support half (SupportDarkRitual): a linked gem whose types fit its allowed and excluded types (a Hex, not one
    // made a trap, mine, totem or Blasphemy aura) and whose level requirement is no higher than Bane's.
    static bool BaneSupportsHex(Player player, SupportedGem bane, SupportedGem hex)
    {
        var catalog = SkillsFor(player);
        if (bane.Host == 0 || bane.Group < 0 || hex.Host != bane.Host || hex.Group != bane.Group
            || !bane.RequirementsMet || !hex.RequirementsMet
            || catalog.PlayerLevelReq(hex.Effect, hex.Level) > catalog.PlayerLevelReq(bane.Effect, bane.Level)
            || Supports.Of(BaneSupportRow(player, bane)) is not { } support || Supports.Of(hex.Effect) is not { } target) return false;
        var types = target.Types.Concat(hex.Supports.SelectMany(s => s.Effect.Added)).ToHashSet();
        return GemSupports.Compatible(support, types, [], null);
    }

    static int BaneSupportRow(Player player, SupportedGem bane) => SkillsFor(player).EffectRow(
        SkillsFor(player).EffectId(bane.Effect) == "BaneAltX" ? "SupportDarkRitualAltX" : "SupportDarkRitual");

    static async Task CastBane(Player player, ushort skill, SupportedGem gem, (int X, int Y) from, (int X, int Y) to,
        int repeat, double radiusMore, Func<bool> current)
    {
        if (CastMs(player, gem.Effect, skill) is not int milliseconds) return;
        double delay = milliseconds / 1000d
            * (repeat + Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject));
        double contact = AreaTime(player) + delay;
        while (player.Paused || AreaTime(player) < contact)
        {
            if (player.Closed || player.LeavingArea || !current()) return;
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        {
            var live = SupportedSkill(player, skill);
            if (player.Closed || player.LeavingArea || !current() || !SameLinkedSkillSource(gem, live)) return;
            BaneContactLocked(player, skill, live, SpellCentre(player, gem.Effect, from, to), radiusMore, ProjectileWorldTime(player));
        }
    }

    internal static void BaneContactLocked(Player player, ushort skill, SupportedGem gem, (int X, int Y) centre,
        double radiusMore, double now)
    {
        var playerStats = CharacterPlayerStats(player);
        int Own(uint key) => GolemStat(gem.Stats, key);
        int PlayerStat(uint key) => GolemStat(playerStats, key);
        int Get(uint key) => Own(key) + PlayerStat(key);
        double baseDps = Own(Stat.BaseChaosDamageToDealPerMinute) / 60d;
        double increased = (double)Get(Stat.DamagePlusPct) + Get(Stat.SpellDamagePlusPct) + Get(Stat.AreaDamagePlusPct) + Get(Stat.ChaosDamagePlusPct)
            + Get(Stat.DamageOverTimePlusPct) + Get(Stat.ChaosDamageOverTimePlusPct) + Get(Stat.DarkRitualDamagePlusPct);
        double more = ChaosAreaDotSupportMore.Aggregate(1d, (m, key) => m * Math.Max(0, 1 + Own(key) / 100d));
        double dps = DamageStats.Prohibited(DamageType.Chaos, Get) ? 0 : baseDps * Math.Max(0, 1 + increased / 100d)
            * more * Math.Max(0, 1 + ((double)Get(Stat.DotMultiplierPlus) + Get(Stat.ChaosDotMultiplierPlus)) / 100d)
            * GhorrDotMore(player, skill);
        int radius = (int)Math.Round(SkillRadius(player, gem.Stats, spell: true, gem.Effect) * radiusMore);
        var hexes = BaneHexes(player, gem);
        // The support half's stats at Bane's level and quality: its less effect, and its quality's increased curse effect.
        var support = SkillsFor(player).EffectStats(BaneSupportRow(player, gem), gem.Level, gem.Quality)
            .GroupBy(s => s.Item1).ToDictionary(g => g.Key, g => g.Sum(s => s.Item2));
        int Support(uint key) => GolemStat(support, key);
        int addedEffect = Get(Stat.DarkRitualLinkedCurseEffectPlusPct) + Support(Stat.CurseEffectPlusPct);
        double curseMultiplier = Math.Max(0, 1 + Support(Stat.SupportBaneCurseEffectPlusPctFinal) / 100d);
        foreach (var monster in player.Area.Monsters.Values)
        {
            if (!monster.Hostile || monster.Dead || monster.Life == 0 || monster.OwnerId is not null
                || Dist(monster.Walk.Position(now), centre) > radius) continue;

            int count = 0;
            foreach (var (id, hex, _) in hexes)
                ApplyCurseLocked(player, monster, id, now, false, hex, playerStats, addedEffect, curseMultiplier);
            foreach (var (id, _, effect) in hexes)
                if (monster.Curses.All.TryGetValue((player.Id, effect), out var applied)
                    && applied.Skill == id && applied.Applied == now && !applied.Aura) count++;

            double damage = dps * Math.Max(0, 1 + count * Own(Stat.DarkRitualDamagePlusPctFinalPerCurseApplied) / 100d);
            int ExtraDuration(uint key) => Own(key) + (key == SkillDurationModifiers.IncreasedDuration
                ? count * Own(Stat.DarkRitualSkillEffectDurationPlusPctPerCurseApplied) : 0);
            double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), PlayerStat, ExtraDuration);
            if (damage > 0 && duration > 0)
                (monster.Bane ??= new()).Apply(now, new(damage, duration, radius, SpreadMore: 0, SkillId: skill, Caster: player));
        }
    }

    static List<(ushort Id, SupportedGem Hex, string Effect)> BaneHexes(Player player, SupportedGem bane)
    {
        var result = new List<(ushort, SupportedGem, string)>();
        if (bane.Host == 0 || bane.Group < 0) return result;
        var catalog = SkillsFor(player);
        foreach (var source in PlayerSkillSourcesLocked(player).Where(s => s.Active && s.SourceGem is not null
            && s.Identity.Host == bane.Host).OrderBy(s => s.Identity.Socket))
        {
            if (CurseCatalog.HexFamily(catalog.EffectId(source.Effect)) is not { } effect || result.Any(r => r.Item3 == effect))
                continue;
            var linked = SupportedSkill(player, source.Id);
            if (GolemStat(linked.Stats, Stat.CannotCastCurses) == 0 && BaneSupportsHex(player, bane, linked))
                result.Add((source.Id, linked, effect));
        }
        return result;
    }
}
