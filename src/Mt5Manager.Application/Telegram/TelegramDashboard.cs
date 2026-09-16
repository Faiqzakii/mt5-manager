using System.Text;

namespace Mt5Manager.Application.Telegram;

public static class TelegramDashboard
{
    public const int MaxMessageLength = 4096;
    private static readonly TelegramKeyboard EmptyKeyboard = new([]);

    public static TelegramMessage Main(TelegramConnectionState state, string? publicIp)
    {
        var status = state.IsConnected ? "🟢 Terhubung" : "🔴 Terputus";
        var bot = string.IsNullOrWhiteSpace(state.BotUsername) ? "-" : $"@{state.BotUsername.TrimStart('@')}";
        var error = string.IsNullOrWhiteSpace(state.Error) ? "" : $"\nKesalahan: {state.Error}";
        var ip = string.IsNullOrWhiteSpace(publicIp) ? "tidak tersedia" : publicIp;
        return new TelegramMessage(
            $"🤖 MT5 Manager\n{status} · {state.TerminalCount} terminal aktif\nBot: {bot}\nIP Publik VPS: {ip}{error}",
            Keyboard(
                [Button("✅ ON Terminal", "pick:on"), Button("⛔ OFF Terminal", "pick:off")],
                [Button("✅ ON Semua", "all:on"), Button("⛔ OFF Semua", "all:off")],
                [Button("🔄 Refresh", "status")]));
    }

    public static TelegramMessage TerminalPicker(bool enable, IReadOnlyList<TelegramTerminal> terminals, string sessionToken)
    {
        ArgumentNullException.ThrowIfNull(terminals);
        ValidateToken(sessionToken);
        var action = enable ? "mengaktifkan" : "menonaktifkan";
        var operation = enable ? "on" : "off";
        var rows = terminals.Select(terminal => (IReadOnlyList<TelegramButton>)[
            Button(ButtonLabel(terminal), Callback($"terminal:{operation}:{terminal.Id:N}:{sessionToken}"))]).ToList();
        rows.Add([Button("↩️ Kembali", "status")]);
        var details = terminals.Count == 0 ? "Tidak ada terminal dengan bridge aktif." : string.Join("\n\n", terminals.Select(TerminalDetails));
        return new TelegramMessage($"📊 Pilih terminal untuk {action} Algo Trading:\n\n{details}", new TelegramKeyboard(rows));
    }

    public static TelegramMessage ConfirmTerminal(bool enable, bool currentlyEnabled, TelegramTerminal terminal, string sessionToken)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (string.IsNullOrWhiteSpace(terminal.Server) && string.IsNullOrWhiteSpace(terminal.AccountName))
            return Confirmation($"{terminal.Name} — {AvailableLogin(terminal)}\nStatus saat ini: {(currentlyEnabled ? "ON" : "OFF")}\nStatus diminta: {(enable ? "ON" : "OFF")}\n\nGlobal Algo Trading memengaruhi setiap EA di terminal ini.\nLanjutkan?", sessionToken);
        return Confirmation($"⚠️ Konfirmasi Perubahan\n\n{TerminalIdentity(terminal)}\n🔢 {Login(terminal.Login)}\n\nStatus saat ini: {State(currentlyEnabled)}\nStatus diminta: {State(enable)}\n\nGlobal Algo Trading memengaruhi setiap EA di terminal ini.\nLanjutkan?", sessionToken);
    }

    public static TelegramMessage ConfirmAll(bool enable, IReadOnlyList<TelegramTerminal> terminals, string sessionToken)
    {
        ArgumentNullException.ThrowIfNull(terminals);
        var header = $"⚠️ {Action(enable)} Algo Trading untuk {terminals.Count} terminal?";
        var disclosure = "Global Algo Trading memengaruhi setiap EA di setiap terminal yang tercantum.";
        var text = terminals.Count == 0
            ? $"{header}\n\n{disclosure}"
            : string.Join("\n", [header, "", .. terminals.Select(BulkTarget), "", disclosure]);
        return Confirmation(text, sessionToken);
    }
    public static TelegramMessage NoBulkTargets(bool enable) =>
        new(enable
            ? "Semua terminal yang tersedia sudah ON."
            : "Semua terminal yang tersedia sudah OFF.", EmptyKeyboard);


    public static TelegramMessage Processing(bool enable, int terminalCount) =>
        new($"{Action(enable)} Algo Trading untuk {terminalCount} terminal sedang diproses…", EmptyKeyboard);

    public static TelegramMessage Cancelled() => new("Dibatalkan.", EmptyKeyboard);

    public static IReadOnlyList<string> Results(IReadOnlyList<TelegramTerminalResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var sections = new List<(string Header, IReadOnlyList<string> Entries)>();
        AddSection(sections, "✅ Berhasil", results.Where(x => x.Outcome == TelegramTerminalOutcome.Changed).Select(ResultLine).ToArray());
        AddSection(sections, "ℹ️ Sudah ON/OFF", results.Where(x => x.Outcome == TelegramTerminalOutcome.AlreadyInRequestedState).Select(ResultLine).ToArray());
        AddSection(sections, "❌ Gagal", results.Where(x => x.Outcome == TelegramTerminalOutcome.Failed).Select(ResultLine).ToArray());
        if (sections.Count == 0) return ["Tidak ada hasil operasi."];

        var messages = new List<string>();
        foreach (var section in sections)
            AppendSection(messages, section.Header, section.Entries);
        return messages;
    }

    private static void AppendSection(List<string> messages, string header, IReadOnlyList<string> entries)
    {
        var current = new StringBuilder(header);
        foreach (var entry in entries)
        {
            if (current.Length + 1 + entry.Length <= MaxMessageLength)
            {
                current.Append('\n').Append(entry);
                continue;
            }
            if (current.Length > header.Length) messages.Add(current.ToString());
            current.Clear().Append(header);
            if (header.Length + 1 + entry.Length <= MaxMessageLength)
            {
                current.Append('\n').Append(entry);
                continue;
            }
            AppendOversized(messages, header, entry);
            current.Clear().Append(header);
        }
        if (current.Length > header.Length) messages.Add(current.ToString());
    }

    private static void AppendOversized(List<string> messages, string header, string entry)
    {
        var prefix = header + "\n";
        var offset = 0;
        while (offset < entry.Length)
        {
            var capacity = offset == 0 ? MaxMessageLength - prefix.Length : MaxMessageLength;
            var length = Math.Min(capacity, entry.Length - offset);
            if (length < entry.Length - offset && char.IsHighSurrogate(entry[offset + length - 1])) length--;
            var chunk = entry.Substring(offset, length);
            messages.Add(offset == 0 ? prefix + chunk : chunk);
            offset += length;
        }
    }

    private static string ResultLine(TelegramTerminalResult result)
    {
        var suffix = result.Outcome != TelegramTerminalOutcome.Failed || string.IsNullOrWhiteSpace(result.Error) ? "" : $": {result.Error}";
        var identity = string.IsNullOrWhiteSpace(result.Server) && string.IsNullOrWhiteSpace(result.AccountName)
            ? $"{result.TerminalName} — {Login(result.Login)}"
            : Identity(result.Server, result.Login, result.AccountName, result.TerminalName);
        return $"• {identity}{suffix}";
    }

    private static string ButtonLabel(TelegramTerminal terminal) =>
        $"{Value(terminal.Server, terminal.Name)} · {AvailableLogin(terminal)}";
    private static string TerminalDetails(TelegramTerminal terminal) =>
        $"{TerminalIdentity(terminal)}\n🔢 {AvailableLogin(terminal)} · {State(terminal.CurrentlyEnabled)}";
    private static string TerminalIdentity(TelegramTerminal terminal) =>
        $"🏦 {Value(terminal.Server, terminal.Name)}\n👤 {Value(terminal.AccountName, "Nama akun tidak tersedia")}";
    private static string Identity(string? server, string? login, string? accountName, string fallbackName) =>
        $"{Value(server, fallbackName)} · {Login(login)} · {Value(accountName, "Nama akun tidak tersedia")}";
    private static string Value(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    private static string AvailableLogin(TelegramTerminal terminal) => terminal.IsAvailable ? Login(terminal.Login) : "akun tidak tersedia";
    private static string Login(string? login) => string.IsNullOrWhiteSpace(login) ? "akun tidak tersedia" : login;
    private static string BulkTarget(TelegramTerminal terminal) =>
        string.IsNullOrWhiteSpace(terminal.Server) && string.IsNullOrWhiteSpace(terminal.AccountName)
            ? $"• {terminal.Name} — {AvailableLogin(terminal)} ({LegacyState(terminal.CurrentlyEnabled)})"
            : $"• {Identity(terminal.Server, terminal.Login, terminal.AccountName, terminal.Name)} · {State(terminal.CurrentlyEnabled)}";
    private static string State(bool? enabled) => enabled is null ? "⚪ tidak diketahui" : enabled.Value ? "🟢 ON" : "🔴 OFF";
    private static string LegacyState(bool? enabled) => enabled is null ? "tidak diketahui" : enabled.Value ? "ON" : "OFF";
    private static string Action(bool enable) => enable ? "Aktifkan" : "Nonaktifkan";
    private static TelegramButton Button(string text, string callbackData) => new(text, callbackData);
    private static TelegramKeyboard Keyboard(params IReadOnlyList<TelegramButton>[] rows) => new(rows);

    private static TelegramMessage Confirmation(string text, string sessionToken)
    {
        ValidateToken(sessionToken);
        return new TelegramMessage(text, Keyboard([
            Button("Ya", Callback($"confirm:{sessionToken}")), Button("Batal", Callback($"cancel:{sessionToken}"))]));
    }

    private static string Callback(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > 64)
            throw new ArgumentException("Telegram callback data cannot exceed 64 UTF-8 bytes.", nameof(value));
        return value;
    }

    private static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Session token is required.", nameof(token));
        if (token.Contains(':')) throw new ArgumentException("Session token must be opaque and compact.", nameof(token));
    }

    private static void AddSection(List<(string Header, IReadOnlyList<string> Entries)> sections,
        string header, IReadOnlyList<string> entries)
    {
        if (entries.Count > 0) sections.Add((header, entries));
    }
}
