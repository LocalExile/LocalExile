-- Add Metadata/NPC/Act3/ScionCage from metadata/npc/act3/scioncage.ot (extends "nothing"):
-- common-pass deserialized components (read = 5): Positioned, BaseEvents, Animated, InteractionAction, Transitionable;
-- client-only components: Render, Targetable.
INSERT INTO "object_types" (
    "_row", "type", "chain", "missing", "abstract", "components", "read",
    "positioned_object_size", "positioned_blocking", "animated_object",
    "shared_timers", "basic_actions", "client_targetable",
    "transitionable_num_states", "transitionable_no_serialise",
    "positioned", "positioned_static",
    "client_transitionable_create_untransitioned",
    "client_transitionable_create_transitioned",
    "client_transitionable_on_transition"
)
SELECT
    COALESCE(MAX("_row"), -1) + 1,
    'Metadata/NPC/Act3/ScionCage',
    '["Metadata/NPC/Act3/ScionCage"]',
    '[]',
    0,
    '["Positioned","BaseEvents","Animated","InteractionAction","Transitionable","Render","Targetable"]',
    5,
    '7',
    'false',
    'Metadata/Terrain/Doodads/RuinedCity/Scepter_of_god/birdcage.ao',
    NULL,
    '[]',
    NULL,
    2,
    0,
    1,
    'true',
    'PlayAnimationByIndex(0); PauseAnimation();',
    'PlayAnimationByIndex(0); GoToAnimationEnd(); DisableTargetable(); DisableHighlighting();',
    'PlayAnimationByIndex(0); DisableTargetable();'
FROM "object_types"
WHERE NOT EXISTS (SELECT 1 FROM "object_types" WHERE "type" = 'Metadata/NPC/Act3/ScionCage');
