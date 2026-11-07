using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;
using static CharacterBuildImports;

static class PobRules
{
    static readonly SemaphoreSlim Converter = new(1,1);
    internal static CancellationToken Stopping;
    internal static async Task Prepare(ClientState conn, string[] args)
    {
        conn.BuildImport.Clear(); // An invalid replacement must never leave an older build available for Y.
        string? sourceFile = null, preparedJson = null;
        if (args.Length == 2 && args[0].Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            string name = args[1];
            if (name != Path.GetFileName(name) || name.Length > 80 || !(name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            { await conn.Commands!.Reply("PoB: нужен файл .txt или .json из data/pob-import/inbox."); return; }
            string path = Path.Combine("data", "pob-import", "inbox", name);
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 2 * 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Файл отсутствует, слишком большой или является ссылкой.");
                string content = File.ReadAllText(path).Trim();
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    using var snapshot = JsonDocument.Parse(content);
                    if (snapshot.RootElement.TryGetProperty("Version", out _))
                        preparedJson = content;
                    else { args = [snapshot.RootElement.GetProperty("pob").GetString() ?? ""]; sourceFile = path; }
                }
                else args = [content];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
            { await conn.Commands!.Reply("PoB: " + Short(e.Message)); return; }
        }
        if (preparedJson is null && (args.Length != 1 || args[0].Length is < 8 or > 60000))
        {
            await conn.Commands!.Reply("Использование: /pob <код PoB>. /pob cancel — отмена.");
            return;
        }
        if (conn.CharacterId is not { } id || conn.LeavingArea || conn.PendingTransfer is not null || conn.PlayerLife == 0)
        { await conn.Commands!.Reply("PoB: дождитесь входа живого персонажа в область."); return; }
        if (!await Converter.WaitAsync(0))
        { await conn.Commands!.Reply("PoB: конвертер занят. Повторите команду немного позже."); return; }
        try
        {
            var record = new SqliteAccountStore(Saves.Db).GetCharacter(id)
                ?? throw new InvalidDataException("Персонаж больше не существует.");
            if (record.AccountId.ToString() != conn.Account && record.AccountId.ToString("N") != conn.Account)
            {
                // Account is normally the login name; ownership is independently bound by the admitted UUID.
                if (!string.Equals(new SqliteAccountStore(Saves.Db).GetAccount(record.AccountId)?.Name, conn.Account, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Не удалось подтвердить владельца персонажа.");
            }
            await conn.Commands!.Reply("Спасибо, код принят. Проверяю билд; персонаж пока не изменён...");
            string text = preparedJson ?? await PobBridge.Convert(args[0], record, sourceFile, Stopping);
            var plan = PobBridge.Validate(text, record);
            if (conn.Closed || conn.LeavingArea || conn.PendingTransfer is not null) return;
            conn.BuildImport.Stage(plan);
            await conn.Commands.Reply($"Код PoB валидный. {plan.Summary}\nБудут заменены класс, уровень, дерево, экипировка, камни и джевелы этого персонажа.");
            foreach (string warning in plan.Warnings.Take(8)) await conn.Commands.Reply("PoB: " + Short(warning));
            if (plan.Warnings.Length > 8) await conn.Commands.Reply($"PoB: всего предупреждений {plan.Warnings.Length}; показаны первые 8.");
            await conn.Commands.Reply("Хотите применить подготовленный билд? Введите в чат Y в течение 5 минут. N или /pob cancel — отмена.");
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or JsonException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException or NullReferenceException)
        { await conn.Commands!.Reply("PoB не применён: " + (e is OperationCanceledException ? "время проверки истекло." : Short(e.Message))); }
        finally { Converter.Release(); }
    }

    internal static async Task<bool> Chat(ClientState conn, string text)
    {
        if (!conn.BuildImport.HasPending) return false;
        var (channel, body) = InstanceChat.SplitChannel(text);
        if (channel == TypedChannel.Whisper) return false;
        body = body.Trim();
        if (body.Equals("N", StringComparison.OrdinalIgnoreCase))
        { conn.BuildImport.Clear(); await conn.Commands!.Reply("Импорт PoB отменён."); return true; }
        if (!body.Equals("Y", StringComparison.OrdinalIgnoreCase)) return false;
        var plan = conn.BuildImport.Confirm();
        if (plan is null)
        { await conn.Commands!.Reply("Подтверждение PoB устарело. Введите /pob с кодом заново."); return true; }
        if (conn.PlayerLife == 0 || conn.LeavingArea || conn.PendingTransfer is not null || conn.CharacterId != plan.CharacterId
            || Program.InGame.Keys.Any(c => c != conn && c.CharacterId == plan.CharacterId && !c.Closed)
            || !Applying.TryAdd(plan.CharacterId, 0))
        { await conn.Commands!.Reply("PoB не применён: персонаж меняет область, погиб или открыт в другой сессии. Подготовьте билд заново."); return true; }
        try
        {
            await conn.Commands!.Reply("Применение PoB подтверждено. Релог персонажа через...");
            for (int seconds = 3; seconds > 0; seconds--)
            {
                if (conn.Closed || conn.PlayerLife == 0 || conn.LeavingArea || conn.PendingTransfer is not null) return true;
                await conn.Commands.Reply(seconds + "...");
                await Task.Delay(TimeSpan.FromSeconds(1), Stopping);
            }
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.PlayerLife == 0 || conn.LeavingArea || conn.PendingTransfer is not null) return true;
                conn.BuildImport.Accept(plan);
                conn.CloseReason = "confirmed PoB import";
                conn.Closed = true;
                conn.InvalidateIncomingHits();
            }
        }
        catch (OperationCanceledException) when (Stopping.IsCancellationRequested) { return true; }
        finally { if (conn.BuildImport.Confirmed is null) Applying.TryRemove(plan.CharacterId, out _); }
        return true;
    }

    static string Short(string text) => text.Replace('\r', ' ').Replace('\n', ' ') is var line && line.Length > 800 ? line[..800] : line;
}
