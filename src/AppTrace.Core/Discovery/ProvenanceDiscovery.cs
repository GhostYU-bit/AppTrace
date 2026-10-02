using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AppTrace.Core.Model;
using Microsoft.Win32;

namespace AppTrace.Core.Discovery;

/// <summary>
/// Enumerates static Windows registrations that independently connect an installed
/// application to a path, and links them to discovered applications.
/// </summary>
/// <remarks>
/// <para><b>The question this answers:</b> not "does this path look like an
/// application", but "can Windows itself show that the application actually reaches
/// it". Each anchor is one such showing.</para>
/// <para><b>Discovery runs once per scan.</b> All six sources are enumerated a
/// single time into one index, because enumerating services, tasks or shortcuts per
/// directory would be quadratic and pointless.</para>
/// <para><b>Everything here is read-only and failure-tolerant.</b> One unreadable
/// key, one malformed command, one broken shortcut or one unparseable task degrades
/// to a diagnostic and no anchor. Absence of evidence is not contradiction, and a
/// scan must never fail because a registration could not be read.</para>
/// <para><b>Discovery is not inventory.</b> Task 02 measured 45 App Paths, 56
/// non-Windows service paths, 96 task paths, 18 Run entries and 227 shortcuts on a
/// modest machine. Creating an installed application from each would be absurd, so
/// these sources only ever link to an application that discovery already found.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ProvenanceDiscovery
{
    private const int MaxExecutableProbes = 400;

    private readonly List<ScanError> _errors = [];
    private readonly Dictionary<string, string?> _probeCache = new(StringComparer.Ordinal);
    private int _probes;

    /// <summary>Non-fatal problems encountered while reading registrations.</summary>
    public IReadOnlyList<ScanError> Errors => _errors;

    /// <summary>Per-source counts, for the yield report.</summary>
    public ProvenanceYield Yield { get; } = new();

    /// <summary>
    /// Discovers every anchor and links it to an installed application.
    /// </summary>
    public IReadOnlyList<ProvenanceAnchor> Discover(IReadOnlyList<AppIdentity> apps)
    {
        var linked = new List<ProvenanceAnchor>();
        var byInstallLocation = InstallLocationIndex(apps);

        CollectDisplayIcons(apps, linked);
        CollectAppPaths(apps, byInstallLocation, linked);
        CollectServices(apps, byInstallLocation, linked);
        CollectScheduledTasks(apps, byInstallLocation, linked);
        CollectRunKeys(apps, byInstallLocation, linked);
        CollectShortcuts(apps, byInstallLocation, linked);

        return linked;
    }

    // ---------------------------------------------------------------------
    // DisplayIcon
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads the <c>DisplayIcon</c> of every discovered uninstall entry.
    /// </summary>
    /// <remarks>
    /// This is the strongest of the six for linkage purposes, and not because the
    /// value is intrinsically better: the record <em>is</em> the application's own
    /// uninstall entry, so the uninstall entry referencing the file is already the
    /// connection. The value still proves only that the entry references that file.
    /// </remarks>
    private void CollectDisplayIcons(IReadOnlyList<AppIdentity> apps, List<ProvenanceAnchor> linked)
    {
        foreach (var app in apps)
        {
            Yield.DisplayIconRecords++;

            var path = ExecutableCommandLine.ResolveExecutable(app.DisplayIcon);
            if (path is null)
            {
                // A DisplayIcon naming only an icon resource is a real registration,
                // just not an executable anchor. Counting it as a parse failure would
                // overstate how much of the source AppTrace could not read.
                if (ExecutableCommandLine.IsResourceTarget(app.DisplayIcon))
                {
                    Yield.DisplayIconResourceOnly++;
                }
                else
                {
                    Yield.DisplayIconInvalid++;
                }

                continue;
            }

            Yield.DisplayIconValidPaths++;

            var metadata = ProbeMetadata(path);
            var role = ExecutableCommandLine.ClassifyRole(
                path,
                ProvenanceSource.DisplayIcon,
                metadata?.ProductName,
                metadata?.FileDescription);

            linked.Add(Anchor(
                app,
                path,
                ProvenanceSource.DisplayIcon,
                role,
                app.RegistrySource ?? "uninstall entry",
                "the application's own uninstall entry references this file",
                independentlyLinked: true));

            Yield.DisplayIconAnchors++;
        }
    }

    // ---------------------------------------------------------------------
    // App Paths
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads <c>App Paths</c> registrations, in machine and user scope and in both
    /// registry views.
    /// </summary>
    /// <remarks>
    /// An App Paths entry proves that Windows resolves this executable name to this
    /// path. It does not say which installed product the executable belongs to, so
    /// linkage still needs identity.
    /// </remarks>
    private void CollectAppPaths(
        IReadOnlyList<AppIdentity> apps,
        IReadOnlyDictionary<string, AppIdentity> byInstallLocation,
        List<ProvenanceAnchor> linked)
    {
        const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

        var surfaces = new (RegistryHive Hive, RegistryView View, string Label)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"HKLM\Software\Microsoft\Windows\CurrentVersion\App Paths"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths"),
        };

        foreach (var (hive, view, label) in surfaces)
        {
            RegistryKey? baseKey = null;
            RegistryKey? root = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                root = baseKey.OpenSubKey(subKey, RegistryKeyPermissionCheck.ReadSubTree);
                if (root is null)
                {
                    continue;
                }

                foreach (var name in root.GetSubKeyNames())
                {
                    Yield.AppPathRecords++;
                    try
                    {
                        using var entry = root.OpenSubKey(name, RegistryKeyPermissionCheck.ReadSubTree);
                        if (entry is null)
                        {
                            continue;
                        }

                        var raw = entry.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                        var path = ExecutableCommandLine.ResolveExecutable(raw);
                        if (path is null)
                        {
                            Yield.AppPathInvalid++;
                            continue;
                        }

                        var registration = $@"{label}\{name}";
                        if (!TryLink(apps, byInstallLocation, path, registration, out var app, out var corroboration))
                        {
                            continue;
                        }

                        var metadata = ProbeMetadata(path);
                        var role = ExecutableCommandLine.ClassifyRole(
                            path, ProvenanceSource.AppPath, metadata?.ProductName, metadata?.FileDescription);

                        linked.Add(Anchor(app, path, ProvenanceSource.AppPath, role, registration, corroboration, false));
                        Yield.AppPathAnchors++;
                    }
                    catch (Exception e) when (IsRecoverable(e))
                    {
                        Warn($@"{label}\{name}", "provenance-apppath", e);
                    }
                }
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                Warn(label, "provenance-apppath-open", e);
            }
            finally
            {
                root?.Dispose();
                baseKey?.Dispose();
            }
        }
    }

    // ---------------------------------------------------------------------
    // Services
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads service <c>ImagePath</c> values.
    /// </summary>
    /// <remarks>
    /// A service proves that this service launches this executable. It does not say
    /// that the application owns the service's other data, and the service's own
    /// name is not product identity. System executables are skipped, which removes
    /// the great majority of the 867 services on a typical machine.
    /// </remarks>
    private void CollectServices(
        IReadOnlyList<AppIdentity> apps,
        IReadOnlyDictionary<string, AppIdentity> byInstallLocation,
        List<ProvenanceAnchor> linked)
    {
        const string servicesKey = @"SYSTEM\CurrentControlSet\Services";

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var services = baseKey.OpenSubKey(servicesKey, RegistryKeyPermissionCheck.ReadSubTree);
            if (services is null)
            {
                return;
            }

            foreach (var serviceName in services.GetSubKeyNames())
            {
                try
                {
                    using var service = services.OpenSubKey(serviceName, RegistryKeyPermissionCheck.ReadSubTree);
                    var raw = service?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    var path = ExecutableCommandLine.ResolveExecutable(raw);
                    if (path is null || ExecutableCommandLine.IsSystemExecutable(path))
                    {
                        continue;
                    }

                    Yield.ServicePathRecords++;

                    var registration = $@"HKLM\{servicesKey}\{serviceName}";
                    if (!TryLink(apps, byInstallLocation, path, registration, out var app, out var corroboration))
                    {
                        continue;
                    }

                    var metadata = ProbeMetadata(path);
                    var role = ExecutableCommandLine.ClassifyRole(
                        path, ProvenanceSource.Service, metadata?.ProductName, metadata?.FileDescription);

                    linked.Add(Anchor(app, path, ProvenanceSource.Service, role, registration, corroboration, false));
                    Yield.ServiceAnchors++;
                }
                catch (Exception e) when (IsRecoverable(e))
                {
                    Warn($@"HKLM\{servicesKey}\{serviceName}", "provenance-service", e);
                }
            }
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            Warn($@"HKLM\{servicesKey}", "provenance-service-open", e);
        }
    }

    // ---------------------------------------------------------------------
    // Scheduled Tasks
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads scheduled-task actions through <c>schtasks /query /xml ONE</c>.
    /// </summary>
    /// <remarks>
    /// <para>The task store under <c>%WINDIR%\System32\Tasks</c> is not readable by
    /// an unelevated process, so the supported read-only export is used instead. It
    /// enumerates every task once, executes nothing and modifies nothing.</para>
    /// <para>Only <c>Exec</c> actions carry a path. <c>ComHandler</c> actions
    /// identify a class, not a file, and are counted but not turned into anchors.
    /// A task's own name is never treated as product identity.</para>
    /// </remarks>
    private void CollectScheduledTasks(
        IReadOnlyList<AppIdentity> apps,
        IReadOnlyDictionary<string, AppIdentity> byInstallLocation,
        List<ProvenanceAnchor> linked)
        => CollectScheduledTasks(ReadScheduledTasksXml(), apps, byInstallLocation, linked);

    /// <summary>
    /// Turns a task export into anchors.
    /// </summary>
    /// <remarks>
    /// Separated from the export so the parsing can be tested against fixed XML.
    /// Reading the real task store needs an elevated process on some systems, and a
    /// test must not depend on the machine it runs on.
    /// </remarks>
    internal void CollectScheduledTasks(
        string? xml,
        IReadOnlyList<AppIdentity> apps,
        IReadOnlyDictionary<string, AppIdentity> byInstallLocation,
        List<ProvenanceAnchor> linked)
    {
        if (xml is null)
        {
            return;
        }

        foreach (var task in SplitTasks(xml))
        {
            Yield.ScheduledTaskRecords++;

            var uri = Match(task, "URI")?.Trim();
            var command = Match(task, "Command")?.Trim();
            if (command is null)
            {
                continue;
            }

            var path = ExecutableCommandLine.ResolveExecutable(command);
            if (path is null || ExecutableCommandLine.IsSystemExecutable(path))
            {
                continue;
            }

            Yield.ScheduledTaskPathRecords++;

            var registration = uri is { Length: > 0 } ? $@"Task Scheduler\{uri}" : "Task Scheduler";
            var arguments = Match(task, "Arguments")?.Trim();

            var primaryLinked = TryLink(apps, byInstallLocation, path, registration, out var app, out var corroboration);
            if (primaryLinked)
            {
                var metadata = ProbeMetadata(path);
                var role = ExecutableCommandLine.ClassifyRole(
                    path, ProvenanceSource.ScheduledTask, metadata?.ProductName, metadata?.FileDescription);

                linked.Add(Anchor(app, path, ProvenanceSource.ScheduledTask, role, registration, corroboration, false));
                Yield.ScheduledTaskAnchors++;
            }

            // An updater task whose arguments name a different executable is really an
            // anchor for that executable, not for the updater. This is the shape Task
            // 02 measured in Adobe's update task: a generic updater receiving the
            // product's path as an argument. It is resolved independently, because the
            // updater itself often sits in an unrelated shared directory and cannot be
            // linked at all.
            var argumentTarget = ExecutableCommandLine.ResolveExecutable(arguments);
            if (argumentTarget is null
                || ExecutableCommandLine.IsSystemExecutable(argumentTarget)
                || string.Equals(TextNormalizer.Fold(argumentTarget), TextNormalizer.Fold(path), StringComparison.Ordinal))
            {
                continue;
            }

            if (TryLink(apps, byInstallLocation, argumentTarget, registration, out var argApp, out var argCorroboration))
            {
                linked.Add(Anchor(
                    argApp,
                    argumentTarget,
                    ProvenanceSource.ScheduledTask,
                    ExecutableCommandLine.ClassifyRole(argumentTarget, ProvenanceSource.ScheduledTask),
                    registration,
                    $"{argCorroboration}; named by the task's arguments",
                    false));
                Yield.ScheduledTaskAnchors++;
            }
        }
    }

    /// <summary>
    /// Exports the scheduled-task definitions as XML, read-only.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the only process AppTrace ever starts, and it is an export,
    /// not an action.</b> The command is exactly <c>schtasks /query /xml ONE</c>:
    /// <c>/query</c> reads, <c>/xml</c> chooses the format. It runs no task, creates
    /// nothing, changes nothing and deletes nothing. This is the supported way to read
    /// the task store, which cannot be opened directly by an unelevated process.</para>
    /// <para>If the export is refused — which happens to a process without the rights
    /// to enumerate the store — the source yields nothing and a diagnostic is
    /// recorded. Absence of evidence is not contradiction, and a scan must never fail
    /// because one source was unreadable.</para>
    /// </remarks>
    private string? ReadScheduledTasksXml()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                    // Read-only export of every task: no task is run, none is changed.
                    Arguments = "/query /xml ONE",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // schtasks writes UTF-16LE. Reading it as anything else yields text
                    // with no recognizable elements, which silently produced zero tasks
                    // until this was corrected.
                    StandardOutputEncoding = Encoding.Unicode,
                    StandardErrorEncoding = Encoding.Unicode,
                },
            };

            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);

            if (output.Length == 0 && error.Length > 0)
            {
                // Reading the task store can be refused outright, for example to a
                // process without the rights to enumerate it. That costs the source
                // and nothing else.
                Warn("Task Scheduler", "provenance-task-export", new InvalidOperationException(error.Trim()));
            }

            return output.Length > 0 ? output : null;
        }
        catch (Exception e) when (IsRecoverable(e) || e is System.ComponentModel.Win32Exception)
        {
            Warn("schtasks /query /xml ONE", "provenance-task-export", e);
            return null;
        }
    }

    /// <summary>
    /// Splits the multi-task export. Each document carries its own XML declaration,
    /// so the tasks are separated before any element is read: searching the whole
    /// text would let a task inherit its neighbour's command.
    /// </summary>
    internal static IEnumerable<string> SplitTasks(string xml)
    {
        foreach (var document in xml.Split("<?xml", StringSplitOptions.RemoveEmptyEntries))
        {
            if (document.Contains("<Exec>", StringComparison.Ordinal)
                || document.Contains("<URI>", StringComparison.Ordinal))
            {
                yield return document;
            }
        }
    }

    private static string? Match(string text, string element)
    {
        var start = text.IndexOf($"<{element}>", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += element.Length + 2;
        var end = text.IndexOf($"</{element}>", start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end];
    }

    // ---------------------------------------------------------------------
    // Run keys
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads common startup registrations.
    /// </summary>
    /// <remarks>
    /// A Run entry proves that Windows starts this executable. Its display name is
    /// whatever the vendor chose and is treated as no identity evidence at all.
    /// </remarks>
    private void CollectRunKeys(
        IReadOnlyList<AppIdentity> apps,
        IReadOnlyDictionary<string, AppIdentity> byInstallLocation,
        List<ProvenanceAnchor> linked)
    {
        const string runSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        var surfaces = new (RegistryHive Hive, RegistryView View, string Label)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"HKLM\Software\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"),
        };

        foreach (var (hive, view, label) in surfaces)
        {
            RegistryKey? baseKey = null;
            RegistryKey? run = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                run = baseKey.OpenSubKey(runSubKey, RegistryKeyPermissionCheck.ReadSubTree);
                if (run is null)
                {
                    continue;
                }

                foreach (var valueName in run.GetValueNames())
                {
                    Yield.RunKeyRecords++;
                    try
                    {
                        var raw = run.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                        var path = ExecutableCommandLine.ResolveExecutable(raw);
                        if (path is null || ExecutableCommandLine.IsSystemExecutable(path))
                        {
                            continue;
                        }

                        Yield.RunKeyPathRecords++;

                        var registration = $@"{label}\{valueName}";
                        if (!TryLink(apps, byInstallLocation, path, registration, out var app, out var corroboration))
                        {
                            continue;
                        }

                        var metadata = ProbeMetadata(path);
                        var role = ExecutableCommandLine.ClassifyRole(
                            path, ProvenanceSource.RunKey, metadata?.ProductName, metadata?.FileDescription);

                        linked.Add(Anchor(app, path, ProvenanceSource.RunKey, role, registration, corroboration, false));
                        Yield.RunKeyAnchors++;
                    }
                    catch (Exception e) when (IsRecoverable(e))
                    {
                        Warn($@"{label}\{valueName}", "provenance-run", e);
                    }
                }
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                Warn(label, "provenance-run-open", e);
            }
            finally
            {
                run?.Dispose();
                baseKey?.Dispose();
            }
        }
    }

    // ---------------------------------------------------------------------
    // Shortcuts
    // ---------------------------------------------------------------------

    /// <summary>
    /// Resolves <c>.lnk</c> shortcuts from the standard application shortcut
    /// locations.
    /// </summary>
    /// <remarks>
    /// <para>Resolution uses the Windows shell's own COM interface, which is part of
    /// the operating system and needs no third-party runtime. This was checked before
    /// being adopted; had it required a dependency, shortcuts would have been
    /// reported as unimplemented instead.</para>
    /// <para>The target is provenance and the display name is weak identity, so only
    /// the target is used. Task 02 measured 13% of targets as
    /// updater/uninstaller/launcher, which is why the role matters here.</para>
    /// </remarks>
    private void CollectShortcuts(
        IReadOnlyList<AppIdentity> apps,
        IReadOnlyDictionary<string, AppIdentity> byInstallLocation,
        List<ProvenanceAnchor> linked)
    {
        foreach (var file in EnumerateShortcuts())
        {
            Yield.ShortcutRecords++;
            try
            {
                var target = ResolveShortcut(file, out var arguments);
                if (target is null)
                {
                    Yield.ShortcutBroken++;
                    continue;
                }

                Yield.ShortcutResolved++;

                var path = ExecutableCommandLine.ResolveExecutable(target)
                    ?? (Path.IsPathFullyQualified(target) ? target : null);
                if (path is null || ExecutableCommandLine.IsSystemExecutable(path))
                {
                    continue;
                }

                var role = ExecutableCommandLine.ClassifyRole(path, ProvenanceSource.Shortcut);
                Yield.CountRole(role);

                // A launcher or updater may carry the real application in its
                // arguments; that file is the better anchor when it resolves.
                var argumentTarget = ExecutableCommandLine.ResolveExecutable(arguments);
                if (argumentTarget is not null
                    && !ExecutableCommandLine.IsSystemExecutable(argumentTarget)
                    && !TextNormalizer.Fold(argumentTarget).Contains(TextNormalizer.Fold(path), StringComparison.Ordinal))
                {
                    if (TryLink(apps, byInstallLocation, argumentTarget, file, out var argApp, out var argCorroboration))
                    {
                        linked.Add(Anchor(
                            argApp, argumentTarget, ProvenanceSource.Shortcut,
                            ExecutableCommandLine.ClassifyRole(argumentTarget, ProvenanceSource.Shortcut),
                            file, $"{argCorroboration}; named by the shortcut's arguments", false));
                        Yield.ShortcutAnchors++;
                    }
                }

                if (!TryLink(apps, byInstallLocation, path, file, out var app, out var corroboration))
                {
                    continue;
                }

                linked.Add(Anchor(app, path, ProvenanceSource.Shortcut, role, file, corroboration, false));
                Yield.ShortcutAnchors++;
            }
            catch (Exception e) when (IsRecoverable(e) || e is COMException)
            {
                Warn(file, "provenance-shortcut", e);
            }
        }
    }

    /// <summary>
    /// Finds every shortcut under the standard application shortcut locations.
    /// </summary>
    /// <remarks>
    /// The traversal is written out rather than using
    /// <c>Directory.EnumerateFiles(..., AllDirectories)</c> on purpose. Enumeration is
    /// lazy, so a folder that denies access throws while the caller is iterating —
    /// outside any try/catch around the call — and the Start Menu really does contain
    /// such folders. Walking the tree explicitly means one unreadable folder costs its
    /// own contents and nothing else, which is the failure tolerance this task
    /// requires.
    /// </remarks>
    private List<string> EnumerateShortcuts()
    {
        var roots = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Windows\Start Menu"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        var found = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            Walk(root, found, depth: 0);
        }

        return found;
    }

    private void Walk(string directory, List<string> found, int depth)
    {
        // Shortcut trees are shallow; the bound stops a reparse loop from becoming an
        // unbounded walk.
        if (depth > 8 || found.Count >= 2000)
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.lnk", SearchOption.TopDirectoryOnly))
            {
                found.Add(file);
            }
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            Warn(directory, "provenance-shortcut-list", e);
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            Warn(directory, "provenance-shortcut-list", e);
            return;
        }

        // Materialize the child list here, where a denial is still catchable, then
        // recurse. A failing child is recorded and skipped.
        List<string> childList;
        try
        {
            childList = children.ToList();
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            Warn(directory, "provenance-shortcut-list", e);
            return;
        }

        foreach (var child in childList)
        {
            Walk(child, found, depth + 1);
        }
    }

    private static string? ResolveShortcut(string linkPath, out string? arguments)
    {
        arguments = null;
        object? shell = null;
        object? shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(type);
            if (shell is null)
            {
                return null;
            }

            shortcut = type.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                [linkPath],
                System.Globalization.CultureInfo.InvariantCulture);

            if (shortcut is null)
            {
                return null;
            }

            var shortcutType = shortcut.GetType();
            var target = shortcutType.InvokeMember(
                "TargetPath",
                System.Reflection.BindingFlags.GetProperty,
                null,
                shortcut,
                null,
                System.Globalization.CultureInfo.InvariantCulture) as string;
            arguments = shortcutType.InvokeMember(
                "Arguments",
                System.Reflection.BindingFlags.GetProperty,
                null,
                shortcut,
                null,
                System.Globalization.CultureInfo.InvariantCulture) as string;

            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.ReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.ReleaseComObject(shell);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Linkage
    // ---------------------------------------------------------------------

    /// <summary>
    /// Links a registered executable path to an installed application, or refuses.
    /// </summary>
    /// <remarks>
    /// <para>Ordered from strongest to weakest. The first rule that applies decides,
    /// and the corroboration text records which one it was so the report can be
    /// judged.</para>
    /// <list type="number">
    /// <item>The file sits inside the application's own registered install location.
    /// No name resemblance is involved.</item>
    /// <item>The file's own version metadata names the application.</item>
    /// <item>The file name matches the application's name exactly, and that name is
    /// not a single generic token.</item>
    /// </list>
    /// <para><b>Refusal is the default.</b> Task 02 measured that a bare executable
    /// file name is not product identity: <c>electron.exe</c> carries
    /// <c>CompanyName = "GitHub, Inc."</c> while living inside DaVinci Resolve. The
    /// same reasoning applies here, so a single shared word never links anything.</para>
    /// </remarks>
    private bool TryLink(
        IReadOnlyList<AppIdentity> apps,
        IReadOnlyDictionary<string, AppIdentity> byInstallLocation,
        string path,
        string registration,
        out AppIdentity app,
        out string corroboration)
    {
        app = null!;
        corroboration = string.Empty;

        var normalizedFile = TextNormalizer.NormalizePath(path);
        var directory = Path.GetDirectoryName(normalizedFile);

        // 1. Inside the application's declared install location.
        if (directory is not null)
        {
            var probe = directory;
            while (probe is { Length: > 3 })
            {
                if (byInstallLocation.TryGetValue(probe, out var byLocation))
                {
                    app = byLocation;
                    corroboration = $"the file is inside this application's registered install location";
                    return true;
                }

                probe = Path.GetDirectoryName(probe);
            }
        }

        // 2. The file's own metadata names an application.
        var metadata = ProbeMetadata(path);
        if (metadata is not null)
        {
            foreach (var app2 in apps)
            {
                if (metadata.NamesApplication(app2))
                {
                    app = app2;
                    corroboration = $"the file's own product metadata names this application";
                    return true;
                }
            }
        }

        // 3. The file name matches the application name exactly.
        var stem = TextNormalizer.Fold(Path.GetFileNameWithoutExtension(path));
        if (stem.Length >= 4)
        {
            foreach (var app3 in apps)
            {
                var folded = TextNormalizer.Fold(app3.NormalizedName);
                if (folded.Length == 0 || !string.Equals(stem, folded, StringComparison.Ordinal))
                {
                    continue;
                }

                // A single generic word such as "node" or "setup" is a name, not an
                // identity, and must not be the whole basis for a link.
                if (TextNormalizer.Tokens(app3.NormalizedName).Count <= 1 && stem.Length < 8)
                {
                    continue;
                }

                app = app3;
                corroboration = $"the executable is named after this application";
                return true;
            }
        }

        _ = registration;
        Yield.RefusedLinks++;
        return false;
    }

    private static Dictionary<string, AppIdentity> InstallLocationIndex(IReadOnlyList<AppIdentity> apps)
    {
        var index = new Dictionary<string, AppIdentity>(StringComparer.Ordinal);
        foreach (var app in apps)
        {
            if (app.NormalizedInstallLocation is { Length: > 0 } location)
            {
                index.TryAdd(location.TrimEnd(Path.DirectorySeparatorChar), app);
            }
        }

        return index;
    }

    // ---------------------------------------------------------------------
    // Executable metadata
    // ---------------------------------------------------------------------

    private sealed record FileMetadata(string? ProductName, string? CompanyName, string? FileDescription, string? InternalName)
    {
        public bool NamesApplication(AppIdentity app)
        {
            var candidates = new[] { ProductName, InternalName, FileDescription };
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                // Whole words only, and every word of the metadata value must be a
                // word of the product name: the same rule, and the same reason, as
                // the Task 04 executable-metadata regression.
                var valueSegments = TextNormalizer.Tokens(candidate);
                var appSegments = TextNormalizer.Tokens(app.NormalizedName);
                if (valueSegments.Count == 0 || appSegments.Count == 0)
                {
                    continue;
                }

                if (string.Equals(
                        string.Concat(valueSegments),
                        string.Concat(appSegments),
                        StringComparison.Ordinal))
                {
                    return true;
                }

                if (valueSegments.Count >= 2 && valueSegments.All(appSegments.Contains))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Reads an executable's version metadata, once per path and within a hard
    /// budget.
    /// </summary>
    /// <remarks>
    /// A missing file, a locked file or a file with no version resource all return
    /// null. That is a normal outcome: a registration whose target has since been
    /// deleted is still a statement about what the application reaches.
    /// </remarks>
    private FileMetadata? ProbeMetadata(string path)
    {
        if (_probeCache.TryGetValue(path, out var cached))
        {
            return cached is null ? null : Deserialize(cached);
        }

        if (_probes >= MaxExecutableProbes)
        {
            _probeCache[path] = null;
            return null;
        }

        _probes++;
        try
        {
            if (!File.Exists(path))
            {
                _probeCache[path] = null;
                return null;
            }

            var info = FileVersionInfo.GetVersionInfo(path);
            var metadata = new FileMetadata(
                Blank(info.ProductName),
                Blank(info.CompanyName),
                Blank(info.FileDescription),
                Blank(info.InternalName));

            _probeCache[path] = Serialize(metadata);
            return metadata;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            _probeCache[path] = null;
            return null;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Serialize(FileMetadata m)
        => string.Join('\u001f', m.ProductName, m.CompanyName, m.FileDescription, m.InternalName);

    private static FileMetadata Deserialize(string value)
    {
        var parts = value.Split('\u001f');
        return new FileMetadata(
            parts.ElementAtOrDefault(0) is { Length: > 0 } p ? p : null,
            parts.ElementAtOrDefault(1) is { Length: > 0 } c ? c : null,
            parts.ElementAtOrDefault(2) is { Length: > 0 } f ? f : null,
            parts.ElementAtOrDefault(3) is { Length: > 0 } i ? i : null);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static ProvenanceAnchor Anchor(
        AppIdentity app,
        string path,
        ProvenanceSource source,
        ExecutableRole role,
        string registration,
        string corroboration,
        bool independentlyLinked)
    {
        var directory = Path.GetDirectoryName(TextNormalizer.NormalizePath(path)) ?? string.Empty;
        return new ProvenanceAnchor(app.Id, path, directory, source, role, registration, corroboration)
        {
            IsIndependentlyLinked = independentlyLinked,
        };
    }

    private void Warn(string path, string stage, Exception e)
        => _errors.Add(new ScanError
        {
            Path = path,
            Severity = ScanErrorSeverity.Info,
            Stage = stage,
            Message = $"{e.GetType().Name}: {e.Message}",
        });

    private static bool IsRecoverable(Exception e)
        => e is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException
            or ObjectDisposedException
            or InvalidOperationException
            or ArgumentException
            or PlatformNotSupportedException
            or NotSupportedException;
}

/// <summary>
/// Per-source counts for the provenance yield report.
/// </summary>
/// <remarks>
/// Recorded so that discovery volume is never mistaken for attribution success. The
/// number that matters is how many anchors could be defensibly linked to an
/// installed application, which is why every refusal is counted too.
/// </remarks>
public sealed class ProvenanceYield
{
    public int DisplayIconRecords { get; set; }

    public int DisplayIconValidPaths { get; set; }

    /// <summary>Registrations naming only an icon resource, not an executable.</summary>
    public int DisplayIconResourceOnly { get; set; }

    public int DisplayIconInvalid { get; set; }

    public int DisplayIconAnchors { get; set; }

    public int AppPathRecords { get; set; }

    public int AppPathInvalid { get; set; }

    public int AppPathAnchors { get; set; }

    public int ServicePathRecords { get; set; }

    public int ServiceAnchors { get; set; }

    public int ScheduledTaskRecords { get; set; }

    public int ScheduledTaskPathRecords { get; set; }

    public int ScheduledTaskAnchors { get; set; }

    public int RunKeyRecords { get; set; }

    public int RunKeyPathRecords { get; set; }

    public int RunKeyAnchors { get; set; }

    public int ShortcutRecords { get; set; }

    public int ShortcutResolved { get; set; }

    public int ShortcutBroken { get; set; }

    public int ShortcutAnchors { get; set; }

    /// <summary>Registrations that resolved to a path but linked to no application.</summary>
    public int RefusedLinks { get; set; }

    public Dictionary<ExecutableRole, int> Roles { get; } = [];

    public void CountRole(ExecutableRole role)
        => Roles[role] = Roles.TryGetValue(role, out var count) ? count + 1 : 1;
}
