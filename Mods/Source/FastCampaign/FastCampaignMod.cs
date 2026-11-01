using GameServer.Modding;

namespace LocalExile.Mods.FastCampaign;

/// <summary>
/// A faster campaign: more experience from kills, extra movement speed, and /fast, which lists the Gateway's own commands
/// that skip ahead (waypoints, area travel, level). Off by default: /mod_enable fastcampaign.
/// The settings, the language of its chat lines included, are kept in settings.txt in the mod's folder; writing it reloads
/// the mod, and the new load reads them back.
/// </summary>
public sealed class FastCampaignMod : IMod
{
    const string SettingsFile = "settings.txt";
    const string SpeedStat = "base_movement_velocity_+%";
    const int MaxExperience = 100, MaxSpeed = 300;

    int _experience = 5;
    int _speed = 50;
    int _language;
    string _path = "";

    public void Load(IModContext mod)
    {
        _path = Path.Combine(mod.Folder, SettingsFile);
        Read();

        mod.ModifyExperience(gain =>
        {
            ulong times = (ulong)Volatile.Read(ref _experience);
            if (times > 1) gain.Experience = (uint)Math.Min(uint.MaxValue, gain.Experience * times);
        });
        mod.ModifyStats(sheet =>
        {
            int speed = Volatile.Read(ref _speed);
            if (speed != 0) sheet.Add(SpeedStat, speed);
        });

        mod.OnChatCommand("fast", "/fast (faster campaign: its settings, and the commands that skip ahead)", command => command.Player.Tell(Help()));
        mod.OnChatCommand("fast_xp", $"/fast_xp <1-{MaxExperience}> (kill experience multiplier)",
            command => SetNumber(command, ref _experience, 1, MaxExperience, Text.ExperienceSet));
        mod.OnChatCommand("fast_speed", $"/fast_speed <0-{MaxSpeed}> (extra movement speed, %)",
            command => SetNumber(command, ref _speed, 0, MaxSpeed, Text.SpeedSet));
        mod.OnChatCommand("fast_lang", $"/fast_lang <{string.Join(" | ", Text.Languages)}> (the language of /fast)", SetLanguage);
        mod.Log($"experience x{_experience}, movement speed +{_speed}%, language {Text.Languages[_language]}");
    }

    string Help() => string.Format(Text.Help[Volatile.Read(ref _language)], Volatile.Read(ref _experience), Volatile.Read(ref _speed));

    Task SetNumber(ChatCommand command, ref int setting, int min, int max, string[] done)
    {
        int language = Volatile.Read(ref _language);
        if (command.Args.Count != 1 || !int.TryParse(command.Args[0], out int value) || value < min || value > max)
            return command.Player.Tell(string.Format(Text.Usage[language], command.Name, min, max, Volatile.Read(ref setting)));
        Volatile.Write(ref setting, value);
        Write();
        return command.Player.Tell(string.Format(done[language], value));
    }

    Task SetLanguage(ChatCommand command)
    {
        int language = command.Args.Count == 1 ? Text.Find(command.Args[0]) : -1;
        if (language < 0) return command.Player.Tell($"/fast_lang <{string.Join(" | ", Text.Languages)}>");
        Volatile.Write(ref _language, language);
        Write();
        return command.Player.Tell(Text.LanguageSet[language]);
    }

    void Read()
    {
        if (!File.Exists(_path)) return;
        foreach (string line in File.ReadAllLines(_path))
        {
            string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (parts[0] == "language" && Text.Find(parts[1]) is >= 0 and var language) _language = language;
            else if (!int.TryParse(parts[1], out int value)) continue;
            else if (parts[0] == "xp") _experience = Math.Clamp(value, 1, MaxExperience);
            else if (parts[0] == "speed") _speed = Math.Clamp(value, 0, MaxSpeed);
        }
    }

    void Write() => File.WriteAllLines(_path,
    [
        $"xp={Volatile.Read(ref _experience)}",
        $"speed={Volatile.Read(ref _speed)}",
        $"language={Text.Languages[Volatile.Read(ref _language)]}",
    ]);

    /// <summary>The mod's chat lines, by language: English, Simplified Chinese, Traditional Chinese.</summary>
    static class Text
    {
        public static readonly string[] Languages = ["en", "zh-Hans", "zh-Hant"];

        public static int Find(string name) => Array.FindIndex(Languages, l => l.Equals(name, StringComparison.OrdinalIgnoreCase));

        public static readonly string[] Help =
        [
            string.Join('\n',
                "Faster campaign: kill experience x{0}, movement speed +{1}%",
                "Change: /fast_xp <multiplier>  /fast_speed <percent>  /fast_lang <en | zh-Hans | zh-Hant> (saved)",
                "The server's own commands that skip ahead:",
                "/waypoints all  activate every waypoint",
                "/goto <area id>  go to an area; the towns:",
                "  1_1_town ... 1_5_town (acts 1-5)  2_6_town ... 2_10_town (acts 6-10)",
                "/set_level <1-100>  set the level",
                "/add_passive_points <n>  the passive points of skipped quests",
                "/god  +1000% attack and cast speed and great regeneration; again to turn it off",
                "/com  every command"),
            string.Join('\n',
                "快速过剧情：击杀经验 ×{0}，移动速度 +{1}%",
                "修改：/fast_xp <倍率>　/fast_speed <百分比>　/fast_lang <en | zh-Hans | zh-Hant>（会保存）",
                "服务器自带的速通命令：",
                "/waypoints all　解锁全部传送点",
                "/goto <区域id>　直接去某个区域，各章城镇：",
                "　1_1_town … 1_5_town（第 1-5 章）　2_6_town … 2_10_town（第 6-10 章）",
                "/set_level <1-100>　直接设置等级",
                "/add_passive_points <n>　补回跳过任务少拿的天赋点",
                "/god　攻速施法 +1000%、大量回血回蓝；再输一次关闭",
                "/com　列出全部命令"),
            string.Join('\n',
                "快速過劇情：擊殺經驗 ×{0}，移動速度 +{1}%",
                "修改：/fast_xp <倍率>　/fast_speed <百分比>　/fast_lang <en | zh-Hans | zh-Hant>（會儲存）",
                "伺服器自帶的速通指令：",
                "/waypoints all　解鎖全部傳送點",
                "/goto <區域id>　直接前往某個區域，各章城鎮：",
                "　1_1_town … 1_5_town（第 1-5 章）　2_6_town … 2_10_town（第 6-10 章）",
                "/set_level <1-100>　直接設定等級",
                "/add_passive_points <n>　補回跳過任務少拿的天賦點",
                "/god　攻速施法 +1000%、大量回血回魔；再輸入一次關閉",
                "/com　列出全部指令"),
        ];

        public static readonly string[] Usage =
        [
            "Usage: /{0} <{1}-{2}>  now: {3}",
            "用法：/{0} <{1}-{2}>　当前：{3}",
            "用法：/{0} <{1}-{2}>　目前：{3}",
        ];

        public static readonly string[] ExperienceSet =
        [
            "Kill experience is now x{0}.",
            "击杀经验倍率已设为 ×{0}。",
            "擊殺經驗倍率已設為 ×{0}。",
        ];

        public static readonly string[] SpeedSet =
        [
            "Extra movement speed is now +{0}%, from the next area.",
            "额外移动速度已设为 +{0}%，进入下一个区域后生效。",
            "額外移動速度已設為 +{0}%，進入下一個區域後生效。",
        ];

        public static readonly string[] LanguageSet =
        [
            "/fast now answers in English.",
            "/fast 现在使用简体中文。",
            "/fast 現在使用繁體中文。",
        ];
    }
}
