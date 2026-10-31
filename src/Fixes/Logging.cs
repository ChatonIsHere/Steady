using System;
using NLog;
using NLog.Config;

namespace Steady.Fixes;

// The game's NLog.config sends Debug and up to its log file and to the server console, which renders on
// the main thread. This raises the catch-all rule to LogLevel. Other rules (console command replies,
// Canary's trace sessions) are left alone.
internal static class Logging
{
    private static LogLevel _level;

    public static void Apply()
    {
        try
        {
            _level = LogLevel.FromString(SteadyConfig.LogLevel);
        }
        catch (ArgumentException)
        {
            SteadyMod.Log.Warning($"LogLevel '{SteadyConfig.LogLevel}' isn't an NLog level; logging left as it is.");
            return;
        }
        if (_level <= LogLevel.Debug) return;
        // Loading or reloading a config (the file is watched) puts Debug back.
        LogManager.ConfigurationChanged += (_, args) => Lift(args.ActivatedConfiguration);
        Lift(LogManager.Configuration);
        SteadyMod.Active.Add($"game log level {_level}");
    }

    private static void Lift(LoggingConfiguration config)
    {
        if (config == null) return;
        foreach (var rule in config.LoggingRules)
        {
            if (rule.LoggerNamePattern != "*" || !rule.IsLoggingEnabledForLevel(LogLevel.Debug)) continue;
            for (var level = LogLevel.Trace; level < _level; level = LogLevel.FromOrdinal(level.Ordinal + 1))
                rule.DisableLoggingForLevel(level);
        }
        LogManager.ReconfigExistingLoggers();
    }
}
