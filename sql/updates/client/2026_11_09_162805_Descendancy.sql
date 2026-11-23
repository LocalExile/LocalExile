-- Client table Descendancy: all 17 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "Descendancy" ("_row", "Id", "Name", "BackgroundImage", "FlavourText", "CoordinateRect", "RGBFlavourTextColour", "UnlockCondition", "SoundEffectOpen", "SoundEffectAccept", "__col9", "PassiveTreeUIArt", "UIArt") VALUES
(0, 'None', '', '', '', '', '0,255,0', '', NULL, NULL, 0, NULL, NULL),
(1, 'Warden', 'Warden of the Maji', 'Art/2DArt/UIImages/InGame/Azmeri/AscendancyFrameWarden', 'A Warden of the Maji has sworn' || char(13) || '
to walk the righteous path.', '485, 226,1000,1000', '176,217,179', '<italic>{Find the Warden of Eaves in the Viridian Wildwood to switch to this Wildwood Specialisation}', 552, 560, 1, 1, 1),
(2, 'Warlock', 'Warlock of the Mists', 'Art/2DArt/UIImages/InGame/Azmeri/AscendancyFrameShaman', '  A Warlock of the Mists' || char(13) || '
 seeks mastery over' || char(13) || '
the darkness.', '327, 252, 1000,1000', '191, 153, 156', '<italic>{Find the Breaker of Oaths in the Viridian Wildwood to switch to this Wildwood Specialisation}', 550, 558, 2, 1, 1),
(3, 'Primalist', 'Wildwood Primalist', 'Art/2DArt/UIImages/InGame/Azmeri/AscendancyFramePrimal', 'A Primalist of the' || char(13) || '
Wildwood answers' || char(13) || '
to no one.', '919,464,900,1250', '207,189,138', '<italic>{Find the Primal Huntress in the Viridian Wildwood to switch to this Wildwood Specialisation}', 551, 559, 3, 1, 1),
(4, 'Trialmaster', 'Chaos Bloodline', 'Art/2DArt/UIImages/InGame/BossAscendancy/ArchitectofChaos/ArchitectofChaosAscendancyFrame', 'Your entire existence is merely' || char(13) || '
a jest to entertain Chaos.', '500,960,900,1250', '193,167,107', '', 551, 559, 4, 2, 2),
(5, 'Oshabi', 'Oshabi Bloodline', 'Art/2DArt/UIImages/InGame/BossAscendancy/DaughterofOshabi/DaughterofOshabiAscendancyFrame', ' Wraeclast''s Lifeforce flows' || char(13) || '
through your veins.', '516,954,1050,1100', '146,192,226', '', 551, 559, 5, 3, 3),
(6, 'KingInTheMists', 'Nameless Bloodline', 'Art/2DArt/UIImages/InGame/BossAscendancy/Ritualist/RitualistAscendancyFrame', 'Crawling out of the dark,' || char(13) || '
you seek the exquisite' || char(13) || '
light of meaning.', '500,950,900,1250', '127,122,183', '', 551, 559, 6, 4, 4),
(7, 'Catarina', 'Catarina Bloodline', 'Art/2DArt/UIImages/InGame/BossAscendancy/Lich/LichAscendancyFrame', 'Distant screaming echoes...' || char(13) || '
the Well of Souls, calling' || char(13) || '
to you from below...', '330,920,900,1250', '180,238,204', '', 551, 559, 7, 5, 5),
(8, 'Aul', 'Aul Bloodline', 'Art/2DArt/UIImages/InGame/BossAscendancy/PrimordialKnight/PrimordialKnightAscendancyFrame', 'The Crystal King traded his' || char(13) || '
people for dark power.' || char(13) || '
Now, it is yours.', '530,1025,900,1250', '220,250,255', '', 551, 559, 8, 6, 6),
(9, 'Lycia', 'Lycia Bloodline', 'Art/2DArt/UIImages/InGame/BossAscendancy/Herald/HeraldAscendancyFrame', 'The Original Sin' || char(13) || '
was not your doing,' || char(13) || '
but it is still your' || char(13) || '
burden to bear.', '925,530,900,1250', '232,194,167', '', 551, 559, 9, 7, 7),
(10, 'Olroth', 'Olroth Bloodline', 'Art/2DArt/UIImages/InGame/BossAscendancy/Runesmith/RunesmithAscendancyFrame', 'Yours is a legacy of' || char(13) || '
heroes... fallen, or' || char(13) || '
otherwise.', '570,470,900,1250', '144,168,198', '', 551, 559, 10, 8, 8),
(11, 'Farrul', 'Farrul Bloodline', 'Art/2DArt/UIImages/InGame/BossAscendancy/Animist/AnimistAscendancyFrame', 'You and your pack run wild' || char(13) || '
and free, brawling' || char(13) || '
as you see fit.', '520,200,900,1250', '255,203,175', '', 551, 559, 11, 9, 9),
(12, 'Delirious', 'Delirious Bloodline', 'Art/2DArt/UIImages/InGame/BossAscendancy/Imposter/ImposterAscendancyFrame', 'Laughter echoes from' || char(13) || '
the darkest depths' || char(13) || '
of your mind.', '850,800,900,1250', '207,189,138', '', 551, 559, 12, 10, 10),
(13, 'Breachlord', 'Breachlord Bloodline', 'Art/2DArt/UIImages/InGame/BossAscendancy/Breachlord/BreachlordAscendancyFrame', 'Deep within you, the chains' || char(13) || '
of life itself were forged' || char(13) || '
with intent...', '340,960,900,1250', '220,175,255', '', 551, 559, 13, 11, 11),
(14, 'Necromantic', 'Saresh Bloodline', 'Art/2DArt/UIImages/InGame/BossAscendancy/Saresh/SareshAscendancyFrame', 'Conquer not the darkness within.' || char(13) || '
Use it. Make it your strength.', '420,890,900,1250', '180,238,204', '', 551, 559, 14, 12, 12),
(15, 'Brinerot', 'Velka Bloodline', 'Art/2DArt/UIImages/InGame/BossAscendancy/Deepwater/DeepwaterAscendancyFrame', 'Tsoatha awaits the drowned' || char(13) || '
and damned, in depths' || char(13) || '
beyond the mortal ken.', '540,930,900,1250', '180,238,204', '', 551, 559, 15, 13, 13),
(16, 'Abyssal', 'Abyssal Bloodline', 'Art/2DArt/UIImages/InGame/BossAscendancy/AbyssRevamp/AbyssRevampAscendancyFrame', ' Return to the Source. ' || char(13) || '
From there, be remade.', '500,920,900,1250', '183,214,138', '', 551, 559, 16, 14, 14);
