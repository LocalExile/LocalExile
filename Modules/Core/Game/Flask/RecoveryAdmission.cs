using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    // Called only inside SpendFlask's accepted charge transaction, in the area's frame and FlaskBarGate.
    // No continuation after the inventory send is allowed to install or acknowledge this paid use again.
    static int? AcceptRecoveryFlaskLocked(Player player, uint slot, InventoryItem flask,
        ItemCatalog.BaseType type, Flasks.Flask use, FlaskRecovery.Use recovery, bool copyLife,
        IReadOnlyDictionary<uint, int> local, Func<uint, int> character, bool appliesToPlayer,
        IReadOnlyList<UtilityFlasks.Source>? during = null)
    {
        var state = Automation(player);
        state.Enqueue(new(FlaskEnchantments.Trigger.Adjacent, flask.Id, (byte)slot));
        if (use.LifePerUse > 0) state.Enqueue(new(FlaskEnchantments.Trigger.LifeFlask, flask.Id, (byte)slot));
        state.Packets.Add(new(InstanceItems.ItemUsed(flask.Item.TypeHash,
            PlayerInventories.InventoryId(PlayerInventories.Flask), flask.Id), $"0x0263 flask {slot} used (native use sound)"));
        double now = AreaTime(player);
        QueueMapFlaskMeteor(player, now);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0070 use flask {slot} ({type.Name}): {recovery.Life:0.#} life, {recovery.Mana:0.#} mana over {recovery.Seconds:0.##} s, " +
            $"instantly {recovery.InstantLife:0.#} life, {recovery.InstantMana:0.#} mana{(recovery.ManaAtEnd > 0 ? $", {recovery.ManaAtEnd:0.#} mana at the end" : "")}" +
            $"{(recovery.RemovesLife + recovery.RemovesMana > 0 ? $", removes {recovery.RemovesLife:0.#} life, {recovery.RemovesMana:0.#} mana" : "")}" +
            $"{(recovery.ExtraLife > 0 ? $", {recovery.ExtraLife:0.#} more life over {FlaskRecovery.ExtraLifeSeconds} s" : "")}");
        if (use.LifePerUse > 0)
        {
            player.RecentEvents.Record(StatConditions.UsedLifeFlask, now);
        }
        RaiseCombatEvent(player, new ChargeEvent.FlaskUse(flask.Id,
            use.LifePerUse > 0 && use.ManaPerUse > 0 ? "hybrid" : use.LifePerUse > 0 ? "life" : "mana"));
        ApplyMinionRecoveryFlasksLocked(player, flask.Item, (byte)slot, use, recovery, local, character, now);
        // Sample the payer at accepted use, including a hybrid flask whose life recovery goes to a minion.
        // An effect rule live for the stat rolls it itself (power_charge_on_mana_flask_use).
        int chargeChance = use.ManaPerUse > 0 && !EffectRules.LiveFor(player, Stat.GainPowerChargeOnManaFlaskUsePctChance)
            ? character(Stat.GainPowerChargeOnManaFlaskUsePctChance) : 0;
        if (chargeChance > 0 && RandomRolls.Chance(chargeChance, 100, AreaRng(player)))
            ChargesOf(player).Enqueue(new ChargeEvent.Grant(ChargeType.Power, 1,
                Stats.Name(Stat.GainPowerChargeOnManaFlaskUsePctChance)));
        if (!appliesToPlayer)
        {
            TryGrantAlchemistsGenius(player);
            return null;
        }
        uint lifeBefore = player.PlayerLife, manaBefore = player.PlayerMana;
        // Liveness and the exact source/generation were admitted by 'still' before the charge deduction.
        var (life, mana) = FlaskRecovery.Instant(recovery, lifeBefore, manaBefore, player.PlayerMaxLife,
            LifeCap(player), UnreservedMana(player))!.Value;
        if (local.GetValueOrDefault(Stat.LocalUniqueFlaskInstantlyRemovesPctMaximumLife) is > 0 and var removeLife)
        {
            uint removed = (uint)Math.Min(life, ((long)player.PlayerMaxLife * removeLife + 99) / 100);
            life = VaalImmortalCallLife(player, life - removed, now);
        }
        (player.PlayerLife, player.PlayerMana) = (life, mana);
        if (life == 0)
        {
            player.InvalidateIncomingHits();
            ClearWardBreakOnDeath(player);
            RecordHardcoreDeath(player);
        }
        // Paid recovery flask modifiers can remove a pool, deal a separate hit, or grant charges.
        if (local.GetValueOrDefault(Stat.LocalFlaskRemovesPctMaximumEnergyShieldOnUse) is > 0 and var shieldPct)
        {
            double carry = 0;
            player.EnergyShield.Lose(player.EnergyShield.Maximum * (long)shieldPct / 100, ref carry);
        }
        if (local.GetValueOrDefault(Stat.LocalFlaskDealsPctMaximumLifeAsChaosDamageOnUse) is > 0 and var chaosPct)
        {
            int chaos = ActorScale.Saturate(player.PlayerMaxLife * (long)chaosPct / 100);
            if (chaos > 0) ApplySelfHitLocked(player, DamagePacket.Of(DamageType.Chaos, chaos, chaos), now);
        }
        (life, mana) = (player.PlayerLife, player.PlayerMana);
        TrimLeechLocked(player); // Full-Mana leech/Recoup settlement is part of the accepted pool commit.
        if (life != lifeBefore) state.Packets.Add(new([], $"0x01b1 player life {life} (flask)", FlaskOutputPool.Life));
        if (mana != manaBefore) state.Packets.Add(new([], $"0x01b1 player mana {mana} (flask)", FlaskOutputPool.Mana));
        if (life == 0) return player.IncomingHitEpoch;
        QueueFlaskUseCharges(player, type.Name, local);
        if (local.GetValueOrDefault(Stat.LocalUniqueFlaskStartEnergyShieldRecharge) != 0)
            player.EnergyShield.StartRecharge(now, character);
        ApplyConditionalFlaskAilmentProtectionLocked(player, local, now);
        bool hadRecoveryFlask = player.FlaskEffects.Any;
        if (recovery.ExtraLife > 0)
            player.FlaskEffects.Start(new((byte)slot, 0, 0, recovery.ExtraLife / FlaskRecovery.ExtraLifeSeconds, 0, now, now + FlaskRecovery.ExtraLifeSeconds));
        var effects = Flasks.RecoveryEffects(recovery, (byte)slot, now, () => player.NextBuff++, FlaskEffectLifeBuffRow, FlaskEffectManaBuffRow);
        StartRecoveryEffectsLocked(player, effects, copyLife);
        bool startedDuring = during is { Count: > 0 } && effects.Count > 0;
        if (startedDuring) player.RecoveryFlaskStats.Start(effects, during!);
        bool grantedArcaneSurge = character(Stat.ArcaneSurgeDuringManaFlaskEffect) != 0 && effects.Any(e => e.RestoresMana);
        if (startedDuring || grantedArcaneSurge || !hadRecoveryFlask && player.FlaskEffects.Any)
        {
            RecomputePoolsAndSpeed(player);
            state.StatRevision++;   // PublishFlaskChanges sends the character's stats
            if (startedDuring)
                Console.WriteLine($"[gateway] {player.Connection.Remote} flask {slot} ({type.Name}) during effect: " +
                    string.Join(", ", during!.Select(s => $"{s.Stat} {s.Value}")));
        }
        // IsRecovery rows carry one u32 recovery-per-minute value, duration as f32 seconds (+0x68),
        // and the flask-bar source cell (+0x60). Preserve the existing native timer/sound payloads.
        bool withDuration = Environment.GetEnvironmentVariable("Game_FLASK_BUFF_DURATION") != "0";
        foreach (var effect in effects)
        {
            uint perMinute = (uint)Math.Round((effect.LifePerSecond + effect.ManaPerSecond) * 60);
            double duration = effect.Ends - effect.Started;
            if (FlaskBuffs)
            {
                player.Area.Buffs.Set(player.Id, Flasks.Buff(player.Id, effect.Buff, effect.BuffRow, (byte)slot,
                    withDuration ? duration : null, [perMinute], FlaskEffectVisual(player, (byte)slot)));
                Note($"player buff {effect.Buff}: {(effect.BuffRow == FlaskEffectLifeBuffRow ? "flask_effect_life" : "flask_effect_mana")} " +
                    $"(BuffDefinitions row {effect.BuffRow}), {perMinute} per minute, {(withDuration ? $"{duration:0.##} s" : "no duration")}");
            }
        }
        TryGrantAlchemistsGenius(player);
        return null;
    }

    // The extracted "if afflicted" suffixes remove an ailment that exists when the paid use is accepted and then
    // protect against new applications for the rolled seconds. None grants protection if its trigger ailment was
    // absent. Bleed/poison live in IncomingAilments; elemental ailments live in ElementalAilments.
    static int BleedFlaskProtection(IReadOnlyDictionary<uint, int> local) =>
        Math.Max(local.GetValueOrDefault(Stat.LocalFlaskBleedingImmunityIfBleedingAndRemoveCorruptedBloodS),
            local.GetValueOrDefault(Stat.LocalFlaskBleedingImmunityIfBleedingAndRemoveCorruptedBlood) != 0 ? 4 : 0);

    static int PoisonFlaskProtection(IReadOnlyDictionary<uint, int> local) =>
        Math.Max(local.GetValueOrDefault(Stat.LocalFlaskPoisonImmunityIfPoisonedS),
            local.GetValueOrDefault(Stat.LocalFlaskPoisonImmunityIfPoisoned) != 0 ? 4 : 0);

    static int ChillFreezeFlaskProtection(IReadOnlyDictionary<uint, int> local) =>
        Math.Max(local.GetValueOrDefault(Stat.LocalFlaskChillOrFreezeImmunityIfChilledOrFrozenS),
            local.GetValueOrDefault(Stat.LocalFlaskChillOrFreezeImmunityIfChilledOrFrozen) != 0 ? 4 : 0);

    static int IgniteFlaskProtection(IReadOnlyDictionary<uint, int> local) =>
        Math.Max(local.GetValueOrDefault(Stat.LocalFlaskIgniteImmunityIfIgnitedAndRemoveBurningS),
            local.GetValueOrDefault(Stat.LocalFlaskIgniteImmunityIfIgnitedAndRemoveBurning) != 0 ? 4 : 0);

    static int ShockFlaskProtection(IReadOnlyDictionary<uint, int> local) =>
        Math.Max(local.GetValueOrDefault(Stat.LocalFlaskShockImmunityIfShockedS),
            local.GetValueOrDefault(Stat.LocalFlaskShockImmunityIfShocked) != 0 ? 4 : 0);

    static bool DispelsBurning(IReadOnlyDictionary<uint, int> local) =>
        local.GetValueOrDefault(Stat.LocalFlaskDispelsBurningAndIgniteImmunityDuringEffect) != 0
        || local.GetValueOrDefault(Stat.LocalFlaskIgniteImmunityIfIgnitedAndRemoveBurning) != 0
        || local.GetValueOrDefault(Stat.LocalFlaskIgniteImmunityIfIgnitedAndRemoveBurningS) > 0;

    static void ApplyEquipmentFlaskCleansesLocked(Player player, double now)
    {
        if (!player.IncomingAilments.Bleeding(now) && !player.ElementalAilments.HasEffects
            && FlameTouchedOf(player)?.Source is null) return;
        // Read equipment at the paid use, after the flask's own suffix has had a chance to grant immunity.
        var stats = CharacterStatSources(player).Stats;
        if (stats.GetValueOrDefault(Stat.RemoveBleedOnFlaskUse) != 0)
            player.IncomingAilments.RemoveOrProtectBleeding(now, 0);
        if (stats.GetValueOrDefault(Stat.RemoveIgniteAndBurningOnFlaskUse) != 0)
        {
            player.ElementalAilments.RemoveOrProtectIgnite(now, 0);
            EndFlameTouched(player, now);
        }
        if (stats.GetValueOrDefault(Stat.RemoveChillAndFreezeOnFlaskUse) != 0)
            RemoveFlaskChillFreezeLocked(player, now, 0);
    }

    static bool RemoveFlaskChillFreezeLocked(Player player, double now, int protectSeconds)
    {
        if (!player.ElementalAilments.RemoveOrProtectChillFreeze(now, protectSeconds)) return false;
        if (player.FreezeHeldUntil > now) player.FreezeHeldUntil = now;
        player.ApplyMovementSpeedAt(now);
        return true;
    }

    internal static void ApplyConditionalFlaskAilmentProtectionLocked(Player player,
        IReadOnlyDictionary<uint, int> local, double now)
    {
        int bleed = BleedFlaskProtection(local);
        if (bleed > 0 && player.IncomingAilments.RemoveOrProtectBleeding(now, bleed))
            Console.WriteLine($"[gateway] flask removed bleeding and protects for {bleed} s");
        if (CorruptedBloodStacks(player, now) > 0)
        {
            if (bleed > 0 || CharacterPlayerStats(player).GetValueOrDefault(Stat.RemoveCorruptedBloodWhenYouUseAFlask) != 0)
                RemoveCorruptedBlood(player, now, bleed);
        }
        int poison = PoisonFlaskProtection(local);
        if (poison > 0 && player.IncomingAilments.RemoveOrProtectPoison(now, poison))
            Console.WriteLine($"[gateway] flask removed poison and protects for {poison} s");
        int chillFreeze = ChillFreezeFlaskProtection(local);
        bool dispelsFreezeChill = local.GetValueOrDefault(Stat.LocalFlaskDispelsFreezeAndChill) != 0;
        if ((chillFreeze > 0 || dispelsFreezeChill) && RemoveFlaskChillFreezeLocked(player, now, chillFreeze))
            Console.WriteLine($"[gateway] flask removed chill/freeze{(chillFreeze > 0 ? $" and protects for {chillFreeze} s" : "")}");
        int ignite = IgniteFlaskProtection(local);
        if (ignite > 0 && player.ElementalAilments.RemoveOrProtectIgnite(now, ignite))
            Console.WriteLine($"[gateway] flask removed ignite and protects for {ignite} s");
        int shock = ShockFlaskProtection(local);
        if (shock > 0 && player.ElementalAilments.RemoveOrProtectShock(now, shock))
            Console.WriteLine($"[gateway] flask removed shock and protects for {shock} s");
        if (DispelsBurning(local))
            EndFlameTouched(player, now);
    }

    static bool ConditionalFlaskAilmentProtectionTriggered(Player player,
        IReadOnlyDictionary<uint, int> local, double now) =>
        BleedFlaskProtection(local) > 0
            && (player.IncomingAilments.Bleeding(now) || CorruptedBloodStacks(player, now) > 0)
        || PoisonFlaskProtection(local) > 0
            && player.IncomingAilments.PoisonStacks(now) > 0
        || (ChillFreezeFlaskProtection(local) > 0 || local.GetValueOrDefault(Stat.LocalFlaskDispelsFreezeAndChill) != 0)
            && (player.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Chill, now) > 0
                || player.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Freeze, now) > 0)
        || IgniteFlaskProtection(local) > 0
            && player.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Ignite, now) > 0
        || ShockFlaskProtection(local) > 0
            && player.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Shock, now) > 0
        || DispelsBurning(local) && FlameTouchedActive(player);
}
