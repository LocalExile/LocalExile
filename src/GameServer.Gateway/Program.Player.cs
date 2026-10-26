using System.Diagnostics;
using System.Net;
using System.Text.Json;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using PlayerWalk = GameServer.Core.World.PlayerWalk;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using Progression = GameServer.Core.World.Progression;
using SaveFiles = GameServer.Core.Saves.SaveFiles;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageRange = GameServer.Core.World.DamageRange;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using DamageOverTime = GameServer.Core.World.DamageOverTime;

partial class Program
{
    static async Task SetGod(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool on)
    {
        lock (conn.Monsters)
        {
            conn.God = on;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
            conn.PlayerLife = on ? UnreservedLife(conn) : Math.Min(conn.PlayerLife, UnreservedLife(conn));
            conn.PlayerMana = on ? (uint)UnreservedMana(conn) : Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
        }
        Console.WriteLine($"[gateway] {remote} god mode {(on ? "on" : "off")}: life {conn.Pools.Life}, mana {conn.Pools.Mana}, {ReservedMana(conn)} reserved");
        await SendCharacterStats(remote, stream, ciphers, conn, on ? "god mode on" : "god mode off");
        await Send(remote, stream, ciphers, PlayerLifeUpdate(conn), $"0x01b1 player life {conn.PlayerLife}");
        await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana}");
        // UI built on entering caches the attributes (see SendPlayer): send them once more.
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [.. BaseStats(conn)]), "0x01b3 player attributes");
    }

    // The player's life per second from regeneration: the flat life regeneration per minute (/god, /set_stat) plus a
    // percentage of maximum life, from the row PanelStats.Recovery sends to the character panel.
    static double LifeRegenPerSecond(ClientState conn)
    {
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        return PanelStats.LifePerMinute(Get, conn.Pools.Life) / 60;
    }

    // The player's mana per second from regeneration: Character.ot's mana_regeneration_rate_per_minute_% (105: 1.75% of
    // maximum mana per second) plus the stats' flat base_mana_regeneration_rate_per_minute (/god), raised by
    // mana_regeneration_rate_+%.
    static double ManaRegenPerSecond(ClientState conn)
    {
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        return PanelStats.ManaPerMinute(Get, conn.Pools.Mana) / 60;
    }

    // The mana the running auras reserve, and what is left for casting and regeneration.
    // Restoring an already enabled aura is not a fresh cast: transient mana availability must not discard its saved intent.
    static IReadOnlyList<AuraCatalog.Aura> RestoreRememberedAuras(ClientState conn, IEnumerable<string> saved)
    {
        var wanted = saved.ToHashSet(StringComparer.Ordinal);
        var candidates = new List<AuraCatalog.Aura>();
        foreach (var gem in GemSkillEffects(conn))
            if (Skills.EffectId(gem.Effect) is { } effect && wanted.Contains(effect) && AuraCatalog.Supported(effect)
                && SkillRestrictionReason(conn, gem.Effect) is null
                && !conn.Auras.Values.Any(a => a.EffectId == effect)
                && SupportedReservation(conn, gem.Id) is { } reservation)
                candidates.Add(new(gem.Id, gem.Effect, effect, gem.Name, conn.AllocateBuff(), AuraCatalog.BuffRow(effect),
                    reservation.Flat, reservation.Percent, SupportedSkill(conn, gem.Id).Level, SupportedSkill(conn, gem.Id).Quality, SourceBuff: conn.AllocateBuff(), ReserveLife: ReservesLife(conn)));
        var restored = AuraCatalog.Restore(saved, candidates, conn.Pools, InTown(conn));
        var accepted = new List<AuraCatalog.Aura>();
        foreach (var aura in restored)
            if (AuraCatalog.RequiredLife(conn.Pools, conn.Auras.Values.Append(aura)) < conn.Pools.Life)
            {
                conn.Auras[aura.SkillId] = aura;
                accepted.Add(aura);
            }
        if (restored.Count > 0)
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
            Console.WriteLine($"[gateway] {CharacterName(conn)}: restored saved auras: {string.Join(", ", restored.Select(a => a.EffectId))}");
        }
        return accepted;
    }

    static bool InTown(ClientState conn) => WaypointCatalog.Areas.Find(conn.Layout.Hash) is { IsTown: true };

    static async Task EndAurasOnDeath(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.PlayerLife != 0 || conn.AuraDeathHandled) return;
        conn.AuraDeathHandled = true;
        CharacterSaves.ClearActiveAuras(CharacterName(conn));
        List<AuraCatalog.Aura> ended;
        lock (conn.Monsters)
        {
            ended = [.. conn.Auras.Values];
            conn.Auras.Clear();
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        foreach (var aura in ended)
            foreach (var packet in AuraCatalog.EndPackets(aura, PlayerId))
                await Send(remote, stream, ciphers, packet, $"aura {aura.EffectId} ends on death");
        if (ended.Count > 0)
        {
            await SendCharacterStats(remote, stream, ciphers, conn, "auras cleared on death");
            await SendPlayerMana(remote, stream, ciphers, conn, "aura reservation cleared on death");
        }
    }

    static async Task PlayerDied(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string label)
    {
        ChargesDied(conn);
        EnduringCryDied(conn);
        TemporaryPassiveBuffs.For(CharacterName(conn)).Clear();
        RageOf(conn).Clear();
        conn.Summons?.Clear(conn.SummonLease);
        conn.Companions?.Clear(conn.SummonLease);
        await RefreshSummons(remote, stream, ciphers, conn);
        await RefreshCompanions(remote, stream, ciphers, conn);
        await EndAurasOnDeath(remote, stream, ciphers, conn);
        await Send(remote, stream, ciphers, InstanceProtocol.DeathScreen(DeathScreenFlags(conn)), label);
    }

    static int ReservedMana(ClientState conn)
    {
        lock (conn.Monsters) return AuraCatalog.ReservedMana(conn.Pools, conn.Auras.Values);
    }

    static int UnreservedMana(ClientState conn) => Math.Max(0, (int)conn.Pools.Mana - ReservedMana(conn));

    static int ReservedLife(ClientState conn)
    {
        lock (conn.Monsters) return AuraCatalog.ReservedLife(conn.Pools, conn.Auras.Values);
    }

    static uint UnreservedLife(ClientState conn) => (uint)Math.Max(0, (long)conn.Pools.Life - ReservedLife(conn));

    static byte[] PlayerLifeUpdate(ClientState conn) => InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife, (uint)ReservedLife(conn));

    // 0x1b1 for the mana pool: every update carries the reserved amount, or the client would clear it.
    static async Task SendPlayerMana(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        string? log = null)
    {
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMana, (uint)ReservedMana(conn), pool: ManaPool), log);
        await SendPlayerEnergyShield(remote, stream, ciphers, conn);
    }

    static async Task<bool> SendPlayerEnergyShield(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, Func<bool>? still = null)
    {
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        bool currentRevision = false;
        await SendGenerated(remote, stream, ciphers, () =>
        {
            lock (conn.Monsters)
            {
                if (still?.Invoke() == false) return null;
                currentRevision = true;
                if (conn.PlayerLife == 0) conn.EnergyShield.Empty();
                uint current = conn.EnergyShield.Current;
                bool active = conn.EnergyShield.Recharging;
                int currentRate = conn.PlayerLife == 0 ? 0
                    : (int)Math.Round(EnergyShield.PredictionPerMinute(conn.Pools.EnergyShield, Get, active));
                if (conn.SentEnergyShield == current && conn.SentRechargeActive == active
                    && conn.SentEsRecoveryRate == currentRate) return null;
                var packets = new List<byte[]> { InstanceProtocol.LifeUpdate(PlayerId, current, pool: 2) };
                if (conn.SentEsRecoveryRate != currentRate && Stats.Key("energy_shield_recovery_per_minute") is uint key)
                    packets.Add(InstanceEntities.StatsUpdate(PlayerId, [(key, currentRate)]));
                conn.SentEnergyShield = current;
                conn.SentRechargeActive = active;
                conn.SentEsRecoveryRate = currentRate;
                return packets.SelectMany(p => p).ToArray();
            }
        }, "0x01b1 current player energy shield/recovery");
        return currentRevision;
    }

    static SkillCost CostFor(ClientState conn, int effect, int level, int quality, ushort skillId = 0)
    {
        var supported = skillId == 0 ? null : SupportedSkill(conn, skillId);
        var own = supported?.Stats ?? Skills.EffectStats(effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        var player = CharacterPlayerStats(conn);
        int Own(string name) => Stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) + own.GetValueOrDefault(key) : 0;
        int Cost(string name) => (int)Math.Clamp(Math.Floor(Own(name) * (supported?.CostMultiplier ?? 1)), 0, int.MaxValue);
        return SkillCost.Calculate(Cost("base_mana_cost"), Cost("base_life_cost"), Get);
    }

    static bool PaySkillCost(ClientState conn, int effect, int level, int quality, out SkillCost cost, ushort skillId = 0)
    {
        cost = CostFor(conn, effect, level, quality, skillId);
        lock (conn.Monsters)
        {
            uint mana = conn.PlayerMana, life = conn.PlayerLife;
            if (!cost.Pay(ref mana, ref life)) return false;
            (conn.PlayerMana, conn.PlayerLife) = (mana, life);
            RageOf(conn).SpendMana(cost.Mana, DateTimeOffset.UtcNow, RageInputs(conn, CharacterStatSources(conn)));
            return true;
        }
    }

    // A cast's time in ms: the same level, effective quality and character modifiers as the client skill record.
    static int? CastMs(ClientState conn, int effect, int level, int quality, ushort skillId = 0)
    {
        if (skillId == 0) return Skills.CastTimeMs(effect, level, quality, CharacterPlayerStats(conn));
        if (Skills.IsWarcry(effect))
        {
            var cry = SupportedSkill(conn, skillId);
            return WarcryTiming.ForSkill(Skills, Stats, effect, cry.Level, cry.Quality,
                CharacterPlayerStats(conn), cry.Stats)?.DurationMilliseconds;
        }
        var stats = SupportedStatsFor(conn, skillId, CharacterPlayerStats(conn));
        int speed = Stats.Key("cast_speed_+%") is uint key ? stats.GetValueOrDefault(key) : 0;
        return SpellCastTiming.ForSkill(Skills, effect, speed, ActionSpeed.Multiplier(stats, Stats.Key))?.DurationMilliseconds;
    }

    // Righteous Fire (GrantedEffects RighteousFire): casting it puts the righteous_fire buff (BuffDefinitions row 12: the
    // flames around the player, BuffVisuals 15) on the player with S→C 0x1c1. While it lasts the player burns for 90% of
    // maximum life per second, nonlethal (base_nonlethal_fire_damage_%_of_maximum_life_taken_per_minute 5400), and enemies
    // within its radius (active_skill_base_area_of_effect_radius 18 cells at level 1, plus their size) take 70% of the
    // player's maximum life per second as fire damage (base_righteous_fire_%_of_max_life_to_deal_to_nearby_per_minute 4200)
    // and carry the righteous_fire_aura buff (row 13). It ends at death or on leaving the area; casting it again does nothing.
    static bool IsRighteousFire(int effect) => Skills.EffectId(effect) == "RighteousFire";

    // The skill_art_variation of the skill effects worn in a gem type's Cosmetics tab slots (0: none), which picks the
    // skill's art (SkillArt).
    static int SkillArtVariation(ClientState conn, uint gem) =>
        Stats.Key("skill_art_variation") is not uint key ? 0
        : CharacterSaves.SkillCosmetics(CharacterName(conn)).Where(kv => CharacterSaves.SkillCosmeticGem(kv.Key) == gem)
            .SelectMany(kv => Mtx.Find(kv.Value.Record) is { } r && MtxEffects.Of(r.MtxTypeHash) is { } fx ? fx.Stats : [])
            .Where(st => st.Stat == key).Select(st => st.Value).FirstOrDefault();

    // Righteous Fire's ring stays on the player. It is an object of its own, which the client moves only when the server
    // says so, so every RingFollowMs (RunRingFollow) and after each move order it glides (0x1ba) to where the player will be
    // RingFollowMs later, along the walk's path. Before 2026-10-23 it glided once per ground click to where that walk
    // ended: a walk started any other way (a click on a stash or an item, C→S 0x24 → Walk.Approach) never moved it, and
    // the straight glide cut the path's corners.
    static async Task FollowRighteousFire(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.RighteousFire is not { Ring: not null } rf) return;
        await rf.RingGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(conn.RighteousFire, rf) || !rf.RingCreated || rf.Ring is not uint ring) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var to = conn.Walk.Position(now + RingFollowMs / 1000.0);
            if (rf.RingAt == to) return;
            rf.RingAt = to;
            await Send(remote, stream, ciphers, InstanceProtocol.MoveTo(ring, to.X, to.Y, RingFollowMs / 1000f, u28: TerrainHeight), null);
        }
        finally { rf.RingGate.Release(); }
    }

    // The ring's follow loop, while its fire burns.
    static async Task RunRingFollow(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, RighteousFireState rf)
    {
        try
        {
            while (!conn.Closed && ReferenceEquals(conn.RighteousFire, rf))
            {
                await Task.Delay(RingFollowMs);
                if (!conn.Paused && ReferenceEquals(conn.RighteousFire, rf)) await FollowRighteousFire(remote, stream, ciphers, conn);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection closed while sending.
        }
    }
    // Refresh a running gem in place after its effective level or quality changes, without paying another cast cost.
    static async Task RefreshRighteousFireStats(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, Func<bool>? still = null)
    {
        if (still?.Invoke() == false) return;
        if (conn.RighteousFire is not { } rf) return;
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == rf.Skill);
        if (gem.Name is null) return; // EndSkillEffects handles a gem that left the equipment.
        if (SkillRestrictionReason(conn, gem.Effect) is { } restriction)
        {
            await EndRighteousFire(remote, stream, ciphers, conn, restriction);
            return;
        }
        var stats = SupportedStatsFor(conn, gem.Id, FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2));
        int Get(string name, int fallback = 0) => Stats.Key(name) is uint key && stats.TryGetValue(key, out int value) ? value : fallback;
        int radius = SkillArea.Radius(Get("active_skill_base_area_of_effect_radius", 18), name => Get(name), spell: true, combined: true);
        await rf.RingGate.WaitAsync();
        try
        {
            // Charge-dependent radius can expire while waiting behind another encrypted send. Commit the radius,
            // its replacement identity and the complete remove/create/wake transition at one admitted boundary.
            await SendGenerated(remote, stream, ciphers, () =>
            {
                lock (conn.Monsters)
                {
                    if (still?.Invoke() == false || !ReferenceEquals(conn.RighteousFire, rf)) return null;
                    bool resized = rf.Refresh(Get("base_righteous_fire_%_of_max_life_to_deal_to_nearby_per_minute", 4200),
                        Get("base_nonlethal_fire_damage_%_of_maximum_life_taken_per_minute", 5400), radius,
                        Get("base_nonlethal_fire_damage_%_of_maximum_energy_shield_taken_per_minute", 4200),
                        Get("base_righteous_fire_%_of_max_energy_shield_to_deal_to_nearby_per_minute", 4200));
                    if (!resized || !rf.RingCreated || rf.Ring is not uint old || rf.RingArt is not { } art) return null;
                    uint next = conn.NextObjectId++;
                    var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                    float scale = (float)(radius * (250.0 / 23) / (Art.ArtRadius(art) ?? 400));
                    var ringObject = new EffectObject(at.X, at.Y, art) { Flags0 = EffectObject.FixedRotation, Scale = scale };
                    byte[] packets = [.. InstanceProtocol.RemoveObject(old),
                        .. InstanceEntities.CreateObject(next, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), ringObject.Blob()),
                        .. InstanceProtocol.EnterWakeZone(next)];
                    rf.Ring = next;
                    rf.RingAt = at;
                    return packets;
                }
            }, $"RF ring refresh: radius {radius}, effective gem level {gem.Level}, quality {gem.Quality}");
        }
        finally { rf.RingGate.Release(); }
    }

    static async Task ActivateRighteousFire(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, ushort skillId,
        int effect, int level, int quality, uint gem)
    {
        ushort buff, aura;
        uint ring;
        // A worn skill effect's art (SkillArt): the two buffs' visuals and the ring, which some variations leave out.
        int variation = SkillArtVariation(conn, gem);
        ushort? buffVisual = Art.BuffVisual("righteous_fire_buff_visual", variation), auraVisual = Art.BuffVisual("righteous_fire_aura_visual", variation);
        string? ringAo = Art.Animated("righteous_fire_ring", variation) ?? (variation == 0 ? RighteousFireRingAo : null);
        // The radius takes the player's area of effect stats (passives, items, gem quality) under the skill's own (SkillArea).
        var stats = SupportedStatsFor(conn, skillId, FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2));
        int Get(string name, int fallback) => Stats.Key(name) is uint k && stats.TryGetValue(k, out int v) ? v : fallback;
        int radius = SkillArea.Radius(Get("active_skill_base_area_of_effect_radius", 18), name => Get(name, 0), spell: true, combined: true);
        (int X, int Y) at;
        lock (conn.Monsters)
        {
            if (conn.RighteousFire is not null || conn.PlayerLife == 0) return;
            buff = conn.AllocateBuff();
            aura = conn.AllocateBuff();
            ring = conn.NextObjectId++;
            at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            conn.RighteousFire = new RighteousFireState(buff, aura, ringAo is null ? null : ring, skillId,
                DamagePctPerMinute: Get("base_righteous_fire_%_of_max_life_to_deal_to_nearby_per_minute", 4200),
                BurnPctPerMinute: Get("base_nonlethal_fire_damage_%_of_maximum_life_taken_per_minute", 5400),
                Radius: radius) { AuraVisual = auraVisual, RingArt = ringAo,
                    EnergyShieldBurnPctPerMinute = Get("base_nonlethal_fire_damage_%_of_maximum_energy_shield_taken_per_minute", 4200),
                    EnergyShieldDamagePctPerMinute = Get("base_righteous_fire_%_of_max_energy_shield_to_deal_to_nearby_per_minute", 4200) };
        }
        await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, buff, RighteousFireBuffRow, PlayerId, skillId, visual: buffVisual),
            $"0x01c1 player buff {buff}: righteous_fire (BuffDefinitions row {RighteousFireBuffRow}) from skill 0x{skillId:x4}, " +
            $"art variation {variation} (BuffVisuals {buffVisual?.ToString() ?? "the row's"} / {auraVisual?.ToString() ?? "the row's"}, ring {ringAo ?? "none"}), " +
            $"radius {conn.RighteousFire!.Radius}, {conn.RighteousFire.DamagePctPerMinute / 60.0:0.#}% of life per second to enemies, {conn.RighteousFire.BurnPctPerMinute / 60.0:0.#}% to the player");
        // The caster burns in its own fire: righteous_fire_aura too, whose BuffVisuals (19) give a player model the
        // flaming hands (EPKFiles2 ri_fire/player.epk) and the ignite flames; the first buff only draws the ring at its feet.
        await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, aura, RighteousFireAuraBuffRow, PlayerId, skillId, visual: auraVisual),
            $"0x01c1 player buff {aura}: righteous_fire_aura (BuffDefinitions row {RighteousFireAuraBuffRow})");
        // The big burning circle is not a buff visual: it is MiscAnimated RighteousFireRing, an effect object of its own
        // (EffectObject), kept under the player as it walks (FollowRighteousFire).
        // FixedRotation: the glides that keep it under the player must not turn it (it spun as the player turned).
        if (ringAo is null) return;
        // Scale: the art is drawn for a radius of MiscAnimated __col4 world units (RighteousFireRing 400, about 37 cells of
        // 250/23 units, where the skill's base radius is 18), so it is scaled to the radius the fire burns in.
        if (conn.RighteousFire is { } burning)
        {
            await burning.RingGate.WaitAsync();
            try
            {
                if (!ReferenceEquals(conn.RighteousFire, burning) || burning.Ring is not uint currentRing) return;
                float scale = (float)(burning.Radius * (250.0 / 23) / (Art.ArtRadius(ringAo) ?? 400));
                var ringObject = new EffectObject(at.X, at.Y, ringAo) { Flags0 = EffectObject.FixedRotation, Scale = scale };
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(currentRing, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), ringObject.Blob()),
                    $"0x02b9 create effect 0x{currentRing:x} {ringAo} at ({at.X}, {at.Y}), scale {scale:0.###} for radius {burning.Radius}");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(currentRing), null);
                burning.RingAt = at;
                burning.RingCreated = true;
            }
            finally { burning.RingGate.Release(); }
            _ = RunRingFollow(remote, stream, ciphers, conn, burning);
        }
    }

    static async Task EndRighteousFire(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
    {
        RighteousFireState? rf;
        List<(uint Monster, ushort Buff)> auras;
        lock (conn.Monsters)
        {
            rf = conn.RighteousFire;
            conn.RighteousFire = null;
            auras = rf is null ? [] : [.. rf.Auras.Select(kv => (kv.Key, kv.Value))];
        }
        if (rf is null) return;
        await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, rf.Buff), $"0x01c2 player buff {rf.Buff}: righteous_fire ends ({why})");
        await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, rf.SelfAura), null);
        await rf.RingGate.WaitAsync();
        try
        {
            if (rf.Ring is uint ring && rf.RingCreated)
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(ring), $"0x02bb remove effect 0x{ring:x} (Righteous Fire's ring)");
        }
        finally { rf.RingGate.Release(); }
        foreach (var (monster, buff) in auras)
            await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(monster, buff), null);
    }

    // The slower player tick: RF on nearby enemies, suppression conditions, waypoints, auto-loot and quests.
    // Continuous player pool changes are handled by RunResourceRecovery instead.
    static async Task RunPlayerTick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        double last = conn.Clock.Elapsed.TotalSeconds;
        int suppression = Suppression(conn).Chance;
        while (!conn.Closed)
        {
            await Task.Delay(PlayerTickMs);
            if (conn.Closed) break;
            await RefreshTemporaryPassives(remote, stream, ciphers, conn);
            await RefreshRage(remote, stream, ciphers, conn);
            await RefreshEnduringCry(remote, stream, ciphers, conn);
            double tickNow = conn.Clock.Elapsed.TotalSeconds;
            double seconds = tickNow - last;
            last = tickNow;
            if (conn.Paused || seconds <= 0) continue;
            var rf = conn.RighteousFire;
            if (conn.PlayerLife == 0)
            {
                await EndAurasOnDeath(remote, stream, ciphers, conn);
                if (rf is not null) await EndRighteousFire(remote, stream, ciphers, conn, "death");
                continue;
            }
            if (rf is not null) await BurnAround(remote, stream, ciphers, conn, rf, seconds);
            // The character panel's Spell Suppression chance again (0x1b3 with only its stats) when a condition changed
            // it: full life lost or regained, a walk started or ended, an enemy hit 4 s ago.
            if (Suppression(conn).Chance is var chance && chance != suppression)
            {
                suppression = chance;
                await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [.. SuppressionChanceKeys.Select(k => (k, chance))]),
                    $"0x01b3 player Spell Suppression chance {chance}%");
            }
            if (conn.Layout.Waypoint is { } waypoint
                && Dist(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), (waypoint.GridX, waypoint.GridY)) <= WaypointActivationRange)
                await ActivateWaypoint(remote, stream, ciphers, conn, "the player came near");
            // Gold within GoldAutoLootRange is picked up without a click.
            var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            foreach (var (id, gold) in conn.Instance.Ground.Within(at.X, at.Y, GoldAutoLootRange, IsGold))
                await PickUpGold(remote, stream, ciphers, conn, id, gold, "auto-looted");
            // Quests that start when the player comes near a monster (a1q1: Hillock, "Kill Hillock").
            if (Quests.Watched(conn.Layout.Id) is { Count: > 0 } watched)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                List<string> seen;
                lock (conn.Monsters)
                    seen = [.. conn.Monsters.Values.Where(m => !m.Dead && m.Life > 0 && m.Variety is { } v && watched.Contains(v.Id)
                        && Dist(m.Walk.Position(now), at) <= Quests.SightRange).Select(m => m.Variety!.Id).Distinct()];
                foreach (string variety in seen) await SetQuestFlags(remote, stream, ciphers, conn, Quests.OnMonsterSeen(conn.Layout.Id, variety));
            }
            // a1q4: coming near the Glyph Wall ("Investigate the Mud Flats for a way to lower the water").
            if (conn.Glyphs?.Wall is { } glyphWall && !conn.HasQuestFlag(GlyphQuest.SeenGlyphWall)
                && Dist(at, (glyphWall.X, glyphWall.Y)) <= Quests.SightRange)
                await SetQuestFlags(remote, stream, ciphers, conn, [GlyphQuest.SeenGlyphWall]);
            // a1q4: the HaveGlyph flags follow the glyphs carried (dropped, sold, stashed ones too), about once a second.
            if (conn.Clock.Elapsed.TotalSeconds >= conn.GlyphSyncAt)
            {
                conn.GlyphSyncAt = conn.Clock.Elapsed.TotalSeconds + 1;
                await SyncGlyphs(remote, stream, ciphers, conn);
            }
        }
    }

    // One writer for continuous recovery: regeneration, flask recovery and the player's RF burn.
    // The game clock excludes pause; scheduling/network delays change the step size, never the per-second rate.
    static async Task RunResourceRecovery(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        double lifeCarry = 0, manaCarry = 0, burnCarry = 0, last = conn.Clock.Elapsed.TotalSeconds;
        double ratesAt = double.NegativeInfinity, lifeRate = 0, manaRate = 0, burnPerSecond = 0;
        Dictionary<uint, int> recoveryStats = [];
        int RecoveryStat(string name) => Stats.Key(name) is uint key ? recoveryStats.GetValueOrDefault(key) : 0;
        var cachedRf = conn.RighteousFire;
        while (!conn.Closed)
        {
            await Task.Delay(RecoveryStepMs);
            if (conn.Closed) break;
            double now = conn.Clock.Elapsed.TotalSeconds;
            double seconds = now - last;
            if (seconds <= 0) continue;
            var rf = conn.RighteousFire;
            // Stat aggregation stays at the old 250 ms cadence, rather than rebuilding the panel 60 times/s.
            if (now - ratesAt >= PlayerTickMs / 1000.0 || rf != cachedRf)
            {
                manaRate = ManaRegenPerSecond(conn);
                lock (conn.Monsters)
                {
                    // Build a baseline without the timed source. A second expiry sample cannot leave its old
                    // contribution cached for another 250 ms; the exact interval is integrated below.
                    recoveryStats = CharacterPlayerStatsCore(conn, includeEnduringCry: false);
                    lifeRate = PanelStats.LifePerMinute(RecoveryStat, conn.Pools.Life) / 60;
                }
                burnPerSecond = rf is null ? 0 : DamageModel.Mitigate((conn.PlayerMaxLife * (double)rf.BurnPctPerMinute + conn.Pools.EnergyShield * (double)rf.EnergyShieldBurnPctPerMinute) / 6000.0,
                    Resistance(conn, DamageType.Fire), MaxResistance(conn, DamageType.Fire))
                    * (1 - ChargeEffects.Reduction(RecoveryStat).Elemental / 100.0)
                    * Math.Max(0, 1 + RecoveryStat("harbinger_channel_buff_damage_taken_+%_final") / 100.0);
                (ratesAt, cachedRf) = (now, rf);
            }
            double recoveryFrom = last;
            var (life, mana, ended) = conn.FlaskEffects.Advance(last, now);
            last = now;
            uint lifeBefore, lifeAfter, manaBefore, manaAfter, manaCap;
            bool dead;
            lock (conn.Monsters)
            {
                lifeBefore = conn.PlayerLife;
                manaBefore = conn.PlayerMana;
                manaCap = (uint)UnreservedMana(conn);
                dead = lifeBefore == 0;
                if (dead)
                {
                    lifeCarry = manaCarry = burnCarry = 0;
                    conn.EnergyShield.Empty();
                    lifeAfter = lifeBefore;
                    manaAfter = manaBefore;
                }
                else
                {
                    burnCarry += burnPerSecond * seconds;
                    int burnWhole = (int)Math.Truncate(burnCarry); burnCarry -= burnWhole;
                    int lifeBurn = conn.EnergyShield.Damage(burnWhole, false, now, RecoveryStat);
                    conn.EnergyShield.Advance(recoveryFrom, now, RecoveryStat, true);
                    conn.PlayerLife = lifeAfter = RecoveryPool.Advance(lifeBefore, UnreservedLife(conn),
                        life + lifeRate * seconds + EnduringCryRecovery(conn, recoveryFrom, now) - lifeBurn,
                        ref lifeCarry, minimum: 1);
                    conn.PlayerMana = manaAfter = RecoveryPool.Advance(manaBefore, manaCap,
                        mana + manaRate * seconds, ref manaCarry);
                }
            }
            await SendPlayerEnergyShield(remote, stream, ciphers, conn);
            await SendWardState(remote, stream, ciphers, conn);
            if (dead)
            {
                await EndFlaskEffects(remote, stream, ciphers, conn, [.. ended, .. conn.FlaskEffects.End(_ => true)], "death");
                continue;
            }
            if (rf is not null && lifeAfter == 1 && conn.EnergyShield.Current == 0)
                await EndRighteousFire(remote, stream, ciphers, conn, "one life and no energy shield");
            if (lifeAfter != lifeBefore) await Send(remote, stream, ciphers, PlayerLifeUpdate(conn), null);
            if (manaAfter != manaBefore) await SendPlayerMana(remote, stream, ciphers, conn, null);
            bool lifeFull = lifeAfter == UnreservedLife(conn), manaFull = manaAfter == manaCap;
            ended.AddRange(conn.FlaskEffects.End(e => (e.LifePerSecond == 0 || lifeFull) && (e.ManaPerSecond == 0 || manaFull)));
            ended = conn.FlaskEffects.KeepRunning(ended, lifeFull, manaFull, now);
            if (ended.Count > 0)
                await EndFlaskEffects(remote, stream, ciphers, conn, ended, $"life {lifeAfter}/{conn.PlayerMaxLife}, mana {manaAfter}/{manaCap}");
        }
    }

    // The ended flask effects' buffs come off the player (S→C 0x1c2).
    static async Task EndFlaskEffects(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, List<Flasks.Effect> ended, string why)
    {
        foreach (var e in ended)
            if (FlaskBuffs && e.Buff != 0) await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, e.Buff), $"0x01c2 player buff {e.Buff}: flask {e.Slot} effect ends ({why})");
            else Console.WriteLine($"[gateway] {remote} flask {e.Slot} effect ends ({why})");
    }

    // Righteous Fire's damage to the hostile monsters within its radius for one tick, with their aura buff on while they
    // are inside.
    static async Task BurnAround(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, RighteousFireState rf, double seconds)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        // These modifiers affect damage, including RF's burning. Hit-only luck, triple damage and inverted resistance
        // never apply to a damage-over-time tick or to RF's self burn.
        double perSecond = RighteousFireDamagePerSecond(conn, rf);
        var damageOverTime = new DamageOverTime(DamageType.Fire, perSecond, seconds);
        var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage)>();
        var stages = new List<(uint Id, SpawnedMonster M)>();
        var entered = new List<(uint Id, ushort Buff)>();
        var left = new List<(uint Id, ushort Buff)>();
        var playerAtBurn = CharacterPlayerStats(conn);
        lock (conn.Monsters)
        {
            var here = conn.Walk.Position(now);
            foreach (var (id, m) in conn.Monsters)
            {
                bool inside = !m.Dead && m.Life > 0 && m.Hostile && Dist(here, m.Walk.Position(now)) <= rf.Radius + m.StopRange;
                if (!inside)
                {
                    if (rf.Auras.Remove(id, out ushort gone)) left.Add((id, gone));
                    continue;
                }
                if (!rf.Auras.ContainsKey(id))
                {
                    ushort instance = conn.AllocateBuff();
                    rf.Auras[id] = instance;
                    entered.Add((id, instance));
                }
                var resistance = MonsterResistance(conn, m, DamageType.Fire);
                var resistedDot = damageOverTime with
                {
                    DamagePerSecond = DamageModel.Mitigate(damageOverTime.DamagePerSecond, resistance.Value, resistance.Maximum)
                        * m.Mods.Effects.DamageTaken(spell: false) * PassiveDamageTakenFactor(conn, m, DamageType.Fire)
                };
                var (damage, carry) = resistedDot.Tick(seconds, m.Burn);
                m.Burn = carry;
                if (damage == 0) continue;
                (m.Life, bool stageStarts) = TakeDamageLocked(m, damage);
                m.Aggro = m.Life > 0;
                if (m.Life == 0)
                {
                    m.Dead = true;
                    ChargeKillCommitted(conn, m, playerKill: true, playerAtBurn);
                    if (rf.Auras.Remove(id, out ushort b)) left.Add((id, b));
                }
                hits.Add((id, m, m.Life, damage));
                if (stageStarts) stages.Add((id, m));
            }
        }
        foreach (var (id, buff) in entered)
            await Send(remote, stream, ciphers, InstanceEntities.AddBuff(id, buff, RighteousFireAuraBuffRow, PlayerId, rf.Skill, visual: rf.AuraVisual), null);
        foreach (var (id, buff) in left)
            await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(id, buff), null);
        foreach (var (id, m, life, damage) in hits)
        {
            if (life == 0)
                await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]), null);
            await SendMonsterLife(remote, stream, ciphers, conn, id, m,
                life == 0 ? $"0x01b1 monster 0x{id:x} ({m.Label}) burnt to death by Righteous Fire" : null);
            if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, id, m);
        }
        foreach (var (id, m) in stages) _ = RunBossStage(remote, stream, ciphers, conn, id, m);
    }

    // The quest flags the permanent Kitava resistance penalty mirrors (ResistancePenalty): the Act 5 pair and the
    // Act 10 pair. A change to any of them rebuilds the pools (SetQuestFlags), because the overcapped-resistance
    // modifiers (ResistanceOvercap) read the penalty.
    internal static readonly string[] ResistancePenaltyAct5 = ["a5q6-CompletedBoss", "SinOnKitavaDefeat"],
        ResistancePenaltyAct10 = ["a10q3-SeenBoss", "a10q3-UsedRewardBook"];

    // The permanent resistance penalty from Kitava: -30% after the Act 5 encounter and another -30% after the final
    // Act 10 Kitava. It is a quest penalty, not a buff/aura: the client keeps it through its quest flags, and the server
    // mirrors it from the same flags. (In the 3.29.3b tables the flags are a5q6-CompletedBoss and a10q3-SeenBoss /
    // a10q3-UsedRewardBook; a9q5-KilledBoss is the Act 9 boss and does not grant it.)
    static int ResistancePenalty(ClientState conn)
    {
        bool act5, act10;
        lock (conn.Monsters)
        {
            act5 = ResistancePenaltyAct5.Any(conn.QuestFlags.Contains);
            act10 = ResistancePenaltyAct10.Any(conn.QuestFlags.Contains);
        }
        return (act5 ? -30 : 0) + (act10 ? -30 : 0);
    }

    // Whether a quest-flag change moves the Kitava resistance penalty.
    static bool ChangesResistancePenalty(IEnumerable<string> changed) =>
        changed.Any(ResistancePenaltyAct5.Concat(ResistancePenaltyAct10).Contains);

    // The player's final resistance for one damage type: the passive/gear modifier plus the Kitava quest penalty.
    static int Resistance(ClientState conn, DamageType type)
    {
        int modifier = type switch
        {
            DamageType.Fire => conn.Pools.FireResistance,
            DamageType.Cold => conn.Pools.ColdResistance,
            DamageType.Lightning => conn.Pools.LightningResistance,
            DamageType.Chaos => conn.Pools.ChaosResistance,
            _ => 0,
        };
        return type == DamageType.Physical ? modifier : modifier + ResistancePenalty(conn);
    }

    // The per-type maximum from Character.ot's 75 plus the passive/gear maximum-resistance modifiers, hard-capped at 90.
    static int MaxResistance(ClientState conn, DamageType type) => type switch
    {
        DamageType.Fire => conn.Pools.FireMaxResistance,
        DamageType.Cold => conn.Pools.ColdMaxResistance,
        DamageType.Lightning => conn.Pools.LightningMaxResistance,
        DamageType.Chaos => conn.Pools.ChaosMaxResistance,
        _ => DamageModel.DefaultMaxResistance,
    };

    // Damage immunities as the damage model sees them, from the same sources the panel's "Immune to X" rows use.
    static HashSet<DamageType> PlayerImmunities(ClientState conn)
    {
        var baseStats = new Dictionary<uint, int>();
        foreach (var (key, value) in BaseStats(conn)) baseStats[key] = value;
        lock (conn.Monsters)
            foreach (var (key, value) in conn.PlayerStats) baseStats[key] = baseStats.GetValueOrDefault(key) + value;
        return DamageImmunities.From(name => Stats.Key(name) is uint key ? baseStats.GetValueOrDefault(key) : 0);
    }

    // Spell Suppression: chance in 0..100 with the conditional passives that hold now (full life, moving, an enemy hit in the
    // last 4 s: PoB's "recently"), the damage reduction on a successful suppress (40% by default) and its lucky or unlucky
    // roll (PoB's suppressRolls).
    static (int Chance, int Effect, bool Lucky, bool Unlucky) Suppression(ClientState conn)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        bool fullLife, hitRecently;
        lock (conn.Monsters)
        {
            fullLife = conn.PlayerLife > 0 && conn.PlayerLife >= conn.PlayerMaxLife;
            hitRecently = now - conn.LastEnemyHitAt <= RecentlySeconds;
        }
        return (CharacterStats.SuppressionChance(conn.Pools, fullLife, moving: now < conn.Walk.ArrivesAt, hitRecently),
            Math.Max(0, conn.Pools.SpellSuppressionEffect),
            conn.Pools.SpellSuppressionLucky, conn.Pools.SpellSuppressionUnlucky);
    }

    static DamageType? ParseDamageType(string text) => text.ToLowerInvariant() switch
    {
        "fire" => DamageType.Fire,
        "cold" => DamageType.Cold,
        "lightning" or "light" => DamageType.Lightning,
        "chaos" => DamageType.Chaos,
        "physical" or "phys" => DamageType.Physical,
        _ => null,
    };

    // The death screen's buttons (C→S 0x54). Both hide the screen (0x55 with bit1) and give the player full life; "at
    // checkpoint" revives next to the area's activated waypoint, else at its arrival point (teleport 0x1b7, AreaLayout.Checkpoint),
    // "in town" in the character's last town (an area change;
    // in that town it is the same as the checkpoint).
    static async Task Resurrect(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool inTown)
    {
        lock (conn.Monsters)
        {
            if (conn.PlayerLife > 0) return;
            conn.PlayerLife = UnreservedLife(conn);
            conn.Ward.Restore();
            conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, refill: true);
            conn.HitRecovery.Clear();
            conn.AuraDeathHandled = false;
            conn.PlayerMana = (uint)UnreservedMana(conn);
            foreach (var m in conn.Monsters.Values) m.Aggro = false;
        }
        Console.WriteLine($"[gateway] {remote} <= 0x0054 resurrect {(inTown ? "in town" : "at checkpoint")}");
        await Send(remote, stream, ciphers, InstanceProtocol.DeathScreen(0x02), "0x0055 hide the death screen");
        // The flasks are full again after a death.
        await GainFlaskCharges(remote, stream, ciphers, conn, int.MaxValue, "resurrected");
        if (inTown && conn.LastTown != 0 && conn.Layout.Hash != conn.LastTown)
        {
            // The new area's player object is created with full life.
            await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(conn.LastTown, 0, false, AtSpawn: true), "resurrect in town");
            return;
        }
        // Revive first: a teleport sent while the player is dead left them where they died (live 2026-10-17).
        await Send(remote, stream, ciphers, PlayerLifeUpdate(conn),
            $"0x01b1 player revived with {conn.PlayerMaxLife} life");
        await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana}");
        // The checkpoint: the area's waypoint once activated, else where the player entered the area, as in the real game
        // before any checkpoint is activated. Checkpoint objects and boss arenas (revive at the arena's door) are not done.
        bool activated;
        lock (conn.Waypoints)
            activated = WaypointCatalog.Areas.Find(conn.Layout.Hash) is { } entry && conn.Waypoints.Contains(entry.Id);
        var (x, y) = conn.Layout.Checkpoint(activated);
        conn.Walk.Teleport(x, y, conn.Clock.Elapsed.TotalSeconds);
        await Send(remote, stream, ciphers, InstanceProtocol.Teleport(PlayerId, x, y),
            $"0x01b7 player to the checkpoint ({x}, {y}): {(activated && conn.Layout.Waypoint is not null ? "the activated waypoint" : "the arrival point")}");
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 0)]),
            "0x01b3 player hide_mini_life_bar = 0");
    }

    // The player in its class: the Characters.dat row's object type (Metadata/Characters/StrInt/StrInt for the Templar), model
    // (AOFile), class byte (IntegerId), attributes and unarmed attack; the name is the character's.
    static async Task SendPlayer(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        PrepareHideoutWaypoint(conn);
        var (layout, name, classRow, pools) = (conn.Layout, conn.Character, conn.ClassRow, conn.Pools);
        var cls = Skills.ClassFor(classRow);
        var stats = CharacterPlayerStats(conn);
        // The equipped weapon's attack with its own local mods (WeaponFor), as SendSkills sends it after an equipment change:
        // before, every area started with the class's unarmed attack, so a weapon's added damage and attack speed showed only
        // after taking it off and putting it back. After the stats, which mark the inactive items WeaponFor skips.
        var weapon = WeaponFor(conn);
        foreach (var (k, v) in CosmeticStats(conn)) stats.TryAdd(k, v);
        var statList = stats.Select(kv => (kv.Key, kv.Value)).ToList();
        var weaponStats = WeaponStats(conn, weapon, stats);
        // The new player object starts with exactly these (PlayerStatsWithCleared clears from here).
        lock (conn.Monsters) conn.SentStatKeys = [.. statList.Select(s => s.Item1), .. weaponStats.Select(s => s.Item1)];
        int reservedMana = ReservedMana(conn);
        var player = new PlayerObject(string.IsNullOrEmpty(name) ? "Exile" : name, ClassIndex: PackedClass(conn), GridX: layout.SpawnX, GridY: layout.SpawnY, Life: conn.PlayerLife, EnergyShield: conn.EnergyShield.Current,
            Mana: (uint)Math.Min(conn.PlayerMana, (uint)Math.Max(0, (int)pools.Mana - reservedMana)), ManaReserved: (uint)reservedMana,
            Experience: conn.Experience,
            AnimatedObject: Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? cls.AnimatedObject,
            Skills: [.. Skills.PlayerDefaults(weaponStats), .. GemSkills(conn, [.. weaponStats, .. statList])], Stats: [.. weaponStats, .. statList],
            Equipped: Looks(conn, PlayerInventories.For(string.IsNullOrEmpty(name) ? "Exile" : name, Items)),
            QuestFlags: PlayerQuestFlags(conn), Hideout: HomeHideout(conn)?.Hash16 ?? 0, LeagueQuestFlags: [.. Quests.League.Bits(conn.LeagueQuestFlags)],
            Portrait: WornPortrait(conn), LifeReserved: (uint)ReservedLife(conn));
        conn.ClientSkillIds = [.. player.Skills!.Select(s => s.Id)];
        uint type = InstanceEntities.ObjectTypeHash(cls.ObjectType);
        var messages = new List<(string Name, byte[] Bytes)>
        {
            ("0x01f6 campaign hideout unlocks", InstanceMicrotransactions.HideoutUnlocks(
                HideoutCatalog.OwnedRows.Where(row => row < 128).Select(row => (int)row))),
            ("0x0022 local player 0x1000", InstanceEntities.SetLocalPlayer(PlayerId)),
            ($"0x02b9 create {player} ({cls.ObjectType}) type=0x{type:x8}", InstanceEntities.CreateObject(PlayerId, type, player.Blob())),
        };
        // The player's inventories (id = row + 1), needed by UI built on entering (flask bar → row 11), with the character's items.
        var items = PlayerInventories.For(string.IsNullOrEmpty(name) ? "Exile" : name, Items);
        if (Items.Error is not null) Console.WriteLine($"[gateway] items: {Items.Error}");
        foreach (var slot in InstanceInventories.PlayerDefaults)
        {
            var inside = items.Items(slot.Row);
            messages.Add(($"0x00c0 inventory {PlayerInventories.InventoryId(slot.Row)} row {slot.Row}" + (inside.Count > 0 ? $" items {inside.Count}" : ""),
                InstanceInventories.AddInventory(PlayerInventories.InventoryId(slot.Row), slot, items: inside)));
        }
        messages.Add(("0x003d atlas passive tree presets 0-2", InstanceEntities.PassiveSlots()));
        var (passives, passivesLabel) = PassivesMessage(conn);
        messages.Add((passivesLabel, passives));
        // Area transitions. Like the player they are queued by the world and created when 0x21 activates it.
        foreach (var (id, transition) in layout.AreaTransitions.Where(t => TransitionShown(conn, t.Transition)))
            messages.Add(($"0x02b9 create area transition 0x{id:x} {transition}",
                InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(transition.Type), transition.Blob())));
        if (layout.Waypoint is { } waypoint)
            messages.Add(($"0x02b9 create waypoint 0x{WaypointCatalog.ObjectId:x} {waypoint}",
                InstanceEntities.CreateObject(WaypointCatalog.ObjectId, InstanceEntities.ObjectTypeHash(WaypointObject.MetadataPath), waypoint.Blob())));
        if (Environment.GetEnvironmentVariable("Game_GATEWAY_ENTER") == "1")
        {
            var tabs = PersonalStashOf(conn).Records((int)StashSelectAffinity);
            bool secondSet = PlayerInventories.For(string.IsNullOrEmpty(name) ? "Exile" : name, Items).WeaponSet == 1;
            messages.Add(($"0x0021 enter area, {tabs.Count} stash tab(s), last {PersonalStashOf(conn).LastTab}, weapon set {(secondSet ? 2 : 1)}",
                InstanceProtocol.EnterArea(tabs, secondSet)));
            // The contents of every stash tab with currency in it (stackable items), as 0xc7 would send them (0xc0, no 0xc8:
            // the panel is not open). The client checks a shop price against the currency it knows of, the inventory's and
            // the loaded stash tabs' (3.29 vendors take it from the stash): without this a price was red, and no 0x88 came,
            // until the stash had been opened in that area (live 2026-10-23, Nessa in the hideout).
            var personal = PersonalStashOf(conn);
            for (int t = 0; t < personal.Count; t++)
            {
                var (slot, inside) = personal.Inventory(t);
                if (!inside.Any(i => i.Item.Components.Contains("Stack")) || !conn.StashTabsSent.Add(t)) continue;
                messages.Add(($"0x00c0 stash tab {t} inventory {personal.InventoryOf(t)}: {inside.Count} item(s) (currency for shop prices)",
                    InstanceInventories.AddInventory(personal.InventoryOf(t), slot, 0, inside)));
            }
            // Put the player in the wake zone so it is drawn and its actions/movement run.
            messages.Add(("0x01a9 player enters wake zone", InstanceProtocol.EnterWakeZone(PlayerId)));
            foreach (var (id, _) in layout.AreaTransitions.Where(t => TransitionShown(conn, t.Transition)))
                messages.Add(($"0x01a9 area transition 0x{id:x} enters wake zone", InstanceProtocol.EnterWakeZone(id)));
            if (layout.Waypoint is not null) messages.Add(($"0x01a9 waypoint 0x{WaypointCatalog.ObjectId:x} enters wake zone", InstanceProtocol.EnterWakeZone(WaypointCatalog.ObjectId)));
            // The attributes once more as a stats update. UI built on entering caches them (0x141e802d0 reads the Stats
            // component's getter); live 2026-10-18 the cache held 0 while the getter returned 14/14/32.
            messages.Add(("0x01b3 class attributes", InstanceEntities.StatsUpdate(PlayerId, [.. statList])));
        }
        foreach (var (label, bytes) in messages) await Send(remote, stream, ciphers, bytes, label);
        await RefreshPassiveBuffs(remote, stream, ciphers, conn);
        // An area change creates a new player object, so the running auras' buffs must be put on it again.
        List<AuraCatalog.Aura> auras;
        lock (conn.Monsters) auras = [.. conn.Auras.Values];
        foreach (var aura in auras)
            foreach (var packet in AuraBuffs(aura))
                await Send(remote, stream, ciphers, packet,
                    $"0x01c1 player aura {aura.Name} restored after the area change");
    }
}
