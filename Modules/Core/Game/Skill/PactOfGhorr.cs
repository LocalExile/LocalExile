using GameServer.Core.Projectiles;

public static partial class Game
{
    const string PactOfGhorrEffect = "PactOfGhorr";
    const string FleshrendEffect = "TriggeredBloodrend";
    const ushort HungerOfGhorrRow = 2761;

    internal sealed record GhorrHunger(ushort Skill, SupportedGem Source, (int X, int Y)? Aim,
        long Ends, long TriggerInterval);

    public sealed partial class ProjectileSession
    {
        internal GhorrHunger? HungerOfGhorr;
        internal ushort GhorrSequence;
    }

    static bool SchedulePactOfGhorr(Player player, ushort id, (int X, int Y)? aim,
        int castMs, int recovery, double? startedAt, Func<bool> current)
    {
        var source = SupportedSkill(player, id);
        if (source.SourceGem is null) return false;
        int Own(uint stat) => source.Stats.GetValueOrDefault(stat);
        var stats = CharacterPlayerStats(player);
        double seconds = SkillDurationModifiers.Seconds(Own(Stat.SkillGrantsBloodriteForBaseXMsOnUse),
            stat => stats.GetValueOrDefault(stat), Own);
        int frequency = Own(Stat.BloodriteBaseTriggerFrequencyMs);
        if (seconds <= 0 || frequency <= 0) return false;

        using (ProjectileMutation(player))
        {
            var session = ProjectilesFor(player);
            session.Advance();
            double contact = (startedAt ?? ProjectileWorldTime(player)) + castMs / 1000d
                * Animations.SpellRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject);
            session.ScheduleContact(ProjectileRuntime.Time(contact),
                () => current() && PlayerStateAllowsAction(player) && player.ActionInterruptionGeneration == recovery
                    && SameTriggerSource(source, SupportedSkill(player, id)),
                () =>
                {
                    long now = session.Runtime.Now;
                    var hunger = new GhorrHunger(id, source, aim, now + ProjectileRuntime.Time(seconds),
                        ProjectileRuntime.Time(frequency / 1000d));
                    session.HungerOfGhorr = hunger;
                    player.Area.Buffs.Set(player.Id, new(HungerOfGhorrRow, player.Id, id, U68: Buff.Seconds(seconds), Stacks: 0));
                    ScheduleGhorrTrigger(session, hunger, now + hunger.TriggerInterval);
                });
        }
        return true;
    }

    static void ScheduleGhorrTrigger(ProjectileSession session, GhorrHunger hunger, long trigger)
    {
        var player = session.Player;
        session.ScheduleContact(Math.Min(trigger, hunger.Ends), () => ReferenceEquals(session.HungerOfGhorr, hunger), () =>
        {
            if (player.PlayerLife == 0 || !SameTriggerSource(hunger.Source, SupportedSkill(player, hunger.Skill))
                || GemSkillInstanceDisabled(player, hunger.Source.Effect, hunger.Skill) is not null)
            {
                ClearGhorrHunger(player);
                return;
            }
            if (trigger <= hunger.Ends)
            {
                var source = PlayerInventories.GemIdentity(hunger.Source.SourceGem!);
                foreach (var child in GemSkillEffects(player))
                {
                    if (SkillsFor(player).EffectId(child.Effect) != FleshrendEffect
                        || SupportedSkill(player, child.Id).SourceGem is not { } gem
                        || !ReferenceEquals(source, PlayerInventories.GemIdentity(gem))
                        || GemSkillInstanceDisabled(player, child.Effect, child.Id, triggered: true) is not null) continue;
                    ReleaseFleshrend(session, child.Id, child.Effect, hunger.Aim);
                    break;
                }
            }
            if (session.Runtime.Now >= hunger.Ends)
                ClearGhorrHunger(player);
            else
                ScheduleGhorrTrigger(session, hunger, trigger + hunger.TriggerInterval);
        });
    }

    static void ClearGhorrHunger(Player player)
    {
        if (player.ProjectileRuntime is not ProjectileSession { HungerOfGhorr: not null } session) return;
        session.HungerOfGhorr = null;
        player.Area.Buffs.Clear(player.Id, HungerOfGhorrRow, player.Id);
    }
}
