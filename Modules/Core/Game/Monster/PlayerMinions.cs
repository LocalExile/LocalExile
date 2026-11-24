using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamageType = GameServer.Core.World.DamageType;
using DamagePacket = GameServer.Core.World.DamagePacket;

public static partial class Game
{
    // Summon Elemental Relic's relics by element (MonsterVarieties 7185–7187, MonsterTypes ElementalRelic: IsPlayerMinion,
    // so MonsterCatalog.BaseLife, as the client, gives them the MinionLife column × LifeMultiplier 400). A RandomElement
    // skill: one of the three, a third each. Their granted effects are RelicTeleport and the element's aura.
    static readonly (DamageType Element, string Variety)[] RelicVarieties =
    [
        (DamageType.Fire, "Metadata/Monsters/AnimatedItem/ElementalLivingRelicFire"),
        (DamageType.Cold, "Metadata/Monsters/AnimatedItem/ElementalLivingRelicCold"),
        (DamageType.Lightning, "Metadata/Monsters/AnimatedItem/ElementalLivingRelicLightning"),
    ];

    /// <summary>The relics' death explosion radius in cells. Unverified: the data has no explosion skill for the relic; 22
    /// is Path of Building's radius for Minion Instability, the other minion death explosion.</summary>
    public const int RelicExplosionRadius = 22;

    // The zone of a minion's aura on its owner.
    const ZoneKind MinionAuraZones = ZoneKind.MinionAura;

    // Every frame of the area (a pause stops them) while the player's updates run: PlayerMinionsTick, then
    // LinkSkillsTick (a link to a minion that just ended sees it gone). A failed step is logged and the next one runs.
    static void StartPlayerMinions(Player player, PlayerUpdatesState updates)
    {
        // Loads the relic trigger's tables now, so that no step or hit loads them in the area's frame.
        _ = RelicTriggerOf(player);
        EveryFrameAsPlayer(player, () => PlayerUpdatesCurrent(player, updates), async player =>
        {
            try
            {
                double now = updates.Schedule.Now(player);
                await PlayerMinionsTick(player, now);
                if (!PlayerUpdatesCurrent(player, updates)) return;
                await LinkSkillsTick(player, now);
            }
            catch (Exception ex) when (ex is not (IOException or ObjectDisposedException))
            {
                if (!player.Closed) Console.WriteLine($"[gateway] {player.Connection.Remote} minions: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// One step of the player's minions at game time <paramref name="now"/>. A minion whose time is up (or that died)
    /// dies and explodes; one whose owner died or lost the granting passive dies without exploding (our rule: the wiki
    /// does not say), and a living owner's links to it end without killing him (our rule, see paladin.md). Then the
    /// queued relic summons are made, and each living minion's aura goes on the owner while he
    /// is within its radius and off when he leaves it or it ends; the player's stats are sent again when that changes.
    /// </summary>
    public static async Task PlayerMinionsTick(Player player, double now)
    {
        var rules = CombatLifetime;
        bool Current() => CurrentGameWork && ReferenceEquals(CombatLifetime, rules);
        RetireDarkMonarchMinions(player, now);
        foreach (var tick in GameEvents.CompanionTick.For(player))
        {
            if (!Current()) return;
            await tick(player, now);
            if (!Current()) return;
        }
        await TickEquippedItemMinions(player, now);
        if (!Current()) return;
        await TickWeaponMinionTriggers(player, now);
        if (!Current()) return;
        await TickBeastCompanions(player, now, rules);
        if (!Current()) return;
        await TickEssenceSummons(player, now);
        if (!Current()) return;
        await TickEssenceDeathSpawns(player);
        if (!Current()) return;
        await TickEssenceInsectContacts(player);
        if (!Current()) return;
        MinionUtilityTick(player, now);
        InfernalLegionTickLocked(player, now);
        TickEssenceAurasLocked(player, now);
        TickRemnantAurasLocked(player, now);
        await GolemsTick(player, now);
        if (!Current()) return;
        await SummonsTick(player, now);
        if (!Current()) return;
        await ReservationSkillsTick(player, now);
        if (!Current()) return;
        await PlacedSkillsTick(player, now);
        if (!Current()) return;
        await MirageWarriorsTick(player, now);
        if (!Current()) return;
        if (player.Minions.All.Count == 0 && player.Minions.Bodies.Count == 0
            && player.Minions.PendingRelics == 0 && player.Minions.OutputQueue.Count == 0
            && player.Summons.PresentedOwnership.Count == 0)
            return;
        var ended = new List<PlayerMinions.Minion>();
        var bytes = new List<byte>();
        int summons;
        bool aurasChanged = false, linksEnded = false;
        using (ProjectileMutation(player))
        {
            if (!Current() || player.Closed || player.LeavingArea) return;
            bool keep = player.PlayerLife > 0 && RelicTriggerOf(player) is not null
                && DarkMonarchAllows("ElementalRelic", StatSourceValues(player));
            summons = keep ? player.Minions.PendingRelics : 0;
            if (!keep) player.Minions.PendingRelics = 0;
            RemoveMinionBodiesLocked(player, now);
            // The relics; the Spirit chieftains end in SpiritChieftainMinion.
            foreach (var minion in player.Minions.All.Where(m => IsRelicSkill(m.Skill)).ToArray())
            {
                var m = minion.Monster;
                bool current = player.Area.Monsters.TryGetValue(minion.Id, out var held) && ReferenceEquals(held, m);
                if (current && keep && !m.Dead && m.Life > 0 && now < minion.Until) continue;
                player.Minions.Remove(minion.Id);
                if (minion.OwnerAura is { SharesWithAllies: true }) ClearSharedMinionAuras(player, minion.Id);
                else DropZones(player, minion.Id, MinionAuraZones);
                if (minion.OwnerBuff != 0)
                {
                    player.Area.Buffs.ClearSource(player.Id, minion.Id);
                    aurasChanged = true;
                }
                if (!current) continue;
                if (keep) ended.Add(minion);
                else if (player.PlayerLife > 0 && player.Links.On(minion.Id) is { } link)
                {
                    // A refund is not the minion expiring (the wiki's death rule): the link ends, the player lives.
                    EndLinkLocked(player, link, bytes);
                    linksEnded = true;
                }
                m.Aggro = false; m.Dead = true; m.Life = 0;
                player.Minions.Bodies.Add((minion.Id, m, now + 1));
                SetStats(player, minion.Id, [(Stat.HideMiniLifeBar, 1)]);
                player.Minions.OutputQueue.Add(new([], "minion life 0", minion.Id, m, PlayerMinions.OutputKind.Life));
            }
            if (linksEnded) { LinksChangedLocked(player); aurasChanged = true; }
            if (bytes.Count > 0) player.Minions.OutputQueue.Add(new([.. bytes], "links to refunded minions end"));
            if (aurasChanged) player.Minions.StatRevision++;
            foreach (var minion in ended) CommitMinionExplosionLocked(player, minion, now);
        }
        await PublishMinionOutcomesFor(player, rules);
        if (!Current()) return;
        for (int i = 0; i < summons && Current(); i++) await SummonElementalRelic(player, now);

        bytes.Clear(); aurasChanged = false;
        if (!Current() || player.Closed || player.LeavingArea) return;
        var here = player.Walk.Position(now);
        foreach (var minion in player.Minions.All)
        {
            var m = minion.Monster;
            bytes.Clear();
            // RelicTeleport: the AI follows within the aggro range only (MonsterVarieties MaxAgroRange, 150 for the
            // relics), so from beyond it the minion jumps to him (as /mob_tp). shortcut: the threshold is the aggro
            // range, the game's is not in the data (RelicTeleport has no stats).
            if (minion.Teleports && player.PlayerLife > 0 && Dist(m.Walk.Position(now), here) > m.AggroRange)
            {
                var (x, y) = NextToPlayer(player, LivingCollision.Size(m), minion.Id);
                m.Walk = player.NewMonsterWalk(minion.Id, x, y, m.Walk.Speed, LivingCollision.Size(m), false);
                m.LastTarget = null;
                bytes.AddRange(InstanceProtocol.Teleport(minion.Id, x, y));
            }
            if (bytes.Count > 0) player.Minions.OutputQueue.Add(new([.. bytes], "minion teleport", minion.Id, m));
            bytes.Clear();
            if (minion.OwnerAura is not { } aura)
            {
                SyncNativeMinionAuras(player, minion, now);
                continue;
            }
            if (aura.SharesWithAllies)
            {
                SyncSharedMinionAura(player, minion, aura, now);
                continue;
            }
            // The aura is a zone around the minion that reaches its owner (OwnedZones).
            SyncZones(player, minion.Id, MinionAuraZones, [(0u, aura.Radius, default((int, int)))],
                new(ReachesOnly(player.Id), new EntityId(minion.Id)));
            bool inside = player.PlayerLife > 0 && InZone(player, minion.Id, MinionAuraZones, 0, player.Id);
            if (inside == (minion.OwnerBuff != 0)) continue;
            aurasChanged = true;
            if (inside) { minion.OwnerBuff = 1; player.Area.Buffs.Set(player.Id, new(aura.BuffRow, minion.Id)); }
            else { player.Area.Buffs.Clear(player.Id, aura.BuffRow, minion.Id); minion.OwnerBuff = 0; }
        }
        if (aurasChanged) player.Minions.StatRevision++;
        await PublishMinionOutcomesFor(player, rules);
    }

    static PlayerMinions.Aura[] SelectMinionAuras(Player player, uint target, List<AuraCatalog.Aura> auras, int effect)
    {
        Dictionary<string, (PlayerMinions.Aura Aura, long Strength)>? strongest = null;
        foreach (var shared in SharedMinionAurasReaching(player, target)) Include(shared);
        if (target == player.Id)
            foreach (var minion in player.Minions.All)
                if (minion.OwnerBuff != 0 && minion.OwnerAura is { SharesWithAllies: false } own) Include(own);
        if (strongest is null) return [];
        var selected = strongest.Values.Select(entry => entry.Aura)
            .Where(aura => !PlayerAuraAtLeastAsStrong(player, aura, auras, effect)).ToArray();
        if (selected.Length > 0)
            auras.RemoveAll(a => selected.Any(other => AuraCatalog.Family(other.EffectId) == AuraCatalog.Family(a.EffectId)));
        return selected;

        void Include(PlayerMinions.Aura candidate)
        {
            string family = AuraCatalog.Family(candidate.EffectId);
            long strength = candidate.Stats.Sum(stat => (long)stat.Value);
            strongest ??= new(StringComparer.Ordinal);
            if (!strongest.TryGetValue(family, out var current) || strength > current.Strength)
                strongest[family] = (candidate, strength);
        }
    }

    // A triggered Summon Elemental Relic: a relic of a random element next to the player at his level up to the skill's
    // minion_actor_level_is_user_level_up_to_maximum (85, Ruthless 80), for base_skill_effect_duration with his skill
    // duration modifiers and his base_minion_duration_+% (Path of Building's SkillStatMap: increased Duration for
    // CreatesMinion skills, which Summon Elemental Relic is); a relic of that element already there has its duration
    // refreshed instead (at most one each).
    // shortcut: Combat Focus's element exclusions (RandomElement skills) are not modelled; every element can come.
    static async Task SummonElementalRelic(Player player, double now)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
        var playerStats = CharacterPlayerStats(player);
        int Player(uint k) => playerStats.GetValueOrDefault(k);
        MonsterCatalog.Variety? variety;
        DamageType element;
        string skill;
        byte level;
        double seconds;
        int lifePct;
        (int X, int Y) at;
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0 || player.Minions.PendingRelics == 0 || RelicTriggerOf(player) is not { } relic) return;
        var own = SkillsFor(player).EffectStats(relic.Grant.Effect, relic.Grant.Level).ToDictionary(s => s.Item1, s => s.Item2);
        int Own(uint k) => own.GetValueOrDefault(k);
        (element, string id) = RelicVarieties[player.Minions.Random.Next(RelicVarieties.Length)];
        skill = relic.Grant.EffectId;
        seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Player,
            name => Own(name) + (name == SkillDurationModifiers.IncreasedDuration ? Player(Stat.BaseMinionDurationPlusPct) : 0));
        if (player.Minions.All.FirstOrDefault(m => m.Skill == skill && m.Kind == element.ToString()) is { } same)
        {
            same.Until = now + seconds;
            player.Minions.PendingRelics--;
            Console.WriteLine($"[gateway] {player.Connection.Remote} {element} Relic 0x{same.Id:x}: duration refreshed to {seconds:0.##} s");
            return;
        }
        level = (byte)Math.Clamp(Math.Min(player.Pools.Level, Own(Stat.MinionActorLevelIsUserLevelUpToMaximum)), 1, 100);
        lifePct = Own(Stat.GuardianRelicExplodeOnDeathForPctLifeAsElementDamage);
        variety = Monsters.Find(id);
        if (variety is null)
        {
            if (CurrentGameWork && player.Minions.PendingRelics > 0) player.Minions.PendingRelics--;
            Console.WriteLine($"[gateway] {player.Connection.Remote} {element} Relic: no MonsterVarieties row"); return;
        }
        at = NextToPlayer(player, Math.Clamp(variety.ObjectSize - 1, 0, 127));
        var aura = RelicAura(player, variety, level);
        bool teleports = variety.GrantedEffects?.Any(e => SkillsFor(player).EffectId(e) == "RelicTeleport") == true;
        // The player's increased minion life raises its life (on its Stats as maximum_life) and so its explosion.
        var mods = Player(Stat.MinionMaximumLifePlusPct) is int moreLife and not 0
            ? MonsterModSet.None with { Effects = new(new Dictionary<uint, int> { [Stat.MaximumLifePlusPct] = moreLife }) }
            : MonsterModSet.None;
        await SpawnMonster(player, variety, level, MonsterRarity.Normal, at.X, at.Y, friendly: true,
            mods: mods, extraStats: MinionStats, owned: (id, m) =>
            {
                player.Minions.PendingRelics--;
                player.Minions.Add(new(id, m, skill, element.ToString())
                {
                    Until = now + seconds, OwnerAura = aura, Explodes = new(element, lifePct, RelicExplosionRadius), Teleports = teleports,
                });
            }, ownedCurrent: () => player.PlayerLife > 0 && player.Minions.PendingRelics > 0
                && RelicTriggerOf(player) is not null && DarkMonarchAllows("ElementalRelic", StatSourceValues(player)));
        if (!CurrentGameWork) return;
        Console.WriteLine($"[gateway] {player.Connection.Remote} {element} Relic summoned at level {level} for {seconds:0.##} s" +
            (aura is null ? "" : $", {aura.EffectId} within {aura.Radius} cells: {string.Join(", ", aura.Stats.Select(s => $"{Stats.Name(s.Key)} {s.Value}"))}"));
    }

    // The stats that mark a monster as the player's minion for the client (0x141bfaf20 reads both).
    static IReadOnlyList<(uint Key, int Value)> MinionStats =>
        [.. new[] { Stat.IsPlayerMinion, Stat.IsSemanticMinion }.Select(k => (k, 1))];

    // A relic's aura: its variety's aura effect at the relic's level (PlayerMinions.AuraStatsAt), with that level's radius:
    // AuraCatalog.BaseRadius and the level's active_skill_base_radius_+ (Anger at gem level 20: 59).
    static PlayerMinions.Aura? RelicAura(Player player, MonsterCatalog.Variety variety, int level)
    {
        var skills = SkillsFor(player);
        foreach (int effect in variety.GrantedEffects ?? [])
        {
            if (skills.EffectId(effect) is not { } id || !AuraCatalog.Supported(id)) continue;
            var own = skills.EffectStats(effect, skills.MaxLevelAt(effect, level)).ToDictionary(s => s.Item1, s => s.Item2);
            int Get(uint k) => own.GetValueOrDefault(k);
            return new(id, AuraCatalog.BuffRow(id), SkillArea.Radius(AuraCatalog.BaseRadius, Get, spell: false),
                PlayerMinions.AuraStatsAt(skills, Stats, effect, id, level)) { SharesWithAllies = true };
        }
        return null;
    }

    // A minion's death explosion (PlayerMinions.Explosion): its share of its maximum life as its element to every hostile
    // living monster within the radius, mitigated as any hit (resistances, the player's debuffs on the monster). It is the
    // minion's hit, not the player's: no hit-recently, no on-kill charges; the kills give the player their experience and
    // items, and count as a nearby ally's for the relic trigger.
    static void CommitMinionExplosionLocked(Player player,
        PlayerMinions.Minion minion, double now)
    {
        if (minion.Explodes is not { } blast) return;
        var hits = new List<(uint Id, SpawnedMonster M, MonsterHit Hit, int Damage)>();
        int amount;
        using (ProjectileMutation(player))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
            var at = minion.Monster.Walk.Position(now);
            amount = (int)Math.Min(int.MaxValue, minion.Monster.MaxLife * (long)blast.LifePct / 100);
            var packet = DamagePacket.Of(blast.Type, amount, amount);
            foreach (var (id, m) in player.Area.Monsters.ToArray())
            {
                if (!m.Hostile || m.Dead || m.Life == 0 || Dist(m.Walk.Position(now), at) > blast.Radius) continue;
                int damage = MonsterTakesRolled(m, packet, spell: false, now: now);
                if (ApplyTypedMonsterHitLocked(player, m, damage, blast.Type == DamageType.Chaos ? damage : 0, allyAt: at) is not { } hit) continue;
                if (hit.StageStarts)
                {
                    CommitBossStage(player, id, m, ProjectileWorldTime(player));
                    hit = hit with { StageStarts = false };
                }
                hits.Add((id, m, hit, damage));
                CommitMinionHitLocked(player, id, m, hit, damage, now, $"{minion.Kind} Relic explosion", source: minion.Monster);
            }
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} {minion.Kind} Relic 0x{minion.Id:x} explodes for {amount} {blast.Type} within {blast.Radius} cells: {hits.Count} hit, {hits.Count(h => h.Hit.Life == 0)} killed");
    }

    // Published mods bind the one-argument signature.
    public static (int X, int Y) NextToPlayer(Player player) => NextToPlayer(player, bodySize: 1);

    // A walkable, collision-clear cell just outside the player's footprint for a mover of size bodySize.
    public static (int X, int Y) NextToPlayer(Player player, int bodySize = 1, uint excludeId = 0)
    {
        int size = Math.Max(0, bodySize);
        var (px, py) = player.Walk.Position(AreaTime(player));
        int r = Math.Max(3, size + LivingCollision.PlayerSize + 1);
        bool Floor(int x, int y) => player.Layout.WalkableFor(x, y, size) && !player.Barrier.Contains((x, y));
        var blockers = player.MonsterPlacementBlockers(excludeId);
        int w = player.Layout.GridW > 0 ? player.Layout.GridW : int.MaxValue;
        int h = player.Layout.GridH > 0 ? player.Layout.GridH : int.MaxValue;
        bool Clear(int x, int y) => CollisionEscape.Clear((x, y), w, h, Floor, blockers, moverSize: size);
        for (int step = 0; step < 2; step++)
        {
            int ring = r + step * 2;
            ReadOnlySpan<(int Dx, int Dy)> offsets =
                [(ring, 0), (-ring, 0), (0, ring), (0, -ring), (ring, ring), (-ring, -ring), (ring, -ring), (-ring, ring)];
            foreach (var (dx, dy) in offsets)
                if (Clear(px + dx, py + dy)) return (px + dx, py + dy);
        }
        if (player.Layout.GridW > 0 && player.Layout.GridH > 0
            && CollisionEscape.Find((px, py), player.Layout.GridW, player.Layout.GridH, Floor, blockers, maxRadius: 24, moverSize: size) is { } escaped)
            return escaped;
        ReadOnlySpan<(int Dx, int Dy)> fallback =
            [(r, 0), (-r, 0), (0, r), (0, -r), (r, r), (-r, -r), (r, -r), (-r, r)];
        foreach (var (dx, dy) in fallback)
            if (Floor(px + dx, py + dy)) return (px + dx, py + dy);
        return (px, py);
    }
}
