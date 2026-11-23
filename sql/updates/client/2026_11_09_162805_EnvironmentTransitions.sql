-- Client table EnvironmentTransitions: all 5 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "EnvironmentTransitions" ("_row", "Id", "OTFiles", "__col2", "Environment", "__col4", "__col5") VALUES
(0, 'LinearBidirectional', '["env_line_start","env_line_end"]', 2, '[102,103]', '[]', 0),
(1, 'LinearMonotonic', '["env_line_start","env_line_end"]', 3, '[102,103]', '[]', 0),
(2, 'Radial', '["env_radial_point"]', 4, '[104]', '[]', 0),
(3, 'RadialMonotonic', '["env_radial_point"]', 5, '[104]', '[]', 0),
(4, 'Abyss', '["env_abyss_node"]', 6, '[]', '[]', 2);
