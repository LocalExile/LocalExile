using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    /// <summary>
    /// The ordinary Pathfinder's rules that are not plain stats (issue #602): the "during any Flask Effect" condition,
    /// Master Alchemist's free use and elemental-ailment removal, Master Toxicist's extra poison damage and poison
    /// spread, and Master Distiller's per-use charge payment. Everything else is the sum of the node's stats.
    /// </summary>
    internal static class Pathfinder
    {
        /// <summary>1.5 m at the client's 10 units per metre (the close-range 20-unit radius is 2 m).</summary>
        internal const double SpreadPoisonRadius = 15;

        /// <summary>Master Distiller's bonuses for one non-channelling skill use: elemental penetration, increased
        /// critical strike chance and physical damage gained as extra chaos, each only for a flask type that paid.</summary>
        internal sealed record DistillerBonuses(int ElementalPenetration, int CriticalChanceIncrease, int PhysicalAsChaos)
        {
            internal bool Any => ElementalPenetration > 0 || CriticalChanceIncrease > 0 || PhysicalAsChaos > 0;

            /// <summary>The stats a supported hit reads from its skill/own dictionary.</summary>
            internal Dictionary<uint, int> AdditionalStats()
            {
                var stats = new Dictionary<uint, int>();
                if (ElementalPenetration > 0 && Stats.Key("base_penetrate_elemental_resistances_%") is uint penetration)
                    stats[penetration] = ElementalPenetration;
                if (PhysicalAsChaos > 0 && Stats.Key("physical_damage_%_to_add_as_chaos") is uint chaos)
                    stats[chaos] = PhysicalAsChaos;
                return stats;
            }
        }

        /// <summary>"During any Flask Effect": a running life, mana or utility flask effect. A flask whose recovery
        /// ended or whose buff expired is not one, even while its item still holds charges.</summary>
        internal static bool FlaskEffectActive(ClientState conn) =>
            conn.FlaskEffects.Any || conn.UtilityFlaskEffects.Any;

        /// <summary>One character stat as the ordinary stat aggregation sees it.</summary>
        internal static int Stat(ClientState conn, string name)
        {
            var stats = CharacterPlayerStats(conn);
            return Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        }

        /// <summary>Master Alchemist's <c>flasks_%_chance_to_not_consume_charges</c>: one roll per flask use, before the
        /// spend, so a free use publishes the same item with its charges unchanged.</summary>
        internal static bool FlaskUseIsFree(ClientState conn, Random? random = null) =>
            FlaskUseIsFree(Stat(conn, "flasks_%_chance_to_not_consume_charges"), random);

        internal static bool FlaskUseIsFree(int chance, Random? random = null) =>
            RandomRolls.Chance(Math.Clamp(chance, 0, 100), 100, random ?? Random.Shared);

        /// <summary>Master Alchemist's <c>dispel_status_ailments_on_flask_use</c>: "Removes Elemental Ailments when you use
        /// a Flask". Clears the model and queues the client's buff removals on the flask output path, in the same
        /// presentation step as the use. Called under the Monsters lock from the accepted flask use.</summary>
        internal static void DispelAilmentsOnFlaskUse(ClientState conn)
        {
            if (Stat(conn, "dispel_status_ailments_on_flask_use") == 0) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var before = conn.ElementalAilments.Snapshot(now);
            if (!before.Ignite && before.Chill == 0 && !before.Freeze && before.Shock == 0) return;
            double previousAction = conn.ElementalAilments.ActionFactor(now);
            conn.ElementalAilments.Clear();
            foreach (var (packet, log) in PlayerAilmentBuffs(before, conn.ElementalAilments.Snapshot(now)))
                Automation(conn).Packets.Add(new(packet, log));
            PlayerAilmentsChangedLocked(conn, previousAction, now);
        }

        /// <summary>Master Toxicist's spread: "When you kill a Poisoned Enemy during any Flask Effect, Enemies within
        /// 1.5 metres are Poisoned" (Ruthless replaces the flask condition with its on-kill stat). The killing monster's
        /// latest poison is copied to each nearby hostile body, without a new chance roll. Called for the player's kills;
        /// under the Monsters lock.</summary>
        internal static void SpreadPoisonOnKill(ClientState conn, SpawnedMonster killed)
        {
            if (killed.Poisons.LastApplied is not { } poison) return;
            double poisonNow = ProjectileWorldTime(conn);
            if (!killed.Poisons.PoisonedAt(poisonNow) && !killed.Poisons.DiedPoisoned) return;
            bool flaskSpread = Stat(conn, "unique_spread_poison_to_nearby_enemies_during_flask_effect") != 0 && FlaskEffectActive(conn);
            bool onKill = Stat(conn, "spread_poison_to_nearby_enemies_on_kill") != 0;
            if (!flaskSpread && !onKill) return;
            lock (conn.Monsters)
            {
                double positionNow = conn.Clock.Elapsed.TotalSeconds;
                var at = killed.Walk.Position(positionNow);
                foreach (var (_, m) in conn.Monsters)
                {
                    if (ReferenceEquals(m, killed) || !m.Hostile || m.Dead || m.Life == 0
                        || m.Mods.Effects["cannot_be_poisoned"] != 0) continue;
                    if (Dist(m.Walk.Position(positionNow), at) > SpreadPoisonRadius + LivingCollision.Size(m)) continue;
                    if (m.Poisons.Add(poisonNow, poison.DamagePerSecond, poison.Seconds))
                        Console.WriteLine($"[gateway] poison spread to {m.Label}: {poison.DamagePerSecond:0.0}/s for {poison.Seconds:0.##} s");
                }
            }
        }

        /// <summary>Flask kinds Master Distiller pays from. Matching is by the base item's name, which magic and unique
        /// flasks of that base keep.</summary>
        internal enum DistillerFlask { Bismuth, Diamond, Amethyst }

        static DistillerFlask? DistillerKind(string? baseName) => baseName switch
        {
            "Bismuth Flask" => DistillerFlask.Bismuth,
            "Diamond Flask" => DistillerFlask.Diamond,
            "Amethyst Flask" => DistillerFlask.Amethyst,
            _ => null,
        };

        /// <summary>What one Master Distiller payment would consume and grant, computed before the flask-bar commit.</summary>
        internal sealed record DistillerPlan(IReadOnlyList<(uint Id, DistillerFlask Kind, int Held)> Flasks,
            int Cost, int Penetration, int Critical, int Chaos)
        {
            /// <summary>The bonuses for the flasks the commit actually changed.</summary>
            internal DistillerBonuses? BonusesFor(IReadOnlyCollection<uint> paid)
            {
                bool bismuth = Flasks.Any(f => f.Kind == DistillerFlask.Bismuth && paid.Contains(f.Id));
                bool diamond = Flasks.Any(f => f.Kind == DistillerFlask.Diamond && paid.Contains(f.Id));
                bool amethyst = Flasks.Any(f => f.Kind == DistillerFlask.Amethyst && paid.Contains(f.Id));
                var bonuses = new DistillerBonuses(bismuth ? Penetration : 0, diamond ? Critical : 0, amethyst ? Chaos : 0);
                return bonuses.Any ? bonuses : null;
            }
        }

        /// <summary>The charge cost and the bonuses for the flask kinds in the bar, or null when this character has no
        /// Master Distiller, no bonus, or no matching flask with enough charges.</summary>
        internal static DistillerPlan? PlanDistiller(IEnumerable<(uint Id, string? BaseName, int Charges)> flasks,
            Func<string, int> get)
        {
            int cost = get("pathfinder_skills_consume_x_charges_from_a_bismuth_diamond_or_amethyst_flask");
            if (cost <= 0) return null;
            int penetration = get("pathfinder_skills_penetrate_elemental_resistances_%_if_charges_consumed_from_bismuth_flask");
            int critical = get("pathfinder_skills_critical_strike_chance_+%_if_charges_consumed_from_diamond_flask");
            int chaos = get("pathfinder_skills_physical_damage_%_to_add_as_chaos_if_charges_consumed_from_amethyst_flask");
            if (penetration <= 0 && critical <= 0 && chaos <= 0) return null;
            List<(uint Id, DistillerFlask Kind, int Held)> pay = [];
            HashSet<DistillerFlask> chosen = [];
            foreach (var (id, name, charges) in flasks)
                if (DistillerKind(name) is { } kind && charges >= cost && chosen.Add(kind)) pay.Add((id, kind, charges));
            return pay.Count == 0 ? null : new(pay, cost, penetration, critical, chaos);
        }

        /// <summary>
        /// Master Distiller: "Grant bonuses to Non-Channelling Skills you use by consuming {0} Charges from a Flask of
        /// each of the following types, if possible". One payment per accepted use, from the flask bar, in one S→C 0xbf.
        /// <paramref name="still"/> rechecks the use under the flask-bar gate, so a cancelled action spends nothing.
        /// </summary>
        internal static async Task<DistillerBonuses?> PayDistiller(string remote, NetworkStream stream,
            ConnectionCiphers ciphers, ClientState conn, Func<bool>? still = null)
        {
            DistillerPlan? plan;
            lock (conn.Monsters)
            {
                var character = CharacterPlayerStats(conn);
                int Get(string name) => Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
                plan = PlanDistiller(ItemsOf(conn).Items(PlayerInventories.Flask)
                    .Select(f => (f.Id, ItemsFor(conn).ByHash(f.Item.TypeHash)?.Name, f.Item.Charges)), Get);
            }
            if (plan is null) return null;
            List<uint> paid = [];
            await ChangeFlaskBar(remote, stream, ciphers, conn, items =>
            {
                if (still is not null && !still()) return null;
                List<InventoryItem> changed = [];
                foreach (var (id, _, held) in plan.Flasks)
                {
                    if (items.Items(PlayerInventories.Flask).FirstOrDefault(i => i.Id == id)?.Item.Charges != held) continue;
                    if (items.Change(PlayerInventories.Flask, id, i => i with { Charges = i.Charges - plan.Cost }) is { } done)
                    {
                        changed.Add(done);
                        paid.Add(id);
                    }
                }
                return changed.Count == 0 ? null : ([.. changed.Select(c => c.Id)], changed);
            }, changed => $"0x00bf flasks: Master Distiller spends {plan.Cost} charges from {string.Join(", ", changed.Select(c => $"slot {c.X}"))}");
            return paid.Count == 0 ? null : plan.BonusesFor(paid);
        }
    }
}
