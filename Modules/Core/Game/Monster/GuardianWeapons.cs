using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string GuardianWeaponEffect = "AnimateGuardiansWeapon";
    const string GuardianKillGrant = "AnimateGuardianWeaponOnGuardianKillUnique__1_";
    const string WeaponKillGrant = "AnimateGuardianWeaponOnAnimatedWeaponKillUnique__1";

    sealed record GuardianWeaponSummon(ushort Source, WornGrantedSkills.Grant Grant,
        GolemState.Golem Guardian, Item Weapon, (int X, int Y) Position, int Epoch);
    static readonly RuleTable<Player, Queue<GuardianWeaponSummon>> PendingGuardianWeapons = new();

    static void QueueGuardianWeaponOnKillLocked(Player player, GolemState.Golem killer, (int X, int Y) position, double now)
    {
        var kind = killer.Definition.Kind;
        if (kind is not ("AnimatedGuardian" or "AnimatedWeapon" or "VaalAnimatedWeapon")
            || !GolemCurrent(player, killer) || killer.Monster.Dead
            || PlayerStat(player, Stat.AnimateGuardianAndWeaponTrackOnKill) == 0) return;
        var guardian = kind == "AnimatedGuardian" ? killer : player.Summons.Fighters.Values.FirstOrDefault(fighter =>
            fighter.Definition.Kind == "AnimatedGuardian" && !fighter.Monster.Dead && fighter.Monster.Life > 0);
        if (guardian?.Equipment.GetValueOrDefault(PlayerInventories.Weapon1) is not { } weapon) return;

        bool guardianKill = kind == "AnimatedGuardian";
        foreach (var skill in GemSkillEffects(player))
        {
            if (SkillsFor(player).EffectId(skill.Effect) != GuardianWeaponEffect) continue;
            var gem = SupportedSkill(player, skill.Id);
            if (gem.WornGrant is not { } grant || grant.ModId != (guardianKill ? GuardianKillGrant : WeaponKillGrant)
                || !gem.RequirementsMet || GemSkillInstanceDisabled(player, gem.Effect, skill.Id, triggered: true) is not null
                || CooldownForSkill(player, skill.Id) is not { } cooldown) continue;
            int key = CooldownKey(player, gem.Effect, skill.Id);
            var state = CooldownStateLocked(player, key, cooldown, now);
            if (!state.Ready) return;
            int chance = GolemStat(gem.Stats, guardianKill ? Stat.ChanceToTriggerOnAnimateGuardianKillPct : Stat.ChanceToTriggerOnAnimateWeaponKillPct);
            if (!RandomRolls.Chance(chance, 100, AreaRng(player)) || !state.TryUse(now, cooldown)) return;
            player.SkillReadyAt[key] = now + state.RemainingSeconds;
            state.ConfirmNativeUse(state.UseVersion, skill.Id, synchronizeProgress: true);
            PendingGuardianWeapons.GetOrCreateValue(player).Enqueue(new(skill.Id, grant, guardian, weapon, position, player.ProjectileEpoch));
            return;
        }
    }

    static async Task GuardianWeaponsTick(Player player)
    {
        if (!PendingGuardianWeapons.TryGetValue(player, out var queue)) return;
        GuardianWeaponSummon[] pending;
        using (ProjectileMutation(player, advance: false))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused || queue.Count == 0) return;
            pending = [.. queue];
            queue.Clear();
        }
        foreach (var request in pending)
        {
            bool Current() => CurrentGameWork && player.ProjectileActive(request.Epoch)
                && GolemCurrent(player, request.Guardian) && !request.Guardian.Monster.Dead
                && ReferenceEquals(request.Guardian.Equipment.GetValueOrDefault(PlayerInventories.Weapon1), request.Weapon)
                && SupportedSkill(player, request.Source) is { RequirementsMet: true, WornGrant: { } grant } && grant == request.Grant;
            if (!Current() || SummonData.Find(GuardianWeaponEffect) is not { } definition) continue;
            await SummonFighterCore(player, request.Source, definition, request.Position, current: Current, triggered: true,
                preparedEquipment: new(0, null, new() { [PlayerInventories.Weapon1] = request.Weapon }));
        }
    }
}
