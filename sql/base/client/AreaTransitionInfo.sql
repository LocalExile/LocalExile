-- Client table AreaTransitionInfo (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AreaTransitionInfo" (
    "_row" INTEGER PRIMARY KEY,
    "StartingArea" INTEGER,
    "EndingArea" INTEGER,
    "AnimationType" INTEGER,
    "AnimationMarauder" INTEGER,
    "AnimationRanger" INTEGER,
    "AnimationWitch" INTEGER,
    "AnimationDualist" INTEGER,
    "AnimationTemplar" INTEGER,
    "AnimationShadow" INTEGER,
    "AnimationScion" INTEGER,
    "Animation" INTEGER,
    "MiscObjectGround" JSON,
    "__col12" INTEGER,
    "BossObject" JSON
);
