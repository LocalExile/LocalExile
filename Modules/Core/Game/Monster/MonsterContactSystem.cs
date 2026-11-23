using System.Runtime.CompilerServices;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

/// <summary>
/// Frame-driven melee contact queue for monsters: replaces per-swing async tasks and 16 ms polling waits
/// (WaitMonsterMeleeContact / WaitMonsterActionTime) with one AreaPhase.Hits pass per frame that commits all due
/// melee contacts under one ProjectileMutation lock and one SendWorldBatch, and coalesces PresentMonsterHit.
/// </summary>
public static partial class Game
{
    sealed class PendingMonsterContact(Player player, MonsterAiCadence? cadence, uint id, SpawnedMonster monster,
        int action, ushort sequence, MonsterAttack attack, bool useMeleeClock, double contactAt)
    {
        public readonly Player Player = player;
        public readonly MonsterAiCadence? Cadence = cadence;
        public readonly uint Id = id;
        public readonly SpawnedMonster Monster = monster;
        public readonly int Action = action;
        public readonly ushort Sequence = sequence;
        public readonly MonsterAttack Attack = attack;
        public readonly bool UseMeleeClock = useMeleeClock;
        public readonly double ContactAt = contactAt;
    }

    sealed class AreaContactQueue
    {
        public readonly List<PendingMonsterContact> Pending = [];
        public readonly Dictionary<Player, List<PendingMonsterContact>> DueByPlayer = [];
    }

    static readonly ConditionalWeakTable<GameArea, AreaContactQueue> AreaContacts = new();

    static void QueueMonsterMeleeContact(Player player, MonsterAiCadence? cadence, uint id, SpawnedMonster m,
        int action, ushort sequence, MonsterAttack a, bool useMeleeClock, double contactAt)
    {
        var queue = AreaContacts.GetValue(player.Area, _ => new());
        queue.Pending.Add(new(player, cadence, id, m, action, sequence, a, useMeleeClock, contactAt));
    }

    internal static void StartMonsterContacts(GameArea area, Player player, object source)
    {
        var queue = AreaContacts.GetValue(area, _ => new());
        area.Systems.Add("monster contacts", AreaPhase.Hits, a =>
        {
            var pending = queue.Pending;
            if (pending.Count == 0) return;
            var dueByPlayer = queue.DueByPlayer;
            dueByPlayer.Clear();
            int write = 0;
            for (int i = 0; i < pending.Count; i++)
            {
                var c = pending[i];
                var p = c.Player;
                if (!MonsterActionCurrent(p, c.Id, c.Monster, c.Action))
                {
                    c.Cadence?.CompleteSwing(c.Monster, c.Action);
                    continue;
                }
                if (p.Paused)
                {
                    pending[write++] = c;
                    continue;
                }
                bool due;
                if (c.UseMeleeClock)
                {
                    if (c.Monster.MeleeClock is not { } clock || clock.Generation != c.Action)
                    {
                        c.Cadence?.CompleteSwing(c.Monster, c.Action);
                        continue;
                    }
                    due = clock.Clock.Elapsed(AreaTime(p)) >= clock.Contact;
                }
                else
                {
                    due = AreaTime(p) >= c.ContactAt;
                }
                if (!due)
                {
                    pending[write++] = c;
                    continue;
                }
                if (!dueByPlayer.TryGetValue(p, out var list))
                    dueByPlayer[p] = list = [];
                list.Add(c);
            }
            if (write < pending.Count) pending.RemoveRange(write, pending.Count - write);
            if (dueByPlayer.Count == 0) return;
            foreach (var (p, list) in dueByPlayer)
            {
                if (p.PlayerUpdatesState is PlayerUpdatesState updates && PlayerUpdatesCurrent(p, updates))
                {
                    var batch = list;
                    _ = AsPlayer(p, updates, () => CommitBatchedMonsterContacts(p, batch));
                }
                else
                {
                    for (int i = 0; i < list.Count; i++)
                        list[i].Cadence?.CompleteSwing(list[i].Monster, list[i].Action);
                }
            }
            dueByPlayer.Clear();
        }, source: source);
    }

    static async Task CommitBatchedMonsterContacts(Player player, List<PendingMonsterContact> due)
    {
        List<(uint Id, MonsterHitOutcome Hit)>? outcomes = null;
        try
        {
            var targets = new WorldObject[due.Count];
            for (int i = 0; i < due.Count; i++) targets[i] = player.Connection.WorldOutput.PublishedObject(due[i].Id);
            while (CurrentGameWork && !player.Closed && !player.LeavingArea && player.PlayerLife > 0)
            {
                bool checkedAtGate = false;
                await SendWorldBatch(player, targets, _ =>
                {
                    using (ProjectileMutation(player, phase: "send"))
                    {
                        if (player.Paused) return null;
                        checkedAtGate = true;
                        if (InGracePeriod(player)) return null;
                        double now = Math.Max(ProjectileWorldTime(player), AreaTime(player));
                        var here = player.Walk.Position(now);
                        List<OutputRecord>? records = null;
                        for (int i = 0; i < due.Count; i++)
                        {
                            var c = due[i];
                            if (!MonsterActionCurrent(player, c.Id, c.Monster, c.Action)) continue;
                            if (MonsterActionFactor(c.Monster, now) == 0)
                            {
                                if (ServerTrace.Combat)
                                    Console.WriteLine($"[gateway] {player.Connection.Remote} monster 0x{c.Id:x} ({c.Monster.Label}) frozen: its swing is cancelled");
                                continue;
                            }
                            var outcome = CommitMonsterHitLocked(player, c.Id, c.Monster, c.Attack, now, here,
                                c.Monster.Walk.Position(now), checkRange: true, sequence: c.Sequence);
                            if (outcome is null) continue;
                            (outcomes ??= []).Add((c.Id, outcome));
                            string? log = ServerTrace.Combat
                                ? $"0x01af monster 0x{c.Id:x} hit on the player (kind 3, sequence 0x{c.Sequence:x4})" : null;
                            (records ??= new List<OutputRecord>(due.Count)).Add(PresentedRecord(targets[i], outcome.HitPacket, log));
                            if (outcome.Life == 0) break;
                        }
                        return records is { Count: > 0 } ? new OutputBatch(records.ToArray()) : null;
                    }
                });
                if (checkedAtGate) break;
                await AreaWait(PlayerUpdateRole.MonsterAction, 16);
            }
        }
        finally
        {
            for (int i = 0; i < due.Count; i++)
                due[i].Cadence?.CompleteSwing(due[i].Monster, due[i].Action);
        }
        if (outcomes is { Count: > 0 })
            await PresentMonsterHitsBatch(player, outcomes);
    }

    static async Task PresentMonsterHitsBatch(Player player, List<(uint Id, MonsterHitOutcome Hit)> outcomes)
    {
        if (!CurrentGameWork || outcomes.Count == 0) return;
        await SendPlayerEnergyShield(player);
        if (!CurrentGameWork) return;
        bool anySpirit = false, died = false;
        PlayerRecoveryIntent? lastRecovery = null;
        uint lastLife = player.PlayerLife;
        uint firstManaBefore = outcomes[0].Hit.ManaBefore;
        uint lastMana = outcomes[^1].Hit.Mana;
        for (int i = 0; i < outcomes.Count; i++)
        {
            var (id, hit) = outcomes[i];
            if (hit.Spirit) anySpirit = true;
            if (hit.Recovery is not null) lastRecovery = hit.Recovery;
            lastLife = hit.Life;
            if (hit.Life == 0) died = true;
            if (ServerTrace.Combat || hit.Life == 0)
                Note($"0x01b1 player hit by monster 0x{id:x} for {hit.Damage}{(hit.Critical ? " (critical)" : "")}" +
                    $"{(hit.RawDamage != hit.Damage ? $" ({hit.RawDamage} physical before {DamageModel.ArmourReduction(hit.Armour.EffectiveArmour, hit.RawDamage)}% armour, effective rating {hit.Armour.EffectiveArmour})" : "")}" +
                    $"{(hit.Blocked ? " (blocked)" : "")}, life {hit.Life}/{player.PlayerMaxLife}{(hit.Life == 0 ? " (dies)" : "")}");
        }
        if (anySpirit) { await SendSpiritChieftain(player); if (!CurrentGameWork) return; }
        if (lastRecovery is not null) { await SendPlayerHitRecovery(player, lastRecovery); if (!CurrentGameWork) return; }
        if (died) { SetStats(player, player.Id, [(Stat.HideMiniLifeBar, 1)]); Note("0x01b3 player hide_mini_life_bar = 1"); }
        if (lastMana != firstManaBefore)
        {
            await SendPlayerMana(player, $"0x01b1 player mana {lastMana} ({(long)lastMana - firstManaBefore:+0;-0})");
            if (!CurrentGameWork) return;
        }
        if (died || lastLife == 0)
        {
            await PlayerDied(player, $"0x0055 death screen ({player.Character.League ?? "?"} league)");
            return;
        }
        if (player.Pools.AttackerSlowPct != 0 || player.Area.SlowedAttackers.Count > 0)
            for (int i = 0; i < outcomes.Count; i++)
                if (!outcomes[i].Hit.Blocked) await SendAttackerSlows(player, outcomes[i].Id);
        int hitCount = outcomes.Count;
        await GainFlaskCharges(player, (_, local) => local.GetValueOrDefault(Stat.LocalFlaskGainXChargesWhenHit) * hitCount, "hit");
        for (int i = 0; i < outcomes.Count; i++)
            if (outcomes[i].Hit.Debuffs is { } debuffs)
                foreach (var spec in debuffs.Specs) await AddPlayerDebuff(player, spec.Row, debuffs.Caster, spec.Stats, spec.Seconds);
    }
}
