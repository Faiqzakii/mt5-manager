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
        snapshot.TerminalPath.Should().Be(terminal.WorkingDirectory);
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
    [Fact]
    public async Task Resolve_returns_the_unique_structured_data_path_for_matching_terminal_directory()
    {
        CreateDataStructure(root);
        WriteSnapshot(Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow, root, true, AlgoTradingState.Enabled);

        (await new Mt5RuntimeSnapshotReader(root).ResolveDataDirectoryAsync(terminal.ExecutablePath))
            .Should().Be(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
    }

    [Fact]
    public async Task Resolve_rejects_mismatched_terminal_path()
    {
        CreateDataStructure(root);
        WriteSnapshot(Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow, root, true, AlgoTradingState.Enabled, terminalPath: @"C:\Other");

        (await new Mt5RuntimeSnapshotReader(root).ResolveDataDirectoryAsync(terminal.ExecutablePath)).Should().BeNull();
    }

    [Theory]
    [InlineData(-10, 2)]
    [InlineData(0, 1)]
    public async Task Resolve_rejects_stale_or_old_protocol_snapshot(int ageMinutes, int protocolVersion)
    {
        CreateDataStructure(root);
        WriteSnapshot(protocolVersion, DateTimeOffset.UtcNow.AddMinutes(ageMinutes), root, true, AlgoTradingState.Enabled);

        (await new Mt5RuntimeSnapshotReader(root).ResolveDataDirectoryAsync(terminal.ExecutablePath)).Should().BeNull();
    }

    [Fact]
    public async Task Resolve_rejects_ambiguous_distinct_data_paths()
    {
        var otherData = Path.Combine(Path.GetTempPath(), $"mt5-runtime-other-{Guid.NewGuid():N}");
        try
        {
            CreateDataStructure(root);
            CreateDataStructure(otherData);
            WriteSnapshot(Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow, root, true, AlgoTradingState.Enabled);
            WriteSnapshotAt(otherData, Mt5RuntimeSnapshotReader.ProtocolVersion, DateTimeOffset.UtcNow, otherData);

            (await new Mt5RuntimeSnapshotReader(root).ResolveDataDirectoryAsync(terminal.ExecutablePath)).Should().BeNull();
        }
        finally { try { Directory.Delete(otherData, true); } catch { } }
    }

    private static void CreateDataStructure(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "config"));
        Directory.CreateDirectory(Path.Combine(path, "bases"));
        Directory.CreateDirectory(Path.Combine(path, "logs"));
    }


    private void WriteSnapshot(int protocolVersion, DateTimeOffset timestamp, string dataPath, bool connected, AlgoTradingState global, string suffix = "", string? terminalPath = null) =>
        WriteRaw(SnapshotJson(protocolVersion, timestamp, dataPath, connected, global, terminalPath ?? terminal.WorkingDirectory), suffix);

    private void WriteSnapshotAt(string snapshotDataPath, int protocolVersion, DateTimeOffset timestamp, string dataPath) =>
        WriteRaw(SnapshotJson(protocolVersion, timestamp, dataPath, true, AlgoTradingState.Enabled, terminal.WorkingDirectory), dataPath: snapshotDataPath);

    private static string SnapshotJson(int protocolVersion, DateTimeOffset timestamp, string dataPath, bool connected, AlgoTradingState global, string? terminalPath) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion,
            timestamp,
            dataPath,
            terminalPath,
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
        });

    private void WriteRaw(string content, string suffix = "", string? dataPath = null)
    {
        var recordPath = Mt5RuntimeSnapshotReader.SnapshotPath(root, dataPath ?? terminal.DataDirectory) + suffix;
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        File.WriteAllText(recordPath, content);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch { } }
}
