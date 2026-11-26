using GameServer.Core;
using GameServer.Core.Projectiles;

public static partial class Game
{
    sealed record PactCondition(ushort Row, double Seconds, bool Boon, bool CancelOnDeath, (uint Stat, int Value)[] Stats);
    sealed class PactConditionSet(GameArea area)
    {
        internal GameArea Area { get; } = area;
        internal readonly List<PactCondition> Active = [];
    }

    static readonly RuleTable<Player, PactConditionSet> PactConditions = new();
    static PactCondition[] pactAfflictions = [], pactBoons = [];
    static int maximumPactConditions = 5;
    static long pactEmpowermentTime = ProjectileRuntime.Time(15);

    internal static void LoadPactConditionData()
    {
        var afflictions = new List<PactCondition>();
        var boons = new List<PactCondition>();
        var definitions = ClientTables.TableOrEmpty("BuffDefinitions");
        foreach (var template in ClientTables.TableOrEmpty("BuffTemplates"))
        {
            if (template.Text("Id") is not { } id) continue;
            bool boon = id.StartsWith("PactBoon", StringComparison.Ordinal);
            if (!boon && !id.StartsWith("PactAffliction", StringComparison.Ordinal)) continue;
            if (template.IntOrNull("BuffDefinitionsKey") is not int row || definitions.Row(row) is not { } definition) continue;
            double duration = template.Real("__col7");
            var stats = definition.Ints("StatsKeys").Zip(template.Ints("Buff_StatValues"),
                (index, value) => ((uint)index + 1, value)).ToArray();
            if (duration <= 0 || stats.Length == 0) continue;
            (boon ? boons : afflictions).Add(new((ushort)row, duration, boon, definition.BoolOrNull("CancelOnDeath") == true, stats));
        }
        pactAfflictions = [.. afflictions];
        pactBoons = [.. boons];

        foreach (var data in ClientTables.TableOrEmpty("GameConstants"))
        {
            double value = data.Int("Value") / (double)Math.Max(1, data.Int("Divisor"));
            if (value <= 0) continue;
            if (data.Text("Id") == "BaseMaximumActivePactAfflictions") maximumPactConditions = (int)value;
            if (data.Text("Id") == "PactEmpowermentsDefaultDurationMs") pactEmpowermentTime = ProjectileRuntime.Time(value / 1000);
        }
    }

    static bool IsPactSkill(Player player, int effect) => SkillsFor(player).HasType(effect, "Pact");

    static bool SchedulePact(Player player, ushort skill, (int X, int Y)? aim, int castMs, int recovery,
        double? startedAt, Func<bool> current)
    {
        var source = SupportedSkill(player, skill);
        if (source.SourceGem is null) return false;
        using (ProjectileMutation(player))
        {
            var session = ProjectilesFor(player);
            session.Advance();
            double contact = (startedAt ?? ProjectileWorldTime(player)) + castMs / 1000d
                * Animations.SpellRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject);
            session.ScheduleContact(ProjectileRuntime.Time(contact),
                () => current() && PlayerStateAllowsAction(player) && player.ActionInterruptionGeneration == recovery
                    && SameTriggerSource(source, SupportedSkill(player, skill)),
                () =>
                {
                    var stats = CharacterPlayerStats(player);
                    if (SkillsFor(player).EffectId(source.Effect) == PactOfGhorrEffect)
                        StartPactOfGhorr(session, skill, source, aim, stats);
                    GrantPactCondition(session, skill, stats.GetValueOrDefault(Stat.GainBoonsInsteadOfAfflictionsWhenUsingAPactSkill) != 0);
                });
        }
        return true;
    }

    static void GrantPactCondition(ProjectileSession session, ushort skill, bool boon)
    {
        var player = session.Player;
        var choices = boon ? pactBoons : pactAfflictions;
        if (choices.Length == 0) return;

        var area = player.Area;
        var state = PactConditions.GetValue(player, _ => new(area));
        if (!ReferenceEquals(state.Area, area))
        {
            ClearPactConditions(player, state);
            PactConditions.AddOrUpdate(player, state = new(area));
        }

        var available = choices.Where(choice => !state.Active.Any(active => active.Row == choice.Row)).ToArray();
        if (available.Length == 0) return;
        // A fresh receipt lets an evicted condition's deadline coexist with a later roll of its row.
        var chosen = available[AreaRng(player).Next(available.Length)] with { };
        if (state.Active.Count(x => x.Boon == boon) >= maximumPactConditions
            && state.Active.FirstOrDefault(x => x.Boon == boon) is { } oldest)
        {
            state.Active.Remove(oldest);
            area.Buffs.Clear(player.Id, oldest.Row, player.Id);
        }
        state.Active.Add(chosen);
        area.Buffs.Set(player.Id, new(chosen.Row, player.Id, skill, U68: Buff.Seconds(chosen.Seconds)));
        UpdatePactConditionStats(player, state);

        void Expire()
        {
            int index = state.Active.FindIndex(effect => ReferenceEquals(effect, chosen));
            if (index < 0 || !CurrentGameWork) return;
            state.Active.RemoveAt(index);
            area.Buffs.Clear(player.Id, chosen.Row, player.Id);
            UpdatePactConditionStats(player, state);
        }

        if (player.Frames.Running)
        {
            long frames = Math.Max(1, (long)Math.Ceiling(chosen.Seconds * 1000 / Program.FrameMs));
            InArea(player, a => a.Timers.After(a.Frame, frames, _ => Expire(),
                owner: new EntityId(player.Id), source: CombatLifetime));
        }
        else session.ScheduleContact(session.Runtime.Now + ProjectileRuntime.Time(chosen.Seconds), () => true, Expire);
    }

    static void UpdatePactConditionStats(Player player, PactConditionSet state)
    {
        var area = state.Area;
        var entity = new EntityId(player.Id);
        if (state.Active.Count == 0)
        {
            area.RemoveStatBuff(entity, state);
            if (PactConditions.TryGetValue(player, out var current) && ReferenceEquals(current, state)) PactConditions.Remove(player);
            return;
        }
        var stats = new Dictionary<uint, int>();
        foreach (var condition in state.Active)
            foreach (var (stat, value) in condition.Stats)
                stats[stat] = stats.GetValueOrDefault(stat) + value;
        area.SetStatBuff(entity, state, stats);
    }

    static bool CancelPactBoon(Player player, ushort row)
    {
        if (!PactConditions.TryGetValue(player, out var state) || !ReferenceEquals(player.Area, state.Area)
            || state.Active.RemoveAll(active => active.Boon && active.Row == row) == 0) return false;
        state.Area.Buffs.Clear(player.Id, row, player.Id);
        UpdatePactConditionStats(player, state);
        return true;
    }

    static void ClearPactConditions(Player player, PactConditionSet state, bool onlyOnDeath = false)
    {
        int removed = state.Active.RemoveAll(condition =>
        {
            if (onlyOnDeath && !condition.CancelOnDeath) return false;
            state.Area.Buffs.Clear(player.Id, condition.Row, player.Id);
            return true;
        });
        if (removed > 0) UpdatePactConditionStats(player, state);
    }

    static void ClearPactConditionsOnDeath(Player player)
    {
        if (PactConditions.TryGetValue(player, out var state)) ClearPactConditions(player, state, onlyOnDeath: true);
    }

    internal static void RetirePactConditions()
    {
        foreach (var (player, state) in PactConditions)
            state.Area.Defer(_ => ClearPactConditions(player, state));
    }
}
