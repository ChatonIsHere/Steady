using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader;
using MelonLoader.Utils;

namespace Steady;

// Cave Performance and Forest Performance (github.com/calebv2/ATT-Optimizations) change some of the same game code
// Steady does. Where both would do the same job, Steady leaves it to them and says so at startup; where the two
// stack (the rebuilt-tile re-cut, the fast area fill, the graph search), both run.
internal static class Others
{
    private const string CaveName = "Cave Performance", ForestName = "Forest Performance";
    private static Dictionary<string, string> _cave, _forest;

    public static void Detect()
    {
        _cave = Load(CaveName, "CavePerformance");
        _forest = Load(ForestName, "ForestPerformance");
    }

    // Whether that mod is loaded and has the setting on. Their settings are read at their startup, as ours are,
    // and a missing one is on: that's their default for every setting Steady asks about.
    public static bool Cave(string setting) => On(_cave, setting);
    public static bool Forest(string setting) => On(_forest, setting);

    // For the startup line: "leaving <what> to Forest Performance".
    public static void Leave(string what, string mod) => SteadyMod.Active.Add($"{what} left to {mod}");
    public static void LeaveToCave(string what) => Leave(what, CaveName);
    public static void LeaveToForest(string what) => Leave(what, ForestName);

    private static Dictionary<string, string> Load(string melon, string folder)
    {
        if (!MelonBase.RegisteredMelons.Any(m => m.Info.Name == melon)) return null;
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(MelonEnvironment.UserDataDirectory, folder, "config.txt");
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 0 && !line.TrimStart().StartsWith("#")) settings[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
        }
        catch (Exception e)
        {
            SteadyMod.Log.Warning($"Couldn't read {melon}'s settings, so taking its defaults. {e.Message}");
        }
        return settings;
    }

    private static bool On(Dictionary<string, string> settings, string setting)
    {
        if (settings == null) return false;
        if (settings.TryGetValue("enabled", out var enabled) && Off(enabled)) return false;
        return !settings.TryGetValue(setting, out var value) || !Off(value);
    }

    private static bool Off(string value) => value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0";
}
