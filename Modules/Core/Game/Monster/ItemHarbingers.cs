using GameServer.Core;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string ItemHarbingerFamily = "ItemHarbinger";
    const int HarbingerBuffRadius = 50;
    const int HarbingerChainRadius = 35;
    const int HarbingerSecondaryShockChance = 15;
    const int HarbingerWarp = 8722;

    sealed record ItemHarbingerBuff(ushort Row, double Until, IReadOnlyDictionary<uint, int> Stats, uint[] Values);
    sealed class ItemHarbingerState
    {
        public readonly Dictionary<ushort, ItemHarbingerBuff> Buffs = [];
        public readonly Dictionary<uint, Player> PlayersInTime = [];
        public readonly Dictionary<uint, SpawnedMonster> MonstersInTime = [];
        public double NextTimeCheck;
    }
    static readonly RuleTable<GolemState.Golem, ItemHarbingerState> ItemHarbingers = new();
    static readonly RuleTable<Player, Dictionary<(uint Source, ushort Row), ItemHarbingerBuff>> ItemHarbingerRecipients = new();

    static KeyValuePair<uint, SpawnedMonster> HarbingerNextEnemy(Player player, (int X, int Y) at, int radius,
        double now, Func<SpawnedMonster, bool>? filter = null, HashSet<uint>? visited = null) =>
        player.Area.MonstersNear(at, radius)
            .Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                && (visited is null || !visited.Contains(p.Key)) && (filter is null || filter(p.Value))
                && Dist(p.Value.Walk.Position(now), at) <= radius)
            .OrderBy(p => Dist(p.Value.Walk.Position(now), at)).ThenBy(p => p.Key).FirstOrDefault();

    static void ItemHarbingerStepLocked(Player player, GolemState.Golem harbinger, (int X, int Y) at, (int X, int Y) owner, double now)
    {
        var state = ItemHarbingers.GetOrCreateValue(harbinger);
        foreach (var buff in state.Buffs.Values.Where(b => now >= b.Until).ToArray())
        {
            RemoveItemHarbingerBuff(player, harbinger.Id, buff.Row);
            state.Buffs.Remove(buff.Row);
            if (buff.Row is 921 or 922) ClearHarbingerTime(player, harbinger, state, buff.Row);
        }
        if (InTown(player)) { GolemFollowLocked(player, harbinger, owner, now); return; }
        if (state.Buffs.Values.FirstOrDefault(b => b.Row is 921 or 922) is { } time && now >= state.NextTimeCheck)
        {
            state.NextTimeCheck = now + .1;
            SyncHarbingerTime(player, harbinger, state, time, owner, now);
        }
        var ready = harbinger.Abilities.Where(a => a.Effect != HarbingerWarp && GolemReady(harbinger, a, now)).ToArray();
        if (ready.Length == 0) { GolemFollowLocked(player, harbinger, owner, now); return; }
        var ability = ready.Length == 1 ? ready[0] : ready[AreaRng(player).Next(ready.Length)];
        if ((ability.Effect is 8708 or 8709 && player.ChannelSkill == 0)
            || (ability.Effect is 8720 or 8721
                && HarbingerNextEnemy(player, owner, HarbingerBuffRadius, now, m => m.Rarity is MonsterRarity.Rare or MonsterRarity.Unique).Value is null))
        {
            GolemFollowLocked(player, harbinger, owner, now);
            return;
        }
        if (ability.Effect is >= 8725 and <= 8728)
        {
            var target = HarbingerNextEnemy(player, at, HarbingerBuffRadius, now);
            if (target.Value is not null)
                GolemReserveLocked(player, harbinger, ability, target.Key, target.Value, target.Value.Walk.Position(now), now);
            else GolemFollowLocked(player, harbinger, owner, now);
            return;
        }
        GolemReserveLocked(player, harbinger, ability, 0, null, owner, now);
    }

    static void ReleaseItemHarbingerSkillLocked(Player player, GolemState.Golem fighter, GolemState.Action action, double now)
    {
        var a = action.Ability;
        int Get(uint stat) => GolemStat(a.Stats, stat);
        int duration = Get(Stat.BaseSkillEffectDuration);
        if (a.Effect is >= 8725 and <= 8728)
        {
            ReleaseHarbingerStorms(player, action, now);
            foreach (var peer in fighter.Abilities.Where(a => a.Effect is >= 8725 and <= 8728 && a != action.Ability))
                GolemStartCooldown(fighter, peer, now);
            return;
        }
        if (a.Effect is 8700 or 8701)
        {
            GainArcaneSurgeLocked(player, now, duration, Get(Stat.SupportArcaneSurgeCastSpeedPlusPct),
                Get(Stat.SupportArcaneSurgeManaRegenerationRatePlusPct));
            return;
        }
        ushort row = a.Effect switch
        {
            8704 => 921, 8705 => 922, 8708 => 923, 8709 => 924,
            >= 8710 and <= 8714 => 925, >= 8715 and <= 8719 => 926,
            8720 => 927, 8721 => 928, 8702 => 929, _ => 0,
        };
        if (row == 0 || duration <= 0 || (row is 923 or 924 && player.ChannelSkill == 0)) return;
        if (row is 925 or 926)
            foreach (var peer in fighter.Abilities.Where(peer => peer != a && peer.Effect is >= 8710 and <= 8719))
                GolemStartCooldown(fighter, peer, now);

        var keys = ClientTables.Table("BuffDefinitions")[row].Ints("StatsKeys").Select(key => (uint)key + 1).ToArray();
        var values = keys.ToDictionary(key => key, Get);
        if (row is 923 or 924)
        {
            values[Stat.HarbingerChannelBuffDamageTakenPlusPctFinal] = Get(Stat.BaseDamageTakenPlusPct);
            values[Stat.ImmuneToElementalAilments] = 1;
        }
        if (row == 929) values[Stat.BaseChanceToShockPct] = HarbingerSecondaryShockChance;
        var buff = new ItemHarbingerBuff(row, now + duration / 1000d, values,
            [.. keys.Select(key => unchecked((uint)values[key]))]);
        var state = ItemHarbingers.GetOrCreateValue(fighter);
        state.Buffs[row] = buff;
        GrantItemHarbingerBuff(player, fighter.Id, buff, now);
        if (row is 921 or 922) SyncHarbingerTime(player, fighter, state, buff, player.Walk.Position(now), now);
    }

    static void ReleaseHarbingerStorms(Player player, GolemState.Action action, double now)
    {
        if (action.Victim is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false } monster
            || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(action.Target), monster)) return;
        var a = action.Ability;
        var ailment = a.Effect is 8725 or 8727 ? MonsterElementalAilments.Kind.Chill : MonsterElementalAilments.Kind.Shock;
        uint duration = ailment == MonsterElementalAilments.Kind.Chill
            ? Stat.ActiveSkillChillDurationPlusPctFinal : Stat.ActiveSkillShockDurationPlusPctFinal;
        int Get(uint stat) => GolemStat(a.Stats, stat);
        double seconds = 2 * Math.Max(0, 1 + Get(duration) / 100d);
        int chains = Get(Stat.NumberOfChains);
        uint id = action.Target;
        var visited = new HashSet<uint>();
        for (int jump = 0; jump <= chains; jump++)
        {
            visited.Add(id);
            monster.Ailments.WithoutDamage(ailment, seconds, now, Get, n => monster.Effects[n]);
            monster.PresentationChanged?.Invoke();
            if (jump == chains) break;
            var next = HarbingerNextEnemy(player, monster.Walk.Position(now), HarbingerChainRadius, now, visited: visited);
            if (next.Value is null) break;
            (id, monster) = next;
        }
    }

    static void GrantItemHarbingerBuff(Player recipient, uint source, ItemHarbingerBuff buff, double now)
    {
        ItemHarbingerRecipients.GetOrCreateValue(recipient)[(source, buff.Row)] = buff;
        recipient.Area.Buffs.Set(recipient.Id, new(buff.Row, source, Values: buff.Values, U68: Buff.Seconds(buff.Until - now)));
        MarkPlayerStats(recipient, PlayerStatStep.Sources);
        PresentStats(recipient);
    }

    static void RemoveItemHarbingerBuff(Player recipient, uint source, ushort row)
    {
        if (!ItemHarbingerRecipients.TryGetValue(recipient, out var buffs) || !buffs.Remove((source, row))) return;
        recipient.Area.Buffs.Clear(recipient.Id, row, source);
        MarkPlayerStats(recipient, PlayerStatStep.Sources);
        PresentStats(recipient);
    }

    public static Dictionary<uint, int> ItemHarbingerStatSources(Player player, Dictionary<uint, int> stats)
    {
        if (!ItemHarbingerRecipients.TryGetValue(player, out var buffs) || buffs.Count == 0 || player.PlayerLife == 0) return stats;
        double now = AreaTime(player);
        int time = 0;
        foreach (var buff in buffs.Values)
        {
            if (now >= buff.Until) continue;
            StatsChangeIn(buff.Until - now);
            if (buff.Row is 921 or 922)
            {
                time = Math.Min(time, buff.Stats.GetValueOrDefault(Stat.ActionSpeedMinusPct));
                continue;
            }
            foreach (var (key, value) in buff.Stats)
                if (value != 0) stats[key] = ActorScale.Saturate((long)stats.GetValueOrDefault(key) + value);
        }
        if (time != 0) stats[Stat.ActionSpeedMinusPct] = ActorScale.Saturate((long)stats.GetValueOrDefault(Stat.ActionSpeedMinusPct) + time);
        return stats;
    }

    static void SyncHarbingerTime(Player owner, GolemState.Golem fighter, ItemHarbingerState state,
        ItemHarbingerBuff buff, (int X, int Y) at, double now)
    {
        bool Near((int X, int Y) cell) => Dist(at, cell) <= HarbingerBuffRadius;
        var players = owner.Instance.PlayersExcept(owner.Id)
            .Where(p => ReferenceEquals(p.Area, owner.Area) && p.PlayerLife > 0 && Near(p.Walk.Position(now))).ToDictionary(p => p.Id);
        foreach (var old in state.PlayersInTime.Values.Where(old => !players.ContainsKey(old.Id)))
            RemoveItemHarbingerBuff(old, fighter.Id, buff.Row);
        foreach (var player in players.Values)
            if (!state.PlayersInTime.ContainsKey(player.Id)) GrantItemHarbingerBuff(player, fighter.Id, buff, now);
        state.PlayersInTime.Clear();
        foreach (var (id, player) in players) state.PlayersInTime.Add(id, player);

        var monsters = owner.Area.MonstersNear(at, HarbingerBuffRadius)
            .Where(p => p.Key != fighter.Id && !p.Value.Dead && p.Value.Life > 0
                && (buff.Row == 921 || !p.Value.Hostile) && Near(p.Value.Walk.Position(now))).ToDictionary();
        string key = $"harbinger-time:{fighter.Id}";
        foreach (var (id, monster) in state.MonstersInTime)
            if (!ReferenceEquals(monsters.GetValueOrDefault(id), monster))
            {
                monster.RemoveTemporaryStats(key);
                owner.Area.Buffs.Clear(id, buff.Row, fighter.Id);
            }
        foreach (var (id, monster) in monsters)
            if (!ReferenceEquals(state.MonstersInTime.GetValueOrDefault(id), monster))
            {
                monster.SetTemporaryStats(key, buff.Stats);
                owner.Area.Buffs.Set(id, new(buff.Row, fighter.Id, Values: buff.Values, U68: Buff.Seconds(buff.Until - now)));
            }
        state.MonstersInTime.Clear();
        foreach (var (id, monster) in monsters) state.MonstersInTime.Add(id, monster);
    }

    static void ClearHarbingerTime(Player owner, GolemState.Golem fighter, ItemHarbingerState state, ushort row)
    {
        foreach (var recipient in state.PlayersInTime.Values) RemoveItemHarbingerBuff(recipient, fighter.Id, row);
        foreach (var (id, monster) in state.MonstersInTime)
        {
            monster.RemoveTemporaryStats($"harbinger-time:{fighter.Id}");
            owner.Area.Buffs.Clear(id, row, fighter.Id);
        }
        state.PlayersInTime.Clear();
        state.MonstersInTime.Clear();
    }

    static void ClearItemHarbingerLocked(Player owner, GolemState.Golem fighter)
    {
        if (!ItemHarbingers.TryGetValue(fighter, out var state)) return;
        foreach (var buff in state.Buffs.Values)
        {
            RemoveItemHarbingerBuff(owner, fighter.Id, buff.Row);
            if (buff.Row is 921 or 922) ClearHarbingerTime(owner, fighter, state, buff.Row);
        }
        ItemHarbingers.Remove(fighter);
    }
}
