using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static global::Game;
using static global::Program;

namespace GameServer.Gateway.Tests;

// Synthetic test drivers for the public game API. The shipped development commands now belong to gm-commands;
// these fixtures verify source mutation and send-gate contracts, not acceptance of that optional mod.
static class ConditionalSourceTestDrivers
{
    internal static ChatCommands Register(ChatCommands commands, string remote, NetworkStream stream,
        ConnectionCiphers ciphers, ClientState conn)
    {
        return commands
        .Add("set_stat", "fixture attribute override", async args =>
        {
            if (args[0] is not ("strength" or "dexterity" or "intelligence"))
                throw new ArgumentException("This fixture changes attributes only");
            using (ProjectileMutation(conn, advance: false))
            {
                var before = CaptureGemSourceRowsLocked(conn);
                int value = int.Parse(args[1]);
                conn.PlayerStats[Stats.Key(args[0])!.Value] = value;
                conn.PlayerStats[Stats.Key($"virtual_{args[0]}_for_requirements")!.Value] = value;
                RecomputePoolsAndSpeed(conn);
                ReconcileEquipmentSourceLifetimesLocked(conn, before);
            }
            await SendCharacterStats(remote, stream, ciphers, conn, "fixture attributes");
        })
        .Add("set_aoe", "fixture area override", async args =>
        {
            uint key = Stats.Key("base_skill_area_of_effect_+%")!.Value;
            bool off = args[0] == "off";
            using (ProjectileMutation(conn, advance: false))
            {
                if (off) conn.PlayerStats.Remove(key); else conn.PlayerStats[key] = int.Parse(args[0]);
                RecomputePoolsAndSpeed(conn);
            }
            await SendPlayerStatSnapshot(remote, stream, ciphers, conn,
                () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)), "fixture area");
            await SendSkills(remote, stream, ciphers, conn);
            await commands.Reply(off ? "Increased area of effect: the character's own" : $"Increased area of effect: {args[0]}%");
        })
        .Add("god", "fixture god state", async args =>
        {
            bool on = args[0] == "on";
            await SetGod(remote, stream, ciphers, conn, on);
            await commands.Reply(on ? "God mode on." : "God mode off.");
        })
        .Add("gem_xp", "/gem_xp <amount|near> (dev: set the experience of every gem in the inventory and the sockets; near = one below the next level's threshold)", async args =>
        {
            if (args.Length != 1) throw new ArgumentException("an amount, or near");
            bool near = args[0].Equals("near", StringComparison.OrdinalIgnoreCase);
            int amount = near ? 0 : int.Parse(args[0]);
            var items = ItemsOf(conn);
            List<(byte Row, InventoryItem Item)> changed = [];
            List<(byte[] Packet, string Label)> outputs = [];
            string? noGem = null;
            bool levelChanged = false;
            int Set(Item gem, ItemCatalog.GemType type)
            {
                int level = ItemsFor(conn).GemLevel(gem);
                int max = Math.Min(SkillsFor(conn).MaxLevelAt(type.Variant(gem.GemVariant).Effect, conn.Pools.Level), type.MaxLevel);
                return near ? (level < max ? type.ExperienceFor(level + 1) - 1 : gem.GemExperience) : amount;
            }
            using (Game.ProjectileMutation(conn, advance: false, phase: "gem experience command"))
            {
                IReadOnlyList<Game.PlayerSkillSource> sourcesBefore = Game.CaptureGemSourceRowsLocked(conn);
                foreach (var held in items.Items(PlayerInventories.MainInventory))
                    if (ItemsFor(conn).ByHash(held.Item.TypeHash) is { } baseType && ItemsFor(conn).Gem(baseType) is { } type)
                        if (items.Change(PlayerInventories.MainInventory, held.Id, it =>
                            {
                                var afterGem = it with { GemExperience = Set(it, type) };
                                levelChanged |= ItemsFor(conn).GemLevel(afterGem) != ItemsFor(conn).GemLevel(it);
                                return afterGem;
                            }) is { } after)
                            changed.Add((PlayerInventories.MainInventory, after));
                foreach (var (row, item, gems) in items.ChangeSocketedGems((_, gem) =>
                    ItemsFor(conn).ByHash(gem.TypeHash) is { } baseType && ItemsFor(conn).Gem(baseType) is { } type
                        ? gem with { GemExperience = Set(gem, type) } : null))
                {
                    levelChanged |= gems.Any(pair => ItemsFor(conn).GemLevel(pair.After) != ItemsFor(conn).GemLevel(pair.Before));
                    changed.Add((row, item));
                }
                if (changed.Count == 0) noGem = "No gem to change";
                else
                {
                    if (levelChanged)
                    {
                        using (new Game.PlayerStatScope(conn, fresh: true))
                        {
                            Game.RecomputePoolsAndSpeed(conn);
                            Game.ReconcileBeastCompanionsLocked(conn);
                            Game.ReconcileEquipmentSourceLifetimesLocked(conn, sourcesBefore);
                        }
                    }
                    foreach (var row in changed.GroupBy(c => c.Row))
                        outputs.Add((InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key),
                            [.. row.Select(c => c.Item.Id)], [.. row.Select(c => c.Item)]),
                            $"0x00bf inventory {row.Key}: /gem_xp {(near ? "near" : amount.ToString())} ({string.Join(", ", row.Select(c => $"{c.Item.Id:x} exp {c.Item.Item.GemExperience}"))})"));
                }
            }
            if (noGem is not null) { await conn.Commands!.Reply(noGem); return; }
            foreach (var (packet, label) in outputs)
                await Send(remote, stream, ciphers, packet, label);
        });
    }
    internal sealed record DevEnemyHit(int Damage, string Mitigation, uint Life, uint Mana, uint ManaBefore,
        bool Blocked, int BlockChance, bool Suppressed, (int Chance, int Effect, bool Lucky, bool Unlucky) Suppression);

    // /hit and /spellhit are synthetic incoming enemy contacts. /blockhit can additionally block.
    // Admission, prior-history decision, pool mutation and recording are a single combat commit.
    internal static bool TryDevEnemyHit(ClientState conn, DamageType type, int amount, bool block, bool spell, bool suppress,
        out DevEnemyHit hit, bool ranged = false)
    {
        lock (conn.Monsters)
        {
            hit = null!;
            if (conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0) return false;
            double now = conn.Clock.Elapsed.TotalSeconds;
            int priorContacts = conn.RecentEnemyHits.Count(now);
            using var defenceTime = FortificationOf(conn).Capture(now);
            // Refresh lazy aura/Fortification pools before reading ANY final defence at the accepted contact.
            var stats = CharacterPlayerStats(conn);
            GainLifeBeforeEnemyHitLocked(conn, stats);
            int resistance = Resistance(conn, type), maximum = MaxResistance(conn, type);
            // Avoidance rolls before block, as for a monster hit; an avoided hit still lands.
            bool avoided = PlayerDamageTaken.Avoids(type, name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0,
                conn.IncomingHitRandom);
            int blockChance = spell ? conn.Pools.SpellBlockChance : conn.Pools.BlockChance;
            bool luckyBlock = spell ? conn.Pools.SpellBlockLucky : conn.Pools.AttackBlockLucky;
            bool unluckyBlock = spell ? conn.Pools.SpellBlockUnlucky : conn.Pools.AttackBlockUnlucky;
            bool blocked = block && DamageModel.Blocks(blockChance, conn.IncomingHitRandom.NextDouble() * 100, luckyBlock, unluckyBlock);
            if (!blocked && DamageTypes.IsElemental(type) && GamblerFor(stats).UsesNinetyResistance(conn.IncomingHitRandom))
                resistance = maximum = 90;
            bool immune = PlayerImmunities(conn).Contains(type);
            bool armourRelevant = type == DamageType.Physical ? amount > 0
                : PlayerHitMitigation.ArmourPercent(conn.Pools, type, name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0) > 0
                    && DamageModel.Mitigate(amount, resistance, maximum) > 0;
            var armour = IncomingArmourLocked(conn, !blocked && !immune && !avoided && armourRelevant, priorContacts, stats);
            int dealt = immune || avoided ? 0 : MitigateTakenWithArmour(conn, type, amount, resistance, maximum, armour.EffectiveArmour,
                stats, attack: !spell, ranged: ranged && !spell, spell: spell);
            // A blocked hit deals only its share ("You take X% of Damage from Blocked Hits", 0 by default).
            if (blocked) dealt = (int)Math.Round(dealt * PlayerDamageTaken.BlockedPct(type,
                name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0) / 100.0);
            string mitigation = immune ? "immunity" : avoided ? "avoidance"
                : MitigationLabel(conn, type, amount, resistance, maximum, armour.EffectiveArmour, stats);
            var suppression = suppress ? Suppression(conn) : default;
            bool suppressed = suppress && DamageModel.Suppresses(suppression.Chance,
                conn.IncomingHitRandom.Next(100), suppression.Lucky, suppression.Unlucky);
            if (suppressed) dealt = DamageModel.Suppress(dealt, suppression.Effect);
            uint manaBefore = conn.PlayerMana;
            var onBlock = blocked ? BlockRecovery(conn, stats) : default;
            int gainedLife = blocked ? conn.Pools.LifeGainedOnBlock + onBlock.Life : 0;
            int gainedMana = blocked ? conn.Pools.ManaGainedOnBlock + onBlock.Mana : 0;
            var taken = DamagePacket.Of(type, dealt, dealt);
            RecoupHitLocked(conn, taken, now, stats);
            if (type == DamageType.Physical) PreventedDamageOf(conn).Add(now, amount - dealt);
            uint lifeBefore = conn.PlayerLife;
            conn.PlayerLife = (uint)Math.Min(LifeCap(conn), Math.Max(0, (long)conn.PlayerLife
                - DamagePlayerHitTyped(conn, type == DamageType.Physical ? dealt : 0, type is not (DamageType.Physical or DamageType.Chaos) ? dealt : 0, type == DamageType.Chaos ? dealt : 0, now, stats,
                    taken) + gainedLife));
            if (block) conn.PlayerMana = (uint)Math.Min((uint)UnreservedMana(conn), (long)conn.PlayerMana + gainedMana);
            conn.EnergyShield.Recover(onBlock.EnergyShield);
            TrimLeechLocked(conn);
            conn.RecentEnemyHits.Record(now);
            conn.RecentEvents.Record(StatConditions.BeenHit, now);
            UnhingedHitRecoveryLocked(conn, now, stats);
            if (blocked)
            {
                conn.RecentEvents.Record(StatConditions.Blocked, now);
                conn.RecentEvents.Record(spell ? StatConditions.BlockedSpell : StatConditions.BlockedAttack, now);
            }
            if (suppressed) conn.RecentEvents.Record(StatConditions.SuppressedSpell, now);
            if (lifeBefore > conn.PlayerLife && (long)(lifeBefore - conn.PlayerLife) * 100 >= (long)conn.PlayerMaxLife * 15)
                conn.RecentEvents.Record(StatConditions.TakenSavageHit, now);
            if (block) ChargesWhenHit(conn, blocked, spell);
            if (conn.PlayerLife == 0) { conn.InvalidateIncomingHits(); ClearAntiquarianOnDeath(conn); }
            RecordHardcoreDeath(conn);
            hit = new(dealt, mitigation, conn.PlayerLife, conn.PlayerMana, manaBefore,
                blocked, blockChance, suppressed, suppression);
            return true;
        }
    }
    internal static Item? CreateClusterJewel(int size, int passives, int sockets, string? theme = null,
        IEnumerable<string>? notables = null)
    {
        string name = size switch { 2 => "Large Cluster Jewel", 1 => "Medium Cluster Jewel", 0 => "Small Cluster Jewel", _ => "" };
        if (name.Length == 0 || Items.Find(name) is not { } baseType) return null;
        var asked = notables?.ToList() ?? [];
        var mods = new List<ItemMod>();
        // "Adds N Passive Skills": one enchantment mod whose two stats are the node index and the count.
        if (Items.Mods.ById("JewelExpansionPassiveNodes") is { } count && count.Stats.Count >= 2)
            mods.Add(new ItemMod(ModSection.Enchant, count.Hash, [0, passives]));
        string socketMod = size switch
        {
            2 => sockets >= 2 ? "JewelExpansionJewelNodesLarge2___" : "JewelExpansionJewelNodesLarge1",
            1 => "JewelExpansionJewelNodesMedium",
            _ => "",
        };
        if (sockets > 0 && socketMod.Length > 0 && Items.Mods.ById(socketMod) is { } socket && socket.Stats.Count > 0)
            mods.Add(new ItemMod(ModSection.Enchant, socket.Hash, [.. socket.MaxValues()]));
        // The theme: what the cluster's small passives grant ("Added Small Passive Skills grant: ..."). It is an
        // enchantment (like the count and the sockets); a smaller jewel in a larger socket is padded by the client
        // with empty nodes instead, see JewelFitsCell.
        if (Items.Mods.ById(theme ?? "AfflictionJewelSmallPassivesGrantLife_") is { } themed && themed.Stats.Count > 0)
            mods.Add(new ItemMod(ModSection.Enchant, themed.Hash, [.. themed.MaxValues()]));
        // "1 Added Passive Skill is <notable>": an explicit prefix, one per notable the jewel has.
        foreach (string notable in asked)
            if (Items.Mods.ById(notable) is { } found && found.Stats.Count > 0)
                mods.Add(new ItemMod(ModSection.Explicit, found.Hash, [.. found.MaxValues()]));
        return ItemCatalog.Create(baseType) with
        {
            // The notables are the item's only affixes. A jewel without one is Normal, one or two are Magic (a magic
            // item may not have zero affixes: the client then renders the whole cluster as empty "Nothingness" nodes)
            // and three (a large cluster can carry three) Rare.
            Rarity = asked.Count switch { 0 => ItemRarity.Normal, <= 2 => ItemRarity.Magic, _ => ItemRarity.Rare },
            ItemLevel = 68,
            Mods = mods,
        };
    }
    internal static async Task SetGod(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool on)
    {
        lock (conn.Monsters)
        {
            conn.God = on;
            if (on) GodCharacters[CharacterName(conn)] = true; else GodCharacters.TryRemove(CharacterName(conn), out _);
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
            conn.PlayerLife = on ? LifeCap(conn) : LifeReservation.Clamp(conn.PlayerLife, LifeReservationOf(conn));
            conn.PlayerMana = on ? (uint)UnreservedMana(conn) : Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
            TrimLeechLocked(conn);
            RecomputePoolsAndSpeed(conn);
        }
        Console.WriteLine($"[gateway] {remote} god mode {(on ? "on" : "off")}: life {conn.Pools.Life}, mana {conn.Pools.Mana}, {ReservedMana(conn)} reserved");
        await SendCharacterStats(remote, stream, ciphers, conn, on ? "god mode on" : "god mode off");
        await Send(remote, stream, ciphers, PlayerLifeUpdate(conn), $"0x01b1 player life {conn.PlayerLife}");
        await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana}");
        // UI built on entering caches the attributes (see SendPlayer): send them once more.
        await SendPlayerStatSnapshot(remote, stream, ciphers, conn,
            () => InstanceEntities.StatsUpdate(PlayerId, [.. BaseStats(conn)]), "0x01b3 player attributes");
    }
}
