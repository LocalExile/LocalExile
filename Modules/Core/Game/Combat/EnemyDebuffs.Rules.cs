using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The taunted buff (BuffDefinitions 408) is IsRecovery (+0x7c), so 0x1c1 carries one u32 per GrantedStats entry
    // (taunt_target_id, is_taunted; reader 0x141fe6090): the taunter and 1.
    static IReadOnlyList<uint> TauntedBuffValues(uint taunter) => [taunter, 1];

    /// <summary>How long the player's taunts last: 3 s (wiki Taunt; the Basalt Flask's reminder text), raised by
    /// taunt_duration_+%.</summary>
    public static double TauntSeconds(Func<uint, int> get) =>
        FlaskGrounds.TauntSeconds * Math.Max(0, 1 + get(Stat.TauntDurationPlusPct) / 100.0);

    /// <summary>Taunts the hostile living monsters within <paramref name="radius"/> cells of <paramref name="at"/> for
    /// <paramref name="seconds"/> (a warcry, a Basalt Flask); a new taunt replaces the player's old one on a monster (wiki
    /// Taunt: "you can replace your own taunts"). The enemy-debuff step shows it and takes it off. In the area's frame;
    /// the number taunted.</summary>
    public static int TauntLocked(Player player, (int X, int Y) at, int radius, double now, double seconds)
    {
        if (!CurrentGameWork || player.PlayerLife == 0 || seconds <= 0) return 0;
        int taunted = 0;
        foreach (var m in player.Area.Monsters.Values)
        {
            if (m.Dead || m.Life == 0 || !m.Hostile || Dist(m.Walk.Position(now), at) > radius) continue;
            m.TauntedUntil = now + seconds / m.RecipientDebuffRate;
            m.TauntingPlayerId = player.Id;
            m.TakenFromPlayer = m.TakenFromPlayer with { All = player.EnemyDebuffs.TauntedDamageTakenPct };
            m.PresentationChanged?.Invoke();
            taunted++;
        }
        return taunted;
    }

    static bool TauntPreventsAttackEvasion(Player attacker, SpawnedMonster target, double now)
    {
        if (target.TauntedUntil <= now || target.TauntingPlayerId is not uint source) return false;
        var taunter = source == attacker.Id ? attacker : attacker.Instance.Player(source);
        return taunter is { PlayerLife: > 0, Closed: false, LeavingArea: false }
            && ReferenceEquals(taunter.Area, attacker.Area)
            && PlayerStat(taunter, Stat.EnemiesTauntedByYouCannotEvadeAttacks) != 0;
    }

    /// <summary>This player's Yoke of Suffering value on a monster, as their enemy-debuff step sets it (0 while they do
    /// not wear it, are dead or the monster is not a living enemy): only their own entry changes.</summary>
    public static void KeepPerAilmentTypeTakenLocked(Player player, SpawnedMonster m, int value) =>
        m.SetSourceValue(player.Id, PerAilmentTypeTaken, value);

    /// <summary>A leaving player's per-source values come off the instance's monsters (SpawnedMonster.ForgetSource);
    /// the other participants' stay.</summary>
    public static void ForgetSourceValues(Player player)
    {
        foreach (var m in player.Area.Monsters.Values) m.ForgetSource(player.Id);
        foreach (var m in player.Area.DormantMonsters.Values) m.ForgetSource(player.Id);
    }

    // A nearby presentation (StatPresentations) steps the player's debuffs on the monsters that changed.
    public static Task TickPassiveAuras(Player player) =>
        EnemyDebuffsTick(player, AreaTime(player));

    // The player's debuffs on monsters, by event. A monster is stepped when something names it: it walked into or out of
    // one of the player's enemy-aura zones (EnemyAuraZones), a debuff was put on it (SpawnedMonster.PresentationChanged),
    // one of its debuffs ends (its own wake on the area's timers), or it died. A change of the player's own side (an
    // aura, its radius or values, a curse aura, the player dying) steps every monster once.
    //
    // What a step shows: a taunt the taunted buff (408) for its time left, gone when it ends, the monster dies or the
    // player dies (wiki: a taunt ends when its source dies); his hits' Intimidate, Unnerve, Crush, Maim, Hinder, Exposure
    // and Withered the same way; each enemy aura of his passives (Light of Divinity's Augury of Penitence, 2551, Born under
    // Fire's) on every hostile living monster in its zone while he lives; Pride's own enemy aura (BuffDefinitions 1331)
    // with the ramp each monster keeps while it stays inside; his curse auras (Blasphemy). Each monster then keeps the
    // increased damage taken they give (SpawnedMonster.TakenFromPlayer), which its hits and burns read. The blind auras
    // (SaboteurBlind, Physical Aegis) follow their zones' members.
    sealed class DebuffSteps
    {
        public readonly Lock Gate = new();
        public int Scope;
        public bool Stepped, HadCursedCorpses;
        public readonly HashSet<uint> Changed = [];
        // Each monster's next timed change on the area's timers (the earliest one asked for).
        public readonly Dictionary<uint, double> Wakes = [];
    }
    static readonly Store<Player, DebuffSteps> DebuffStepsOf = new();

    /// <summary>Something about the monster's debuffs changed (a debuff put on it, a zone entered or left, its death): it
    /// is stepped with the player's next nearby presentation.</summary>
    public static void MonsterDebuffsChanged(Player player, uint id)
    {
        var steps = DebuffStepsOf.GetOrCreateValue(player);
        lock (steps.Gate) steps.Changed.Add(id);
        PresentNearby(player);
    }

    // The zones of the player's auras on enemies: an aura's by its definition (a BuffDefinitions row), the others past the
    // rows, a curse aura's by its skill.
    const ZoneKind EnemyAuraZones = ZoneKind.EnemyAuras;
    const uint FleshZone = 0x10000, BlindZone = 0x10001, AegisBlindZone = 0x10002, DoryaniZone = 0x10003, CurseZone = 0x20000;

    // Doryani's Prototype: while its wearer lives, the hostile monsters in its aura keep his capped lightning resistance
    // (EnemyDebuffsTick sets it; MonsterHitResistance replaces their own with it). 60 cells (6 metres) is the retail
    // aura's reach. Stored with an offset so a 0% wearer resistance stays distinct from SetSourceValue's 0-removal.
    const int DoryaniAuraRadius = 60;
    const int DoryaniResistOffset = 1_000_000;
    const string LightningResistEqualsYours = "nearby_enemies_lightning_resist_equal_to_yours";

    // The player's own side of the step: what every monster's step reads. Its Hash changes when any of it does.
    sealed record EnemyDebuffScope(bool Alive, IReadOnlyList<PassiveBuffAuras.Aura> Auras, bool Pride, (int Min, int Max) PrideValues,
        int FleshRadius, int FleshPhysical, int? LightningEquals, int TauntedTakenPct, int PerAilmentType, CurseAura[] Curses,
        IReadOnlyDictionary<uint, int>? CurseStats, uint[] CursedCorpses, IReadOnlyDictionary<ushort, double>? Impurities, int Hash);

    static EnemyDebuffScope PrepareEnemyDebuffs(Player player, double now, IReadOnlyList<PassiveBuffAuras.Aura> current,
        DebuffSteps steps)
    {
        var auras = current.Where(a => a.Template != PassiveBuffAuras.BlindTemplate).ToList();
        int? blindRadius = PassiveBuffAuras.BlindRadius(current);
        var playerStats = CharacterPlayerStats(player);
        // The influenced blind and unique intimidate BuffTemplates use 35 and 25 cells respectively.
        if (StatOf(playerStats, Stat.VirtualNearbyEnemiesAreBlinded) > 0)
            blindRadius = Math.Max(blindRadius ?? 0, 35);
        if (StatOf(playerStats, Stat.VirtualNearbyEnemiesAreIntimidated) > 0)
            auras.Add(new(0, "NearbyEnemiesAreIntimidatedUnique__1", IntimidatedBuffRow, 25, [3], []));
        bool alive = player.PlayerLife > 0;
        var here = player.Walk.Position(now);
        // Pride is a skill aura whose effect is on enemies: a synthetic enemy aura carries its buff, and its ramp is kept
        // per monster.
        AuraCatalog.Aura? pride = player.Auras.Values.FirstOrDefault(a => AuraCatalog.Family(a.EffectId) == "Pride");
        (int Min, int Max) prideValues = default;
        if (pride is not null)
        {
            var skills = SkillsFor(player);
            int prideRadius = AuraRadius.Of(pride, player.Pools.AuraAreaPct, skills, Stats);
            prideValues = AuraCatalog.PridePhysicalDamageTaken(pride, player.Pools.AuraEffectPct, skills, Stats);
            auras.Add(new PassiveBuffAuras.Aura(0, "Pride", AuraCatalog.PrideBuffRow, prideRadius, [3], []));
        }
        // Flesh and Stone's Blood Stance affects physical hits, with distance falloff (worked out when a hit reads it).
        // Sand Stance's hit reduction belongs to the incoming-hit executor, not the blind aura.
        AuraCatalog.Aura? flesh = player.Auras.Values.FirstOrDefault(a => AuraCatalog.Family(a.EffectId) == "FleshAndStone");
        int fleshRadius = 0, fleshPhysical = 0;
        if (flesh is not null)
        {
            var skills = SkillsFor(player);
            fleshRadius = AuraRadius.Of(flesh, player.Pools.AuraAreaPct, skills, Stats);
            if (flesh.Stance != "Sand")
                fleshPhysical = AuraCatalog.FleshAndStonePhysicalTaken(flesh, player.Pools.AuraEffectPct, skills, Stats);
        }
        var impurities = AddImpurityEnemyAuras(player, now, auras);
        var curses = CurseAuras(player, out var curseStats);
        // Doryani's Prototype: its aura carries the wearer's own effective (capped) lightning resistance to the enemies
        // around him, negative included (floored at -200% and capped at his max lightning resistance).
        int? lightningEquals = alive && StatOf(playerStats, Stat.NearbyEnemiesLightningResistEqualToYours) > 0
            ? GameServer.Core.World.DamageModel.Capped(
                Resistance(player, GameServer.Core.World.DamageType.Lightning),
                MaxResistance(player, GameServer.Core.World.DamageType.Lightning))
            : null;
        var cursedCorpses = alive && StatOf(playerStats, CursedCorpseBlast) > 0 ? OwnCursedCorpses(player, now).ToArray() : [];
        if (cursedCorpses.Length > 0 || steps.HadCursedCorpses)
            SyncZones(player, player.Id, ZoneKind.CursedCorpses, cursedCorpses.Select(c => (c.Id, KillExplosionRadius, c.At)),
                new(ReachesHostileMonsters(player, body: false), Changed: id => MonsterDebuffsChanged(player, id.Value)));
        steps.HadCursedCorpses = cursedCorpses.Length > 0;
        var aegis = PhysicalAegisStatus(player, now);
        int aegisReach = alive && aegis.Active && PhysicalAegisHasBlind(player, aegis) ? PhysicalAegisRadius(player, aegis) : 0;
        bool cursing = alive && !InTown(player);
        // Who each aura reaches follows the walkers' moves; entering or leaving one steps that monster.
        SyncZones(player, player.Id, EnemyAuraZones, !alive ? []
            : [.. auras.Select(a => ((uint)a.Definition, a.Radius, here)),
                .. flesh is not null ? [(FleshZone, fleshRadius, here)] : Array.Empty<(uint, int, (int, int))>(),
                .. lightningEquals is not null ? [(DoryaniZone, DoryaniAuraRadius, here)] : Array.Empty<(uint, int, (int, int))>(),
                .. blindRadius is int blindAt and > 0 ? [(BlindZone, blindAt, here)] : Array.Empty<(uint, int, (int, int))>(),
                .. aegisReach > 0 ? [(AegisBlindZone, aegisReach, here)] : Array.Empty<(uint, int, (int, int))>(),
                .. cursing ? curses.Select(c => (CurseZone + c.Aura.SkillId, c.Radius, here)) : Array.Empty<(uint, int, (int, int))>()],
            new(ReachesHostileMonsters(player, body: false), new EntityId(player.Id), id => MonsterDebuffsChanged(player, id.Value)));
        // Yoke of Suffering: each wearer keeps their own value on a shared monster (PerAilmentTypeTaken).
        int perAilmentType = StatOf(playerStats, Stat.AilmentTypesApplyDamageTakenPlusPct);
        var hash = new HashCode();
        hash.Add(alive); hash.Add(InTown(player)); hash.Add(player.EnemyDebuffs.TauntedDamageTakenPct); hash.Add(perAilmentType);
        foreach (var a in auras)
        {
            hash.Add(a.Definition); hash.Add(a.Radius);
            foreach (var s in a.Stats) hash.Add(s);
        }
        hash.Add(prideValues); hash.Add(fleshRadius); hash.Add(fleshPhysical); hash.Add(lightningEquals);
        if (impurities is not null)
            foreach (var (row, until) in impurities) { hash.Add(row); hash.Add(until); }
        // A curse aura's application reads the player's stats.
        foreach (var c in curses) { hash.Add(c.Aura.SkillId); hash.Add(c.Radius); }
        if (curseStats is not null) hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(curseStats));
        return new(alive, auras, pride is not null, prideValues, fleshRadius, fleshPhysical, lightningEquals,
            player.EnemyDebuffs.TauntedDamageTakenPct, perAilmentType, curses, curseStats, [.. cursedCorpses.Select(c => c.Id)],
            impurities, hash.ToHashCode());
    }

    public static async Task EnemyDebuffsTick(Player player, double now)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
        ushort[] nodes = [.. player.Character.Passives, .. player.Character.MainAscendancy.Allocated];
        // Outside the lock: the first call loads the aura table from PassiveSkills.
        var all = PassiveBuffAuras.EnemyAurasFor(nodes, LeagueFor(player).HardMode, EnemyAuraActs);
        await Send(player, [], null, atGate: () =>
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea) return null;
            now = AreaTime(player);
            ushort[] currentNodes = [.. player.Character.Passives, .. player.Character.MainAscendancy.Allocated];
            var current = nodes.SequenceEqual(currentNodes) ? all
                : PassiveBuffAuras.EnemyAurasFor(currentNodes, LeagueFor(player).HardMode, EnemyAuraActs);
            var steps = DebuffStepsOf.GetOrCreateValue(player);
            var scope = PrepareEnemyDebuffs(player, now, current, steps);
            uint[] ids;
            lock (steps.Gate)
            {
                bool everyone = !steps.Stepped || steps.Scope != scope.Hash;
                (steps.Scope, steps.Stepped) = (scope.Hash, true);
                ids = everyone ? [.. player.Area.Monsters.Keys] : [.. steps.Changed];
                steps.Changed.Clear();
            }
            var bytes = new List<byte>();
            var counts = new int[2];
            foreach (uint id in ids)
                if (player.Area.Monsters.TryGetValue(id, out var m)) StepMonsterDebuffsLocked(player, scope, steps, id, m, now, bytes, counts);
            ReconcileBlinds(player, now, scope.CursedCorpses, counts);
            if (counts[0] + counts[1] > 0)
                Console.WriteLine($"[gateway] {player.Connection.Remote} enemy debuffs: {counts[0]} added, {counts[1]} removed (t={now:0.000})");
            return bytes.Count == 0 ? null : [.. bytes];
        });
    }

    // One monster's share of the player's debuffs (see DebuffSteps), and its next wake.
    static void StepMonsterDebuffsLocked(Player player, EnemyDebuffScope scope, DebuffSteps steps, uint id, SpawnedMonster m, double now,
        List<byte> bytes, int[] counts)
    {
        bool In(uint key) => InZone(player, player.Id, EnemyAuraZones, key, id);
        StepCursesLocked(player, scope.Curses, scope.CurseStats, id, m, now, skill => In(CurseZone + skill));
        // Rupture outlives short bleeds and the attacker; only its own timers or the target's death end it.
        SettleBleeding(m, now);
        if (MonsterShown(player, id))
            foreach (var packet in PresentRupturesLocked(player, id, m, now)) bytes.AddRange(packet);
        bool living = scope.Alive && !m.Dead && m.Life > 0 && m.Hostile;
        // One timed debuff: its buff shown for the time left, shown again when that changes, taken off when it ends or
        // the monster or the player dies. Whether it is on.
        bool Timed(ref double until, ref double shown, ref ushort buff, ushort row, IReadOnlyList<uint>? values, double rate = 1)
        {
            bool on = living && until > now;
            if (!on) until = double.NegativeInfinity;
            if (buff != 0 && (!on || shown != until))
            {
                player.Area.Buffs.Clear(id, row, player.Id);
                buff = 0;
                counts[1]++;
            }
            if (on && buff == 0)
            {
                shown = until;
                buff = 1;
                player.Area.Buffs.Set(id, new(row, player.Id, Values: values, U68: Buff.Seconds((until - now) * rate)));
                counts[0]++;
            }
            return on;
        }
        bool taunted = Timed(ref m.TauntedUntil, ref m.TauntShownUntil, ref m.TauntBuff,
            FlaskGrounds.TauntedBuffRow, TauntedBuffValues(player.Id), m.RecipientDebuffRate);
        Timed(ref m.IntimidatedUntil, ref m.IntimidateShownUntil, ref m.IntimidateBuff, IntimidatedBuffRow, null);
        Timed(ref m.UnnervedUntil, ref m.UnnerveShownUntil, ref m.UnnerveBuff, UnnervedBuffRow, null);
        Timed(ref m.CrushedUntil, ref m.CrushShownUntil, ref m.CrushBuff, CrushedBuffRow, null);
        Timed(ref m.MaimedUntil, ref m.MaimShownUntil, ref m.MaimBuff, MaimedBuffRow, null);
        double hinderUntil = Math.Max(m.HinderedUntil, m.TotemHinderedUntil);
        if (!Timed(ref hinderUntil, ref m.HinderShownUntil, ref m.HinderBuff, HinderedBuffRow, null))
            m.HinderedUntil = m.TotemHinderedUntil = double.NegativeInfinity;
        Timed(ref m.CoveredInAshUntil, ref m.CoveredInAshShownUntil, ref m.CoveredInAshBuff, CoveredInAshBuffRow, null);
        StepDebilitateLocked(player, id, m, now);
        for (int i = 0; i < ExposureTypes.Length; i++)
            if (!Timed(ref m.ExposureUntil[i], ref m.ExposureShownUntil[i], ref m.ExposureBuff[i], ExposureBuffRows[i], null))
                EndExposureLocked(m, ExposureTypes[i]);
        // Its Withered stacks: one buff with their count and the time the last one has left, shown
        // again when either changes; off when none runs or it or the player dies.
        if (m.Withered.Count > 0)
        {
            if (living) m.Withered.RemoveAll(s => s.Until <= now);
            else m.Withered.Clear();
        }
        var (stacks, _, witheredUntil, witheredPct) = living ? ReadWithered(m, now) : default;
        if (stacks > 0 || m.WitherBuff != 0)
        {
            if (m.WitherBuff != 0 && (stacks != m.WitherShownStacks || witheredUntil != m.WitherShownUntil))
            {
                player.Area.Buffs.Clear(id, WitheredBuffRow, player.Id);
                m.WitherBuff = 0;
                counts[1]++;
            }
            if (stacks > 0 && m.WitherBuff == 0)
            {
                (m.WitherShownStacks, m.WitherShownUntil) = (stacks, witheredUntil);
                m.WitherBuff = 1;
                player.Area.Buffs.Set(id, new(WitheredBuffRow, player.Id, Values: [(uint)Math.Round(witheredPct), 1],
                    U68: Buff.Seconds(witheredUntil - now), Stacks: (ushort)stacks, U82: 0));
                counts[0]++;
            }
        }
        if (m.PlayerAuras.Count > 0)
            foreach (var ((peer, definition), _) in m.PlayerAuras.ToArray())
                if (peer == player.Id && (!living || scope.Auras.All(a => a.Definition != definition) || !In(definition)))
                {
                    player.Area.Buffs.Clear(id, definition, peer);
                    m.PlayerAuras.Remove((peer, definition));
                    counts[1]++;
                }
        int elemental = 0;
        uint? elementalKey = Stat.ElementalDamageTakenPlusPct;
        foreach (var aura in scope.Auras)
        {
            if (!living || !In(aura.Definition)) continue;
            if (!m.PlayerAuras.ContainsKey((player.Id, aura.Definition)))
            {
                m.PlayerAuras[(player.Id, aura.Definition)] = 1;
                player.Area.Buffs.Set(id, new(aura.Definition, player.Id));
                counts[0]++;
            }
            if (scope.Impurities?.TryGetValue(aura.Definition, out double until) == true)
                m.MarkSource(player.Id, ImpurityMark(aura.Definition)!, until);
            elemental += aura.Stats.Where(s => s.Key == elementalKey).Sum(s => s.Value);
        }
        double wake = double.PositiveInfinity;
        if (scope.Pride && living && In(AuraCatalog.PrideBuffRow))
        {
            if (double.IsNaN(m.PrideSince)) m.PrideSince = now;
            m.PridePhysicalMore = AuraCatalog.PridePhysicalMore(scope.PrideValues.Min, scope.PrideValues.Max, now - m.PrideSince);
            // Its 4-second ramp steps each frame until it is full.
            if (now - m.PrideSince < 4) wake = now + FrameSeconds;
        }
        else
        {
            m.PrideSince = double.NaN;
            m.PridePhysicalMore = 0;
        }
        // Blood Stance: its full value here; a hit works out the falloff with the distance (FleshTaken).
        bool flesh = living && scope.FleshPhysical > 0 && In(FleshZone);
        (m.FleshFrom, m.FleshRadius) = flesh ? (player, scope.FleshRadius) : (null, 0);
        // Doryani's Prototype: the monsters in its zone keep his lightning resistance until they leave it, he dies or
        // takes the chest off (a hash change re-steps everyone). Offset keeps 0% distinct from SetSourceValue's removal.
        m.SetSourceValue(player.Id, LightningResistEqualsYours,
            living && scope.LightningEquals is int ownResist && In(DoryaniZone) ? ownResist + DoryaniResistOffset : 0);
        // Bottled Faith: consecrated ground created during its effect makes the enemies on it take more damage.
        int consecrated = living ? ConsecratedEnemyDamageTaken(player, id) : 0;
        m.TakenFromPlayer = ((taunted ? scope.TauntedTakenPct : 0) + consecrated, elemental, flesh ? scope.FleshPhysical : 0);
        KeepPerAilmentTypeTakenLocked(player, m, living ? scope.PerAilmentType : 0);
        // Its next timed change: a debuff or curse ending.
        void Next(double until) { if (until > now && until < wake) wake = until; }
        Next(m.TauntedUntil); Next(m.IntimidatedUntil); Next(m.UnnervedUntil); Next(m.CrushedUntil);
        Next(m.MaimedUntil); Next(m.HinderedUntil); Next(m.TotemHinderedUntil);
        Next(m.CoveredInAshUntil); Next(m.DebilitatedUntil);
        foreach (double until in m.ExposureUntil) Next(until);
        foreach (var stack in m.Withered) Next(stack.Until);
        foreach (var curse in m.Curses.All.Values) { Next(curse.Until); Next(curse.AmplifiesAt); }
        if (scope.Impurities is not null)
            foreach (var (row, until) in scope.Impurities)
                if (In(row)) Next(until);
        if (!double.IsFinite(wake)) return;
        lock (steps.Gate)
        {
            if (steps.Wakes.TryGetValue(id, out double asked) && asked > now && asked <= wake) return;
            steps.Wakes[id] = wake;
        }
        AfterSeconds(player, wake - now + 1e-6, () =>
        {
            lock (steps.Gate) if (steps.Wakes.GetValueOrDefault(id) == wake) steps.Wakes.Remove(id);
            MonsterDebuffsChanged(player, id);
        });
    }

    // Flesh and Stone's Blood Stance on a hit: its value falls off with the monster's distance from the player, full at 0
    // and nothing at the aura's edge (the data's ..._from_player_distance).
    static int FleshTaken(SpawnedMonster m, double now) => m.FleshFrom is not { } from ? m.TakenFromPlayer.Physical
        : (int)Math.Round(m.TakenFromPlayer.Physical * AuraCatalog.DistanceEffect(Dist(m.Walk.Position(now), from.Walk.Position(now)),
            m.FleshRadius), MidpointRounding.AwayFromZero);

    // The blind auras: the hostile monsters in their zones that can be blinded.
    static void ReconcileBlinds(Player player, double now, IReadOnlyList<uint> cursedCorpses, int[] counts)
    {
        bool alive = player.PlayerLife > 0;
        HashSet<uint> Inside(uint zone) => !alive ? [] : ZoneMembers(player, player.Id, EnemyAuraZones, zone).Select(e => e.Value)
            .Where(id => player.Area.Monsters.GetValueOrDefault(id) is { } m && m.Effects[Stat.CannotBeBlinded] == 0).ToHashSet();
        var blindedByAura = Inside(BlindZone);
        foreach (uint corpse in cursedCorpses)
            foreach (var target in ZoneMembers(player, player.Id, ZoneKind.CursedCorpses, corpse))
                if (player.Area.Monsters.GetValueOrDefault(target.Value) is { } enemy && enemy.Effects[Stat.CannotBeBlinded] == 0)
                    blindedByAura.Add(target.Value);
        foreach (var (id, _, blinded) in player.BlindAura.Reconcile(blindedByAura, player.Area.Monsters.ContainsKey, () => 1))
        {
            if (blinded) player.Area.Buffs.Set(id, new(FlaskGrounds.BlindedBuffRow, player.Id, Tag: (ushort)BuffTag.BlindAura));
            else player.Area.Buffs.Clear(id, FlaskGrounds.BlindedBuffRow, player.Id, tag: (ushort)BuffTag.BlindAura);
            counts[blinded ? 0 : 1]++;
        }
        // Physical Aegis blinds nearby enemies only while any capacity remains. It has its own instances, so depleting the
        // Aegis removes only this source and leaves a timed hit Blind untouched.
        var aegis = PhysicalAegisStatus(player, now);
        foreach (var (id, _, blinded) in aegis.Blind.Reconcile(Inside(AegisBlindZone), player.Area.Monsters.ContainsKey, () => 1))
        {
            if (blinded) player.Area.Buffs.Set(id, new(FlaskGrounds.BlindedBuffRow, player.Id, Tag: (ushort)BuffTag.AegisBlind));
            else player.Area.Buffs.Clear(id, FlaskGrounds.BlindedBuffRow, player.Id, tag: (ushort)BuffTag.AegisBlind);
            counts[blinded ? 0 : 1]++;
        }
    }
}
