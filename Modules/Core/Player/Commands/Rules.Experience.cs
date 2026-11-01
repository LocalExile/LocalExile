using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;

static partial class CommandsRules
{
    internal static ExperienceChange RecordExperienceReset(ClientState conn, int level)
    {
        var updates = LevelUpdatesFor(conn);
        lock (updates.Gate)
        lock (conn.Monsters)
        {
            if (HardcoreProgressBlocked(conn))
            {
                uint held = CurrentExperience(conn);
                return new(updates.Revision, held, held, false);
            }
            uint before = CurrentExperience(conn), after = CharacterStats.ExperienceFor(level);
            CharacterSaves.SetExperience(CharacterName(conn), after);
            conn.Experience = after;
            return new(++updates.Revision, before, after, true, Reset: true);
        }
    }
}
