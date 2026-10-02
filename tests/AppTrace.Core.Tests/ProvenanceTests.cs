using AppTrace.Core.Attribution;
using AppTrace.Core.Discovery;
using AppTrace.Core.Model;

namespace AppTrace.Core.Tests;

/// <summary>
/// Task 07: static Windows provenance enrichment.
/// </summary>
/// <remarks>
/// <para>The question these tests protect:</para>
/// <blockquote>Do not merely ask whether a path looks like an application. Ask
/// whether Windows itself can show that the application actually reaches it.</blockquote>
/// <para>Every source is tested twice: once for what it proves, and once for what it
/// must not be allowed to prove. The second half is the point, because a
/// registration is about the file it names and not about that file's ancestors.</para>
/// </remarks>
public class ProvenanceTests
{
    // ---------------------------------------------------------------------
    // Command-line resolution
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(@"""C:\Program Files\Vendor\app.exe"" --service", @"C:\Program Files\Vendor\app.exe")]
    [InlineData(@"""C:\Program Files\Vendor\app.exe""", @"C:\Program Files\Vendor\app.exe")]
    [InlineData(@"C:\Program Files\Vendor\app.exe --service", @"C:\Program Files\Vendor\app.exe")]
    [InlineData(@"C:\Vendor\app.exe", @"C:\Vendor\app.exe")]
    [InlineData(@"C:\Program Files With Spaces\Vendor\app.exe", @"C:\Program Files With Spaces\Vendor\app.exe")]
    [InlineData(@"C:\Vendor\app.exe -flag value", @"C:\Vendor\app.exe")]
    [InlineData(@"""C:\Vendor\app.exe"",0", @"C:\Vendor\app.exe")]
    [InlineData(@"C:\Vendor\app.dll,-123", @"C:\Vendor\app.dll")]
    public void QuotedAndUnquotedCommandsResolveToTheExecutable(string command, string expected)
        => Assert.Equal(expected, ExecutableCommandLine.ResolveExecutable(command));

    [Theory]
    [InlineData(@"\SystemRoot\System32\svc.exe")]
    [InlineData(@"SystemRoot\System32\svc.exe")]
    [InlineData(@"\??\C:\Windows\System32\drivers\x.sys")]
    [InlineData(@"System32\drivers\x.sys")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("no path here at all")]
    public void ValuesThatAreNotApplicationPathsResolveToNothing(string? command)
        => Assert.Null(ExecutableCommandLine.ResolveExecutable(command));

    [Fact]
    public void UnquotedArgumentsAreNotMistakenForTheExecutable()
    {
        // The extension marks where the path ends, so a later .exe in the arguments
        // must not win.
        var resolved = ExecutableCommandLine.ResolveExecutable(@"C:\Vendor\app.exe --open ""C:\Other\thing.exe""");

        Assert.Equal(@"C:\Vendor\app.exe", resolved);
    }

    [Fact]
    public void IconResourcesAreRecognisedButAreNotExecutables()
    {
        Assert.True(ExecutableCommandLine.IsResourceTarget(@"C:\Vendor\icon.ico"));
        Assert.True(ExecutableCommandLine.IsResourceTarget(@"""C:\Vendor\icon.ico"",0"));
        Assert.False(ExecutableCommandLine.IsResourceTarget(@"C:\Vendor\app.exe"));
        Assert.Null(ExecutableCommandLine.ResolveExecutable(@"C:\Vendor\icon.ico"));
    }

    [Fact]
    public void TheIndexSuffixIsStrippedFromARegistrationValue()
    {
        Assert.Equal(@"C:\Vendor\app.exe", ExecutableCommandLine.StripIndex(@"""C:\Vendor\app.exe"",-101"));
        Assert.Equal(@"C:\Vendor\app.exe", ExecutableCommandLine.StripIndex(@"C:\Vendor\app.exe,0"));
        Assert.Equal(@"C:\Vendor\app.exe", ExecutableCommandLine.StripIndex(@"C:\Vendor\app.exe"));
    }

    // ---------------------------------------------------------------------
    // System executables
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Windows\System32\svc.exe", true)]
    [InlineData(@"C:\Windows\notepad.exe", true)]
    [InlineData(@"C:\Program Files\Vendor\app.exe", false)]
    [InlineData(@"C:\Users\user\AppData\Local\Vendor\app.exe", false)]
    public void SystemExecutablesAreExcludedFromApplicationAnchoring(string path, bool expected)
        => Assert.Equal(expected, ExecutableCommandLine.IsSystemExecutable(path));

    // ---------------------------------------------------------------------
    // Executable roles
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Vendor\unins000.exe", ExecutableRole.Uninstaller)]
    [InlineData(@"C:\Vendor\uninstall.exe", ExecutableRole.Uninstaller)]
    [InlineData(@"C:\Vendor\setup.exe", ExecutableRole.Uninstaller)]
    [InlineData(@"C:\Vendor\updater.exe", ExecutableRole.Updater)]
    [InlineData(@"C:\Vendor\360zipUpdate.exe", ExecutableRole.Updater)]
    [InlineData(@"C:\Vendor\launcher.exe", ExecutableRole.Launcher)]
    [InlineData(@"C:\Vendor\crashpad_handler.exe", ExecutableRole.Helper)]
    [InlineData(@"C:\Vendor\VendorApp.exe", ExecutableRole.MainApplication)]
    public void ExecutableRolesAreClassifiedFromTheFileName(string path, ExecutableRole expected)
        => Assert.Equal(expected, ExecutableCommandLine.ClassifyRole(path, ProvenanceSource.Shortcut));

    [Fact]
    public void AServiceRegistrationClassifiesItsTargetAsAService()
    {
        Assert.Equal(
            ExecutableRole.Service,
            ExecutableCommandLine.ClassifyRole(@"C:\Vendor\VendorApp.exe", ProvenanceSource.Service));
    }

    [Theory]
    [InlineData(ExecutableRole.MainApplication, true)]
    [InlineData(ExecutableRole.Service, true)]
    [InlineData(ExecutableRole.Uninstaller, false)]
    [InlineData(ExecutableRole.Updater, false)]
    [InlineData(ExecutableRole.Launcher, false)]
    public void OnlySomeRolesMayCorroborateProductIdentity(ExecutableRole role, bool expected)
        => Assert.Equal(expected, ExecutableCommandLine.CanCorroborateProductIdentity(role));

    // ---------------------------------------------------------------------
    // Scheduled task parsing
    // ---------------------------------------------------------------------

    private static string Task(string uri, string command, string? arguments = null)
        => $"""
           <?xml version="1.0" encoding="UTF-16"?>
           <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
             <RegistrationInfo><URI>{uri}</URI></RegistrationInfo>
             <Actions Context="Author">
               <Exec>
                 <Command>{command}</Command>
                 {(arguments is null ? string.Empty : $"<Arguments>{arguments}</Arguments>")}
               </Exec>
             </Actions>
           </Task>
           """;

    private static List<ProvenanceAnchor> ParseTasks(string xml, params AppIdentity[] apps)
    {
        var discovery = new ProvenanceDiscovery();
        var linked = new List<ProvenanceAnchor>();
        discovery.CollectScheduledTasks(xml, apps, InstallLocationIndex(apps), linked);
        return linked;
    }

    private static Dictionary<string, AppIdentity> InstallLocationIndex(IReadOnlyList<AppIdentity> apps)
    {
        var index = new Dictionary<string, AppIdentity>(StringComparer.Ordinal);
        foreach (var app in apps)
        {
            if (app.NormalizedInstallLocation is { Length: > 0 } location)
            {
                index[location.TrimEnd(Path.DirectorySeparatorChar)] = app;
            }
        }

        return index;
    }

    [Fact]
    public void ATaskExecActionProducesAnAnchorForTheApplicationThatOwnsThePath()
    {
        var app = Fixtures.App("Vendor App", "Vendor Ltd.", @"C:\Program Files\Vendor\Vendor App");

        var anchors = ParseTasks(
            Task(@"\Vendor\Updater", @"C:\Program Files\Vendor\Vendor App\VendorApp.exe", "--quiet"),
            app);

        var anchor = Assert.Single(anchors);
        Assert.Equal(app.Id, anchor.AppId);
        Assert.Equal(ProvenanceSource.ScheduledTask, anchor.Source);
        Assert.Contains("VendorApp.exe", anchor.Path);
    }

    [Fact]
    public void EachTaskInAnExportIsParsedIndependently()
    {
        // Searching the whole export for the first <Command> would let one task
        // inherit its neighbour's executable, which is why the tasks are split first.
        var first = Fixtures.App("First App", "First Ltd.", @"C:\Program Files\First\First App");
        var second = Fixtures.App("Second App", "Second Ltd.", @"C:\Program Files\Second\Second App");

        var anchors = ParseTasks(
            Task(@"\First\Task", @"C:\Program Files\First\First App\first.exe")
            + Task(@"\Second\Task", @"C:\Program Files\Second\Second App\second.exe"),
            first,
            second);

        Assert.Equal(2, anchors.Count);
        Assert.Contains(anchors, a => a.AppId == first.Id && a.Path.EndsWith("first.exe", StringComparison.Ordinal));
        Assert.Contains(anchors, a => a.AppId == second.Id && a.Path.EndsWith("second.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void ASystemExecutableInATaskIsIgnored()
    {
        var app = Fixtures.App("Vendor App", "Vendor Ltd.", @"C:\Program Files\Vendor\Vendor App");

        var anchors = ParseTasks(
            Task(@"\Microsoft\Windows\Defrag\ScheduledDefrag", @"C:\Windows\System32\defrag.exe"),
            app);

        Assert.Empty(anchors);
    }

    [Fact]
    public void AMalformedTaskDegradesToNothingRatherThanFailing()
    {
        var app = Fixtures.App("Vendor App", "Vendor Ltd.", @"C:\Program Files\Vendor\Vendor App");

        // No Command element, a truncated document, and stray text.
        Assert.Empty(ParseTasks(Task(@"\Vendor\Task", string.Empty), app));
        Assert.Empty(ParseTasks(Task(@"\Vendor\Task", "not a path"), app));
        Assert.Empty(ParseTasks("", app));
        Assert.Empty(ParseTasks("<?xml version=\"1.0\"?><Task><URI>\\Broken</URI>", app));
    }

    [Fact]
    public void AnUpdaterTaskAnchorsTheProductItsArgumentsName()
    {
        // The Adobe update-task shape: a generic updater in a shared directory takes
        // the product's path as an argument. The product file is the better anchor, and
        // it must be produced even though the updater itself links to nothing.
        using var tree = new TempTree();
        var product = tree.File(@"Vendor App\VendorApp.exe", 32);
        var updater = tree.File(@"Vendor\Updater\ARM.exe", 32);

        var app = Fixtures.App("Vendor App", "Vendor Ltd.", Path.GetDirectoryName(product));

        var anchors = ParseTasks(
            Task(@"\Vendor\Update", updater, $@"""{product}"""),
            app);

        Assert.Contains(anchors, a => a.Path.EndsWith("VendorApp.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void ATaskNamingAMissingFileStillProducesNoAnchorForIt()
    {
        // A registration whose target no longer exists is still a statement about what
        // the application reaches, but nothing can link the file to an application, so
        // it stays an unlinked registration rather than a guessed anchor.
        var app = Fixtures.App("Vendor App", "Vendor Ltd.", @"C:\Program Files\Vendor\Vendor App");

        var anchors = ParseTasks(
            Task(@"\Vendor\Update", @"C:\Program Files\Vendor\Updater\ARM.exe"),
            app);

        Assert.Empty(anchors);
    }

    // ---------------------------------------------------------------------
    // Provenance as evidence
    // ---------------------------------------------------------------------

    private static ProvenanceIndex Index(params ProvenanceAnchor[] anchors) => new(anchors);

    private static ProvenanceAnchor AnchorFor(
        AppIdentity app,
        string path,
        ProvenanceSource source = ProvenanceSource.Service,
        ExecutableRole role = ExecutableRole.MainApplication)
        => new(
            app.Id,
            path,
            Path.GetDirectoryName(TextNormalizer.NormalizePath(path)) ?? string.Empty,
            source,
            role,
            "test registration",
            "the file is inside this application's registered install location")
        {
            IsIndependentlyLinked = true,
        };

    [Fact]
    public void StrongProvenancePlusMatchingIdentityReachesHigh()
    {
        // The ladder rule Task 07 must preserve: provenance plus identity agreement.
        var app = Fixtures.App("Vendor App", "Vendor Ltd.");

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Vendor\Vendor App",
            [app],
            LocationCategory.ProgramFiles,
            provenance: Index(AnchorFor(app, @"C:\Program Files\Vendor\Vendor App\VendorApp.exe")));

        Assert.Equal(Classification.High, attribution.Classification);
        Assert.True(attribution.OwnershipEstablished);
    }

    [Fact]
    public void ProvenanceWithoutIdentityCannotManufactureHighOwnership()
    {
        // A registration is about the file it names. With no identity agreement at
        // this directory, it must not promote the claim to HIGH.
        var app = Fixtures.App("Completely Different Name", "Vendor Ltd.");

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Vendor\SomeOtherDirectory",
            [app],
            LocationCategory.ProgramFiles,
            provenance: Index(AnchorFor(app, @"C:\Program Files\Vendor\SomeOtherDirectory\svc.exe")));

        var owner = Fixtures.Candidate(attribution, "Completely Different Name");
        Assert.NotNull(owner);
        Assert.True(owner.Accepted);
        Assert.Equal(Classification.Medium, owner.Classification);
        Assert.NotEqual(Classification.High, attribution.Classification);
    }

    [Fact]
    public void StructurePlusProvenanceAloneIsStillNotHigh()
    {
        // "This is an AppData folder" plus a registration with no identity match is
        // not a confident claim.
        var app = Fixtures.App("Nothing Alike", "Vendor Ltd.");

        var attribution = Fixtures.Evaluate(
            @"C:\Users\user\AppData\Local\VendorName",
            [app],
            LocationCategory.LocalAppData,
            provenance: Index(AnchorFor(app, @"C:\Users\user\AppData\Local\VendorName\svc.exe")));

        Assert.NotEqual(Classification.High, attribution.Classification);
    }

    [Fact]
    public void TheProvenanceEvidenceRecordSaysWhatTheRegistrationProves()
    {
        var app = Fixtures.App("Vendor App", "Vendor Ltd.");

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Vendor\Vendor App",
            [app],
            LocationCategory.ProgramFiles,
            provenance: Index(AnchorFor(app, @"C:\Program Files\Vendor\Vendor App\VendorApp.exe")));

        var evidence = attribution.AcceptedOwners.Single()
            .Evidence.Single(e => e.Type == EvidenceType.ProvenanceAnchorMatch);

        Assert.Equal(EvidenceKind.Provenance, evidence.Kind);
        Assert.Contains("shows", evidence.Description, StringComparison.Ordinal);
        Assert.Contains("VendorApp.exe", evidence.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ALowValueAnchorIsStillReportedButMarked()
    {
        // An uninstaller really is a file the application reaches, so it is an anchor;
        // the explanation says what it is so it is not read as the product's own
        // executable.
        var app = Fixtures.App("Vendor App", "Vendor Ltd.");

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Vendor\Vendor App\Installer",
            [app],
            LocationCategory.ProgramFiles,
            provenance: Index(AnchorFor(
                app,
                @"C:\Program Files\Vendor\Vendor App\Installer\unins000.exe",
                ProvenanceSource.Shortcut,
                ExecutableRole.Uninstaller)));

        var evidence = attribution.AcceptedOwners.Single()
            .Evidence.Single(e => e.Type == EvidenceType.ProvenanceAnchorMatch);

        Assert.Contains("Uninstaller", evidence.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDisplayIconOfTheApplicationsOwnEntryIsItsOwnEvidenceType()
    {
        var app = Fixtures.App("Vendor App", "Vendor Ltd.");

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Vendor\Vendor App",
            [app],
            LocationCategory.ProgramFiles,
            provenance: Index(AnchorFor(
                app,
                @"C:\Program Files\Vendor\Vendor App\VendorApp.exe",
                ProvenanceSource.DisplayIcon)));

        Assert.Contains(
            attribution.AcceptedOwners.Single().Evidence,
            e => e.Type == EvidenceType.DisplayIconMatch);
    }

    // ---------------------------------------------------------------------
    // Boundaries
    // ---------------------------------------------------------------------

    [Fact]
    public void AnAnchorInAnAncestorDoesNotHandOverUnrelatedSiblings()
    {
        // The registration names a file at the product root. A sibling directory the
        // product has nothing to do with must not inherit it.
        var vendor = Fixtures.App("Vendor App", "Vendor Ltd.");
        var sibling = Fixtures.App("Unrelated Thing", "Other Ltd.");

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Other",
            [vendor, sibling],
            LocationCategory.ProgramFiles,
            provenance: Index(AnchorFor(vendor, @"C:\Program Files\Vendor\VendorApp.exe")));

        Assert.DoesNotContain(attribution.Candidates, c => c.AppId == vendor.Id && c.Accepted);
    }

    [Fact]
    public void TheAncestorWindowIsBounded()
    {
        // A registration buried deep inside one product must not reach the top of the
        // tree, or every directory above it would be promoted.
        var app = Fixtures.App("Vendor App", "Vendor Ltd.");

        var index = Index(AnchorFor(
            app,
            @"C:\Program Files\Vendor\Vendor App\a\b\c\VendorApp.exe"));

        Assert.Empty(index.ForDirectory(TextNormalizer.NormalizePath(@"C:\Program Files")));
        Assert.NotEmpty(index.ForDirectory(TextNormalizer.NormalizePath(@"C:\Program Files\Vendor\Vendor App\a")));
    }

    [Fact]
    public void ComponentMetadataCannotTransferAParentDirectoryToAnotherApplication()
    {
        // The Task 04 regression, restated for Task 07: a component's metadata is
        // provenance for the component, never ownership of the tree containing it.
        var git = Fixtures.App("Git", "The Git Development Community", @"C:\Program Files\Git\");
        var daVinci = Fixtures.App("DaVinci Resolve", "Blackmagic Design");

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Blackmagic Design\DaVinci Resolve\Electron",
            [git, daVinci],
            LocationCategory.ProgramFiles,
            provenance: Index(AnchorFor(
                daVinci,
                @"C:\Program Files\Blackmagic Design\DaVinci Resolve\Electron\electron.exe")));

        Assert.DoesNotContain(attribution.Candidates, c => c.AppId == git.Id && c.Accepted);
        Assert.NotEqual(Classification.High, attribution.Classification);
    }

    // ---------------------------------------------------------------------
    // Integration with Task 06 propagation
    // ---------------------------------------------------------------------

    [Fact]
    public void ProvenanceEstablishedOwnershipFeedsDescendantPropagation()
    {
        // The intended composition: static provenance establishes the owner at the
        // product boundary, and Task 06's propagation carries it into ordinary
        // application data children. There is no second propagation system.
        var app = Fixtures.App("Vendor App", "Vendor Ltd.");

        var root = AttributionEvaluatorRoot(app);
        var owned = new[]
        {
            new OwnedAncestor(TextNormalizer.NormalizePath(root), app.Id, Classification.High),
        };

        var attribution = Fixtures.Evaluate(
            root + @"\User Data\Default\Cache",
            [app],
            LocationCategory.ProgramFiles,
            acceptedAncestors: [root],
            ownedAncestors: owned);

        var owner = attribution.AcceptedOwners.SingleOrDefault();
        Assert.NotNull(owner);
        Assert.Equal(app.Id, owner.AppId);
        Assert.Contains(owner.Evidence, e => e.Type == EvidenceType.InheritedFromOwner);
    }

    private static string AttributionEvaluatorRoot(AppIdentity app)
        => @"C:\Program Files\Vendor\Vendor App";

    // ---------------------------------------------------------------------
    // Task 05 regressions
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("sdk")]
    [InlineData("node")]
    [InlineData("helper")]
    [InlineData("tool")]
    [InlineData("Universal")]
    [InlineData("adm-zip")]
    [InlineData("cities_skylines")]
    public void ProvenanceDoesNotReopenTheGenericTokenFalsePositives(string leaf)
    {
        // Task 05 refused these by structure and specificity. An unrelated
        // registration elsewhere must not reopen them.
        var asus = Fixtures.App("ASUS Aura SDK", "ASUSTeK COMPUTER INC.");
        var node = Fixtures.App("Node.js Krypton via nvm-windows", "OpenJS Foundation");
        var epson = Fixtures.App("Epson Printer Driver Security Support Tool", "Seiko Epson Corporation");
        var holtek = Fixtures.App("Universal Holtek RGB DRAM", "PD");
        var zip = Fixtures.App("360 Zip", "360 Security Center", @"C:\Program Files (x86)\360\360zip");
        var skies = Fixtures.App("Cities: Skylines", "Colossal Order");

        var attribution = Fixtures.Evaluate(
            @"C:\Users\user\AppData\Local\npm-cache\_npx\1e7f6d95\node_modules\@anthropic-ai\" + leaf,
            [asus, node, epson, holtek, zip, skies],
            LocationCategory.LocalAppData,
            provenance: Index(AnchorFor(asus, @"C:\Program Files\ASUS\Aura\aura.exe")));

        Assert.Empty(attribution.AcceptedOwners);
        Assert.Equal(Classification.Unknown, attribution.Classification);
    }

    [Fact]
    public void AnEmptyProvenanceIndexChangesNothing()
    {
        // Provenance is enrichment, not a prerequisite: with no anchors the engine
        // must behave exactly as it did before it existed.
        var app = Fixtures.App("Vendor App", "Vendor Ltd.");

        var withIndex = Fixtures.Evaluate(
            @"C:\Program Files\Vendor\Vendor App",
            [app],
            LocationCategory.ProgramFiles,
            provenance: ProvenanceIndex.Empty);

        var withoutIndex = Fixtures.Evaluate(
            @"C:\Program Files\Vendor\Vendor App",
            [app],
            LocationCategory.ProgramFiles);

        Assert.Equal(withoutIndex.Classification, withIndex.Classification);
        Assert.Equal(
            withoutIndex.Candidates.Select(c => c.AppId),
            withIndex.Candidates.Select(c => c.AppId));
    }

    [Fact]
    public void ProvenanceAddsNoBytesAndNoOwnersOfItsOwn()
    {
        // A registration does not create an application, and it cannot create a
        // candidate for one that discovery never found.
        var app = Fixtures.App("Vendor App", "Vendor Ltd.");

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Vendor\Vendor App",
            [app],
            LocationCategory.ProgramFiles,
            provenance: Index(
                AnchorFor(app, @"C:\Program Files\Vendor\Vendor App\VendorApp.exe"),
                new ProvenanceAnchor(
                    "app-not-installed",
                    @"C:\Program Files\Vendor\Vendor App\ghost.exe",
                    @"c:\program files\vendor\vendor app",
                    ProvenanceSource.Service,
                    ExecutableRole.Service,
                    "test",
                    "test")));

        Assert.All(attribution.AcceptedOwners, o => Assert.Equal(app.Id, o.AppId));
    }
}
