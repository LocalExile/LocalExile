using System.Runtime.CompilerServices;

public static partial class Game
{
    /// <summary>
    /// Cospri's Malice: "Trigger a Socketed Cold Spell on Melee Critical Strike, with a 0.25 second Cooldown".
    /// Its mod CastSocketedColdSkillsOnCriticalStrikeUnique__1 prints Stats row 4984 (0-based;
    /// Stat.LocalUniqueCastSocketedColdSkillsOnMeleeCriticalStrike = 4985) and grants GrantedEffectsPerLevel 46904 =
    /// SupportUniqueCosprisMaliceColdSpellsCastOnMeleeCriticalStrike, which GemSupports.ItemCandidates already
    /// applies to every compatible gem socketed in the sword, independently of links. Only the release was missing:
    /// the player's own melee critical strike rotates over those spells, one a strike, each paying and keeping its
    /// own cooldown, and the attack carries no support of its own.
    /// </summary>
    public static LinkedTriggerSource? CaptureItemCriticalTriggerSource(Player player, ushort skill)
    {
        var gem = SupportedSkill(player, skill);
        if (!SkillsFor(player).IsAttack(gem.Effect) || !SkillsFor(player).IsMelee(gem.Effect)
            || PlacedKind(gem) is not (false, false) || GolemStat(gem.Stats, Stat.IsTotem) != 0
            || MeleeWeaponItem(player) is not uint weapon) return null;
        var spells = CaptureItemTriggeredSpells(player, weapon, Stat.UniqueCosprisMaliceColdSpellsTriggered);
        return spells.Length == 0 ? null : new(skill, gem, spells, player.ProjectileEpoch, CombatLifetime);
    }

    /// <summary>The item a melee hit of this player is made with: the main hand, the only hand the melee path
    /// swings with, and only while the item's own requirements are met.</summary>
    static uint? MeleeWeaponItem(Player player)
    {
        foreach (var (row, item) in ItemsOf(player).EquipmentSnapshot())
            if (row == PlayerInventories.Weapon1 && !IsInactive(player, item.Id)) return item.Id;
        return null;
    }

    /// <summary>The executable spells an item's own trigger support releases: every gem in that item it supports,
    /// independently of links (an item support is not in a socket group).</summary>
    static LinkedSpell[] CaptureItemTriggeredSpells(Player player, uint host, uint spellFlag) =>
        [.. GemSkillEffects(player).Select(s => new LinkedSpell(s.Id, SupportedSkill(player, s.Id)))
            .Where(s => s.Gem.Host == host && TriggeredSpellExecutable(player, s.Skill, s.Gem)
                && s.Gem.Supports.Any(trigger => SupportHas(trigger, spellFlag)))];

    // Called only after attack accuracy, block and critical confirmation, as ReleaseCriticalTrigger is.
    public static bool ReleaseItemCriticalTrigger(Player player, ProjectileSession session, LinkedTriggerSource? source,
        bool critical, bool hostile, (int X, int Y) aim, double at)
    {
        if (!critical || !hostile || source is null || !LinkedSourceCurrent(player, source) || !session.Active) return false;
        // The rotation belongs to the item that triggers, not to the attack's own socket group: the same sword keeps
        // its place in the cycle when the melee attack that crits is a different skill.
        return ReleaseLinkedSpell(player, session, source, "Cospri", aim, at, Stat.UniqueCosprisMaliceColdSpellsTriggered,
            rotation: (source.Spells[0].Gem.Host, -1));
    }
}
