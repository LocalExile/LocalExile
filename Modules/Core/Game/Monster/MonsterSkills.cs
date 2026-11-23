using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using static Game;
using static Program;

/// <summary>
/// The kinds of monster skill a kit plays beyond a hit (BossKits): auras up from the spawn, travel skills that carry the
/// monster to the player, war cries and buffs on itself, and curses on the player. A skill's stats are its granted
/// effect's at the monster's level (GolemData.Skill), named as the monster mods name them; its look is the
/// BuffDefinitions row of its family (the player's aura and curse rows, AuraCatalog, else the row of its snake-case
/// name). A Rogue Exile's archetype is its variety's GrantedEffectsKeys: Hatred and Rain of Arrows for Orra Greengate,
/// Leap Slam, Shield Charge and Enduring Cry for the Marauders.
/// </summary>
public static partial class MonsterSkills
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadMonsterSkillsData()
    {
        CorpseSkills = LoadCorpseSkills();
        BuffRows = LoadBuffRows();
        Dashes = LoadDashes();
    }

    /// <summary>How long a buff or curse lasts when its skill names no duration.</summary>
    const double SelfSeconds = 4, CurseSeconds = 8;

    /// <summary>How far a travel skill reaches and the nearest the player must be for it (cells, ours): within the
    /// nearer, the monster walks or swings.</summary>
    public const double TravelReach = 45, TravelMinRange = 14;

    /// <summary>The radius a travel's landing hits within, and where it lands short of the player (cells, ours).</summary>
    const double LandingRadius = 12, LandShort = 4;

    /// <summary>The GrantedEffects rows of the Dash action (ActiveSkills <c>dash</c>): the player's Dash and the monsters'
    /// (ExileQuickDodge, DashMercenary, MonsterQuickDodgeRun …).</summary>
    static HashSet<int> Dashes = [];

    static HashSet<int> LoadDashes()
    {
        var skills = ClientTables.Table("ActiveSkills");
        int dash = skills.Select(r => r.Text("Id")).ToList().IndexOf("dash");
        return [.. ClientTables.Table("GrantedEffects").Where(e => dash >= 0 && e.IntOrNull("ActiveSkill") == dash).Select(e => e.Index)];
    }

    static bool IsDash(int effect) => Dashes.Contains(effect);

    /// <summary>The skill's own skill_travel_distance_+% at the monster's level (ExileQuickDodge's 20: its Dash goes 36
    /// cells, where the player's base Dash goes 30). StatsOf leaves skill_ stats out of a use's stats.</summary>
    static int TravelPct(Player player, int effect, byte level) =>
        Stat.SkillTravelDistancePlusPct is var key
            ? GolemData.Skill(SkillsFor(player), effect, level).Stats.GetValueOrDefault(key) : 0;

    /// <summary>What the skill does beyond a hit; null for a plain hit and for an aura (which is up from the spawn).</summary>
    public static MonsterSkillShape? ShapeOf(SkillCatalog skills, int effect)
    {
        if (skills.IsAura(effect)) return null;
        if (ActionClasses.OfEffect(effect)?.SelfBuff is not null) return MonsterSkillShape.Self;
        // A skill on a corpse: Bodyswap travels to one, Volatile Dead, Cremation and Detonate Dead hit from them. One that
        // raises a minion from it is no hit (the server raises none).
        if (IsCorpseSkill(effect))
            return skills.HasType(effect, "Minion") ? null : skills.IsTravel(effect) ? MonsterSkillShape.Travel : MonsterSkillShape.Corpse;
        if (skills.HasType(effect, "Hex")) return MonsterSkillShape.Curse;
        // A travel that summons or channels plays as no simple move (Blink Arrow's clone, Charged Dash's hold).
        if (skills.IsTravel(effect) && !skills.IsChannelled(effect) && !skills.HasType(effect, "Minion")) return MonsterSkillShape.Travel;
        // A boss's own teleport and a monster-only movement skill carry it the same way (BossMoves).
        if (BossMoves.IsMove(skills, effect)) return MonsterSkillShape.Travel;
        if (skills.IsWarcry(effect)) return MonsterSkillShape.Self;
        if (skills.HasType(effect, "Buff") && !skills.IsAttack(effect) && !skills.IsProjectile(effect)) return MonsterSkillShape.Self;
        return null;
    }

    /// <summary>Its use, with its stats at the monster's level and its look; null when the skill is a plain hit.</summary>
    public static MonsterSkillUse? UseOf(SkillCatalog skills, int effect, byte level)
    {
        if (ShapeOf(skills, effect) is not { } shape) return null;
        var (stats, seconds) = StatsOf(skills, effect, level);
        if (IsCorpseSkill(effect)) return CorpseUseOf(skills, effect, level, shape, stats, seconds);
        ushort row = ActionClasses.OfEffect(effect)?.SelfBuff is { } buff && BuffRows.TryGetValue(buff(skills, effect), out ushort own)
            ? own : BuffRowOf(skills.EffectId(effect) ?? "", shape);
        return shape switch
        {
            MonsterSkillShape.Travel => new(shape, 0, 0, stats, TravelMinRange),
            MonsterSkillShape.Curse => new(shape, row, seconds ?? CurseSeconds, stats),
            _ => new(shape, row, Math.Max(seconds ?? SelfSeconds, 1), stats),
        };
    }

    // ---- Corpses --------------------------------------------------------------------------------------------------------

    /// <summary>How far a corpse's orbs and projectiles reach the player from it (cells, ours), and how fast they fly.</summary>
    const double CorpseShotReach = 40, CorpseShotSpeed = 30;

    /// <summary>A skill that targets a corpse (ActiveSkills ActiveSkillTargetTypes 6: Detonate Dead, Raise Zombie,
    /// Volatile Dead, Cremation, Bodyswap). Without one it does nothing in the client.</summary>
    public static bool IsCorpseSkill(int effect) => CorpseSkills.Contains(effect);

    static HashSet<int> CorpseSkills = default!;

    static HashSet<int> LoadCorpseSkills()
    {
        HashSet<int> effects = [];
        if (ClientTables.TableOrNull("ActiveSkills") is not { } active || ClientTables.TableOrNull("GrantedEffects") is not { } granted) return effects;
        HashSet<int> skills = [];
        int i = 0;
        foreach (var a in active) { if (a.Ints("ActiveSkillTargetTypes").Contains(6)) skills.Add(i); i++; }
        i = 0;
        foreach (var e in granted) { if (e.IntOrNull("ActiveSkill") is int a && skills.Contains(a)) effects.Add(i); i++; }
        return effects;
    }

    /// <summary>A corpse skill's use from its stats: its area (active_skill_base_area_of_effect_radius), the corpses it
    /// takes (volatile_dead_base_number_of_corpses_to_consume), and a geyser's bursts (Cremation: one every
    /// cremation_base_fires_projectile_every_x_ms for its duration). An orb or a projectile reaches the player from the
    /// corpse; an explosion only its area.</summary>
    static MonsterSkillUse CorpseUseOf(SkillCatalog skills, int effect, byte level, MonsterSkillShape shape, Dictionary<uint, int> stats, double? seconds)
    {
        var raw = GolemData.Skill(skills, effect, level).Stats;
        int Raw(uint key) => raw.GetValueOrDefault(key);
        double radius = Raw(Stat.ActiveSkillBaseAreaOfEffectRadius) is > 0 and var r ? r : LandingRadius;
        bool shoots = skills.IsProjectile(effect) || skills.HasType(effect, "Orb");
        int corpses = Math.Max(1, Raw(Stat.VolatileDeadBaseNumberOfCorpsesToConsume));
        int every = Raw(Stat.CremationBaseFiresProjectileEveryXMs);
        int pulses = every > 0 && seconds is > 0 ? Math.Max(1, (int)(seconds.Value * 1000 / every)) : 1;
        return new(shape, 0, seconds ?? 0, stats, shape == MonsterSkillShape.Travel ? TravelMinRange : 0, OnCorpse: true,
            Radius: radius, Reach: shape == MonsterSkillShape.Travel ? radius : shoots ? CorpseShotReach : radius,
            Corpses: corpses, Pulses: pulses, PulseSeconds: every / 1000.0);
    }

    static bool IsCorpse(SpawnedMonster m) =>
        m.Dead && m.Life == 0 && !m.CorpseConsumed && m.Variety?.LifeData.IsUsableAsCorpse(m.Life) == true;

    /// <summary>The corpse a monster uses the skill on, in the area's frame: for a travel, the one nearest the player
    /// that is within its reach and brings it nearer; for a spell, the one nearest the player whose hits reach the player.
    /// Null when there is none.</summary>
    public static (uint Id, SpawnedMonster Corpse, (int X, int Y) At)? CorpseFor(Player player, SpawnedMonster m, MonsterAttack a, double now)
    {
        if (a.Use is not { OnCorpse: true } use) return null;
        var here = m.Walk.Position(now);
        var playerAt = player.Walk.Position(now);
        double near = Dist(here, playerAt);
        (uint, SpawnedMonster, (int, int))? best = null;
        double bestDistance = double.MaxValue;
        foreach (var (id, corpse) in player.Area.Monsters)
        {
            if (!IsCorpse(corpse)) continue;
            var at = corpse.Walk.Position(now);
            double fromMonster = Dist(here, at), fromPlayer = Dist(at, playerAt);
            bool fits = use.Shape == MonsterSkillShape.Travel
                ? fromMonster <= a.Range && fromPlayer < near - use.MinRange / 2
                : fromMonster <= a.Range && fromPlayer <= use.Reach;
            if (!fits || fromPlayer >= bestDistance) continue;
            (best, bestDistance) = ((id, corpse, at), fromPlayer);
        }
        return best;
    }

    /// <summary>Whether the kit may choose this attack now (BossKits.Choose): a corpse skill only with a corpse for it, a
    /// summon only while its summoner has fewer of its monsters than the skill's limit (a totem is placed when there is
    /// none, not again over the one that stands). In the area's frame.</summary>
    public static bool Usable(Player player, uint id, SpawnedMonster m, MonsterAttack a, double now) =>
        (a.Use is not { OnCorpse: true } || CorpseFor(player, m, a, now) is not null)
        && (a.Summon is not { Limit: > 0 } summon || Game.LivingSummonsLocked(player, id, summon) < summon.Limit)
        // A summon on its cooldown or at the cap of living summons waits too: chosen, the boss stood until it could.
        && Game.SummonReadyLocked(player, id, m, a, now);

    /// <summary>Takes the corpse and, for a skill that takes several, others within its radius: they leave the client
    /// (0x2bd) and every corpse use. Returns where each was.</summary>
    static async Task<List<(int X, int Y)>> Consume(Player player, uint corpseId, MonsterSkillUse use)
    {
        List<(uint Id, string Label, (int X, int Y) At)> taken = [];
        double now = AreaTime(player);
        if (!player.Area.Monsters.TryGetValue(corpseId, out var first) || !IsCorpse(first)) return [];
        var centre = first.Walk.Position(now);
        foreach (var (id, corpse) in player.Area.Monsters.ToArray()
            .Where(p => IsCorpse(p.Value) && (p.Key == corpseId || Dist(p.Value.Walk.Position(now), centre) <= use.Radius))
            .OrderBy(p => p.Key == corpseId ? 0 : 1).Take(use.Corpses))
        {
            corpse.CorpseConsumed = true;
            player.Area.Monsters.Remove(id);
            taken.Add((id, corpse.Label, corpse.Walk.Position(now)));
        }
        foreach (var (id, label, _) in taken)
            await SendWorldRemoval(player, id, $"0x02bd corpse 0x{id:x} ({label}) consumed by a monster's skill");
        return [.. taken.Select(t => t.At)];
    }

    /// <summary>A hit on the player from a point (a corpse's explosion, orb or geyser), if the player is within reach of
    /// it when it lands.</summary>
    static async Task HitFrom(Player player, uint id, SpawnedMonster m, int action, ushort seq, MonsterAttack a, (int X, int Y) from, double reach)
    {
        MonsterHitOutcome? outcome = null;
        var hit = a with { Range = reach, Use = null };
        if (!await SendMonsterAction(player, id, m, action, () =>
        {
            if (InGracePeriod(player) || m.Dead) return null;
            double now = Math.Max(ProjectileWorldTime(player), AreaTime(player));
            outcome = CommitMonsterHitLocked(player, id, m, hit, now, player.Walk.Position(now), from, checkRange: true, sequence: seq);
            return outcome?.HitPacket;
        }, $"0x01af monster 0x{id:x} hit on the player from ({from.X}, {from.Y})")) return;
        if (outcome is not null) await PresentMonsterHit(player, id, outcome);
    }

    /// <summary>The auras a monster's skills keep up: their stats together and the BuffDefinitions rows that draw them.</summary>
    public static (Dictionary<uint, int> Stats, List<ushort> Rows) Auras(SkillCatalog skills, MonsterCatalog.Variety variety, byte level)
    {
        Dictionary<uint, int> stats = [];
        List<ushort> rows = [];
        foreach (int effect in variety.GrantedEffects ?? [])
        {
            if (effect < 0 || !skills.IsAura(effect)) continue;
            foreach (var (name, value) in StatsOf(skills, effect, level).Stats) stats[name] = stats.GetValueOrDefault(name) + value;
            if (BuffRowOf(skills.EffectId(effect) ?? "", null) is > 0 and var row && !rows.Contains(row)) rows.Add(row);
        }
        return (stats, rows);
    }

    /// <summary>A skill's stats at the level, renamed to the monster mods' names ("…_granted_from_skill" is the plain
    /// stat), without those that only shape the skill; and its duration (base_skill_effect_duration), if it names one.</summary>
    static (Dictionary<uint, int> Stats, double? Seconds) StatsOf(SkillCatalog skills, int effect, byte level)
    {
        Dictionary<uint, int> stats = [];
        double? seconds = null;
        foreach (var (key, value) in GolemData.Skill(skills, effect, level).Stats)
        {
            string name = Stats.Name(key);
            if (key == Stat.BaseSkillEffectDuration) { seconds = value / 1000.0; continue; }
            if (name.StartsWith("active_skill_", StringComparison.Ordinal) || name.Contains("area_of_effect", StringComparison.Ordinal)
                || name.StartsWith("skill_", StringComparison.Ordinal) || name.StartsWith("display_", StringComparison.Ordinal)) continue;
            name = name.Replace("_granted_from_skill", "", StringComparison.Ordinal);
            uint id = Stat.Of(name); stats[id] = stats.GetValueOrDefault(id) + value;
        }
        return (stats, seconds);
    }

    /// <summary>The skill's family: its id without the monster prefixes and variant suffixes (ExileVaalHaste2 → Haste,
    /// ExileDuelistBlinkArrow → BlinkArrow, ExileImmortalCallOnDamageTaken → ImmortalCall).</summary>
    public static string Family(string effectId)
    {
        string id = Prefix().Replace(effectId, "");
        if (id.StartsWith("Vaal", StringComparison.Ordinal)) id = id[4..];
        return Suffix().Replace(id, "");
    }

    [GeneratedRegex("^(Dark)?(Exile|Monster)(Duelist|Ranger|Templar|Shadow|Marauder|Witch|Scion)?")]
    private static partial Regex Prefix();

    [GeneratedRegex("(OnDamageTaken|Weak|Fast|Stun)?[0-9]*$")]
    private static partial Regex Suffix();

    /// <summary>The BuffDefinitions row that draws the skill on its target: the player's aura or curse row for its
    /// family (AuraCatalog), else the row named as the family in snake case ("curse_" first for a curse,
    /// "new_arctic_armour" for Arctic Armour); 0 when there is none.</summary>
    static ushort BuffRowOf(string effectId, MonsterSkillShape? shape)
    {
        string family = Family(effectId);
        if (AuraCatalog.Supported(family)) return AuraCatalog.BuffRow(family);
        string snake = Snake(family);
        string[] names = shape == MonsterSkillShape.Curse ? ["curse_" + snake, snake] : [snake, "new_" + snake];
        foreach (string name in names)
            if (BuffRows.TryGetValue(name, out ushort row)) return row;
        return 0;
    }

    static string Snake(string pascal)
    {
        var text = new StringBuilder();
        foreach (char c in pascal)
        {
            if (char.IsUpper(c) && text.Length > 0) text.Append('_');
            text.Append(char.ToLowerInvariant(c));
        }
        return text.ToString();
    }

    static Dictionary<string, ushort> BuffRows = default!;

    static Dictionary<string, ushort> LoadBuffRows()
    {
        Dictionary<string, ushort> rows = new(StringComparer.Ordinal);
        if (ClientTables.TableOrNull("BuffDefinitions") is not { } table) return rows;
        foreach (var b in table)
            if (b.Text("Id") is { } id) rows.TryAdd(id, (ushort)b.Index);
        return rows;
    }

    /// <summary>Sends the aura looks of a monster just created.</summary>
    public static async Task ShowAuras(Player player, uint id, IReadOnlyList<ushort> rows, bool log)
    {
        foreach (ushort row in rows)
        {
            player.Area.Buffs.Set(id, new(row, id, AuraSource: true));
        }
    }

    // ---- Playing a kit skill ------------------------------------------------------------------------------------------

    /// <summary>Plays a kit skill that is no plain hit: the action, then at its contact what it does.</summary>
    public static async Task Play(Player player, uint id,
        SpawnedMonster m, int action, MonsterAttack a, MonsterSkillUse use)
    {
        // The skill goes on its cooldown here too: its order may come from a walk-up the kit's choice did not consume.
        BossKits.Consume(m, a, AreaTime(player));
        if (use.OnCorpse)
        {
            await OnCorpse(player, id, m, action, a, use);
            return;
        }
        int chargePct = use.Shape == MonsterSkillShape.Travel ? use.Stats.GetValueOrDefault(ChargeStat) : 0;
        if (chargePct > 0)
        {
            await Charge(player, id, m, action, a, chargePct);
            return;
        }
        double startedAt = 0, seconds = a.Seconds;
        ushort seq = player.NextActionSequence();
        (int X, int Y) from = default, target = default;
        if (!await SendMonsterAction(player, id, m, action, () =>
        {
            if (MonsterSwingOrderLocked(player, m, a, action, false, seconds, AreaTime(player), seq) is not { } order) return null;
            (startedAt, seconds, seq) = (order.StartedAt, order.Seconds, order.Sequence);
            from = order.From;
            target = use.Shape switch
            {
                // A Dash goes its own distance toward the aim, as the player's does (Resync.DashLanding): Ainsley Varrich's
                // ExileQuickDodge, landed short of the player, ran on 14 cells past that on the client and snapped back at
                // his next order, or stood where the client's line stopped while the server had him at the end (sync trace
                // 2026-11-17).
                MonsterSkillShape.Travel when a.Effect is int effect && IsDash(effect) => Game.DashLanding(player.Layout, player.Barrier, from,
                    order.Player, TravelPct(player, effect, m.Level), false, m.Variety?.ObjectSize ?? 1),
                MonsterSkillShape.Travel => Landing(player, m, from, order.Player),
                MonsterSkillShape.Self => from,
                _ => order.Player,
            };
            // The client's action carries its copy to the target once it starts, whatever the server does after: the
            // estimate goes there with the order, not at the contact a retarget or a death can skip.
            // A Dash's copy crosses its line over the action's time, not at once: Ainsley Varrich's 36 cells take the
            // 0.25 s action on the client (sync trace 2026-11-17: a slide over the action is 6 cells RMS off the drawn
            // position mid-Dash, the jump at the order 17).
            if (use.Shape == MonsterSkillShape.Travel && a.Effect is int dash && IsDash(dash)) m.Walk.Slide(target.X, target.Y, seconds, startedAt);
            else if (use.Shape == MonsterSkillShape.Travel) m.Walk.Teleport(target.X, target.Y, startedAt);
            return InstanceProtocol.ActionStarted(id, from.X, from.Y, target.X, target.Y, a.SkillId, seq, player.MonsterOrderFlags,
                targetObjectId: use.Shape == MonsterSkillShape.Curse ? player.Id : null);
        }, $"0x01ab monster 0x{id:x} ({m.Label}) uses {use.Shape} skill 0x{a.SkillId:x4}, {seconds:0.00} s")) return;
        if (!await WaitMonsterActionTime(player, id, m, action, startedAt + seconds * (StanceContact(player, m, a, seq) ?? .6))) return;
        switch (use.Shape)
        {
            case MonsterSkillShape.Travel:
                if (!a.Damage.IsZero)
                    await MonsterContact(player, id, m, action, seq, a with { Range = LandingRadius, Use = null });
                break;
            case MonsterSkillShape.Self:
                await BuffSelf(player, id, m, use);
                break;
            case MonsterSkillShape.Curse:
                await Curse(player, id, use);
                break;
        }
    }

    /// <summary>A corpse skill: the action aimed at the corpse (its object as the target), then at its contact the corpse
    /// taken. Bodyswap carries the monster onto it and explodes around it; a spell hits from each corpse it took, an
    /// explosion at once, an orb or a geyser's projectile when it has flown to the player, a geyser once per burst.</summary>
    static async Task OnCorpse(Player player, uint id,
        SpawnedMonster m, int action, MonsterAttack a, MonsterSkillUse use)
    {
        double startedAt = 0, seconds = a.Seconds;
        ushort seq = player.NextActionSequence();
        uint corpseId = 0;
        (int X, int Y) corpseAt = default;
        if (!await SendMonsterAction(player, id, m, action, () =>
        {
            if (CorpseFor(player, m, a, AreaTime(player)) is not { } corpse) return null;
            if (MonsterSwingOrderLocked(player, m, a, action, false, seconds, AreaTime(player), seq) is not { } order) return null;
            (startedAt, seconds, seq) = (order.StartedAt, order.Seconds, order.Sequence);
            (corpseId, corpseAt) = (corpse.Id, corpse.At);
            return InstanceProtocol.ActionStarted(id, order.From.X, order.From.Y, corpseAt.X, corpseAt.Y, a.SkillId, seq,
                player.MonsterOrderFlags, targetObjectId: corpseId);
        }, $"0x01ab monster 0x{id:x} ({m.Label}) uses {use.Shape} skill 0x{a.SkillId:x4} on a corpse, {seconds:0.00} s")) return;
        if (!await WaitMonsterActionTime(player, id, m, action, startedAt + seconds * (StanceContact(player, m, a, seq) ?? .6))) return;
        var corpses = await Consume(player, corpseId, use);
        if (corpses.Count == 0) return;
        if (use.Shape == MonsterSkillShape.Travel)
        {
            m.Walk.Teleport(corpseAt.X, corpseAt.Y, AreaTime(player));
            if (!a.Damage.IsZero) await HitFrom(player, id, m, action, seq, a, corpseAt, use.Reach);
            return;
        }
        if (a.Damage.IsZero) return;
        bool shots = use.Reach > use.Radius;
        for (int pulse = 0; pulse < use.Pulses; pulse++)
        {
            if (pulse > 0) await AreaDelay(player, use.PulseSeconds);
            if (player.Closed || m.Dead) return;
            foreach (var from in corpses)
            {
                if (!shots) { await HitFrom(player, id, m, action, seq, a, from, use.Reach); continue; }
                // An orb or a projectile flies to where the player is, and hits if the player is still near it.
                double flight = Dist(from, player.Walk.Position(AreaTime(player))) / CorpseShotSpeed;
                var corpse = from;
                _ = AreaFrames.Start(async () =>
                {
                    await AreaDelay(player, flight);
                    var playerAt = player.Walk.Position(AreaTime(player));
                    if (Dist(corpse, playerAt) <= use.Reach) await HitFrom(player, id, m, action, seq, a, playerAt, LandingRadius);
                });
            }
        }
    }

    /// <summary>The stat that makes a travel a charge: its user's movement speed while it runs (Shield Charge's).</summary>
    const uint ChargeStat = Stat.BaseMovementVelocityPlusPct;

    /// <summary>A charge's farthest run and the radius its end hits within (cells; the player's: ShieldCharge.cs).</summary>
    const int ChargeDistance = 75, ChargeEndRadius = 16;

    /// <summary>How much later than its order a lockstep client's monster starts its charge (seconds, measured).</summary>
    const double ChargeStartDelay = .085;

    /// <summary>A charge (Shield Charge): the client runs it as a walk at the actor's own movement speed (0x141e827a0
    /// reads movement_velocity_+% from its Stats), so its length is the distance over that speed, not the attack time.
    /// The skill's movement bonus goes to the client as the monster's movement_velocity_+% (0xf5) while it runs, the
    /// server's walk charges at the same speed (PlayerWalk.Charge), and the hit lands where and when the walk arrives.</summary>
    static async Task Charge(Player player, uint id,
        SpawnedMonster m, int action, MonsterAttack a, int bonusPct)
    {
        int ownPct = m.Mods.Effects.MovementPct;
        double walkSpeed, chargeSpeed;
        walkSpeed = m.Walk.Speed;
        chargeSpeed = walkSpeed / Math.Max(.01, 1 + ownPct / 100.0) * Math.Max(.01, 1 + (ownPct + bonusPct) / 100.0);
        SetStats(player, id, [(Stat.MovementVelocityPlusPct, ownPct + bonusPct)]);
        double startedAt = 0, arrives = 0;
        ushort seq = player.NextActionSequence();
        (int X, int Y) from = default, target = default;
        bool sent = await SendMonsterAction(player, id, m, action, () =>
        {
            if (MonsterSwingOrderLocked(player, m, a, action, false, a.Seconds, AreaTime(player), seq) is not { } order) return null;
            (startedAt, seq) = (order.StartedAt, order.Sequence);
            from = order.From;
            target = Landing(player, m, from, order.Player);
            // The client drops a charge whose straight line meets terrain: its monster stays where it stands, so there is
            // no charge to send (sync trace 2026-11-17: Jonah Unchained stood still on five lines past a pillar that the
            // server's run stopped at, 1 to 10 cells along).
            foreach (var cell in GameServer.Core.World.ClientPathfinder.Line(from, target))
                if (cell != from && !m.Walk.Floor(cell.X, cell.Y)) return null;
            // A lockstep client starts the run later than the order (ten charges, sync trace 2026-11-17: it left its cell
            // and arrived 85 ms after the server's walk on average, 12-169 ms), so the walk and the impact wait for it.
            double runsFrom = startedAt + (player.Connection.LockstepClient ? ChargeStartDelay : 0);
            m.Walk.SetSpeed(chargeSpeed, runsFrom);
            m.Walk.Charge(target.X, target.Y, ChargeDistance, runsFrom);
            // The client runs a monster's charge past other bodies; terrain still ends it (sync traces 2026-11-17, Jonah
            // Unchained: with walk body checks 22 of 23 charges ended 2-25 cells from the client's, run through 15 of 19
            // matched and the rest were within 4 cells but one).
            m.Walk.ChargesThrough = true;
            arrives = Math.Max(m.Walk.ArrivesAt, runsFrom);
            m.BusyUntil = Math.Max(m.BusyUntil, arrives + player.LockstepActionTail);
            return InstanceProtocol.ActionStarted(id, from.X, from.Y, target.X, target.Y, a.SkillId, seq, player.MonsterOrderFlags);
        }, $"0x01ab monster 0x{id:x} ({m.Label}) charges with skill 0x{a.SkillId:x4} at {ownPct + bonusPct:+0;-0}% movement speed");
        if (sent)
            Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) charge from {from} to {target}: arrives in {arrives - startedAt:0.00} s");
        bool arrived = sent && await WaitMonsterActionTime(player, id, m, action, arrives);
        // The step pass runs on frames, so the walk can be a cell or two short at its arrival time; it ends as a charge only
        // once the pass has stepped past the arrival, or a body next to the end stops its last step (sync traces
        // 2026-11-17: 2 cells short of the client's beside the player; then, waiting only for the walk's own clock, 1 to 3
        // cells short a second after 8 of 29 charges, until the next action).
        for (double until = AreaTime(player) + .25; arrived && !player.Closed;)
        {
            if (!MonsterActionCurrent(player, id, m, action) || player.WalkersSteppedTo >= m.Walk.ArrivesAt
                || AreaTime(player) >= until) break;
            await AreaWait(PlayerUpdateRole.MonsterAction, 16);
        }
        m.Walk.SetSpeed(walkSpeed, AreaTime(player)); m.Walk.ChargesThrough = false;
        if (!player.Closed && !m.Dead)
            SetStats(player, id, [(Stat.MovementVelocityPlusPct, ownPct)]);
        if (arrived && !a.Damage.IsZero)
            await MonsterContact(player, id, m, action, seq, a with { Range = ChargeEndRadius, Use = null });
    }

    /// <summary>Where a travel lands: short of the player on the line from the monster, on a walkable cell outside
    /// barriers; else the player's own.</summary>
    static (int X, int Y) Landing(Player player, SpawnedMonster m, (int X, int Y) from, (int X, int Y) playerAt)
    {
        double dx = from.X - playerAt.X, dy = from.Y - playerAt.Y, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1) return playerAt;
        int size = Math.Max(0, LivingCollision.Size(m));
        for (double back = LandShort; back >= 0; back--)
        {
            int x = playerAt.X + (int)Math.Round(dx / d * back), y = playerAt.Y + (int)Math.Round(dy / d * back);
            if (player.Layout.WalkableFor(x, y, size) && !player.Barrier.Contains((x, y))) return (x, y);
        }
        return playerAt;
    }

    sealed class Buffs { public readonly List<(ushort Buff, IReadOnlyDictionary<uint, int> Stats, double Until)> Active = []; }
    static readonly ConditionalWeakTable<SpawnedMonster, Buffs> MonsterBuffs = new();

    static async Task BuffSelf(Player player, uint id, SpawnedMonster m, MonsterSkillUse use)
    {
        var buffs = MonsterBuffs.GetValue(m, _ => new());
        ushort buff;
        double until;
        buff = player.NextBuff++;
        until = AreaTime(player) + use.Seconds;
        buffs.Active.Add((buff, use.Stats, until));
        m.BuffStats = Sum(buffs.Active.Select(b => b.Stats));
        // Each use is a buff of its own (its tag), with its countdown.
        if (use.BuffRow > 0)
            player.Area.Buffs.Set(id, new(use.BuffRow, id, U68: Buff.Seconds(use.Seconds), Tag: buff));
        _ = EndBuff(player, id, m, use, buffs, buff, player.Layout);
    }

    static async Task EndBuff(Player player, uint id, SpawnedMonster m, MonsterSkillUse use, Buffs buffs, ushort buff, object area)
    {
        await AreaDelay(player, use.Seconds);
        buffs.Active.RemoveAll(b => b.Buff == buff);
        m.BuffStats = Sum(buffs.Active.Select(b => b.Stats));
        if (use.BuffRow > 0 && !player.Closed && !m.Dead && ReferenceEquals(area, player.Layout))
            player.Area.Buffs.Clear(id, use.BuffRow, id, tag: buff);
    }

    static Dictionary<uint, int> Sum(IEnumerable<IReadOnlyDictionary<uint, int>> all)
    {
        Dictionary<uint, int> sum = [];
        foreach (var stats in all)
            foreach (var (name, value) in stats) sum[name] = sum.GetValueOrDefault(name) + value;
        return sum;
    }

    // ---- Curses on the player -------------------------------------------------------------------------------------------

    /// <summary>A curse on the player for its time: its look, and its stats on the player's sheet (Game.PlayerDebuffs). A
    /// curse of the same look replaces the one there.</summary>
    static Task Curse(Player player, uint caster, MonsterSkillUse use) =>
        AddPlayerDebuff(player, use.BuffRow, caster, Player(use.Stats), use.Seconds);

    /// <summary>A curse's stats as the player's: the reductions a curse names for its target are the player's own
    /// (physical_damage_taken_+%, the resistances); the curse's own mechanics without a player stat are left out.</summary>
    static Dictionary<uint, int> Player(IReadOnlyDictionary<uint, int> stats)
    {
        Dictionary<uint, int> player = [];
        foreach (var (name, value) in stats)
            if (!Stats.Name(name).StartsWith("curse_", StringComparison.Ordinal)) player[name] = value;
        return player;
    }
}
