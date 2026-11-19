using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MinionSupportExecutionTests
{
    static void Invoke(string name, params object[] arguments) =>
        typeof(Game).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments);

    static async Task<GolemState.Golem> Skeleton(AnimatedAdmissionTests.Fixture f, params string[] supports)
    {
        var items = Game.ItemsOf(f.Conn);
        foreach (var p in items.Items(PlayerInventories.Weapon1)) items.Remove(PlayerInventories.Weapon1, p.Id);
        Item Gem(string name)
        {
            var type = Program.Items.FindGem(name)!.Value;
            return ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index,
                GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20) };
        }
        var names = new[] { "Summon Skeletons" }.Concat(supports).ToArray();
        items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Gnarled Branch")!) with
        { Sockets = names.Select(n => new ItemSocket(SocketColour.White, Gem(n))).ToArray(), Links = [(byte)names.Length] });
        ushort source = Game.GemSkillEffects(f.Conn).Single().Id;
        uint id = await Game.SummonFighter(f.Conn, source,
            Game.SummonData.Find("SummonSkeletons")!, (100, 100));
        var fighter = f.Conn.Summons.Fighters[id];
        fighter.Monster.Walk.Teleport(100, 100, 0);
        Invoke("RefreshGolemLocked", f.Conn, fighter, Game.CharacterPlayerStats(f.Conn), (double?)0d);
        fighter.Monster.Mods = fighter.Monster.Mods with { Effects = new(new Dictionary<string, int>
            { ["accuracy_rating"] = 1000000, ["never_crit"] = 1,
                ["support_melee_splash_damage_+%_final_for_splash"] = fighter.Own.GetValueOrDefault(Stat.SupportMeleeSplashDamagePlusPctFinalForSplash) }) };
        f.Conn.Walk.Teleport(100, 100, 0);
        return fighter;
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        internal double Time;
        public double Now(Player conn) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping)
        { stopping.ThrowIfCancellationRequested(); Time += milliseconds / 1000d; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Multistrike_repeats_after_recovery_and_ruthless_counts_uses_per_minion()
    {
        using var f = new AnimatedAdmissionTests.Fixture();
        var time = new Clock(); var before = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, time);
        try
        {
            var fighter = await Skeleton(f, "Multistrike Support", "Ruthless Support");
            fighter.Gear["global_minimum_added_chaos_damage"] = fighter.Gear["global_maximum_added_chaos_damage"] = 100;
            fighter.Gear["base_deal_no_physical_damage"] = 1;
            fighter.Own[Stat.BaseChanceToPoisonOnHitPct] = 100;
            fighter.Abilities = Game.GolemAbilities(f.Conn, fighter.Monster.Variety!, fighter.Monster.Level, fighter, fighter.Player);
            var ability = fighter.Abilities.First(a => !a.Attack.Spell && Program.Skills.IsMelee(a.Effect));
            var enemy = new SpawnedMonster("target", new(105, 100, 0), 100, 3, 1000000) { Hostile = true };
            f.Conn.Monsters[999] = enemy;
            double Poison(GolemState.Ability hit)
            {
                var target = new SpawnedMonster("poison target", new(105, 100, 0), 100, 3, 100000) { Hostile = true };
                Invoke("MinionAilmentsLocked", f.Conn, fighter, hit, 998u, target, false, time.Time);
                return target.Poisons.TotalDps(time.Time);
            }
            double originalPoison = Poison(ability);
            Assert.True(originalPoison > 0);
            double original = ability.Attack.Damage.MaxTotal;
            Assert.True(original > 0);
            int Own(string n) => fighter.Own.GetValueOrDefault(Program.Stats.Key(n)!.Value);
            Assert.Equal(2, Own("base_melee_attack_repeat_count"));
            for (int use = 0; use < 3; use++)
            {
                Invoke("GolemReserveLocked", f.Conn, fighter, ability, 999u, enemy, (105, 100), time.Time);
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    Invoke("GolemActionRecords", f.Conn, new PlayerMinions.Output([], null, fighter.Id, fighter.Monster,
                        PlayerMinions.OutputKind.GolemAction, Action: fighter.Monster.ActionGeneration));
                    var pending = fighter.Pending!;
                    double ruthless = use == 2 ? 1 + Own("support_ruthless_big_hit_damage_+%_final") / 100d : 1;
                    double extra = repeat == 0 ? 1 : 1 + Own(repeat == 1 ? "multistrike_damage_+%_final_on_first_repeat"
                        : "multistrike_damage_+%_final_on_second_repeat") / 100d;
                    Assert.Equal(original * ruthless * extra, pending.Ability.Attack.Damage.MaxTotal, 5);
                    double ailmentRuthless = use == 2 ? 1 + Own("support_ruthless_blow_ailment_damage_from_melee_hits_+%_final") / 100d : 1;
                    Assert.Equal(originalPoison * ailmentRuthless * extra, Poison(pending.Ability), 5);
                    Assert.Equal(repeat, pending.RepeatIndex);
                    time.Time = pending.At;
                    uint life = enemy.Life;
                    Game.GolemStepLocked(f.Conn, fighter, time.Time);
                    Assert.True(enemy.Life < life);
                    if (repeat < 2)
                    {
                        Assert.Null(fighter.Pending);
                        Assert.NotNull(fighter.Repeat);
                        time.Time = fighter.Monster.BusyUntil;
                        Game.GolemStepLocked(f.Conn, fighter, time.Time);
                        Assert.NotNull(fighter.Pending);
                    }
                    else { Assert.Null(fighter.Repeat); time.Time = fighter.Monster.BusyUntil; }
                }
            }
        }
        finally { Game.AreaUpdateContext.Value = before; }
    }

    [Fact]
    public async Task Melee_splash_hits_nearby_enemies_once_with_its_own_multiplier()
    {
        using var f = new AnimatedAdmissionTests.Fixture();
        var fighter = await Skeleton(f, "Melee Splash Support");
        var original = fighter.Abilities.First(a => !a.Attack.Spell && Program.Skills.IsMelee(a.Effect));
        var ability = original with { Attack = original.Attack with { Damage = DamagePacket.Of(DamageType.Physical, 100, 100) } };
        fighter.Abilities = [ability];
        var enemies = new[] { (999u, 105, 100, true), (998u, 108, 100, true), (997u, 160, 100, true), (996u, 108, 100, false) };
        foreach (var (id, x, y, hostile) in enemies)
            f.Conn.Monsters[id] = new("target", new(x, y, 0), 100, 3, 10000) { Hostile = hostile };
        Invoke("GolemAbilityContactLocked", f.Conn, fighter,
            new GolemState.Action(ability, 999, f.Conn.Monsters[999], (105, 100), 0, fighter.Monster.ActionGeneration), 0d);
        Assert.Equal(9900u, f.Conn.Monsters[999].Life);
        Assert.Equal(9940u, f.Conn.Monsters[998].Life);
        Assert.Equal(10000u, f.Conn.Monsters[997].Life);
        Assert.Equal(10000u, f.Conn.Monsters[996].Life);
    }
}
