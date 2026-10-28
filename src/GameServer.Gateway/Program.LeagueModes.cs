using System.Collections.Concurrent;

partial class Program
{
    internal static readonly RuthlessRulesFile RuthlessSettings = new(
        Environment.GetEnvironmentVariable("Game_RUTHLESS_RULES") ?? Path.Combine("config", "ruthless.json"), Console.WriteLine);
    static readonly Lazy<ItemCatalog> RuthlessItems = new(() => new("data", ruthless: true));
    static readonly Lazy<SkillCatalog> RuthlessSkills = new(() => new("data", ruthless: true));
    static readonly Lazy<PassiveTree> RuthlessPassives = new(() => new("data", ruthless: true));
    static readonly Lazy<ItemRolls> RuthlessRolls = new(() => new(RuthlessItems.Value));
    static readonly Lazy<UniqueCatalog> RuthlessUniques = new(() => new(RuthlessItems.Value));
    static readonly Lazy<EssenceCrafting> RuthlessEssences = new(() => new(RuthlessItems.Value, RuthlessRolls.Value));
    static readonly Lazy<HarvestCrafting> RuthlessHarvest = new(() => new(RuthlessItems.Value, RuthlessRolls.Value));
    static readonly Lazy<ItemDrops> RuthlessDrops = new(() => new(RuthlessItems.Value, RuthlessRolls.Value, ruthless: true));

    internal static ItemCatalog ItemsFor(ClientState conn) => conn.Ruthless ? RuthlessItems.Value : Items;
    internal static SkillCatalog SkillsFor(ClientState conn) => conn.Ruthless ? RuthlessSkills.Value : Skills;
    internal static PassiveTree PassivesFor(ClientState conn) => conn.Ruthless ? RuthlessPassives.Value : Passives;
    internal static ItemRolls RollsFor(ClientState conn) => conn.Ruthless ? RuthlessRolls.Value : Rolls;
    internal static UniqueCatalog UniquesFor(ClientState conn) => conn.Ruthless ? RuthlessUniques.Value : Uniques;
    internal static EssenceCrafting EssencesFor(ClientState conn) => conn.Ruthless ? RuthlessEssences.Value : Essences;
    internal static HarvestCrafting HarvestFor(ClientState conn) => conn.Ruthless ? RuthlessHarvest.Value : Harvest;
    internal static ItemDrops DropsFor(ClientState conn) => conn.Ruthless ? RuthlessDrops.Value : Drops;

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
        if (!conn.Ruthless) return;
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
            if (!conn.Ruthless) initial = InitialVitals(false, null, maxLife, maxMana);
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
        if (!conn.Ruthless || VitalsAdmissionKey(conn) is not { } key
            || !VitalsAdmissions.TryGetValue(key, out var admission)) return;
        lock (admission.Gate)
            if (admission.Latest == conn.RuthlessVitalsSessionId)
                VitalsAdmissions.TryRemove(new KeyValuePair<(string, Guid), VitalsAdmission>(key, admission));
    }

    internal static void PersistRuthlessVitals(ClientState conn, bool bestEffort = false)
    {
        if (!conn.Ruthless) return;
        try
        {
            lock (conn.Monsters)
                if (!conn.LeavingArea && conn.CharacterId is { } id)
                    CharacterSaves.SaveVitals(CharacterName(conn), id, conn.RuthlessVitalsSessionId, conn.League,
                        conn.PlayerLife, conn.PlayerMana);
        }
        catch (Exception e) when (bestEffort)
        {
            Console.WriteLine($"[ruthless] {CharacterName(conn)}: resource pools not saved; will retry: {e.Message}");
        }
    }

    // Save and retire before Auth can issue the destination ticket. Neither periodic nor disconnect saves from the
    // source may follow that handoff. An Auth refusal can resume only this same retired token, never a newer owner.
    internal static bool RetireRuthlessVitalsForTransfer(ClientState conn)
    {
        if (!conn.Ruthless) return true;
        if (conn.CharacterId is not { } id) return false;
        lock (conn.Monsters)
            return CharacterSaves.SaveVitals(CharacterName(conn), id, conn.RuthlessVitalsSessionId, conn.League,
                conn.PlayerLife, conn.PlayerMana, retire: true);
    }

    internal static void ResumeRuthlessVitalsAfterFailedTransfer(ClientState conn)
    {
        if (!conn.Ruthless || conn.CharacterId is not { } id) return;
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
            if (!conn.Ruthless || conn.PlayerLife != 0 || conn.RuthlessDeathHandled) return [];
            conn.RuthlessDeathHandled = true;
            if (!conn.Layout.Id.StartsWith("1_", StringComparison.Ordinal)
                && !conn.Layout.Id.StartsWith("2_", StringComparison.Ordinal)) return [];
            var settings = RuthlessSettings.Current;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var at = conn.Walk.Position(now);
            List<(uint, uint)> changed = [];
            foreach (var (id, monster) in conn.Monsters)
                if (!monster.Dead && monster.Life > 0 && monster.Variety?.BossHealthBar == true
                    && Dist(monster.Walk.Position(now), at) <= ExperienceRange)
                {
                    uint life = RuthlessGameplay.BossLifeAfterDeath(monster.Life, monster.MaxLife, settings.CampaignBossLifeRecoveryFraction);
                    if (life == monster.Life) continue;
                    monster.Life = life;
                    changed.Add((id, life));
                }
            return changed;
        }
    }
}
