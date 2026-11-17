-- Named unique creation variants are catalog data, including their coupled modifier sets.
CREATE TABLE unique_item_variants (
    name TEXT NOT NULL,
    base_type TEXT NOT NULL,
    variant TEXT NOT NULL,
    variant_base_type TEXT,
    weight INTEGER NOT NULL DEFAULT 0 CHECK (weight >= 0),
    PRIMARY KEY (name, base_type, variant),
    FOREIGN KEY (name, base_type) REFERENCES unique_items (name, base_type)
);
CREATE TABLE unique_item_variant_mods (
    name TEXT NOT NULL,
    base_type TEXT NOT NULL,
    variant TEXT NOT NULL,
    ord INTEGER NOT NULL,
    mod TEXT NOT NULL,
    PRIMARY KEY (name, base_type, variant, ord),
    FOREIGN KEY (name, base_type, variant) REFERENCES unique_item_variants (name, base_type, variant)
);
INSERT INTO unique_item_variants (name, base_type, variant, variant_base_type, weight) VALUES
('Yriel''s Fostering', 'Metadata/Items/Armours/BodyArmours/BodyDex15', 'Ursa', NULL, 1),
('Yriel''s Fostering', 'Metadata/Items/Armours/BodyArmours/BodyDex15', 'Rhoa', NULL, 1),
('Yriel''s Fostering', 'Metadata/Items/Armours/BodyArmours/BodyDex15', 'Snake', NULL, 1),
('Beacon of Madness', 'Metadata/Items/Armours/Boots/BootsAtlas1', 'Chaos', 'Metadata/Items/Armours/Boots/BootsAtlas1', 0),
('Beacon of Madness', 'Metadata/Items/Armours/Boots/BootsAtlas1', 'Physical', 'Metadata/Items/Armours/Boots/BootsAtlas2', 0),
('Beacon of Madness', 'Metadata/Items/Armours/Boots/BootsAtlas1', 'Elemental', 'Metadata/Items/Armours/Boots/BootsAtlas3', 0);
INSERT INTO unique_item_variant_mods (name, base_type, variant, ord, mod) VALUES
('Yriel''s Fostering', 'Metadata/Items/Armours/BodyArmours/BodyDex15', 'Ursa', 0, 'GrantsSummonBeastUrsaUnique__1'),
('Yriel''s Fostering', 'Metadata/Items/Armours/BodyArmours/BodyDex15', 'Ursa', 1, 'AddedPhysicalDamageToAttacksBeastialMinionUnique__1'),
('Yriel''s Fostering', 'Metadata/Items/Armours/BodyArmours/BodyDex15', 'Ursa', 2, 'ProjectileAttacksChanceToBleedBeastialMinionUnique__1_'),
('Yriel''s Fostering', 'Metadata/Items/Armours/BodyArmours/BodyDex15', 'Rhoa', 0, 'GrantsSummonBeastRhoaUnique__1'),
('Yriel''s Fostering', 'Metadata/Items/Armours/BodyArmours/BodyDex15', 'Rhoa', 1, 'AttackAndMovementSpeedBeastialMinionUnique__1'),
('Yriel''s Fostering', 'Metadata/Items/Armours/BodyArmours/BodyDex15', 'Rhoa', 2, 'ProjectileAttacksChanceToMaimBeastialMinionUnique__1'),
('Yriel''s Fostering', 'Metadata/Items/Armours/BodyArmours/BodyDex15', 'Snake', 0, 'GrantsSummonBeastSnakeUnique__1'),
('Yriel''s Fostering', 'Metadata/Items/Armours/BodyArmours/BodyDex15', 'Snake', 1, 'AddedChaosDamageToAttacksBeastialMinionUnique__1'),
('Yriel''s Fostering', 'Metadata/Items/Armours/BodyArmours/BodyDex15', 'Snake', 2, 'ProjectileAttacksChanceToPoisonBeastialMinionUnique__1'),
('Beacon of Madness', 'Metadata/Items/Armours/Boots/BootsAtlas1', 'Chaos', 0, 'IncreasedChaosDamageUnique__5'),
('Beacon of Madness', 'Metadata/Items/Armours/Boots/BootsAtlas1', 'Chaos', 1, 'AllDamageCanPoisonGloriousMadnessUnique___1'),
('Beacon of Madness', 'Metadata/Items/Armours/Boots/BootsAtlas1', 'Chaos', 2, 'EnemiesExplodeOnDeathChaosGloriousMadnessUnique1'),
('Beacon of Madness', 'Metadata/Items/Armours/Boots/BootsAtlas1', 'Physical', 0, 'LocalIncreasedArmourAndEvasionUnique__13'),
('Beacon of Madness', 'Metadata/Items/Armours/Boots/BootsAtlas1', 'Physical', 1, 'DoubleDamageChanceGloriousMadnessUnique_1'),
('Beacon of Madness', 'Metadata/Items/Armours/Boots/BootsAtlas1', 'Physical', 2, 'FortifyEffectSelfGloriousMadnessUnique1'),
('Beacon of Madness', 'Metadata/Items/Armours/Boots/BootsAtlas1', 'Elemental', 0, 'IncreasedAilmentEffectOnEnemiesUnique_2'),
('Beacon of Madness', 'Metadata/Items/Armours/Boots/BootsAtlas1', 'Elemental', 1, 'ElementalConfluxesGloriousMadnessUnique1'),
('Beacon of Madness', 'Metadata/Items/Armours/Boots/BootsAtlas1', 'Elemental', 2, 'ElementalAilmentImmunityGloriousMadnessUnique1');
