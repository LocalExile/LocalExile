/// <summary>What the connection holds for the game's rules, in the rules' types. A reload gives new types: what an old one holds starts over.</summary>
public static class PlayerRules
{
    // The last passive and equipment stat sum and its inputs (CharacterStatSources), per connection. A rule table rather
    // than a field of the connection: a closed connection that something still refers to (a monster of a kept instance)
    // would hold the value, and with it the rules that made it, in memory after a reload.
    static readonly Store<Player, Game.StatSourcesResult> StatSources = new();

    extension(Player player)
    {
        public RampageWorld RampageWorld
        {
            get
            {
                // Old Game stat/recovery continuations must not claim the replacement's runtime.
                if (!Game.CurrentGameRules) return Game.RetiredRampageWorld;
                var source = AscendancyPolicies.SourceOwner(player);
                if (player.OwnedCombatState is not RampageWorld current
                    || !ReferenceEquals(current.SourceOwner, source))
                {
                    if (player.OwnedCombatState is not null) player.RetireOwnedCombat();
                    current = new RampageWorld(source);
                    current.PendingRemoval.UnionWith(player.RetiredCombatObjects);
                    player.RetiredCombatObjects.Clear();
                    player.OwnedCombatState = current;
                    player.SentRampage = default;
                    player.SentRampageStat = null;
                    player.SentRampageStacks = 0;
                }
                return current;
            }
        }

        /// <summary>The Rampage world this connection has, when <see cref="RampageWorld"/> would hand that one out as
        /// it is (the same test); null when asking would make a new one, or the rules are retired. Makes and changes
        /// nothing.</summary>
        public RampageWorld? StandingRampageWorld
        {
            get
            {
                return Game.CurrentGameRules && player.OwnedCombatState is RampageWorld current
                    && ReferenceEquals(current.SourceOwner, AscendancyPolicies.SourceOwner(player)) ? current : null;
            }
        }

        public VaalUseReservation? VaalUse
        {
            get => player.VaalUseState as VaalUseReservation;
            set => player.VaalUseState = value;
        }

        internal Game.WinterOrbRuntime? OrbRuntime
        {
            get => player.WinterOrbState as Game.WinterOrbRuntime;
            set => player.WinterOrbState = value;
        }

        public Game.ProjectileSession? ProjectileRuntime
        {
            get => player.ProjectileRuntimeState as Game.ProjectileSession;
            set => player.ProjectileRuntimeState = value;
        }

        public Game.StatSourcesResult? StatSourcesCache
        {
            get => StatSources.TryGetValue(player, out var cached) ? cached : null;
            set
            {
                if (value is null) StatSources.Remove(player);
                else StatSources.AddOrUpdate(player, value);
            }
        }
    }
}
