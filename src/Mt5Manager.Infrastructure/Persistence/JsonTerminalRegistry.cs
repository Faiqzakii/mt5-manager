using System.Text.Json;
using System.Text.Json.Serialization;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Persistence;

public sealed class JsonTerminalRegistry : ITerminalRegistry
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public JsonTerminalRegistry(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Mt5Manager", "terminals.json");

    public async Task<IReadOnlyList<TerminalRegistration>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return [];

        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var documentVersion) ||
                documentVersion != CurrentVersion ||
                !root.TryGetProperty("terminals", out var terminals) ||
                terminals.ValueKind != JsonValueKind.Array)
                return [];

            var valid = new List<TerminalRegistration>();
            foreach (var element in terminals.EnumerateArray())
            {
                try
                {
                    var registration = element.Deserialize<TerminalRegistration>(Options);
                    if (IsValid(registration)) valid.Add(registration!);
                }
                catch (JsonException)
                {
                    // One invalid registration must not hide the rest of the registry.
                }
            }
            return valid;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task SaveAsync(IReadOnlyList<TerminalRegistration> terminals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminals);
        var invalid = terminals.FirstOrDefault(item => !IsValid(item));
        if (invalid is not null) throw new ArgumentException("All terminal registrations must be valid.", nameof(terminals));

        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new TerminalRegistryDocument(CurrentVersion, terminals), Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    internal static bool IsValid(TerminalRegistration? registration) => registration is not null &&
        registration.Id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(registration.DisplayName) &&
        !string.IsNullOrWhiteSpace(registration.ExecutablePath) &&
        registration.Arguments is not null &&
        Enum.IsDefined(registration.Source) &&
        (!registration.DataDirectoryVerified || !string.IsNullOrWhiteSpace(registration.DataDirectory));

    public sealed record TerminalRegistryDocument(int Version, IReadOnlyList<TerminalRegistration> Terminals);
}
