// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE.txt file in the project root for more information.

using System.Globalization;
using Microsoft.Build.Tasks.Deployment.ManifestUtilities;

namespace Sign.Core
{
    internal sealed class ClickOnceDeployManifestFileGraphResolver
    {
        private const string LauncherFileName = "Launcher.exe";
        private const string SetupFileName = "setup.exe";

        private readonly IClickOnceManifestReader _manifestReader;
        private readonly ClickOncePayloadFileResolver _payloadResolver;

        internal ClickOnceDeployManifestFileGraphResolver(
            IClickOnceManifestReader manifestReader,
            ClickOncePayloadFileResolver payloadResolver)
        {
            ArgumentNullException.ThrowIfNull(manifestReader, nameof(manifestReader));
            ArgumentNullException.ThrowIfNull(payloadResolver, nameof(payloadResolver));

            _manifestReader = manifestReader;
            _payloadResolver = payloadResolver;
        }

        internal ClickOnceFileGraph Resolve(FileInfo deploymentManifestFile)
        {
            ArgumentNullException.ThrowIfNull(deploymentManifestFile, nameof(deploymentManifestFile));

            IDeployManifest deploymentManifest = ReadDeployManifest(deploymentManifestFile);
            deploymentManifest.ReadOnly = false;

            DirectoryInfo deploymentDirectory = deploymentManifestFile.Directory!;

            try
            {
                deploymentManifest.ResolveFiles(new[] { deploymentDirectory.FullName });
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                System.Xml.XmlException or
                ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                List<ClickOnceManifestDiagnostic> resolutionDiagnostics = GetDiagnostics(deploymentManifest);

                throw new ClickOnceFileGraphResolutionException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.ClickOnceDeploymentManifestResolveFailed,
                        deploymentManifestFile.FullName),
                    resolutionDiagnostics,
                    exception);
            }

            List<ClickOnceManifestDiagnostic> diagnostics = GetDiagnostics(deploymentManifest);

            AssemblyReference? entryPoint = deploymentManifest.EntryPoint;

            if (entryPoint is null || string.IsNullOrWhiteSpace(entryPoint.TargetPath))
            {
                throw new ClickOnceFileGraphResolutionException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.ClickOnceDeploymentManifestMissingEntryPoint,
                        deploymentManifestFile.FullName),
                    diagnostics);
            }

            FileInfo applicationManifestFile = GetApplicationManifestFile(
                deploymentManifestFile,
                entryPoint,
                diagnostics);
            IApplicationManifest applicationManifest = ReadApplicationManifest(
                deploymentManifestFile,
                applicationManifestFile,
                diagnostics);
            applicationManifest.ReadOnly = false;

            IReadOnlyList<ClickOnceFileGraphEntry> payloads = _payloadResolver.ResolveForDeployment(
                applicationManifestFile,
                applicationManifest,
                deploymentDirectory,
                deploymentManifest.MapFileExtensions,
                diagnostics);

            IReadOnlyList<ClickOnceFileGraphEntry> adjacentExecutables = ResolveAdjacentExecutables(
                deploymentDirectory,
                payloads);

            return new ClickOnceFileGraph(
                new ClickOnceFileGraphEntry(
                    deploymentManifestFile,
                    deploymentManifestFile.Name,
                    ClickOnceFileGraphEntryKind.DeploymentManifest),
                deploymentManifest,
                new ClickOnceFileGraphEntry(
                    applicationManifestFile,
                    entryPoint.TargetPath,
                    ClickOnceFileGraphEntryKind.ApplicationManifest,
                    entryPoint),
                applicationManifest,
                payloads,
                adjacentExecutables,
                diagnostics);
        }

        private IDeployManifest ReadDeployManifest(FileInfo deploymentManifestFile)
        {
            try
            {
                using FileStream stream = deploymentManifestFile.OpenRead();

                if (_manifestReader.TryReadDeployManifest(
                    stream,
                    preserveStream: false,
                    out IDeployManifest? deploymentManifest))
                {
                    return deploymentManifest;
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                System.Xml.XmlException)
            {
                throw new ClickOnceFileGraphResolutionException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.ClickOnceDeploymentManifestReadFailed,
                        deploymentManifestFile.FullName),
                    innerException: exception);
            }

            throw new ClickOnceFileGraphResolutionException(
                string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.ClickOnceDeploymentManifestWrongType,
                    deploymentManifestFile.FullName));
        }

        private IApplicationManifest ReadApplicationManifest(
            FileInfo deploymentManifestFile,
            FileInfo applicationManifestFile,
            IReadOnlyCollection<ClickOnceManifestDiagnostic> diagnostics)
        {
            try
            {
                using FileStream stream = applicationManifestFile.OpenRead();

                if (_manifestReader.TryReadApplicationManifest(
                    stream,
                    preserveStream: false,
                    out IApplicationManifest? applicationManifest))
                {
                    return applicationManifest;
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                System.Xml.XmlException)
            {
                throw new ClickOnceFileGraphResolutionException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.ClickOnceDeploymentManifestReferencedApplicationReadFailed,
                        applicationManifestFile.FullName,
                        deploymentManifestFile.FullName),
                    diagnostics,
                    exception);
            }

            throw new ClickOnceFileGraphResolutionException(
                string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.ClickOnceDeploymentManifestReferencedApplicationWrongType,
                    applicationManifestFile.FullName,
                    deploymentManifestFile.FullName),
                diagnostics);
        }

        private static FileInfo GetApplicationManifestFile(
            FileInfo deploymentManifestFile,
            AssemblyReference entryPoint,
            IReadOnlyCollection<ClickOnceManifestDiagnostic> diagnostics)
        {
            string? resolvedPath = entryPoint.ResolvedPath;

            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                string expectedPath = Path.Combine(
                    deploymentManifestFile.DirectoryName!,
                    entryPoint.TargetPath);

                throw new ClickOnceFileGraphResolutionException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.ClickOnceDeploymentManifestUnresolvedApplicationManifest,
                        deploymentManifestFile.FullName,
                        expectedPath),
                    diagnostics);
            }

            FileInfo applicationManifestFile = new(resolvedPath);

            if (!applicationManifestFile.Exists)
            {
                throw new ClickOnceFileGraphResolutionException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.ClickOnceDeploymentManifestApplicationManifestNotFound,
                        deploymentManifestFile.FullName,
                        applicationManifestFile.FullName),
                    diagnostics);
            }

            return applicationManifestFile;
        }

        private static IReadOnlyList<ClickOnceFileGraphEntry> ResolveAdjacentExecutables(
            DirectoryInfo deploymentDirectory,
            IReadOnlyList<ClickOnceFileGraphEntry> payloads)
        {
            HashSet<string> payloadPaths = payloads
                .Select(payload => Path.GetFullPath(payload.Source.FullName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<ClickOnceFileGraphEntry> adjacentExecutables = new();

            FileInfo? setup = deploymentDirectory
                .EnumerateFiles()
                .FirstOrDefault(file => file.Name.Equals(SetupFileName, StringComparison.OrdinalIgnoreCase));

            if (setup is not null)
            {
                adjacentExecutables.Add(
                    new ClickOnceFileGraphEntry(
                        setup,
                        setup.Name,
                        ClickOnceFileGraphEntryKind.Setup));
            }

            FileInfo? launcher = deploymentDirectory
                .EnumerateFiles()
                .FirstOrDefault(file => file.Name.Equals(LauncherFileName, StringComparison.OrdinalIgnoreCase));

            if (launcher is not null &&
                !payloadPaths.Contains(Path.GetFullPath(launcher.FullName)))
            {
                adjacentExecutables.Add(
                    new ClickOnceFileGraphEntry(
                        launcher,
                        launcher.Name,
                        ClickOnceFileGraphEntryKind.Launcher));
            }

            return adjacentExecutables;
        }

        private static List<ClickOnceManifestDiagnostic> GetDiagnostics(IClickOnceManifest manifest)
        {
            return manifest.OutputMessages
                .Cast<OutputMessage>()
                .Select(message => new ClickOnceManifestDiagnostic(message))
                .ToList();
        }
    }
}
