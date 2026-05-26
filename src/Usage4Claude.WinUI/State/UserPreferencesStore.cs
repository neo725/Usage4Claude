using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Storage;

namespace Usage4Claude.WinUI.State;

internal sealed class UserPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public UserPreferencesStore()
    {
        var directory = GetSettingsDirectory();
        _filePath = Path.Combine(directory, "preferences.json");
    }

    public UserPreferences Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return UserPreferences.Default;
            }

            var json = File.ReadAllText(_filePath);
            var preferences = JsonSerializer.Deserialize<UserPreferences>(json, JsonOptions);
            return Normalize(preferences);
        }
        catch
        {
            return UserPreferences.Default;
        }
    }

    public void Save(UserPreferences preferences)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(preferences, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    private static UserPreferences Normalize(UserPreferences? preferences)
    {
        if (preferences?.DisplaySettings is null)
        {
            return UserPreferences.Default;
        }

        var settings = preferences.DisplaySettings;
        var hasAnyDisplayOption =
            settings.ShowFiveHour ||
            settings.ShowSevenDay ||
            settings.ShowExtraUsage ||
            settings.ShowOpus ||
            settings.ShowSonnet ||
            settings.ShowCodexPrimary ||
            settings.ShowCodexSecondary ||
            settings.ShowCodexCredits;

        var normalized = hasAnyDisplayOption
            ? preferences
            : preferences with { DisplaySettings = DisplaySettings.Default };

        var accounts = normalized.Accounts ?? Array.Empty<ProviderAccount>();
        var now = DateTimeOffset.UtcNow;
        var profiles = normalized.Profiles?.ToList() ?? new List<AccountProfile>();
        if (profiles.Count == 0)
        {
            profiles.Add(new AccountProfile(
                Guid.NewGuid(),
                "Default",
                normalized.CurrentClaudeAccountId,
                normalized.CurrentCodexAccountId,
                now,
                now));
        }

        var currentProfileId = profiles.Any(profile => profile.Id == normalized.CurrentProfileId)
            ? normalized.CurrentProfileId
            : profiles.FirstOrDefault()?.Id;
        var currentProfile = profiles.FirstOrDefault(profile => profile.Id == currentProfileId);

        var currentClaude = accounts.Any(account =>
            account.Provider == ProviderKind.Claude &&
            account.Id == currentProfile?.ClaudeAccountId)
            ? currentProfile?.ClaudeAccountId
            : accounts.FirstOrDefault(account => account.Provider == ProviderKind.Claude)?.Id;
        var currentCodex = accounts.Any(account =>
            account.Provider == ProviderKind.Codex &&
            account.Id == currentProfile?.CodexAccountId)
            ? currentProfile?.CodexAccountId
            : accounts.FirstOrDefault(account => account.Provider == ProviderKind.Codex)?.Id;

        profiles = profiles.Select(profile => profile.Id == currentProfileId
            ? profile with
            {
                ClaudeAccountId = currentClaude,
                CodexAccountId = currentCodex,
            }
            : profile).ToList();

        return normalized with
        {
            Accounts = accounts,
            Profiles = profiles,
            CurrentProfileId = currentProfileId,
            CurrentClaudeAccountId = currentClaude,
            CurrentCodexAccountId = currentCodex,
        };
    }

    private static string GetSettingsDirectory()
    {
        try
        {
            return ApplicationData.Current.LocalFolder.Path;
        }
        catch (InvalidOperationException)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Usage4Claude");
        }
    }
}
