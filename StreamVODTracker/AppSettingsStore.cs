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
            File.WriteAllText(_settingsPath, "[Appearance]\nTheme=Light\n", new UTF8Encoding(false));
    }

    public bool LoadIsDarkTheme()
    {
        var inAppearanceSection = false;
        foreach (var rawLine in File.ReadLines(_settingsPath, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inAppearanceSection = line.Equals("[Appearance]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inAppearanceSection) continue;
            var separator = line.IndexOf('=');
            if (separator < 0 || !line[..separator].Trim().Equals("Theme", StringComparison.OrdinalIgnoreCase)) continue;
            return line[(separator + 1)..].Trim().Equals("Dark", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public void SaveIsDarkTheme(bool isDark)
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
            if (line.Equals("[Appearance]", StringComparison.OrdinalIgnoreCase))
                sectionStart = index;
        }

        var themeLine = $"Theme={(isDark ? "Dark" : "Light")}";
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add("[Appearance]");
            lines.Add(themeLine);
        }
        else
        {
            var themeIndex = -1;
            for (var index = sectionStart + 1; index < sectionEnd; index++)
            {
                if (lines[index].Trim().StartsWith("Theme=", StringComparison.OrdinalIgnoreCase))
                {
                    themeIndex = index;
                    break;
                }
            }
            if (themeIndex >= 0) lines[themeIndex] = themeLine;
            else lines.Insert(sectionEnd, themeLine);
        }

        File.WriteAllLines(_settingsPath, lines, new UTF8Encoding(false));
    }
}
