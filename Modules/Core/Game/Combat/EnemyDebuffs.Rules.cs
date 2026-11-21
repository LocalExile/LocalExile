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
            m.TakenFromPlayer = m.TakenFromPlayer with { All = player.EnemyDebuffs.TauntedDamageTakenPct };
            taunted++;
        }
        if (taunted > 0) PresentNearby(player);
        return taunted;
    }

    // A nearby presentation (StatPresentations: a walker moved, a debuff changed or ended) steps the player's debuffs on monsters.
    public static Task TickPassiveAuras(Player player) =>
        EnemyDebuffsTick(player, AreaTime(player));

    // One step of the player's debuffs on monsters, worked out at the send gate so a removed monster gets none: a taunt
    // shows the taunted buff (408) for its time left and goes when it ends, the monster dies or the player dies (wiki: a
    // taunt ends when its source dies), and his hits' Intimidate and Unnerve (HitDebuffs) the same way; each enemy aura
    // of his passives (Light of Divinity's Augury of Penitence, 2551)
    // and Born under Fire's) is on every hostile living monster within its radius of the player while he lives, and the
    // blind aura (SaboteurBlind) blinds them, shown with the blinded buff (PassiveBuffAuras.BlindState, Game.Blinded).
    // Pride adds its own enemy aura (BuffDefinitions 1331) with the ramp each monster keeps while it stays inside.
    // Each monster then keeps the increased damage taken they give (SpawnedMonster.TakenFromPlayer), which its hits and
    // burns read. A refund or a death takes the auras off; a new area is a new connection.

    // The zones of the player's auras on enemies (one per enemy aura's buff row, Flesh and Stone's, the blinds').
    const string EnemyAuraZones = "enemy auras", FleshZone = "flesh and stone", BlindZone = "blind", AegisBlindZone = "aegis blind";

    // When a timed debuff on a monster next ends (taunt, hit debuffs, exposure, wither, curse), or a step of Pride's
    // 4-second ramp is due.
    static double? MonsterDebuffsNextChange(Player player, double now)
    {
        double next = double.PositiveInfinity;
        void Next(double until) { if (until > now && until < next) next = until; }
        foreach (var m in player.Area.Monsters.Values)
        {
            if (m.Dead) continue;
            Next(m.TauntedUntil); Next(m.IntimidatedUntil); Next(m.UnnervedUntil); Next(m.CrushedUntil);
            Next(m.MaimedUntil); Next(m.HinderedUntil);
            foreach (double until in m.ExposureUntil) Next(until);
            foreach (var stack in m.Withered) Next(stack.Until);
            foreach (var curse in m.Curses.All.Values) Next(curse.Until);
            if (!double.IsNaN(m.PrideSince) && now - m.PrideSince < 4) Next(now + Program.FrameMs / 1000.0);
        }
        return double.IsFinite(next) ? next : null;
    }

    public static async Task EnemyDebuffsTick(Player player, double now)
    {
        ushort[] nodes;
        if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
        nodes = [.. player.Character.Passives, .. player.Character.MainAscendancy.Allocated];
        // Outside the lock: the first call loads the aura table from PassiveSkills.
        var all = PassiveBuffAuras.EnemyAurasFor(nodes, LeagueFor(player).HardMode, EnemyAuraActs);
        await Send(player, [], null, atGate: () =>
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea) return null;
            now = AreaTime(player);
            if (MonsterDebuffsNextChange(player, now) is double next) PresentIn(player, next - now + 1e-6);
            ushort[] currentNodes = [.. player.Character.Passives, .. player.Character.MainAscendancy.Allocated];
            var current = nodes.SequenceEqual(currentNodes) ? all
                : PassiveBuffAuras.EnemyAurasFor(currentNodes, LeagueFor(player).HardMode, EnemyAuraActs);
            var auras = current.Where(a => a.Template != PassiveBuffAuras.BlindTemplate).ToList();
            int? blindRadius = PassiveBuffAuras.BlindRadius(current);
            var playerStats = CharacterPlayerStats(player);
            // The influenced blind and unique intimidate BuffTemplates use 35 and 25 cells respectively.
            if (StatOf(playerStats, Stat.VirtualNearbyEnemiesAreBlinded) > 0)
                blindRadius = Math.Max(blindRadius ?? 0, 35);
            if (StatOf(playerStats, Stat.VirtualNearbyEnemiesAreIntimidated) > 0)
                auras.Add(new(0, "NearbyEnemiesAreIntimidatedUnique__1", IntimidatedBuffRow, 25, [3], []));
            var config = player.EnemyDebuffs;
            bool alive = player.PlayerLife > 0;
            var here = player.Walk.Position(now);
            uint? elementalKey = Stat.ElementalDamageTakenPlusPct;
            // Pride is a skill aura whose effect is on enemies: a synthetic enemy aura carries its buff, and its ramp
            // is kept per monster.
            AuraCatalog.Aura? pride = player.Auras.Values.FirstOrDefault(a => AuraCatalog.Family(a.EffectId) == "Pride");
            IReadOnlyList<PassiveBuffAuras.Aura> enemyAuras = auras;
            int prideRadius = 0;
            (int Min, int Max) prideValues = default;
            if (pride is not null)
            {
                var skills = SkillsFor(player);
                prideRadius = AuraRadius.Of(pride, player.Pools.AuraAreaPct, skills, Stats);
                prideValues = AuraCatalog.PridePhysicalDamageTaken(pride, player.Pools.AuraEffectPct, skills, Stats);
                enemyAuras = [.. auras, new PassiveBuffAuras.Aura(0, "Pride", AuraCatalog.PrideBuffRow,
                    prideRadius, [3], [])];
            }
            // Flesh and Stone's Blood Stance affects physical hits, with distance falloff.
            // Sand Stance's hit reduction belongs to the incoming-hit executor, not the blind aura.
            AuraCatalog.Aura? fleshAndStone = player.Auras.Values.FirstOrDefault(a =>
                AuraCatalog.Family(a.EffectId) == "FleshAndStone");
            int fleshRadius = 0, fleshPhysical = 0;
            bool fleshBlood = false;
            if (fleshAndStone is not null)
            {
                var fleshSkills = SkillsFor(player);
                fleshRadius = AuraRadius.Of(fleshAndStone, player.Pools.AuraAreaPct, fleshSkills, Stats);
                fleshBlood = fleshAndStone.Stance != "Sand";
                if (fleshBlood)
                    fleshPhysical = AuraCatalog.FleshAndStonePhysicalTaken(fleshAndStone, player.Pools.AuraEffectPct,
                        fleshSkills, Stats);
            }
            var bytes = new List<byte>();
            CursesTickLocked(player, now, bytes);
            int added = 0, removed = 0;
            // One timed debuff on a monster: its buff shown for the time left, shown again when that changes, taken
            // off when it ends or the monster or the player dies. Whether it is on.
            bool Timed(uint id, bool living, ref double until, ref double shown, ref ushort buff, ushort row,
                IReadOnlyList<uint>? values, double rate = 1)
            {
                bool on = living && until > now;
                if (!on) until = double.NegativeInfinity;
                if (buff != 0 && (!on || shown != until))
                {
                    player.Area.Buffs.Clear(id, row, player.Id);
                    buff = 0;
                    removed++;
                }
                if (on && buff == 0)
                {
                    shown = until;
                    buff = 1;
                    player.Area.Buffs.Set(id, new(row, player.Id, Values: values, U68: Buff.Seconds((until - now) * rate)));
                    added++;
                }
                return on;
            }
            // Its Withered stacks (WitherOnHitLocked): one buff with their count and the time the last one has left,
            // shown again when either changes; off when none runs or it or the player dies.
            void Withers(uint id, bool living, SpawnedMonster m)
            {
                if (m.Withered.Count == 0 && m.WitherBuff == 0) return;
                if (living) m.Withered.RemoveAll(s => s.Until <= now);
                else m.Withered.Clear();
                int stacks = m.Withered.Count;
                double until = stacks > 0 ? m.Withered.Max(s => s.Until) : 0;
                if (m.WitherBuff != 0 && (stacks != m.WitherShownStacks || until != m.WitherShownUntil))
                {
                    player.Area.Buffs.Clear(id, WitheredBuffRow, player.Id);
                    m.WitherBuff = 0;
                    removed++;
                }
                if (stacks == 0 || m.WitherBuff != 0) return;
                (m.WitherShownStacks, m.WitherShownUntil) = (stacks, until);
                m.WitherBuff = 1;
                player.Area.Buffs.Set(id, new(WitheredBuffRow, player.Id, Values: [(uint)Math.Round(m.Withered[^1].Pct), 1],
                    U68: Buff.Seconds(until - now), Stacks: (ushort)stacks, U82: 0));
                added++;
            }
            // Who each aura reaches follows the walkers' moves (EnemyAuraZones); entering or leaving one presents again.
            var blindAegis = PhysicalAegisStatus(player, now);
            int aegisReach = alive && blindAegis.Active && PhysicalAegisHasBlind(player, blindAegis) ? PhysicalAegisRadius(player, blindAegis) : 0;
            SyncZones(player, player.Id, EnemyAuraZones, !alive ? []
                : [.. enemyAuras.Select(a => ((object)a.Definition, a.Radius, here)),
                    .. fleshAndStone is not null ? [((object)FleshZone, fleshRadius, here)] : Array.Empty<(object, int, (int, int))>(),
                    .. blindRadius is int blindAt and > 0 ? [((object)BlindZone, blindAt, here)] : Array.Empty<(object, int, (int, int))>(),
                    .. aegisReach > 0 ? [((object)AegisBlindZone, aegisReach, here)] : Array.Empty<(object, int, (int, int))>()],
                new(ReachesHostileMonsters(player, body: false), new EntityId(player.Id), _ => PresentNearby(player)));
            bool In(object key, uint id) => InZone(player, player.Id, EnemyAuraZones, key, id);
            foreach (var (id, m) in player.Area.Monsters)
            {
                // Rupture outlives short bleeds and the attacker; only its own timers or the target's death end it.
                SettleBleeding(m, now);
                if (MonsterShown(player, id))
                    foreach (var packet in PresentRupturesLocked(player, id, m, now)) bytes.AddRange(packet);
                bool living = alive && !m.Dead && m.Life > 0 && m.Hostile;
                bool taunted = Timed(id, living, ref m.TauntedUntil, ref m.TauntShownUntil, ref m.TauntBuff,
                    FlaskGrounds.TauntedBuffRow, TauntedBuffValues(player.Id), m.RecipientDebuffRate);
                Timed(id, living, ref m.IntimidatedUntil, ref m.IntimidateShownUntil, ref m.IntimidateBuff, IntimidatedBuffRow, null);
                Timed(id, living, ref m.UnnervedUntil, ref m.UnnerveShownUntil, ref m.UnnerveBuff, UnnervedBuffRow, null);
                Timed(id, living, ref m.CrushedUntil, ref m.CrushShownUntil, ref m.CrushBuff, CrushedBuffRow, null);
                Timed(id, living, ref m.MaimedUntil, ref m.MaimShownUntil, ref m.MaimBuff, MaimedBuffRow, null);
                Timed(id, living, ref m.HinderedUntil, ref m.HinderShownUntil, ref m.HinderBuff, HinderedBuffRow, null);
                for (int i = 0; i < ExposureTypes.Length; i++)
                    if (!Timed(id, living, ref m.ExposureUntil[i], ref m.ExposureShownUntil[i], ref m.ExposureBuff[i],
                            ExposureBuffRows[i], null))
                        EndExposureLocked(m, ExposureTypes[i]);
                Withers(id, living, m);
                if (m.PlayerAuras.Count > 0)
                    foreach (var ((peer, definition), buff) in m.PlayerAuras.ToArray())
                        if (peer == player.Id && (!living || enemyAuras.All(a => a.Definition != definition) || !In(definition, id)))
                        {
                            player.Area.Buffs.Clear(id, definition, peer);
                            m.PlayerAuras.Remove((peer, definition));
                            removed++;
                        }
                int elemental = 0;
                foreach (var aura in enemyAuras)
                {
                    if (!living || !In(aura.Definition, id)) continue;
                    if (!m.PlayerAuras.ContainsKey((player.Id, aura.Definition)))
                    {
                        m.PlayerAuras[(player.Id, aura.Definition)] = 1;
                        player.Area.Buffs.Set(id, new(aura.Definition, player.Id));
                        added++;
                    }
                    elemental += aura.Stats.Where(s => s.Key == elementalKey).Sum(s => s.Value);
                }
                if (pride is not null && living && In(AuraCatalog.PrideBuffRow, id))
                {
                    if (double.IsNaN(m.PrideSince)) m.PrideSince = now;
                    m.PridePhysicalMore = AuraCatalog.PridePhysicalMore(prideValues.Min, prideValues.Max, now - m.PrideSince);
                }
                else
                {
                    m.PrideSince = double.NaN;
                    m.PridePhysicalMore = 0;
                }
                // Blood Stance: the more Physical Hit Damage taken falls off with the monster's distance from the
                // player, full at 0 and nothing at the aura's edge (the data's ..._from_player_distance).
                int physical = fleshBlood && living && fleshPhysical > 0 && In(FleshZone, id)
                    ? (int)Math.Round(fleshPhysical * AuraCatalog.DistanceEffect(Dist(m.Walk.Position(now), here), fleshRadius), MidpointRounding.AwayFromZero)
                    : 0;
                // Bottled Faith: consecrated ground created during its effect makes the enemies on it take more damage.
                int consecrated = living ? ConsecratedEnemyDamageTaken(player, id) : 0;
                m.TakenFromPlayer = ((taunted ? config.TauntedDamageTakenPct : 0) + consecrated, elemental, physical);
            }
            int blindReach = blindRadius ?? 0;
            int? radius = player.PlayerLife > 0 && blindReach > 0 ? blindReach : null;
            HashSet<uint> inside = radius is int r
                ? ZoneMembers(player, player.Id, EnemyAuraZones, BlindZone).Select(e => e.Value)
                    .Where(id => player.Area.Monsters.GetValueOrDefault(id) is { } m && m.Effects[Stat.CannotBeBlinded] == 0).ToHashSet()
                : [];
            foreach (var (id, _, blinded) in player.BlindAura.Reconcile(inside, player.Area.Monsters.ContainsKey, () => 1))
            {
                if (blinded) player.Area.Buffs.Set(id, new(FlaskGrounds.BlindedBuffRow, player.Id, Tag: (ushort)BuffTag.BlindAura));
                else player.Area.Buffs.Clear(id, FlaskGrounds.BlindedBuffRow, player.Id, tag: (ushort)BuffTag.BlindAura);
                if (blinded) added++; else removed++;
            }
            // Physical Aegis blinds nearby enemies only while any capacity remains. It has its own instances, so
            // depleting the Aegis removes only this source and leaves a timed hit Blind untouched.
            var aegis = PhysicalAegisStatus(player, now);
            int aegisRadius = alive && aegis.Active && PhysicalAegisHasBlind(player, aegis)
                ? PhysicalAegisRadius(player, aegis) : 0;
            HashSet<uint> aegisInside = aegisRadius > 0
                ? ZoneMembers(player, player.Id, EnemyAuraZones, AegisBlindZone).Select(e => e.Value)
                    .Where(id => player.Area.Monsters.GetValueOrDefault(id) is { } m && m.Effects[Stat.CannotBeBlinded] == 0).ToHashSet()
                : [];
            foreach (var (id, _, blinded) in aegis.Blind.Reconcile(aegisInside, player.Area.Monsters.ContainsKey, () => 1))
            {
                if (blinded) player.Area.Buffs.Set(id, new(FlaskGrounds.BlindedBuffRow, player.Id, Tag: (ushort)BuffTag.AegisBlind));
                else player.Area.Buffs.Clear(id, FlaskGrounds.BlindedBuffRow, player.Id, tag: (ushort)BuffTag.AegisBlind);
                if (blinded) added++; else removed++;
            }
            if (added + removed > 0)
                Console.WriteLine($"[gateway] {player.Connection.Remote} enemy debuffs: {added} added, {removed} removed (t={now:0.000})");
            if (bytes.Count == 0) return null;
            return [.. bytes];
        });
    }
}
