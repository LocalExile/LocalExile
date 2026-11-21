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
    internal static ChatCommands Register(ChatCommands commands, Player player)
    {
        return commands
        .Add("god", "fixture god state", async args =>
        {
            bool on = args[0] == "on";
            await SetGod(player, on);
            await commands.Reply(on ? "God mode on." : "God mode off.");
        })
        .Add("gem_xp", "/gem_xp <amount|near> (dev: set the experience of every gem in the inventory and the sockets; near = one below the next level's threshold)", async args =>
        {
            if (args.Length != 1) throw new ArgumentException("an amount, or near");
            bool near = args[0].Equals("near", StringComparison.OrdinalIgnoreCase);
            int amount = near ? 0 : int.Parse(args[0]);
            var items = ItemsOf(player);
            List<(byte Row, InventoryItem Item)> changed = [];
            List<(byte[] Packet, string Label)> outputs = [];
            string? noGem = null;
            bool levelChanged = false;
            int Set(Item gem, ItemCatalog.GemType type)
            {
                int level = ItemsFor(player).GemLevel(gem);
                int max = Math.Min(SkillsFor(player).MaxLevelAt(type.Variant(gem.GemVariant).Effect, player.Pools.Level), type.MaxLevel);
                return near ? (level < max ? type.ExperienceFor(level + 1) - 1 : gem.GemExperience) : amount;
            }
            using (Game.ProjectileMutation(player, advance: false, phase: "gem experience command"))
            {
                IReadOnlyList<Game.PlayerSkillSource> sourcesBefore = Game.CaptureGemSourceRowsLocked(player);
                foreach (var held in items.Items(PlayerInventories.MainInventory))
                    if (ItemsFor(player).ByHash(held.Item.TypeHash) is { } baseType && ItemsFor(player).Gem(baseType) is { } type)
                        if (items.Change(PlayerInventories.MainInventory, held.Id, it =>
                            {
                                var afterGem = it with { GemExperience = Set(it, type) };
                                levelChanged |= ItemsFor(player).GemLevel(afterGem) != ItemsFor(player).GemLevel(it);
                                return afterGem;
                            }) is { } after)
                            changed.Add((PlayerInventories.MainInventory, after));
                foreach (var (row, item, gems) in items.ChangeSocketedGems((_, gem) =>
                    ItemsFor(player).ByHash(gem.TypeHash) is { } baseType && ItemsFor(player).Gem(baseType) is { } type
                        ? gem with { GemExperience = Set(gem, type) } : null))
                {
                    levelChanged |= gems.Any(pair => ItemsFor(player).GemLevel(pair.After) != ItemsFor(player).GemLevel(pair.Before));
                    changed.Add((row, item));
                }
                if (changed.Count == 0) noGem = "No gem to change";
                else
                {
                    if (levelChanged)
                    {
                        using (new Game.PlayerStatScope(player, fresh: true))
                        {
                            Game.RecomputePoolsAndSpeed(player);
                            Game.ReconcileBeastCompanionsLocked(player);
                            Game.ReconcileEquipmentSourceLifetimesLocked(player, sourcesBefore);
                        }
                    }
                    foreach (var row in changed.GroupBy(c => c.Row))
                        outputs.Add((InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key),
                            [.. row.Select(c => c.Item.Id)], [.. row.Select(c => c.Item)]),
                            $"0x00bf inventory {row.Key}: /gem_xp {(near ? "near" : amount.ToString())} ({string.Join(", ", row.Select(c => $"{c.Item.Id:x} exp {c.Item.Item.GemExperience}"))})"));
                }
            }
            if (noGem is not null) { await player.Commands!.Reply(noGem); return; }
            foreach (var (packet, label) in outputs)
                await Send(player, packet, label);
        });
    }
    internal sealed record DevEnemyHit(int Damage, string Mitigation, uint Life, uint Mana, uint ManaBefore,
        bool Blocked, int BlockChance, bool Suppressed, (int Chance, int Effect, bool Lucky, bool Unlucky) Suppression);

    // /hit and /spellhit are synthetic incoming enemy contacts. /blockhit can additionally block.
    // Admission, prior-history decision, pool mutation and recording are a single combat commit.
    internal static bool TryDevEnemyHit(Player player, DamageType type, int amount, bool block, bool spell, bool suppress,
        out DevEnemyHit hit, bool ranged = false)
    {
        {
            hit = null!;
            if (player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0) return false;
            double now = player.Clock.Elapsed.TotalSeconds;
            int priorContacts = player.RecentEnemyHits.Count(now);
            using var defenceTime = FortificationOf(player).Capture(now);
            // Refresh lazy aura/Fortification pools before reading ANY final defence at the accepted contact.
            var stats = CharacterPlayerStats(player);
            GainLifeBeforeEnemyHitLocked(player, stats);
            int resistance = Resistance(player, type), maximum = MaxResistance(player, type);
            // Avoidance rolls before block, as for a monster hit; an avoided hit still lands.
            bool avoided = PlayerDamageTaken.Avoids(type, name => stats.GetValueOrDefault(name),
                player.IncomingHitRandom);
            int blockChance = spell ? player.Pools.SpellBlockChance : player.Pools.BlockChance;
            bool luckyBlock = spell ? player.Pools.SpellBlockLucky : player.Pools.AttackBlockLucky;
            bool unluckyBlock = spell ? player.Pools.SpellBlockUnlucky : player.Pools.AttackBlockUnlucky;
            bool blocked = block && DamageModel.Blocks(blockChance, player.IncomingHitRandom.NextDouble() * 100, luckyBlock, unluckyBlock);
            if (!blocked && DamageTypes.IsElemental(type) && GamblerFor(stats).UsesNinetyResistance(player.IncomingHitRandom))
                resistance = maximum = 90;
            bool immune = PlayerImmunities(player).Contains(type);
            bool armourRelevant = type == DamageType.Physical ? amount > 0
                : PlayerHitMitigation.ArmourPercent(player.Pools, type, name => stats.GetValueOrDefault(name)) > 0
                    && DamageModel.Mitigate(amount, resistance, maximum) > 0;
            var armour = IncomingArmourLocked(player, !blocked && !immune && !avoided && armourRelevant, priorContacts, stats);
            int dealt = immune || avoided ? 0 : MitigateTakenWithArmour(player, type, amount, resistance, maximum, armour.EffectiveArmour,
                stats, attack: !spell, ranged: ranged && !spell, spell: spell);
            // A blocked hit deals only its share ("You take X% of Damage from Blocked Hits", 0 by default).
            if (blocked) dealt = (int)Math.Round(dealt * PlayerDamageTaken.BlockedPct(type,
                name => stats.GetValueOrDefault(name)) / 100.0);
            string mitigation = immune ? "immunity" : avoided ? "avoidance"
                : MitigationLabel(player, type, amount, resistance, maximum, armour.EffectiveArmour, stats);
            var suppression = suppress ? Suppression(player) : default;
            bool suppressed = suppress && DamageModel.Suppresses(suppression.Chance,
                player.IncomingHitRandom.Next(100), suppression.Lucky, suppression.Unlucky);
            if (suppressed) dealt = DamageModel.Suppress(dealt, suppression.Effect);
            uint manaBefore = player.PlayerMana;
            var onBlock = blocked ? BlockRecovery(player, stats) : default;
            int gainedLife = blocked ? player.Pools.LifeGainedOnBlock + onBlock.Life : 0;
            int gainedMana = blocked ? player.Pools.ManaGainedOnBlock + onBlock.Mana : 0;
            var taken = DamagePacket.Of(type, dealt, dealt);
            RecoupHitLocked(player, taken, now, stats);
            if (type == DamageType.Physical) PreventedDamageOf(player).Add(now, amount - dealt);
            uint lifeBefore = player.PlayerLife;
            player.PlayerLife = (uint)Math.Min(LifeCap(player), Math.Max(0, (long)player.PlayerLife
                - DamagePlayerHitTyped(player, type == DamageType.Physical ? dealt : 0, type is not (DamageType.Physical or DamageType.Chaos) ? dealt : 0, type == DamageType.Chaos ? dealt : 0, now, stats,
                    taken) + gainedLife));
            if (block) player.PlayerMana = (uint)Math.Min((uint)UnreservedMana(player), (long)player.PlayerMana + gainedMana);
            player.EnergyShield.Recover(onBlock.EnergyShield);
            TrimLeechLocked(player);
            player.RecentEnemyHits.Record(now);
            player.RecentEvents.Record(StatConditions.BeenHit, now);
            UnhingedHitRecoveryLocked(player, now, stats);
            if (blocked)
            {
                player.RecentEvents.Record(StatConditions.Blocked, now);
                player.RecentEvents.Record(spell ? StatConditions.BlockedSpell : StatConditions.BlockedAttack, now);
            }
            if (suppressed) player.RecentEvents.Record(StatConditions.SuppressedSpell, now);
            if (lifeBefore > player.PlayerLife && (long)(lifeBefore - player.PlayerLife) * 100 >= (long)player.PlayerMaxLife * 15)
                player.RecentEvents.Record(StatConditions.TakenSavageHit, now);
            if (block) ChargesWhenHit(player, blocked, spell);
            if (player.PlayerLife == 0) { player.InvalidateIncomingHits(); ClearAntiquarianOnDeath(player); }
            RecordHardcoreDeath(player);
            hit = new(dealt, mitigation, player.PlayerLife, player.PlayerMana, manaBefore,
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
    internal static async Task SetGod(Player player, bool on)
    {
        player.God = on;
        if (on) GodCharacters[CharacterName(player)] = true; else GodCharacters.TryRemove(CharacterName(player), out _);
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        player.PlayerLife = on ? LifeCap(player) : LifeReservation.Clamp(player.PlayerLife, LifeReservationOf(player));
        player.PlayerMana = on ? (uint)UnreservedMana(player) : Math.Min(player.PlayerMana, (uint)UnreservedMana(player));
        TrimLeechLocked(player);
        RecomputePoolsAndSpeed(player);
        Console.WriteLine($"[gateway] {player.Connection.Remote} god mode {(on ? "on" : "off")}: life {player.Pools.Life}, mana {player.Pools.Mana}, {ReservedMana(player)} reserved");
        await SendCharacterStats(player, PlayerStatChange.God);
        await SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana}");
        // UI built on entering caches the attributes (see SendPlayer): send them once more.
        await SendPlayerStatSnapshot(player,
            () => InstanceEntities.StatsUpdate(player.Id, [.. CharacterPlayerStats(player).Select(p => (p.Key, p.Value))]), "0x01b3 player attributes");
    }
}
