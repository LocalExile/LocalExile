// The launcher's text in English, Simplified Chinese and Traditional Chinese. A key missing from a language falls back
// to English; a key missing everywhere shows as itself, so a typo is visible instead of blank.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace LocalExileGui
{
    static class I18n
    {
        public const string English = "en", Simplified = "zh-Hans", Traditional = "zh-Hant";
        public static readonly string[] Languages = { English, Simplified, Traditional };

        static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>(StringComparer.Ordinal);
        static int _column;

        public static string Current { get { return Languages[_column]; } }

        /// <summary>The language for a saved setting: one of <see cref="Languages"/>, or anything else for the system's.</summary>
        public static void Use(string setting)
        {
            int index = Array.IndexOf(Languages, setting);
            _column = index >= 0 ? index : Array.IndexOf(Languages, FromCulture(CultureInfo.CurrentUICulture));
        }

        public static string FromCulture(CultureInfo culture)
        {
            for (var c = culture; c != null && c.Name.Length > 0; c = c.Parent)
            {
                string name = c.Name;
                if (name == "zh-Hant" || name == "zh-TW" || name == "zh-HK" || name == "zh-MO" || name == "zh-CHT") return Traditional;
                if (name == "zh-Hans" || name == "zh-CN" || name == "zh-SG" || name == "zh-CHS" || name == "zh") return Simplified;
            }
            return English;
        }

        /// <summary>The name of a language in itself, for the language picker.</summary>
        public static string NativeName(string language)
        {
            switch (language)
            {
                case Simplified: return "简体中文";
                case Traditional: return "繁體中文";
                default: return "English";
            }
        }

        public static string UiFont
        {
            get
            {
                switch (Current)
                {
                    case Simplified: return "Microsoft YaHei UI";
                    case Traditional: return "Microsoft JhengHei UI";
                    default: return "Segoe UI";
                }
            }
        }

        public static string T(string key)
        {
            string[] row;
            if (!Table.TryGetValue(key, out row)) return key;
            return row[_column] ?? row[0];
        }

        public static string T(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }

        static void Add(string key, string en, string hans, string hant)
        {
            Table.Add(key, new[] { en, hans, hant });
        }

        static I18n()
        {
            Add("app.title", "LocalExile Launcher", "LocalExile 启动器", "LocalExile 啟動器");
            Add("app.noExe", "LocalExile.exe was not found next to this program.\nPut LocalExileGUI.exe in the same folder as LocalExile.exe.",
                "没有在本程序所在目录找到 LocalExile.exe。\n请把 LocalExileGUI.exe 放到 LocalExile.exe 旁边。",
                "沒有在本程式所在目錄找到 LocalExile.exe。\n請把 LocalExileGUI.exe 放到 LocalExile.exe 旁邊。");

            Add("exit.0", "Done", "完成", "完成");
            Add("exit.1", "Unexpected error; see the log", "意外错误，请查看日志", "意外錯誤，請查看日誌");
            Add("exit.2", "Bad input (for example an invalid account name, a weak password or an account that already exists)",
                "参数有误（例如账号名不合法、密码太弱、账号已存在）", "參數有誤（例如帳號名稱不合法、密碼太弱、帳號已存在）");
            Add("exit.3", "Client not usable: the standalone Path of Exile 3.29.3b client is required",
                "客户端不可用：需要 Path of Exile 3.29.3b 独立版客户端", "用戶端無法使用：需要 Path of Exile 3.29.3b 獨立版用戶端");
            Add("exit.4", "Extracting the game data failed; see the logs folder", "游戏数据解包失败，请查看 logs 目录", "遊戲資料解包失敗，請查看 logs 資料夾");
            Add("exit.5", "A server failed; see the logs folder", "服务器出错，请查看 logs 目录", "伺服器出錯，請查看 logs 資料夾");
            Add("exit.6", "A port is in use, or LocalExile is already running (try Stop first)",
                "端口被占用，或 LocalExile 已在运行（可以先点“停止”）", "連接埠被佔用，或 LocalExile 已在執行（可以先按「停止」）");
            Add("exit.7", "More input needed: there is no local account yet; create one on the Setup or Account tab",
                "需要更多信息：还没有本地账号，请在“首次设置”或“账号”页创建", "需要更多資訊：還沒有本機帳號，請在「首次設定」或「帳號」頁建立");
            Add("exit.8", "The game client failed to start", "游戏客户端启动失败", "遊戲用戶端啟動失敗");
            Add("exit.unknown", "Unknown exit code {0}", "未知退出码 {0}", "未知結束代碼 {0}");

            Add("step.client-check", "Checking the client", "检查客户端", "檢查用戶端");
            Add("step.client-copy", "Copying the client", "复制客户端", "複製用戶端");
            Add("step.extract", "Extracting the game data", "解包游戏数据", "解包遊戲資料");
            Add("step.keys", "Checking the login keys", "检查登录密钥", "檢查登入金鑰");
            Add("step.account", "Checking the account", "检查账号", "檢查帳號");
            Add("step.login-list", "Pointing the client at the local login server", "设置客户端的登录服务器", "設定用戶端的登入伺服器");
            Add("step.servers", "Starting the servers", "启动服务器", "啟動伺服器");
            Add("step.client", "Starting the game client", "启动游戏客户端", "啟動遊戲用戶端");

            Add("mod.fastcampaign", "Faster campaign: 5× kill experience, +50% movement speed (/fast in game shows and changes it, with the speed-run commands)",
                "快速过剧情：击杀经验 ×5、移动速度 +50%（游戏里 /fast 查看和调整，并列出速通命令）",
                "快速過劇情：擊殺經驗 ×5、移動速度 +50%（遊戲內 /fast 查看與調整，並列出速通指令）");
            Add("mod.doubleexperience", "2× experience from kills", "击杀获得的经验 ×2", "擊殺獲得的經驗 ×2");
            Add("mod.itemquantity", "10× as many item drops", "物品掉落数量 ×10", "物品掉落數量 ×10");
            Add("mod.itemrarity", "10× the chance of magic, rare and unique equipment", "魔法、稀有、传奇装备的出现几率 ×10", "魔法、稀有、傳奇裝備的出現機率 ×10");
            Add("mod.bossloot", "An extra Exalted Orb from every unique monster", "每个传奇怪物额外掉落一个崇高石", "每個傳奇怪物額外掉落一個崇高石");
            Add("mod.hello", "Example: chat command /hello [name]", "示例：聊天命令 /hello [名字]", "範例：聊天指令 /hello [名字]");
            Add("mod.example", "Example: chat command /example, counting kills and areas", "示例：聊天命令 /example，统计击杀和进入的区域", "範例：聊天指令 /example，統計擊殺與進入的區域");

            Add("tab.play", "Play", "开始游戏", "開始遊戲");
            Add("tab.setup", "Setup", "首次设置", "首次設定");
            Add("tab.mods", "Mods", "Mod 管理", "Mod 管理");
            Add("tab.account", "Account", "账号", "帳號");
            Add("tab.check", "Check", "环境检查", "環境檢查");
            Add("status.ready", "Ready", "就绪", "就緒");
            Add("common.refresh", "Refresh", "刷新", "重新整理");
            Add("common.none", "(none)", "（无）", "（無）");

            Add("play.start", "Start game", "启动游戏", "啟動遊戲");
            Add("play.stop", "Stop", "停止", "停止");
            Add("play.updateCheck", "Check for a newer LocalExile at start (only tells; never downloads)",
                "启动时检查 LocalExile 是否有新版本（只提示，不会自动下载）", "啟動時檢查 LocalExile 是否有新版本（只提示，不會自動下載）");
            Add("play.serversOnly", "Start only the servers, not the game client", "只启动服务器，不启动游戏客户端", "只啟動伺服器，不啟動遊戲用戶端");
            Add("play.hint",
                "· To log in, put your LocalExile account name in the Email field of the game's login screen, then the password.\n" +
                "· Once in game, set Options > Game > Network Mode to Predictive (the only mode the server supports).\n" +
                "· Always start the game with LocalExile: the official launcher or Steam updates the client, and an updated client no longer works.\n" +
                "· Closing the game client stops the servers.",
                "· 登录时，在游戏登录界面的 Email 一栏填你的 LocalExile 账号名，再填密码。\n" +
                "· 第一次进游戏后，在 Options > Game > Network Mode 中选择 Predictive（服务器只支持这一种）。\n" +
                "· 请始终用 LocalExile 启动游戏：用官方启动器或 Steam 打开会触发自动更新，更新后就无法再用。\n" +
                "· 关闭游戏客户端会自动停止服务器。",
                "· 登入時，在遊戲登入畫面的 Email 欄位填入你的 LocalExile 帳號名稱，再填密碼。\n" +
                "· 第一次進遊戲後，在 Options > Game > Network Mode 中選擇 Predictive（伺服器只支援這一種）。\n" +
                "· 請一律用 LocalExile 啟動遊戲：用官方啟動器或 Steam 開啟會觸發自動更新，更新後就無法再用。\n" +
                "· 關閉遊戲用戶端會自動停止伺服器。");
            Add("play.openLogs", "Open logs folder", "打开日志文件夹", "開啟日誌資料夾");
            Add("play.openRoot", "Open install folder", "打开安装目录", "開啟安裝目錄");
            Add("play.clearLog", "Clear log", "清空下方日志", "清除下方日誌");
            Add("play.language", "Language", "语言", "語言");
            Add("lang.auto", "Automatic (system)", "自动（跟随系统）", "自動（跟隨系統）");
            Add("lang.restart", "The language changes when the launcher restarts. Restart now?", "重启启动器后切换语言。现在重启吗？", "重新啟動啟動器後切換語言。現在重新啟動嗎？");
            Add("lang.later", "The language changes the next time the launcher starts.", "下次打开启动器时切换语言。", "下次開啟啟動器時切換語言。");

            Add("setup.clientDir", "Client folder", "客户端目录", "用戶端目錄");
            Add("setup.browse", "Browse…", "浏览…", "瀏覽…");
            Add("setup.browseTitle", "Choose the Path of Exile 3.29.3b install folder (the one with PathOfExile.exe)",
                "选择 Path of Exile 3.29.3b 的安装目录（包含 PathOfExile.exe 的文件夹）", "選擇 Path of Exile 3.29.3b 的安裝目錄（包含 PathOfExile.exe 的資料夾）");
            Add("setup.mode", "Mode", "使用方式", "使用方式");
            Add("setup.copy", "Make a copy (recommended; keeps the original)", "复制一份（推荐，原客户端不会被改动）", "複製一份（建議，原用戶端不會被改動）");
            Add("setup.link", "Use the folder itself (changes it)", "直接使用原目录（会修改它）", "直接使用原目錄（會修改它）");
            Add("setup.check", "Check client", "检查客户端", "檢查用戶端");
            Add("setup.run", "Start setup", "开始设置", "開始設定");
            Add("setup.hint",
                "The client must be the standalone Path of Exile 3.29.3b. The copy takes about 65 GB, and extracting the data a few minutes.\n" +
                "With a local account already, the account fields can stay empty. Account names are 3-32 letters, digits, _ or -; passwords at least 4 characters. Do not reuse a real password.",
                "客户端必须是 Path of Exile 3.29.3b 独立版。复制约 65GB，解包数据需要几分钟。\n" +
                "已经有本地账号时可以不填账号和密码。账号名 3-32 位，只能用字母、数字、_ 或 -；密码至少 4 位，不要用你在别处用过的密码。",
                "用戶端必須是 Path of Exile 3.29.3b 獨立版。複製約 65GB，解包資料需要幾分鐘。\n" +
                "已經有本機帳號時可以不填帳號和密碼。帳號名稱 3-32 位，只能用字母、數字、_ 或 -；密碼至少 4 位，不要用你在別處用過的密碼。");
            Add("setup.needDir", "Choose the client folder first.", "请先选择客户端目录。", "請先選擇用戶端目錄。");
            Add("setup.clientOk", "Client usable (3.29.3b)", "客户端可用（3.29.3b）", "用戶端可用（3.29.3b）");
            Add("setup.needAccount", "There is no local account yet: fill in an account name and a password.",
                "还没有本地账号，请填写账号名和密码。", "還沒有本機帳號，請填寫帳號名稱和密碼。");
            Add("setup.linkWarn", "\"Use the folder itself\" changes the client in that folder. Continue?",
                "“直接使用原目录”会修改那个文件夹里的客户端。确定继续吗？", "「直接使用原目錄」會修改那個資料夾裡的用戶端。確定繼續嗎？");
            Add("setup.running", "Setting up …", "正在设置 …", "正在設定 …");

            Add("field.account", "Account name", "账号名", "帳號名稱");
            Add("field.password", "Password", "密码", "密碼");
            Add("field.password2", "Confirm password", "确认密码", "確認密碼");
            Add("field.newPassword", "New password", "新密码", "新密碼");
            Add("field.newPassword2", "Confirm new password", "确认新密码", "確認新密碼");

            Add("mods.version", "Version", "版本", "版本");
            Add("mods.effect", "What it does", "作用", "作用");
            Add("mods.save", "Save", "保存", "儲存");
            Add("mods.open", "Open mods folder", "打开 Mod 文件夹", "開啟 Mod 資料夾");
            Add("mods.hint", "Checked mods are on. Saved changes apply the next time the game starts; in game, /mod_enable <id> and /mod_disable <id> switch one at once.",
                "勾选表示启用。保存后在下次启动游戏时生效；游戏运行中也可以在聊天框用 /mod_enable <id> 或 /mod_disable <id> 立即切换。",
                "勾選表示啟用。儲存後在下次啟動遊戲時生效；遊戲執行中也可以在聊天框用 /mod_enable <id> 或 /mod_disable <id> 立即切換。");
            Add("mods.loadFailed", "Could not read the mods: {0}", "读取 Mod 失败：{0}", "讀取 Mod 失敗：{0}");
            Add("mods.saved", "Mod settings saved. On: {0}", "Mod 设置已保存。启用：{0}", "Mod 設定已儲存。啟用：{0}");
            Add("mods.savedStatus", "Mod settings saved", "Mod 设置已保存", "Mod 設定已儲存");
            Add("mods.savedRunning", "Mod settings saved; they apply the next time the game starts", "Mod 设置已保存，下次启动游戏时生效", "Mod 設定已儲存，下次啟動遊戲時生效");
            Add("mods.saveFailed", "Could not save the mods: {0}", "保存 Mod 失败：{0}", "儲存 Mod 失敗：{0}");

            Add("account.count", "Local accounts: {0}", "本地账号数量：{0}", "本機帳號數量：{0}");
            Add("account.readFailed", "could not read", "读取失败", "讀取失敗");
            Add("account.newTitle", "New account", "新建账号", "新增帳號");
            Add("account.create", "Create account", "创建账号", "建立帳號");
            Add("account.passwd", "Change password", "修改密码", "變更密碼");
            Add("account.hint", "Accounts stay on this PC and have nothing to do with official accounts. In the game's login screen the account name goes in the Email field.\nA changed password works once the game restarts.",
                "账号只保存在本机，与官方账号无关。登录游戏时，账号名填在 Email 一栏。\n修改密码后要重新启动游戏才生效。",
                "帳號只儲存在本機，與官方帳號無關。登入遊戲時，帳號名稱填在 Email 欄位。\n變更密碼後要重新啟動遊戲才生效。");
            Add("account.badName", "Account names are 3-32 letters, digits, _ or -.", "账号名需要 3-32 位，只能用字母、数字、_ 或 -。", "帳號名稱需要 3-32 位，只能用字母、數字、_ 或 -。");
            Add("account.shortPassword", "The password needs at least 4 characters.", "密码至少 4 位。", "密碼至少 4 位。");
            Add("account.mismatch", "The passwords differ.", "两次输入的密码不一致。", "兩次輸入的密碼不一致。");
            Add("account.created", "Account {0} created", "账号 {0} 已创建", "帳號 {0} 已建立");
            Add("account.createFailed", "Creating the account failed:\n{0}", "创建失败：\n{0}", "建立失敗：\n{0}");
            Add("account.passwdDone", "The password of {0} was changed.", "账号 {0} 的密码已修改。", "帳號 {0} 的密碼已變更。");
            Add("account.passwdStatus", "Password changed", "密码已修改", "密碼已變更");
            Add("account.passwdRestart", "Password changed; it works once the game restarts", "密码已修改，重新启动游戏后生效", "密碼已變更，重新啟動遊戲後生效");
            Add("account.passwdFailed", "Changing the password failed:\n{0}", "修改失败：\n{0}", "變更失敗：\n{0}");

            Add("check.run", "Run full check", "运行全面检查", "執行完整檢查");
            Add("check.hint", "Checks every part of LocalExile: the client version, the game data, the login keys, the patched client, the local accounts and the mods. The results go to the log below.",
                "检查 LocalExile 的各个部分是否就绪：客户端版本、游戏数据、登录密钥、修改版客户端、本地账号和 Mod 状态。结果显示在下方日志里。",
                "檢查 LocalExile 的各個部分是否就緒：用戶端版本、遊戲資料、登入金鑰、修改版用戶端、本機帳號和 Mod 狀態。結果顯示在下方日誌裡。");
            Add("check.codes", "Exit codes:", "退出码说明：", "結束代碼說明：");
            Add("check.client", "Client ({0}): {1}", "客户端（{0}）：{1}", "用戶端（{0}）：{1}");
            Add("check.redoSetup", "set it up again on the Setup tab", "到“首次设置”页重新设置", "到「首次設定」頁重新設定");
            Add("check.noClient", "The client has not been copied yet (no Client folder)", "还没有复制客户端（Client 目录不存在）", "還沒有複製用戶端（Client 資料夾不存在）");
            Add("check.noClientFix", "choose the client on the Setup tab and start the setup", "到“首次设置”页选择客户端并开始设置", "到「首次設定」頁選擇用戶端並開始設定");
            Add("check.data", "Game data extracted", "游戏数据已解包", "遊戲資料已解包");
            Add("check.runSetup", "press Start setup on the Setup tab", "到“首次设置”页点“开始设置”", "到「首次設定」頁按「開始設定」");
            Add("check.keys", "Login keys made", "登录密钥已生成", "登入金鑰已產生");
            Add("check.patched", "Patched client Game.patched.exe present", "修改版客户端 Game.patched.exe 存在", "修改版用戶端 Game.patched.exe 存在");
            Add("check.patchedFix", "an antivirus may have removed it: allow it, then run the setup again",
                "可能被杀毒软件删除了：把它加入白名单后重新运行设置", "可能被防毒軟體刪除了：把它加入白名單後重新執行設定");
            Add("check.accountsFix", "create one on the Account tab", "到“账号”页创建一个", "到「帳號」頁建立一個");
            Add("check.mods", "· Mods on: {0}", "· 已启用的 Mod：{0}", "· 已啟用的 Mod：{0}");
            Add("check.allOk", "Every check passed; the game is ready.", "全部检查通过，可以开始游戏。", "全部檢查通過，可以開始遊戲。");
            Add("check.problems", "{0} problem(s) found; follow the hints.", "发现 {0} 个问题，请按提示处理。", "發現 {0} 個問題，請依提示處理。");
            Add("check.okStatus", "Check passed", "环境检查通过", "環境檢查通過");
            Add("check.problemsStatus", "Check found {0} problem(s)", "环境检查发现 {0} 个问题", "環境檢查發現 {0} 個問題");
            Add("check.failed", "The check failed: {0}", "检查时出错：{0}", "檢查時出錯：{0}");

            Add("event.warning", "  warning: {0}", "  警告：{0}", "  警告：{0}");
            Add("event.needsInput", "More input needed: {0}", "需要更多信息：{0}", "需要更多資訊：{0}");
            Add("event.clientStarting", "The game client is starting (about a minute). Closing the game stops the servers.",
                "游戏客户端正在启动（大约需要一分钟）。关闭游戏会自动停止服务器。", "遊戲用戶端正在啟動（大約需要一分鐘）。關閉遊戲會自動停止伺服器。");
            Add("event.clientStarted", "The client started. To log in, put the account name in the Email field.",
                "客户端已启动。登录时 Email 一栏填账号名。", "用戶端已啟動。登入時 Email 欄位填帳號名稱。");
            Add("event.serversReady", "Servers ready: login 127.0.0.1:{0}, game 127.0.0.1:{1}",
                "服务器已就绪：登录 127.0.0.1:{0}，游戏 127.0.0.1:{1}", "伺服器已就緒：登入 127.0.0.1:{0}，遊戲 127.0.0.1:{1}");
            Add("event.serversReadyLog", "Servers ready.", "服务器已就绪。", "伺服器已就緒。");
            Add("event.error", "Error ({0}): {1}", "错误（{0}）：{1}", "錯誤（{0}）：{1}");

            Add("job.setup", "Setup", "设置", "設定");
            Add("job.run", "Run", "运行", "執行");
            Add("exit.interruptedStatus", "Setup interrupted", "设置已中断", "設定已中斷");
            Add("exit.interruptedLog", "Setup interrupted; press Start setup again when you want.", "设置已中断，需要时再点“开始设置”重新进行。", "設定已中斷，需要時再按「開始設定」重新進行。");
            Add("exit.stopped", "Stopped", "已停止", "已停止");
            Add("exit.stoppedLog", "The servers and the game stopped.", "已停止服务器和游戏。", "已停止伺服器和遊戲。");
            Add("exit.setupDone", "Setup finished; start the game on the Play tab", "设置完成，可以到“开始游戏”页启动了", "設定完成，可以到「開始遊戲」頁啟動了");
            Add("exit.ended", "Finished", "已结束", "已結束");
            Add("exit.endedLog", "{0} finished.", "{0}结束。", "{0}結束。");
            Add("exit.failedStatus", "{0} failed: {1}", "{0}失败：{1}", "{0}失敗：{1}");
            Add("exit.failedLog", "{0} failed, exit code {1}: {2}", "{0}失败，退出码 {1}：{2}", "{0}失敗，結束代碼 {1}：{2}");
            Add("exit.chooseClient", "Choose the right client folder on the Setup tab.", "请到“首次设置”页选择正确的客户端目录。", "請到「首次設定」頁選擇正確的用戶端目錄。");

            Add("first.ready", "LocalExile launcher ready; install folder: {0}", "LocalExile 启动器已就绪，安装目录：{0}", "LocalExile 啟動器已就緒，安裝目錄：{0}");
            Add("first.notSetUp", "The first setup is not done yet: choose the client folder on the Setup tab and press Start setup.",
                "看起来还没有完成首次设置：请到“首次设置”页选择客户端目录并点“开始设置”。", "看起來還沒有完成首次設定：請到「首次設定」頁選擇用戶端目錄並按「開始設定」。");
            Add("first.notSetUpStatus", "First setup not done", "尚未完成首次设置", "尚未完成首次設定");

            Add("run.starting", "Starting …", "正在启动 …", "正在啟動 …");
            Add("run.serversOnly", "Starting only the servers …", "只启动服务器 …", "只啟動伺服器 …");
            Add("run.game", "Starting the game …", "启动游戏 …", "啟動遊戲 …");
            Add("run.cannotStart", "Could not start LocalExile.exe: {0}", "无法启动 LocalExile.exe：{0}", "無法啟動 LocalExile.exe：{0}");
            Add("stop.stopping", "Stopping …", "正在停止 …", "正在停止 …");
            Add("stop.log", "Stopping the servers and the game …", "停止服务器和游戏 …", "停止伺服器和遊戲 …");
            Add("stop.ended", "  ended {0}", "  已结束 {0}", "  已結束 {0}");
            Add("stop.nothing", "  No LocalExile process was running.", "  没有正在运行的 LocalExile 进程。", "  沒有正在執行的 LocalExile 程序。");
            Add("stop.failed", "Stopping failed: {0}", "停止失败：{0}", "停止失敗：{0}");

            Add("close.setup", "The setup is still running. Closing the launcher interrupts it. Close anyway?",
                "设置还在进行中。关闭启动器会中断设置，确定关闭吗？", "設定還在進行中。關閉啟動器會中斷設定，確定關閉嗎？");
            Add("close.run", "The game or the servers are still running. Closing the launcher stops them. Close anyway?",
                "游戏或服务器还在运行。关闭启动器会同时停止它们，确定关闭吗？", "遊戲或伺服器還在執行。關閉啟動器會同時停止它們，確定關閉嗎？");
        }
    }
}
