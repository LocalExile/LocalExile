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

    static Player Connection(string name)
    {
        var player = new Player(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = name, Experience = CharacterStats.ExperienceFor(100) },
            PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        TestRequirements.Waive(player);
        return player;
    }

    static Item Weapon(string id) => ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/" + id)!);

    // The main hand holds the gem; the off hand a weapon or a shield.
    static ushort Equip(Player player, string main, Item? off, string gem)
    {
        var items = Game.ItemsOf(player);
        foreach (var e in items.EquipmentSnapshot().ToList()) items.Remove(e.Row, e.Item.Id);
        items.Add(PlayerInventories.Weapon1, 0, 0, Weapon(main) with
        {
            Sockets = [new(SocketColour.White, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + gem)!))], Links = [1],
        });
        if (off is not null) items.Add(PlayerInventories.Offhand1, 0, 0, off);
        player.PlayerSkillsCache = null;
        Game.RecomputePoolsAndSpeed(player);
        return Game.GemSkillEffects(player).Single().Id;
    }

    static void Run(string name, Action<Player> body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"dual-wield-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var player = Connection(name);
        try { body(player); }
        finally { PlayerInventories.Forget(player.Character.Name!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path); }
    }

    static DamagePacket DefaultHit(Player player, AttackHand hand) =>
        Game.AttackHitConverted(Game.HandWeapon(player, hand), Game.CharacterPlayerStats(player), Game.MeleeAttack(player),
            Game.ConversionFor(player, -1, 1, attack: true), new(Game.HandAccuracy(player, hand)));

    [Fact]
    public void Two_weapons_alternate_by_the_clients_sequence_with_each_hands_own_inputs()
    {
        Run("DualWieldAlternation", player =>
        {
            // Sword in the main hand, claw in the off hand: Heavy Strike's weapon list (ActiveSkills) excludes claws.
            ushort heavy = Equip(player, Sword, Weapon(Claw), "SkillGemHeavyStrike");
            var stats = Game.CharacterPlayerStats(player);
            var main = Game.WeaponFor(player);
            var off = Game.OffhandWeaponFor(player)!;
            Assert.NotEqual(main.AttackMs, off.AttackMs);
            // Even sequences (stage 0) swing the main hand, odd ones the off hand: main, off, main, off.
            for (ushort sequence = 0x8002; sequence <= 0x8005; sequence++)
            {
                var hand = Game.DefaultAttackHand(player, sequence);
                Assert.Equal(sequence % 2 == 0 ? AttackHand.Main : AttackHand.Off, hand);
                var weapon = hand == AttackHand.Main ? main : off;
                var expected = Game.AttackHitConverted(weapon, stats, Game.MeleeAttack(player), Game.ConversionFor(player, -1, 1, attack: true),
                    new(Game.HandAccuracy(player, hand)));
                Assert.Equal((expected.MinTotal, expected.MaxTotal), (DefaultHit(player, hand).MinTotal, DefaultHit(player, hand).MaxTotal));
                double seconds = Game.DefaultAttackSeconds(player, hand, stats);
                Assert.Equal(hand == AttackHand.Main ? main.AttackSeconds(stats)
                    : DualWieldPanel.OffHandSeconds(off, SkillCatalog.Unarmed.OffHandAttackSpeedPct(main.IncreasedAttackSpeed(stats),
                        Game.WeaponProfileFor(player).LocalAttackSpeedPct)), seconds, 9);
                // Heavy Strike never swings the claw.
                Assert.Equal(AttackHand.Main, Game.AttackHandFor(player, heavy, sequence));
                Assert.Equal(Game.SupportedAttackSeconds(player, heavy), Game.SupportedAttackSeconds(player, heavy, sequence), 9);
            }
            Assert.True(Game.DefaultAttackSeconds(player, AttackHand.Off, stats) < Game.DefaultAttackSeconds(player, AttackHand.Main, stats));
            // The claw's own critical chance (7.3% base against the sword's 5%) goes with its swings.
            Assert.True(Game.HandAttackCriticalChance(player, AttackHand.Off, stats) > Game.HandAttackCriticalChance(player, AttackHand.Main, stats));
            // A use keeps the hand chosen at its start: the off-hand use stays off-hand after the claw is taken away.
            var items = Game.ItemsOf(player);
            items.Remove(PlayerInventories.Offhand1, items.Items(PlayerInventories.Offhand1).Single().Id);
            Assert.Equal(AttackHand.Off, Game.DefaultAttackHand(player, 0x8003));
            Assert.Equal(AttackHand.Main, Game.DefaultAttackHand(player, 0x8007));

            // Two weapons Heavy Strike fits: its uses alternate, each with that hand's time and damage.
            ushort both = Equip(player, Sword, Weapon(Axe), "SkillGemHeavyStrike");
            Assert.Equal(AttackHand.Main, Game.AttackHandFor(player, both, 0x8010));
            Assert.Equal(AttackHand.Off, Game.AttackHandFor(player, both, 0x8011));
            Assert.Equal(Game.SupportedOffHandAttackSeconds(player, both, Game.OffhandWeaponFor(player)!),
                Game.SupportedAttackSeconds(player, both, 0x8011), 9);
            var offSwing = Game.SupportedHitFor(player, both, suppress: true, weapon: Game.OffhandWeaponFor(player));
            var swung = Game.SupportedSwingHit(player, both, AttackHand.Off, w => Game.SupportedHitFor(player, both, suppress: true, weapon: w));
            Assert.Equal((offSwing.MinTotal, offSwing.MaxTotal), (swung.MinTotal, swung.MaxTotal));
            Assert.NotEqual(Game.SupportedHit(player, both).MaxTotal, swung.MaxTotal);
            // The server's own uses (no client sequence) do not alternate.
            Assert.Equal(AttackHand.Main, Game.AttackHandFor(player, both, 0xffff));

            // Lock order: a use's admission selects its hand while a contact asks for its own use's hand. The selection
            // must not hold the hand log's lock.
            using (var selecting = new ManualResetEventSlim()) using (var contactHolds = new ManualResetEventSlim())
            {
                bool admitted = false;
                var admission = Task.Run(() => AttackHands.Resolve(player, both, 0x8041, _ =>
                {
                    selecting.Set();
                    contactHolds.Wait(TimeSpan.FromSeconds(10));
                    admitted = Monitor.TryEnter(player.Area.Monsters, TimeSpan.FromSeconds(10));
                    if (admitted) Monitor.Exit(player.Area.Monsters);
                    return AttackHand.Off;
                }));
                var contact = Task.Run(() =>
                {
                    Assert.True(selecting.Wait(TimeSpan.FromSeconds(10)));
                    lock (player.Area.Monsters) { contactHolds.Set(); return Game.AttackHandFor(player, both, 0x8040); }
                });
                Assert.True(Task.WaitAll([admission, contact], TimeSpan.FromSeconds(30)));
                Assert.True(admitted);
                Assert.Equal((AttackHand.Off, AttackHand.Main), (admission.Result, contact.Result));
                Assert.Equal(AttackHand.Off, Game.AttackHandFor(player, both, 0x8041));   // recorded once selected
            }

            // A shield is not a weapon: nothing alternates and the off hand has no swing.
            Equip(player, Sword, ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!), "SkillGemHeavyStrike");
            for (ushort sequence = 0x8020; sequence <= 0x8023; sequence++)
                Assert.Equal(AttackHand.Main, Game.DefaultAttackHand(player, sequence));
            Assert.Null(Game.OffhandWeaponFor(player));
        });
    }

    [Fact]
    public void Both_hand_skills_add_the_two_weapons_hits_per_their_data()
    {
        Run("DualWieldBothHands", player =>
        {
            // Dual Strike (skill_double_hits_when_dual_wielding): every use hits with both, whatever its sequence.
            ushort dual = Equip(player, Claw, Weapon(Sword), "SkillGemDualStrike");
            var off = Game.OffhandWeaponFor(player)!;
            Assert.Equal(AttackHand.Both, Game.AttackHandFor(player, dual, 0x8002));
            Assert.Equal(AttackHand.Both, Game.AttackHandFor(player, dual, 0x8003));
            DamagePacket Hit(SkillCatalog.Unarmed? w) => Game.SupportedHitFor(player, dual, suppress: true, weapon: w);
            var mainHit = Hit(null);
            var offHit = Hit(off);
            var combined = Game.SupportedSwingHit(player, dual, AttackHand.Both, Hit);
            Assert.Equal(mainHit.MinTotal + offHit.MinTotal, combined.MinTotal, 9);
            Assert.Equal(mainHit.MaxTotal + offHit.MaxTotal, combined.MaxTotal, 9);
            // One use at the average of the two weapons' times (Path of Building's harmonic mean of the speeds).
            double mainSeconds = Game.SupportedAttackSeconds(player, dual), offSeconds = Game.SupportedOffHandAttackSeconds(player, dual, off);
            Assert.Equal((mainSeconds + offSeconds) / 2, Game.SupportedAttackSeconds(player, dual, AttackHand.Both), 9);
            int mainCritical = Game.SupportedCriticalChance(player, dual, AttackHand.Main);
            int offCritical = Game.SupportedCriticalChance(player, dual, AttackHand.Off);
            Assert.NotEqual(mainCritical, offCritical);   // the claw's base, then the sword's
            Assert.Equal((int)Math.Round((mainCritical + offCritical) / 2.0, MidpointRounding.AwayFromZero),
                Game.SupportedCriticalChance(player, dual, AttackHand.Both));

            // Cleave: both hits at once, at active_skill_merged_damage_+%_final_while_dual_wielding (-40 in its stat set).
            ushort cleave = Equip(player, Sword, Weapon(Axe), "SkillGemCleave");
            Assert.Equal(-40, Game.SupportedSkill(player, cleave).Stats[Stat.ActiveSkillMergedDamagePlusPctFinalWhileDualWielding]);
            DamagePacket CleaveHit(SkillCatalog.Unarmed? w) => Game.SupportedHitFor(player, cleave, suppress: true, weapon: w);
            var cleaved = Game.SupportedSwingHit(player, cleave, Game.AttackHandFor(player, cleave, 0x8031), CleaveHit);
            Assert.Equal(.6 * (CleaveHit(null).MaxTotal + CleaveHit(Game.OffhandWeaponFor(player)).MaxTotal), cleaved.MaxTotal, 6);
            // Its panel: both hits per use, at that rate.
            var rows = Game.PlayerSkills(player).Single(s => s.Id == cleave).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal((int)Math.Round(100 / Game.SupportedAttackSeconds(player, cleave, AttackHand.Both)),
                rows[Stat.HundredTimesAttacksPerSecond]);
            Assert.Equal(1, rows[Stat.SkillUsesOffHand]);
        });
    }
}
