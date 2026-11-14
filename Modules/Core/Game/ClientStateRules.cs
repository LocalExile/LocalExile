/// <summary>What the connection holds for the game's rules, in the rules' types. A reload gives new types: what an old one holds starts over.</summary>
public static class ClientStateRules
{
    // The last passive and equipment stat sum and its inputs (CharacterStatSources), per connection. A rule table rather
    // than a field of the connection: a closed connection that something still refers to (a monster of a kept instance)
    // would hold the value, and with it the rules that made it, in memory after a reload.
    static readonly RuleTable<ClientState, Game.StatSourcesResult> StatSources = new();

    extension(ClientState conn)
    {
        public RampageWorld RampageWorld
        {
            get
            {
                lock (conn.Monsters)
                {
                    // Old Game stat/recovery continuations must not claim the replacement's runtime.
                    if (!Game.CurrentGameRules) return Game.RetiredRampageWorld;
                    var source = AscendancyPolicies.SourceOwner(conn);
                    if (conn.OwnedCombatState is not RampageWorld current
                        || !ReferenceEquals(current.SourceOwner, source))
                    {
                        conn.RetireOwnedCombat();
                        current = new RampageWorld(source);
                        current.PendingRemoval.UnionWith(conn.RetiredCombatObjects);
                        current.PendingBuffRemoval.UnionWith(conn.RetiredCombatBuffs);
                        conn.RetiredCombatObjects.Clear(); conn.RetiredCombatBuffs.Clear();
                        conn.OwnedCombatState = current;
                        conn.SentRampage = default;
                    }
                    return current;
                }
            }
        }

        /// <summary>The Rampage world this connection has, when <see cref="RampageWorld"/> would hand that one out as
        /// it is (the same test); null when asking would make a new one, or the rules are retired. Makes and changes
        /// nothing.</summary>
        public RampageWorld? StandingRampageWorld
        {
            get
            {
                lock (conn.Monsters)
                    return Game.CurrentGameRules && conn.OwnedCombatState is RampageWorld current
                        && ReferenceEquals(current.SourceOwner, AscendancyPolicies.SourceOwner(conn)) ? current : null;
            }
        }

        public VaalUseReservation? VaalUse
        {
            get => conn.VaalUseState as VaalUseReservation;
            set => conn.VaalUseState = value;
        }

        internal Game.WinterOrbRuntime? OrbRuntime
        {
            get => conn.WinterOrbState as Game.WinterOrbRuntime;
            set => conn.WinterOrbState = value;
        }

        public Game.ProjectileSession? ProjectileRuntime
        {
            get => conn.ProjectileRuntimeState as Game.ProjectileSession;
            set => conn.ProjectileRuntimeState = value;
        }

        public Game.StatSourcesResult? StatSourcesCache
        {
            get => StatSources.TryGetValue(conn, out var cached) ? cached : null;
            set
            {
                if (value is null) StatSources.Remove(conn);
                else StatSources.AddOrUpdate(conn, value);
            }
        }
    }
}
