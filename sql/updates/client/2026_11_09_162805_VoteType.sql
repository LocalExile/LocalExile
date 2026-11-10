-- Client table VoteType: all 3 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "VoteType" ("_row", "Id", "Text", "AcceptText", "RejectText", "__col4", "__col5", "__col6", "__col7") VALUES
(0, 'BanditVoteInt', 'If you help Alira, you will be permanently granted:

{0}

You will have to fight any party members who do not side with you. If you kill Alira, you cannot choose to help her.', 'Help Alira', 'Kill Alira', 56, 0, 0, 0),
(1, 'BanditVoteStr', 'If you help Oak, you will be permanently granted:

{0}

You will have to fight any party members who do not side with you. If you kill Oak, you cannot choose to help him.', 'Help Oak', 'Kill Oak', 89, 0, 0, 0),
(2, 'BanditVoteDex', 'If you help Kraityn, you will be permanently granted:

{0}

You will have to fight any party members who do not side with you. If you kill Kraityn, you cannot choose to help him.', 'Help Kraityn', 'Kill Kraityn', 54, 0, 0, 0);
