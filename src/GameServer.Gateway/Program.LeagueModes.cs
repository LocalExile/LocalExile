using System.Collections.Concurrent;
using GameServer.Core.Diagnostics;

partial class Program
{
    /// <summary>The character's league: Standard, or its module's (Modding/ServerLeagues.cs), decided once per connection.</summary>
    internal static LeagueRuntime LeagueFor(ClientState conn) =>
        conn.LeagueModule ??= ServerLeagues.Resolve(conn.Ruthless, conn.League, ServerMods.IsLoaded, Console.WriteLine);

    // The game files' two sets of data; a league module picks one (ILeague.Catalogs).
    static readonly Lazy<ItemCatalog> RuthlessItems = new(() => new("data", ruthless: true));
    static readonly Lazy<SkillCatalog> RuthlessSkills = new(() => new("data", ruthless: true));
    static readonly Lazy<PassiveTree> RuthlessPassives = new(() => new("data", ruthless: true));
    static readonly Lazy<ItemRolls> RuthlessRolls = new(() => new(RuthlessItems.Value));
    static readonly Lazy<UniqueCatalog> RuthlessUniques = new(() => new(RuthlessItems.Value));
    static readonly Lazy<EssenceCrafting> RuthlessEssences = new(() => new(RuthlessItems.Value, RuthlessRolls.Value));
    static readonly Lazy<HarvestCrafting> RuthlessHarvest = new(() => new(RuthlessItems.Value, RuthlessRolls.Value));
    static readonly Lazy<ItemDrops> RuthlessDrops = new(() => new(RuthlessItems.Value, RuthlessRolls.Value, ruthless: true, uniques: RuthlessUniques.Value, league: ServerLeagues.HardModeRules));

    internal static ItemCatalog ItemsFor(ClientState conn) => LeagueFor(conn).HardMode ? RuthlessItems.Value : Items;
    internal static SkillCatalog SkillsFor(ClientState conn) => LeagueFor(conn).HardMode ? RuthlessSkills.Value : Skills;
    internal static PassiveTree PassivesFor(ClientState conn) => LeagueFor(conn).HardMode ? RuthlessPassives.Value : Passives;
    internal static ItemRolls RollsFor(ClientState conn) => LeagueFor(conn).HardMode ? RuthlessRolls.Value : Rolls;
    internal static UniqueCatalog UniquesFor(ClientState conn) => LeagueFor(conn).HardMode ? RuthlessUniques.Value : Uniques;
    internal static EssenceCrafting EssencesFor(ClientState conn) => LeagueFor(conn).HardMode ? RuthlessEssences.Value : Essences;
    internal static HarvestCrafting HarvestFor(ClientState conn) => LeagueFor(conn).HardMode ? RuthlessHarvest.Value : Harvest;
    internal static ItemDrops DropsFor(ClientState conn) => LeagueFor(conn).HardMode ? RuthlessDrops.Value : Drops;

    sealed class VitalsAdmission
    {
        public readonly Lock Gate = new();
        public Guid Latest;
    }

    // Registration precedes expensive progress preparation. An older preparation cannot claim resources after a
    // newer connection was admitted. Entries are removed when their latest connection finishes, including refusals.
    static readonly ConcurrentDictionary<(string Database, Guid Character), VitalsAdmission> VitalsAdmissions = new();
    static (string, Guid)? VitalsAdmissionKey(ClientState conn) => conn.CharacterId is { } id
        ? (Path.GetFullPath(Saves.Db.Path).ToUpperInvariant(), id) : null;

    internal static void RegisterRuthlessVitalsAdmission(ClientState conn)
    {
        if (!LeagueFor(conn).Rules.PersistsVitals) return;
        lock (conn.Monsters)
        {
            var key = VitalsAdmissionKey(conn)
                ?? throw new InvalidOperationException("Ruthless resources require an admitted character identity");
            while (true)
            {
                var admission = VitalsAdmissions.GetOrAdd(key, _ => new());
                lock (admission.Gate)
                {
                    if (!VitalsAdmissions.TryGetValue(key, out var current) || !ReferenceEquals(current, admission)) continue;
                    admission.Latest = conn.RuthlessVitalsSessionId;
                    break;
                }
            }
        }
    }

    internal static void InitializeCharacterVitals(ClientState conn)
    {
        lock (conn.Monsters)
        {
            uint maxLife = conn.PlayerMaxLife, maxMana = (uint)UnreservedMana(conn);
            (uint Life, uint Mana) initial;
            if (!LeagueFor(conn).Rules.PersistsVitals) initial = InitialVitals(false, null, maxLife, maxMana);
            else
            {
                var key = VitalsAdmissionKey(conn);
                if (key is null || !VitalsAdmissions.TryGetValue(key.Value, out var admission))
                    throw new InvalidOperationException("Ruthless resource admission was not registered");
                lock (admission.Gate)
                {
                    if (admission.Latest != conn.RuthlessVitalsSessionId)
                        throw new InvalidOperationException("A newer connection superseded this resource admission");
                    initial = CharacterSaves.BeginVitalsSession(CharacterName(conn), key.Value.Item2,
                        conn.RuthlessVitalsSessionId, conn.League, save => InitialVitals(true, save, maxLife, maxMana));
                }
            }
            conn.PlayerLife = initial.Life;
            conn.PlayerMana = initial.Mana;
            conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, refill: true);
            conn.AuraDeathHandled = false;
        }
    }

    internal static void ForgetRuthlessVitalsAdmission(ClientState conn)
    {
        if (!LeagueFor(conn).Rules.PersistsVitals || VitalsAdmissionKey(conn) is not { } key
            || !VitalsAdmissions.TryGetValue(key, out var admission)) return;
        lock (admission.Gate)
            if (admission.Latest == conn.RuthlessVitalsSessionId)
                VitalsAdmissions.TryRemove(new KeyValuePair<(string, Guid), VitalsAdmission>(key, admission));
    }

    internal static void PersistRuthlessVitals(ClientState conn, bool bestEffort = false)
    {
        if (!LeagueFor(conn).Rules.PersistsVitals) return;
        try
        {
            lock (conn.Monsters)
                if (!conn.LeavingArea && conn.CharacterId is { } id)
                    CharacterSaves.SaveVitals(CharacterName(conn), id, conn.RuthlessVitalsSessionId, conn.League,
                        conn.PlayerLife, conn.PlayerMana);
        }
        catch (Exception e) when (bestEffort)
        {
            Console.WriteLine($"[ruthless] {CharacterName(conn)}: resource pools not saved; will retry: {ServerLog.Describe(e)}");
        }
    }

    // Save and retire before Auth can issue the destination ticket. Neither periodic nor disconnect saves from the
    // source may follow that handoff. An Auth refusal can resume only this same retired token, never a newer owner.
    internal static bool RetireRuthlessVitalsForTransfer(ClientState conn)
    {
        if (!LeagueFor(conn).Rules.PersistsVitals) return true;
        if (conn.CharacterId is not { } id) return false;
        lock (conn.Monsters)
            return CharacterSaves.SaveVitals(CharacterName(conn), id, conn.RuthlessVitalsSessionId, conn.League,
                conn.PlayerLife, conn.PlayerMana, retire: true);
    }

    internal static void ResumeRuthlessVitalsAfterFailedTransfer(ClientState conn)
    {
        if (!LeagueFor(conn).Rules.PersistsVitals || conn.CharacterId is not { } id) return;
        lock (conn.Monsters)
            CharacterSaves.ResumeVitalsSession(CharacterName(conn), id, conn.RuthlessVitalsSessionId, conn.League,
                conn.PlayerLife, conn.PlayerMana);
    }

    internal static (uint Life, uint Mana) InitialVitals(bool ruthless, CharacterSaves.Save? saved, uint maxLife, uint maxMana)
    {
        // New and legacy saves have no pools. A softcore death/relogin revives; HC death is refused before this point.
        bool alive = saved?.Life is > 0;
        return ruthless && alive ? (Math.Min(saved!.Life!.Value, maxLife), Math.Min(saved.Mana ?? maxMana, maxMana))
            : (maxLife, maxMana);
    }

    internal static IReadOnlyList<(uint Id, uint Life)> RecoverRuthlessBossesOnDeath(ClientState conn)
    {
        lock (conn.Monsters)
        {
            var settings = LeagueFor(conn).Rules;
            if (settings.BossLifeRecoveryFraction <= 0 || conn.PlayerLife != 0 || conn.RuthlessDeathHandled) return [];
            conn.RuthlessDeathHandled = true;
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
