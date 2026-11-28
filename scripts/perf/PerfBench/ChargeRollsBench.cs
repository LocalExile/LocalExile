using System.Diagnostics;
using GameServer.Core.Protocol.Instance;

// The "charges" scenario: one charge roll (ChargeGains.Roll, as the charge ticker calls it) per event kind, for a
// character with no charge stat, one with a few, and one with every charge stat set; then compiling the world's effect
// rules once for a snapshot (ItemEffectRules, at every world-data load). Time and allocation per call.
static class ChargeRollsBench
{
    public static void Run(Action<string, string, double, string> record)
    {
        uint[] all = [.. ChargeGains.Rules.Select(r => r.Stat).Distinct()];
        var few = new Dictionary<uint, int>
        {
            [Stat.AddPowerChargeOnCriticalStrikePct] = 30, [Stat.ChanceToGainEnduranceChargeWhenHitPct] = 20,
            [Stat.AddFrenzyChargeOnKillPctChance] = 15, [Stat.ChanceToGainFrenzyChargeOnBlockPct] = 25,
        };
        var every = all.ToDictionary(s => s, s => (int)(s % 90) + 5);
        (string Name, Func<uint, int> Get)[] characters =
            [("no charge stats", static _ => 0), ("a few charge stats", few.GetValueOrDefault), ("every charge stat", every.GetValueOrDefault)];
        var skill = new Dictionary<uint, int> { [Stat.AddPowerChargeOnCriticalStrikePct] = 20 };
        (string Name, ChargeEvent Event)[] events =
        [
            ("kill", new ChargeEvent.Kill(MonsterRarity.Rare, Frozen: true, Poisons: 3, Skill: skill.GetValueOrDefault)),
            ("hit", new ChargeEvent.Hit(true, false, true, MonsterRarity.Rare, "Cleave", skill.GetValueOrDefault, Bleeding: true)),
            ("when hit", new ChargeEvent.WhenHit(true)),
            ("block", new ChargeEvent.Block(false)),
            ("stun", new ChargeEvent.Stun(true, skill.GetValueOrDefault)),
            ("skill use", new ChargeEvent.SkillUse(false, true, false)),
        ];
        WeaponProfile weapon = new() { Shield = true, OneHand = true, Melee = true };
        const int calls = 200_000;
        foreach (var (who, get) in characters)
            foreach (var (name, e) in events)
            {
                var rng = new Random(1);
                for (int i = 0; i < 20_000; i++) ChargeGains.Roll(e, get, weapon, rng);
                var times = new List<double>();
                long bytes = 0;
                for (int round = 0; round < 5; round++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    var clock = Stopwatch.StartNew();
                    for (int i = 0; i < calls; i++) ChargeGains.Roll(e, get, weapon, rng);
                    clock.Stop();
                    bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / calls;
                    times.Add(clock.Elapsed.TotalNanoseconds / calls);
                }
                times.Sort();
                record("charges", $"{name}, {who}", times[2], "ns/roll");
                record("charges", $"{name}, {who}, allocated", bytes, "B/roll");
            }
        var world = GameData.Current;
        for (int i = 0; i < 3; i++) ItemEffectRules.Validate(world with { });
        var compile = new List<double>();
        for (int i = 0; i < 9; i++)
        {
            var copy = world with { };
            var clock = Stopwatch.StartNew();
            ItemEffectRules.Validate(copy);
            compile.Add(clock.Elapsed.TotalMilliseconds);
        }
        compile.Sort();
        record("charges", $"compile {world.FxRules.Count} rules", compile[4], "ms");
    }
}
