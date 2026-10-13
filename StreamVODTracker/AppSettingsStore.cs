using System.Text;

namespace StreamVODTracker;

public sealed class AppSettingsStore
{
    private readonly string _settingsPath;

    public AppSettingsStore(string applicationDirectory)
    {
        var settingsDirectory = Path.Combine(applicationDirectory, "settings");
        Directory.CreateDirectory(settingsDirectory);
        _settingsPath = Path.Combine(settingsDirectory, "settings.ini");
        if (!File.Exists(_settingsPath))
            File.WriteAllText(_settingsPath, "[Appearance]\nTheme=Light\n\n[Library]\nSortOrder=PublishedAt\n", new UTF8Encoding(false));
    }

    public bool LoadIsDarkTheme()
    {
        return string.Equals(LoadValue("Appearance", "Theme"), "Dark", StringComparison.OrdinalIgnoreCase);
    }

    public string LoadSortOrder() => LoadValue("Library", "SortOrder") ?? "PublishedAt";

    public void SaveSortOrder(string sortOrder) => SaveValue("Library", "SortOrder", sortOrder);

    public void SaveIsDarkTheme(bool isDark) => SaveValue("Appearance", "Theme", isDark ? "Dark" : "Light");

    private string? LoadValue(string sectionName, string key)
    {
        var inSection = false;
        foreach (var rawLine in File.ReadLines(_settingsPath, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line.Equals($"[{sectionName}]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            var separator = line.IndexOf('=');
            if (separator >= 0 && line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(separator + 1)..].Trim();
        }
        return null;
    }

    private void SaveValue(string sectionName, string key, string value)
    {
        var lines = File.ReadAllLines(_settingsPath, Encoding.UTF8).ToList();
        var sectionStart = -1;
        var sectionEnd = lines.Count;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index].Trim();
            if (!line.StartsWith('[') || !line.EndsWith(']')) continue;
            if (sectionStart >= 0)
            {
                sectionEnd = index;
                break;
            }
            if (line.Equals($"[{sectionName}]", StringComparison.OrdinalIgnoreCase))
                sectionStart = index;
        }

        var settingLine = $"{key}={value}";
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add($"[{sectionName}]");
            lines.Add(settingLine);
        }
        else
        {
            var settingIndex = -1;
            for (var index = sectionStart + 1; index < sectionEnd; index++)
            {
                var separator = lines[index].IndexOf('=');
                if (separator >= 0 && lines[index][..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    settingIndex = index;
                    break;
                }
            }
            if (settingIndex >= 0) lines[settingIndex] = settingLine;
            else lines.Insert(sectionEnd, settingLine);
        }

        File.WriteAllLines(_settingsPath, lines, new UTF8Encoding(false));
    }
}
