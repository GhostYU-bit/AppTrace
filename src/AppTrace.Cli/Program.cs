using System.Diagnostics;
using System.Text;
using AppTrace.Core.Discovery;
using AppTrace.Core.Model;
using AppTrace.Core.Reporting;
using AppTrace.Core.Scanning;

namespace AppTrace.Cli;

/// <summary>
/// AppTrace's developer-facing command line. Phase 0 exposes discovery, scanning
/// and reporting only; there is no command that modifies the system.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var commandLine = CommandLine.Parse(args, out var parseError);

        if (parseError is not null)
        {
            Console.Error.WriteLine("apptrace: " + parseError);
            Console.Error.WriteLine();
            Usage(Console.Error);
            return 2;
        }

        if (commandLine.Has("help") || commandLine.Command is "help")
        {
            Usage(Console.Out);
            return 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("apptrace: Phase 0 only supports Windows.");
            return 3;
        }

        return commandLine.Command.ToLowerInvariant() switch
        {
            "scan" => RunScan(commandLine),
            "apps" => RunApps(commandLine),
            "roots" => RunRoots(commandLine),
            _ => Unknown(commandLine.Command),
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"apptrace: unknown command \"{command}\".");
        Console.Error.WriteLine();
        Usage(Console.Error);
        return 2;
    }

    private static int RunScan(CommandLine commandLine)
    {
        var verbose = commandLine.Has("verbose") || commandLine.Has("v");
        var json = commandLine.Has("json");
        var showAll = commandLine.Has("all");

        var limits = new ScanLimits
        {
            MaxDepth = commandLine.GetInt("depth", ScanLimits.Default.MaxDepth),
            MaxDirectories = commandLine.GetInt("max-directories", ScanLimits.Default.MaxDirectories),
            MaxFiles = commandLine.GetInt("max-files", ScanLimits.Default.MaxFiles),
            FollowReparsePoints = commandLine.Has("follow-reparse-points"),
        };

        var options = new ScanOptions
        {
            Limits = limits,
            PathFilter = commandLine.RequireValue("filter"),
        };

        var roots = KnownFolders.DefaultScanRoots();
        if (roots.Count == 0)
        {
            Console.Error.WriteLine("apptrace: no scannable locations were found on this machine.");
            return 4;
        }

        if (!json)
        {
            Console.Error.WriteLine($"Discovering installed applications and scanning {roots.Count} location(s)...");
            Console.Error.WriteLine("This is read-only: AppTrace never modifies the system.");
        }

        var stopwatch = Stopwatch.StartNew();
        var registry = new UninstallRegistry();
        var apps = registry.Discover();
        var scanner = new AppTraceScanner(apps, options);
        var scan = scanner.Scan(roots);
        stopwatch.Stop();

        if (!json)
        {
            Console.Error.WriteLine($"Discovered {apps.Count} applications in {stopwatch.Elapsed.TotalSeconds:0.##} s.");
        }

        var report = FootprintReport.Build(scan);

        if (json)
        {
            Console.WriteLine(JsonReporter.Render(report));
            return 0;
        }

        var reportOptions = new ReportOptions
        {
            NameOf = id => apps.FirstOrDefault(a => a.Id == id)?.DisplayName ?? id,
            Verbose = verbose,
            ShowWhy = true,
            IncludeZeroFootprintApps = showAll,
            MaxApplications = commandLine.GetInt("top", 25),
            MaxAmbiguousLocations = commandLine.GetInt("max-ambiguous", 15),
            MaxUnattributedLocations = commandLine.GetInt("max-unattributed", 15),
            RootLabels = scan.Roots.Select(r => $"{r.Label}  ({r.Path})").ToArray(),
        };

        Console.WriteLine(TextReporter.Render(report, reportOptions));
        return 0;
    }

    private static int RunApps(CommandLine commandLine)
    {
        var registry = new UninstallRegistry();
        var apps = registry.Discover();

        if (commandLine.Has("json"))
        {
            var scan = new ScanResult
            {
                StartedAt = DateTimeOffset.Now,
                Duration = TimeSpan.Zero,
                Applications = apps,
            };
            Console.WriteLine(JsonReporter.Render(FootprintReport.Build(scan)));
            return 0;
        }

        Console.WriteLine($"AppTrace {AppTraceInfo.Version} - {apps.Count} installed application(s) discovered");        Console.WriteLine(new string('=', 72));
        foreach (var app in apps)
        {
            Console.WriteLine($"{app.DisplayName}");
            Console.WriteLine($"  id         : {app.Id}");
            if (app.Version is { Length: > 0 })
            {
                Console.WriteLine($"  version    : {app.Version}");
            }

            if (app.Publisher is { Length: > 0 })
            {
                Console.WriteLine($"  publisher  : {app.Publisher}");
            }

            Console.WriteLine($"  normalized : {app.NormalizedName}");
            if (app.InstallLocation is { Length: > 0 })
            {
                Console.WriteLine($"  install    : {app.InstallLocation}");
            }

            Console.WriteLine($"  source     : {app.RegistrySource} ({app.RegistryRoot})");
            Console.WriteLine();
        }

        if (registry.Errors.Count > 0)
        {
            Console.WriteLine($"{registry.Errors.Count} registry read note(s); run `apptrace scan --json` for details.");
        }

        return 0;
    }

    private static int RunRoots(CommandLine commandLine)
    {
        _ = commandLine;
        var roots = KnownFolders.DefaultScanRoots();
        Console.WriteLine("Locations AppTrace inspects (read-only):");
        foreach (var root in roots)
        {
            var exists = Directory.Exists(root.Path);
            Console.WriteLine($"  {root.Label,-32} {root.Path} {(exists ? string.Empty : "(missing)")}");
        }

        Console.WriteLine();
        Console.WriteLine("Measured but never partitioned (Phase 0 exclusion):");
        foreach (var exclusion in ScanOptions.DefaultExclusions(KnownFolders.LocalAppData() ?? string.Empty))
        {
            Console.WriteLine($"  {exclusion}");
        }

        return 0;
    }

    private static void Usage(TextWriter writer)
    {
        writer.WriteLine($"AppTrace {AppTraceInfo.Version} - read-only Windows application storage attribution");
        writer.WriteLine();
        writer.WriteLine("Usage: apptrace <command> [options]");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  scan     Discover applications and attribute filesystem locations (default)");
        writer.WriteLine("  apps     List installed applications discovered from the registry");
        writer.WriteLine("  roots    Show the locations AppTrace would inspect");
        writer.WriteLine();
        writer.WriteLine("Options for `scan`:");
        writer.WriteLine("  --json                     Emit structured JSON diagnostics instead of text");
        writer.WriteLine("  --filter <text>            Only scan roots whose path contains <text>");
        writer.WriteLine("  --depth <n>                Max attribution depth (default 6)");
        writer.WriteLine("  --top <n>                  Applications to show in text output (default 25)");
        writer.WriteLine("  --max-ambiguous <n>        Shared/ambiguous locations to show (default 15)");
        writer.WriteLine("  --max-unattributed <n>     Unattributed locations to show (default 15)");
        writer.WriteLine("  --max-directories <n>      Directory budget per scan");
        writer.WriteLine("  --max-files <n>            File budget per scan");
        writer.WriteLine("  --follow-reparse-points    Follow junctions/symlinks (off by default; risks double counting)");
        writer.WriteLine("  -v, --verbose              Include full WHY blocks and suppressed notes");
        writer.WriteLine("  --all                      Also list applications with no attributed storage");
        writer.WriteLine();
        writer.WriteLine("AppTrace performs no deletion, cleanup, uninstallation or registry modification.");
    }
}
