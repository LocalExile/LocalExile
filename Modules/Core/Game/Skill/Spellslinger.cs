using GameServer.Core.World;

public static partial class Game
{
    const string SpellslingerEffect = "Spellslinger", SpellslingerSupport = "SupportSpellslinger";

    static GemSupports.Applied? FindSpellslingerSupport(SupportedGem gem) =>
        gem.Stats.GetValueOrDefault(Stat.SpellslingerTriggerOnWandAttackPct) == 0 ? null
            : gem.Supports.FirstOrDefault(static support => support.Effect.Id == SpellslingerSupport);

    static IEnumerable<(ushort Skill, SupportedGem Gem, GemSupports.Applied Support)> SpellslingerLinks(Player player, ushort controller)
    {
        if (CurrentPlayerSkillSourceLocked(player, controller) is not { Active: true, SourceGem: not null } owner
            || SkillsFor(player).EffectId(owner.Effect) != SpellslingerEffect) yield break;
        foreach (var child in PlayerSkillSourcesLocked(player))
        {
            if (!child.Active || child.SourceGem is null || child.Id == controller || child.Identity.Host != owner.Identity.Host) continue;
            var spell = SupportedSkill(player, child.Id);
            if (spell.RequirementsMet && FindSpellslingerSupport(spell) is { } support && support.Gem.Socket == owner.Identity.Socket
                && spell.Supports.Count(static s => s.Effect.Added.Contains(36)) == 1)
                yield return (child.Id, spell, support);
        }
    }

    static SkillCatalog.ReservationCost? SpellslingerReservation(Player player, ushort controller)
    {
        var skills = SkillsFor(player);
        var sources = StatSourceValues(player);
        var caster = SupportedSkill(player, controller);
        bool controllerLife = ReservesLifeInstead(player, caster, curse: false,
            id => caster.Stats.GetValueOrDefault(id) + sources.GetValueOrDefault(id));
        long mana = 0, life = 0;
        bool linked = false;
        foreach (var (skill, gem, support) in SpellslingerLinks(player, controller))
        {
            linked = true;
            if (skills.Reservation(support.Gem.Effect, support.Gem.Level) is not { } price
                || gem.Stats.GetValueOrDefault(Stat.BaseSkillNoReservation) != 0) continue;
            var context = ReservationContext(player, skill, sources);
            bool reserveLife = controllerLife || context.Life;
            int Get(uint id) => gem.Stats.GetValueOrDefault(id) + sources.GetValueOrDefault(id);
            if (reserveLife && (Get(Stat.BaseSkillNoLifeReservation) != 0 || Get(Stat.SkillNoLifeReservation) != 0)) continue;
            var factor = gem.Supports.Aggregate(1m, static (cost, modifier) => cost * modifier.CostMultiplier / 100m);
            long efficiency = (reserveLife ? player.Pools.LifeReservationEfficiencyPct : player.Pools.ManaReservationEfficiencyPct)
                + (long)context.Efficiency;
            int percent = (int)Math.Clamp(decimal.Round(decimal.Floor(price.ManaPercent * factor) * 100m
                / Math.Max(1, 100 + efficiency), 0, MidpointRounding.AwayFromZero), 0, int.MaxValue);
            if (reserveLife) life += percent;
            else mana += percent;
        }
        // The support applies efficiency and multipliers to each child. The controller publishes the sum as-is.
        if (!linked || mana > 0 && player.Pools.Mana == 0) return null;
        return new(0, ActorScale.Saturate(mana), 0, ActorScale.Saturate(life)) { FixedMana = true, FixedLife = true };
    }

    static DamagePacket AddSpellslingerWandDamage(Player player, DamagePacket damage, double factor)
    {
        var main = ActiveMainHand(player);
        var off = ActiveOffHand(player);
        bool mainWand = main?.Type?.Class == "Wand", offWand = off?.Type?.Class == "Wand";
        if (mainWand && offWand) factor *= .5;
        if (mainWand && WeaponIn(player, main)?.LocalDamage is { } mainDamage) damage = damage.Add(mainDamage.Scale(factor));
        if (offWand && WeaponIn(player, off)?.LocalDamage is { } offDamage) damage = damage.Add(offDamage.Scale(factor));
        return damage;
    }

    static void SpellslingerOnWandRelease(Player player, ProjectileSession session, (int X, int Y) aim, double now)
    {
        if (player.Auras.Count == 0 || !CurrentGameWork || player.Paused || player.PendingTransfer is not null
            || player.PlayerLife == 0 || player.Closed || player.LeavingArea
            || player.Auras.Values.FirstOrDefault(static aura => aura.EffectId == SpellslingerEffect) is not { } controller) return;
        foreach (var (skillId, gem, support) in SpellslingerLinks(player, controller.SkillId))
        {
            if (!TriggeredSpellExecutable(player, skillId, gem)
                || GemSkillInstanceDisabled(player, gem.Effect, skillId, triggered: true) is not null) continue;

            bool Current() => session.Active && CurrentGameWork && !player.Paused && !player.Closed && !player.LeavingArea
                && player.PendingTransfer is null && player.PlayerLife > 0
                && player.Auras.TryGetValue(controller.SkillId, out var aura) && aura.Buff == controller.Buff
                && SameTriggerSource(gem, SupportedSkill(player, skillId));

            if (CooldownForSkill(player, skillId) is not { } cooldown)
            {
                if (TriggerGuardSpell(player, gem)
                    && now >= LinkedTriggersOf(player).ReadyAt.GetValueOrDefault(gem.Effect))
                    TryReleaseAutomaticPayload(player, session, skillId, gem, support, aim, Current, now, SpellslingerEffect, out _);
                continue;
            }
            var release = PrepareTriggeredPayload(player, session, skillId, aim, Current);
            if (release is null || !TryBeginCooldownUseCore(player, gem.Effect, cooldown, instant: true, () => { },
                    out var ticket, null, true, null, skillId, Current,
                    reserveAttack: false, triggered: true, advanceProjectiles: false)
                || ticket?.TryExecute(player, gem.Effect) != true) continue;
            ticket.CommitNativeUse(player, skillId);
            release();
            if (ticket.Paid is { } paid && (paid.Mana > 0 || paid.Life > 0))
                session.Enqueue(() => SendSkillCost(player, paid.Life, "Spellslinger cost"));
            session.Enqueue(() => SendNativeCooldownRefresh(player));
        }
    }
}
