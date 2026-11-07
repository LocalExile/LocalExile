using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GameServer.Core.Accounts;

sealed class PobSettings
{
    // Operator-owned executable and fixed arguments. Chat input is sent only through stdin.
    public string ConverterExecutable { get; set; } = "";
    public string[] ConverterArguments { get; set; } = [];
}

static class PobBridge
{
    internal const int Version = 1, MaxOutput = 8 * 1024 * 1024;
    internal static PobSettings Settings = new();

    internal static async Task<string> Convert(string code, Character record, string? sourceFile, CancellationToken stopping)
    {
        string executable = Settings.ConverterExecutable;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidDataException("Внешний конвертер не настроен (Modules/POB/README.md).");
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in Settings.ConverterArguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить внешний конвертер.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var output = ReadLimited(process.StandardOutput, MaxOutput, timeout.Token);
        var error = ReadLimited(process.StandardError, 8192, timeout.Token);
        // Cancel the process as soon as either pipe exceeds its limit, even if it stops writing or never exits.
        _ = output.ContinueWith(_ => timeout.Cancel(), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = error.ContinueWith(_ => timeout.Cancel(), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
            {
                Version, Code = code, Name = record.Name, Data = Path.GetFullPath("data"),
                Record = record, SourceFile = sourceFile is null ? null : Path.GetFullPath(sourceFile),
            }).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            string text = await output, errors = await error;
            if (process.ExitCode != 0) throw new InvalidDataException(errors.Trim() is { Length: > 0 } e ? e : "Внешний конвертер отклонил билд.");
            return text;
        }
        catch (OperationCanceledException) when (output.IsFaulted || error.IsFaulted)
        {
            if (output.IsFaulted) await output;
            await error;
            throw;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(output, error); } catch (Exception e) when (e is OperationCanceledException or InvalidDataException or IOException) { }
        }
    }

    internal static async Task<string> ReadLimited(TextReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder();
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (read > limit - result.Length) throw new InvalidDataException("Ответ внешнего конвертера слишком большой.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }

    internal static CharacterBuildImports.Prepared Validate(string text, Character record)
    {
        if (text.Length > MaxOutput) throw new InvalidDataException("Ответ внешнего конвертера слишком большой.");
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        if (root.GetProperty("Version").GetInt32() != Version)
            throw new InvalidDataException("Неподдерживаемая версия формата импорта.");
        var converted = root.GetProperty("Record").Deserialize<Character>()
            ?? throw new InvalidDataException("Нет персонажа в результате импорта.");
        var save = root.GetProperty("Save").Deserialize<CharacterSaves.Save>()
            ?? throw new InvalidDataException("Нет сохранения в результате импорта.");
        if (converted.Id != record.Id || converted.AccountId != record.AccountId || converted.Name != record.Name
            || converted.League != record.League || converted.Level is < 1 or > 100 || !Enum.IsDefined(converted.Class)
            || save.Inventories is null || save.Inventories.Keys.Any(r => !CharacterBuildImports.Replaced.Contains(r))
            || save.Inventories.Values.Any(v => v is null) || save.Inventories.Values.Sum(v => v.Count) > 1024)
            throw new InvalidDataException("Некорректный результат внешнего конвертера.");
        foreach (var item in save.Inventories.Values.SelectMany(v => v))
            _ = CharacterSaves.FromSaved(item.Item, Program.Items).Payload();
        string summary = root.GetProperty("Summary").GetString() ?? "";
        string[] warnings = root.GetProperty("Warnings").Deserialize<string[]>() ?? [];
        if (summary.Length > 800 || warnings.Length > 100 || warnings.Any(w => w is null || w.Length > 800))
            throw new InvalidDataException("Слишком длинное описание результата импорта.");
        return new(record.Id, record.AccountId, record.League, converted.Class, converted.Level, save, summary, warnings);
    }
}
