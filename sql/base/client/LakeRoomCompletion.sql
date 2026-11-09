-- Client table LakeRoomCompletion (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "LakeRoomCompletion" (
    "_row" INTEGER PRIMARY KEY,
    "Room" INTEGER,
    "__col1" INTEGER,
    "__col2" INTEGER,
    "Achievements" JSON,
    "__col4" JSON,
    "__col5" JSON,
    "__col6" INTEGER
);
