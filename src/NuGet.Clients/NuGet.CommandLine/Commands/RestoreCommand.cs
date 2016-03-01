using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Commands;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.DependencyResolver;
using NuGet.Logging;
using NuGet.PackageManagement;
using NuGet.Packaging;
using NuGet.ProjectManagement;
using NuGet.ProjectModel;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.Core.v3;
using NuGet.Repositories;

namespace NuGet.CommandLine
{
    [Command(typeof(NuGetCommand), "restore", "RestoreCommandDescription",
        MinArgs = 0, MaxArgs = 128, UsageSummaryResourceName = "RestoreCommandUsageSummary",
        UsageDescriptionResourceName = "RestoreCommandUsageDescription",
        UsageExampleResourceName = "RestoreCommandUsageExamples")]
    public class RestoreCommand : DownloadCommandBase
    {
        [Option(typeof(NuGetCommand), "RestoreCommandRequireConsent")]
        public bool RequireConsent { get; set; }

        [Option(typeof(NuGetCommand), "RestoreCommandP2PTimeOut")]
        public int Project2ProjectTimeOut { get; set; }

        [Option(typeof(NuGetCommand), "RestoreCommandPackagesDirectory", AltName = "OutputDirectory")]
        public string PackagesDirectory { get; set; }

        [Option(typeof(NuGetCommand), "RestoreCommandSolutionDirectory")]
        public string SolutionDirectory { get; set; }

        [Option(typeof(NuGetCommand), "CommandMSBuildVersion")]
        public string MSBuildVersion { get; set; }

        private readonly List<string> _runtimes = new List<string>();

        [Option(typeof(NuGetCommand), "CommandRuntimeDescription")]
        public ICollection<string> Runtime
        {
            get { return _runtimes; }
        }

        private static readonly int MaxDegreesOfConcurrency = Environment.ProcessorCount;

        [ImportingConstructor]
        public RestoreCommand()
            : base(MachineCache.Default)
        {
        }

        // The directory that contains msbuild
        private string _msbuildDirectory;

        public override async Task ExecuteCommandAsync()
        {
            CalculateEffectivePackageSaveMode();

            var restoreSummaries = new List<RestoreSummary>();

            _msbuildDirectory = MsBuildUtility.GetMsbuildDirectory(MSBuildVersion, Console);

            if (!string.IsNullOrEmpty(PackagesDirectory))
            {
                PackagesDirectory = Path.GetFullPath(PackagesDirectory);
            }

            if (!string.IsNullOrEmpty(SolutionDirectory))
            {
                SolutionDirectory = Path.GetFullPath(SolutionDirectory);
            }

            var restoreInputs = DetermineRestoreInputs();

            // packages.config
            if (restoreInputs.PackagesConfigFiles.Count > 0)
            {
                var v2RestoreResult = await PerformNuGetV2RestoreAsync(restoreInputs);
                restoreSummaries.AddRange(v2RestoreResult);
            }

            // project.json
            if (restoreInputs.RestoreV3Context.Inputs.Any())
            {
                using (var cacheContext = new SourceCacheContext())
                {
                    var providerCache = new RestoreCommandProvidersCache();

                    // Read the settings outside of parallel loops.
                    var settingsOverrideRoot = SolutionDirectory ?? restoreInputs.SolutionFiles.FirstOrDefault();
                    ReadSettings(settingsOverrideRoot);

                    // Add restore args to the restore context
                    var restoreContext = restoreInputs.RestoreV3Context;
                    cacheContext.NoCache = NoCache;
                    restoreContext.CacheContext = cacheContext;
                    restoreContext.DisableParallel = DisableParallelProcessing;
                    restoreContext.ConfigFileName = ConfigFile;
                    restoreContext.MachineWideSettings = MachineWideSettings;
                    restoreContext.Sources = Source.ToList();
                    restoreContext.Log = Console;
                    restoreContext.CachingSourceProvider = GetSourceRepositoryProvider();
                    restoreContext.Runtimes.UnionWith(Runtime);

                    var packageSaveMode = EffectivePackageSaveMode;
                    if (packageSaveMode != Packaging.PackageSaveMode.None)
                    {
                        restoreContext.PackageSaveMode = EffectivePackageSaveMode;
                    }

                    // Override packages folder
                    var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(Settings);
                    restoreContext.GlobalPackagesFolder = GetEffectiveGlobalPackagesFolder(
                                        PackagesDirectory,
                                        SolutionDirectory,
                                        globalPackagesFolder);

                    // Providers
                    restoreContext.RequestProviders.Add(new MSBuildCachedRequestProvider(
                        providerCache,
                        restoreInputs.ProjectReferenceLookup));
                    restoreContext.RequestProviders.Add(new MSBuildP2PRestoreRequestProvider(providerCache));
                    restoreContext.RequestProviders.Add(new ProjectJsonRestoreRequestProvider(providerCache));

                    // Run restore
                    var v3Summaries = await RestoreRunner.Run(restoreContext);
                    restoreSummaries.AddRange(v3Summaries);
                }
            }

            // Summaries
            RestoreSummary.Log(Console, restoreSummaries, Verbosity != Verbosity.Quiet);

            if (restoreSummaries.Any(x => !x.Success))
            {
                throw new ExitCodeException(exitCode: 1);
            }
        }

        private static string GetEffectiveGlobalPackagesFolder(
            string packagesDirectoryParameter,
            string solutionDirectoryParameter,
            string globalPackagesFolder)
        {
            // Return the -PackagesDirectory parameter if specified
            if (!string.IsNullOrEmpty(packagesDirectoryParameter))
            {
                return packagesDirectoryParameter;
            }

            // Return the globalPackagesFolder as-is if it is a full path
            if (Path.IsPathRooted(globalPackagesFolder))
            {
                return globalPackagesFolder;
            }
            else if (!string.IsNullOrEmpty(solutionDirectoryParameter))
            {
                // -PackagesDirectory parameter was not provided and globalPackagesFolder is a relative path.
                // Use the solutionDirectory to construct the full path
                return Path.Combine(solutionDirectoryParameter, globalPackagesFolder);
            }

            // -PackagesDirectory parameter was not provided and globalPackagesFolder is a relative path.
            // solution directory is not available either. Throw
            var message = string.Format(
                CultureInfo.CurrentCulture,
                LocalizedResourceManager.GetString("RestoreCommandCannotDetermineGlobalPackagesFolder"));

            throw new CommandLineException(message);
        }

        private static CachingSourceProvider _sourceProvider;
        private CachingSourceProvider GetSourceRepositoryProvider()
        {
            if (_sourceProvider == null)
            {
                _sourceProvider = new CachingSourceProvider(SourceProvider);
            }

            return _sourceProvider;
        }

        private void ReadSettings(string currentSolutionDirectory)
        {
            if (!string.IsNullOrEmpty(SolutionDirectory) || !string.IsNullOrEmpty(currentSolutionDirectory))
            {
                var solutionDirectory = !string.IsNullOrEmpty(currentSolutionDirectory) ?
                    currentSolutionDirectory :
                    SolutionDirectory;

                // Read the solution-level settings
                var solutionSettingsFile = Path.Combine(
                    solutionDirectory,
                    NuGetConstants.NuGetSolutionSettingsFolder);
                if (ConfigFile != null)
                {
                    ConfigFile = Path.GetFullPath(ConfigFile);
                }

                Settings = Configuration.Settings.LoadDefaultSettings(
                    solutionSettingsFile,
                    configFileName: ConfigFile,
                    machineWideSettings: MachineWideSettings);

                // Recreate the source provider and credential provider
                SourceProvider = PackageSourceBuilder.CreateSourceProvider(Settings);
                SetDefaultCredentialProvider();
            }
        }

        private async Task<List<RestoreSummary>> PerformNuGetV2RestoreAsync(PackageRestoreInputs packageRestoreInputs)
        {
            var summaries = new List<RestoreSummary>();
            var missingPackages = 0;

            // Restore for one root directory at a time
            // This is needed since some of the settings are currently set on the base class.
            // Settings may share a common root directory, those can be run together.
            // Null group.Key values indicate no solution file.
            var groups = packageRestoreInputs.PackagesConfigFiles
                .GroupBy(solutionInput => solutionInput.RootDirectory, StringComparer.Ordinal)
                .ToList();

            foreach (var group in groups)
            {
                var rootDirectory = group.Key;

                // Apply settings
                ReadSettings(rootDirectory);

                // Get packages folder relative to the solution, or throw if one cannot be found.
                var packagesFolderPath = GetPackagesFolder(rootDirectory);

                var sourceRepositoryProvider = new CommandLineSourceRepositoryProvider(SourceProvider);
                var nuGetPackageManager = new NuGetPackageManager(sourceRepositoryProvider, Settings, packagesFolderPath);

                var installedPackageReferences = new HashSet<Packaging.PackageReference>(new PackageReferenceComparer());

                // Create a look up of references
                var itemReferences = new Dictionary<SolutionInput, List<Packaging.PackageReference>>();

                foreach (var item in group)
                {
                    if (itemReferences.ContainsKey(item))
                    {
                        var references = GetInstalledPackageReferences(item.Input, allowDuplicatePackageIds: true) 
                            .ToList();

                        itemReferences.Add(item, references);
                    }
                }

                // Full list of packages
                installedPackageReferences.UnionWith(itemReferences.SelectMany(item => item.Value));

                // Missing packages
                var missingPackageReferences = installedPackageReferences.Where(reference =>
                    !nuGetPackageManager.PackageExistsInPackagesFolder(reference.PackageIdentity)).ToArray();

                if (missingPackageReferences.Length == 0)
                {
                    // No packages to install
                    summaries.Add(new RestoreSummary(true));
                    continue;
                }

                var packageRestoreData = new List<PackageRestoreData>();

                foreach (var reference in missingPackageReferences)
                {
                    // Find all items that reference the missing package
                    var parents = new HashSet<string>();

                    foreach (var pair in itemReferences)
                    {
                        if (pair.Value.Equals(reference))
                        {
                            parents.Add(pair.Key.Input);
                        }
                    }

                    var record = new PackageRestoreData(
                        reference,
                        parents,
                        isMissing: true);

                    packageRestoreData.Add(record);
                }

                var packageSources = GetPackageSources(Settings);

                var repositories = packageSources
                    .Select(sourceRepositoryProvider.CreateRepository)
                    .ToArray();

                var installCount = 0;
                var failedEvents = new ConcurrentQueue<PackageRestoreFailedEventArgs>();

                var packageRestoreContext = new PackageRestoreContext(
                    nuGetPackageManager,
                    packageRestoreData,
                    CancellationToken.None,
                    packageRestoredEvent: (sender, args) => { Interlocked.Add(ref installCount, args.Restored ? 1 : 0); },
                    packageRestoreFailedEvent: (sender, args) => { failedEvents.Enqueue(args); },
                    sourceRepositories: repositories,
                    maxNumberOfParallelTasks: DisableParallelProcessing
                            ? 1
                            : PackageManagementConstants.DefaultMaxDegreeOfParallelism);

                CheckRequireConsent();

                var collectorLogger = new CollectorLogger(Console);
                var projectContext = new ConsoleProjectContext(collectorLogger)
                {
                    PackageExtractionContext = new PackageExtractionContext()
                };

                if (EffectivePackageSaveMode != Packaging.PackageSaveMode.None)
                {
                    projectContext.PackageExtractionContext.PackageSaveMode = EffectivePackageSaveMode;
                }

                var result = await PackageRestoreManager.RestoreMissingPackagesAsync(
                    packageRestoreContext,
                    projectContext);

                var summary = new RestoreSummary(
                    result.Restored,
                    "packages.config projects",
                    Settings.Priority.Select(x => Path.Combine(x.Root, x.FileName)),
                    packageSources.Select(x => x.Source),
                    installCount,
                    collectorLogger.Errors.Concat(failedEvents.Select(e => e.Exception.Message)));

                summaries.Add(summary);
            }

            if (missingPackages == 0 && summaries.Count == 0)
            {
                var message = string.Format(
                        CultureInfo.CurrentCulture,
                        LocalizedResourceManager.GetString("InstallCommandNothingToInstall"),
                        "packages.config");

                Console.LogMinimal(message);
            }

            return summaries;
        }

        private void CheckRequireConsent()
        {
            if (RequireConsent)
            {
                var packageRestoreConsent = new PackageRestoreConsent(new SettingsToLegacySettings(Settings));

                if (packageRestoreConsent.IsGranted)
                {
                    var message = string.Format(
                        CultureInfo.CurrentCulture,
                        LocalizedResourceManager.GetString("RestoreCommandPackageRestoreOptOutMessage"),
                        NuGet.Resources.NuGetResources.PackageRestoreConsentCheckBoxText.Replace("&", ""));

                    Console.LogMinimal(message);
                }
                else
                {
                    var message = string.Format(
                        CultureInfo.CurrentCulture,
                        LocalizedResourceManager.GetString("InstallCommandPackageRestoreConsentNotFound"),
                        NuGet.Resources.NuGetResources.PackageRestoreConsentCheckBoxText.Replace("&", ""));

                    throw new CommandLineException(message);
                }
            }
        }

        /// <summary>
        /// Discover all restore inputs, this checks for both v2 and v3
        /// </summary>
        private PackageRestoreInputs DetermineRestoreInputs()
        {
            var packageRestoreInputs = new PackageRestoreInputs();

            if (Arguments.Count == 0)
            {
                // If no arguments were provided use the current directory
                GetInputsFromDirectory(Directory.GetCurrentDirectory(), packageRestoreInputs);
            }
            else
            {
                // Restore takes multiple arguments, each could be a file or directory
                foreach (var argument in Arguments)
                {
                    var fullPath = Path.GetFullPath(argument);

                    if (Directory.Exists(fullPath))
                    {
                        // Dir
                        GetInputsFromDirectory(fullPath, packageRestoreInputs);
                    }
                    else if (File.Exists(fullPath))
                    {
                        // File
                        GetInputsFromFile(fullPath, packageRestoreInputs);
                    }
                    else
                    {
                        // Not found
                        var message = string.Format(
                            CultureInfo.CurrentCulture,
                            LocalizedResourceManager.GetString("RestoreCommandFileNotFound"),
                            argument);

                        throw new InvalidOperationException(message);
                    }
                }
            }

            // Find P2P graph for v3 inputs.
            var projectsWithPotentialP2PReferences = packageRestoreInputs.RestoreV3Context.Inputs
                .Where(MsBuildUtility.IsMsBuildBasedProject)
                .ToArray();

            if (projectsWithPotentialP2PReferences.Length > 0)
            {
                int scaleTimeout;

                if (Project2ProjectTimeOut > 0)
                {
                    scaleTimeout = Project2ProjectTimeOut * 1000;
                }
                else
                {
                    scaleTimeout = MsBuildUtility.MsBuildWaitTime *
                        Math.Max(10, projectsWithPotentialP2PReferences.Length / 2) / 10;
                }

                Console.LogVerbose($"MSBuild P2P timeout [ms]: {scaleTimeout}");

                // Call MSBuild to resolve P2P references.
                var referencesLookup = MsBuildUtility.GetProjectReferences(
                    _msbuildDirectory,
                    projectsWithPotentialP2PReferences,
                    scaleTimeout);

                packageRestoreInputs.ProjectReferenceLookup = referencesLookup;
            }

            return packageRestoreInputs;
        }

        /// <summary>
        /// Determine if a file is v2 or v3
        /// </summary>
        private void GetInputsFromFile(string projectFilePath, PackageRestoreInputs packageRestoreInputs)
        {
            // An argument was passed in. It might be a solution file or directory,
            // project file, project.json, or packages.config file
            var projectFileName = Path.GetFileName(projectFilePath);

            if (ProjectJsonPathUtilities.IsProjectConfig(projectFileName))
            {
                // project.json or projName.project.json
                packageRestoreInputs.RestoreV3Context.Inputs.Add(projectFilePath);
            }
            else if (IsPackagesConfig(projectFileName))
            {
                // restoring from packages.config or packages.projectname.config file
                packageRestoreInputs.PackagesConfigFiles.Add(
                        new SolutionInput(solutionFile: null, input: projectFilePath));
            }
            else if (projectFileName.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            {
                // For msbuild files find the project.json or packages.config file,
                // if neither exist skip it
                var projectName = Path.GetFileNameWithoutExtension(projectFileName);
                var dir = Path.GetDirectoryName(projectFilePath);

                var projectJsonPath = ProjectJsonPathUtilities.GetProjectConfigPath(dir, projectName);
                var packagesConfigPath = GetPackageReferenceFile(projectFilePath);

                // Check for project.json
                if (File.Exists(projectJsonPath))
                {
                    if (MsBuildUtility.IsMsBuildBasedProject(projectFilePath))
                    {
                        // Add the project file path if it allows p2ps
                        packageRestoreInputs.RestoreV3Context.Inputs.Add(projectFilePath);
                    }
                    else
                    {
                        // Unknown project type, add the project.json by itself
                        packageRestoreInputs.RestoreV3Context.Inputs.Add(projectJsonPath);
                    }
                }
                else if (File.Exists(packagesConfigPath))
                {
                    // Check for packages.config, if it exists add it directly
                    packageRestoreInputs.PackagesConfigFiles.Add(
                        new SolutionInput(solutionFile: null, input: packagesConfigPath));
                }
            }
            else if (projectFileName.EndsWith(".dg", StringComparison.OrdinalIgnoreCase))
            {
                packageRestoreInputs.RestoreV3Context.Inputs.Add(projectFilePath);
            }
            else if (projectFileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                ProcessSolutionFile(projectFilePath, packageRestoreInputs);
            }
        }

        /// <summary>
        /// Search a directory for v2 or v3 inputs, only the first type is taken.
        /// </summary>
        private void GetInputsFromDirectory(string directory, PackageRestoreInputs packageRestoreInputs)
        {
            var topLevelFiles = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly);

            //  Solution files
            var solutionFiles = topLevelFiles.Where(file =>
                file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            if (solutionFiles.Length > 0)
            {
                foreach (var file in solutionFiles)
                {
                    if (Verbosity == Verbosity.Detailed)
                    {
                        var message = string.Format(CultureInfo.CurrentCulture,
                            LocalizedResourceManager.GetString("FoundRestoreInput"),
                            file);

                        Console.WriteLine(message);
                    }

                    ProcessSolutionFile(file, packageRestoreInputs);
                }

                return;
            }

            // Packages.config
            var packagesConfigFiles = topLevelFiles.Where(file =>
                IsPackagesConfig(Path.GetFileName(file)))
                    .ToArray();

            if (packagesConfigFiles.Length > 0)
            {
                foreach (var file in packagesConfigFiles)
                {
                    if (Verbosity == Verbosity.Detailed)
                    {
                        var message = string.Format(CultureInfo.CurrentCulture,
                        LocalizedResourceManager.GetString("FoundRestoreInput"),
                        file);

                        Console.WriteLine(message);
                    }

                    // packages.confg with no solution file
                    packageRestoreInputs.PackagesConfigFiles.Add(
                        new SolutionInput(solutionFile: null, input: file));
                }

                return;
            }

            // Project.json is any directory
            if (Directory.GetFiles(
                directory,
                $"*{ProjectJsonPathUtilities.ProjectConfigFileName}",
                SearchOption.AllDirectories).Length > 0)
            {
                // V3 recursive project.json search
                packageRestoreInputs.RestoreV3Context.Inputs.Add(directory);

                return;
            }

            // The directory did not contain a valid target, fail!
            var noInputs = string.Format(
                CultureInfo.CurrentCulture,
                    LocalizedResourceManager.GetString(
                    "Error_UnableToLocateRestoreTarget"),
                    directory);

            throw new InvalidOperationException(noInputs);
        }

        /// <summary>
        /// True if the filename is a packages.config file
        /// </summary>
        private static bool IsPackagesConfig(string projectFileName)
        {
            return string.Equals(projectFileName, Constants.PackageReferenceFile)
                || (projectFileName.StartsWith("packages.", StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    Path.GetExtension(projectFileName),
                    Path.GetExtension(Constants.PackageReferenceFile), StringComparison.OrdinalIgnoreCase));
        }

        private string GetPackagesFolder(string solutionDirectory)
        {
            if (!string.IsNullOrEmpty(PackagesDirectory))
            {
                return PackagesDirectory;
            }

            var repositoryPath = SettingsUtility.GetRepositoryPath(Settings);
            if (!string.IsNullOrEmpty(repositoryPath))
            {
                return repositoryPath;
            }

            if (!string.IsNullOrEmpty(SolutionDirectory))
            {
                return Path.Combine(SolutionDirectory, CommandLineConstants.PackagesDirectoryName);
            }

            if (solutionDirectory != null)
            {
                return Path.Combine(
                    solutionDirectory,
                    CommandLineConstants.PackagesDirectoryName);
            }

            throw new InvalidOperationException(
                LocalizedResourceManager.GetString("RestoreCommandCannotDeterminePackagesFolder"));
        }

        private static string ConstructPackagesConfigFromProjectName(string projectName)
        {
            // we look for packages.<project name>.config file
            // but we don't want any space in the file name, so convert it to underscore.
            return "packages." + projectName.Replace(' ', '_') + ".config";
        }

        // returns the package reference file associated with the project
        private string GetPackageReferenceFile(string projectFile)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            var pathWithProjectName = Path.Combine(
                Path.GetDirectoryName(projectFile),
                ConstructPackagesConfigFromProjectName(projectName));
            if (File.Exists(pathWithProjectName))
            {
                return pathWithProjectName;
            }

            return Path.Combine(
                Path.GetDirectoryName(projectFile),
                Constants.PackageReferenceFile);
        }

        private void ProcessSolutionFile(string solutionFileFullPath, PackageRestoreInputs restoreInputs)
        {
            var solutionDirectory = Path.GetDirectoryName(solutionFileFullPath);

            restoreInputs.SolutionFiles.Add(solutionFileFullPath);

            // restore packages for the solution
            var solutionLevelPackagesConfig = Path.Combine(
                solutionDirectory,
                NuGetConstants.NuGetSolutionSettingsFolder,
                Constants.PackageReferenceFile);

            if (File.Exists(solutionLevelPackagesConfig))
            {
                restoreInputs.PackagesConfigFiles.Add(
                    new SolutionInput(solutionFileFullPath, solutionLevelPackagesConfig));
            }

            var projectFiles = MsBuildUtility.GetAllProjectFileNames(solutionFileFullPath, _msbuildDirectory);
            foreach (var projectFile in projectFiles)
            {
                if (!File.Exists(projectFile))
                {
                    continue;
                }

                // packages.config
                var packagesConfigFilePath = GetPackageReferenceFile(projectFile);

                // project.json
                var dir = Path.GetDirectoryName(projectFile);
                var projectName = Path.GetFileNameWithoutExtension(projectFile);
                var projectJsonPath = ProjectJsonPathUtilities.GetProjectConfigPath(dir, projectName);

                // project.json overrides packages.config
                if (File.Exists(projectJsonPath))
                {
                    // project.json inputs are resolved again against the p2p file
                    // and are matched with the solution there

                    // For known msbuild project types use the project
                    if (MsBuildUtility.IsMsBuildBasedProject(projectFile))
                    {
                        restoreInputs.RestoreV3Context.Inputs.Add(projectFile);
                    }
                    else
                    {
                        // For unknown types restore the project.json file without p2ps
                        restoreInputs.RestoreV3Context.Inputs.Add(projectJsonPath);
                    }
                }
                else if (File.Exists(packagesConfigFilePath))
                {
                    restoreInputs.PackagesConfigFiles.Add(
                        new SolutionInput(solutionFileFullPath, packagesConfigFilePath));
                }
            }
        }

        private class PackageRestoreInputs
        {
            public PackageRestoreInputs()
            {
                ProjectReferenceLookup = new MSBuildProjectReferenceProvider(Enumerable.Empty<string>());
            }

            public List<SolutionInput> PackagesConfigFiles { get; } = new List<SolutionInput>();

            public MSBuildProjectReferenceProvider ProjectReferenceLookup { get; set; }

            public RestoreArgs RestoreV3Context { get; set; } = new RestoreArgs();

            public HashSet<string> SolutionFiles { get; } = new HashSet<string>();
        }

        private class SolutionInput
        {
            public string SolutionFile { get; }

            public string Input { get; }

            public SolutionInput(string solutionFile, string input)
            {
                SolutionFile = solutionFile;
                Input = input;
            }

            /// <summary>
            /// Root of the solution OR the input if the solution is null.
            /// </summary>
            public string RootDirectory
            {
                get
                {
                    if (!string.IsNullOrEmpty(SolutionFile))
                    {
                        return Path.GetDirectoryName(SolutionFile);
                    }

                    return Path.GetDirectoryName(Input);
                }
            }
        }
    }
}
