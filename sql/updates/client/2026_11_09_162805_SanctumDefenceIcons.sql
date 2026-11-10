-- Client table SanctumDefenceIcons: all 3 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "SanctumDefenceIcons" ("_row", "Id", "Stat", "DefenceIcon", "DefenceBrokenIcon", "BrokenStat", "Description") VALUES
(0, 'ResolveArmour', 18794, 'Art/2DArt/UIImages/InGame/Sanctum/ResolveDefenceArmour', '', NULL, 'Mitigation prevents a portion of Resolve loss and scales with your Armour'),
(1, 'ResolveEvasion', 18795, 'Art/2DArt/UIImages/InGame/Sanctum/ResolveDefenceEvasion', '', NULL, 'This scales with your Evasion Rating'),
(2, 'ResolveEnergyShield', 18796, 'Art/2DArt/UIImages/InGame/Sanctum/ResolveDefenceEnergyShield', 'Art/2DArt/UIImages/InGame/Sanctum/ResolveDefenceEnergyShieldBroken', 18797, 'Up to 50% of Resolve loss from Enemy Hits is removed from the Aegis before Resolve or Inspiration
Aegis is broken when any Resolve is lost from Enemy Hits
Aegis is instantly restored after 4 seconds of no Resolve being lost
Resolve Aegis scales with your maximum Energy Shield');
