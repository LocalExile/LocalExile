using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;

static partial class Game
{
    // A triggered Shock Nova (Cast when Damage Taken, Cast on Crit) runs without its cast clip, and the clip is what
    // draws the nova: the server draws the worn skin's shock_nova_circle and shock_nova_donut (MiscAnimatedArtVariations
    // by skill_art_variation; SkillArtVariations shock_nova) at the area centre, scaled to the radius. A cast by hand
    // draws its own.
    internal static async Task ShowTriggeredShockNova(Player player, uint gem, (int X, int Y) at, int radius)
    {
        int variation = SkillArtVariation(player, gem);
        var arts = new[] { "shock_nova_circle", "shock_nova_donut" }
            .Select(id => Art.Animated(id, variation) ?? Art.Animated(id, 0)).OfType<string>().ToArray();
        if (ServerTrace.Skills)
            Console.WriteLine($"[gateway] {player.Connection.Remote} triggered shock nova art at ({at.X}, {at.Y}), radius {radius}, "
                + $"skin variation {variation}: {(arts.Length == 0 ? "none" : string.Join(", ", arts))}");
        int epoch = player.ProjectileEpoch;
        foreach (string art in arts)
        {
            if (player.Closed || player.LeavingArea) return;
            uint obj = player.TakeObjectId();
            // Native art radii are world units, the area radius grid cells; art with no radius plays at its own size.
            float scale = Art.ArtRadius(art) is int native and > 0 ? (float)(radius * (250d / 23) / native) : 1;
            await SendWorldCreate(player, obj, () => CreateEffectObject(obj, new EffectObject(at.X, at.Y, art)
                { Flags0 = EffectObject.FixedRotation, Scale = scale }),
                $"0x02bb triggered shock nova effect 0x{obj:x} {art} at ({at.X}, {at.Y}), scale {scale:0.###}",
                awaitBirth: false);
            SendAfter(player, 1.5, async () =>
            {
                if (player.Closed || player.LeavingArea || player.ProjectileEpoch != epoch) return;
                await SendWorldRemoval(player, obj, null);
            });
        }
    }
}
