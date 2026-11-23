-- Client table SkillGems (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SkillGems" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" INTEGER,
    "StrengthRequirementPercent" INTEGER,
    "DexterityRequirementPercent" INTEGER,
    "IntelligenceRequirementPercent" INTEGER,
    "VaalVariant_BaseItemTypesKey" INTEGER,
    "IsVaalVariant" BOOLEAN,
    "MinionGlobalSkillLevelStat" INTEGER,
    "IsSupport" BOOLEAN,
    "__col8" BOOLEAN,
    "__col9" BOOLEAN,
    "__col10" BOOLEAN,
    "RegisterPrimaryGrantedEffect" BOOLEAN,
    "RegisterSecondaryGrantedEffect" BOOLEAN,
    "AwakenedVariant" INTEGER,
    "RegularVariant" INTEGER,
    "GemColour" INTEGER,
    "ItemExperienceType" INTEGER,
    "MtxSlotTypes" JSON,
    "GemVariants" JSON
);
