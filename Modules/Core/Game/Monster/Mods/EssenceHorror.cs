using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Horror's ice-explosion cascade (<c>EssenceDaemonStormCascadeDaemon</c> → <c>StormCascade</c>), Maelström
/// chilled-ground trails (<c>WaterSpiralEssence</c> / <c>EssenceWhiteMonsterCyclone</c>) and periodic charge generation
/// (<c>EssenceHorrorCharges1</c>, BuffTemplates 233: random Endurance/Frenzy/Power charges).</summary>
public static partial class MapEssences
{
    public const string EssenceHorrorChargesMod = "EssenceHorrorCharges1";
    const string StormCascadeIdleArt = "Metadata/Effects/Spells/monsters_effects/coldstorm_call/coldstormcall_idle.ao";
    const string StormCascadeImpactArt = "Metadata/Effects/Spells/monsters_effects/coldstorm_call/coldstormcall_impact.ao";
    const string HorrorMaelstromVariety = "Metadata/Monsters/WaterSpiral/WaterSpiralEssence";
    const int HorrorEssenceType = 22;

    // BuffDefinitions rows verified in client.db: 27 frenzy_charge, 28 endurance_charge, 29 power_charge.
    const ushort FrenzyChargeBuff = 27;
    const ushort EnduranceChargeBuff = 28;
    const ushort PowerChargeBuff = 29;
    const int MaxHorrorCharges = 3;
    const double HorrorChargeInterval = 3.0;
    const double HorrorChargeDuration = 10.0;

    sealed class HorrorChargeState
    {
        public double NextGain;
        public readonly int[] Counts = new int[3]; // 0 = Endurance, 1 = Frenzy, 2 = Power
        public readonly double[] Until = new double[3];
        public readonly ushort[] Buff = new ushort[3];
        public readonly int[] Sent = new int[3];
    }
    sealed class MaelstromTrailState { public double NextTrail; }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpawnedMonster, HorrorChargeState> HorrorCharges = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpawnedMonster, MaelstromTrailState> MaelstromTrails = new();

    static async Task CastStormCascade(Player conn, SpawnedMonster monster, Player target, SpecialSpell spell)
    {
        var catalog = SkillsFor(conn);
        int cascadeEffect = catalog.EffectRow("StormCascade");
        var damage = EssenceSkillDamage(conn, cascadeEffect, monster.Level);
        if (damage.IsZero) return;
        int count = ScaledSpellCount(spell.Pulse, spell.Tier, 9);
        var from = monster.Walk.Position(AreaTime(conn));
        var aim = target.Walk.Position(AreaTime(target));
        double dx = aim.X - from.X, dy = aim.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
        double ux = len < 1 ? 1 : dx / len, uy = len < 1 ? 0 : dy / len;
        var circles = new List<((int X, int Y) Spot, uint Idle)>(count);
        for (int i = 0; i < count && !conn.Closed && !conn.LeavingArea; i++)
        {
            double d = 7 * (i + 1);
            var spot = conn.Layout.Near((from.X + (int)Math.Round(ux * d), from.Y + (int)Math.Round(uy * d)));
            circles.Add((spot, SpawnEffect(conn, spot, StormCascadeIdleArt)));
            if (i + 1 < count) await AreaDelay(conn, 0.12);
        }
        await AreaDelay(conn, 0.35);
        for (int i = 0; i < circles.Count; i++)
        {
            var (spot, idle) = circles[i];
            RemoveEffect(conn, idle);
            if (conn.Closed || conn.LeavingArea) continue;
            uint impact = SpawnEffect(conn, spot, StormCascadeImpactArt);
            _ = RemovePulseLater(conn, impact, 0.9);
            await EssenceSpellHit(conn, monster, cascadeEffect, damage, radius: 9, spot);
            if (i + 1 < circles.Count) await AreaDelay(conn, 0.12);
        }
        Console.WriteLine($"[map-content] essence horror storm cascade: {circles.Count} circles from 0x{monster.ObjectId:x}");
    }

    /// <summary>Applies one Endurance (0), Frenzy (1) or Power (2) charge to <paramref name="monster"/> (capped at 3 each,
    /// refreshing that kind's duration) and updates its <see cref="SpawnedMonster.TemporaryStats"/>.</summary>
    public static void ApplyHorrorCharge(SpawnedMonster monster, int kind, double now)
    {
        if (kind is < 0 or > 2) return;
        var state = HorrorCharges.GetOrCreateValue(monster);
        lock (state)
        {
            ExpireHorrorChargesLocked(state, now);
            state.Counts[kind] = Math.Min(MaxHorrorCharges, state.Counts[kind] + 1);
            state.Until[kind] = now + HorrorChargeDuration;
            SyncHorrorChargeStatsLocked(monster, state);
        }
    }

    static void ExpireHorrorChargesLocked(HorrorChargeState state, double now)
    {
        for (int k = 0; k < 3; k++)
            if (state.Counts[k] > 0 && state.Until[k] <= now)
                state.Counts[k] = 0;
    }

    static void SyncHorrorChargeStatsLocked(SpawnedMonster monster, HorrorChargeState state)
    {
        int end = state.Counts[0], fre = state.Counts[1], pow = state.Counts[2];
        if (end == 0 && fre == 0 && pow == 0)
        {
            monster.RemoveTemporaryStats("EssenceHorrorCharges");
            return;
        }
        var stats = new Dictionary<uint, int>();
        if (end > 0)
        {
            stats[Stat.BaseResistAllElementsPct] = 15 * end;
            stats[Stat.BaseAdditionalPhysicalDamageReductionPct] = 15 * end;
        }
        if (fre > 0)
        {
            stats[Stat.AttackSpeedPlusPct] = 15 * fre;
            stats[Stat.BaseCastSpeedPlusPct] = 15 * fre;
            stats[Stat.BaseMovementVelocityPlusPct] = 5 * fre;
            stats[Stat.ActiveSkillDamagePlusPctFinal] = 4 * fre;
        }
        if (pow > 0) stats[Stat.CriticalStrikeChancePlusPct] = 200 * pow;
        monster.SetTemporaryStats("EssenceHorrorCharges", stats);
    }

    static void TickHorror(Player conn)
    {
        if (conn.Closed || conn.LeavingArea || !Areas.TryGetValue(conn.Instance, out var area) || area.Monoliths is null) return;
        var rares = new HashSet<uint>();
        foreach (var mono in area.Monoliths)
        {
            if (mono.Stage < 4 || mono.Monster == 0) continue;
            foreach (int row in mono.Essences)
                if (MapEssenceCatalog.Find(row)?.Type == HorrorEssenceType) { rares.Add(mono.Monster); break; }
        }
        if (rares.Count == 0) return;
        double now = AreaTime(conn);
        double areaTime = AreaTime(conn);
        List<SpawnedMonster> livingRares = [];
        List<SpawnedMonster> maelstroms = [];
        foreach (uint id in rares)
            if (conn.Area.Monsters.TryGetValue(id, out var rare) && !rare.Dead && rare.Life > 0)
                livingRares.Add(rare);
        foreach (var m in conn.Area.Monsters.Values)
            if (!m.Dead && m.Life > 0 && m.SummonerId is uint s && rares.Contains(s) && m.Variety?.Id == HorrorMaelstromVariety)
                maelstroms.Add(m);
        ReadOnlySpan<ushort> chargeBuffs = [EnduranceChargeBuff, FrenzyChargeBuff, PowerChargeBuff];
        foreach (var rare in livingRares)
        {
            var state = HorrorCharges.GetOrCreateValue(rare);
            List<(byte Kind, ushort Inst, ushort Row, int Count, double Rem)>? updates = null;
            lock (state)
            {
                ExpireHorrorChargesLocked(state, now);
                if (rare.Aggro && now >= state.NextGain)
                {
                    state.NextGain = now + HorrorChargeInterval;
                    int pick = Random.Shared.Next(3);
                    state.Counts[pick] = Math.Min(MaxHorrorCharges, state.Counts[pick] + 1);
                    state.Until[pick] = now + HorrorChargeDuration;
                }
                SyncHorrorChargeStatsLocked(rare, state);
                for (int k = 0; k < 3; k++)
                {
                    if (state.Sent[k] == state.Counts[k]) continue;
                    if (state.Counts[k] > 0 && state.Buff[k] == 0) state.Buff[k] = conn.NextBuff++;
                    (updates ??= []).Add(((byte)k, state.Buff[k], chargeBuffs[k], state.Counts[k], Math.Max(0.5, state.Until[k] - now)));
                    state.Sent[k] = state.Counts[k];
                    if (state.Counts[k] == 0) state.Buff[k] = 0;
                }
            }
            if (updates is not null)
                foreach (var (_, inst, row, count, rem) in updates)
                {
                    if (inst == 0) continue;
                    if (count == 0)
                        conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(rare.ObjectId, inst), null, WorldId: rare.ObjectId));
                    else
                        conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(rare.ObjectId, inst, row,
                            u68: BitConverter.SingleToUInt32Bits((float)rem), stacks: (ushort)count), null, WorldId: rare.ObjectId));
                }
        }
        foreach (var spiral in maelstroms)
        {
            var trail = MaelstromTrails.GetOrCreateValue(spiral);
            if (now < trail.NextTrail) continue;
            trail.NextTrail = now + 0.8;
            AddGroundZone(conn, area, spiral.Walk.Position(areaTime), TrailArt, radius: 10, duration: 3.0,
                DamagePacket.Empty, PlayerElementalAilments.Kind.Chill, 20, scale: 1.15f);
        }
    }
}