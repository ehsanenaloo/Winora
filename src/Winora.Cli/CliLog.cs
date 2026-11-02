// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.Extensions.Logging;
using Winora.Infrastructure.Persistence;
namespace Winora.Cli;

/// <summary>
/// CLI logging composition: the redacting rolling file log under %LocalAppData%\Winora\logs at Information level, on by default.
/// Disabled by --no-log or the environment variable WINORA_NO_LOG=1 (used by tests so they never touch the real log folder).
/// WINORA_DATA_DIR redirects the application data root for tests. Only command names and exit codes are logged, never arguments.
/// </summary>
internal static class CliLog
{
    public const string NoLogFlag = "--no-log";
    public const string NoLogVariable = "WINORA_NO_LOG";
    public const string DataDirVariable = "WINORA_DATA_DIR";

    public static bool IsDisabled(string[] args) => args.Contains(NoLogFlag) || Environment.GetEnvironmentVariable(NoLogVariable) is "1" or "true";

    public static AppDataPaths Paths()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(DataDirVariable);
        return string.IsNullOrWhiteSpace(overrideRoot) ? AppDataPaths.Default() : new AppDataPaths(overrideRoot);
    }

    public static ILoggerFactory CreateFactory(bool disabled) => LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
        if (!disabled) builder.AddWinoraFileLogger(new FileLoggerOptions(Paths().LogsFolder, new LogRedactor(RedactionContext.FromEnvironment())));
    });
}
