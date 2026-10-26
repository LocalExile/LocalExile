using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // One connection owns the buff and its game clock. It is deliberately not a saved/cross-area effect.
    sealed class EnduringCryRuntime
    {
        public EnduringCryBuff? Active;
        public readonly List<EnduringCryBuff> Recovery = [];
        public long Revision, PublishedRevision = -1;
        public ushort Shown;
        public long ShownRevision = -1;
        public int Generation;
        public bool Dead;
    }

    sealed class EnduringCryBuff(double starts, double ends, int regen, Func<bool> sourcePresent, int epoch,
        ushort skillId)
    {
        public readonly double Starts = starts;
        public double Ends = ends;
        public readonly int Regen = regen, Epoch = epoch;
        public readonly ushort SkillId = skillId;
        public readonly Func<bool> SourcePresent = sourcePresent;
    }

    sealed class EnduringCryUse(ushort skillId, EnduringCry.Profile profile, SkillCost cost,
        Func<bool> sourcePresent, double acceptedAt, int generation)
    {
        public readonly ushort SkillId = skillId;
        public readonly EnduringCry.Profile Profile = profile;
        public readonly SkillCost Cost = cost;
        public readonly Func<bool> SourcePresent = sourcePresent;
        public readonly double AcceptedAt = acceptedAt;
        public readonly int Generation = generation;
        public bool Completed;
    }

    static readonly ConditionalWeakTable<ClientState, EnduringCryRuntime> EnduringCries = new();
    static readonly ConditionalWeakTable<CooldownUseTicket, EnduringCryUse> EnduringCryUses = new();

    // These variants require distinct execution/cost/recipient policies. They cannot fall through to a raw 8 s cast.
    static readonly string[] UnsupportedEnduringCryInputs =
    [
        "support_autocast_warcries", "warcries_do_not_apply_buffs_to_self_or_allies",
        "warcry_gain_mp_from_allies", "warcry_gain_mp_from_corpses",
        "warcries_cost_no_mana", "warcries_cost_%_of_life",
    ];

    static EnduringCry.Profile? EnduringCryProfile(ClientState conn, ushort skillId)
    {
        if (InTown(conn)) return null;
        var skill = SupportedSkill(conn, skillId);
        var player = CharacterPlayerStats(conn);
        bool Unsupported(string name) => Stats.Key(name) is uint k
            && (player.GetValueOrDefault(k) != 0 || skill.Stats.GetValueOrDefault(k) != 0);
        if (UnsupportedEnduringCryInputs.Any(Unsupported)
            || skill.Stats.Any(kv => kv.Value != 0 && (Stats.Name(kv.Key)?.StartsWith("support_corrupting_cry",
                StringComparison.Ordinal) == true))) return null;
        return EnduringCry.For(Skills, Stats, skill.Effect, skill.Level, skill.Quality, player, skill.Stats);
    }

    // Count actual player skill IDs: defaults, socketed item gems and host-zero passive grants.
    // Additional item-mod-granted skills must join this identity list when that runtime is implemented.
    // Native non-36 cooldown groups propagate the origin's budget; only a singleton is safe in this bounded adapter.
    internal static bool EnduringCrySingletonGroup(ClientState conn, ushort skillId)
    {
        var grants = GemSkillEffects(conn).ToArray();
        var selected = grants.Where(g => g.Id == skillId).ToArray();
        if (selected.Length != 1 || Skills.EffectId(selected[0].Effect) != EnduringCry.Effect
            || grants.Count(g => g.Effect == selected[0].Effect) != 1) return false;
        var defaults = Skills.PlayerDefaults(Array.Empty<(uint, int)>());
        if (defaults.Any(s => Skills.EffectAtLevelRow(s.EffectsPerLevelRow) == selected[0].Effect)) return false;
        var skill = SupportedSkill(conn, skillId);
        if (Skills.CooldownInfo(skill.Effect, skill.Level)?.Group is not int group) return false;
        if (group == 36) return true;
        var player = CharacterPlayerStats(conn);
        var peers = grants.Where(g => g.Id != skillId).Select(g => SupportedSkill(conn, g.Id)).Concat(
            defaults.Select(s => Skills.EffectAtLevelRow(s.EffectsPerLevelRow)).Select(effect =>
                new SupportedGem(effect, 1, 0, [], Skills.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2),
                    [], 1)));
        foreach (var peer in peers)
        {
            var info = Skills.CooldownInfo(peer.Effect, peer.Level);
            if (info is not { Milliseconds: not null, Group: not null }) return false;
            int peerGroup = info.Group.Value;
            uint? key = Stats.Key("skill_cooldown_group_override");
            int replacement = key is uint k ? peer.Stats.GetValueOrDefault(k) + player.GetValueOrDefault(k) : 0;
            // Count even a zero-base same-group member: a native override/propagated budget can make it relevant.
            if ((replacement == 0 ? peerGroup : replacement) == group) return false;
        }
        return true;
    }

    static Func<bool>? EnduringCrySource(ClientState conn, ushort skillId)
    {
        var grant = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        if (grant.Name is null) return null;
        if (grant.Gem == 0)
            return () => PassiveEffects.Skills(PhreciaNodes(conn)).Any(g => g.Effect == grant.Effect);
        var places = ItemsOf(conn).SocketedGemPlaces();
        // GemSkillIds matches the socket identity. Capture that identity rather than just a same-effect gem count.
        var all = places.SelectMany(p => Items.GemEffects(p.Gem).Select(e => (Place: p, Effect: e))).ToArray();
        var passive = PassiveEffects.Skills(PhreciaNodes(conn)).ToArray();
        var ids = GemSkillIdsOf.GetOrAdd(CharacterName(conn), _ => new()).Assign(
            [.. all.Select(g => new GemSkillIds.Gem(g.Place.ItemId, g.Place.Socket, g.Effect)),
                .. passive.Select(g => new GemSkillIds.Gem(0, g.Node, g.Effect))], GemSkillIdPool.Value);
        int index = Enumerable.Range(0, all.Length).FirstOrDefault(i => ids[i] == skillId, -1);
        if (index < 0) return null;
        var source = all[index].Place;
        return () => !IsInactive(conn, source.ItemId) && ItemsOf(conn).SocketedGemPlaces().Any(p =>
            p.ItemId == source.ItemId && p.Socket == source.Socket && p.Gem.TypeHash == source.Gem.TypeHash
            && p.Gem.GemVariant == source.Gem.GemVariant);
    }

    static void SettleEnduringCryLocked(ClientState conn, EnduringCryRuntime runtime, double now)
    {
        if (conn.PlayerLife == 0 && !runtime.Dead)
        {
            runtime.Generation++;
            runtime.Recovery.Clear();
        }
        runtime.Dead = conn.PlayerLife == 0;
        if (runtime.Active is not { } buff) return;
        if (conn.PlayerLife == 0 || !conn.ProjectileActive(buff.Epoch) || !buff.SourcePresent() || now >= buff.Ends)
        {
            buff.Ends = Math.Min(buff.Ends, now);
            runtime.Active = null;
            runtime.Revision++;
        }
    }

    internal static void EnduringCryDied(ClientState conn)
    {
        lock (conn.Monsters)
            if (EnduringCries.TryGetValue(conn, out var runtime))
                SettleEnduringCryLocked(conn, runtime, conn.Clock.Elapsed.TotalSeconds);
    }

    internal static Dictionary<uint, int> EnduringCryStats(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!EnduringCries.TryGetValue(conn, out var runtime)) return [];
            SettleEnduringCryLocked(conn, runtime, conn.Clock.Elapsed.TotalSeconds);
            if (runtime.Active is not { } buff) return [];
            var result = new Dictionary<uint, int>();
            if (Stats.Key("life_regeneration_rate_per_minute_%") is uint regen) result[regen] = buff.Regen;
            if (Stats.Key("affected_by_a_warcry") is uint affected) result[affected] = 1;
            return result;
        }
    }

    // Integrate only the actual overlap, even if the recovery loop wakes after expiration or a new cast starts.
    internal static double EnduringCryRecovery(ClientState conn, double from, double to)
    {
        lock (conn.Monsters)
        {
            if (!EnduringCries.TryGetValue(conn, out var runtime)) return 0;
            SettleEnduringCryLocked(conn, runtime, conn.Clock.Elapsed.TotalSeconds);
            bool replaced = Stats.Key("life_regeneration_rate_per_minute_%") is uint key && conn.PlayerStats.ContainsKey(key);
            double amount = conn.PlayerLife == 0 || replaced ? 0 : runtime.Recovery.Sum(b =>
                Math.Max(0, Math.Min(to, b.Ends) - Math.Max(from, b.Starts)) * conn.Pools.Life * b.Regen / 6000.0);
            runtime.Recovery.RemoveAll(b => b.Ends <= to);
            return amount;
        }
    }

    internal static bool CompleteEnduringCry(ClientState conn, CooldownUseTicket ticket)
    {
        if (!EnduringCryUses.TryGetValue(ticket, out var use)) return false;
        // Resolve fresh gain sources, then commit power, grant and owner buff in the same combat critical section.
        lock (conn.Monsters)
        {
            var player = CharacterPlayerStats(conn);
            double now = conn.Clock.Elapsed.TotalSeconds;
            var runtime = EnduringCries.GetValue(conn, _ => new());
            SettleEnduringCryLocked(conn, runtime, now);
            if (use.Completed || !conn.ProjectileActive(ticket.ProjectileEpoch) || conn.Paused || conn.PlayerLife == 0
                || runtime.Generation != use.Generation
                || !use.SourcePresent() || now < use.AcceptedAt + use.Profile.Timing.DurationMilliseconds / 1000.0)
                return false;
            use.Completed = true;
            var centre = conn.Walk.Position(now);
            var result = EnduringCry.Resolve(use.Profile, conn.Monsters.Values.Select(m => new EnduringCry.Target(
                m.Rarity, Dist(centre, m.Walk.Position(now)), !m.Dead && m.Life > 0, m.Hostile, m.Owned)));
            if (result.Charges > 0) GrantCharges(conn, ChargeType.Endurance, result.Charges, "Enduring Cry", player);
            SettleEnduringCryLocked(conn, runtime, now);
            if (runtime.Active is { } prior) prior.Ends = Math.Min(prior.Ends, now);
            runtime.Active = result.DurationMilliseconds > 0
                ? new(now, now + result.DurationMilliseconds / 1000.0, result.RegenPerMinute, use.SourcePresent,
                    ticket.ProjectileEpoch, use.SkillId) : null;
            if (runtime.Active is { } buff) runtime.Recovery.Add(buff);
            runtime.Revision++;
            return true;
        }
    }

    static async Task ExecuteEnduringCry(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, CooldownUseTicket ticket)
    {
        var use = EnduringCryUses.GetValue(ticket, _ => throw new InvalidOperationException("Unpaid warcry"));
        double until = use.AcceptedAt + use.Profile.Timing.DurationMilliseconds!.Value / 1000.0;
        while (true)
        {
            lock (conn.Monsters)
            {
                var runtime = EnduringCries.GetValue(conn, _ => new());
                SettleEnduringCryLocked(conn, runtime, conn.Clock.Elapsed.TotalSeconds);
                if (!conn.ProjectileActive(ticket.ProjectileEpoch) || conn.PlayerLife == 0
                    || runtime.Generation != use.Generation || !use.SourcePresent()) return;
                if (!conn.Paused && conn.Clock.Elapsed.TotalSeconds >= until) break;
            }
            await Task.Delay(16);
        }
        if (!CompleteEnduringCry(conn, ticket)) return;
        await RefreshCharges(remote, stream, ciphers, conn, publication => SendCharacterStats(remote, stream, ciphers,
            conn, "Enduring Cry charges", () => ChargePublicationCurrent(conn, publication)));
        await RefreshEnduringCry(remote, stream, ciphers, conn);
    }

    static async Task UseInstantEnduringCry(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId)
    {
        if (EnduringCryProfile(conn, skillId) is not { Timing.Instant: true } profile) return;
        if (!TryBeginCooldownUse(conn, profile.EffectRow,
            CooldownFor(conn, profile.EffectRow, profile.Level, profile.Quality), true, () => { }, out var ticket,
            skillId: skillId)) return;
        var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        if (!await CastGemSkill(remote, stream, ciphers, conn, skillId, at, at, cooldownUse: ticket)) return;
        await SendGenerated(remote, stream, ciphers, () =>
        {
            lock (conn.Monsters)
            {
                if (conn.Paused || !ticket!.CommitNativeUse(conn, skillId)
                    || !EnduringCryUses.TryGetValue(ticket, out var use) || !use.SourcePresent()) return null;
                var current = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                return InstanceProtocol.InstantAction(PlayerId, skillId, current.X, current.Y);
            }
        }, "Enduring Cry instant action");
        await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
    }

    // Build under the cipher gate; never retain a prebuilt timed buff or life packet through pause/death/transfer.
    internal static byte[]? EnduringCryBuffPublication(ClientState conn, int epoch)
    {
        lock (conn.Monsters)
        {
            if (!conn.ProjectileActive(epoch) || conn.Paused || !EnduringCries.TryGetValue(conn, out var runtime))
                return null;
            double now = conn.Clock.Elapsed.TotalSeconds;
            SettleEnduringCryLocked(conn, runtime, now);
            if (runtime.ShownRevision == runtime.Revision) return [];
            var bytes = new List<byte>();
            if (runtime.Shown != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, runtime.Shown));
            runtime.Shown = 0;
            if (runtime.Active is { } buff)
            {
                runtime.Shown = conn.AllocateBuff();
                bytes.AddRange(InstanceEntities.AddBuff(PlayerId, runtime.Shown, EnduringCry.BuffDefinition,
                    PlayerId, buff.SkillId, u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, buff.Ends - now))));
            }
            runtime.ShownRevision = runtime.Revision;
            return bytes.ToArray();
        }
    }

    static async Task RefreshEnduringCry(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn)
    {
        int epoch;
        lock (conn.Monsters)
        {
            if (!EnduringCries.TryGetValue(conn, out var state)) return;
            SettleEnduringCryLocked(conn, state, conn.Clock.Elapsed.TotalSeconds);
            if (state.PublishedRevision == state.Revision) return;
            epoch = conn.ProjectileEpoch;
        }
        await SendGenerated(remote, stream, ciphers, () => EnduringCryBuffPublication(conn, epoch), "Enduring Cry buff");
        long revision;
        lock (conn.Monsters) revision = EnduringCries.GetValue(conn, _ => new()).ShownRevision;
        bool Current()
        {
            lock (conn.Monsters)
            {
                var state = EnduringCries.GetValue(conn, _ => new());
                SettleEnduringCryLocked(conn, state, conn.Clock.Elapsed.TotalSeconds);
                return conn.ProjectileActive(epoch) && !conn.Paused && state.Revision == revision;
            }
        }
        if (!Current()) return;
        if (!await SendCharacterStats(remote, stream, ciphers, conn, "Enduring Cry regeneration", Current)) return;
        await SendGenerated(remote, stream, ciphers, () =>
        {
            lock (conn.Monsters)
            {
                if (!Current()) return null;
                var bytes = PlayerLifeUpdate(conn);
                EnduringCries.GetValue(conn, _ => new()).PublishedRevision = revision;
                return bytes;
            }
        }, "Enduring Cry current life");
    }
}
