using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageRange = GameServer.Core.World.DamageRange;

partial class Program
{
    // Whether an attack may start now: the shared attack timer (the default attack and the attack gem skills) is over, or
    // the use is no more than a quarter of an attack early (the client's jitter). Does not reserve the timer: an attack used
    // on a monster waits for the walk into reach, and CastGemSkill reserves it when the swing starts.
    static bool AttackReady(ClientState conn, out double seconds)
    {
        seconds = WeaponFor(conn).AttackSeconds(CharacterPlayerStats(conn));
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters) return AttackTiming.Ready(now, conn.NextAttackAt, seconds);
    }

    // The 0x1af hit angle (0..255 = a full turn), the direction the hit comes from (attacker → target) in the client's
    // facing convention atan2(dx, −dy), like Positioned's rotation.
    static byte HitAngle(int dx, int dy)
    {
        double a = Math.Atan2(dx, -dy);
        if (a < 0) a += 2 * Math.PI;
        return (byte)((int)Math.Round(a * 256.0 / (2 * Math.PI)) & 0xff);
    }

    // A positive offset on the hit's contact timing (seconds): the client starts its swing a beat after the server's 0x1ab
    // echo (the walk estimate leads the client's own walk, so the contact keyframe alone leaves the blood early). A fixed
    // offset is a compromise over the variable walk-sync; Game_HIT_OFFSET_MS overrides it (150 ms here covers most of the
    // walk-into-range error without overshooting a swing). A value that is not a finite number keeps the default.
    static double HitContactOffsetSeconds() =>
        (double.TryParse(Environment.GetEnvironmentVariable("Game_HIT_OFFSET_MS"), System.Globalization.CultureInfo.InvariantCulture, out var hitOff)
            && double.IsFinite(hitOff) ? hitOff : 150) / 1000.0;

    // The attack's contact time in seconds (AttackTiming.ContactDelay): the attack time scaled to the active weapon's attack1
    // contact keyframe, plus the walk-in offset for an attack that approached its target first, and always before the next
    // attack may start (which would cancel the hit).
    static double AttackContactDelay(ClientState conn, double attackSeconds, bool approach)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? Skills.ClassFor(conn.ClassRow).AnimatedObject;
        double contact = Animations.AttackContact(ao, WeaponProfileFor(conn, activeSet: true));
        return AttackTiming.ContactDelay(attackSeconds, contact, approach ? HitContactOffsetSeconds() : 0);
    }

    // The player uses a skill on a monster (C→S 0x24, e.g. the default attack on a left click). The client walks into range
    // first, so the server walks its estimate too (WalkEstimator.Approach, reach = the weapon's MaxAttackDistance in cells,
    // centre to centre) and only then starts the swing: it echoes the action aimed at the monster (0x1ab, flags | 0x80 +
    // target id) and, at the contact point (AttackContactDelay: the attack animation's contact keyframe), lowers the monster's life (0x1b1) by the unarmed damage
    // (Characters.dat MinDamage..MaxDamage raised by the increased melee physical damage stats). Any later action (a move, another click) cancels both.
    // One attack per attack time (WeaponSpeed ms scaled by the player's attack speed stats, a quarter early allowed for
    // jitter); earlier repeats of 0x24 are ignored. At 0 the client's Life component runs its death; the corpse is
    // removed 10 s later.
    static async Task Attack(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, SpawnedMonster m)
    {
        int projectileEpoch;
        lock (conn.Monsters) projectileEpoch = conn.ProjectileEpoch;
        var weapon = WeaponFor(conn);
        double now = conn.Clock.Elapsed.TotalSeconds, duration;
        lock (conn.Monsters) duration = weapon.AttackSeconds(CharacterPlayerStats(conn));
        // The client sends one 0x24 per swing while the button is held; jitter makes the next one arrive a little before a
        // full attack time, so accept it up to a quarter of an attack early.
        lock (conn.Monsters)
            if (!AttackTiming.Ready(now, conn.NextAttackAt, duration))
            {
                Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} ignored: {conn.NextAttackAt - now:0.000} s before the next attack");
                return;
            }
        int action = Interlocked.Increment(ref conn.ActionCount);
        (int X, int Y) target;
        lock (conn.Monsters) target = m.Walk.Position(now);
        var (here, stop, arrive) = conn.Walk.Approach(target.X, target.Y, weapon.Range, now, tolerance: m.StopRange - 2);
        Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} on 0x{use.TargetId:x}: player ({here.X}, {here.Y}), " +
            $"monster ({target.X}, {target.Y}), distance {Dist(here, target):0.0}; swing from ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (conn.ActionCount != action) { Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled before the swing"); return; }
        now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters)
        {
            target = m.Walk.Position(now);
            conn.NextAttackAt = AttackTiming.After(now, conn.NextAttackAt, duration);
        }
        if (Dist(stop, target) > weapon.Range + 2)
        {
            Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} out of reach: {Dist(stop, target):0.0} > {weapon.Range}");
            return;
        }
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionStarted(PlayerId, stop.X, stop.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
            $"0x01ab player skill 0x{use.SkillId:x4} on monster 0x{use.TargetId:x} ({m.Label}, life {m.Life}/{m.MaxLife}), attack time {duration:0.000} s");
        if (m.Life == 0) return;
        // A bow's ordinary attack shoots an arrow at the target: the client draws the projectile object we create
        // (Projectiles.dat Metadata/Projectiles/DefaultArrow); the hit below keeps the swing's own timing.
        if (WeaponProfileFor(conn).Bow && Skills.Projectile("Metadata/Projectiles/DefaultArrow") is { } arrow)
            _ = FlyProjectile(remote, stream, ciphers, conn, BowArrow(conn, arrow), stop, target, damageTo: null, impact: "none", epoch: projectileEpoch);
        // The hit lands at the attack animation's contact keyframe (data/animation-contacts.json), not a fixed half: the
        // client stretches the clip to the attack time, so the blood and the weapon impact sound line up with the swing.
        await Task.Delay(TimeSpan.FromSeconds(AttackContactDelay(conn, duration, approach: true)));
        uint life;
        bool stageStarts = false;
        int accuracy, evasion;
        lock (conn.Monsters)
        {
            if (m.Life == 0) return;
            if (conn.ActionCount != action)
            {
                Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled by a later action");
                return;
            }
            accuracy = conn.Pools.Accuracy;
            evasion = Monsters.BaseEvasion(m.Level) ?? 0;
        }
        int hitChance = DamageModel.AccuracyHitChance(evasion, accuracy);
        if (!DamageModel.Hits(hitChance, Random.Shared.Next(100)))
        {
            Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} missed: accuracy {accuracy} vs evasion {evasion}, " +
                $"{hitChance}% chance");
            return;
        }
        int attackEffect = SkillEffectId(conn, SkillCatalog.FirstSkillId) is { } attackId ? Skills.EffectRow(attackId) : -1;
        var player = CharacterPlayerStats(conn);
        var hit = AttackHit(weapon, player)
            .Convert(ConversionFor(conn, attackEffect, 1, attack: true));
        // Impale: the stacks already on the target add their stored damage to this hit, this hit uses one of their five
        // hits, and the hit's own physical damage may store a new stack for the hits after it.
        double physical = hit[DamageType.Physical].Max;
        double stored = m.Impale.Bonus;
        if (stored > 0) hit = hit.Add(DamageType.Physical, new DamageRange(stored, stored));
        m.Impale.AfterHit();
        int impaleChance = Stats.Key("impale_on_hit_%_chance") is uint impaleKey ? player.GetValueOrDefault(impaleKey) : 0;
        bool impaled = impaleChance > 0 && Random.Shared.Next(100) < impaleChance && m.Impale.Apply(physical);
        // Read before the hit is applied: a killing blow clears the target's impales.
        int impaleStacks = m.Impale.Stacks;
        double impaleStored = m.Impale.Bonus;
        int damage = hit.RollTotal(Random.Shared);
        bool critical = conn.Pools.AttackCriticalStrikeChance > 0
            && Random.Shared.Next(10_000) < conn.Pools.AttackCriticalStrikeChance;
        if (critical) damage = DamageModel.CriticalDamage(damage, conn.Pools.CriticalStrikeMultiplier);
        lock (conn.Monsters)
        {
            if (m.Life == 0) return;
            if (conn.ActionCount != action)
            {
                Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled by a later action");
                return;
            }
            (m.Life, stageStarts) = TakeDamageLocked(m, damage);
            life = m.Life;
            conn.LastEnemyHitAt = conn.Clock.Elapsed.TotalSeconds;
            if (life == 0) { m.Aggro = false; m.Dead = true; m.Impale.Clear(); }
        }
        // At 0 the client's Life tick (0x141e6a190) stops notifying its listeners, so the mini life bar would keep the last
        // value: hide it with the stat its listener watches (hide_mini_life_bar).
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(use.TargetId, [(StatKeys.HideMiniLifeBar, 1)]),
                $"0x01b3 monster 0x{use.TargetId:x} hide_mini_life_bar = 1");
        // The hit reaction: one of the rig's TakeHit clips and its SoundEvent (0x1b2).
        await SendFlinch(remote, stream, ciphers, conn, use.TargetId, m, damage, log: true);
        // The hit result for the client's damage/effect pipeline (0x1af): the client applies the hit locally (the target's
        // blood, the weapon impact sound "bonk") and 0x1b1 stays authoritative on life. flags 0x4201 is the real server's
        // player-melee hit (bit 0 hit, 0x200 damage, 0x4000 attack), kind 0x03 = action hit with hit data, the angle drives
        // the directional blood.
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionHit(PlayerId, use.Sequence, use.TargetId, flags: 0x4201, kind: 0x03,
                angle: HitAngle(target.X - stop.X, target.Y - stop.Y)),
            $"0x01af player hit on monster 0x{use.TargetId:x} (sequence 0x{use.Sequence:x4})");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(use.TargetId, life),
            $"0x01b1 monster 0x{use.TargetId:x} hit for {damage}{(critical ? " (critical)" : "")}" +
            (stored > 0 ? $", impale +{stored:0.#} ({impaleStacks} stack(s))" : "") +
            (impaled ? $", impaled ({impaleStacks} stack(s), {impaleStored:0.#} stored)" : "") +
            $", life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
        if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, use.TargetId, m);
        if (stageStarts) _ = RunBossStage(remote, stream, ciphers, conn, use.TargetId, m);
    }

    // A gem skill used on a monster (C→S 0x24 with a skill id other than the default attack's). A spell stops and casts from
    // where the player stands: the server echoes the action from there, aimed at the monster, so the client plays the cast.
    // An attack gem (Holy Strike, Ground Slam …) is used like the default attack (see Attack): the client walks into reach
    // first, so the server walks its estimate too and only echoes the swing — and pays the skill's cost — on arrival. Before
    // 2026-10-25 the attack cast at the click from out of reach: the mana was gone at once and the hit landed around the
    // player, hitting nothing. CastGemSkill pays the cost and runs the skill's effect (projectile, area hit, aura, self-cast).
    static async Task CastOn(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, SpawnedMonster m)
    {
        var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == use.SkillId);
        // A movement attack (Flicker Strike, Leap Slam) closes the distance itself, and an attack used with "always attack
        // without moving" (Flags bit 0) swings from where the player stands: both keep the immediate cast.
        if (gem.Name is not null && Skills.IsAttack(gem.Effect) && !Skills.IsMovement(gem.Effect) && (use.Flags & 0x1) == 0)
        {
            if (!AttackReady(conn, out double attackSeconds))
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x}: ignored, the previous attack is still running (attack time {attackSeconds:0.00} s) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
                return;
            }
            var weapon = WeaponFor(conn);
            double now = conn.Clock.Elapsed.TotalSeconds;
            int action = Interlocked.Increment(ref conn.ActionCount);
            (int X, int Y) target;
            lock (conn.Monsters) target = m.Walk.Position(now);
            var (here, stop, arrive) = conn.Walk.Approach(target.X, target.Y, weapon.Range, now, tolerance: m.StopRange - 2);
            Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x}: player ({here.X}, {here.Y}), " +
                $"monster ({target.X}, {target.Y}), distance {Dist(here, target):0.0}; swing from ({stop.X}, {stop.Y}) in {arrive - now:0.00} s (t={now:0.000})");
            if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
            if (conn.ActionCount != action)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x} cancelled before the swing");
                return;
            }
            now = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters) target = m.Walk.Position(now);
            if (Dist(stop, target) > weapon.Range + 2)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x} out of reach: {Dist(stop, target):0.0} > {weapon.Range}");
                return;
            }
            await Send(remote, stream, ciphers,
                InstanceProtocol.ActionStarted(PlayerId, stop.X, stop.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player skill 0x{use.SkillId:x4} {gem.Name} on monster 0x{use.TargetId:x} ({m.Label}) from ({stop.X}, {stop.Y}) to ({target.X}, {target.Y}), flags 0x{use.Flags:x4} (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
            _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, stop, target, use.TargetId, sequence: use.Sequence);
            return;
        }
        Interlocked.Increment(ref conn.ActionCount);
        double castAt = conn.Clock.Elapsed.TotalSeconds;
        (int X, int Y) aim;
        lock (conn.Monsters) aim = m.Walk.Position(castAt);
        var stand = conn.Walk.Position(castAt);
        conn.Walk.Order(stand.X, stand.Y, castAt);
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionStarted(PlayerId, stand.X, stand.Y, aim.X, aim.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
            $"0x01ab player skill 0x{use.SkillId:x4} cast on monster 0x{use.TargetId:x} ({m.Label}) from ({stand.X}, {stand.Y}) to ({aim.X}, {aim.Y}), flags 0x{use.Flags:x4}");
        _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, stand, aim, use.TargetId, sequence: use.Sequence);
    }

    // A gem skill's cast: Righteous Fire puts its buff on, any other skill launches its projectile (if it has one). False when
    // it was refused (cooldown, mana), so its use is not confirmed to the client.
    static async Task<bool> CastGemSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, uint targetId = 0, ushort sequence = 0)
    {
        if (InTown(conn) || conn.PlayerLife == 0) return false;
        var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
        // A gem whose requirements the character's level does not meet is disabled: its skill stays on the skill bar but
        // cannot be used (the client shows the gem red but still sends the use; the server withholds the action, see
        // OnClientMessages). Attributes are not checked yet: their formula is not fitted (19-gems.md "Open").
        if (gem.Name is not null && GemDisabled(conn, gem.Effect) is { } disabled)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: {disabled} (disabled)");
            await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (disabled skill, cost back)");
            return false;
        }
        // Vaal souls are not gathered yet (Roadmap "Gems later"), so a Vaal skill never has the souls it needs.
        if (gem.Name is not null && Skills.VaalSouls(gem.Effect, gem.Level) is int souls and > 0)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: needs {souls} Vaal souls, has 0");
            return false;
        }
        // A skill with a cooldown (GEPL Cooldown: auras 1200 ms) is refused until it is over: spamming Haste toggled it.
        if (gem.Name is not null && Skills.CooldownMs(gem.Effect, gem.Level) is int cooldown and > 0)
        {
            double now = conn.Clock.Elapsed.TotalSeconds, readyAt;
            lock (conn.Monsters)
            {
                readyAt = conn.SkillReadyAt.GetValueOrDefault(gem.Effect);
                if (now >= readyAt) conn.SkillReadyAt[gem.Effect] = now + cooldown / 1000.0;
            }
            if (now < readyAt)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: on cooldown for {(readyAt - now) * 1000:0} ms more");
                return false;
            }
        }
        // An attack gem swings with the weapon's attack time, like the default attack (see Attack): while the button is held
        // the client keeps aiming it, and every use used to pay its cost and start a swing (live 2026-10-25: Holy Strike on
        // the right mouse button, twelve swings a second at 6 mana each). The timer is shared with the default attack, so
        // two attacks cannot start at once either.
        if (gem.Name is not null && Skills.IsAttack(gem.Effect))
        {
            double duration = WeaponFor(conn).AttackSeconds(CharacterPlayerStats(conn));
            double now = conn.Clock.Elapsed.TotalSeconds, next;
            bool ready;
            lock (conn.Monsters)
            {
                next = conn.NextAttackAt;
                ready = AttackTiming.Ready(now, next, duration);
                if (ready) conn.NextAttackAt = AttackTiming.After(now, next, duration);
            }
            if (!ready)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: ignored, {next - now:0.000} s before the next attack (t={now:0.000})");
                return false;
            }
        }
        if (gem.Name is not null && Skills.Reservation(gem.Effect, gem.Level) is { } reservation)
        {
            await ToggleAura(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Name, reservation, gem.Level, gem.Quality);
            return true;
        }
        if (gem.Name is not null)
        {
            if (!PayMana(conn, gem.Effect, gem.Level, gem.Quality, out uint cost))
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: not enough mana ({conn.PlayerMana} < {cost})");
                return false;
            }
            if (cost > 0)
                await SendPlayerMana(remote, stream, ciphers, conn,
                    $"0x01b1 player mana {conn.PlayerMana} (-{cost} for {gem.Name}) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
        }
        // The Portal skill: a town portal, as the Portal Scroll's, once its cast (2.5 s) is over.
        if (gem.Name is not null && Skills.EffectId(gem.Effect) == "Portal")
        {
            if (!PortalAllowed(conn)) { Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} Portal: not here"); return false; }
            int castMs = Skills.CastTimeMs(gem.Effect);
            int action = conn.ActionCount;
            _ = Task.Delay(castMs).ContinueWith(async _ =>
            {
                if (!conn.Closed && conn.ActionCount == action)
                    await OpenTownPortal(remote, stream, ciphers, conn, from.X, from.Y, "Portal skill");
            });
            return true;
        }
        if (gem.Name is not null && IsRighteousFire(gem.Effect))
            await ActivateRighteousFire(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Level, gem.Quality, gem.Gem);
        else
            await LaunchProjectile(remote, stream, ciphers, conn, skillId, from, to, targetId, sequence: sequence);
        return true;
    }

    // An instant skill (C→S 0x191): its effect, then S→C 0x1ce, which has the client run the skill's action without animation
    // and consume a use of it: the skill bar's cooldown starts (the tooltip showed the cooldown, the bar did not run it).
    static async Task UseInstantSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, uint targetId, (int X, int Y) at)
    {
        if (await CastGemSkill(remote, stream, ciphers, conn, skillId, at, at, targetId))
            await Send(remote, stream, ciphers, InstanceProtocol.InstantAction(PlayerId, skillId, at.X, at.Y, targetId),
                $"0x01ce player skill 0x{skillId:x4}: instant action (its use and cooldown)");
    }

    // Aura skills toggle: the first cast reserves their mana and puts their buff on the player, the next cast turns them
    // off. The reserved mana is sent in the Life pool and in every later 0x1b1 mana update; the client does not derive it.
    static async Task ToggleAura(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, int effect, string name, (int Flat, int Percent) reservation, int level, int quality)
    {
        string? effectId = Skills.EffectId(effect);
        if (effectId is null || !AuraCatalog.Supported(effectId))
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: reservation is known but this aura's effect is not implemented yet");
            return;
        }
        AuraCatalog.Aura? ended = null;
        AuraCatalog.Aura? started = null;
        lock (conn.Monsters)
        {
            if (conn.Auras.Remove(skillId, out var old))
                ended = old;
            else if (conn.Auras.Values.FirstOrDefault(a => a.EffectId == effectId) is { } same)
            {
                // One aura of a kind at a time: a second gem of the same aura can't start it.
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: refused, {same.Name} already runs (skill 0x{same.SkillId:x4})");
                return;
            }
            else
            {
                var aura = new AuraCatalog.Aura(skillId, effect, effectId, name, conn.NextBuff++,
                    AuraCatalog.BuffRow(effectId), reservation.Flat, reservation.Percent, level, quality, SourceBuff: conn.NextBuff++);
                conn.Auras[skillId] = aura;
                int reserved = AuraCatalog.RequiredMana(conn.Pools, conn.Auras.Values);
                if (reserved > conn.Pools.Mana)
                {
                    conn.Auras.Remove(skillId);
                    Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: needs {reserved} mana reserved, " +
                        $"maximum is {conn.Pools.Mana}");
                    return;
                }
                started = aura;
                conn.PlayerMana = (uint)Math.Min(conn.PlayerMana, (uint)(conn.Pools.Mana - reserved));
            }
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }

        CharacterSaves.SetAuraActive(CharacterName(conn), effectId, started is not null);
        if (ended is not null)
        {
            foreach (var packet in AuraCatalog.EndPackets(ended, PlayerId))
                await Send(remote, stream, ciphers, packet,
                    $"0x01c2 player aura {ended.Name} ends");
        }
        if (started is not null)
        {
            foreach (var packet in AuraBuffs(started))
                await Send(remote, stream, ciphers, packet,
                    $"0x01c1 player aura {started.Name} (BuffDefinitions row {started.BuffRow})");
        }
        await SendCharacterStats(remote, stream, ciphers, conn, started is not null ? $"aura {name} on" : $"aura {ended!.Name} off");
        await SendPlayerMana(remote, stream, ciphers, conn,
            $"0x01b1 player mana {conn.PlayerMana}/{conn.Pools.Mana}, {ReservedMana(conn)} reserved");
    }

    static IEnumerable<byte[]> AuraBuffs(AuraCatalog.Aura aura) =>
        AuraCatalog.StartPackets(aura, PlayerId, Skills.ActionHash(aura.Effect));

    // Experimental: a projectile gem skill's projectile. After the cast time the server creates the Projectiles.dat object
    // named after the effect (Fireball → Metadata/Projectiles/Fireball) just ahead of the caster, wakes it and moves it to the
    // aimed point and on to its range with 0x1ba at the row's ProjectileSpeed (world units per second, 250/23 per grid
    // cell), stopping at the first wall or monster on its path; a monster takes the skill's damage packet
    // (SkillCatalog.HitDamage, after conversion). It explodes there; then the object is removed.
    static async Task LaunchProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, uint targetId = 0, ushort sequence = 0)
    {
        var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is null) { Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4}: no gem skill"); return; }
        if (Skills.ProjectileFor(gem.Effect) is not { } projectile)
        {
            // Not a projectile skill (strikes, slams, areas): its hit lands on the monsters around the aimed point.
            await AreaSkillHit(remote, stream, ciphers, conn, skillId, gem, from, to, sequence: sequence);
            return;
        }
        var projectileHitPlan = SkillHitPlan.ForSkill(Skills, gem.Effect, gem.Level, gem.Quality);
        if (!projectileHitPlan.CanHit) return;
        int action = conn.ActionCount;
        int epoch;
        lock (conn.Monsters) epoch = conn.ProjectileEpoch;
        // Released at the action's contact keyframe. An attack gem (Burning Arrow, Caustic Arrow …) plays the weapon's
        // attack1 animation, which the client stretches to the weapon's attack time: its contact is attack1_bow's 399/1066
        // (0.374 for the Ranger) of that time. A spell plays its spell_projectile clip instead (Dex: 699/1133 = 0.617 of
        // the cast). Using the spell fraction for a bow sent the arrow 0.617 of the action after the swing had already
        // lowered the bow (live 2026-10-25: the client's create arrived ~600 ms after 0x1ab, while attack1_bow's contact is
        // ~0.37 of it).
        var cls = Skills.ClassFor(conn.ClassRow);
        bool attack = gem.Name is not null && Skills.IsAttack(gem.Effect);
        double releaseDelay = attack
            ? AttackContactDelay(conn, WeaponFor(conn).AttackSeconds(CharacterPlayerStats(conn)), approach: false)
            : CastMs(conn, gem.Effect) / 1000.0 * Animations.SpellRelease(cls.AnimatedObject);
        if (!await WaitProjectileTime(conn, conn.Clock.Elapsed.TotalSeconds + Math.Max(0.001, releaseDelay), epoch)
            || conn.ActionCount != action) { Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} cancelled before release"); return; }
        // A worn skill effect (its skill_art_variation) flies its own projectile and explodes in its own art (SkillArt).
        int variation = SkillArtVariation(conn, gem.Gem);
        projectile = (Art.Projectile(projectile.Id, variation), projectile.Speed);
        // The fireball explosion is only right for the Fireball spell; a bow's arrow (Burning Arrow, Caustic Arrow …)
        // sticks and disappears on hit, so it gets no impact. Its burning/debuff is the monster's, not the arrow's.
        string impact = projectile.Id.Contains("Fireball")
            ? Art.AnimatedInPlaceOf(DefaultProjectileImpactAo, variation)
            : "none";
        // Keep the player's final speed separate from the skill's raw stats: the panel already includes the player's
        // base speed, while a gem's own base speed (including quality) adds to that total instead of replacing it.
        var playerSpeedStats = CharacterPlayerStats(conn);
        var skillSpeedStats = Skills.EffectStats(gem.Effect, gem.Level, gem.Quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int PlayerSpeedStat(string name) => Stats.Key(name) is uint key ? playerSpeedStats.GetValueOrDefault(key) : 0;
        int SkillSpeedStat(string name) => Stats.Key(name) is uint key ? skillSpeedStats.GetValueOrDefault(key) : 0;
        float speed = ProjectileSpeed.CellsPerSecond(projectile.Speed, PlayerSpeedStat, SkillSpeedStat,
            Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        await FlyProjectile(remote, stream, ciphers, conn, (projectile.Id, speed), from, to, impact: impact, damageTo: m =>
        {
            // The player's stats (passives, items, auras) under the skill's own, as GemSkills sends them. Keep the hit's
            // types separate so conversion and per-type mitigation apply. An attack gem (Burning Arrow, Caustic Arrow …) has
            // no spell damage of its own: its hit is the weapon's, scaled by the skill's damage effectiveness, with the
            // attack's crit — the same split AreaSkillHit makes for the non-projectile gems.
            var stats = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            var own = Skills.EffectStats(gem.Effect, gem.Level, gem.Quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            int chance = projectileHitPlan.CriticalChance(conn.Pools.AttackCriticalStrikeChance,
                () => SpellCriticalStrike.Chance(Skills.SpellCritChance(gem.Effect, gem.Level), Stats, stats, own));
            foreach (var (k, v) in own) stats[k] = v;
            bool spell = projectileHitPlan.Kind == SkillHitKind.Spell;
            var hit = projectileHitPlan.BuildHit(
                () => SpellDamage.WithEffectiveness(AttackHit(WeaponFor(conn), CharacterPlayerStats(conn)),
                        Skills.DamageEffectiveness(gem.Effect, gem.Level))
                    .Convert(ConversionFor(conn, gem.Effect, gem.Level, attack: true)),
                spellBase => SpellDamage.Packet(spellBase, name => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0,
                        AddedEffectiveness(gem.Effect, gem.Level))
                    .Convert(ConversionFor(conn, gem.Effect, gem.Level, attack: false)));
            int damage = hit.RollTotal(Random.Shared);
            bool critical = spell
                ? chance > 0 && SpellCriticalStrike.Hits(chance, Random.Shared.Next(SpellCriticalStrike.RollSize))
                : chance > 0 && Random.Shared.Next(10_000) < chance;
            if (critical)
                damage = DamageModel.CriticalDamage(damage,
                    spell ? conn.Pools.SpellCriticalStrikeMultiplier : conn.Pools.CriticalStrikeMultiplier);
            return (damage, critical);
        }, epoch: epoch);
    }

    // The radius in cells of a gem skill's hit around its aimed point (AreaSkillHit): its own
    // active_skill_base_area_of_effect_radius, else Cyclone's 16 or the generic 12, grown by the player's area stats
    // (SkillArea). The radius stat is in cells (Righteous Fire's 18 is its 1.8 m, verified against the real game 2026-10-23).
    static int SkillRadius(ClientState conn, IReadOnlyDictionary<uint, int> skillStats, bool spell, int effect)
    {
        var areaStats = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        foreach (var (k, v) in skillStats) areaStats[k] = v;
        int Get(string name) => Stats.Key(name) is uint k ? areaStats.GetValueOrDefault(k) : 0;
        return SkillArea.Radius(SkillArea.BaseRadius(Skills.EffectId(effect), Get), Get, spell);
    }

    // Whether a living monster stands within a gem attack's hit radius of a ground point. The client sends a point with a
    // monster on it as C→S 0x25 (not C→S 0x24 on the object), so an attack-move at such a point has to become a swing.
    static bool MonsterAtAim(ClientState conn, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        int x, int y, double at)
    {
        var stats = Skills.EffectStats(gem.Effect, gem.Level, gem.Quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int radius = SkillRadius(conn, stats, spell: false, gem.Effect);
        lock (conn.Monsters)
            return conn.Monsters.Any(m => !m.Value.Dead && m.Value.Life > 0
                && Dist(m.Value.Walk.Position(at), (x, y)) <= radius);
    }

    // A gem skill without a projectile (strikes, slams, area spells): a first, simple hit (2026-10-23). It lands once, after
    // half the attack time (attacks) or the cast (spells), on every living monster within the skill's area radius
    // (active_skill_base_area_of_effect_radius, else AreaSkillRadius cells, grown by area of effect: SkillArea) of the aimed point, or of the player for a
    // point too far for a melee skill. Attacks deal the weapon's hit as the default attack does, spells their own damage
    // (SkillCatalog.HitDamage) with the player's stats; both convert, crit and roll per monster. An attack's hit carries
    // the skill's damage effectiveness (SkillCatalog.DamageEffectiveness). Not modelled yet: the skills' own shapes (waves,
    // cones, chains), stun, and the attack's accuracy roll.
    static async Task AreaSkillHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) from, (int X, int Y) to,
        bool immediate = false, ushort sequence = 0)
    {
        var skillStats = Skills.EffectStats(gem.Effect, gem.Level, gem.Quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        // An attack gem's share of the weapon's damage ("Deals 77.3% of base Attack Damage"): the hit below uses it.
        int effectiveness = Skills.DamageEffectiveness(gem.Effect, gem.Level);
        int Stat(string name) => Stats.Key(name) is uint k ? skillStats.GetValueOrDefault(k) : 0;
        if (Stat("base_deal_no_damage") != 0) return;
        var hitPlan = SkillHitPlan.ForSkill(Skills, gem.Effect, gem.Level, gem.Quality);
        if (!hitPlan.CanHit) return;
        bool spell = hitPlan.Kind == SkillHitKind.Spell;
        var weapon = WeaponFor(conn);
        double delay;
        // An attack lands at its contact keyframe; the player swings where it stands, so without the walk-in offset.
        lock (conn.Monsters) delay = spell ? CastMs(conn, gem.Effect) / 1000.0
            : AttackContactDelay(conn, weapon.AttackSeconds(CharacterPlayerStats(conn)), approach: false);
        int radius = SkillRadius(conn, skillStats, spell, gem.Effect);
        // A melee skill aimed far away still lands in front of the player.
        var centre = !spell && Dist(from, to) > radius + weapon.Range ? from : to;
        int action = conn.ActionCount;
        // A channel's hits (Cyclone, C→S 0x197) land at once, where the player is.
        if (!immediate)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.05, delay)));
            if (conn.ActionCount != action || conn.Closed) return;
        }
        double now = conn.Clock.Elapsed.TotalSeconds;
        List<(uint Id, SpawnedMonster M)> targets;
        lock (conn.Monsters)
            targets = [.. conn.Monsters.Where(e => !e.Value.Dead && e.Value.Life > 0 && Dist(e.Value.Walk.Position(now), centre) <= radius)
                .Select(e => (e.Key, e.Value))];
        var player = CharacterPlayerStats(conn);
        var stats = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int criticalChance = hitPlan.CriticalChance(conn.Pools.AttackCriticalStrikeChance,
            () => SpellCriticalStrike.Chance(Skills.SpellCritChance(gem.Effect, gem.Level), Stats, stats, skillStats));
        foreach (var (k, v) in skillStats) stats[k] = v;
        foreach (var (id, m) in targets)
        {
            DamagePacket hit = hitPlan.BuildHit(
                () => SpellDamage.WithEffectiveness(AttackHit(weapon, player), effectiveness)
                    .Convert(ConversionFor(conn, gem.Effect, gem.Level, attack: true)),
                spellBase => SpellDamage.Packet(spellBase, name => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0,
                        AddedEffectiveness(gem.Effect, gem.Level))
                    .Convert(ConversionFor(conn, gem.Effect, gem.Level, attack: false)));
            int damage = hit.RollTotal(Random.Shared);
            bool critical = spell
                ? criticalChance > 0 && SpellCriticalStrike.Hits(criticalChance, Random.Shared.Next(SpellCriticalStrike.RollSize))
                : conn.Pools.AttackCriticalStrikeChance > 0 && Random.Shared.Next(10_000) < conn.Pools.AttackCriticalStrikeChance;
            if (critical) damage = DamageModel.CriticalDamage(damage, spell ? conn.Pools.SpellCriticalStrikeMultiplier : conn.Pools.CriticalStrikeMultiplier);
            await HitMonster(remote, stream, ciphers, conn, id, m, damage, $"{gem.Name}{(critical ? " (critical)" : "")}", melee: !spell, sequence: sequence);
        }
        Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: {(spell ? "spell" : "attack")} hit {targets.Count} monster(s) within {radius} cells of ({centre.X}, {centre.Y}) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
    }

    // A non-gem bow shot (the ordinary attack's DefaultArrow): the row's ProjectileSpeed with the player's own
    // projectile-speed bonuses, resolved once at launch like a gem's projectile (ProjectileSpeed).
    static (string Id, float CellsPerSecond) BowArrow(ClientState conn, (string Id, int Speed) arrow)
    {
        var stats = CharacterPlayerStats(conn);
        int PlayerSpeedStat(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        return (arrow.Id, ProjectileSpeed.CellsPerSecond(arrow.Speed, PlayerSpeedStat, _ => 0,
            Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED")));
    }

    // A projectile's flight (LaunchProjectile): created just ahead of from, aimed at to, it flies on
    // to its range and stops at the first wall or monster on its path; a monster hit takes damage(monster).
    static async Task FlyProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (string Id, float CellsPerSecond) projectile, (int X, int Y) from, (int X, int Y) to, Func<SpawnedMonster, (int Damage, bool Critical)>? damageTo,
        int epoch, bool log = true, string? impact = null)
    {
        uint id;
        lock (conn.Monsters) id = conn.NextObjectId++;
        bool Active() => conn.ProjectileActive(epoch);
        if (!Active()) return;
        async Task<bool> SendVisual(byte[] bytes, string? line, Action? commit = null)
        {
            bool sent = false;
            while (!sent && Active())
            {
                await Send(remote, stream, ciphers, bytes, line,
                    still: () => sent = conn.TryCommitProjectileVisual(epoch, commit));
                if (!sent) await Task.Delay(16);
            }
            return sent;
        }
        double dx = to.X - from.X, dy = to.Y - from.Y, aim = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // One launch-time speed drives collision sweeps, the client's glide duration and the server's flight clock.
        float cellsPerSecond = projectile.CellsPerSecond;
        if (!float.IsFinite(cellsPerSecond) || cellsPerSecond < 0) return;
        // It leaves a little ahead of the caster (Game_PROJECTILE_AHEAD cells, default 1.5) and flies toward the clicked
        // point or monster and on to Game_PROJECTILE_RANGE cells (default 120, ~1.2 s at the real speed), clamped to the
        // area. The Positioned x/y are whole grid cells, so an offset under one cell rounds back onto the player's own
        // cell: the arrow then spawns inside the character and only becomes visible after it has flown out of the body.
        double ahead = double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_AHEAD"), inv, out var pa) ? pa : 1.5;
        double range = double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_RANGE"), inv, out var pr) && pr > 0 ? pr : 120;
        ahead = Math.Min(ahead, aim - 1);
        from = ((int)Math.Round(from.X + dx / aim * ahead), (int)Math.Round(from.Y + dy / aim * ahead));
        double length = Math.Max(range - ahead, 1);
        if (TerrainHeights.Size(conn.Layout.Id, conn.Seed) is (int w, int h))
            foreach (var (d, lo, hi) in new[] { (dx, -from.X, w - 1 - from.X), (dy, -from.Y, h - 1 - from.Y) })
                if (d != 0) length = Math.Min(length, Math.Max(0, (d > 0 ? hi : lo) / (d / aim)));
        cellsPerSecond = ProjectileSpeed.ForFlightDistance(cellsPerSecond, length + 1);
        // Static walls cap the route; monsters are swept from observed positions during flight, not selected at launch.
        double hitRadius = double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_HIT_RADIUS"), inv, out var phr) && phr > 0 ? phr : 4;
        to = ((int)Math.Round(from.X + dx / aim * length), (int)Math.Round(from.Y + dy / aim * length));
        bool hitWall;
        (to, hitWall) = ProjectileFlight.ClipWall(from, to,
            (x, y) => ProjectileGrid.Blocks(conn.Layout.Id, conn.Seed, x, y) == true);
        dx = to.X - from.X; dy = to.Y - from.Y;
        double cells = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        // Positioned rotation = atan2(dx, -dy) (the player's own moves, traced live 2026-10-18: facing (+15, -18) read 0.668)
        // Height: the captured terrain height plus Game_PROJECTILE_HEIGHT steps of 7.8125 world units (default 5, the
        // height the live server's arrow flew at: i32 -5, f32 +39.0625). The protocol carries no player z (C→S
        // 0x25/0x24 have only x,y, and the player's Positioned follows the terrain), so natural ground is the supported
        // case; without a capture the bare offset is used. A platform/doodad is not in the terrain grid (the hideout's
        // waypoint platform read z -78.37 while the terrain under it was 0..2 steps); that needs a server-side
        // world/surface height model (issue #182).
        int up = int.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_HEIGHT"), out int u) ? u : 5;
        int startHeight = (TerrainHeights.At(conn.Layout.Id, conn.Seed, from.X, from.Y) ?? 0) + up;
        // The protocol's height is the object's world z in steps, and world z points DOWN: the Positioned blob's i32 is
        // -z / 7.8125 and its f32 is z; 0x1ba's u32 is the same signed value. The live arrow's i32 -5 / f32 +39.0625 was
        // z = +39.0625 with its player at z = +78.125 (5 steps above the feet). In the hideout the player's feet are at
        // z = -78.37 (up), so the torso is at z = -117.4: i32 +15, f32 -117.1875, 0x1ba 0xfffffff1. Sending positive 15
        // put the arrow at z = +117 (below the platform) — the client reads the height as signed world z.
        int clientHeight = -startHeight;
        var obj = new ProjectileObject(projectile.Id, from.X, from.Y, (float)Math.Atan2(dx, -dy), Height: clientHeight);
        uint type = InstanceEntities.ObjectTypeHash(projectile.Id);
        // All observations below are taken with the monster lock held, including the initial observation when the
        // glide reaches the send gate. The game clock, not time spent waiting for the gate, starts the flight.
        List<ProjectileFlight.Target> Targets(double at) => [.. conn.Monsters
            .Where(m => !m.Value.Dead && m.Value.Life > 0).Select(m => new ProjectileFlight.Target(m.Key, m.Value.Walk.Position(at), m.Value.Walk))];
        ProjectileFlight? flight = null;
        try
        {
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type, obj.Blob(), headerU32: 0xffffffff), !log ? null :
                $"0x02b9 create projectile 0x{id:x} {projectile.Id} type=0x{type:x8} at ({from.X}, {from.Y})", still: () =>
                {
                    lock (conn.Monsters)
                    {
                        if (!Active()) return false;
                        conn.ProjectileObjects.Add(id);
                        return true;
                    }
                });
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), !log ? null :
                $"0x01a9 projectile 0x{id:x} enters wake zone", still: Active);
            await Send(remote, stream, ciphers, InstanceProtocol.MoveTo(id, to.X, to.Y, (float)(cells / cellsPerSecond), u28: unchecked((uint)clientHeight)), !log ? null :
                $"0x01ba projectile 0x{id:x} to ({to.X}, {to.Y}), {cells:0.0} cells at {cellsPerSecond:0.0} cells/s, height {startHeight} up (client z {clientHeight})" +
                (hitWall ? ", route ends at a wall" : ""), still: () =>
                {
                    lock (conn.Monsters)
                    {
                        if (!Active()) return false;
                        double at = conn.Clock.Elapsed.TotalSeconds;
                        flight = new(from, to, cellsPerSecond, at, hitRadius, hitWall, Targets(at));
                        return true;
                    }
                });
            if (flight is null) return;
            ProjectileFlight.Step step;
            while (true)
            {
                await Task.Delay(16);
                lock (conn.Monsters)
                {
                    if (!Active()) return;
                    if (conn.Paused) continue;
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    step = flight.Advance(now, Targets(now));
                }
                if (step.Finished) break;
            }
            if (step.TargetId != 0 || step.Wall)
            {
                int x = (int)Math.Round(step.Position.X), y = (int)Math.Round(step.Position.Y);
                // 0x1b7 copies the fixed pair directly: retain the same marker/world Z as creation, while
                // 0x1ba takes the signed world-Z steps. Do not resample terrain and jump the impact vertically.
                uint height = unchecked((uint)-clientHeight);
                float worldZ = clientHeight * 7.8125f;
                bool committed = false;
                SpawnedMonster? target = null;
                MonsterHit? hit = null;
                int damage = 0;
                bool critical = false;
                while (!committed && Active())
                {
                    // 0x1b7 relocates and cancels the existing glide (0x141e31200 -> 0x141e35250). Commit the hit
                    // under the same gate and short monster lock; a pause/transfer while waiting defers/cancels it.
                    await Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, x, y, height, worldZ), !log ? null :
                        $"0x01b7 projectile 0x{id:x} impact at ({x}, {y}) on {(step.Wall ? "wall" : $"monster 0x{step.TargetId:x}")} (t={step.At:0.000})", still: () =>
                        {
                            lock (conn.Monsters)
                            {
                                if (!Active() || conn.Paused) return false;
                                if (damageTo is not null && step.TargetId != 0 && conn.Monsters.TryGetValue(step.TargetId, out target) && !target.Dead && target.Life > 0)
                                {
                                    (damage, critical) = damageTo(target);
                                    hit = ApplyMonsterHitLocked(conn, target, damage);
                                }
                                committed = true;
                                return true;
                            }
                        });
                    if (!committed) await Task.Delay(16);
                }
                if (!committed) return;
                if (hit is { } applied && target is not null)
                    await NotifyMonsterHit(remote, stream, ciphers, conn, step.TargetId, target, applied, damage,
                        $"{projectile.Id}{(critical ? " (critical)" : "")}", log);
                impact = Environment.GetEnvironmentVariable("Game_PROJECTILE_IMPACT_AO") ?? impact ?? DefaultProjectileImpactAo;
                if (damageTo is not null && impact != "none")
                {
                    if (!await SendVisual(InstanceEntities.AnimatedModel(id, ProjectileObject.AnimatedComponentIndex, impact),
                        $"0x02ba projectile 0x{id:x} explodes ({impact})")) return;
                    if (!await WaitProjectileTime(conn, conn.Clock.Elapsed.TotalSeconds + 1.5, epoch)) return;
                }
            }
            await SendVisual(InstanceProtocol.RemoveObject(id), $"0x02bb remove projectile 0x{id:x}",
                () => conn.ProjectileObjects.Remove(id));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } // The old area's connection has gone.
    }

    // Release and terminal visual lifetimes share the game clock and area epoch with an active flight.
    static async Task<bool> WaitProjectileTime(ClientState conn, double until, int epoch)
    {
        while (true)
        {
            lock (conn.Monsters)
            {
                if (!conn.ProjectileActive(epoch)) return false;
                if (!conn.Paused && conn.Clock.Elapsed.TotalSeconds >= until) return true;
            }
            await Task.Delay(16);
        }
    }

    // A hit on a living monster: life is mutated once under Monsters, then notifications await outside the lock.
    static async Task HitMonster(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m,
        int damage, string by, bool log = true, bool melee = false, ushort sequence = 0)
    {
        MonsterHit? hit;
        lock (conn.Monsters) hit = ApplyMonsterHitLocked(conn, m, damage);
        if (hit is { } applied) await NotifyMonsterHit(remote, stream, ciphers, conn, id, m, applied, damage, by, log, melee, sequence);
    }

    // The short state mutation is shared with projectile impact; no awaits under Monsters.
    static MonsterHit? ApplyMonsterHitLocked(ClientState conn, SpawnedMonster m, int damage)
    {
        if (m.Dead || m.Life == 0) return null;
        (m.Life, bool starts) = TakeDamageLocked(m, damage);
        conn.LastEnemyHitAt = conn.Clock.Elapsed.TotalSeconds;
        if (m.Life == 0) { m.Aggro = false; m.Dead = true; }
        return new(m.Life, starts);
    }

    static async Task NotifyMonsterHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m,
        MonsterHit hit, int damage, string by, bool log = true, bool melee = false, ushort sequence = 0)
    {
        var (life, stageStarts) = hit;
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]),
                !log ? null : $"0x01b3 monster 0x{id:x} hide_mini_life_bar = 1");
        // The hit result for the client's damage/effect pipeline (0x1af): a melee hit plays the target's blood and the weapon
        // impact sound "bonk" locally, 0x1b1 stays authoritative on life. Spells and projectiles have their own impact and skip it.
        if (melee)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var (px, py) = conn.Walk.Position(now);
            var (mx, my) = m.Walk.Position(now);
            await Send(remote, stream, ciphers,
                InstanceProtocol.ActionHit(PlayerId, sequence, id, flags: 0x4201, kind: 0x03, angle: HitAngle(mx - px, my - py)),
                !log ? null : $"0x01af player hit on monster 0x{id:x} (skill, sequence 0x{sequence:x4})");
        }
        // The hit reaction: one of the rig's TakeHit clips and its SoundEvent (0x1b2), as in Attack.
        await SendFlinch(remote, stream, ciphers, conn, id, m, damage, log);
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life),
            !log && life > 0 ? null : $"0x01b1 monster 0x{id:x} hit by {by} for {damage}, life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
        if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, id, m);
        if (stageStarts) _ = RunBossStage(remote, stream, ciphers, conn, id, m);
    }

    // The hit reaction: one of the rig's TakeHit clips and its SoundEvent, S→C 0x1b2 kind 0 (see Attack and HitMonster).
    // A killing blow gets none: the client's Life component runs the death instead, and the flinch would only be cut off.
    // The gates (HitReaction) keep it a stun-like reaction rather than a loop; see that class. The monster is checked again
    // inside the send gate: a hit that kills it meanwhile marks it Dead before it sends its own 0x1b1, so the flinch either
    // goes out before the death or not at all.
    static async Task SendFlinch(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m, int damage, bool log)
    {
        HitAnimation? clip;
        lock (conn.Monsters) clip = HitReaction.TryFlinch(m, damage, conn.Clock.Elapsed.TotalSeconds, Random.Shared);
        if (clip is not { } flinch) return;
        await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, (uint)flinch.Index, 1f),
            !log ? null : $"0x01b2 monster 0x{id:x} flinch (animation {flinch.Index}, {flinch.Seconds:0.00} s)",
            still: () => { lock (conn.Monsters) return m.Life > 0 && !m.Dead; });
    }

    // A hit's damage off a monster's life (under the Monsters lock): the new life, and whether the hit starts its boss stage
    // (BossFights; the stage is then taken, so it runs once).
    static (uint Life, bool StageStarts) TakeDamageLocked(SpawnedMonster m, int damage)
    {
        // While the stage runs the boss cannot die: its life fills up at the end anyway.
        if (m.StageRunning) return ((uint)Math.Max(1, (long)m.Life - damage), false);
        var (life, starts) = BossFights.Damage(m.Life, m.MaxLife, damage, m.StageTaken ? null : m.Stage);
        if (starts) m.StageTaken = m.StageRunning = true;
        return (life, starts);
    }

    // A boss's stage (BossFights): it stops and uses its stage skill in place (0x1ab, the skill's animation: Hillock pulls
    // out his sword), then its Transitionable steps to the stage's state (0x2ba: the sword in his hand) and its life fills
    // up (0x1b1).
    static async Task RunBossStage(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m)
    {
        var stage = m.Stage!;
        (int X, int Y) at;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            at = m.Walk.Position(now);
            m.Walk.Order(at.X, at.Y, now);
            m.LastTarget = null;
            m.BusyUntil = now + stage.CastSeconds;
        }
        await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, StageSkillId, 0xffff, 0x0408),
            $"0x01ab monster 0x{id:x} ({m.Label}) starts its stage at {m.Life}/{m.MaxLife} life: skill 0x{StageSkillId:x4} (GrantedEffects {stage.SkillEffect}), {stage.CastSeconds:0.#} s");
        await Task.Delay(TimeSpan.FromSeconds(stage.CastSeconds));
        await WhilePaused(conn);
        uint life;
        lock (conn.Monsters)
        {
            m.StageRunning = false;
            if (m.Dead || conn.Closed) return;
            m.Life = life = m.MaxLife;
        }
        if (m.Variety?.ComponentsOrBase.ToList().IndexOf("Transitionable") is int t and >= 0)
            await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, (byte)t, stage.TransitionTo),
                $"0x02ba monster 0x{id:x} ({m.Label}) Transitionable → {stage.TransitionTo}");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life), $"0x01b1 monster 0x{id:x} ({m.Label}) life refilled to {life}");
        // The stage's action is over: 0x1ac ends it, so the client never keeps the boss in it.
        await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(id), $"0x01ac monster 0x{id:x} ({m.Label}) stage action ended");
    }

    // A boss whose variety has the big life bar (MonsterVarieties BossHealthBar) shows it at the top of the screen once it
    // aggroes: its StateMachine's boss_life_bar (Metadata/Monsters/BossLifeBar.ot) and aggro (Hillock's aggro effect) go to 1.
    static async Task ShowBossLifeBar(string remote, NetworkStream stream, ConnectionCiphers ciphers, uint id, SpawnedMonster m, ClientState conn)
    {
        if (m.Variety is not { } v || m.States is not { } states) return;
        int index = v.ComponentsOrBase.ToList().IndexOf("StateMachine");
        if (index < 0) return;
        states = states.Set("boss_life_bar", 1).Set("aggro", 1);
        lock (conn.Monsters) m.States = states;
        await Send(remote, stream, ciphers, InstanceEntities.StateMachineUpdate(id, (byte)index, states),
            $"0x02ba monster 0x{id:x} ({m.Label}) StateMachine: boss_life_bar 1, aggro 1");
    }

    // A monster died (its life reached 0 from the player's attack or skill): the player gains its experience when near
    // enough, it drops its items around where it fell (ItemDrops), and its corpse is removed 10 s later.
    static async Task MonsterKilled(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        (int X, int Y) at, player;
        bool alive;
        lock (conn.Monsters) { at = m.Walk.Position(now); player = conn.Walk.Position(now); alive = conn.PlayerLife > 0; }
        _ = Task.Delay(10_000).ContinueWith(async _ =>
        {
            bool removed;
            lock (conn.Monsters) removed = conn.Monsters.Remove(id);
            if (removed && !conn.Closed)
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove dead monster 0x{id:x}");
        });
        if (m.Variety is not { } variety || !m.Hostile) return;
        if (conn.Layout.Id == TutorialObjects.Strand && variety.Id == TutorialObjects.Zombie
            && CharacterSaves.Load(CharacterName(conn))?.Tutorial is { ZombieKilled: false })
        {
            CharacterSaves.ChangeTutorial(CharacterName(conn), t => t with { ZombieKilled = true });
            await DropTutorialGem(remote, stream, ciphers, conn, false, at.X, at.Y);
        }
        await SetQuestFlags(remote, stream, ciphers, conn, Quests.OnMonsterKilled(conn.Layout.Id, variety.Id));
        if (alive && Dist(at, player) <= ExperienceRange && Monsters.BaseExperience(variety, m.Level) is int baseXp)
        {
            uint xp = Progression.MonsterExperience(baseXp, variety.ExperienceMultiplier, m.Rarity, conn.Pools.Level, m.Level);
            // The gems gain their share of the kill only (not a quest's level or /add_xp), at the character's cap too.
            await GainGemExperience(remote, stream, ciphers, conn, xp);
            await GainExperience(remote, stream, ciphers, conn, xp, $"{m.Rarity} {m.Label} 0x{id:x}");
        }
        // A quest kill that levels the character up (a1q1: Hillock → at least level 2), wherever the player stands.
        if (Quests.LevelAfterKill(conn.Layout.Id, variety.Id) is int level and > 1 && level <= CharacterStats.LevelStarts.Count)
        {
            uint needed = CharacterStats.LevelStarts[level - 1], have;
            lock (conn.Monsters) have = conn.Experience;
            if (have < needed) await GainExperience(remote, stream, ciphers, conn, needed - have, $"quest: {m.Label} killed, level {level}");
        }
        await GainFlaskCharges(remote, stream, ciphers, conn, Flasks.KillCharges(m.Rarity, Environment.GetEnvironmentVariable("Game_FLASK_KILL_CHARGES")),
            $"{m.Rarity} {m.Label} 0x{id:x} killed");
        await DropLoot(remote, stream, ciphers, conn, m, at);
    }

    // The monster's items (ItemDrops.Roll) as WorldItem objects scattered around where it died, on walkable cells when the
    // area's walk map is known. Like dropped items they lie in the instance (InstanceStore).
    static async Task DropLoot(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpawnedMonster m, (int X, int Y) at)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        List<ItemDrops.Drop> drops = [.. Drops.Roll(m.Level, m.Rarity, Random.Shared)];
        // And a pile of gold (ItemDrops.Gold), picked up into the stash's gold.
        if (Drops.Gold(m.Level, m.Rarity, Random.Shared) is { } gold) drops.Add(gold);
        foreach (var drop in drops)
        {
            // A ring around the corpse, 3 to 6 cells out.
            double angle = Random.Shared.NextDouble() * Math.Tau, r = 3 + Random.Shared.NextDouble() * 3;
            int x = at.X + (int)Math.Round(Math.Cos(angle) * r), y = at.Y + (int)Math.Round(Math.Sin(angle) * r);
            if (conn.Layout.Walkable is { } walks && !walks(x, y)) (x, y) = at;
            var ground = new WorldItemObject(x, y, drop.Item);
            uint id = conn.Instance.Ground.Add(ground);
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
                $"0x02b9 create world item 0x{id:x} {drop.Type.Name} (item level {drop.Item.ItemLevel}) dropped by {m.Label} at ({x}, {y})");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        }
    }

    // /god: what it adds on top of the character's stats (GodStats) and pools (PoolsFor): 1000% increased attack and cast
    // speed, 100% of maximum life and 10 000 mana regenerated per second, 200% increased area of effect, +10 000 maximum
    // life and +1000 to each attribute.
}
