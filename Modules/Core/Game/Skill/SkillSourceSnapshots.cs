public static partial class Game
{

    static IReadOnlyList<PlayerSkillSource> PlayerSkillSourcesLocked(ClientState conn)
    {
        string name = CharacterName(conn);
        var equipment = ItemsOf(conn).EquipmentSnapshot();
        return PlayerSkillSourcesAt(name, equipment, InactiveItems.GetValueOrDefault(name), LeagueFor(conn),
            GrantedSkillsOf(conn), WornGrantedSkillsOf(conn, equipment));
    }

    static PlayerSkillSource? CurrentPlayerSkillSourceLocked(ClientState conn, ushort source) =>
        PlayerSkillSourcesLocked(conn).FirstOrDefault(row => row.Id == source);
}
