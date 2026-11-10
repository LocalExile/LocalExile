-- Client table MicrotransactionCounters: all 2 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "MicrotransactionCounters" ("_row", "Id", "CountInterval", "HASH32", "FunctionCall") VALUES
(0, 'MtxEldritchHungerArmourHelmetKillstreak', 5, 3850780029, 'this.eldritch_hunger_head.Call(update_killstreak, trigger_microtransaction_counter_value);'),
(1, 'SolarGuardianBackAttachmentKillstreak', 10, 3479704997, 'this.sun_back_attachment.Call(update_killstreak, trigger_microtransaction_counter_value);');
