using GameServer.Modding;

/// <summary>What the Gateway calls where it does the same thing itself: the hooks of the modules, then those of the mods.</summary>
sealed class ServerHooks(ModHost modules, ModHost mods)
{
    public IEnumerable<(string Name, string Usage)> Commands => modules.Commands.Concat(mods.Commands);

    public async Task<bool> RunCommand(IPlayer player, string name, IReadOnlyList<string> args) =>
        await modules.RunCommand(player, name, args) || await mods.RunCommand(player, name, args);

    public async Task Tick(IPlayer player, double seconds)
    {
        await modules.Tick(player, seconds);
        await mods.Tick(player, seconds);
    }

    public bool WantsPacket(ushort opcode) => modules.WantsPacket(opcode) || mods.WantsPacket(opcode);

    public async Task<bool> Packet(IPlayer player, ushort opcode, byte[] bytes) =>
        await modules.Packet(player, opcode, bytes) || await mods.Packet(player, opcode, bytes);

    public async Task AreaGenerated(IPlayer player)
    {
        await modules.AreaGenerated(player);
        await mods.AreaGenerated(player);
    }

    public async Task MonsterKilled(MonsterKill kill)
    {
        await modules.MonsterKilled(kill);
        await mods.MonsterKilled(kill);
    }

    public async Task<bool> ObjectUsed(ObjectUse use) => await modules.ObjectUsed(use) || await mods.ObjectUsed(use);

    public DropRoll? Drops(IPlayer player, MonsterInfo monster)
    {
        if (!modules.ChangesDrops && !mods.ChangesDrops) return null;
        var roll = new DropRoll(player, monster);
        modules.ModifyDrops(roll);
        mods.ModifyDrops(roll);
        return roll;
    }

    public uint Experience(IPlayer player, MonsterInfo monster, uint experience) =>
        mods.Experience(player, monster, modules.Experience(player, monster, experience));

    public bool ChangesStats => modules.ChangesStats || mods.ChangesStats;

    public IEnumerable<KeyValuePair<string, int>> Stats(IPlayer player) => modules.Stats(player).Concat(mods.Stats(player));
}
