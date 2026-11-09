using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // Cast On Critical Strike Support: two halves of one gem. On the attack (SupportCastOnCriticalStrike) it carries
    // cast_linked_spells_on_attack_crit_% (100); on the spell (SupportCastOnCritTriggered) cast_spell_on_linked_attack_crit,
    // spell_uncastable_if_triggerable, the 0.15 s cooldown (GrantedEffectsPerLevel 2737, one use) and its less spell
    // damage (support_cast_on_crit_spell_damage_+%_final, GemSupportDamage). A crit with the attack triggers one
    // spell of the same link group (CastOnCritState), cast at the enemy it crit: no cast time, its cost paid.

    /// <summary>Whether the client is told to run the triggered spell (S→C 0x1ce with the triggered flag), so it
    /// draws the spell. The hit is the server's either way.</summary>
    internal static bool CastOnCritSendsAction = true;

    /// <summary>After an attack's critical hit on <paramref name="targetId"/>, under the Monsters lock: rolls the
    /// support's chance and triggers a linked spell off the lock.</summary>
    internal static void TriggerCastOnCritLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort attackSkill, IReadOnlyDictionary<uint, int> attackStats, uint targetId)
    {
        if (Stats.Key(CastOnCritState.AttackStat) is not uint key || attackStats.GetValueOrDefault(key) is not (> 0 and var chance))
            return;
        if (chance < 100 && Random.Shared.Next(100) >= chance) return;
        _ = Task.Run(() => CastOnCritAsync(remote, stream, ciphers, conn, attackSkill, targetId));
    }

    /// <summary>A spell supported by Cast On Critical Strike is only triggered, never cast by the player
    /// (spell_uncastable_if_triggerable).</summary>
    internal static bool CastOnCritOnly(ClientState conn, ushort skillId)
    {
        var stats = SupportedSkill(conn, skillId).Stats;
        return Stats.Key(CastOnCritState.SpellStat) is uint trigger && stats.GetValueOrDefault(trigger) != 0
            && Stats.Key("spell_uncastable_if_triggerable") is uint uncastable && stats.GetValueOrDefault(uncastable) != 0;
    }

    static async Task CastOnCritAsync(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort attackSkill, uint targetId)
    {
        try
        {
            var attack = SupportedSkill(conn, attackSkill);
            if (attack.Group < 0 || Stats.Key(CastOnCritState.SpellStat) is not uint spellKey) return;
            var player = CharacterPlayerStats(conn);
            int Get(IReadOnlyDictionary<uint, int> values, string name) => Stats.Key(name) is uint k ? values.GetValueOrDefault(k) : 0;
            var spells = new List<((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem, double Cooldown)>();
            foreach (var gem in GemSkillEffects(conn))
            {
                if (gem.Id == attackSkill) continue;
                var spell = SupportedSkill(conn, gem.Id);
                if (spell.Host != attack.Host || spell.Group != attack.Group || !spell.RequirementsMet
                    || spell.Stats.GetValueOrDefault(spellKey) == 0) continue;
                // The cooldown is the trigger support's own row (0.15 s), with the player's and the spell's recovery.
                var support = spell.Supports.FirstOrDefault(s => s.Stats.Any(v => v.Key == spellKey));
                int baseMs = support is null ? 0 : SkillsFor(conn).CooldownMs(support.Effect.Row, support.Gem.Level);
                long recovery = (long)Get(player, "virtual_cooldown_speed_+%") + Get(spell.Stats, "base_cooldown_speed_+%")
                    + Get(spell.Stats, "virtual_cooldown_speed_+%");
                spells.Add((gem, CastOnCritState.CooldownSeconds(baseMs, recovery)));
            }
            if (spells.Count == 0) return;
            int? picked;
            (int X, int Y) from, to;
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0) return;
                if (!conn.Monsters.TryGetValue(targetId, out var target)) return;
                var cooldowns = spells.ToDictionary(s => s.Gem.Effect, s => s.Cooldown);
                picked = conn.CastOnCrit.Pick((attack.Host, attack.Group), [.. spells.Select(s => s.Gem.Effect)],
                    conn.Clock.Elapsed.TotalSeconds, effect => cooldowns[effect]);
                double at = ProjectileWorldTime(conn);
                from = conn.Walk.Position(at);
                to = target.Walk.Position(at);
            }
            if (picked is not int index) return;
            var cast = spells[index].Gem;
            if (!PayMana(conn, cast.Id, out uint cost, out uint lifeCost))
            {
                Console.WriteLine($"[gateway] {remote} cast on crit: {cast.Name} not triggered, not enough mana or life " +
                    $"({conn.PlayerMana} < {cost})");
                return;
            }
            bool projectile = UsesProjectileRuntime(cast.Effect);
            if (cost > 0 || lifeCost > 0)
            {
                string log = $"0x01b1 player cost {conn.PlayerMana} mana (-{cost} mana, -{lifeCost} life for triggered {cast.Name})";
                if (projectile) ProjectilesFor(remote, stream, ciphers, conn).Enqueue(() => SendSkillCost(remote, stream, ciphers, conn, lifeCost, log));
                else await SendSkillCost(remote, stream, ciphers, conn, lifeCost, log);
            }
            Console.WriteLine($"[gateway] {remote} cast on crit: skill 0x{attackSkill:x4} crit 0x{targetId:x}, triggers {cast.Name} " +
                $"(skill 0x{cast.Id:x4}) at ({to.X}, {to.Y}), cost {cost} mana, cooldown {spells[index].Cooldown * 1000:0} ms " +
                $"(t={conn.Clock.Elapsed.TotalSeconds:0.000})");
            if (CastOnCritSendsAction)
                await Send(remote, stream, ciphers, InstanceProtocol.InstantAction(PlayerId, cast.Id, to.X, to.Y, targetId, triggered: true),
                    $"0x01ce player skill 0x{cast.Id:x4}: triggered by cast on crit");
            // An instant, unreserved ticket: later actions do not cancel the triggered spell; death or an area change does.
            var ticket = new CooldownUseTicket(cast.Effect, conn.ActionCount, conn.ProjectileEpoch, Instant: true, Reserved: false);
            var roll = CaptureSkillCriticalRoll(conn, cast.Id);
            if (projectile)
                await ScheduleGemProjectile(remote, stream, ciphers, conn, cast.Id, cast, to, conn.ActionCount,
                    conn.ActionInterruptionGeneration, ticket, roll, triggered: true);
            else
                await AreaSkillHit(remote, stream, ciphers, conn, cast.Id, cast, from, to, immediate: true,
                    cooldownUse: ticket, criticalRoll: roll, targetId: targetId);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[gateway] {remote} cast on crit failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
