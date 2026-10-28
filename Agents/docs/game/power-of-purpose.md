# Power of Purpose mana conversion (#291)

## Source and calculation model

This is an explicit server calculation model based on [pinned PoB source][pob].
It is not a claim that the official server algorithm or its rounding has been recovered.
At commit `16de4b82d57f1c0de6eb40f37143c32d4da36a02`, `src/Modules/CalcDefence.lua` lines 973-979
convert raw flat Mana to twice as much Armour and combine Mana, Armour, ArmourAndEvasion and Defences modifiers.
Lines 1027-1029 round the combined defence totals.
`src/Modules/CalcTools.lua` lines 15-17 add INC modifiers before applying MORE.
`src/Modules/CalcPerform.lua`, `doActorLifeMana` lines 105-106, computes the remaining mana after conversion.
These pinned files were checked directly for this implementation.

[pob]: https://github.com/PathOfBuildingCommunity/PathOfBuilding/tree/16de4b82d57f1c0de6eb40f37143c32d4da36a02

The raw boolean `keystone_mental_conditioning` (Stats key 9548) enables an 80% conversion.
Any nonzero value enables it; an explicit development zero replaces the passive source.
Passive graph node 38080 (`AscendancyCustomBerserker11`, Mental Conditioning) supplies the flag.
`display_mode_is_behemoth_ascendancy` (key 21159) alone has no effect.

Let B include class base mana, six per level, final Intelligence / 2 and flat mana.
It also includes `StrengthMana.Flat(final Strength, X_mana_per_4_strength)` once.
Both attributes come from main's `AttributeModifiers`, after all flat sources and increased attributes combine.
The conversion never reads an unscaled class-plus-bonus attribute or a final native display replacement.
The existing Strength consumer counts complete nonnegative four-Strength groups and retains the signed raw value.
Let M be the existing increased mana percentage and A the existing armour percentage, including shared defences.
The converted armour contribution is `2 * B * 0.8 * (1 + (M + A) / 100) * armourMore`.
Remaining maximum mana is `B * (1 + M / 100) * 0.2`.
For B=100, M=25 and A=50, converted armour is 280 and remaining maximum mana is 25.
The ordinary armour branch keeps only A and its existing MORE multiplier.
Iron Reflexes keeps its existing armour/evasion/shared-defence scope, subtracting shared increases once.
Its converted evasion also retains the existing evasion and armour MORE multipliers.
Neither branch's increases are applied again to the combined final armour.

## Rounding and numeric boundaries

With conversion active, Intelligence / 2 retains its half point until the final pool calculation.
For example, B=102.5 leaves 20.5 mana, rounded to 21.
Ordinary armour, Iron Reflexes armour and mana-derived armour combine before rounding.
Final mana and armour round to nearest, with positive ties upward, and clamp to `0..int.MaxValue`.
Signed flat contributions and percentage sums remain signed until that final clamp.
`PowerOfPurpose` uses exact `BigInteger` numerators over 1,000,000 to avoid decimal overflow and cancellation loss.
The pure `ConvertFlatMana` helper accepts twice B so half mana remains exact and returns numerators at that scale.
The inactive path retains main's decimal arithmetic, integer Intelligence / 2 and intermediate truncations.
Main's `Rating` clamp still bounds ordinary Armour, unconverted Evasion and the Iron Reflexes sum.
Active conversion bypasses ordinary Armour evaluation and combines the exact signed contributions once.

## Sources, consumers and integration boundary

`PoolsFor` reads the passive/active-equipment raw flag and its explicit development replacement.
Inactive equipment contributes neither flat mana nor increases; equipment activation still uses main's attribute rules.
It sends remaining maximum mana through the existing class stats and final armour through the existing native stat.
Maximum-mana added lightning reads that remaining maximum; Steel Willed reads final converted armour once.
Spending or reserving mana does not change the maximum-mana damage input.

`/set_stat` keeps every raw attribute input alongside the StrengthMana and conversion rebuild inputs.
An attribute replacement rebuilds requirements, dependent equipment and pools before the full stat snapshot is sent.
It clamps current mana to the new unreserved cap and publishes reservation even if current mana did not change.
Growth or source removal does not refill mana, and a shrinking pool does not revive a dead character.
Existing passive/equipment/area and fresh-connection pool rebuilds use the same model.
The unchanged current-main `SendPlayerMana` samples current and reserved mana together at the send gate.
Its own-aura reconciliation, retirement guard and energy-shield publication remain in place.

Mental Conditioning now composes the existing mana-per-Strength consumer with conversion.
`StrengthMana.Flat` joins B exactly once before `ConvertFlatMana`, using `CharacterStats.For`'s existing final Strength.
A fresh Witch's full node supplies B=80, leaving 16 mana and 128 Armour.
Explicit conversion zero retains the independent mana source and gives 80 mana with no converted Armour.
Explicit mana-per-Strength zero retains conversion and gives 11 mana and 90 Armour.
The current incoming chaos-armour/CI, own-aura and Mageblood consumers keep their existing scope.
Percentage attributes (#285/#290) use main's existing engine and source precedence throughout this conversion.
The model uses only the current mana increases and armour/shared-defence increases and MORE multipliers.
Missing Mana MORE modifiers and other pool conversions are outside this slice.
Phrecia selection and its existing client compatibility gate are unchanged.

## Verification

`PowerOfPurposeTests` covers the 280/25 example, shared increases, MORE, Iron Reflexes, combined rounding,
odd Intelligence, signed cancellation, extreme bounds and ordinary native-byte controls.
`PowerOfPurposeGatewayTests` uses the production tables and node, real Commands dispatch and decrypted wire decoding.
It covers idle reservation updates with regeneration disabled, unchanged current mana, lower-cap clamping,
dead characters, raw toggles/removal, fresh pool reconstruction and queued snapshot freshness.
It also checks signed native limits and the maximum-mana lightning and Steel Willed consumers.
Current-main regressions cover both real-node sources, complete Strength groups with odd Intelligence,
additive mana/armour increases, active/inactive equipment, zero/off/removal, Mageblood and chaos-armour/CI.
The rebase onto `939b44fec76ba46df762fcd9cdf616fa3d2f8739` adds combined increased-attribute regressions.
Real item/passive sources and actual `/set_stat` commands cover typed/all increases, final Strength groups,
odd final Intelligence, independent raw zero/off controls and display overrides without pool feedback.
Decrypted complete stat snapshots match the final source snapshot, including attributes and requirement values.
Half-mana reservation changes clamp current mana without refilling it when a source returns.
Extreme scaled attributes retain main's signed pool and rating limits with conversion enabled or disabled.
Queued mana publication also checks own-aura restriction with both raw mana sources changing before the send gate.

The following thirteen native controls are historical evidence for original head
`7678d60e5020793722556d0689a8f08e58b499f0`, using an unascended Witch and chosen level-one Plate Vest modifiers.
Historical ordered #290 / #294 / #296 / #300 composition at `c9cd1864` passed the build and all three suites.
It also passed 28 native controls, including eight conversion controls on an unascended Marauder.
Those checks do not establish native acceptance for the new candidate on `939b44fe`.
At final Strength 40 / Intelligence 14, B=127 gives 25 mana and 222 Armour including the ordinary 19-Armour vest.
Adding 100% increased mana gives 51 mana / 425 Armour; final Strength 48 / Intelligence 18 gives 58 / 483.
At final Strength 16 / Intelligence 7, B=75.5 keeps its half point and yields 15 mana / 140 Armour.
Raw conversion zero restores ordinary truncated mana 75 and vest Armour 19.
The joint resolution uses final Intelligence in the twice-flat bucket; #290 owns the attribute engine.
Gateway, Core, Storage and Host hashes/Git versions, listener DLL and working directory were pinned before entry.
The earlier thirteen controls below remain historical; fresh joint checks do not claim the remaining cases.
The display-only flag does not enable conversion.
Naked 56 mana becomes 11 remaining mana and 90 Armour; removing the source restores the ordinary pools.
With 25% increased mana, 50% increased armour and 19 base Armour, conversion gives 14 mana and 185 Armour.
Adding 20% shared defences once produces 207 Armour while mana remains 14.
A flat mana bucket of 2000 gives 500 remaining mana and 5629 combined Armour with those first two increases.
Steel Willed's raw 2-per-450 source then changes a fixed-100 chaos attack to an actual 124-damage hit.
Removing conversion restores the 100-damage hit; restoring it returns 124 without compounding.
Maximum-mana attack lightning reads the remaining mana and the existing attack increase.
Relog preserves the real item modifiers and clears the development conversion source.
Each run verifies embedded Git versions, DLL hash and working directory before attachment.
These controls do not allocate Mental Conditioning or establish official-server rounding parity.
Native idle-aura reservation, Iron Reflexes and MORE compositions remain additional checks;
their authoritative calculation and resource snapshots retain the automated coverage above.
