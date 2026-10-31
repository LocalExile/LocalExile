/// <summary>What the connection holds for the game's rules, in the rules' types. A reload gives new types: what an old one holds starts over.</summary>
static class ClientStateRules
{
    extension(ClientState conn)
    {
        internal RampageWorld RampageWorld
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

        internal VaalUseReservation? VaalUse
        {
            get => conn.VaalUseState as VaalUseReservation;
            set => conn.VaalUseState = value;
        }

        internal Game.ProjectileSession? ProjectileRuntime
        {
            get => conn.ProjectileRuntimeState as Game.ProjectileSession;
            set => conn.ProjectileRuntimeState = value;
        }

        internal Game.StatSourcesResult? StatSourcesCache
        {
            get => conn.StatSourcesCacheState as Game.StatSourcesResult;
            set => conn.StatSourcesCacheState = value;
        }
    }
}
