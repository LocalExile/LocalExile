using GameServer.Core.Protocol.Instance;

static partial class Game
{
    // Owned by one original player cast, shared by its repeats/cascades; never created by a proxy executor.
    public sealed class SacrificeUse(ClientState caster, ushort skill, int effect, uint gem, bool triggered = false)
    {
        readonly int epoch = caster.ProjectileEpoch;
        readonly GameServer.Modding.IModuleLifetime? owner = CombatLifetime;
        int? added;
        readonly SupportedGem original = SupportedSkill(caster, skill);
        readonly (uint Host, int Socket, object Identity)[] sources = CaptureSources(caster, skill);

        static (uint, int, object)[] CaptureSources(ClientState conn, ushort skill)
        {
            var supported = SupportedSkill(conn, skill);
            var sockets = supported.Supports.Select(s => s.Gem.Socket).Where(s => s >= 0).ToHashSet();
            return [.. ItemsOf(conn).SocketedGemPlaces().Where(p => p.ItemId == supported.Host
                && (ReferenceEquals(p.Gem, supported.SourceGem) || sockets.Contains(p.Socket)))
                .Select(p => (p.ItemId, p.Socket, PlayerInventories.GemIdentity(p.Gem)))];
        }

        bool OwnerCurrent => CurrentGameWork && ReferenceEquals(owner, CombatLifetime) && caster.ProjectileActive(epoch);
        internal byte[]? LifePacket()
        {
            lock (caster.Monsters) return OwnerCurrent
                ? InstanceProtocol.LifeUpdate(Program.PlayerId, caster.PlayerLife, caster.PlayerReservedLife) : null;
        }

        internal bool Current => OwnerCurrent
            && (original.SourceGem is null || sources.Length > 0 && sources.All(source =>
                ItemsOf(caster).SocketedGemPlaces().Any(p => p.ItemId == source.Host && p.Socket == source.Socket
                    && ReferenceEquals(PlayerInventories.GemIdentity(p.Gem), source.Identity))))
            && SupportsCurrent()
            && GemSkillEffects(caster).Any(g => g.Id == skill && g.Effect == effect && g.Gem == gem)
            && GemSkillInstanceDisabled(caster, effect, skill, triggered: triggered) is null;

        bool SupportsCurrent()
        {
            var current = SupportedSkill(caster, skill);
            return original.Supports.All(s => current.Supports.Any(c => c.Gem.Socket == s.Gem.Socket
                && c.Gem.Effect == s.Gem.Effect));
        }

        internal int AddedChaos(Action? paid = null)
        {
            lock (caster.Monsters)
            {
                if (!Current) return 0;
                if (added is int captured) return captured;
                var supported = SupportedSkill(caster, skill);
                int Get(string name) => Stats.Key(name) is uint key ? supported.Stats.GetValueOrDefault(key) : 0;
                int percent = Get("support_sacrifice_sacrifice_%_of_current_life");
                int sacrificed = SkillsFor(caster).IsSpell(effect) && percent > 0 && caster.PlayerLife > 1
                    ? (int)Math.Min((long)caster.PlayerLife - 1, (long)caster.PlayerLife * percent / 100) : 0;
                caster.PlayerLife -= (uint)sacrificed;
                if (sacrificed > 0) paid?.Invoke();
                added = (int)((long)sacrificed * Get("support_sacrifice_gain_%_of_sacrificed_life_as_added_chaos_damage") / 100);
                return added.Value;
            }
        }
    }
}
