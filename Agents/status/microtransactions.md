# Status: Microtransactions and cosmetics

Part of [Status](../Status.md); what to build next in this area is in
[Roadmap](../Roadmap.md).

## Works (verified with the real client)

- Shop portal effects as game data (dev): `/cosmetic list [filter]`, `/cosmetic portal <name, number or part of a
  name>` (`CosmeticCatalog.Matches`: an ambiguous piece lists the matching effects with their numbers instead of
  applying the first), `/cosmetic off`.
  A portal cosmetic is the portal's own 0x008f variation (+0x26, a `MicrotransactionPortalVariations` row), so the
  client draws the shop effect in place of the base portal — also for portals already standing, which are listed again
  — with no second object, store or balance (seen live 2026-10-21: the Black Hole effect replaces the base portal
  visual on all six portals — its flat disc is the real art, and the client keeps each rig's own orientation, ignoring
  the object's rotation and the portal type; `/portal_rotation` is the dev knob for a rig that does come out
  misoriented); the choice is saved per character (`PortalCosmetic` in the save, verified to survive a relog).
  Code: `Cosmetics.cs`, `InstanceMapDevice.Portal`, Gateway (`ResendMapPortals`, `/cosmetic`).
  Docs: [21](../docs/protocol/21-hideouts.md) §5.
- Microtransactions: every account owns every cosmetic the client can show (the 7721 `MtxTypes` rows with an
  `MtxTypeGameSpecific` row); the Microtransactions panel (N), the store's ALREADY OWNED marks and the Cosmetics tab
  (K) load (C→S 0x1f2 → S→C 0x1f4, hideout and guild lists empty via 0x1f5 / 0x1f4);
  a pick in the Cosmetics tab (C→S 0x1f9) is saved per character and worn (S→C 0x1f3, also on every area entry);
  in the world: equipment skins and weapon effects (including the bow in its left-hand model slot), attachments and character effects on the model (Inventories component
  references), footprints and apparitions through the stats, portals (the map device's), cursors and flask skins.
  Verified live 2026-10-23.
  Verified live 2026-11-12: a pet (Kiwi: spawned, beside the player again after a teleport, gone when the slot is
  emptied; not yet the idle-relaxed animations, e.g. the Goblin Troupe's song) and a portrait in the character panel.
  Built, awaiting live verification: the portrait on the selection screen, skill effects (the Cosmetics tab's skill
  slots, picked with C→S 0x1fa, the effect's stats on that skill,
  Righteous Fire's ring and buff visuals, restyled at once while it burns, and the projectile and explosion of
  projectile skills such as Fireball).
  Portrait frames (the Social Frame slot): a pick is saved and applies at the next login, as the client's own chat line
  says; the character list shows it at once. Verified live 2026-10-31: the client loaded the `SupporterIconFrame` art at
  selection and in game, and another player's client in a party; the frame stayed as it was through area changes until
  the next login, and emptying the slot went back to the default.
  Verified live 2026-11-12: a quiver skin on the worn quiver (a skin or weapon effect is drawn only on an item of a
  class it lists), the Goblin Shaman Skin's town and hideout form (the server sends the morph, its model and
  animation set), Alternate Charge Skins (the charge buffs name the skin's art), and a portal effect on a Portal
  Scroll's portal, which is listed twice so that a slow client cannot miss it and is restyled by a pick while it stands.
  Code: `InstanceMicrotransactions.cs`, `Microtransactions.cs` (`MtxCatalog`, `CosmeticEffects`), Gateway
  (`AnswerMtxRequest`, `PickCosmetic`, `Looks`, `CosmeticStats`), `CharacterSaves.Cosmetics`, `PetObject`,
  `SpawnPets`, `StepPetFollow`, `SkillSlots`.
  Docs: [23](../docs/protocol/23-microtransactions.md).

## Partly working

- Cosmetics skill target lifetime and request validation (issue [#306](https://github.com/Wrathen/LocalExile/issues/306)).
  Key reuse across gem replacements and acceptance of stale/incompatible picks are reproduced in Gateway tests.
  Targets now retain keys only while unchanged; replacements and reappearances get fresh connection-local keys.
  Queued 0x1f3 refreshes sample current targets inside the send gate, with matching binding logs.
  0x1fa validates the current target and effect eligibility before changing the per-gem, per-slot saved choice.
  Automated coverage includes configured/cleared A → B → A, removal/reappearance, queueing, multiple skills,
  primary/totem slots, and preservation of Righteous Fire's saved art input.
  The reported native picker mismatch still needs user verification; packet tests do not establish a UI fix.
  Protocol evidence, limits and client acceptance steps: [23](../docs/protocol/23-microtransactions.md) §4.

- The Portal slot's two "random effect" choices give each portal a random effect; the panel's own display of the
  choice was not checked ([23](../docs/protocol/23-microtransactions.md) §4).
- Cosmetics whose stat the client reads only as a derived stat: the morphs (goblins, skeleton, Lunar and Wuzhiqi
  seen), the charge skins (seen) and the choices of art (surge, hit effect, ailment, mine and ring art, chest
  effects, floating: sent, not seen on screen): [23](../docs/protocol/23-microtransactions.md) §3 and §4.

## Not working yet

- The phasing ring, the summoning rings' surges, and every cosmetic that counts kills, hits, blocks or skills used:
  [23](../docs/protocol/23-microtransactions.md) §4.
- The Ashbringer Wyvern, polymorph wand and pet morphs: [23](../docs/protocol/23-microtransactions.md) §4.
