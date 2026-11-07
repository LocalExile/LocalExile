using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal static readonly SummonCatalog SummonData = new();
    const string SummonSaveKey = "Game.SummonedMinions";
    const string GuardianSaveKey = "Game.AnimatedGuardianEquipment";

    static void SaveSummons(ClientState conn)
    {
        if (!conn.Summons.Restored) return;
        string text;
        lock (conn.Monsters)
            text = JsonSerializer.Serialize(conn.Summons.Fighters.Values.Where(g => !g.Monster.Dead && g.Monster.Life > 0
                    && conn.Summons.Definitions[g.Id].Persistent)
                .Select(g => new SummonedMinions.Saved(g.Definition.Id, g.Source, g.Monster.Variety!.Id,
                    g.Monster.Life / (double)Math.Max(1, g.Monster.MaxLife), g.Equipment.Count > 0 ? g.Equipment : null)).ToArray());
        CharacterSaves.ChangeModuleData(CharacterName(conn), SummonSaveKey, _ => text);
        CharacterSaves.ChangeModuleData(CharacterName(conn), GuardianSaveKey, _ => JsonSerializer.Serialize(conn.Summons.GuardianEquipment));
    }

    internal static int SummonLimit(ClientState conn, SummonCatalog.Definition definition, SupportedGem gem)
    {
        if (definition.LimitStat.Length == 0) return 1;
        var player = CharacterPlayerStats(conn);
        string final = definition.LimitStat.Replace("base_", "", StringComparison.Ordinal);
        int value = GolemStat(gem.Stats, definition.LimitStat) + GolemStat(player, definition.LimitStat) + GolemStat(player, final);
        // Clones have no ordinary cap; duration is their lifetime constraint.
        return Math.Clamp(value == 0 && definition.Family == "Clone" ? 100 : value, 0, 200);
    }

    static double SummonDuration(ClientState conn, SummonCatalog.Definition definition, SupportedGem gem)
    {
        if (definition.Persistent) return double.PositiveInfinity;
        int ms = GolemStat(gem.Stats, definition.Family == "Spectre" ? "spectres_have_base_duration_ms" : "base_skill_effect_duration");
        var player = CharacterPlayerStats(conn);
        return SkillDurationModifiers.Seconds(ms, n => GolemStat(player, n), n => GolemStat(gem.Stats, n));
    }

    static SpawnedMonster? SummonCorpse(ClientState conn, (int X, int Y) aim, uint target)
    {
        bool Allowed(SpawnedMonster m) => m.Hostile && m.Dead && !m.CorpseConsumed && !m.Ailments.Shattered
            && m.Variety?.LifeData.IsUsableAsCorpse(m.Life) == true && m.Rarity != MonsterRarity.Unique;
        if (target != 0 && conn.Monsters.GetValueOrDefault(target) is { } selected && Allowed(selected)) return selected;
        return conn.Monsters.Values.Where(Allowed).Where(m => Dist(m.Walk.Position(AreaTime(conn)), aim) <= 15)
            .MinBy(m => Dist(m.Walk.Position(AreaTime(conn)), aim));
    }

    static async Task<bool> CastSummon(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort source, SummonCatalog.Definition definition, (int X, int Y) aim, uint target, int recovery, Func<bool> current)
    {
        var gem = SupportedSkill(conn, source);
        double release = AreaTime(conn) + (CastMs(conn, gem.Effect, source) ?? 1000) / 1000d;
        var lifetime = CombatLifetime;
        while (true)
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || conn.Closed || conn.LeavingArea
                    || conn.PlayerLife == 0 || recovery != conn.ActionInterruptionGeneration) return false;
                if (!conn.Paused && AreaTime(conn) >= release) break;
            }
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        int count = definition.Family == "Skeleton" ? Math.Max(1, new[] { "number_of_melee_skeletons_to_summon",
            "number_of_archer_skeletons_to_summon", "number_of_mage_skeletons_to_summon" }.Sum(n => GolemStat(gem.Stats, n))) : 1;
        for (int i = 0; i < count && current(); i++)
        {
            if (definition.Family == "Reaper" && definition.Id != "SummonReaperAltX")
                lock (conn.Monsters)
                    if (conn.Summons.Fighters.Values.FirstOrDefault(g => g.Source == source && g.Definition.Id == definition.Id && !g.Monster.Dead) is { } reaper
                        && reaper.Abilities.FirstOrDefault(a => a.Effect == 10584) is { } ultimate)
                    {
                        GolemReserveLocked(conn, reaper, ultimate, target, conn.Monsters.GetValueOrDefault(target), aim, AreaTime(conn));
                        return true;
                    }
            await SummonFighter(remote, stream, ciphers, conn, source, definition, aim, target, current: current);
        }
        SaveSummons(conn);
        return true;
    }

    internal static async Task<uint> SummonFighter(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort source, SummonCatalog.Definition definition, (int X, int Y) aim, uint target = 0,
        string? restoredVariety = null, double life = 1, Func<bool>? current = null,
        Dictionary<byte, Item>? restoredEquipment = null, double? remaining = null)
    {
        var gem = SupportedSkill(conn, source);
        if (!gem.RequirementsMet || SkillsFor(conn).EffectId(gem.Effect) != definition.Id) return 0;
        int limit = SummonLimit(conn, definition, gem);
        if (limit < 1) return 0;
        SpawnedMonster? corpse = null;
        AnimatedSource? animated = null;
        string path = restoredVariety ?? definition.Variety;
        lock (conn.Monsters)
        {
            if (restoredVariety is null && definition.Corpse)
            {
                corpse = SummonCorpse(conn, aim, target);
                if (corpse is null) return 0;
                if (definition.Family == "Spectre") path = corpse.Variety!.Id;
                aim = corpse.Walk.Position(AreaTime(conn));
            }
            if (definition.Family is "AnimatedWeapon" or "AnimatedGuardian" or "Clone")
            {
                animated = AnimatedItems(conn, definition, gem, aim, target, restoredEquipment);
                if (animated is null) return 0;
                if (animated.Ground is { } item) aim = (item.GridX, item.GridY);
            }
        }
        if (definition.Id == "SummonSkeletonsAltY") path = $"Metadata/Monsters/RaisedSkeletons/RaisedSkeletonSpellcaster{Random.Shared.Next(1, 4)}Jewel";
        if (definition.Family == "Clone" && restoredVariety is null)
            path = "Metadata/Monsters/Clone/" + (conn.ClassRow switch { 0 => "Marauder", 1 => "Witch", 2 => "Scion", 3 => "Ranger", 4 => "Duelist", 5 => "Shadow", _ => "Templar" }) + "CloneImmobile";
        if (Monsters.Find(path) is not { Supported: true } variety) return 0;
        int actorLevel = GolemStat(gem.Stats, definition.Family == "Spectre" ? "raised_spectre_level" : "base_display_minion_actor_level");
        byte level = (byte)Math.Clamp(actorLevel, 1, 100);
        var native = new GolemCatalog.Definition(gem.Effect, definition.Id, definition.Family, path, 0, [], false, false, false);
        var player = CharacterPlayerStats(conn);
        var fighter = new GolemState.Golem(0, null!, native, source)
        { GemLevel = gem.Level, Quality = gem.Quality, Own = gem.Stats, Player = new(player), Limit = limit };
        if (animated is not null)
        {
            fighter.Equipment = animated.Equipment;
            var items = definition.Family == "Clone" ? fighter.Equipment.Where(p => p.Key is 2 or 3).Select(p => p.Value) : fighter.Equipment.Values;
            fighter.Gear = ItemModStats.Sum(items, ItemsFor(conn).Mods);
            if (fighter.Equipment.GetValueOrDefault((byte)2) is { } item) fighter.Weapon = WeaponIn(conn, (item, ItemsFor(conn).ByHash(item.TypeHash)));
        }
        var mods = GolemModifiers(fighter, player);
        uint maximum = GolemMaximumLife(variety, level, fighter, player);
        fighter.Abilities = GolemAbilities(conn, variety, level, fighter, player);
        fighter.Skills = GolemNativeSkills(conn, level, fighter.Abilities);
        if (!conn.Layout.WalkableFor(aim.X, aim.Y, 0)) aim = NextToPlayer(conn);
        var consumed = corpse;
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Normal, aim.X, aim.Y,
            friendly: true, mods: mods, unrisen: false, extraStats: MinionStats,
            ownedSkills: fighter.Skills, ownedMaximumLife: maximum, damagedLife: (uint)Math.Clamp(life * maximum, 1, maximum),
            ownedLooks: SummonLooks(fighter),
            ownedAdmission: () => animated?.Ground is not { } item || conn.Instance.Ground.Take(animated.Id, item) is not null,
            ownedCurrent: () => conn.PlayerLife > 0 && (current?.Invoke() ?? true) && (consumed is null || !consumed.CorpseConsumed)
                && GemSkillInstanceDisabled(conn, gem.Effect, source) is null,
            owned: (id, monster) =>
            {
                if (consumed is not null) consumed.CorpseConsumed = true;
                if (animated?.Ground is not null) conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(animated.Id), "animated item consumed"));
                if (definition.Family == "AnimatedGuardian") conn.Summons.GuardianEquipment = new(fighter.Equipment);
                while (conn.Summons.Fighters.Values.Count(g => g.Definition.Kind == definition.Family && !g.Monster.Dead) >= limit)
                    RemoveSummonLocked(conn, conn.Summons.Fighters.Values.Where(g => g.Definition.Kind == definition.Family && !g.Monster.Dead)
                        .MinBy(g => g.SummonedAt)!, AreaTime(conn));
                fighter.Id = id; fighter.Monster = monster; fighter.SummonedAt = fighter.LastStep = AreaTime(conn);
                fighter.Shield = fighter.MaximumShield = GolemShield(fighter, player);
                conn.Summons.Fighters.Add(id, fighter); conn.Summons.Definitions[id] = definition;
                conn.Summons.Until[id] = AreaTime(conn) + (remaining ?? SummonDuration(conn, definition, gem));
                conn.Minions.Add(new(id, monster, definition.Id, definition.Family) { Until = conn.Summons.Until[id] });
                monster.NoRewards = true; monster.Aggro = true;
                if (definition.Id == "RaiseZombieAltY" && fighter.Abilities.FirstOrDefault(a => a.Effect == 7133) is { } fall)
                    GolemReserveLocked(conn, fighter, fall, 0, null, aim, AreaTime(conn));
                conn.Minions.StatRevision++;
            });
        return id;
    }

    static void RemoveSummonLocked(ClientState conn, GolemState.Golem fighter, double now)
    {
        conn.Summons.Fighters.Remove(fighter.Id); conn.Summons.Until.Remove(fighter.Id);
        conn.Summons.Definitions.Remove(fighter.Id); conn.Summons.Weapons.Remove(fighter.Id);
        RemoveGolemLocked(conn, fighter, now);
        conn.Minions.StatRevision++;
    }

    internal static async Task SummonsTick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double now)
    {
        var state = conn.Summons;
        if (!state.Restored)
        {
            state.Restored = true;
            var saved = CharacterSaves.Load(CharacterName(conn))?.ModuleData?.GetValueOrDefault(SummonSaveKey);
            var guardian = CharacterSaves.Load(CharacterName(conn))?.ModuleData?.GetValueOrDefault(GuardianSaveKey);
            if (guardian is not null)
                try { state.GuardianEquipment = JsonSerializer.Deserialize<Dictionary<byte, Item>>(guardian) ?? []; }
                catch (JsonException) { state.GuardianEquipment = []; }
            if (saved is not null)
            {
                SummonedMinions.Saved[] restore;
                try { restore = JsonSerializer.Deserialize<SummonedMinions.Saved[]>(saved) ?? []; }
                catch (JsonException) { restore = []; }
                foreach (var old in restore)
                    if (SummonData.Find(old.Effect) is { Persistent: true } definition)
                    {
                        var gem = GemSkillEffects(conn).FirstOrDefault(g => SkillsFor(conn).EffectId(g.Effect) == old.Effect);
                        if (gem.Name is not null) await SummonFighter(remote, stream, ciphers, conn, gem.Id, definition,
                            NextToPlayer(conn), restoredVariety: old.Variety, life: old.Life, restoredEquipment: old.Equipment);
                    }
            }
        }
        bool changed = false;
        var rescaled = new List<(GolemState.Golem Old, SummonCatalog.Definition Definition)>();
        using (ProjectileMutation(conn))
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused) return;
            if (state.HolyBuff != 0 && now >= state.HolyRegenerationUntil)
            {
                conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, state.HolyBuff), null));
                state.HolyBuff = 0; state.HolyRegenerationPerMinute = 0; conn.Minions.StatRevision++;
            }
            var player = now >= state.RefreshAt && state.Fighters.Count > 0 ? CharacterPlayerStats(conn) : null;
            if (player is not null) state.RefreshAt = now + .5;
            foreach (var fighter in state.Fighters.Values.ToArray())
            {
                if (conn.PlayerLife == 0 || fighter.Monster.Dead || fighter.Monster.Life == 0 || now >= state.Until[fighter.Id])
                {
                    if (fighter.Definition.Kind == "AnimatedGuardian" && (fighter.Monster.Dead || fighter.Monster.Life == 0)) state.GuardianEquipment.Clear();
                    RemoveSummonLocked(conn, fighter, now); changed = true; continue;
                }
                if (player is null) { GolemStepLocked(remote, stream, ciphers, conn, fighter, now); continue; }
                var gem = SupportedSkill(conn, fighter.Source);
                if (SkillsFor(conn).EffectId(gem.Effect) != fighter.Definition.Id || !gem.RequirementsMet)
                { RemoveSummonLocked(conn, fighter, now); changed = true; continue; }
                int actorLevel = GolemStat(gem.Stats, fighter.Definition.Kind == "Spectre" ? "raised_spectre_level" : "base_display_minion_actor_level");
                if (actorLevel > 0 && actorLevel != fighter.Monster.Level)
                { rescaled.Add((fighter, state.Definitions[fighter.Id])); continue; }
                fighter.Own = gem.Stats; fighter.GemLevel = gem.Level; fighter.Quality = gem.Quality;
                RefreshGolemLocked(conn, fighter, player);
                GolemStepLocked(remote, stream, ciphers, conn, fighter, now);
            }
        }
        foreach (var (old, definition) in rescaled)
            if (await SummonFighter(remote, stream, ciphers, conn, old.Source, definition, old.Monster.Walk.Position(now),
                restoredVariety: old.Monster.Variety!.Id, life: old.Monster.Life / (double)old.Monster.MaxLife,
                restoredEquipment: old.Equipment.Count > 0 ? old.Equipment : null, remaining: Math.Max(0, state.Until[old.Id] - now)) != 0)
                lock (conn.Monsters)
                { if (state.Fighters.ContainsKey(old.Id)) RemoveSummonLocked(conn, old, now); changed = true; }
        if (changed) SaveSummons(conn);
    }

    static void AddSummonCounts(ClientState conn, Dictionary<uint, int> stats)
    {
        var names = SummonCountNames;
        lock (conn.Monsters)
        foreach (var (kind, name) in names)
            if (Stats.Key("number_of_active_" + name) is uint key)
                stats[key] = conn.Summons.Fighters.Values.Count(g => g.Definition.Kind == kind && !g.Monster.Dead && g.Monster.Life > 0);
    }
    static readonly IReadOnlyDictionary<string, string> SummonCountNames = new Dictionary<string, string> { ["Zombie"] = "zombies", ["Spectre"] = "spectres", ["Skeleton"] = "skeletons",
            ["RagingSpirit"] = "raging_spirits", ["HolyRelic"] = "relics", ["Reaper"] = "reapers", ["AnimatedWeapon"] = "animated_weapons", ["AnimatedGuardian"] = "animated_armour" };
}
