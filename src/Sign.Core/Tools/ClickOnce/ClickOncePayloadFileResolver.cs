// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE.txt file in the project root for more information.

using System.Globalization;
using Microsoft.Build.Tasks.Deployment.ManifestUtilities;

namespace Sign.Core
{
    internal sealed class ClickOncePayloadFileResolver
    {
        private const string ClrPlatformAssemblyName = "Microsoft.Windows.CommonLanguageRuntime";
        private const string DeploySuffix = ".deploy";

        internal IReadOnlyList<ClickOnceFileGraphEntry> ResolveForDeployment(
            FileInfo applicationManifestFile,
            IApplicationManifest applicationManifest,
            DirectoryInfo deploymentDirectory,
            bool mapFileExtensions,
            ICollection<ClickOnceManifestDiagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(applicationManifestFile, nameof(applicationManifestFile));
            ArgumentNullException.ThrowIfNull(applicationManifest, nameof(applicationManifest));
            ArgumentNullException.ThrowIfNull(deploymentDirectory, nameof(deploymentDirectory));
            ArgumentNullException.ThrowIfNull(diagnostics, nameof(diagnostics));

            DirectoryInfo applicationDirectory = applicationManifestFile.Directory!;
            DirectoryInfo[] searchDirectories = PathsEqual(applicationDirectory.FullName, deploymentDirectory.FullName)
                ? new[] { applicationDirectory }
                : new[] { applicationDirectory, deploymentDirectory };
            DirectoryInfo[][] resolutionSearchDirectories = searchDirectories.Length == 1
                ? new[] { searchDirectories }
                // Preserve diagnostics from the application-directory attempt before retrying with fallback.
                : new[] { new[] { applicationDirectory }, searchDirectories };

            return Resolve(
                applicationManifestFile,
                applicationManifest,
                searchDirectories,
                resolutionSearchDirectories,
                mapFileExtensions ? PayloadLookupKind.Mapped : PayloadLookupKind.Unmapped,
                diagnostics);
        }

        internal IReadOnlyList<ClickOnceFileGraphEntry> ResolveForExplicitApplication(
            FileInfo applicationManifestFile,
            IApplicationManifest applicationManifest,
            ICollection<ClickOnceManifestDiagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(applicationManifestFile, nameof(applicationManifestFile));
            ArgumentNullException.ThrowIfNull(applicationManifest, nameof(applicationManifest));
            ArgumentNullException.ThrowIfNull(diagnostics, nameof(diagnostics));

            return Resolve(
                applicationManifestFile,
                applicationManifest,
                new[] { applicationManifestFile.Directory! },
                new[] { new[] { applicationManifestFile.Directory! } },
                PayloadLookupKind.UnmappedThenMapped,
                diagnostics);
        }

        private static IReadOnlyList<ClickOnceFileGraphEntry> Resolve(
            FileInfo applicationManifestFile,
            IApplicationManifest applicationManifest,
            DirectoryInfo[] searchDirectories,
            DirectoryInfo[][] resolutionSearchDirectories,
            PayloadLookupKind lookupKind,
            ICollection<ClickOnceManifestDiagnostic> diagnostics)
        {
            List<BaseReference> references = GetPhysicalReferences(applicationManifest);

            foreach (BaseReference reference in references)
            {
                ValidateTargetPath(
                    applicationManifestFile,
                    reference.TargetPath,
                    searchDirectories,
                    diagnostics);
            }

            int diagnosticCount = 0;

            foreach (DirectoryInfo[] resolutionDirectories in resolutionSearchDirectories)
            {
                try
                {
                    applicationManifest.ResolveFiles(
                        resolutionDirectories.Select(directory => directory.FullName).ToArray());
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
                    AddDiagnostics(applicationManifest, diagnostics, ref diagnosticCount);

                    throw new ClickOnceFileGraphResolutionException(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.ClickOnceApplicationManifestResolveFailed,
                            applicationManifestFile.FullName),
                        diagnostics,
                        exception);
                }

                AddDiagnostics(applicationManifest, diagnostics, ref diagnosticCount);
            }

            List<ClickOnceFileGraphEntry> payloads = new(references.Count);

            foreach (BaseReference reference in references)
            {
                string? targetPath = reference.TargetPath;

                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    if (reference.IsOptional)
                    {
                        continue;
                    }

                    throw new ClickOnceFileGraphResolutionException(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.ClickOnceApplicationManifestMissingTargetPath,
                            applicationManifestFile.FullName),
                        diagnostics);
                }

                FileInfo? source;
                string? mappingAddedSuffix;

                try
                {
                    if (TryResolve(
                        targetPath,
                        searchDirectories,
                        lookupKind,
                        out source,
                        out mappingAddedSuffix))
                    {
                        reference.ResolvedPath = source.FullName;

                        payloads.Add(
                            new ClickOnceFileGraphEntry(
                                source,
                                targetPath,
                                ClickOnceFileGraphEntryKind.Payload,
                                reference,
                                mappingAddedSuffix));

                        continue;
                    }
                }
                catch (Exception exception) when (
                    exception is ArgumentException or
                    NotSupportedException or
                    PathTooLongException)
                {
                    throw new ClickOnceFileGraphResolutionException(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.ClickOnceApplicationManifestInvalidTargetPath,
                            applicationManifestFile.FullName,
                            targetPath),
                        diagnostics,
                        exception);
                }

                if (reference.IsOptional)
                {
                    continue;
                }

                throw new ClickOnceFileGraphResolutionException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.ClickOnceApplicationManifestRequiredFileNotFound,
                        applicationManifestFile.FullName,
                        targetPath),
                    diagnostics);
            }

            return payloads;
        }

        private static List<BaseReference> GetPhysicalReferences(IApplicationManifest applicationManifest)
        {
            List<BaseReference> references = applicationManifest.AssemblyReferences
                .Cast<AssemblyReference>()
                .Where(IsPhysical)
                .Cast<BaseReference>()
                .ToList();

            if (applicationManifest.EntryPoint is not null &&
                IsPhysical(applicationManifest.EntryPoint) &&
                !references.Any(reference => ReferenceEquals(reference, applicationManifest.EntryPoint)))
            {
                references.Add(applicationManifest.EntryPoint);
            }

            references.AddRange(applicationManifest.FileReferences.Cast<FileReference>());

            return references;
        }

        private static bool IsPhysical(AssemblyReference reference)
        {
            return !reference.IsPrerequisite &&
                !string.Equals(
                    reference.AssemblyIdentity?.Name,
                    ClrPlatformAssemblyName,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateTargetPath(
            FileInfo applicationManifestFile,
            string? targetPath,
            IEnumerable<DirectoryInfo> searchDirectories,
            ICollection<ClickOnceManifestDiagnostic> diagnostics)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return;
            }

            try
            {
                foreach (DirectoryInfo directory in searchDirectories)
                {
                    _ = Path.GetFullPath(Path.Combine(directory.FullName, targetPath));
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                throw new ClickOnceFileGraphResolutionException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.ClickOnceApplicationManifestInvalidTargetPath,
                        applicationManifestFile.FullName,
                        targetPath),
                    diagnostics,
                    exception);
            }
        }

        private static bool TryResolve(
            string targetPath,
            IEnumerable<DirectoryInfo> searchDirectories,
            PayloadLookupKind lookupKind,
            out FileInfo source,
            out string? mappingAddedSuffix)
        {
            foreach (DirectoryInfo directory in searchDirectories)
            {
                if (lookupKind is PayloadLookupKind.Unmapped or PayloadLookupKind.UnmappedThenMapped)
                {
                    FileInfo unmappedSource = new(Path.Combine(directory.FullName, targetPath));

                    if (unmappedSource.Exists)
                    {
                        source = unmappedSource;
                        mappingAddedSuffix = null;

                        return true;
                    }
                }

                if (lookupKind is PayloadLookupKind.Mapped or PayloadLookupKind.UnmappedThenMapped)
                {
                    FileInfo mappedSource = new(Path.Combine(directory.FullName, $"{targetPath}{DeploySuffix}"));

                    if (mappedSource.Exists)
                    {
                        source = mappedSource;
                        mappingAddedSuffix = DeploySuffix;

                        return true;
                    }
                }
            }

            source = null!;
            mappingAddedSuffix = null;

            return false;
        }

        private static void AddDiagnostics(
            IClickOnceManifest manifest,
            ICollection<ClickOnceManifestDiagnostic> diagnostics,
            ref int diagnosticCount)
        {
            OutputMessage[] messages = manifest.OutputMessages.Cast<OutputMessage>().ToArray();

            foreach (OutputMessage message in messages.Skip(diagnosticCount))
            {
                diagnostics.Add(new ClickOnceManifestDiagnostic(message));
            }

            diagnosticCount = messages.Length;
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private enum PayloadLookupKind
        {
            Unmapped,
            Mapped,
            UnmappedThenMapped
        }
    }
}
