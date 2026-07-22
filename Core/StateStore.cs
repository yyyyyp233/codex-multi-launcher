using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexChannelLauncher.Core;

public sealed class StateStore(LauncherPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LauncherState Load()
    {
        try
        {
            if (!File.Exists(paths.StateFile))
            {
                return new LauncherState();
            }

            return JsonSerializer.Deserialize<LauncherState>(File.ReadAllText(paths.StateFile), JsonOptions)
                   ?? new LauncherState();
        }
        catch
        {
            return new LauncherState();
        }
    }

    public ProcessMarker? TryLoadLegacyCompanyRootProcess()
    {
        try
        {
            if (!File.Exists(paths.StateFile))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(paths.StateFile));
            if (!document.RootElement.TryGetProperty("CompanyRootProcess", out var property) ||
                property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            return property.Deserialize<ProcessMarker>(JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(LauncherState state)
    {
        Directory.CreateDirectory(paths.StateDirectory);
        var temporary = paths.StateFile + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, paths.StateFile, true);
    }
}
