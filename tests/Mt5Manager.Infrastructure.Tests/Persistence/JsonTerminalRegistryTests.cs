using System.Text.Json;
using FluentAssertions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Persistence;

namespace Mt5Manager.Infrastructure.Tests.Discovery;

public sealed class JsonTerminalRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mt5-registry-{Guid.NewGuid():N}");
    private string RegistryPath => Path.Combine(_root, "terminals.json");

    public JsonTerminalRegistryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Save_and_load_round_trips_registration_and_replaces_existing_file_atomically()
    {
        var registry = new JsonTerminalRegistry(RegistryPath);
        var original = Registration("Original", @"C:\MT5\terminal64.exe", @"C:\Data");
        var replacement = Registration("Replacement", @"D:\MT5\terminal64.exe", @"D:\Data") with
        {
            Arguments = ["/portable", @"/config:C:\Program Files\MetaTrader 5\config.ini"]
        };

        await registry.SaveAsync([original]);
        await registry.SaveAsync([replacement]);

        (await registry.LoadAsync()).Should().BeEquivalentTo([replacement], options => options.WithStrictOrdering());
        Directory.EnumerateFiles(_root).Should().ContainSingle().Which.Should().Be(RegistryPath);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(RegistryPath));
        document.RootElement.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Load_skips_malformed_records_without_losing_valid_records()
    {
        await File.WriteAllTextAsync(RegistryPath, """
            {
              "version": 1,
              "terminals": [
                {
                  "id": "8d9ec548-2e99-4fd6-9b43-195d18f91983",
                  "displayName": "Valid",
                  "executablePath": "C:\\MT5\\terminal64.exe",
                  "dataDirectory": "C:\\Data",
                  "workingDirectory": "C:\\MT5",
                  "arguments": ["/portable"],
                  "source": "Manual",
                  "dataDirectoryVerified": true
                },
                { "id": "not-a-guid", "displayName": "Broken" }
              ]
            }
            """);

        var loaded = await new JsonTerminalRegistry(RegistryPath).LoadAsync();

        loaded.Should().ContainSingle();
        loaded[0].DisplayName.Should().Be("Valid");
        loaded[0].Arguments.Should().Equal("/portable");
    }

    [Fact]
    public async Task Load_returns_empty_for_non_object_json_root()
    {
        await File.WriteAllTextAsync(RegistryPath, "[1,2,3]");

        var loaded = await new JsonTerminalRegistry(RegistryPath).LoadAsync();

        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task Load_keeps_a_damaged_registry_file_instead_of_leaving_it_to_be_overwritten()
    {
        await File.WriteAllTextAsync(RegistryPath, "{ this is not the registry");

        var loaded = await new JsonTerminalRegistry(RegistryPath).LoadAsync();

        loaded.Should().BeEmpty();
        var backup = Directory.EnumerateFiles(_root, "*.corrupt-*").Should().ContainSingle().Subject;
        (await File.ReadAllTextAsync(backup)).Should().Be("{ this is not the registry");
    }

    [Fact]
    public async Task Load_returns_empty_for_non_numeric_version()
    {
        await File.WriteAllTextAsync(RegistryPath, """
            { "version": "one", "terminals": [] }
            """);

        var loaded = await new JsonTerminalRegistry(RegistryPath).LoadAsync();

        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_registry_instances_leave_a_complete_document()
    {
        var registries = Enumerable.Range(0, 20).Select(_ => new JsonTerminalRegistry(RegistryPath)).ToArray();
        var registrations = Enumerable.Range(0, 20).Select(index => Registration($"Terminal {index}", @"C:\MT5\terminal64.exe", @"C:\Data")).ToArray();

        await Task.WhenAll(registries.Zip(registrations).Select(pair => pair.First.SaveAsync([pair.Second])));

        registrations.Should().ContainEquivalentOf((await registries[0].LoadAsync()).Single());
        Directory.EnumerateFiles(_root, ".terminals.json.*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Corrupt_load_preserves_snapshot_without_moving_a_new_current_file()
    {
        await File.WriteAllTextAsync(RegistryPath, "{broken");
        var registry = new JsonTerminalRegistry(RegistryPath);

        await registry.LoadAsync();
        await registry.SaveAsync([Registration("New", @"C:\MT5\terminal64.exe", @"C:\Data")]);

        File.Exists(RegistryPath).Should().BeTrue();
        (await registry.LoadAsync()).Should().ContainSingle(x => x.DisplayName == "New");
        (await File.ReadAllTextAsync(Directory.EnumerateFiles(_root, "terminals.json.corrupt-*").Single())).Should().Be("{broken");
    }

    private static TerminalRegistration Registration(string name, string executable, string data) =>
        new(Guid.NewGuid(), name, executable, data, Path.GetDirectoryName(executable)!, [], DiscoverySource.Manual, true);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
