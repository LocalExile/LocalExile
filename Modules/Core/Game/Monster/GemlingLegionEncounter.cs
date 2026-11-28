using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act6To8Driver;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 8 — The Grain Gate (<c>2_8_9</c>) Gemling Legion encounter (7× <c>GemlingLegionnaire*</c>):
/// <list type="bullet">
/// <item>Whenever any Legionnaire dies, all surviving Legionnaires cast <c>GemlingRestoreLife[6189]</c> (healing to full
/// life) and escalate their active skill tier (<c>1 → 2 → 3</c> across Fireball <c>[11..13]</c>, Earthquake
/// <c>[5469..5471]</c>, Glacial Cascade <c>[3312..3314]</c>).</item>
/// <item>The <c>GemlingLegionnaireCaptain[4658]</c> rotates all three elemental schools at the current tier and casts
/// <c>TauntTarget[5999]</c> (<c>taunted</c> buff 408).</item>
/// </list>
/// </summary>
public static class GemlingLegionEncounter
{
    public const string Area = "2_8_9";
    public const string Fireball = "Metadata/Monsters/GemlingLegionnaire/GemlingLegionnaireFireball";             // MV 4646
    public const string Earthquake = "Metadata/Monsters/GemlingLegionnaire/GemlingLegionnaireEarthquake";         // MV 4650
    public const string GlacialCascade = "Metadata/Monsters/GemlingLegionnaire/GemlingLegionnaireGlacialCascade"; // MV 4654
    public const string Captain = "Metadata/Monsters/GemlingLegionnaire/GemlingLegionnaireCaptain";               // MV 4658

    static readonly int[] FireballEffects = [11, 12, 13, 6189];
    static readonly int[] EarthquakeEffects = [406, 5469, 5470, 5471, 6189];
    static readonly int[] CascadeEffects = [3312, 3313, 3314, 6189];
    static readonly int[] CaptainEffects = [406, 6189, 5999, 11, 12, 13, 5469, 5470, 5471, 3312, 3313, 3314];

    const ushort TauntedBuffRow = 408; // taunted
    const string TierStatsKey = "gemling_tier_escalation";

    const string QuakeMarkerArt = "Metadata/Effects/Spells/monsters_effects/act8/gemlinglegionnaires/earthquake/markercrystal.ao";
    const string QuakeSpikeArt = "Metadata/Effects/Spells/monsters_effects/act8/gemlinglegionnaires/earthquake/quakespikecrystal_01.ao";
    const string QuakeUpgradedMarkerArt = "Metadata/Effects/Spells/monsters_effects/act8/gemlinglegionnaires/earthquake_upgraded/markercrystal_upgraded.ao";
    const string QuakeUpgradedSpikeArt = "Metadata/Effects/Spells/monsters_effects/act8/gemlinglegionnaires/earthquake_upgraded/quakespikecrystal_01_upgraded.ao";
    const string FireballImpactArt = "Metadata/Effects/Spells/monsters_effects/act6/abberath/combofireballbig.ao";
    const string CascadeImpactArt = "Metadata/Effects/Spells/monsters_effects/act8/dusk/carpetbomb/impact.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();

    public static bool IsLegionnaire(string? id) => id is Fireball or Earthquake or GlacialCascade or Captain;

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            if (!player.Area.Monsters.Values.Any(m => m.Hostile && IsLegionnaire(m.Variety?.Id)))
                return;
            fight = Fights.GetValue(player.Instance, _ => new Fight(player.Area));
        }
        await fight.Gate.WaitAsync();
        try
        {
            if (IsMonsterRunner(player))
                await fight.Step(player, AreaTime(player));
        }
        finally { fight.Gate.Release(); }
    }

    public static async Task OnMonsterKilled(MonsterKill kill)
    {
        var player = kill.Player;
        if (player.Layout.Id != Area || !IsLegionnaire(kill.Monster.VarietyId)) return;
        if (!Fights.TryGetValue(player.Instance, out var fight)) return;
        await fight.Gate.WaitAsync();
        try
        {
            await fight.OnLegionnaireDied(player, kill.Monster.ObjectId, AreaTime(player));
        }
        finally { fight.Gate.Release(); }
    }

    public static Task Leaving(Player player)
    {
        if (player.Layout.Id != Area) return Task.CompletedTask;
        if (!player.Closed) player.Area.Buffs.ClearRow(player.Id, TauntedBuffRow);
        return Task.CompletedTask;
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
        }
    }

    sealed class MemberState
    {
        public bool Bound;
        public int AppliedTier;
        public double NextSkillAt;
        public double NextChaseAt;
        public double NextTauntAt;
        public int CaptainSchool;
    }

    sealed class Fight(GameArea area)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly List<TimedEffect> effects = [];
        readonly List<ScheduledHit> hits = [];
        readonly Dictionary<uint, MemberState> members = [];
        readonly HashSet<uint> countedDead = [];

        bool cleaned, stopped;

        public void StopAndClear()
        {
            stopped = true;
            foreach (uint id in members.Keys)
                if (area.Monsters.TryGetValue(id, out var m))
                {
                    m.RemoveTemporaryStats(TierStatsKey);
                    m.RebuildStats();
                }
        }

        public async Task OnLegionnaireDied(Player runner, uint deadId, double now)
        {
            if (stopped || !countedDead.Add(deadId)) return;
            await TriggerRestoreAndEscalate(runner, now);
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await ResolveHits(runner, hits, effects, now, () => !stopped);

            var all = runner.Area.Monsters.Where(p => p.Value.Hostile && IsLegionnaire(p.Value.Variety?.Id)).ToArray();
            if (all.Length == 0) return;

            // Detect any death missed between ticks (e.g. DoT or portal re-entry)
            bool newDeaths = false;
            foreach (var (id, m) in all)
                if (!Alive(m) && countedDead.Add(id))
                    newDeaths = true;

            var living = all.Where(p => Alive(p.Value)).ToArray();
            if (living.Length == 0)
            {
                if (!cleaned) await Cleanup(runner);
                return;
            }

            if (newDeaths)
                await TriggerRestoreAndEscalate(runner, now);

            int tier = CurrentTier();
            double cooldownScale = tier == 3 ? 0.62 : tier == 2 ? 0.8 : 1.0;

            foreach (var (id, m) in living)
            {
                if (!members.TryGetValue(id, out var st))
                {
                    st = new MemberState { NextSkillAt = now + 2.0 + (id % 4) * 0.6, NextTauntAt = now + 5.0 };
                    members[id] = st;
                }
                if (!st.Bound)
                {
                    st.Bound = true;
                    await BindEffects(runner, id, m, EffectsFor(m.Variety!.Id));
                }
                if (st.AppliedTier != tier)
                    ApplyTierStats(m, st, tier);

                var pos = m.Walk.Position(now);
                var target = NearestLivingTarget(runner, pos, now, maxRange: 115, awakeId: id);
                if (target is null) continue;
                m.Aggro = true;
                m.Target = target;

                if (!CanAct(m, now)) continue;
                await StepMember(runner, id, m, st, target, tier, cooldownScale, now);
            }
        }

        int CurrentTier() => countedDead.Count >= 4 ? 3 : countedDead.Count >= 2 ? 2 : 1;

        static int[] EffectsFor(string id) => id switch
        {
            Fireball => FireballEffects,
            Earthquake => EarthquakeEffects,
            GlacialCascade => CascadeEffects,
            _ => CaptainEffects,
        };

        static void ApplyTierStats(SpawnedMonster m, MemberState st, int tier)
        {
            st.AppliedTier = tier;
            if (tier <= 1)
            {
                m.RemoveTemporaryStats(TierStatsKey);
            }
            else
            {
                int bonus = tier == 3 ? 45 : 22;
                m.SetTemporaryStats(TierStatsKey, new Dictionary<uint, int>
                {
                    [Stat.AttackSpeedPlusPct] = bonus,
                    [Stat.CastSpeedPlusPct] = bonus,
                    [Stat.BaseMovementVelocityPlusPct] = tier == 3 ? 25 : 12,
                });
            }
            m.RebuildStats();
        }

        async Task TriggerRestoreAndEscalate(Player runner, double now)
        {
            int tier = CurrentTier();
            var survivors = runner.Area.Monsters
                .Where(p => p.Value.Hostile && IsLegionnaire(p.Value.Variety?.Id) && Alive(p.Value))
                .ToArray();
            foreach (var (id, m) in survivors)
            {
                if (!members.TryGetValue(id, out var st))
                    members[id] = st = new MemberState { NextSkillAt = now + 2.0, NextTauntAt = now + 5.0 };
                if (!st.Bound)
                {
                    st.Bound = true;
                    await BindEffects(runner, id, m, EffectsFor(m.Variety!.Id));
                }
                ApplyTierStats(m, st, tier);
                var pos = m.Walk.Position(now);
                await CastAction(runner, id, m, 6189, pos, 0.75, now, "[Gemling] Restore Life");
                await HealBoss(runner, id, m, m.MaxLife, "[Gemling] Full heal on fallen comrade");
                await SpawnTimedEffect(runner, effects, pos,
                    tier >= 3 ? QuakeUpgradedMarkerArt : QuakeMarkerArt, now + 1.1, scale: 1.15f);
            }
            Console.WriteLine($"[gateway] Gemling Legion: {countedDead.Count} fallen -> {survivors.Length} survivors healed to full & escalated to Tier {tier}");
        }

        async Task StepMember(Player runner, uint id, SpawnedMonster m, MemberState st, Player target, int tier, double cdScale, double now)
        {
            var pos = m.Walk.Position(now);
            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);
            string kind = m.Variety!.Id;

            // Captain TauntTarget[5999] (ActionType 527 requires targetEntity != null in CanUse;
            // BuffDefinitions[408] taunted is IsRecovery with GrantedStats [taunt_target_id, is_taunted]).
            if (kind == Captain && now >= st.NextTauntAt && dist <= 45)
            {
                st.NextTauntAt = now + 9.0 * cdScale;
                if (await CastAction(runner, id, m, 5999, aim, 0.75, now, "[Gemling Captain] Taunt", targetObjectId: target.Id) is not null)
                {
                    target.Area.Buffs.Set(target.Id, new(TauntedBuffRow, id, Values: [id, 1], U68: Buff.Seconds(3.5)));
                    return;
                }
            }

            if (now >= st.NextSkillAt)
            {
                string school = kind == Captain
                    ? (st.CaptainSchool++ % 3) switch { 0 => Fireball, 1 => Earthquake, _ => GlacialCascade }
                    : kind;

                if (school == Fireball && dist <= 68)
                {
                    st.NextSkillAt = now + 4.2 * cdScale;
                    int eff = 10 + tier; // 11, 12, 13
                    if (await CastAction(runner, id, m, eff, aim, 1.0, now, $"[Gemling] Fireball T{tier}") is { } gen)
                    {
                        int count = tier == 3 ? 5 : tier == 2 ? 3 : 1;
                        double baseAngle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                        var atk = ElementalSpellAttack(m, DamageType.Fire, 0.8 + tier * 0.2, 9.5);
                        for (int i = 0; i < count; i++)
                        {
                            double off = (i - (count - 1) / 2.0) * 0.2;
                            var drop = WalkableNear(runner, (
                                (int)Math.Round(pos.X + Math.Cos(baseAngle + off) * Math.Min(45, dist + 5)),
                                (int)Math.Round(pos.Y + Math.Sin(baseAngle + off) * Math.Min(45, dist + 5))));
                            hits.Add(new(now + 0.55 + Math.Abs(off) * 0.3, id, m, drop, 9 + tier, atk, gen, FireballImpactArt, 0.95f + tier * 0.1f));
                        }
                        return;
                    }
                }
                else if (school == Earthquake && dist <= 34)
                {
                    st.NextSkillAt = now + 5.2 * cdScale;
                    int eff = 5468 + tier; // 5469, 5470, 5471
                    if (await CastAction(runner, id, m, eff, aim, 1.2, now, $"[Gemling] Earthquake T{tier}") is { } gen)
                    {
                        var slamAt = WalkableNear(runner, aim);
                        string marker = tier >= 2 ? QuakeUpgradedMarkerArt : QuakeMarkerArt;
                        string spike = tier >= 2 ? QuakeUpgradedSpikeArt : QuakeSpikeArt;
                        await SpawnTimedEffect(runner, effects, slamAt, marker, now + 1.5, scale: 1.0f + tier * 0.15f);
                        hits.Add(new(now + 0.55, id, m, slamAt, 11 + tier, PhysicalAttack(m, 0.85 + tier * 0.15, 11 + tier), gen));
                        hits.Add(new(now + 1.45, id, m, slamAt, 14 + tier * 2,
                            PhysicalAttack(m, 1.05 + tier * 0.25, 14 + tier * 2), null, spike, 1.0f + tier * 0.15f));
                        return;
                    }
                }
                else if (school == GlacialCascade && dist <= 56)
                {
                    st.NextSkillAt = now + 4.6 * cdScale;
                    int eff = 3311 + tier; // 3312, 3313, 3314
                    if (await CastAction(runner, id, m, eff, aim, 1.1, now, $"[Gemling] Glacial Cascade T{tier}") is { } gen)
                    {
                        double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                        int bursts = 2 + tier;
                        var atk = HybridAttack(m, DamageType.Cold, 0.4, 0.65 + tier * 0.15, 10, spell: true);
                        for (int s = 1; s <= bursts; s++)
                        {
                            var spot = WalkableNear(runner, (
                                (int)Math.Round(pos.X + Math.Cos(angle) * (s * 10)),
                                (int)Math.Round(pos.Y + Math.Sin(angle) * (s * 10))));
                            hits.Add(new(now + 0.4 + s * 0.16, id, m, spot, 9 + tier, atk, gen, CascadeImpactArt, 0.9f + tier * 0.1f));
                        }
                        return;
                    }
                }
            }

            st.NextChaseAt = await StepChaseOrMelee(runner, id, m, target, now, st.NextChaseAt, hits, meleeScale: 0.9 + tier * 0.1);
        }

        async Task Cleanup(Player runner)
        {
            cleaned = true;
            hits.Clear();
            foreach (var p in ActivePlayers(runner))
                if (!p.Closed) p.Area.Buffs.ClearRow(p.Id, TauntedBuffRow);
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Gemling Legion defeated; hazards cleared");
        }
    }
}
