-- Client table CharacterPanelDescriptionModes: all 12 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "CharacterPanelDescriptionModes" ("_row", "Id", "__col1", "FormatString_Positive", "FormatString_Negative") VALUES
(0, 'FlatValue', 'x', '{0}', ''),
(1, 'PercentageValue', 'x%', '{0}%', ''),
(2, 'Range', 'x - y', '{0} - {1}', ''),
(3, 'CurrentOfMax', 'x / y', '{0} / {1}', ''),
(4, 'Yes', 'Yes', 'No', 'Yes'),
(5, 'Resistance', 'x% (y%)', '{0}% ({1}%)', '<colour:red>{{{0}%}} ({1}%)'),
(6, 'RangeOrMin', 'x - y OR x', '{0}', '{0} - {1}'),
(7, 'PercentModifier', '+/-x%', '{0:+d}%', ''),
(8, 'PercentModifierFinal', 'x% more/less', '{0}% more', '{0}% less'),
(9, 'Seconds', 'x seconds', '{0} seconds', ''),
(10, 'FlatValueFromPercentValue', 'x (y%)', '{0} ({1}%)', ''),
(11, 'Metres', 'x metres', '{0} metre', '{0} metres');
