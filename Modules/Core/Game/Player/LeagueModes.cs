using GameServer.Core.Accounts;
using System.Collections.Concurrent;
using GameServer.Core.Diagnostics;

public static partial class Game
{
    static readonly Lock HardModeGate = new();
    static volatile bool hardModeLoaded;

    /// <summary>Builds the HardMode data set (Ruthless leagues: league.json "dataSet") and this league's drops, once each,
    /// when the first character of such a league logs in (<see cref="GameServer.Modules.Game.GameModule"/>'s
    /// admission). A server whose players never play one never builds it. A null league builds the data set alone.
    /// Every HardMode connection is admitted only after this returns, so the fields are set for all of its reads.</summary>
    public static void LoadHardModeData(LeagueRuntime? league = null)
    {
        if (hardModeLoaded && (league is null || HardModeDrops.ContainsKey(league.Id))) return;
        lock (HardModeGate)
        {
            if (!hardModeLoaded)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                HardModeItems = new("data", hardMode: true);
                HardModeSkills = new("data", hardMode: true);
                HardModePassives = new("data", hardMode: true);
                HardModeAtlasPassives = new("data", hardMode: true);
                HardModeRolls = new(HardModeItems);
                HardModeUniques = new(HardModeItems);
                HardModeEssences = new(HardModeItems, HardModeRolls);
                HardModeCrafting = new(HardModeItems, HardModeRolls);
                RuthlessGrantedSkills = NewGrantedSkills(ruthless: true);
                RuthlessWornGrantedSkillCatalog = new(HardModeSkills, HardModeItems.Mods, ruthless: true);
                VendorPrices.For(HardModeItems, hardMode: true);
                foreach (bool supports in new[] { true, false })
                    GemVendorStock.For(HardModeItems, Npcs, HardModeSkills, hardMode: true, supportGems: supports);
                hardModeLoaded = true;
                Console.WriteLine($"[gateway] loaded the HardMode data set in {watch.ElapsedMilliseconds} ms");
            }
            // Drops read the league's gem rules, which exist once every module loaded, so each HardMode league has its own.
            if (league is not null && !HardModeDrops.ContainsKey(league.Id))
                HardModeDrops[league.Id] = new(HardModeItems, HardModeRolls, hardMode: true, uniques: HardModeUniques, league: league.Rules);
        }
    }

    // The game files' two sets of data; a league picks one (league.json "dataSet"). HardMode is built by LoadHardModeData.
    public static ItemCatalog HardModeItems = default!;
    public static SkillCatalog HardModeSkills = default!;
    public static PassiveTree HardModePassives = default!;
    public static AtlasPassives HardModeAtlasPassives = default!;
    public static ItemRolls HardModeRolls = default!;
    public static UniqueCatalog HardModeUniques = default!;
    public static EssenceCrafting HardModeEssences = default!;
    public static readonly ConcurrentDictionary<string, ItemDrops> HardModeDrops = new();

    public static ItemCatalog ItemsFor(Player conn) => LeagueFor(conn).HardMode ? HardModeItems : Items;
    public static SkillCatalog SkillsFor(Player conn) => LeagueFor(conn).HardMode ? HardModeSkills : Skills;
    public static PassiveTree PassivesFor(Player conn) => LeagueFor(conn).HardMode ? HardModePassives : Passives;
    public static ItemRolls RollsFor(Player conn) => LeagueFor(conn).HardMode ? HardModeRolls : Rolls;
    public static UniqueCatalog UniquesFor(Player conn) => LeagueFor(conn).HardMode ? HardModeUniques : Uniques;
    public static EssenceCrafting EssencesFor(Player conn) => LeagueFor(conn).HardMode ? HardModeEssences : Essences;
    public static ItemDrops DropsFor(Player conn) => LeagueFor(conn) is { HardMode: true } league
        ? HardModeDrops[league.Id] : Drops;

    public sealed class VitalsAdmission
    {
        public readonly Lock Gate = new();
        public Guid Latest;
    }

    // Registration precedes expensive progress preparation. An older preparation cannot claim resources after a
    // newer connection was admitted. Entries are removed when their latest connection finishes, including refusals.
    public static readonly ConcurrentDictionary<(string Database, Guid Character), VitalsAdmission> VitalsAdmissions = new();
    public static (string, Guid)? VitalsAdmissionKey(Player conn) => conn.CharacterId is { } id
        ? (Path.GetFullPath(Saves.Db.Path).ToUpperInvariant(), id) : null;

    public static void RegisterVitalsAdmission(Player conn)
    {
        if (!LeagueFor(conn).Rules.PersistsVitals) return;
        lock (conn.Monsters)
        {
            var key = VitalsAdmissionKey(conn)
                ?? throw new InvalidOperationException("Saved life and mana require an admitted character identity");
            while (true)
            {
                var admission = VitalsAdmissions.GetOrAdd(key, _ => new());
                lock (admission.Gate)
                {
                    if (!VitalsAdmissions.TryGetValue(key, out var current) || !ReferenceEquals(current, admission)) continue;
                    admission.Latest = conn.VitalsSessionId;
                    break;
                }
            }
        }
    }

    public static void InitializeCharacterVitals(Player conn)
    {
        lock (conn.Monsters)
        {
            uint maxLife = LifeCap(conn), maxMana = (uint)UnreservedMana(conn);
            (uint Life, uint Mana) initial;
            if (!LeagueFor(conn).Rules.PersistsVitals) initial = InitialVitals(false, null, maxLife, maxMana);
            else
            {
                var key = VitalsAdmissionKey(conn);
                if (key is null || !VitalsAdmissions.TryGetValue(key.Value, out var admission))
                    throw new InvalidOperationException("Saved-vitals admission was not registered");
                lock (admission.Gate)
                {
                    if (admission.Latest != conn.VitalsSessionId)
                        throw new InvalidOperationException("A newer connection superseded this resource admission");
                    initial = CharacterSaves.BeginVitalsSession(CharacterName(conn), key.Value.Item2,
                        conn.VitalsSessionId, conn.League, save => InitialVitals(true, save, maxLife, maxMana));
                }
            }
            conn.PlayerLife = initial.Life;
            conn.PlayerMana = initial.Mana;
            conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, refill: true);
            conn.AuraDeathHandled = false;
        }
    }

    public static void ForgetVitalsAdmission(Player conn)
    {
        if (!LeagueFor(conn).Rules.PersistsVitals || VitalsAdmissionKey(conn) is not { } key
            || !VitalsAdmissions.TryGetValue(key, out var admission)) return;
        lock (admission.Gate)
            if (admission.Latest == conn.VitalsSessionId)
                VitalsAdmissions.TryRemove(new KeyValuePair<(string, Guid), VitalsAdmission>(key, admission));
    }

    public static void PersistVitals(Player conn, bool bestEffort = false)
    {
        if (!LeagueFor(conn).Rules.PersistsVitals) return;
        try
        {
            lock (conn.Monsters)
                if (!conn.LeavingArea && conn.CharacterId is { } id)
                    CharacterSaves.SaveVitals(CharacterName(conn), id, conn.VitalsSessionId, conn.League,
                        conn.PlayerLife, conn.PlayerMana);
        }
        catch (Exception e) when (bestEffort)
        {
            Console.WriteLine($"[vitals] {CharacterName(conn)}: resource pools not saved; will retry: {ServerLog.Describe(e)}");
        }
    }

    // Save and retire before Auth can issue the destination ticket. Neither periodic nor disconnect saves from the
    // source may follow that handoff. An Auth refusal can resume only this same retired token, never a newer owner.
    public static bool RetireVitalsForTransfer(Player conn)
    {
        if (!LeagueFor(conn).Rules.PersistsVitals) return true;
        if (conn.CharacterId is not { } id) return false;
        lock (conn.Monsters)
            return CharacterSaves.SaveVitals(CharacterName(conn), id, conn.VitalsSessionId, conn.League,
                conn.PlayerLife, conn.PlayerMana, retire: true);
    }

    public static void ResumeVitalsAfterFailedTransfer(Player conn)
    {
        if (!LeagueFor(conn).Rules.PersistsVitals || conn.CharacterId is not { } id) return;
        lock (conn.Monsters)
            CharacterSaves.ResumeVitalsSession(CharacterName(conn), id, conn.VitalsSessionId, conn.League,
                conn.PlayerLife, conn.PlayerMana);
    }

    public static (uint Life, uint Mana) InitialVitals(bool persists, CharacterSaves.Save? saved, uint maxLife, uint maxMana)
    {
        // New and legacy saves have no pools. A softcore death/relogin revives; HC death is refused before this point.
        bool alive = saved?.Life is > 0;
        return persists && alive ? (Math.Min(saved!.Life!.Value, maxLife), Math.Min(saved.Mana ?? maxMana, maxMana))
            : (maxLife, maxMana);
    }

    public static IReadOnlyList<(uint Id, uint Life)> RecoverBossesOnDeath(Player conn)
    {
        lock (conn.Monsters)
        {
            var settings = LeagueFor(conn).Rules;
            if (settings.BossLifeRecoveryFraction <= 0 || conn.PlayerLife != 0 || conn.BossRecoveryHandled) return [];
            conn.BossRecoveryHandled = true;
            if (!settings.BossRecoveryAreaPrefixes.Any(p => conn.Layout.Id.StartsWith(p, StringComparison.Ordinal))) return [];
            double now = conn.Clock.Elapsed.TotalSeconds;
            var at = conn.Walk.Position(now);
            List<(uint, uint)> changed = [];
            foreach (var (id, monster) in conn.Monsters)
                if (!monster.Dead && monster.Life > 0 && monster.Variety?.BossHealthBar == true
                    && Dist(monster.Walk.Position(now), at) <= ExperienceRange)
                {
                    uint life = LeagueGameplay.BossLifeAfterDeath(monster.Life, monster.MaxLife, settings.BossLifeRecoveryFraction);
                    if (life == monster.Life) continue;
                    monster.Life = life;
                    changed.Add((id, life));
                }
            return changed;
        }
    }
}
