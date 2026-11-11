using System.Buffers.Binary;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    static async Task TwisterFixture(ClientState template, ModuleLifetime lifetime)
    {
        string character = "TwisterRuntime" + Guid.NewGuid().ToString("N");
        PlayerInventories.SetClass(character, 1);
        CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
            Experience: CharacterStats.ExperienceFor(100)));
        var schedule = new CatClockSchedule { At = 100 };
        var priorContext = Game.AreaUpdateContext.Value;
        var updates = new PlayerUpdatesState(lifetime, schedule);
        Game.AreaUpdateContext.Value = updates;
        using var wire = new Wire();
        var conn = new ClientState(template.Layout, 1, 1)
        {
            Character = character, CharacterId = Guid.NewGuid(), Account = "twister-runtime", ClassRow = 1,
            League = "Standard", LeagueForm = "Standard", Instance = new(), PlayerLife = 1,
            PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
            Experience = CharacterStats.ExperienceFor(100), LeagueModule = template.LeagueModule,
            Wire = ("twister", wire.Stream, wire.Ciphers), Pools = new(100, 1, 1, 0, 0),
        };
        Game.ProjectileSession? session = null;
        try
        {
            var inventory = Game.ItemsOf(conn);
            foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1,
                         PlayerInventories.Helmet, PlayerInventories.BodyArmour, PlayerInventories.Gloves,
                         PlayerInventories.Boots, 5, 6, 7, 10 })
                foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);
            var uniques = new UniqueCatalog(Program.Items);
            inventory.Add(5, 0, 0, uniques.Create("Astramentis", new Random(504))!);
            inventory.Add(6, 0, 0, uniques.Create("Le Heup of All", new Random(504))!);
            inventory.Add(7, 0, 0, uniques.Create("Le Heup of All", new Random(505))!);
            inventory.Add(PlayerInventories.Boots, 0, 0, uniques.Create("Saqawal's Talons", new Random(504))!);
            conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = conn.Pools.Mana;
            conn.Walk.Teleport(100, 100, schedule.At);
            var avian = Assert.Single(Game.GemSkillEffects(conn), g => Program.Skills.EffectId(g.Effect) == "AspectOfTheAvian");
            Assert.True(await Game.ToggleAura("twister", wire.Stream, wire.Ciphers, conn, avian.Id, avian.Effect,
                avian.Name, Game.SupportedReservationCost(conn, avian.Id)!.Value, avian.Level, avian.Quality));
            Assert.Empty(conn.Twister.Pending);
            // Equipping Flock while a phase is already present does not retroactively gain that phase.
            var flock = inventory.Add(PlayerInventories.Helmet, 0, 0, uniques.Create("Saqawal's Flock", new Random(504))!);
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            Assert.False(Game.IsInactive(conn, flock.Id));
            Assert.Empty(conn.Twister.Pending);
            var twister = Assert.Single(Game.GemSkillEffects(conn), g => Program.Skills.EffectId(g.Effect) == "Twister");
            Assert.Equal(20, twister.Level);
            Assert.Equal("Metadata/Projectiles/SandTornadoSpark", Program.Skills.ProjectileFor(twister.Effect)!.Value.Id);
            Assert.Equal(100, Program.Skills.CooldownMs(twister.Effect, twister.Level));
            Assert.Contains(Game.PlayerSkills(conn).Single(s => s.Id == twister.Id).Stats!,
                s => s.Key == Program.Stats.Key("skill_behaviour_variation") && s.Value == 4);
            Assert.True(Game.TryBeginSkillCooldownUse(conn, avian.Id, avian.Effect, null, true, () => { }, out _));
            int action = conn.ActionCount;
            uint mana = conn.PlayerMana;
            bool sideEffect = false;
            Assert.False(Game.TryBeginSkillCooldownUse(conn, twister.Id, twister.Effect,
                Game.CooldownFor(conn, twister.Effect, twister.Level, 0), true, () => sideEffect = true, out _));
            Assert.False(sideEffect);
            Assert.False(await Game.CastGemSkill("twister", wire.Stream, wire.Ciphers, conn, twister.Id, (100, 100), (110, 100)));
            Assert.Equal(action, conn.ActionCount);
            Assert.Equal(mana, conn.PlayerMana);
            Assert.Empty(conn.SingleUseCooldowns);
            schedule.At = conn.Reservations.AvianDeadline;
            Game.AvianReconcileLocked(conn, schedule.At);
            var gain = Assert.Single(conn.Twister.Pending);
            conn.Twister.Pending.Clear();
            Game.AvianReconcileLocked(conn, schedule.At);
            Assert.Empty(conn.Twister.Pending);
            // A duration-only edit keeps the current phase and never earns a second trigger.
            var durationInput = new Dictionary<uint, int>(Game.CharacterStatSources(conn).Stats);
            durationInput[Program.Stats.Key("avians_flight_duration_ms_+")!.Value] = 500;
            Game.AvianReconcileLocked(conn, schedule.At, durationInput);
            Assert.Empty(conn.Twister.Pending);
            session = new("twister-fixed", wire.Stream, wire.Ciphers, conn);
            conn.ProjectileRuntime = session;
            session.Runtime.AdvanceTo(ProjectileRuntime.Time(schedule.At));
            conn.Monsters[0x504A] = new("twister target", new(105, 100, 1), 100, 8, 100000);
            byte[] packet = Game.AdmitTwisterGainLocked(conn, gain, session)!;
            Assert.NotNull(packet);
            Assert.Equal(InstanceProtocol.InstantActionId, BinaryPrimitives.ReadUInt16BigEndian(packet));
            Assert.Equal(twister.Id, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12)));
            Assert.Equal(0x80, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(16)));
            var projectile = Assert.Single(session.Runtime.Projectiles);
            Assert.Equal(-1, projectile.Profile.Pierce);
            Assert.Equal(ProjectileHitScope.Projectile, projectile.Profile.HitRule.Scope);
            Assert.IsType<SparkLegs>(projectile.Profile.Legs!(0));
            Assert.Equal(ProjectileRuntime.Time(2.5), projectile.ExpiresAt - session.Runtime.Now);
            Assert.True(Assert.IsType<ProjectileOffense>(projectile.Use.Payload).Hit.MinTotal > 0);
            Assert.Equal(action, conn.ActionCount);
            Assert.Equal(mana, conn.PlayerMana);
            Assert.Null(Game.AdmitTwisterGainLocked(conn, gain, session));
            Game.QueueTwisterGainLocked(conn);
            var cooldownGain = conn.Twister.Pending.Dequeue();
            Assert.Null(Game.AdmitTwisterGainLocked(conn, cooldownGain, session));
            session.Runtime.AdvanceTo(session.Runtime.Now + ProjectileRuntime.Time(.101));
            Assert.NotNull(Game.AdmitTwisterGainLocked(conn, cooldownGain, session));
            // Only one final observed phase is queued after a long skip, even when it returns to the same family.
            schedule.At += 10000;
            Game.AvianReconcileLocked(conn, schedule.At);
            Assert.Single(conn.Twister.Pending);
            var stale = conn.Twister.Pending.Dequeue();
            uint spellsOff = Program.Stats.Key("your_spells_are_disabled")!.Value;
            conn.PlayerStats[spellsOff] = 1;
            Assert.Null(Game.AdmitTwisterGainLocked(conn, stale, session));
            conn.PlayerStats.Remove(spellsOff);
            Assert.False(Game.TwisterGainCurrentLocked(conn, stale with { Epoch = conn.ProjectileEpoch + 1 }));
            Assert.False(Game.TwisterGainCurrentLocked(conn, stale with { Owner = new object() }));
            inventory.Remove(PlayerInventories.Helmet, flock.Id);
            Game.AvianReconcileLocked(conn, schedule.At);
            Assert.False(Game.TwisterGainCurrentLocked(conn, stale));
            inventory.Add(PlayerInventories.Helmet, 0, 0, flock.Item);
            Game.AvianReconcileLocked(conn, schedule.At);
            Assert.False(Game.TwisterGainCurrentLocked(conn, stale));
            Game.QueueTwisterGainLocked(conn);
            var deathGain = conn.Twister.Pending.Dequeue();
            conn.PlayerLife = 0;
            Assert.False(Game.TwisterGainCurrentLocked(conn, deathGain));
            Assert.Empty(conn.Twister.Sources);
            Assert.Empty(conn.PlayerStats);
        }
        finally
        {
            conn.Closed = true;
            if (session is not null) { session.Invalidate(); await session.OutputCompleted; }
            Game.AreaUpdateContext.Value = priorContext;
            PlayerInventories.Forget(character);
        }
    }
}
