using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>Which weapon a dual-wielded attack swings (AttackHands): the client's sequence parity alternates the hands
/// the skill's weapon restriction allows, and skill_double_hits_when_dual_wielding skills hit with both at once.</summary>
public sealed class DualWieldHandTests
{
    const string Sword = "OneHandWeapons/OneHandSwords/OneHandSword5", Claw = "OneHandWeapons/Claws/Claw1",
        Axe = "OneHandWeapons/OneHandAxes/OneHandAxe1";

    static ClientState Connection(string name)
    {
        var conn = new ClientState(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = name, PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        foreach (string attr in new[] { "strength", "dexterity", "intelligence" })
            conn.PlayerStats[Program.Stats.Key($"virtual_{attr}_for_requirements")!.Value] = 1000;
        return conn;
    }

    static Item Weapon(string id) => ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/" + id)!);

    // The main hand holds the gem; the off hand a weapon or a shield.
    static ushort Equip(ClientState conn, string main, Item? off, string gem)
    {
        var items = Game.ItemsOf(conn);
        foreach (var e in items.EquipmentSnapshot().ToList()) items.Remove(e.Row, e.Item.Id);
        items.Add(PlayerInventories.Weapon1, 0, 0, Weapon(main) with
        {
            Sockets = [new(SocketColour.White, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + gem)!))], Links = [1],
        });
        if (off is not null) items.Add(PlayerInventories.Offhand1, 0, 0, off);
        conn.PlayerSkillsCache = null;
        lock (conn.Monsters) Game.RecomputePoolsAndSpeed(conn);
        return Game.GemSkillEffects(conn).Single().Id;
    }

    static void Run(string name, Action<ClientState> body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"dual-wield-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection(name);
        try { body(conn); }
        finally { PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path); }
    }

    static DamagePacket DefaultHit(ClientState conn, AttackHand hand) =>
        Game.AttackHitConverted(Game.HandWeapon(conn, hand), Game.CharacterPlayerStats(conn), Game.MeleeAttack(conn),
            Game.ConversionFor(conn, -1, 1, attack: true), new(Game.HandAccuracy(conn, hand)));

    [Fact]
    public void Two_weapons_alternate_by_the_clients_sequence_with_each_hands_own_inputs()
    {
        Run("DualWieldAlternation", conn =>
        {
            // Sword in the main hand, claw in the off hand: Heavy Strike's weapon list (ActiveSkills) excludes claws.
            ushort heavy = Equip(conn, Sword, Weapon(Claw), "SkillGemHeavyStrike");
            var stats = Game.CharacterPlayerStats(conn);
            var main = Game.WeaponFor(conn);
            var off = Game.OffhandWeaponFor(conn)!;
            Assert.NotEqual(main.AttackMs, off.AttackMs);
            // Even sequences (stage 0) swing the main hand, odd ones the off hand: main, off, main, off.
            for (ushort sequence = 0x8002; sequence <= 0x8005; sequence++)
            {
                var hand = Game.DefaultAttackHand(conn, sequence);
                Assert.Equal(sequence % 2 == 0 ? AttackHand.Main : AttackHand.Off, hand);
                var weapon = hand == AttackHand.Main ? main : off;
                var expected = Game.AttackHitConverted(weapon, stats, Game.MeleeAttack(conn), Game.ConversionFor(conn, -1, 1, attack: true),
                    new(Game.HandAccuracy(conn, hand)));
                Assert.Equal((expected.MinTotal, expected.MaxTotal), (DefaultHit(conn, hand).MinTotal, DefaultHit(conn, hand).MaxTotal));
                double seconds = Game.DefaultAttackSeconds(conn, hand, stats);
                Assert.Equal(hand == AttackHand.Main ? main.AttackSeconds(stats)
                    : DualWieldPanel.OffHandSeconds(off, SkillCatalog.Unarmed.OffHandAttackSpeedPct(main.IncreasedAttackSpeed(stats),
                        Game.WeaponProfileFor(conn).LocalAttackSpeedPct)), seconds, 9);
                // Heavy Strike never swings the claw.
                Assert.Equal(AttackHand.Main, Game.AttackHandFor(conn, heavy, sequence));
                Assert.Equal(Game.SupportedAttackSeconds(conn, heavy), Game.SupportedAttackSeconds(conn, heavy, sequence), 9);
            }
            Assert.True(Game.DefaultAttackSeconds(conn, AttackHand.Off, stats) < Game.DefaultAttackSeconds(conn, AttackHand.Main, stats));
            // The claw's own critical chance (7.3% base against the sword's 5%) goes with its swings.
            Assert.True(Game.HandAttackCriticalChance(conn, AttackHand.Off, stats) > Game.HandAttackCriticalChance(conn, AttackHand.Main, stats));
            // A use keeps the hand chosen at its start: the off-hand use stays off-hand after the claw is taken away.
            var items = Game.ItemsOf(conn);
            items.Remove(PlayerInventories.Offhand1, items.Items(PlayerInventories.Offhand1).Single().Id);
            Assert.Equal(AttackHand.Off, Game.DefaultAttackHand(conn, 0x8003));
            Assert.Equal(AttackHand.Main, Game.DefaultAttackHand(conn, 0x8007));

            // Two weapons Heavy Strike fits: its uses alternate, each with that hand's time and damage.
            ushort both = Equip(conn, Sword, Weapon(Axe), "SkillGemHeavyStrike");
            Assert.Equal(AttackHand.Main, Game.AttackHandFor(conn, both, 0x8010));
            Assert.Equal(AttackHand.Off, Game.AttackHandFor(conn, both, 0x8011));
            Assert.Equal(Game.SupportedOffHandAttackSeconds(conn, both, Game.OffhandWeaponFor(conn)!),
                Game.SupportedAttackSeconds(conn, both, 0x8011), 9);
            var offSwing = Game.SupportedHitFor(conn, both, suppress: true, weapon: Game.OffhandWeaponFor(conn));
            var swung = Game.SupportedSwingHit(conn, both, AttackHand.Off, w => Game.SupportedHitFor(conn, both, suppress: true, weapon: w));
            Assert.Equal((offSwing.MinTotal, offSwing.MaxTotal), (swung.MinTotal, swung.MaxTotal));
            Assert.NotEqual(Game.SupportedHit(conn, both).MaxTotal, swung.MaxTotal);
            // The server's own uses (no client sequence) do not alternate.
            Assert.Equal(AttackHand.Main, Game.AttackHandFor(conn, both, 0xffff));

            // Lock order: a use's admission selects its hand (reading the skill under conn.Monsters) while a contact,
            // already holding conn.Monsters, asks for its own use's hand. The selection must not hold the hand log's lock.
            using (var selecting = new ManualResetEventSlim()) using (var contactHolds = new ManualResetEventSlim())
            {
                bool admitted = false;
                var admission = Task.Run(() => AttackHands.Resolve(conn, both, 0x8041, _ =>
                {
                    selecting.Set();
                    contactHolds.Wait(TimeSpan.FromSeconds(10));
                    admitted = Monitor.TryEnter(conn.Monsters, TimeSpan.FromSeconds(10));
                    if (admitted) Monitor.Exit(conn.Monsters);
                    return AttackHand.Off;
                }));
                var contact = Task.Run(() =>
                {
                    Assert.True(selecting.Wait(TimeSpan.FromSeconds(10)));
                    lock (conn.Monsters) { contactHolds.Set(); return Game.AttackHandFor(conn, both, 0x8040); }
                });
                Assert.True(Task.WaitAll([admission, contact], TimeSpan.FromSeconds(30)));
                Assert.True(admitted);
                Assert.Equal((AttackHand.Off, AttackHand.Main), (admission.Result, contact.Result));
                Assert.Equal(AttackHand.Off, Game.AttackHandFor(conn, both, 0x8041));   // recorded once selected
            }

            // A shield is not a weapon: nothing alternates and the off hand has no swing.
            Equip(conn, Sword, ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!), "SkillGemHeavyStrike");
            for (ushort sequence = 0x8020; sequence <= 0x8023; sequence++)
                Assert.Equal(AttackHand.Main, Game.DefaultAttackHand(conn, sequence));
            Assert.Null(Game.OffhandWeaponFor(conn));
        });
    }

    [Fact]
    public void Both_hand_skills_add_the_two_weapons_hits_per_their_data()
    {
        Run("DualWieldBothHands", conn =>
        {
            // Dual Strike (skill_double_hits_when_dual_wielding): every use hits with both, whatever its sequence.
            ushort dual = Equip(conn, Claw, Weapon(Sword), "SkillGemDualStrike");
            var off = Game.OffhandWeaponFor(conn)!;
            Assert.Equal(AttackHand.Both, Game.AttackHandFor(conn, dual, 0x8002));
            Assert.Equal(AttackHand.Both, Game.AttackHandFor(conn, dual, 0x8003));
            DamagePacket Hit(SkillCatalog.Unarmed? w) => Game.SupportedHitFor(conn, dual, suppress: true, weapon: w);
            var mainHit = Hit(null);
            var offHit = Hit(off);
            var combined = Game.SupportedSwingHit(conn, dual, AttackHand.Both, Hit);
            Assert.Equal(mainHit.MinTotal + offHit.MinTotal, combined.MinTotal, 9);
            Assert.Equal(mainHit.MaxTotal + offHit.MaxTotal, combined.MaxTotal, 9);
            // One use at the average of the two weapons' times (Path of Building's harmonic mean of the speeds).
            double mainSeconds = Game.SupportedAttackSeconds(conn, dual), offSeconds = Game.SupportedOffHandAttackSeconds(conn, dual, off);
            Assert.Equal((mainSeconds + offSeconds) / 2, Game.SupportedAttackSeconds(conn, dual, AttackHand.Both), 9);
            int mainCritical = Game.SupportedCriticalChance(conn, dual, AttackHand.Main);
            int offCritical = Game.SupportedCriticalChance(conn, dual, AttackHand.Off);
            Assert.NotEqual(mainCritical, offCritical);   // the claw's base, then the sword's
            Assert.Equal((int)Math.Round((mainCritical + offCritical) / 2.0, MidpointRounding.AwayFromZero),
                Game.SupportedCriticalChance(conn, dual, AttackHand.Both));

            // Cleave: both hits at once, at active_skill_merged_damage_+%_final_while_dual_wielding (-40 in its stat set).
            ushort cleave = Equip(conn, Sword, Weapon(Axe), "SkillGemCleave");
            uint merged = Program.Stats.Key("active_skill_merged_damage_+%_final_while_dual_wielding")!.Value;
            Assert.Equal(-40, Game.SupportedSkill(conn, cleave).Stats[merged]);
            DamagePacket CleaveHit(SkillCatalog.Unarmed? w) => Game.SupportedHitFor(conn, cleave, suppress: true, weapon: w);
            var cleaved = Game.SupportedSwingHit(conn, cleave, Game.AttackHandFor(conn, cleave, 0x8031), CleaveHit);
            Assert.Equal(.6 * (CleaveHit(null).MaxTotal + CleaveHit(Game.OffhandWeaponFor(conn)).MaxTotal), cleaved.MaxTotal, 6);
            // Its panel: both hits per use, at that rate.
            var rows = Game.PlayerSkills(conn).Single(s => s.Id == cleave).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal((int)Math.Round(100 / Game.SupportedAttackSeconds(conn, cleave, AttackHand.Both)),
                rows[Program.Stats.Key("hundred_times_attacks_per_second")!.Value]);
            Assert.Equal(1, rows[Program.Stats.Key("skill_uses_off_hand")!.Value]);
        });
    }
}
