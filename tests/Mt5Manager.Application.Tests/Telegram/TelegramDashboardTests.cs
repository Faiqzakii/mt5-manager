using FluentAssertions;
using Mt5Manager.Application.Telegram;

namespace Mt5Manager.Application.Tests.Telegram;

public sealed class TelegramDashboardTests
{
    private static readonly Guid FirstId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Main_dashboard_has_indonesian_status_and_five_actions()
    {
        var message = TelegramDashboard.Main(new TelegramConnectionState(true, "operator", 3, null));

        message.Text.Should().Be("🤖 MT5 Manager\nStatus: Terhubung\nBot: @operator\nTerminal: 3");
        message.Keyboard.Rows.SelectMany(row => row).Select(button => button.Text).Should().Equal(
            "Status/Refresh", "ON Terminal", "OFF Terminal", "ON Semua", "OFF Semua");
        message.Keyboard.Rows.SelectMany(row => row).Select(button => button.CallbackData).Should().Equal(
            "status", "pick:on", "pick:off", "all:on", "all:off");
    }

    [Fact]
    public void Terminal_picker_uses_name_and_login_and_only_id_plus_opaque_token_in_callback()
    {
        var terminals = new[]
        {
            new TelegramTerminal(FirstId, "Alpha", "12345", true),
            new TelegramTerminal(Guid.NewGuid(), "Beta", null, false)
        };

        var message = TelegramDashboard.TerminalPicker(true, terminals, "opaque7");

        message.Text.Should().Be("Pilih terminal untuk mengaktifkan Algo Trading:");
        message.Keyboard.Rows[0][0].Text.Should().Be("Alpha — 12345");
        message.Keyboard.Rows[0][0].CallbackData.Should().Be($"terminal:on:{FirstId:N}:opaque7");
        message.Keyboard.Rows[1][0].Text.Should().Be("Beta — akun tidak tersedia");
        message.Keyboard.Rows.SelectMany(x => x).Select(x => x.CallbackData)
            .Should().OnlyContain(value => !value.Contains("Alpha") && !value.Contains("12345"));
    }

    [Fact]
    public void Confirmations_put_only_supplied_token_in_confirm_callbacks()
    {
        var single = TelegramDashboard.ConfirmTerminal(false, new TelegramTerminal(FirstId, "Alpha", "12345", true), "S3");
        var bulk = TelegramDashboard.ConfirmAll(true, 9, "B4");

        single.Text.Should().Be("Nonaktifkan Algo Trading untuk Alpha — 12345?");
        single.Keyboard.Rows.SelectMany(x => x).Select(x => x.CallbackData).Should().Equal("confirm:S3", "cancel:S3");
        bulk.Text.Should().Be("Aktifkan Algo Trading untuk semua 9 terminal?");
        bulk.Keyboard.Rows.SelectMany(x => x).Select(x => x.CallbackData).Should().Equal("confirm:B4", "cancel:B4");
    }

    [Fact]
    public void Picker_accepts_exact_OFF_callback_byte_limit_and_rejects_one_byte_over()
    {
        var terminal = new TelegramTerminal(FirstId, "Alpha", null, true);

        var message = TelegramDashboard.TerminalPicker(false, [terminal], new string('a', 18));

        System.Text.Encoding.UTF8.GetByteCount(message.Keyboard.Rows[0][0].CallbackData).Should().Be(64);
        var act = () => TelegramDashboard.TerminalPicker(false, [terminal], new string('a', 19));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Picker_rejects_multibyte_token_when_complete_callback_exceeds_byte_limit()
    {
        var terminal = new TelegramTerminal(FirstId, "Alpha", null, true);

        var act = () => TelegramDashboard.TerminalPicker(true, [terminal], new string('界', 7));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Results_are_grouped_by_outcome_and_split_only_between_terminal_entries()
    {
        var longError = new string('x', 4050);
        var results = new[]
        {
            new TelegramTerminalResult(FirstId, "Alpha", "12345", true, true, null),
            new TelegramTerminalResult(Guid.NewGuid(), "Beta", null, true, false, longError),
            new TelegramTerminalResult(Guid.NewGuid(), "Gamma", "789", true, true, null)
        };

        var messages = TelegramDashboard.Results(results);

        messages.Should().HaveCount(2);
        messages.Should().OnlyContain(x => x.Length <= TelegramDashboard.MaxMessageLength);
        messages[0].Should().Be("✅ Berhasil\n• Alpha — 12345\n• Gamma — 789");
        messages[1].Should().StartWith("❌ Gagal\n• Beta — akun tidak tersedia: ");
        string.Concat(messages).Should().Contain(longError);
    }

    [Fact]
    public void Oversized_single_result_is_safely_split_to_telegram_limit()
    {
        var messages = TelegramDashboard.Results([
            new TelegramTerminalResult(FirstId, "Alpha", "12345", false, false, new string('z', 9000))]);

        messages.Should().OnlyContain(x => x.Length <= TelegramDashboard.MaxMessageLength);
        string.Concat(messages).Should().Contain(new string('z', 9000));
    }

    [Fact]
    public void Oversized_result_never_splits_a_supplementary_character_at_any_cutoff()
    {
        var error = string.Concat(Enumerable.Repeat("😀", 5000));

        var messages = TelegramDashboard.Results([
            new TelegramTerminalResult(FirstId, "Alpha", null, false, false, error)]);

        messages.Should().OnlyContain(message => message.Length <= TelegramDashboard.MaxMessageLength);
        messages.All(message => !char.IsHighSurrogate(message[message.Length - 1]) && !char.IsLowSurrogate(message[0])).Should().BeTrue();
        string.Concat(messages).Count(character => char.IsHighSurrogate(character)).Should().Be(5000);
    }

    [Fact]
    public void Consecutive_oversized_results_have_no_empty_messages_and_keep_section_semantics()
    {
        var results = new[]
        {
            new TelegramTerminalResult(FirstId, new string('a', 9000), null, false, false, "first"),
            new TelegramTerminalResult(Guid.NewGuid(), new string('b', 9000), null, false, false, "second")
        };

        var messages = TelegramDashboard.Results(results);

        messages.Should().OnlyContain(message => message.Length > 0 && message.Length <= TelegramDashboard.MaxMessageLength);
        messages.Count(message => message.StartsWith("❌ Gagal\n• ")).Should().Be(2);
        string.Concat(messages).Should().Contain("first").And.Contain("second");
    }
}
