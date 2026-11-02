using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>Opening objects use fitted .arm placements and captured .tmd placements, never seed-specific coordinates.</summary>
static class TutorialObjects
{
    public const string Strand = "1_1_1", Exile = "Metadata/NPC/Act1/WoundedExile",
        Zombie = "Metadata/Monsters/Zombies/ZombieBite", Spawner = "Metadata/Monsters/Zombies/BiteZombieSpawner",
        Chest = "Metadata/Chests/TutorialSupportGemChest";
    public sealed record Placement(string Type, string Model, int X, int Y, float Rotation);

    // TalkToWoundedExile(8.2,22.8) supplies the two voice lengths, not two stages of one scene.
    // NPCTalk options 2/3 play DBG_03 (22.763 s) / DBG_02 (7.951 s). The rig's bite clip lasts 3.1 s.
    public const double BiteSeconds = 3.1;
    // ActionTypes WashedUp HASH16 47065, masked to its basic-action skill id.
    public const ushort WashedUpSkillId = 0x37d9;
    // An action the server starts itself has the sequence's top bit set (0x141e94fc0); this is the character's first.
    public const ushort GetUpSequence = 0x8001;
    public static (byte Option, double? BiteAt) Dialogue(bool armed, bool weaponPromptHeard) =>
        !armed ? ((byte)1, null) : weaponPromptHeard ? ((byte)3, 8.2) : ((byte)2, 22.8);

    public static bool ShouldStartWashedUp(string area, bool weaponPending, Func<CharacterSaves.OpeningTutorial?> tutorial) =>
        area == Strand && weaponPending && tutorial() is
        { WakeUpShown: false, WeaponPromptHeard: false, Talked: false, ZombieKilled: false,
          ChestOpened: false, SkillClaimed: false, SupportClaimed: false };

    /// <summary>The exile's voice line ends when its window closes: the running scene goes on to the bite now.</summary>
    public static void SkipDialogue(ClientState conn)
    {
        bool exile;
        lock (conn.Npcs) exile = conn.Npcs.GetValueOrDefault(conn.TalkingTo)?.Npc.Id == Exile;
        if (exile && conn.TutorialSceneRunning) conn.TutorialDialogueSkipped?.TrySetResult();
    }

    public sealed record RoomPlacement(int X,int Y,int Width,int Height,int Code)
    {
        public AreaPopulation.RoomPoint Place(AreaPopulation.RoomPoint point)
        {
            int x=point.X,y=point.Y;
            var (cx,cy)=Code switch {0=>(x,y),1=>(Height-y-1,x),2=>(Width-x-1,Height-y-1),3=>(y,Width-x-1),
                4=>(Width-x-1,y),5=>(Height-y-1,Width-x-1),6=>(x,Height-y-1),7=>(y,x),_=>throw new ArgumentOutOfRangeException(nameof(Code))};
            // Mirrored codes 4..7 take 2π − θ, as the town rooms' NPC lines do (#236, TownNpcs.Room.Angle, from the client's
            // own placements).
            float turn=MathF.PI*2,angle=(Code>=4?turn-point.Angle:point.Angle)+(Code&3)*MathF.PI/2;
            return new(point.Name,X+cx,Y+cy,(angle%turn+turn)%turn);
        }
    }
    /// <summary>The captured placements of one room in (area, seed); without a capture, or with <paramref name="generated"/>,
    /// those of the Gateway's own generation (ServerLevels) when it generates the area.</summary>
    public static List<RoomPlacement> RoomPlacements(string area,uint seed,string path,string folder="data/walkability",bool generated=false)
    {
        string file=Path.Combine(folder,$"{area}_seed{seed}.rooms.json");
        // Without a capture, an area the Gateway generates itself has the same rooms from the port (ServerLevels).
        if(generated || !File.Exists(file))
            return ServerLevels.InPlaceOf(folder,area,seed) is { } level
                ? level.Rooms.Where(r=>string.Equals(r.Path,path,StringComparison.OrdinalIgnoreCase))
                    .Select(r=>new RoomPlacement(r.X,r.Y,r.Width,r.Height,r.Code)).Distinct().ToList()
                : [];
        // It runs while the area's layout is built (room points): a broken capture means no placement, not an error.
        try
        {
            using var doc=JsonDocument.Parse(File.ReadAllBytes(file));var root=doc.RootElement;
            if(root.GetProperty("area").GetString()!=area || root.GetProperty("seed").GetUInt32()!=seed) return [];
            return root.GetProperty("rooms").EnumerateArray().Where(r=>string.Equals(r.GetProperty("path").GetString(),path,StringComparison.OrdinalIgnoreCase))
                .Select(r=>new RoomPlacement(r.GetProperty("x").GetInt32(),r.GetProperty("y").GetInt32(),r.GetProperty("width").GetInt32(),r.GetProperty("height").GetInt32(),r.GetProperty("code").GetInt32()))
                .Where(r=>r.Width>0 && r.Height>0 && r.Code is >=0 and <=7).Distinct().ToList();
        }
        catch(Exception ex) when(ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Console.WriteLine($"[gateway] {file}: {ex.Message}");
            return [];
        }
    }
    public static Placement? ChestAt(AreaPopulation.RoomPoint point,string folder="data")
    {
        using var doc=GameTables.Open(Path.Combine(folder,"tables","Chests.json"));
        foreach(var row in doc.RootElement.EnumerateArray())
            if(row.GetProperty("Id").GetString()==Chest && row.GetProperty("AOFiles").EnumerateArray().FirstOrDefault().GetString() is {Length:>0} model)
                return new(Chest,model,point.X,point.Y,point.Angle);
        return null;
    }
    public static string DropKey(string character, CharacterSaves.OpeningTutorial tutorial, bool support) =>
        $"{character}/{tutorial.Id}/{(support ? "support" : "skill")}";

    public static InventoryItem? Claim(string character, bool support, Item item, Func<InventoryItem> place) => Saves.Atomically(() =>
    {
        if (CharacterSaves.Load(character)?.Tutorial is not { } t
            || (support ? t.SupportClaimed || !t.ChestOpened || t.SupportGem.Type != item.TypeHash
                        : t.SkillClaimed || !t.ZombieKilled || t.SkillGem.Type != item.TypeHash)) return null;
        var added = place();
        CharacterSaves.ChangeTutorial(character, s => support ? s with { SupportClaimed = true } : s with { SkillClaimed = true });
        return added;
    });

    // The 3.29.3b WoundedMale1Zombie/Rig.amd has bite at index 1. 0x1b2 kind zero plays the clip; speed zero pauses it.
    public static byte[] BiteAnimation(uint id, float speed) => new LoginWriter().U16(0x1b2).U32(id).U32(0).U16(0)
        .U8(0).U32(1).U32(BitConverter.SingleToUInt32Bits(speed)).ToArray();

    public static Placement? ChestPlacement(string area, uint seed, string folder = "data/walkability")
    {
        string file = Path.Combine(folder, $"{area}_seed{seed}.objects.json");
        // Without a capture, an area the Gateway generates itself has the same chest from the port (ServerLevels).
        if (!File.Exists(file))
            return ServerLevels.InPlaceOf(folder, area, seed)?.QuestObjects.FirstOrDefault(o => o.Type == Chest) is { } made
                ? new(Chest, made.Model, made.X, made.Y, made.Rotation)
                : null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        var root = doc.RootElement;
        if (root.GetProperty("area").GetString() != area || root.GetProperty("seed").GetUInt32() != seed) return null;
        foreach (var r in root.GetProperty("objects").EnumerateArray())
            if (r.GetProperty("objectType").GetString() == Chest && r.GetProperty("model").GetString() is { Length: > 0 } model)
                return new(Chest, model, r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(), r.GetProperty("rotation").GetSingle());
        return null;
    }

    // Chest reader 0x141e138b0 consumes three bytes: four flags, u8 +0x16c, u8 +0x16d.
    public static byte[] ChestBlob(Placement p, bool opened)
    {
        var w = new BlobWriter().U8(0).U8(0);
        Positioned(w,p.X,p.Y,p.Rotation);
        w.U8(1).WStr32(p.Model); // Animated: the .tmd supplies the model, not TutorialSupportGemChest.ot.
        w.U8((byte)(opened ? 1 : 0)).U8(0).U8(0);
        w.U8(0).U32(0).U32(0).U8(0); // Stats
        w.U16(0).U8(0).U8(0); // ObjectMagicProperties
        return w.U8(0).U8(0).ToArray(); // InteractionAction, tasks; BaseEvents consumes no bytes.
    }
    public static byte[] SceneBlob(int x,int y,float rotation)
    {
        var w = new BlobWriter().U8(0).U8(0); Positioned(w,x,y,rotation);
        return w.U8(0).U8(0).ToArray(); // Animated, tasks; BaseEvents consumes no bytes.
    }
    static void Positioned(BlobWriter w,int x,int y,float rotation) =>
        w.I32(x).I32(y).F32(rotation).U16(0).U8(0).U8(2).U8(0).U8(1);
}
