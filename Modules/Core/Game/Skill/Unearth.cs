public static partial class Game
{
    internal sealed record UnearthCorpse(byte Level, int Maximum);

    public sealed partial class ProjectileSession
    {
        const string BoneArcher = "Metadata/Monsters/Skeletons/SkeletonBowBoneLance";
        readonly Queue<uint> unearthCorpses = new();

        void LandUnearth(UnearthCorpse corpse, (int X, int Y) at)
        {
            if (corpse.Maximum <= 0 || Monsters.Find(BoneArcher) is not { Supported: true } variety
                || !Player.Layout.WalkableFor(at.X, at.Y, 0)) return;
            Enqueue(() => SpawnSkillCorpse(Player, variety, corpse.Level, at, unearthCorpses,
                corpse.Maximum, () => Active && Player.PlayerLife > 0, MonsterPlacement.Established));
        }
    }
}
