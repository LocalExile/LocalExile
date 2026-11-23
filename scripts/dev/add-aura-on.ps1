# Inserts the /aura_on dev command into CommandsFeature.cs after the /auras command block.
# Run from the repository root.
param([string]$RepoPath = "$PSScriptRoot\..\..")

$path = Join-Path $RepoPath "Modules\Core\Player\Commands\CommandsFeature.cs"
$c = [System.IO.File]::ReadAllText($path)

$anchor = 'auraEffect}%.' + [char]34 + ');' + [char]13 + [char]10 + '        })'
$idx = $c.IndexOf($anchor)
if ($idx -lt 0) { throw "anchor not found" }
if ($c.Contains('.Add("aura_on"')) { throw "aura_on already present" }

$nl = "`r`n"
$insert = $nl `
+ '        .Add("aura_on", "/aura_on <name|effectId> [level] (dev: activate a supported aura by name without a socketed gem, e.g. /aura_on Determination 20; it reserves and shows like a real cast)", async args =>' + $nl `
+ '        {' + $nl `
+ '            if (args.Length < 1) throw new ArgumentException("which aura? /aura_on <name|effectId> [level]");' + $nl `
+ '            if (!int.TryParse(args.Length > 1 ? args[1] : "", out int level) || level < 1) level = 20;' + $nl `
+ '            level = Math.Clamp(level, 1, 30);' + $nl `
+ '            string name = args[0];' + $nl `
+ '            var skills = SkillsFor(conn);' + $nl `
+ '            // A GrantedEffects id ("Determination") or a display name; the gem list resolves the display names.' + $nl `
+ '            int effect = skills.EffectRow(name);' + $nl `
+ '            string? effectId = effect >= 0 ? name : null;' + $nl `
+ '            if (effect < 0)' + $nl `
+ '                foreach (var gem in GemSkillEffects(conn))' + $nl `
+ '                    if (skills.EffectId(gem.Effect)?.Equals(name, StringComparison.OrdinalIgnoreCase) == true' + $nl `
+ '                        || gem.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)' + $nl `
+ '                    { effect = gem.Effect; effectId = skills.EffectId(gem.Effect); break; }' + $nl `
+ '            if (effect < 0 || effectId is null || !AuraCatalog.Supported(effectId))' + $nl `
+ '                throw new ArgumentException($"no supported aura named {name} (AuraCatalog.BuffRows lists the set)");' + $nl `
+ '            if (InTown(conn)) throw new ArgumentException("towns disable skills; travel to a combat area first");' + $nl `
+ '            var reservation = skills.Reservation(effect, level)' + $nl `
+ '                ?? throw new ArgumentException($"{name} has no reservation at level {level} (not an aura?)");' + $nl `
+ '            // A virtual dev skill id above the real ones: it never collides with a socketed gem id.' + $nl `
+ '            ushort skillId;' + $nl `
+ '            lock (conn.Monsters)' + $nl `
+ '                skillId = conn.Auras.Keys.Count == 0 ? (ushort)0x7001 : (ushort)Math.Min(0x7fff, conn.Auras.Keys.Max() + 1);' + $nl `
+ '            bool started = await ToggleAura(remote, stream, ciphers, conn, skillId, effect, effectId, reservation, level, 0);' + $nl `
+ '            if (!started) await conn.Commands!.Reply($"{name}: the aura did not start (refused, see the gateway log)");' + $nl `
+ '        })'

$c = $c.Insert($idx + $anchor.Length, $insert)
[System.IO.File]::WriteAllText($path, $c)
"inserted at offset $idx"
