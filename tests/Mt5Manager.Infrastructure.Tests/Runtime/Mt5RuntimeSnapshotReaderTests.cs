using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Runtime;

namespace Mt5Manager.Infrastructure.Tests.Runtime;

public sealed class Mt5RuntimeSnapshotReaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mt5-runtime-{Guid.NewGuid():N}");
    private readonly TerminalRegistration terminal;

    public Mt5RuntimeSnapshotReaderTests() => terminal = new(Guid.NewGuid(), "Broker", @"C:\Apps\Broker\terminal64.exe", root, @"C:\Apps\Broker", [], DiscoverySource.Manual, true);

    [Fact]
    public async Task Read_returns_a_fresh_snapshot_with_matching_data_path()
    {
        WriteSnapshot(Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow, root, true, AlgoTradingState.Enabled);

        var snapshot = await new Mt5RuntimeSnapshotReader(root).ReadAsync(terminal);

        snapshot!.Login.Should().Be(12345);
        snapshot.AccountName.Should().Be("Trader");
        snapshot.Server.Should().Be("Broker-Live");
        snapshot.Company.Should().Be("Broker Ltd");
        snapshot.TradeMode.Should().Be(AccountTradeMode.Real);
        snapshot.Connected.Should().BeTrue();
        snapshot.GlobalAlgoTrading.Should().Be(AlgoTradingState.Enabled);
        snapshot.EaTradingAllowed.Should().BeTrue();
        snapshot.AccountTradingAllowed.Should().BeTrue();
        snapshot.AccountExpertAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Read_rejects_stale_snapshot()
    {
        WriteSnapshot(Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow.AddMinutes(-10), root, true, AlgoTradingState.Enabled);

        (await new Mt5RuntimeSnapshotReader(root).ReadAsync(terminal)).Should().BeNull();
    }

    [Fact]
    public async Task Read_rejects_snapshot_beyond_future_clock_skew_tolerance()
    {
        WriteSnapshot(Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow.AddMinutes(2), root, true, AlgoTradingState.Enabled);

        (await new Mt5RuntimeSnapshotReader(root).ReadAsync(terminal)).Should().BeNull();
    }

    [Fact]
    public async Task Read_honors_cancellation()
    {
        WriteSnapshot(Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow, root, true, AlgoTradingState.Enabled);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = async () => await new Mt5RuntimeSnapshotReader(root).ReadAsync(terminal, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Read_rejects_mismatched_data_path()
    {
        WriteSnapshot(Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow, @"C:\Other\Data", true, AlgoTradingState.Enabled);

        (await new Mt5RuntimeSnapshotReader(root).ReadAsync(terminal)).Should().BeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"protocolVersion\":2,\"timestamp\":\"2026-09-11T00:00:00Z\",\"dataPath\":\"root\"}")]
    public async Task Read_rejects_unsupported_or_malformed_snapshot(string content)
    {
        WriteRaw(content);

        (await new Mt5RuntimeSnapshotReader(root).ReadAsync(terminal)).Should().BeNull();
    }

    [Fact]
    public async Task Read_rejects_unknown_enum_values()
    {
        WriteRaw(JsonSerializer.Serialize(new
        {
            protocolVersion = Mt5RuntimeSnapshotReader.ProtocolVersion,
            timestamp = DateTimeOffset.UtcNow,
            dataPath = root,
            login = 12345,
            accountName = "Trader",
            server = "Broker-Live",
            company = "Broker Ltd",
            tradeMode = "Unknown",
            connected = true,
            globalAlgoTrading = "Unknown",
            eaTradingAllowed = true,
            accountTradingAllowed = true,
            accountExpertAllowed = true
        }));

        (await new Mt5RuntimeSnapshotReader(root).ReadAsync(terminal)).Should().BeNull();
    }

    [Fact]
    public async Task Read_falls_back_to_fresh_valid_backup_when_final_is_missing()
    {
        WriteSnapshot(Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow, root, true, AlgoTradingState.Enabled, ".bak");

        (await new Mt5RuntimeSnapshotReader(root).ReadAsync(terminal))!.Login.Should().Be(12345);
    }

    [Fact]
    public async Task Read_falls_back_to_fresh_valid_backup_when_final_is_invalid()
    {
        WriteRaw("not json");
        WriteSnapshot(Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow, root, true, AlgoTradingState.Enabled, ".bak");

        (await new Mt5RuntimeSnapshotReader(root).ReadAsync(terminal))!.Login.Should().Be(12345);
    }

    [Fact]
    public async Task Read_prefers_valid_final_over_backup()
    {
        WriteSnapshot(Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow, root, true, AlgoTradingState.Enabled);
        WriteSnapshot(Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow, root, false, AlgoTradingState.Disabled, ".bak");

        (await new Mt5RuntimeSnapshotReader(root).ReadAsync(terminal))!.Connected.Should().BeTrue();
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(2)]
    public async Task Read_rejects_backup_outside_freshness_window(int minutes)
    {
        WriteSnapshot(Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow.AddMinutes(minutes), root, true, AlgoTradingState.Enabled, ".bak");

        (await new Mt5RuntimeSnapshotReader(root).ReadAsync(terminal)).Should().BeNull();
    }

    [Fact]
    public async Task Read_rejects_invalid_backup()
    {
        WriteRaw("not json", ".bak");

        (await new Mt5RuntimeSnapshotReader(root).ReadAsync(terminal)).Should().BeNull();
    }

    [Fact]
    public async Task Read_survives_missing_or_inaccessible_record()
    {
        (await new Mt5RuntimeSnapshotReader(Path.Combine(root, "missing-common")).ReadAsync(terminal)).Should().BeNull();
    }

    [Fact]
    public void Snapshot_path_matches_bridge_hashing_contract()
    {
        var path = @"C:\Users\Trader\AppData\Roaming\MetaQuotes\Terminal\ABC123";
        var bridgeInput = Encoding.UTF8.GetBytes(path.ToUpperInvariant());
        var bridgeHash = Convert.ToHexString(SHA256.HashData(bridgeInput)).ToLowerInvariant();

        Mt5RuntimeSnapshotReader.SnapshotPath(root, path)
            .Should().Be(Path.Combine(root, "Mt5Manager", $"runtime-{bridgeHash}.json"));
    }

    private void WriteSnapshot(int protocolVersion, DateTimeOffset timestamp, string dataPath, bool connected, AlgoTradingState global, string suffix = "") =>
        WriteRaw(JsonSerializer.Serialize(new
        {
            protocolVersion,
            timestamp,
            dataPath,
            login = 12345,
            accountName = "Trader",
            server = "Broker-Live",
            company = "Broker Ltd",
            tradeMode = "Real",
            connected,
            globalAlgoTrading = global == AlgoTradingState.Enabled ? "Enabled" : "Disabled",
            eaTradingAllowed = true,
            accountTradingAllowed = true,
            accountExpertAllowed = true
        }), suffix);

    private void WriteRaw(string content, string suffix = "")
    {
        var recordPath = Mt5RuntimeSnapshotReader.SnapshotPath(root, terminal.DataDirectory) + suffix;
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        File.WriteAllText(recordPath, content);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch { } }
}
