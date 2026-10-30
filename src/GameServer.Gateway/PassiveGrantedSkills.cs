using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

/// <summary>
/// Skills a passive grants or triggers (Agents/docs/protocol/11-skills.md §12, "Passive-granted skills"): PassiveSkills.dat
/// <c>GrantedEffectsPerLevel</c> (<c>GrantedEffectsPerLevelHardmode</c> for a Ruthless alternate) is a GrantedEffectsPerLevel
/// row, which gives the GrantedEffects row and its level. Only the effects in <see cref="Supported"/> act, because their
/// trigger runs here; a passive's other granted skills do nothing yet. <c>RepeatingShockwave</c> (Abberath's Fury,
/// Polytheist's Devout of Abberath at level 20) is a spell triggered by each of the player's footsteps while running.
/// </summary>
static class PassiveGrantedSkills
{
    public enum Trigger { Footstep }

    /// <summary>A granted skill: its GrantedEffects row and Id, its level and what triggers it.</summary>
    public sealed record Granted(int Effect, string Id, int Level, Trigger Trigger);

    // The effects the server triggers: their trigger has a runtime (RunFootstepSkills).
    static readonly Dictionary<string, Trigger> Supported = new() { ["RepeatingShockwave"] = Trigger.Footstep };

    static readonly Lazy<Dictionary<ushort, Granted>> Normal = new(() => Load("data", ruthless: false));
    static readonly Lazy<Dictionary<ushort, Granted>> Ruthless = new(() => Load("data", ruthless: true));

    /// <summary>The supported skills these nodes (PassiveSkillGraphIds) grant, once each however many nodes grant one.</summary>
    public static IReadOnlyList<Granted> For(IEnumerable<ushort> nodes, bool ruthless) =>
        [.. nodes.Select(n => (ruthless ? Ruthless : Normal).Value.GetValueOrDefault(n)).OfType<Granted>().Distinct()];

    static Dictionary<ushort, Granted> Load(string folder, bool ruthless)
    {
        var result = new Dictionary<ushort, Granted>();
        string path = Path.Combine(folder, "tables", "PassiveSkills.json");
        if (!File.Exists(path)) return result;
        using var passives = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var row in passives.RootElement.EnumerateArray())
        {
            if (row.GetProperty("PassiveSkillGraphId").GetInt32() is not (> 0 and <= ushort.MaxValue and var node)) continue;
            // As PassiveTree reads the stats: a Ruthless catalog takes the alternate columns only for a marked row.
            bool alternate = ruthless && row.TryGetProperty("HasHardmodeAlternate", out var marked) && marked.GetBoolean();
            if (row.TryGetProperty(alternate ? "GrantedEffectsPerLevelHardmode" : "GrantedEffectsPerLevel", out var granted)
                && granted.ValueKind == JsonValueKind.Number && Program.Skills.PerLevelRow(granted.GetInt32()) is (int effect, int level)
                && Program.Skills.EffectId(effect) is { } id && Supported.TryGetValue(id, out var trigger))
                result[(ushort)node] = new(effect, id, level, trigger);
        }
        return result;
    }

    // The Templar's run animations (Art/Models/CHARACTERS/Templar/rig.amd, all 18 run_* clips): an 800 ms loop with
    // footstepright at 233 ms and footstepleft at 633 ms.
    // shortcut: the Templar's timing stands for every class, because only the Templar has a passive that grants a footstep
    // skill (the other rigs have 800-833 ms loops with other offsets); read the class rig's run_* footstep events
    // (scripts/re/anim_contacts.py) when another class gets one.
    const double RunLoopSeconds = .8;
    static readonly double[] FootstepAt = [.233, .633];

    /// <summary>The footsteps the run animation passes from <paramref name="from"/> to <paramref name="to"/> (its own clock:
    /// seconds since the run began at the animation's base rate): every footstep event in (from, to].</summary>
    public static int Footsteps(double from, double to) => to <= from ? 0
        : FootstepAt.Sum(at => (int)(Math.Floor((to - at) / RunLoopSeconds) - Math.Floor((from - at) / RunLoopSeconds)));

    /// <summary>
    /// Abberath's Fury's base radius in cells. Its stats have no <c>active_skill_base_area_of_effect_radius</c>, and
    /// neither PoB nor poedb give one: the real radius is the game server's own. This is its explosion art's:
    /// MiscAnimated AbberathsHoovesImpact's mesh (Art/Microtransactions/Aura/Uniques/Disjunction/disjunction.sm, BoundingBox
    /// ±73.553 world units, 6.8 cells at 250/23 units per cell). Unverified against the real game.
    /// </summary>
    public const int FootstepExplosionRadius = 7;

    /// <summary>The explosion art: SkillArtVariations <c>repeating_shockwave</c>'s default (MiscAnimated 1173
    /// AbberathsHoovesImpact), which is not scaled to an area (MiscAnimated <c>__col4</c> 0).</summary>
    public const string ExplosionArtVariation = "repeating_shockwave";

    /// <summary>How long the explosion art plays: its one clip (disjunction.amd "animate", once, 666 ms).</summary>
    public const int ExplosionArtMs = 700;
}

partial class Program
{
    // The footstep trigger's poll: the run animation's clock and each footstep are followed to within this.
    const int FootstepPollMs = 50;

    // The player's footstep skills (Abberath's Fury), for the connection's life: every poll moves the run animation's clock
    // and triggers each skill once per footstep it passed. Not while paused: the game clock stands still then.
    static async Task RunFootstepSkills(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        double run = 0, last = conn.Clock.Elapsed.TotalSeconds;
        try
        {
            while (!conn.Closed)
            {
                await Task.Delay(FootstepPollMs);
                double now = conn.Clock.Elapsed.TotalSeconds;
                if (conn.Closed || conn.Paused || now <= last) continue;
                int steps = FootstepsDue(conn, ref run, last, now);
                last = now;
                if (steps == 0) continue;
                foreach (var skill in FootstepSkills(conn, now))
                    for (int i = 0; i < steps; i++) await TriggerFootstepSkill(remote, stream, ciphers, conn, skill);
            }
        }
        catch (Exception ex)
        {
            if (!conn.Closed) Console.WriteLine($"[gateway] {remote} footstep skills: {ex.Message}");
        }
    }

    // One poll of the run animation's clock, from → now, and the footsteps it passed. The clock runs while the player walks
    // (until Walk.ArrivesAt; a movement skill's blink is no walk) at the walk's speed over the base speed, so movement speed
    // and chill change the step rate (the wiki: movement speed raises the frequency). It starts over when the walk ends, as
    // the run animation does.
    internal static int FootstepsDue(ClientState conn, ref double run, double from, double now)
    {
        double before = run, until = Math.Min(now, conn.Walk.ArrivesAt);
        if (until > from) run += (until - from) * conn.Walk.Speed / WalkSpeed;
        int steps = PassiveGrantedSkills.Footsteps(before, run);
        if (now >= conn.Walk.ArrivesAt) run = 0;
        return steps;
    }

    // The footstep skills that trigger now: none while the player is dead, leaving, frozen or stunned, in town or in the
    // grace period, nor a disabled one (Devout of Gruthkul's "Your Spells are disabled": the wiki says it disables Abberath's
    // Fury).
    internal static IReadOnlyList<PassiveGrantedSkills.Granted> FootstepSkills(ClientState conn, double now)
    {
        List<ushort> nodes;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || PlayerActionBlocked(conn, now, now)) return [];
            nodes = [.. conn.Passives, .. conn.MainAscendancy.Allocated];
        }
        var skills = PassiveGrantedSkills.For(nodes, conn.Ruthless).Where(s => s.Trigger == PassiveGrantedSkills.Trigger.Footstep).ToList();
        if (skills.Count == 0 || InTown(conn) || InGracePeriod(conn)) return [];
        return [.. skills.Where(s => ActorSkillDisabledCore(conn, s.Effect, null) is null)];
    }

    // A granted spell's hit (no gem, no supports): its base damage with the player's increases, added damage and conversion,
    // as a supported spell's (SupportedHitFor); Spell Skills deal no Damage zeroes it (SkillDamageContext).
    internal static DamagePacket GrantedSpellHit(ClientState conn, int effect, int level)
    {
        var caster = FinalPlayerStats(conn).ToDictionary(v => v.Item1, v => v.Item2);
        var stats = MergeSkillStats(caster, SkillsFor(conn).EffectStats(effect, level).ToDictionary(v => v.Item1, v => v.Item2));
        int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        var plan = SkillHitPlan.ForSkill(SkillsFor(conn), effect, level);
        if (!plan.CanHit) return DamagePacket.Empty;
        var hit = SpellDamage.Packet(plan.SpellBase, Get, AddedEffectiveness(conn, effect, level))
            .Convert(DamageStats.Conversion(stats, Stats, attack: false));
        return new SkillDamageContext(plan.Kind, caster).Hit(hit);
    }

    // One footstep's skill (Abberath's Fury): its explosion art where the player stands, and a spell hit on every living
    // hostile monster within its radius (FootstepExplosionRadius grown by area of effect, SkillArea) of the player, as an
    // area spell's sweep (AreaSkillHit) without a gem: no cost, no cast time, crits and rolls per monster, every hit before
    // the sweep's deaths. Its 25% chance to ignite does nothing: no player hit ignites monsters yet.
    internal static async Task TriggerFootstepSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        PassiveGrantedSkills.Granted skill)
    {
        var own = SkillsFor(conn).EffectStats(skill.Effect, skill.Level).ToDictionary(v => v.Item1, v => v.Item2);
        var area = MergeSkillStats(AreaPlayerStats(conn), own);
        int radius = SkillArea.Radius(PassiveGrantedSkills.FootstepExplosionRadius,
            name => Stats.Key(name) is uint k ? area.GetValueOrDefault(k) : 0, spell: true);
        string? art = Art.Animated(PassiveGrantedSkills.ExplosionArtVariation, 0);
        (int X, int Y) at;
        uint? effectObject = null;
        List<(uint Id, SpawnedMonster M)> targets;
        using (ProjectileMutation(conn))
        {
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || conn.Paused) return;
            double now = ProjectileWorldTime(conn);
            at = conn.Walk.Position(now);
            targets = [.. conn.Monsters.Where(e => e.Value is { Hostile: true, Dead: false, Life: > 0 }
                && Dist(e.Value.Walk.Position(now), at) <= radius).Select(e => (e.Key, e.Value))];
            if (art is not null) effectObject = conn.NextObjectId++;
        }
        if (effectObject is uint obj)
        {
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(obj, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(at.X, at.Y, art!).Blob()), null);
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(obj), null);
            _ = RemoveAfter(remote, stream, ciphers, conn, obj, PassiveGrantedSkills.ExplosionArtMs);
        }
        if (targets.Count == 0) return;

        var player = FinalPlayerStats(conn).ToDictionary(v => v.Item1, v => v.Item2);
        int Player(string name) => Stats.Key(name) is uint k ? player.GetValueOrDefault(k) : 0;
        int Own(string name) => Stats.Key(name) is uint k ? own.GetValueOrDefault(k) : 0;
        var hit = GrantedSpellHit(conn, skill.Effect, skill.Level);
        var pen = Penetration.Of(player, Stats, attack: false, own);
        var mods = HitResistMods(player, own);
        int baseChance = (SkillsFor(conn).SpellCritChance(skill.Effect, skill.Level) ?? 0) + Own("additional_base_critical_strike_chance");
        int chance = SpellCriticalStrike.Chance(baseChance, Player, Own, conn.Pools.SpellCriticalStrikeChancePct);
        int? vsBlinded = Player(VsBlindedCriticalStat) is not 0 and var extra
            ? SpellCriticalStrike.Chance(baseChance, Player, Own, conn.Pools.SpellCriticalStrikeChancePct + (long)extra) : null;
        int multiplier = (int)Math.Clamp((long)conn.Pools.SpellCriticalStrikeMultiplier + Own("base_critical_strike_multiplier_+"),
            int.MinValue, int.MaxValue);
        var gambler = GamblerFor(player);
        bool rageGranted = false;
        var deaths = new List<(uint Id, SpawnedMonster M)>();
        foreach (var (id, m) in targets)
        {
            MonsterHit? applied;
            int damage;
            bool critical;
            using (ProjectileMutation(conn))
            {
                if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0 || conn.Paused) return;
                if (m.Dead || m.Life == 0 || MonsterBlocks(m, Random.Shared, spell: true)) continue;
                var rolled = (gambler with { DamageMorePct = 0 }).RollOutgoingHit(hit, Random.Shared);
                critical = RollCriticalStrike(conn, CriticalChanceOn(conn, id, chance, vsBlinded), own, attack: false);
                (damage, _) = MonsterDamage(m, rolled, critical, attack: false, mods, pen, gambler.InvertsResistance(Random.Shared));
                if (critical) damage = DamageModel.CriticalDamage(damage, CriticalMultiplierOn(m, multiplier));
                applied = ApplyMonsterHitLocked(conn, m, damage);
                if (applied is { StageStarts: true } staged)
                {
                    CommitBossStage(remote, stream, ciphers, conn, id, m, ProjectileWorldTime(conn));
                    applied = staged with { StageStarts = false };
                }
                if (applied is not null && !rageGranted) rageGranted = RageOnHitLocked(conn, attack: false, melee: false, critical, own);
            }
            if (applied is not { } landed) continue;
            if (critical) _ = GainFlaskChargesOnCrit(remote, stream, ciphers, conn);
            await NotifyMonsterHit(remote, stream, ciphers, conn, id, m, landed, damage,
                $"{skill.Id} (footstep){(critical ? " (critical)" : "")}", deaths: deaths);
            ChargesOnHit(conn, m, critical, spell: true, skill.Effect, skill.Level, primary: id == targets[0].Id);
        }
        foreach (var (deadId, dead) in deaths) await MonsterKilled(remote, stream, ciphers, conn, deadId, dead);
    }

    // An effect object's art goes after it has played; nothing to remove once the connection is gone (a new area is a new
    // connection with its own objects).
    static async Task RemoveAfter(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint obj, int ms)
    {
        try
        {
            await Task.Delay(ms);
            if (!conn.Closed && !conn.LeavingArea) await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(obj), null);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}
