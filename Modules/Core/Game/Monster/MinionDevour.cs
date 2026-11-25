using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static void MinionDevourKillingBlowLocked(Player player, GolemState.Golem fighter, uint id,
        SpawnedMonster corpse, IReadOnlyDictionary<uint, int> skill, double now)
    {
        int life = GolemStat(skill, Stat.KillingBlowConsumesCorpseRestoreXLife);
        int mana = GolemStat(skill, Stat.KillingBlowConsumesCorpseRestoreXMana);
        if ((life <= 0 && mana <= 0) || fighter.Monster.Dead || !GolemCurrent(player, fighter)
            || corpse.Ailments.Shattered || !IsCorpse(corpse)) return;

        corpse.DevourCorpse = true;
        if (!ConsumeDevouredCorpseLocked(player, id, corpse, now, playerConsumes: false)) return;
        player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), "minion devours corpse", WorldId: id, RemovesObject: true));
        RecoverMinionLifeLocked(player, fighter, life);

        int chance = GolemStat(skill, Stat.KillingBlowConsumesCorpseChanceToGainSoulPerPowerPermillage);
        if (chance <= 0 || !RandomRolls.Chance(ActorScale.Saturate((long)chance * WarcryPower(corpse.Rarity)), 1000, AreaRng(player))) return;
        (fighter.DevouredSouls ??= new()).Soul(now);
        PresentMinionDevouredSoulsLocked(player, fighter, now);
    }

    static int PresentMinionDevouredSoulsLocked(Player player, GolemState.Golem fighter, double now)
    {
        if (fighter.DevouredSouls is not { } souls) return 0;
        int count = souls.Stacks(now);
        if (count != souls.SentStacks)
        {
            if (count == 0) player.Area.Buffs.Clear(fighter.Id, SoulEaterBuffRow, fighter.Id);
            else player.Area.Buffs.Set(fighter.Id, new(SoulEaterBuffRow, fighter.Id, Stacks: (ushort)count));
            souls.SentStacks = count;
        }
        if (count == 0) fighter.DevouredSouls = null;
        return count;
    }
}
