using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public const string CatStealthCryEffect = "CatsStealthTriggeredIntimidatingCry";
    public const ushort IntimidatingCryBuffRow = 1035;
    public sealed class CatCrySource(WornGrantedSkills.Grant grant, double since)
    {
        public readonly WornGrantedSkills.Grant Grant = grant;
        public readonly double Since = since;
    }
    public sealed record CatCryLoss(object Rules, int Epoch, CatCrySource Source)
    {
        public bool Consumed;
        internal uint LifeCost;
    }
    sealed record CryGift(InstancePresence Instance, double Until, int Movement);
    sealed class CryRuntime
    {
        internal object? Rules;
        internal int Epoch;
        internal readonly Dictionary<WornGrantedSkills.Grant, CatCrySource> Sources = [];
        internal readonly Queue<CatCryLoss> Pending = [];
        internal readonly ConcurrentQueue<CryGift> Inbox = new();
        internal readonly List<CryGift> Gifts = [];
        internal int Exerts, PresentedMovement;
        internal ushort Buff;
        internal double PresentedUntil;
    }
    static readonly RuleTable<ClientState, CryRuntime> CryRuntimes = new();
    static readonly RuleTable<SpawnedMonster, List<CryGift>> MinionCryGifts = new();

    static CryRuntime CryOf(ClientState conn)
    {
        var state = CryRuntimes.GetOrCreateValue(conn);
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != conn.ProjectileEpoch)
        {
            bool initialized = state.Rules is not null;
            state.Rules = CombatLifetime;
            state.Epoch = conn.ProjectileEpoch;
            state.Sources.Clear(); state.Pending.Clear(); state.Gifts.Clear(); state.Exerts = 0;
            if (initialized) while (state.Inbox.TryDequeue(out _)) { }
        }
        return state;
    }

    static void ReconcileCatCrySourcesLocked(ClientState conn, double now)
    {
        var state = CryOf(conn);
        var live = !CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0
            ? [] : AllWornGrantedSkillsOf(conn).Where(g => g.EffectId == CatStealthCryEffect && !g.Support
                && !IsInactive(conn, g.Host) && LeagueFor(conn).Rules.AllowsGemEffect(g.EffectId)).ToArray();
        var wanted = live.ToHashSet();
        foreach (var old in state.Sources.Keys.Where(g => !wanted.Contains(g)).ToArray()) state.Sources.Remove(old);
        foreach (var grant in live) state.Sources.TryAdd(grant, new(grant, now));
    }

    static void QueueCatStealthLossLocked(ClientState conn, double at, double now)
    {
        ReconcileCatCrySourcesLocked(conn, now);
        var state = CryOf(conn);
        if (state.Rules is null || conn.Paused || InTown(conn)) return;
        foreach (var source in state.Sources.Values)
            if (source.Since <= at + 1e-9 && state.Pending.Count < 64)
                state.Pending.Enqueue(new(state.Rules, conn.ProjectileEpoch, source));
    }

    public static CatCryLoss[] PendingCatCryLosses(ClientState conn) => [.. CryOf(conn).Pending];

    public static bool CatCryLossCurrentLocked(ClientState conn, CatCryLoss loss)
    {
        ReconcileCatCrySourcesLocked(conn, AreaTime(conn));
        return !loss.Consumed && CurrentGameWork && ReferenceEquals(loss.Rules, CombatLifetime)
            && conn.ProjectileActive(loss.Epoch) && !conn.RageRetired && conn.PlayerLife > 0 && !conn.Paused
            && !InTown(conn) && CryOf(conn).Sources.TryGetValue(loss.Source.Grant, out var current)
            && ReferenceEquals(current, loss.Source);
    }

    // Source, payment, cooldown and payload commit at the same native-send gate. A revoked receipt spends nothing.
    public static byte[]? AdmitCatCryLossLocked(ClientState conn, CatCryLoss loss)
    {
        if (!CatCryLossCurrentLocked(conn, loss)) return null;
        var grant = loss.Source.Grant;
        var source = GemSkillEffects(conn).FirstOrDefault(g => g.Effect == grant.Effect
            && SupportedSkill(conn, g.Id).WornGrant == grant);
        if (source.Name is null || GemSkillInstanceDisabled(conn, source.Effect, source.Id, triggered: true) is not null)
            return null;
        var gem = SupportedSkill(conn, source.Id);
        if (CooldownFor(conn, source.Effect, source.Level, source.Quality) is not { } plan) return null;
        double now = AreaTime(conn);
        int key = CooldownKey(conn, source.Effect, source.Level);
        var cooldown = CooldownStateLocked(conn, key, plan, now);
        if (!cooldown.Ready || !PayMana(conn, source.Id, out _, out uint lifeCost)) return null;
        if (!cooldown.TryUse(now, plan)) return null;
        loss.Consumed = true;
        loss.LifeCost = lifeCost;
        conn.SkillReadyAt[key] = now + cooldown.RemainingSeconds;
        cooldown.ConfirmNativeUse(cooldown.UseVersion, source.Id, synchronizeProgress: true);
        ApplyIntimidatingCryLocked(conn, gem.Stats, CharacterPlayerStats(conn), now);
        Console.WriteLine($"[gateway] Cat Stealth loss triggers Intimidating Cry skill=0x{source.Id:x4} source={grant.Host} t={now:0.000000}");
        var at = conn.Walk.Position(now);
        return InstanceProtocol.InstantAction(PlayerId, source.Id, at.X, at.Y,
            sequence: conn.LockstepClient ? NextLockstepSequence(conn) : (ushort)0xffff,
            flags: conn.LockstepClient ? (ushort)0x101 : (ushort)0, triggered: true);
    }

    public static void ApplyIntimidatingCryLocked(ClientState conn, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> player, double now)
    {
        int Own(string n) => GolemStat(own, n);
        int Player(string n) => GolemStat(player, n);
        var at = conn.Walk.Position(now);
        int radius = SkillArea.Radius(WarcryRadius, n => ActorScale.Saturate((long)Player(n) + Own(n)), spell: false);
        var enemies = conn.Monsters.Values.Where(m => m.Hostile && !m.Dead && m.Life > 0
            && Dist(m.Walk.Position(now), at) <= radius).ToArray();
        int power = Player("warcries_have_infinite_power") != 0 ? InfiniteWarcryPower
            : enemies.Sum(m => WarcryPower(m.Rarity));
        double taunt = TauntSeconds(Player);
        TauntLocked(conn, at, radius, now, taunt);
        if (Own("enemies_taunted_by_your_warcies_are_intimidated") != 0 && taunt > 0)
            foreach (var m in enemies)
                m.IntimidatedUntil = Math.Max(m.IntimidatedUntil, now + taunt / m.RecipientDebuffRate);
        double duration = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player, Own);
        double effect = Math.Max(0, 1 + ((long)Player("warcry_buff_effect_+%") + Own("skill_buff_effect_+%")) / 100d);
        int movement = (int)Math.Clamp(Math.Floor(Math.Min(30, power) / 5d)
            * Own("intimidating_cry_movement_speed_+%_per_5_power_up_to_cap") * effect, 0, int.MaxValue);
        var gift = new CryGift(conn.Instance.Presence, now + duration, movement);
        if (duration > 0)
        {
            CryOf(conn).Gifts.Add(gift);
            // Deliver immutable release-time gifts. Recipients consume their own inbox, without cross-simulation reads.
            foreach (var peer in conn.Instance.PlayersExcept(conn.PeerId)
                         .Where(p => p.Current && p.Life > 0 && Dist((p.X, p.Y), at) <= radius))
                if (peer.Owner is { } recipient)
                {
                    var inbox = CryRuntimes.GetOrCreateValue(recipient).Inbox;
                    if (inbox.Count < 64) inbox.Enqueue(gift);
                }
            foreach (var m in conn.Monsters.Values.Where(m => m.OwnerId is not null && !m.Hostile && !m.Dead && m.Life > 0
                         && Dist(m.Walk.Position(now), at) <= radius))
                MinionCryGifts.GetOrCreateValue(m).Add(gift);
        }
        CryOf(conn).Exerts = Math.Max(0, Own("skill_empowers_next_x_melee_attacks"));
        conn.RecentEvents.Record(StatConditions.UsedWarcry, now);
        conn.AuraStatsDirty = true;
        Console.WriteLine($"[gateway] Intimidating Cry: power={power} taunted={enemies.Length} movement={movement} "
            + $"duration={duration:0.###} exerts={CryOf(conn).Exerts}");
    }

    public static int IntimidatingCryMovement(ClientState conn)
    {
        lock (conn.Monsters) return IntimidatingCryMovementLocked(conn);
    }

    static int IntimidatingCryMovementLocked(ClientState conn)
    {
        var state = CryOf(conn);
        double now = AreaTime(conn);
        while (state.Inbox.TryDequeue(out var gift))
            if (ReferenceEquals(gift.Instance, conn.Instance.Presence) && gift.Until > now) state.Gifts.Add(gift);
        if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0)
        { state.Gifts.Clear(); state.Exerts = 0; }
        state.Gifts.RemoveAll(g => !ReferenceEquals(g.Instance, conn.Instance.Presence) || g.Until <= now);
        return state.Gifts.Select(g => g.Movement).DefaultIfEmpty().Max();
    }

    static int MinionIntimidatingCryMovement(ClientState conn, SpawnedMonster? minion)
    {
        if (minion is null || !MinionCryGifts.TryGetValue(minion, out var gifts)) return 0;
        double now = AreaTime(conn);
        gifts.RemoveAll(g => !ReferenceEquals(g.Instance, conn.Instance.Presence) || g.Until <= now || minion.Dead);
        return gifts.Select(g => g.Movement).DefaultIfEmpty().Max();
    }

    public static int IntimidatingCryExerts(ClientState conn)
    {
        IntimidatingCryMovement(conn);
        return CryOf(conn).Exerts;
    }

    public static bool ConsumeIntimidatingCryExertLocked(ClientState conn, bool eligible)
    {
        if (!eligible || IntimidatingCryExerts(conn) <= 0) return false;
        CryOf(conn).Exerts--;
        return true;
    }

    static bool IntimidatingCryCanExert(ClientState conn, ushort skill, int effect) =>
        SkillsFor(conn).IsAttack(effect) && SkillsFor(conn).IsMelee(effect) && !SkillsFor(conn).IsVaal(effect)
        && !SkillsFor(conn).IsChannelled(effect) && MeleeAttackRepeats(conn, skill) == 0
        && !IsTotemSkill(conn, skill) && PlacedKind(SupportedSkill(conn, skill)) is { Trap: false, Mine: false };

    public static GamblerModifiers WithIntimidatingCryExert(GamblerModifiers modifiers, bool exerted) =>
        exerted ? modifiers with { DoubleDamageChance = Math.Max(100, modifiers.DoubleDamageChance) } : modifiers;

    static bool IntimidatingCryPoolsChangedLocked(ClientState conn)
    {
        int movement = IntimidatingCryMovement(conn);
        var state = CryOf(conn);
        if (movement == state.PresentedMovement) return false;
        state.PresentedMovement = movement;
        return true;
    }

    static byte[] IntimidatingCryBuffRecordsLocked(ClientState conn)
    {
        IntimidatingCryMovement(conn);
        var state = CryOf(conn);
        double until = state.Gifts.Select(g => g.Until).DefaultIfEmpty().Max();
        if (until == state.PresentedUntil) return [];
        var packets = new List<byte[]>();
        if (state.Buff != 0) packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.Buff));
        state.Buff = until > AreaTime(conn) ? conn.NextBuff++ : (ushort)0;
        state.PresentedUntil = until;
        if (state.Buff != 0) packets.Add(InstanceEntities.AddBuff(PlayerId, state.Buff, IntimidatingCryBuffRow, PlayerId,
            u68: BitConverter.SingleToUInt32Bits((float)(until - AreaTime(conn)))));
        return [.. packets.SelectMany(p => p)];
    }

    static async Task DrainCatCryLosses(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        CatCryLoss[] losses;
        lock (conn.Monsters) { var state = CryOf(conn); losses = [.. state.Pending]; state.Pending.Clear(); }
        foreach (var loss in losses)
        {
            bool accepted = false;
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, [], "0x01ce Cat Stealth loss triggers Intimidating Cry",
                atGate: () =>
                {
                    using (ProjectileMutation(conn))
                    {
                        var packet = AdmitCatCryLossLocked(conn, loss);
                        accepted = packet is not null;
                        return packet;
                    }
                });
            if (accepted)
            {
                await SendSkillCost(remote, stream, ciphers, conn, loss.LifeCost, "triggered Intimidating Cry cost");
                await SendChargeStats(remote, stream, ciphers, conn, "Intimidating Cry");
                var at = conn.Walk.Position(AreaTime(conn));
                await WarcryReleased(remote, stream, ciphers, conn, at, WarcryRadius, loss.Epoch);
            }
        }
        lock (conn.Monsters)
        {
            var state = CryOf(conn);
            if (losses.Length == 0 && state.Buff == 0 && state.Gifts.Count == 0 && state.Inbox.IsEmpty) return;
        }
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, [], "Intimidating Cry buff",
            atGate: () =>
            {
                lock (conn.Monsters)
                    return !CurrentGameWork || conn.Closed || conn.LeavingArea ? null : IntimidatingCryBuffRecordsLocked(conn);
            });
        if (losses.Length > 0) await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
    }
}
