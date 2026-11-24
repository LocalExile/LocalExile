public static partial class Game
{
    /// <summary>
    /// Cospri's Malice: "Trigger a Socketed Cold Spell on Melee Critical Strike, with a 0.25 second Cooldown".
    /// Its mod CastSocketedColdSkillsOnCriticalStrikeUnique__1 prints Stats row 4984 (0-based;
    /// Stat.LocalUniqueCastSocketedColdSkillsOnMeleeCriticalStrike = 4985) and grants GrantedEffectsPerLevel 46904 =
    /// SupportUniqueCosprisMaliceColdSpellsCastOnMeleeCriticalStrike, which GemSupports.ItemCandidates already
    /// applies to every compatible gem socketed in the sword, independently of links. Only the release was missing:
    /// the player's own melee critical strike rotates over those spells, one a strike per swinging sword, each paying
    /// and keeping its own cooldown, and the attack carries no support of its own.
    /// </summary>
    public static LinkedTriggerSource[]? CaptureItemCriticalTriggerSource(Player player, ushort skill,
        AttackHand hand = AttackHand.Main)
    {
        uint? mainWeapon = null, offWeapon = null;
        foreach (var (row, item) in ItemsOf(player).EquipmentSnapshot())
        {
            if (row == PlayerInventories.Weapon1 && hand != AttackHand.Off
                && HasModStat(player, item.Item, Stat.LocalUniqueCastSocketedColdSkillsOnMeleeCriticalStrike)
                && !IsInactive(player, item.Id))
                mainWeapon = item.Id;
            else if (row == PlayerInventories.Offhand1 && hand != AttackHand.Main
                && HasModStat(player, item.Item, Stat.LocalUniqueCastSocketedColdSkillsOnMeleeCriticalStrike)
                && !IsInactive(player, item.Id))
                offWeapon = item.Id;
        }
        if (mainWeapon is null && offWeapon is null) return null;
        var gem = SupportedSkill(player, skill);
        if (!SkillsFor(player).IsAttack(gem.Effect) || !SkillsFor(player).IsMelee(gem.Effect)
            || PlacedKind(gem) is not (false, false) || GolemStat(gem.Stats, Stat.IsTotem) != 0)
            return null;
        var mainSpells = mainWeapon is uint m ? CaptureItemTriggeredSpells(player, m, Stat.UniqueCosprisMaliceColdSpellsTriggered) : [];
        var offSpells = offWeapon is uint o ? CaptureItemTriggeredSpells(player, o, Stat.UniqueCosprisMaliceColdSpellsTriggered) : [];
        if (mainSpells.Length == 0 && offSpells.Length == 0) return null;
        if (mainSpells.Length > 0 && offSpells.Length > 0)
            return
            [
                new(skill, gem, mainSpells, player.ProjectileEpoch, CombatLifetime),
                new(skill, gem, offSpells, player.ProjectileEpoch, CombatLifetime),
            ];
        return [new(skill, gem, mainSpells.Length > 0 ? mainSpells : offSpells, player.ProjectileEpoch, CombatLifetime)];
    }

    /// <summary>The executable spells an item's own trigger support releases: every gem in that item it supports,
    /// independently of links (an item support is not in a socket group).</summary>
    static LinkedSpell[] CaptureItemTriggeredSpells(Player player, uint host, uint spellFlag) =>
        [.. GemSkillEffects(player).Select(s => new LinkedSpell(s.Id, SupportedSkill(player, s.Id)))
            .Where(s => s.Gem.Host == host && TriggeredSpellExecutable(player, s.Skill, s.Gem)
                && s.Gem.Supports.Any(trigger => SupportHas(trigger, spellFlag)))];

    // Called only after attack accuracy, block and critical confirmation, as ReleaseCriticalTrigger is.
    public static bool ReleaseItemCriticalTrigger(Player player, ProjectileSession session, LinkedTriggerSource[]? sources,
        bool critical, bool hostile, (int X, int Y) aim, double at)
    {
        if (!critical || !hostile || sources is null || sources.Length == 0 || !session.Active) return false;
        bool released = false;
        foreach (var source in sources)
        {
            if (!LinkedSourceCurrent(player, source) || !session.Active) continue;
            // The rotation belongs to the item that triggers, not to the attack's own socket group: the same sword keeps
            // its place in the cycle when the melee attack that crits is a different skill.
            released |= ReleaseLinkedSpell(player, session, source, "Cospri", aim, at, Stat.UniqueCosprisMaliceColdSpellsTriggered,
                rotation: (source.Spells[0].Gem.Host, -1));
        }
        return released;
    }
}
