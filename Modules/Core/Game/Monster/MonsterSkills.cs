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
    /// <summary>How long a buff or curse lasts when its skill names no duration.</summary>
    const double SelfSeconds = 4, CurseSeconds = 8;

    /// <summary>How far a travel skill reaches and the nearest the player must be for it (cells, ours): within the
    /// nearer, the monster walks or swings.</summary>
    public const double TravelReach = 45, TravelMinRange = 14;

    /// <summary>The radius a travel's landing hits within, and where it lands short of the player (cells, ours).</summary>
    const double LandingRadius = 12, LandShort = 4;

    /// <summary>What the skill does beyond a hit; null for a plain hit and for an aura (which is up from the spawn).</summary>
    public static MonsterSkillShape? ShapeOf(SkillCatalog skills, int effect)
    {
        if (skills.IsAura(effect)) return null;
        // A skill on a corpse: Bodyswap travels to one, Volatile Dead, Cremation and Detonate Dead hit from them. One that
        // raises a minion from it is no hit (the server raises none).
        if (IsCorpseSkill(effect))
            return skills.HasType(effect, "Minion") ? null : skills.IsTravel(effect) ? MonsterSkillShape.Travel : MonsterSkillShape.Corpse;
        if (skills.HasType(effect, "Hex")) return MonsterSkillShape.Curse;
        // A travel that summons or channels plays as no simple move (Blink Arrow's clone, Charged Dash's hold).
        if (skills.IsTravel(effect) && !skills.IsChannelled(effect) && !skills.HasType(effect, "Minion")) return MonsterSkillShape.Travel;
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
        ushort row = BuffRowOf(skills.EffectId(effect) ?? "", shape);
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
    public static bool IsCorpseSkill(int effect) => CorpseSkills.Value.Contains(effect);

    static readonly Lazy<HashSet<int>> CorpseSkills = new(() =>
    {
        HashSet<int> effects = [];
        if (ClientTables.TableOrNull("ActiveSkills") is not { } active || ClientTables.TableOrNull("GrantedEffects") is not { } granted) return effects;
        HashSet<int> skills = [];
        int i = 0;
        foreach (var a in active) { if (a.Ints("ActiveSkillTargetTypes").Contains(6)) skills.Add(i); i++; }
        i = 0;
        foreach (var e in granted) { if (e.IntOrNull("ActiveSkill") is int a && skills.Contains(a)) effects.Add(i); i++; }
        return effects;
    });

    /// <summary>A corpse skill's use from its stats: its area (active_skill_base_area_of_effect_radius), the corpses it
    /// takes (volatile_dead_base_number_of_corpses_to_consume), and a geyser's bursts (Cremation: one every
    /// cremation_base_fires_projectile_every_x_ms for its duration). An orb or a projectile reaches the player from the
    /// corpse; an explosion only its area.</summary>
    static MonsterSkillUse CorpseUseOf(SkillCatalog skills, int effect, byte level, MonsterSkillShape shape, Dictionary<string, int> stats, double? seconds)
    {
        var raw = GolemData.Skill(skills, effect, level).Stats;
        int Raw(string name) => Stats.Key(name) is uint key ? raw.GetValueOrDefault(key) : 0;
        double radius = Raw("active_skill_base_area_of_effect_radius") is > 0 and var r ? r : LandingRadius;
        bool shoots = skills.IsProjectile(effect) || skills.HasType(effect, "Orb");
        int corpses = Math.Max(1, Raw("volatile_dead_base_number_of_corpses_to_consume"));
        int every = Raw("cremation_base_fires_projectile_every_x_ms");
        int pulses = every > 0 && seconds is > 0 ? Math.Max(1, (int)(seconds.Value * 1000 / every)) : 1;
        return new(shape, 0, seconds ?? 0, stats, shape == MonsterSkillShape.Travel ? TravelMinRange : 0, OnCorpse: true,
            Radius: radius, Reach: shape == MonsterSkillShape.Travel ? radius : shoots ? CorpseShotReach : radius,
            Corpses: corpses, Pulses: pulses, PulseSeconds: every / 1000.0);
    }

    static bool IsCorpse(SpawnedMonster m) =>
        m.Dead && m.Life == 0 && !m.CorpseConsumed && m.Variety?.LifeData.IsUsableAsCorpse(m.Life) == true;

    /// <summary>The corpse a monster uses the skill on, under the Monsters lock: for a travel, the one nearest the player
    /// that is within its reach and brings it nearer; for a spell, the one nearest the player whose hits reach the player.
    /// Null when there is none.</summary>
    public static (uint Id, SpawnedMonster Corpse, (int X, int Y) At)? CorpseFor(ClientState conn, SpawnedMonster m, MonsterAttack a, double now)
    {
        if (a.Use is not { OnCorpse: true } use) return null;
        var here = m.Walk.Position(now);
        var player = conn.Walk.Position(now);
        double near = Dist(here, player);
        (uint, SpawnedMonster, (int, int))? best = null;
        double bestDistance = double.MaxValue;
        foreach (var (id, corpse) in conn.Monsters)
        {
            if (!IsCorpse(corpse)) continue;
            var at = corpse.Walk.Position(now);
            double fromMonster = Dist(here, at), fromPlayer = Dist(at, player);
            bool fits = use.Shape == MonsterSkillShape.Travel
                ? fromMonster <= a.Range && fromPlayer < near - use.MinRange / 2
                : fromMonster <= a.Range && fromPlayer <= use.Reach;
            if (!fits || fromPlayer >= bestDistance) continue;
            (best, bestDistance) = ((id, corpse, at), fromPlayer);
        }
        return best;
    }

    /// <summary>Whether the kit may choose this attack now (BossKits.Choose): a corpse skill only with a corpse for it.</summary>
    public static bool Usable(ClientState conn, SpawnedMonster m, MonsterAttack a, double now) =>
        a.Use is not { OnCorpse: true } || CorpseFor(conn, m, a, now) is not null;

    /// <summary>Takes the corpse and, for a skill that takes several, others within its radius: they leave the client
    /// (0x2bd) and every corpse use. Returns where each was.</summary>
    static async Task<List<(int X, int Y)>> Consume(ClientState conn, uint corpseId, MonsterSkillUse use)
    {
        List<(uint Id, string Label, (int X, int Y) At)> taken = [];
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (!conn.Monsters.TryGetValue(corpseId, out var first) || !IsCorpse(first)) return [];
            var centre = first.Walk.Position(now);
            foreach (var (id, corpse) in conn.Monsters.ToArray()
                .Where(p => IsCorpse(p.Value) && (p.Key == corpseId || Dist(p.Value.Walk.Position(now), centre) <= use.Radius))
                .OrderBy(p => p.Key == corpseId ? 0 : 1).Take(use.Corpses))
            {
                corpse.CorpseConsumed = true;
                conn.Monsters.Remove(id);
                taken.Add((id, corpse.Label, corpse.Walk.Position(now)));
            }
        }
        var (remote, stream, ciphers) = conn.Wire;
        foreach (var (id, label, _) in taken)
            await SendWorldRemoval(remote, stream, ciphers, conn, id, $"0x02bd corpse 0x{id:x} ({label}) consumed by a monster's skill");
        return [.. taken.Select(t => t.At)];
    }

    /// <summary>A hit on the player from a point (a corpse's explosion, orb or geyser), if the player is within reach of
    /// it when it lands.</summary>
    static async Task HitFrom(ClientState conn, uint id, SpawnedMonster m, int action, ushort seq, MonsterAttack a, (int X, int Y) from, double reach)
    {
        var (remote, stream, ciphers) = conn.Wire;
        MonsterHitOutcome? outcome = null;
        var hit = a with { Range = reach, Use = null };
        if (!await SendMonsterAction(remote, stream, ciphers, conn, id, m, action, () =>
        {
            if (InGracePeriod(conn) || m.Dead) return null;
            double now = Math.Max(ProjectileWorldTime(conn), conn.Clock.Elapsed.TotalSeconds);
            outcome = CommitMonsterHitLocked(remote, conn, id, m, hit, now, conn.Walk.Position(now), from, checkRange: true, sequence: seq);
            return outcome?.HitPacket;
        }, $"0x01af monster 0x{id:x} hit on the player from ({from.X}, {from.Y})")) return;
        if (outcome is not null) await PresentMonsterHit(remote, stream, ciphers, conn, id, outcome);
    }

    /// <summary>The auras a monster's skills keep up: their stats together and the BuffDefinitions rows that draw them.</summary>
    public static (Dictionary<string, int> Stats, List<ushort> Rows) Auras(SkillCatalog skills, MonsterCatalog.Variety variety, byte level)
    {
        Dictionary<string, int> stats = [];
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
    static (Dictionary<string, int> Stats, double? Seconds) StatsOf(SkillCatalog skills, int effect, byte level)
    {
        Dictionary<string, int> stats = [];
        double? seconds = null;
        foreach (var (key, value) in GolemData.Skill(skills, effect, level).Stats)
        {
            string name = Stats.Name(key);
            if (name == "base_skill_effect_duration") { seconds = value / 1000.0; continue; }
            if (name.StartsWith("active_skill_", StringComparison.Ordinal) || name.Contains("area_of_effect", StringComparison.Ordinal)
                || name.StartsWith("skill_", StringComparison.Ordinal) || name.StartsWith("display_", StringComparison.Ordinal)) continue;
            name = name.Replace("_granted_from_skill", "", StringComparison.Ordinal);
            stats[name] = stats.GetValueOrDefault(name) + value;
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
            if (BuffRows.Value.TryGetValue(name, out ushort row)) return row;
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

    static readonly Lazy<Dictionary<string, ushort>> BuffRows = new(() =>
    {
        Dictionary<string, ushort> rows = new(StringComparer.Ordinal);
        if (ClientTables.TableOrNull("BuffDefinitions") is not { } table) return rows;
        foreach (var b in table)
            if (b.Text("Id") is { } id) rows.TryAdd(id, (ushort)b.Index);
        return rows;
    });

    /// <summary>Sends the aura looks of a monster just created.</summary>
    public static async Task ShowAuras(ClientState conn, uint id, IReadOnlyList<ushort> rows, bool log)
    {
        var (remote, stream, ciphers) = conn.Wire;
        foreach (ushort row in rows)
        {
            ushort buff;
            lock (conn.Monsters) buff = conn.NextBuff++;
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.AddBuff(id, buff, row, id, auraSource: true),
                !log ? null : $"0x01c1 monster 0x{id:x} aura (BuffDefinitions row {row})");
        }
    }

    // ---- Playing a kit skill ------------------------------------------------------------------------------------------

    /// <summary>Plays a kit skill that is no plain hit: the action, then at its contact what it does.</summary>
    public static async Task Play(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m, int action, MonsterAttack a, MonsterSkillUse use)
    {
        // The skill goes on its cooldown here too: its order may come from a walk-up the kit's choice did not consume.
        lock (conn.Monsters) BossKits.Consume(m, a, conn.Clock.Elapsed.TotalSeconds);
        if (use.OnCorpse)
        {
            await OnCorpse(remote, stream, ciphers, conn, id, m, action, a, use);
            return;
        }
        int chargePct = use.Shape == MonsterSkillShape.Travel ? use.Stats.GetValueOrDefault(ChargeStat) : 0;
        if (chargePct > 0)
        {
            await Charge(remote, stream, ciphers, conn, id, m, action, a, chargePct);
            return;
        }
        double startedAt = 0, seconds = a.Seconds;
        ushort seq = conn.NextActionSequence();
        (int X, int Y) from = default, target = default;
        if (!await SendMonsterAction(remote, stream, ciphers, conn, id, m, action, () =>
        {
            if (MonsterSwingOrderLocked(conn, m, a, action, false, seconds, AreaTime(conn), seq) is not { } order) return null;
            (startedAt, seconds, seq) = (order.StartedAt, order.Seconds, order.Sequence);
            from = order.From;
            target = use.Shape switch
            {
                MonsterSkillShape.Travel => Landing(conn, from, order.Player),
                MonsterSkillShape.Self => from,
                _ => order.Player,
            };
            return InstanceProtocol.ActionStarted(id, from.X, from.Y, target.X, target.Y, a.SkillId, seq, conn.MonsterOrderFlags,
                targetObjectId: use.Shape == MonsterSkillShape.Curse ? PlayerId : null);
        }, $"0x01ab monster 0x{id:x} ({m.Label}) uses {use.Shape} skill 0x{a.SkillId:x4}, {seconds:0.00} s")) return;
        if (!await WaitMonsterActionTime(conn, id, m, action, startedAt + seconds * 0.6)) return;
        switch (use.Shape)
        {
            case MonsterSkillShape.Travel:
                // The client's action carries its copy to the target; the server's goes with it.
                lock (conn.Monsters) m.Walk.Teleport(target.X, target.Y, conn.Clock.Elapsed.TotalSeconds);
                if (!a.Damage.IsZero)
                    await MonsterContact(remote, stream, ciphers, conn, id, m, action, seq, a with { Range = LandingRadius, Use = null });
                break;
            case MonsterSkillShape.Self:
                await BuffSelf(conn, id, m, use);
                break;
            case MonsterSkillShape.Curse:
                await Curse(conn, id, use);
                break;
        }
    }

    /// <summary>A corpse skill: the action aimed at the corpse (its object as the target), then at its contact the corpse
    /// taken. Bodyswap carries the monster onto it and explodes around it; a spell hits from each corpse it took, an
    /// explosion at once, an orb or a geyser's projectile when it has flown to the player, a geyser once per burst.</summary>
    static async Task OnCorpse(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m, int action, MonsterAttack a, MonsterSkillUse use)
    {
        double startedAt = 0, seconds = a.Seconds;
        ushort seq = conn.NextActionSequence();
        uint corpseId = 0;
        (int X, int Y) corpseAt = default;
        if (!await SendMonsterAction(remote, stream, ciphers, conn, id, m, action, () =>
        {
            if (CorpseFor(conn, m, a, AreaTime(conn)) is not { } corpse) return null;
            if (MonsterSwingOrderLocked(conn, m, a, action, false, seconds, AreaTime(conn), seq) is not { } order) return null;
            (startedAt, seconds, seq) = (order.StartedAt, order.Seconds, order.Sequence);
            (corpseId, corpseAt) = (corpse.Id, corpse.At);
            return InstanceProtocol.ActionStarted(id, order.From.X, order.From.Y, corpseAt.X, corpseAt.Y, a.SkillId, seq,
                conn.MonsterOrderFlags, targetObjectId: corpseId);
        }, $"0x01ab monster 0x{id:x} ({m.Label}) uses {use.Shape} skill 0x{a.SkillId:x4} on a corpse, {seconds:0.00} s")) return;
        if (!await WaitMonsterActionTime(conn, id, m, action, startedAt + seconds * 0.6)) return;
        var corpses = await Consume(conn, corpseId, use);
        if (corpses.Count == 0) return;
        if (use.Shape == MonsterSkillShape.Travel)
        {
            lock (conn.Monsters) m.Walk.Teleport(corpseAt.X, corpseAt.Y, conn.Clock.Elapsed.TotalSeconds);
            if (!a.Damage.IsZero) await HitFrom(conn, id, m, action, seq, a, corpseAt, use.Reach);
            return;
        }
        if (a.Damage.IsZero) return;
        bool shots = use.Reach > use.Radius;
        for (int pulse = 0; pulse < use.Pulses; pulse++)
        {
            if (pulse > 0) await Task.Delay(TimeSpan.FromSeconds(use.PulseSeconds));
            if (conn.Closed || m.Dead) return;
            foreach (var from in corpses)
            {
                if (!shots) { await HitFrom(conn, id, m, action, seq, a, from, use.Reach); continue; }
                // An orb or a projectile flies to where the player is, and hits if the player is still near it.
                double flight = Dist(from, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds)) / CorpseShotSpeed;
                var corpse = from;
                _ = Task.Delay(TimeSpan.FromSeconds(flight)).ContinueWith(async _ =>
                {
                    var player = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                    if (Dist(corpse, player) <= use.Reach) await HitFrom(conn, id, m, action, seq, a, player, LandingRadius);
                }).Unwrap();
            }
        }
    }

    /// <summary>The stat that makes a travel a charge: its user's movement speed while it runs (Shield Charge's).</summary>
    const string ChargeStat = "base_movement_velocity_+%";

    /// <summary>A charge's farthest run and the radius its end hits within (cells; the player's: ShieldCharge.cs).</summary>
    const int ChargeDistance = 75, ChargeEndRadius = 16;

    /// <summary>A charge (Shield Charge): the client runs it as a walk at the actor's own movement speed (0x141e827a0
    /// reads movement_velocity_+% from its Stats), so its length is the distance over that speed, not the attack time.
    /// The skill's movement bonus goes to the client as the monster's movement_velocity_+% (0xf5) while it runs, the
    /// server's walk charges at the same speed (PlayerWalk.Charge), and the hit lands where and when the walk arrives.</summary>
    static async Task Charge(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m, int action, MonsterAttack a, int bonusPct)
    {
        int ownPct = m.Mods.Effects.MovementPct;
        double walkSpeed, chargeSpeed;
        lock (conn.Monsters)
        {
            walkSpeed = m.Walk.Speed;
            chargeSpeed = walkSpeed / Math.Max(.01, 1 + ownPct / 100.0) * Math.Max(.01, 1 + (ownPct + bonusPct) / 100.0);
        }
        await SendWorldRecord(remote, stream, ciphers, conn, id,
            InstanceEntities.StatsUpdate(id, [(StatKeys.MovementVelocityPct, ownPct + bonusPct)]), null);
        double startedAt = 0, arrives = 0;
        ushort seq = conn.NextActionSequence();
        (int X, int Y) from = default, target = default;
        bool sent = await SendMonsterAction(remote, stream, ciphers, conn, id, m, action, () =>
        {
            if (MonsterSwingOrderLocked(conn, m, a, action, false, a.Seconds, AreaTime(conn), seq) is not { } order) return null;
            (startedAt, seq) = (order.StartedAt, order.Sequence);
            from = order.From;
            target = Landing(conn, from, order.Player);
            m.Walk.SetSpeed(chargeSpeed, startedAt);
            m.Walk.Charge(target.X, target.Y, ChargeDistance, startedAt);
            arrives = Math.Max(m.Walk.ArrivesAt, startedAt);
            m.BusyUntil = Math.Max(m.BusyUntil, arrives + conn.LockstepActionTail);
            return InstanceProtocol.ActionStarted(id, from.X, from.Y, target.X, target.Y, a.SkillId, seq, conn.MonsterOrderFlags);
        }, $"0x01ab monster 0x{id:x} ({m.Label}) charges with skill 0x{a.SkillId:x4} at {ownPct + bonusPct:+0;-0}% movement speed");
        if (sent)
            Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) charge from {from} to {target}: arrives in {arrives - startedAt:0.00} s");
        bool arrived = sent && await WaitMonsterActionTime(conn, id, m, action, arrives);
        lock (conn.Monsters) m.Walk.SetSpeed(walkSpeed, conn.Clock.Elapsed.TotalSeconds);
        if (!conn.Closed && !m.Dead)
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.StatsUpdate(id, [(StatKeys.MovementVelocityPct, ownPct)]), null);
        if (arrived && !a.Damage.IsZero)
            await MonsterContact(remote, stream, ciphers, conn, id, m, action, seq, a with { Range = ChargeEndRadius, Use = null });
    }

    /// <summary>Where a travel lands: short of the player on the line from the monster, on a walkable cell; else the
    /// player's own.</summary>
    static (int X, int Y) Landing(ClientState conn, (int X, int Y) from, (int X, int Y) player)
    {
        double dx = from.X - player.X, dy = from.Y - player.Y, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1) return player;
        for (double back = LandShort; back >= 0; back--)
        {
            int x = player.X + (int)Math.Round(dx / d * back), y = player.Y + (int)Math.Round(dy / d * back);
            if (conn.Layout.WalkableFor(x, y, 0)) return (x, y);
        }
        return player;
    }

    sealed class Buffs { public readonly List<(ushort Buff, IReadOnlyDictionary<string, int> Stats, double Until)> Active = []; }
    static readonly ConditionalWeakTable<SpawnedMonster, Buffs> MonsterBuffs = new();

    static async Task BuffSelf(ClientState conn, uint id, SpawnedMonster m, MonsterSkillUse use)
    {
        var (remote, stream, ciphers) = conn.Wire;
        var buffs = MonsterBuffs.GetValue(m, _ => new());
        ushort buff;
        double until;
        lock (conn.Monsters)
        {
            buff = conn.NextBuff++;
            until = conn.Clock.Elapsed.TotalSeconds + use.Seconds;
            buffs.Active.Add((buff, use.Stats, until));
            m.BuffStats = Sum(buffs.Active.Select(b => b.Stats));
        }
        if (use.BuffRow > 0)
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.AddBuff(id, buff, use.BuffRow, id,
                u68: BitConverter.SingleToUInt32Bits((float)use.Seconds)), $"0x01c1 monster 0x{id:x} buffs itself (row {use.BuffRow}, {use.Seconds:0.#} s)");
        _ = EndBuff(conn, id, m, use, buffs, buff, conn.Layout);
    }

    static async Task EndBuff(ClientState conn, uint id, SpawnedMonster m, MonsterSkillUse use, Buffs buffs, ushort buff, object area)
    {
        var (remote, stream, ciphers) = conn.Wire;
        await Task.Delay(TimeSpan.FromSeconds(use.Seconds));
        lock (conn.Monsters)
        {
            buffs.Active.RemoveAll(b => b.Buff == buff);
            m.BuffStats = Sum(buffs.Active.Select(b => b.Stats));
        }
        if (use.BuffRow > 0 && !conn.Closed && !m.Dead && ReferenceEquals(area, conn.Layout))
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.RemoveBuff(id, buff), null);
    }

    static Dictionary<string, int> Sum(IEnumerable<IReadOnlyDictionary<string, int>> all)
    {
        Dictionary<string, int> sum = [];
        foreach (var stats in all)
            foreach (var (name, value) in stats) sum[name] = sum.GetValueOrDefault(name) + value;
        return sum;
    }

    // ---- Curses on the player -------------------------------------------------------------------------------------------

    sealed class PlayerCurses { public readonly List<(ushort Buff, ushort Row, IReadOnlyDictionary<string, int> Stats, double Until)> Active = []; }
    static readonly ConditionalWeakTable<ClientState, PlayerCurses> Cursed = new();

    /// <summary>A curse on the player for its time: its look, and its stats on the player's sheet (PlayerStats). A curse
    /// of the same look replaces the one there.</summary>
    static async Task Curse(ClientState conn, uint caster, MonsterSkillUse use)
    {
        if (InGracePeriod(conn)) return;
        var (remote, stream, ciphers) = conn.Wire;
        var curses = Cursed.GetValue(conn, _ => new());
        ushort buff, replaced = 0;
        double until;
        lock (conn.Monsters)
        {
            buff = conn.NextBuff++;
            until = conn.Clock.Elapsed.TotalSeconds + use.Seconds;
            int same = curses.Active.FindIndex(c => use.BuffRow > 0 && c.Row == use.BuffRow);
            if (same >= 0) { replaced = curses.Active[same].Buff; curses.Active.RemoveAt(same); }
            curses.Active.Add((buff, use.BuffRow, Player(use.Stats), until));
        }
        if (replaced != 0) await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, replaced), null);
        if (use.BuffRow > 0)
            await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, buff, use.BuffRow, caster,
                u68: BitConverter.SingleToUInt32Bits((float)use.Seconds)), $"0x01c1 the player is cursed by 0x{caster:x} (row {use.BuffRow}, {use.Seconds:0.#} s)");
        await RefreshCursedStats(conn);
        _ = EndCurse(conn, use, curses, buff, conn.Layout);
    }

    static async Task EndCurse(ClientState conn, MonsterSkillUse use, PlayerCurses curses, ushort buff, object area)
    {
        var (remote, stream, ciphers) = conn.Wire;
        await Task.Delay(TimeSpan.FromSeconds(use.Seconds));
        bool removed;
        lock (conn.Monsters) removed = curses.Active.RemoveAll(c => c.Buff == buff) > 0;
        if (!removed || conn.Closed) return;
        // A curse outlives no area change: the new instance has none of its look to remove.
        if (use.BuffRow > 0 && ReferenceEquals(area, conn.Layout)) await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, buff), null);
        await RefreshCursedStats(conn);
    }

    /// <summary>A curse's stats as the player's: the reductions a curse names for its target are the player's own
    /// (physical_damage_taken_+%, the resistances); the curse's own mechanics without a player stat are left out.</summary>
    static Dictionary<string, int> Player(IReadOnlyDictionary<string, int> stats)
    {
        Dictionary<string, int> player = [];
        foreach (var (name, value) in stats)
            if (Stats.Key(name) is not null && !name.StartsWith("curse_", StringComparison.Ordinal)) player[name] = value;
        return player;
    }

    static async Task RefreshCursedStats(ClientState conn)
    {
        lock (conn.Monsters) { conn.Pools = PoolsFor(conn, conn.Pools.Level); conn.ApplyMovementSpeed(); }
        var wire = conn.Wire;
        await SendPlayerStatSnapshot(wire.Remote, wire.Stream, wire.Ciphers, conn, () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)), null);
    }

    /// <summary>GameEvents.PlayerStatSources: the curses on the player add their stats.</summary>
    public static Dictionary<uint, int> PlayerStats(ClientState conn, Dictionary<uint, int> sources)
    {
        if (!Cursed.TryGetValue(conn, out var curses)) return sources;
        double now = conn.Clock.Elapsed.TotalSeconds;
        foreach (var curse in curses.Active.Where(c => c.Until > now))
            foreach (var (name, value) in curse.Stats)
                if (Stats.Key(name) is uint key) sources[key] = sources.GetValueOrDefault(key) + value;
        return sources;
    }
}
