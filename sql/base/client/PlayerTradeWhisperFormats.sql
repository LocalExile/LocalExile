-- Client table PlayerTradeWhisperFormats (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "PlayerTradeWhisperFormats" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Whisper" TEXT,
    "InStash" BOOLEAN,
    "IsPriced" BOOLEAN
);
