using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Village.Mapping;

/// <summary>
/// The mapping line of Kingsmarch: the devices that run the maps, the runners that work them and what the finished
/// maps pay. The state of the line lives in the mapping save; the treasury is the town treasury of
/// <see cref="KingsmarchPort"/>.
/// </summary>
public static class MappingEngine
{
    /// <summary>The number of mapping devices a town owns.</summary>
    public const int DeviceCount = 3;

    /// <summary>The number of candidates the panel offers.</summary>
    public const int CandidateCount = 3;

    /// <summary>The chance a finished map takes a runner hostage.</summary>
    private const double KidnapChance = 0.03;

    /// <summary>How many hours a hostage is held before the bandits keep the runner.</summary>
    private const double KidnapHours = 24;

    /// <summary>The cap of the time one map takes: a day.</summary>
    private const double MaxMinutesPerMap = 1440;

    /// <summary>The hire cost of a candidate.</summary>
    private const int HireBaseCost = 2000;

    /// <summary>The hire cost of one specialty rank.</summary>
    private const int HireRankCost = 500;

    /// <summary>What the specialty costs on the hire.</summary>
    private const double SpecialtyCostMultiplier = 1.5;

    /// <summary>The wage of a runner: its level and its rank.</summary>
    private const int WageBase = 20;

    /// <summary>What one level of a runner costs on its wage.</summary>
    private const int WageLevel = 10;

    /// <summary>What one rank of a runner costs on its wage.</summary>
    private const int WageRank = 15;

    /// <summary>The highest specialty rank a candidate can have.</summary>
    private const int MaxCandidateRank = 9;

    /// <summary>The chance in a hundred that a candidate has the Atlas Runner specialty. A candidate without it
    /// cannot work a device, so most of them carry the specialty.</summary>
    private const int SpecialtyChance = 60;

    /// <summary>The map rarity bands: the tier a band starts at, its rarity byte and its name.</summary>
    private static readonly (int Tier, byte Rarity, string Name)[] Bands =
    [
        (1, 0, "white"),
        (6, 1, "yellow"),
        (11, 2, "red"),
        (17, 3, "purple"),
    ];

    /// <summary>The special content a map may carry.</summary>
    private static readonly string[] AllowedSpecials = ["Boss", "Guardian", "Eldritch", "Maven", "Season"];

    /// <summary>The names the recruits get.</summary>
    private static readonly string[] CandidateNames =
    [
        "Aldric", "Brenna", "Cassia", "Doran", "Elara", "Fenn", "Garrick", "Hesta", "Ivo", "Joren",
        "Kael", "Lira", "Mabon", "Orin", "Petra", "Quill", "Rhea", "Soren", "Tamsin", "Vela",
    ];

    /// <summary>The classes the recruits come from.</summary>
    private static readonly string[] CandidateClasses =
    [
        "Ranger", "Scholar", "Warrior", "Trader", "Hunter", "Mercenary", "Mage", "Warden",
    ];

    private static readonly ConcurrentDictionary<string, double> LastWrite = new();
    private static readonly object MapGate = new();
    private static HashSet<uint>? _uniqueMaps;

    /// <summary>
    /// Moves every device forward: the maps that finished pay out, a runner that died is lost and a runner that was
    /// taken hostage waits for an answer. The wages of the elapsed hours are paid.
    /// </summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="now">The current unix time in seconds. A value that cannot be a unix time (the player's tick hands
    /// over the seconds since the last tick) is replaced by the engine's own clock.</param>
    /// <returns>A short report of what the line did.</returns>
    public static string Advance(string who, double now)
    {
        try
        {
            var save = MappingStore.Of(who);
            // The town owns three devices (the client's village_number_of_map_devices): they are made with the line.
            bool changed = Ensure(save);
            double clock = Clock(now);
            double last = save.LastAdvance;
            if (last <= 0)
            {
                save.LastAdvance = clock;
                Write(save, who, true);
                return "The mapping line is ready.";
            }

            double elapsed = clock - last;
            if (elapsed < 0) elapsed = 0;
            var rng = Random.Shared;
            var notes = new List<string>();
            foreach (var device in save.Devices) changed |= AdvanceDevice(who, save, device, clock, rng, notes);

            int hours = (int)(elapsed / 3600.0);
            if (hours > 0)
            {
                int wage = Wages(save);
                if (wage > 0)
                {
                    int due = wage * hours;
                    bool paid = KingsmarchPort.PayWages(who, due);
                    notes.Add(paid
                        ? $"The wages of {hours} hour(s) cost {due} gold."
                        : $"The wages of {hours} hour(s) cost {due} gold; the treasury is short of it.");
                }
                // Only the paid hours move the clock on, so the rest of the hour is not lost.
                save.LastAdvance = last + hours * 3600.0;
                changed = true;
            }

            Write(save, who, changed);
            return notes.Count == 0 ? "The mapping line had nothing to do." : string.Join(" ", notes);
        }
        catch (Exception ex)
        {
            return "The mapping line did not move: " + ex.Message;
        }
    }

    /// <summary>Opens the first map of a device's queue.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="deviceId">The device to open.</param>
    /// <returns>A short report of what happened.</returns>
    public static string Start(string who, int deviceId)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            var device = Device(save, deviceId);
            if (device is null) return NoDevice(deviceId);
            if (device.Kidnapped) return Waiting(device);
            if (device.Current is not null) return $"Device {device.Id} is already running {device.Current.Entry.Name}.";
            if (HasHostage(save)) return "A runner of another device is hostage; no device opens a map until you answer.";
            if (device.Queue.Count == 0) return $"Device {device.Id} has no map queued.";
            if (Workers(device, save).Count == 0) return $"Device {device.Id} has no runner; assign one first.";
            if (!StartNext(save, device, Now())) return $"Device {device.Id} did not open a map.";
            Write(save, who, true);
            var run = device.Current!;
            return $"Device {device.Id} opens {run.Entry.Name}: {run.Completion}% in {Minutes(run.Minutes)}.";
        }
        catch (Exception ex)
        {
            return "The device did not open a map: " + ex.Message;
        }
    }

    /// <summary>Stops a device after the map it is in, or lets it run on when it already stops.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="deviceId">The device to stop or to let run.</param>
    /// <returns>A short report of what happened.</returns>
    public static string Suspend(string who, int deviceId)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            var device = Device(save, deviceId);
            if (device is null) return NoDevice(deviceId);
            device.SuspendAfterCurrent = !device.SuspendAfterCurrent;
            string report;
            if (device.SuspendAfterCurrent)
            {
                report = $"Device {device.Id} stops after the map it is in.";
            }
            else
            {
                report = $"Device {device.Id} runs on.";
                // A device that was waiting for a map takes the next one of its queue again.
                if (device.Current is null && device.Queue.Count > 0 && !device.Kidnapped && !HasHostage(save)
                    && StartNext(save, device, Now()))
                {
                    var run = device.Current!;
                    report += $" It opens {run.Entry.Name}: {run.Completion}% in {Minutes(run.Minutes)}.";
                }
            }
            Write(save, who, true);
            return report;
        }
        catch (Exception ex)
        {
            return "The device did not change: " + ex.Message;
        }
    }

    /// <summary>Leaves the map a device is in right now; the runners come back and the map pays nothing.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="deviceId">The device to leave its map.</param>
    /// <returns>A short report of what happened.</returns>
    public static string Interrupt(string who, int deviceId)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            var device = Device(save, deviceId);
            if (device is null) return NoDevice(deviceId);
            if (device.Kidnapped) return Waiting(device);
            if (device.Current is null) return $"Device {device.Id} is not running a map.";
            var entry = device.Current.Entry;
            device.Current = null;
            // The map goes back to the front of the queue: it may run again, but it pays nothing.
            device.Queue.Insert(0, entry);
            Write(save, who, true);
            return $"Device {device.Id} left {entry.Name}; the runners are back and the map pays nothing.";
        }
        catch (Exception ex)
        {
            return "The device did not leave its map: " + ex.Message;
        }
    }

    /// <summary>Queues a map on a device.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="deviceId">The device to queue the map on.</param>
    /// <param name="mapTypeHash">The base type hash of the map.</param>
    /// <param name="tier">The tier of the map.</param>
    /// <param name="rarity">The rarity band of the map: 0 white, 1 yellow, 2 red, 3 purple.</param>
    /// <param name="affixCount">The number of affixes of the map.</param>
    /// <param name="special">The special content of the map: "", Boss, Guardian, Eldritch, Maven or Season.</param>
    /// <returns>A short report of what happened.</returns>
    public static string QueueMap(string who, int deviceId, uint mapTypeHash, int tier, byte rarity, int affixCount, string special)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            var device = Device(save, deviceId);
            if (device is null) return NoDevice(deviceId);
            if (device.Kidnapped) return Waiting(device);
            if (device.Queue.Count + (device.Current is null ? 0 : 1) >= MappingBalance.MaxQueue)
                return $"Device {device.Id} holds at most {MappingBalance.MaxQueue} maps.";

            if (tier < 1 || tier > MappingBalance.MaxTier) return $"The tier of a map runs from 1 to {MappingBalance.MaxTier}.";
            var band = Band(tier);
            if (band is null) return $"The tier of a map runs from 1 to {MappingBalance.MaxTier}.";
            if (band.Value.Rarity != rarity)
                return $"A tier {tier} map is a {band.Value.Name} map; queue it with rarity {band.Value.Rarity}.";

            int cap = MappingBalance.TierCap(device.Level);
            if (tier > cap)
                return $"This device runs maps up to tier {cap}; it needs a {band.Value.Name} map, and a {band.Value.Name} map starts at tier {band.Value.Tier}.";

            var type = Program.Items.ByHash(mapTypeHash);
            if (type is null) return "The map type is unknown.";
            if (!IsMap(type.Id, type.Class)) return "That is not a map.";
            if (UniqueMaps().Contains(mapTypeHash)) return "A unique map cannot run on a mapping device.";

            string kind = (special ?? "").Trim();
            string? known = AllowedSpecials.FirstOrDefault(item => item.Equals(kind, StringComparison.OrdinalIgnoreCase));
            if (kind.Length > 0 && known is null)
                return "The special content of a map is Boss, Guardian, Eldritch, Maven or Season.";

            device.Queue.Add(new MappingQueueEntry
            {
                MapTypeHash = mapTypeHash,
                Name = type.Name,
                Tier = tier,
                Rarity = rarity,
                AffixCount = Math.Max(0, affixCount),
                Special = known ?? "",
            });
            Write(save, who, true);
            return $"Device {device.Id} queues {type.Name} (tier {tier}, {band.Value.Name}).";
        }
        catch (Exception ex)
        {
            return "The map was not queued: " + ex.Message;
        }
    }

    /// <summary>Puts a runner to work on a device.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="deviceId">The device the runner works.</param>
    /// <param name="runnerId">The runner to assign.</param>
    /// <returns>A short report of what happened.</returns>
    public static string Assign(string who, int deviceId, int runnerId)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            var device = Device(save, deviceId);
            if (device is null) return NoDevice(deviceId);
            var runner = save.Runners.FirstOrDefault(candidate => candidate.Id == runnerId);
            if (runner is null) return "There is no runner with that number.";
            if (runner.Dead) return $"{runner.Name} is dead.";
            if (runner.Rank <= 0) return $"{runner.Name} has no Atlas Runner specialty; only a runner with one may work a device.";
            if (runner.DeviceId != 0 && runner.DeviceId != device.Id) return $"{runner.Name} already works device {runner.DeviceId}.";
            if (!device.Workers.Contains(runner.Id))
            {
                if (device.Workers.Count >= MappingBalance.MaxWorkers)
                    return $"Device {device.Id} holds at most {MappingBalance.MaxWorkers} runners.";
                device.Workers.Add(runner.Id);
            }
            runner.DeviceId = device.Id;
            Write(save, who, true);
            return $"{runner.Name} works device {device.Id} now.";
        }
        catch (Exception ex)
        {
            return "The runner was not assigned: " + ex.Message;
        }
    }

    /// <summary>Takes a runner off a device.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="deviceId">The device the runner leaves.</param>
    /// <param name="runnerId">The runner to unassign.</param>
    /// <returns>A short report of what happened.</returns>
    public static string Unassign(string who, int deviceId, int runnerId)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            var device = Device(save, deviceId);
            if (device is null) return NoDevice(deviceId);
            var runner = save.Runners.FirstOrDefault(candidate => candidate.Id == runnerId);
            if (runner is null) return "There is no runner with that number.";
            if (!device.Workers.Remove(runner.Id)) return $"{runner.Name} does not work device {device.Id}.";
            runner.DeviceId = 0;
            Write(save, who, true);
            return $"{runner.Name} does not work device {device.Id} any more.";
        }
        catch (Exception ex)
        {
            return "The runner was not unassigned: " + ex.Message;
        }
    }

    /// <summary>Hires a candidate of the panel.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="index">The number of the candidate, from 1.</param>
    /// <returns>A short report of what happened.</returns>
    public static string Hire(string who, int index)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            if (index < 1 || index > save.Recruits.Count) return $"The candidates are numbered 1 to {save.Recruits.Count}.";
            if (save.Runners.Count >= MappingBalance.PopulationLimit) return "The town is at its population limit.";
            var candidate = save.Recruits[index - 1];
            if (!KingsmarchPort.Spend(who, candidate.Cost)) return "The treasury is out of gold.";
            var runner = new MappingRunner
            {
                Id = save.NextRunnerId++,
                Name = candidate.Name,
                Class = candidate.Class,
                Level = candidate.Level,
                Rank = candidate.Rank,
                Wage = candidate.Wage,
            };
            save.Runners.Add(runner);
            save.Recruits.RemoveAt(index - 1);
            RollCandidate(save);
            Write(save, who, true);
            return $"{candidate.Name} joins the line for {candidate.Cost} gold (wage {candidate.Wage} an hour).";
        }
        catch (Exception ex)
        {
            return "The candidate was not hired: " + ex.Message;
        }
    }

    /// <summary>Rolls the candidates of the panel anew.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="superReroll">Whether the dearer reroll is bought.</param>
    /// <returns>A short report of what happened.</returns>
    public static string Reroll(string who, bool superReroll)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            int cost = MappingBalance.RecruitRerollCost * (superReroll ? MappingBalance.RecruitSuperRerollMultiplier : 1);
            if (!KingsmarchPort.Spend(who, cost)) return "The treasury is out of gold.";
            save.Recruits.Clear();
            while (save.Recruits.Count < CandidateCount) RollCandidate(save);
            Write(save, who, true);
            return $"The candidates are new (cost {cost} gold).";
        }
        catch (Exception ex)
        {
            return "The candidates did not change: " + ex.Message;
        }
    }

    /// <summary>Fires a runner.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="runnerId">The runner to fire.</param>
    /// <returns>A short report of what happened.</returns>
    public static string Fire(string who, int runnerId)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            var runner = save.Runners.FirstOrDefault(candidate => candidate.Id == runnerId);
            if (runner is null) return "There is no runner with that number.";
            foreach (var device in save.Devices) device.Workers.Remove(runner.Id);
            save.Runners.Remove(runner);
            Write(save, who, true);
            return $"{runner.Name} is fired.";
        }
        catch (Exception ex)
        {
            return "The runner was not fired: " + ex.Message;
        }
    }

    /// <summary>Fights the bandits for the hostage of a device.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="deviceId">The device whose hostage is fought for.</param>
    /// <returns>A short report of what happened.</returns>
    public static string Rescue(string who, int deviceId)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            var device = Device(save, deviceId);
            if (device is null) return NoDevice(deviceId);
            if (!device.Kidnapped || device.KidnapDeadline <= 0) return $"Device {device.Id} holds no hostage.";
            int tier = device.Current?.Entry.Tier ?? 1;
            double power = Power(device, save);
            double odds = 3.0 + 0.5 * tier;
            if (power >= odds)
            {
                Free(device);
                Resume(save, device);
                Write(save, who, true);
                return $"The runners of device {device.Id} fought the bandits off ({power:0.00} against {odds:0.00}); the hostage is free.";
            }
            LoseRunners(save, device);
            Free(device);
            Write(save, who, true);
            return $"The runners of device {device.Id} lost the fight ({power:0.00} against {odds:0.00}); every runner on the device is lost.";
        }
        catch (Exception ex)
        {
            return "The rescue failed: " + ex.Message;
        }
    }

    /// <summary>Buys the hostage of a device back.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="deviceId">The device whose hostage is bought back.</param>
    /// <returns>A short report of what happened.</returns>
    public static string Ransom(string who, int deviceId)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            var device = Device(save, deviceId);
            if (device is null) return NoDevice(deviceId);
            if (!device.Kidnapped || device.KidnapDeadline <= 0) return $"Device {device.Id} holds no hostage.";
            int cost = (int)MappingBalance.RansomCost;
            if (!KingsmarchPort.Spend(who, cost)) return "The treasury is out of gold.";
            Free(device);
            Resume(save, device);
            Write(save, who, true);
            return $"The hostage of device {device.Id} is bought back for {cost} gold.";
        }
        catch (Exception ex)
        {
            return "The ransom failed: " + ex.Message;
        }
    }

    /// <summary>Gives the hostage of a device up; the runners of the device are lost.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="deviceId">The device whose hostage is given up.</param>
    /// <returns>A short report of what happened.</returns>
    public static string Abandon(string who, int deviceId)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            var device = Device(save, deviceId);
            if (device is null) return NoDevice(deviceId);
            if (!device.Kidnapped || device.KidnapDeadline <= 0) return $"Device {device.Id} holds no hostage.";
            LoseRunners(save, device);
            Free(device);
            Write(save, who, true);
            return $"The runners of device {device.Id} are lost; the bandits keep the hostage.";
        }
        catch (Exception ex)
        {
            return "The device was not abandoned: " + ex.Message;
        }
    }

    /// <summary>Acknowledges the loss of a runner.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="deviceId">The device that lost a runner.</param>
    /// <returns>A short report of what happened.</returns>
    public static string AcknowledgeLost(string who, int deviceId)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            var device = Device(save, deviceId);
            if (device is null) return NoDevice(deviceId);
            if (!device.Kidnapped) return $"Device {device.Id} lost no runner.";
            if (device.KidnapDeadline > 0)
                return $"The hostage of device {device.Id} waits for an answer: rescue it, ransom it or abandon it.";
            var runner = save.Runners.FirstOrDefault(candidate => candidate.Id == device.KidnappedRunnerId);
            device.Kidnapped = false;
            device.KidnappedRunnerId = 0;
            device.KidnapDeadline = 0;
            device.Current = null;
            Resume(save, device);
            Write(save, who, true);
            return runner is null
                ? $"The loss of device {device.Id} is acknowledged."
                : $"The loss of {runner.Name} is acknowledged; device {device.Id} runs on.";
        }
        catch (Exception ex)
        {
            return "The loss was not acknowledged: " + ex.Message;
        }
    }

    /// <summary>Takes the rewards a device holds.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <param name="deviceId">The device whose rewards are taken.</param>
    /// <param name="index">The number of the reward, or -1 for every reward of the device.</param>
    /// <returns>A short report of what happened.</returns>
    public static string TakeRewards(string who, int deviceId, int index)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            var device = Device(save, deviceId);
            if (device is null) return NoDevice(deviceId);
            var pending = device.Rewards.Where(reward => !reward.Taken).ToList();
            if (pending.Count == 0) return $"Device {device.Id} has no reward to take.";

            var taken = new List<MappingReward>();
            if (index < 0)
            {
                foreach (var reward in pending)
                {
                    reward.Taken = true;
                    taken.Add(reward);
                }
            }
            else
            {
                if (index >= device.Rewards.Count) return "There is no reward with that number.";
                var reward = device.Rewards[index];
                if (reward.Taken) return "That reward is already taken.";
                reward.Taken = true;
                taken.Add(reward);
            }

            Write(save, who, true);
            int items = taken.Sum(reward => reward.Drops.Count);
            int stats = taken.Sum(reward => reward.BonusStats.Count);
            long gold = taken.Sum(reward => reward.Gold);
            string names = string.Join(", ", taken.Select(reward => reward.Name));
            return $"Took {taken.Count} reward(s) of device {device.Id} ({names}): {items} item(s), {stats} bonus stat(s), {gold} gold paid into the treasury when the map ended.";
        }
        catch (Exception ex)
        {
            return "The reward was not taken: " + ex.Message;
        }
    }

    /// <summary>Reports the state of the mapping line.</summary>
    /// <param name="who">The character the mapping line belongs to.</param>
    /// <returns>The report of the line.</returns>
    public static string Status(string who)
    {
        try
        {
            var save = MappingStore.Of(who);
            Ensure(save);
            var lines = new List<string> { $"Mapping devices ({save.Devices.Count}):" };
            foreach (var device in save.Devices)
            {
                string state = device.Kidnapped
                    ? device.KidnapDeadline > 0 ? "a runner is hostage" : "a runner died"
                    : device.Current is { } run ? $"running {run.Entry.Name} ({run.Completion}%, {Minutes(run.Minutes)} left)"
                    : "idle";
                lines.Add($"  Device {device.Id} (level {device.Level}): {state}; queue {device.Queue.Count}; runners {device.Workers.Count}/{MappingBalance.MaxWorkers}; rewards {device.Rewards.Count(reward => !reward.Taken)} waiting.");
            }

            var runners = save.Runners.Where(runner => !runner.Dead).ToList();
            lines.Add($"Runners ({runners.Count}/{MappingBalance.PopulationLimit}): " + (runners.Count == 0
                ? "none"
                : string.Join(", ", runners.Select(runner => $"#{runner.Id} {runner.Name} (rank {runner.Rank}, wage {runner.Wage})"))));
            lines.Add("Candidates: " + string.Join(", ", save.Recruits.Select((candidate, index) =>
                $"{index + 1}. {candidate.Name} ({candidate.Class}, level {candidate.Level}, rank {candidate.Rank}, wage {candidate.Wage}, cost {candidate.Cost})")));
            lines.Add($"Treasury: {KingsmarchPort.Gold(who)} gold.");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return "The mapping line could not be read: " + ex.Message;
        }
    }

    /// <summary>
    /// Creates the devices and the candidates of a player that has no mapping state yet. The town owns three mapping
    /// devices (the client's <c>village_number_of_map_devices</c>).
    /// </summary>
    /// <returns>Whether something was created.</returns>
    private static bool Ensure(MappingSave save)
    {
        bool changed = false;
        while (save.Devices.Count < DeviceCount)
        {
            save.Devices.Add(new MappingDeviceState { Id = save.NextDeviceId++, Level = 1 });
            changed = true;
        }
        while (save.Recruits.Count < CandidateCount)
        {
            RollCandidate(save);
            changed = true;
        }
        return changed;
    }

    /// <summary>The device with that number.</summary>
    private static MappingDeviceState? Device(MappingSave save, int deviceId) =>
        save.Devices.FirstOrDefault(device => device.Id == deviceId);

    /// <summary>The report of a device that does not exist.</summary>
    private static string NoDevice(int deviceId) => $"There is no mapping device {deviceId}.";

    /// <summary>The report of a device that waits for an answer.</summary>
    private static string Waiting(MappingDeviceState device) => device.KidnapDeadline > 0
        ? $"Device {device.Id} has a runner in the hands of the bandits; rescue it, ransom it or abandon it."
        : $"Device {device.Id} lost a runner; acknowledge the loss.";

    /// <summary>Whether a device of the line holds a hostage.</summary>
    private static bool HasHostage(MappingSave save) => save.Devices.Any(device => device.Kidnapped && device.KidnapDeadline > 0);

    /// <summary>The runners that work a device.</summary>
    private static List<MappingRunner> Workers(MappingDeviceState device, MappingSave save)
    {
        var workers = new List<MappingRunner>();
        foreach (int id in device.Workers)
        {
            var runner = save.Runners.FirstOrDefault(candidate => candidate.Id == id);
            if (runner is null || runner.Dead) continue;
            workers.Add(runner);
        }
        return workers;
    }

    /// <summary>The effective power of a device: its runners and its own level.</summary>
    private static double Power(MappingDeviceState device, MappingSave save) =>
        MappingBalance.TeamPower(Workers(device, save)) * MappingBalance.DevicePowerMultiplier(device.Level);

    /// <summary>The wages of the runners that work the devices, an hour.</summary>
    private static int Wages(MappingSave save)
    {
        int total = 0;
        foreach (var device in save.Devices)
        {
            foreach (var runner in Workers(device, save)) total += runner.Wage;
        }
        return total;
    }

    /// <summary>Moves one device forward: the map that ends pays out, a death or a hostage stops the line.</summary>
    private static bool AdvanceDevice(string who, MappingSave save, MappingDeviceState device, double now, Random rng, List<string> notes)
    {
        if (device.Kidnapped)
        {
            // A hostage that waited too long is lost: the bandits keep the runner and the runners of the device go.
            if (device.KidnapDeadline > 0 && now >= device.KidnapDeadline)
            {
                LoseRunners(save, device);
                device.Kidnapped = false;
                device.KidnappedRunnerId = 0;
                device.KidnapDeadline = 0;
                device.Current = null;
                notes.Add($"The bandits kept the hostage of device {device.Id}; every runner of the device is lost.");
                return true;
            }
            return false;
        }

        bool changed = false;
        while (device.Current is { } run && run.EndsAt <= now)
        {
            // The map ended at its own end time: the next one starts there, so a long gap runs the whole queue.
            double ended = run.EndsAt;
            Finish(who, save, device, run, ended, rng, notes);
            changed = true;
            // A runner that died or was taken hostage: the device waits for the player.
            if (device.Kidnapped) return true;
            device.Current = null;
            // A device that was told to stop after this map stops here.
            if (device.SuspendAfterCurrent) return true;
            // While a runner of another device is hostage, no device opens the next map.
            if (HasHostage(save)) return true;
            if (!StartNext(save, device, ended)) return true;
        }
        return changed;
    }

    /// <summary>Pays out a finished map and rolls the death of a runner and the taking of a hostage.</summary>
    private static void Finish(string who, MappingSave save, MappingDeviceState device, MappingRun run, double now, Random rng, List<string> notes)
    {
        var reward = MappingRewards.Roll(device.Id, run.Entry, run.Completion, rng);
        reward.At = now;
        device.Rewards.Add(reward);
        if (reward.Gold > 0) KingsmarchPort.Fund(who, reward.Gold);
        notes.Add($"Device {device.Id} finished {run.Entry.Name} ({reward.Completion}%, {reward.Gold} gold).");

        if (run.DeathChance > 0 && rng.NextDouble() * 100 < run.DeathChance)
        {
            var runner = Pick(device, save, rng);
            if (runner is not null)
            {
                runner.Dead = true;
                device.Workers.Remove(runner.Id);
                runner.DeviceId = 0;
                device.Kidnapped = true;
                device.KidnappedRunnerId = runner.Id;
                device.KidnapDeadline = 0;
                notes.Add($"{runner.Name} died in the atlas of device {device.Id}; acknowledge the loss.");
                return;
            }
        }

        if (rng.NextDouble() < KidnapChance)
        {
            var runner = Pick(device, save, rng);
            if (runner is not null)
            {
                device.Kidnapped = true;
                device.KidnappedRunnerId = runner.Id;
                device.KidnapDeadline = now + KidnapHours * 3600.0;
                notes.Add($"A runner of device {device.Id} was taken hostage; every device stops until you answer.");
            }
        }
    }

    /// <summary>Starts the next map of a device.</summary>
    private static bool StartNext(MappingSave save, MappingDeviceState device, double now)
    {
        if (device.Queue.Count == 0) return false;
        var workers = Workers(device, save);
        if (workers.Count == 0) return false;
        var entry = device.Queue[0];
        device.Queue.RemoveAt(0);

        double power = MappingBalance.TeamPower(workers) * MappingBalance.DevicePowerMultiplier(device.Level);
        int affixes = Math.Max(0, entry.AffixCount);
        int completion = MappingBalance.CompletionPercent(entry.Tier, entry.Rarity, affixes, power);
        double minutes = MappingBalance.MinutesPerMap(entry.Tier, entry.Rarity, affixes, power, completion);
        if (Slow(entry.Special)) minutes *= 1.10;
        minutes = Math.Min(minutes, MaxMinutesPerMap);
        int deathAffixes = Influence(entry.Special) ? affixes + 1 : affixes;
        double death = MappingBalance.DeathChancePercent(entry.Tier, entry.Rarity, deathAffixes, power, completion, workers.Count, MappingBalance.SkillReliefUnits(workers));

        device.Current = new MappingRun
        {
            Entry = entry,
            StartedAt = now,
            EndsAt = now + minutes * 60.0,
            Completion = completion,
            Minutes = minutes,
            DeathChance = death,
        };
        return true;
    }

    /// <summary>Takes the next map of a device's queue when the device may run on.</summary>
    private static void Resume(MappingSave save, MappingDeviceState device)
    {
        if (device.Current is not null) return;
        if (device.Queue.Count == 0) return;
        if (device.Kidnapped || HasHostage(save)) return;
        StartNext(save, device, Now());
    }

    /// <summary>A runner of the device, picked at random.</summary>
    private static MappingRunner? Pick(MappingDeviceState device, MappingSave save, Random rng)
    {
        var workers = Workers(device, save);
        if (workers.Count == 0) return null;
        return workers[rng.Next(workers.Count)];
    }

    /// <summary>Loses every runner of a device.</summary>
    private static void LoseRunners(MappingSave save, MappingDeviceState device)
    {
        foreach (int id in device.Workers)
        {
            var runner = save.Runners.FirstOrDefault(candidate => candidate.Id == id);
            if (runner is null) continue;
            runner.Dead = true;
            runner.DeviceId = 0;
        }
        device.Workers.Clear();
    }

    /// <summary>Ends the hostage of a device.</summary>
    private static void Free(MappingDeviceState device)
    {
        device.Kidnapped = false;
        device.KidnappedRunnerId = 0;
        device.KidnapDeadline = 0;
        device.Current = null;
    }

    /// <summary>Rolls one candidate of the panel.</summary>
    private static void RollCandidate(MappingSave save)
    {
        int level = Random.Shared.Next(1, 6);
        int rank = Random.Shared.Next(0, 100) < SpecialtyChance ? Random.Shared.Next(1, MaxCandidateRank + 1) : 0;
        int wage = WageBase + WageLevel * level + WageRank * rank;
        int cost = HireBaseCost + HireRankCost * rank;
        if (rank > 0) cost = (int)Math.Round(cost * SpecialtyCostMultiplier);
        save.Recruits.Add(new MappingRecruit
        {
            Name = CandidateNames[Random.Shared.Next(CandidateNames.Length)],
            Class = CandidateClasses[Random.Shared.Next(CandidateClasses.Length)],
            Level = level,
            Rank = rank,
            Wage = wage,
            Cost = cost,
        });
    }

    /// <summary>The rarity band of a tier.</summary>
    private static (int Tier, byte Rarity, string Name)? Band(int tier)
    {
        (int Tier, byte Rarity, string Name)? found = null;
        foreach (var band in Bands)
        {
            if (tier >= band.Tier) found = band;
        }
        return found;
    }

    /// <summary>Whether the special content of a map makes the map harder.</summary>
    private static bool Influence(string? special) =>
        special is not null && (special.Equals("Boss", StringComparison.OrdinalIgnoreCase)
            || special.Equals("Eldritch", StringComparison.OrdinalIgnoreCase)
            || special.Equals("Maven", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the special content of a map makes the map slower.</summary>
    private static bool Slow(string? special) =>
        Influence(special) || (special is not null && special.Equals("Guardian", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an item base is a map.</summary>
    private static bool IsMap(string id, string @class) =>
        id.StartsWith("Metadata/Items/Maps/", StringComparison.Ordinal) || @class == "Map";

    /// <summary>The unique maps of the client: the bases the UniqueMaps table names.</summary>
    private static HashSet<uint> UniqueMaps()
    {
        lock (MapGate)
        {
            if (_uniqueMaps is not null) return _uniqueMaps;
            var unique = new HashSet<uint>();
            try
            {
                foreach (var entry in NessaMapStock.Build(Program.Items, "data"))
                {
                    if (entry.Item.Rarity != ItemRarity.Unique) continue;
                    unique.Add(entry.Type.Hash);
                    unique.Add(entry.Item.TypeHash);
                }
            }
            catch (Exception)
            {
                // The unique map table is optional: without it no map is refused as a unique one.
            }
            _uniqueMaps = unique;
            return _uniqueMaps;
        }
    }

    /// <summary>The time of a map in a readable form.</summary>
    private static string Minutes(double minutes)
    {
        int total = (int)Math.Round(minutes);
        return $"{total / 60}h {total % 60}m";
    }

    /// <summary>The current unix time in seconds.</summary>
    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>
    /// The clock of the line: a unix time in seconds. The player's tick hands over the seconds since the last tick, not
    /// a time of day, so a value that cannot be a unix time is replaced by the engine's own clock.
    /// </summary>
    private static double Clock(double now) => now >= 1_000_000_000 ? now : Now();

    /// <summary>Keeps the mirror of the treasury in step and writes the save.</summary>
    private static void Write(MappingSave save, string who, bool force = false)
    {
        try
        {
            save.TreasuryGold = KingsmarchPort.Gold(who);
        }
        catch (Exception)
        {
            // The town treasury is not there yet; the mirror keeps the last number.
        }

        double now = Now();
        double last = LastWrite.GetOrAdd(who, 0);
        if (!force && now - last < 60) return;
        LastWrite[who] = now;
        MappingStore.Save();
    }
}
