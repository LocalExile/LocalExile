using GameServer.Core.Protocol.Instance;

static partial class Game
{
    // Called only inside SpendFlask's accepted charge transaction, under Monsters and FlaskBarGate.
    // No continuation after the inventory send is allowed to install or acknowledge this paid use again.
    static void AcceptRecoveryFlaskLocked(string remote, ClientState conn, uint slot, InventoryItem flask,
        ItemCatalog.BaseType type, Flasks.Flask use, FlaskRecovery.Use recovery, bool copyLife,
        IReadOnlyDictionary<string, int> local)
    {
        var state = Automation(conn);
        state.Enqueue(new(FlaskEnchantments.Trigger.Adjacent, flask.Id, (byte)slot));
        if (use.LifePerUse > 0) state.Enqueue(new(FlaskEnchantments.Trigger.LifeFlask, flask.Id, (byte)slot));
        state.Packets.Add(new(InstanceItems.ItemUsed(flask.Item.TypeHash,
            PlayerInventories.InventoryId(PlayerInventories.Flask), flask.Id), $"0x0263 flask {slot} used (native use sound)"));
        Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot} ({type.Name}): {recovery.Life:0.#} life, {recovery.Mana:0.#} mana over {recovery.Seconds:0.##} s, " +
            $"instantly {recovery.InstantLife:0.#} life, {recovery.InstantMana:0.#} mana{(recovery.ManaAtEnd > 0 ? $", {recovery.ManaAtEnd:0.#} mana at the end" : "")}" +
            $"{(recovery.RemovesLife + recovery.RemovesMana > 0 ? $", removes {recovery.RemovesLife:0.#} life, {recovery.RemovesMana:0.#} mana" : "")}" +
            $"{(recovery.ExtraLife > 0 ? $", {recovery.ExtraLife:0.#} more life over {FlaskRecovery.ExtraLifeSeconds} s" : "")}");
        uint lifeBefore = conn.PlayerLife, manaBefore = conn.PlayerMana;
        // Liveness and the exact source/generation were admitted by 'still' before the charge deduction.
        var (life, mana) = FlaskRecovery.Instant(recovery, lifeBefore, manaBefore, conn.PlayerMaxLife,
            LifeCap(conn), UnreservedMana(conn))!.Value;
        (conn.PlayerLife, conn.PlayerMana) = (life, mana);
        TrimLeechLocked(conn); // Full-Mana leech/Recoup settlement is part of the accepted pool commit.
        if (life != lifeBefore) state.Packets.Add(new([], $"0x01b1 player life {life} (flask)", FlaskOutputPool.Life));
        if (mana != manaBefore) state.Packets.Add(new([], $"0x01b1 player mana {mana} (flask)", FlaskOutputPool.Mana));
        double now = AreaTime(conn);
        ApplyConditionalFlaskAilmentProtectionLocked(conn, local, now);
        if (recovery.ExtraLife > 0)
            conn.FlaskEffects.Start(new((byte)slot, 0, 0, recovery.ExtraLife / FlaskRecovery.ExtraLifeSeconds, 0, now, now + FlaskRecovery.ExtraLifeSeconds));
        var effects = Flasks.RecoveryEffects(recovery, (byte)slot, now, () => conn.NextBuff++, FlaskEffectLifeBuffRow, FlaskEffectManaBuffRow);
        StartRecoveryEffectsLocked(conn, effects, copyLife);
        // IsRecovery rows carry one u32 recovery-per-minute value, duration as f32 seconds (+0x68),
        // and the flask-bar source cell (+0x60). Preserve the existing native timer/sound payloads.
        bool withDuration = Environment.GetEnvironmentVariable("Game_FLASK_BUFF_DURATION") != "0";
        foreach (var effect in effects)
        {
            uint perMinute = (uint)Math.Round((effect.LifePerSecond + effect.ManaPerSecond) * 60);
            double duration = effect.Ends - effect.Started;
            if (FlaskBuffs)
                state.Packets.Add(new(Flasks.Buff(PlayerId, effect.Buff, effect.BuffRow, (byte)slot,
                    withDuration ? duration : null, [perMinute]),
                    $"0x01c1 player buff {effect.Buff}: {(effect.BuffRow == FlaskEffectLifeBuffRow ? "flask_effect_life" : "flask_effect_mana")} " +
                    $"(BuffDefinitions row {effect.BuffRow}), {perMinute} per minute, {(withDuration ? $"{duration:0.##} s" : "no duration")}"));
        }
    }

    // The extracted "if bleeding/poisoned" suffixes remove an ailment that exists when the paid use is accepted and
    // then protect against new applications for the rolled seconds. Neither suffix grants protection if its trigger
    // ailment was absent. Both live in the shared incoming-ailment owner.
    static void ApplyConditionalFlaskAilmentProtectionLocked(ClientState conn,
        IReadOnlyDictionary<string, int> local, double now)
    {
        if (local.GetValueOrDefault("local_flask_bleeding_immunity_if_bleeding_and_remove_corrupted_blood_s") is int bleed
            && bleed > 0 && conn.IncomingAilments.RemoveOrProtectBleeding(now, bleed))
            Console.WriteLine($"[gateway] flask removed bleeding and protects for {bleed} s");
        if (local.GetValueOrDefault("local_flask_poison_immunity_if_poisoned_s") is int poison
            && poison > 0 && conn.IncomingAilments.RemoveOrProtectPoison(now, poison))
            Console.WriteLine($"[gateway] flask removed poison and protects for {poison} s");
    }

    static bool ConditionalFlaskAilmentProtectionTriggered(ClientState conn,
        IReadOnlyDictionary<string, int> local, double now) =>
        local.GetValueOrDefault("local_flask_bleeding_immunity_if_bleeding_and_remove_corrupted_blood_s") > 0
            && conn.IncomingAilments.Bleeding(now)
        || local.GetValueOrDefault("local_flask_poison_immunity_if_poisoned_s") > 0
            && conn.IncomingAilments.PoisonStacks(now) > 0;
}
