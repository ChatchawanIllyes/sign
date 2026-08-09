// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE.txt file in the project root for more information.

using System.Reflection;
using System.Text;
using Microsoft.Build.Tasks.Deployment.ManifestUtilities;
using Microsoft.Extensions.Logging;
using Moq;
using Sign.TestInfrastructure;

namespace Sign.Core.Test
{
    public sealed class ClickOnceFileGraphResolverTests : IDisposable
    {
        private const string ApplicationDirectory = @"Application Files\App_1_0_0_0";
        private const string ApplicationManifestFileName = "App.exe.manifest";
        private const string ClrPlatformAssemblyName = "Microsoft.Windows.CommonLanguageRuntime";
        private const string DependencyManifestFileName = "dependency.manifest";
        private const string DeploySuffix = ".deploy";
        private const string DeploymentManifestFileName = "App.application";
        private const string FileContents = "payload";
        private const string LauncherFileName = "Launcher.exe";
        private const string ManifestVersion = "1.0.0.0";
        private const string MissingTargetPathMessage = "without a target path";
        private const string OptionalPayloadFileName = "optional.txt";
        private const string PayloadFileName = "payload.dll";
        private const string ProcessorArchitecture = "msil";
        private const string SharedPayloadFileName = "shared.dll";
        private const string SetupFileName = "setup.exe";
        private const string VstoManifestFileName = "App.vsto";
        private const string WarningMessageName = "GenerateManifest.ResolveFailedInReadWriteMode";
        private const string WarningOneTargetPath = "warning-one.txt";
        private const string WarningTwoTargetPath = "warning-two.txt";

        private readonly DirectoryService _directoryService;
        private readonly ClickOnceApplicationManifestFileGraphResolver _applicationResolver;
        private readonly ClickOnceDeployManifestFileGraphResolver _deploymentResolver;
        private readonly ClickOncePayloadFileResolver _payloadResolver;

        public ClickOnceFileGraphResolverTests()
        {
            _directoryService = new(Mock.Of<ILogger<IDirectoryService>>());

            ClickOnceManifestReader manifestReader = new();
            _payloadResolver = new ClickOncePayloadFileResolver();

            _applicationResolver = new ClickOnceApplicationManifestFileGraphResolver(
                manifestReader,
                _payloadResolver);
            _deploymentResolver = new ClickOnceDeployManifestFileGraphResolver(
                manifestReader,
                _payloadResolver);
        }

        public void Dispose()
        {
            _directoryService.Dispose();
        }

        [Fact]
        public void DeploymentResolver_WhenMultipleVersionsAndManifestsExist_UsesReferencedApplicationManifest()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string CurrentApplicationDirectory = @"Application Files\App_2_0_0_0";
            const string CurrentPayloadFileName = "current.dll";
            const string OldPayloadFileName = "old.dll";

            FileInfo currentPayload = CreateFile(
                root,
                $@"{CurrentApplicationDirectory}\{CurrentPayloadFileName}");
            ApplicationManifest currentApplication = CreateApplicationManifest();
            AddFileReference(currentApplication, CurrentPayloadFileName);
            FileInfo currentManifest = WriteManifest(
                root,
                $@"{CurrentApplicationDirectory}\{ApplicationManifestFileName}",
                currentApplication);

            ApplicationManifest oldApplication = CreateApplicationManifest();
            AddFileReference(oldApplication, OldPayloadFileName);
            CreateFile(root, $@"{ApplicationDirectory}\{OldPayloadFileName}");
            WriteManifest(
                root,
                $@"{ApplicationDirectory}\{ApplicationManifestFileName}",
                oldApplication);
            CreateFusionManifest(
                root,
                $@"{CurrentApplicationDirectory}\{DependencyManifestFileName}");

            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                Path.GetRelativePath(root.FullName, currentManifest.FullName));

            ClickOnceFileGraph graph = _deploymentResolver.Resolve(deploymentManifest);

            Assert.Equal(currentManifest.FullName, graph.ApplicationManifest.Source.FullName);
            Assert.False(graph.DeployManifest!.ReadOnly);
            Assert.False(graph.ApplicationManifestModel.ReadOnly);
            Assert.Equal(
                currentManifest.FullName,
                graph.DeployManifest.EntryPoint!.ResolvedPath);
            ClickOnceFileGraphEntry payload = Assert.Single(graph.Payloads);
            Assert.Equal(currentPayload.FullName, payload.Source.FullName);
            Assert.Equal(CurrentPayloadFileName, payload.TargetPath);
        }

        [Fact]
        public void DeploymentResolver_WhenPayloadExistsInBothDirectories_PrefersApplicationDirectory()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string PayloadTargetPath = SharedPayloadFileName;

            FileInfo expectedPayload = CreateFile(root, $@"{ApplicationDirectory}\{PayloadTargetPath}");
            CreateFile(root, PayloadTargetPath);

            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadTargetPath);
            FileInfo applicationManifest = WriteManifest(
                root,
                $@"{ApplicationDirectory}\{ApplicationManifestFileName}",
                application);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                Path.GetRelativePath(root.FullName, applicationManifest.FullName));

            ClickOnceFileGraph graph = _deploymentResolver.Resolve(deploymentManifest);

            Assert.Equal(expectedPayload.FullName, Assert.Single(graph.Payloads).Source.FullName);
        }

        [Fact]
        public void DeploymentResolver_WhenPayloadIsMissingFromApplicationDirectory_UsesDeploymentDirectory()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string PayloadTargetPath = SharedPayloadFileName;

            FileInfo expectedPayload = CreateFile(root, PayloadTargetPath);
            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadTargetPath);
            FileInfo applicationManifest = WriteManifest(
                root,
                $@"{ApplicationDirectory}\{ApplicationManifestFileName}",
                application);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                Path.GetRelativePath(root.FullName, applicationManifest.FullName));

            ClickOnceFileGraph graph = _deploymentResolver.Resolve(deploymentManifest);

            Assert.Equal(expectedPayload.FullName, Assert.Single(graph.Payloads).Source.FullName);
            ClickOnceManifestDiagnostic diagnostic = Assert.Single(graph.Diagnostics);
            Assert.Equal(OutputMessageType.Error, diagnostic.Type);
            Assert.Contains(PayloadTargetPath, diagnostic.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void DeploymentResolver_WhenFileExtensionsAreMapped_RecordsMappingAddedSuffix()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string PayloadTargetPath = PayloadFileName;

            FileInfo expectedPayload = CreateFile(root, $@"{ApplicationDirectory}\{PayloadTargetPath}{DeploySuffix}");
            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadTargetPath);
            FileInfo applicationManifest = WriteManifest(
                root,
                $@"{ApplicationDirectory}\{ApplicationManifestFileName}",
                application);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                Path.GetRelativePath(root.FullName, applicationManifest.FullName),
                mapFileExtensions: true);

            ClickOnceFileGraph graph = _deploymentResolver.Resolve(deploymentManifest);

            ClickOnceFileGraphEntry payload = Assert.Single(graph.Payloads);
            Assert.Equal(expectedPayload.FullName, payload.Source.FullName);
            Assert.Equal(PayloadTargetPath, payload.TargetPath);
            Assert.Equal(DeploySuffix, payload.MappingAddedSuffix);
            Assert.Equal($"{PayloadTargetPath}{DeploySuffix}", payload.Source.Name);
            Assert.Equal(FileContents, File.ReadAllText(expectedPayload.FullName));
        }

        [Fact]
        public void DeploymentResolver_WhenMappedPayloadExistsInBothDirectories_PrefersApplicationDirectory()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string PayloadTargetPath = SharedPayloadFileName;

            FileInfo expectedPayload = CreateFile(
                root,
                $@"{ApplicationDirectory}\{PayloadTargetPath}{DeploySuffix}");
            CreateFile(root, $"{PayloadTargetPath}{DeploySuffix}");
            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadTargetPath);
            FileInfo applicationManifest = WriteManifest(
                root,
                $@"{ApplicationDirectory}\{ApplicationManifestFileName}",
                application);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                Path.GetRelativePath(root.FullName, applicationManifest.FullName),
                mapFileExtensions: true);

            ClickOnceFileGraph graph = _deploymentResolver.Resolve(deploymentManifest);

            Assert.Equal(expectedPayload.FullName, Assert.Single(graph.Payloads).Source.FullName);
        }

        [Fact]
        public void DeploymentResolver_WhenFileExtensionsAreMapped_DoesNotUseExactTarget()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string PayloadTargetPath = PayloadFileName;

            CreateFile(root, $@"{ApplicationDirectory}\{PayloadTargetPath}");
            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadTargetPath);
            FileInfo applicationManifest = WriteManifest(
                root,
                $@"{ApplicationDirectory}\{ApplicationManifestFileName}",
                application);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                Path.GetRelativePath(root.FullName, applicationManifest.FullName),
                mapFileExtensions: true);

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => _deploymentResolver.Resolve(deploymentManifest));

            Assert.Contains(PayloadTargetPath, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void DeploymentResolver_WhenFileExtensionsAreNotMapped_DoesNotUseMappedTarget()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string PayloadTargetPath = PayloadFileName;

            CreateFile(root, $@"{ApplicationDirectory}\{PayloadTargetPath}{DeploySuffix}");
            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadTargetPath);
            FileInfo applicationManifest = WriteManifest(
                root,
                $@"{ApplicationDirectory}\{ApplicationManifestFileName}",
                application);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                Path.GetRelativePath(root.FullName, applicationManifest.FullName));

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => _deploymentResolver.Resolve(deploymentManifest));

            Assert.Contains(PayloadTargetPath, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void DeploymentResolver_WhenMappedTargetAlreadyEndsInDeploy_AddsExactlyOneSuffix()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string PayloadTargetPath = "payload.deploy";

            FileInfo expectedPayload = CreateFile(
                root,
                $@"{ApplicationDirectory}\{PayloadTargetPath}{DeploySuffix}");
            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadTargetPath);
            FileInfo applicationManifest = WriteManifest(
                root,
                $@"{ApplicationDirectory}\{ApplicationManifestFileName}",
                application);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                Path.GetRelativePath(root.FullName, applicationManifest.FullName),
                mapFileExtensions: true);

            ClickOnceFileGraph graph = _deploymentResolver.Resolve(deploymentManifest);

            ClickOnceFileGraphEntry payload = Assert.Single(graph.Payloads);
            Assert.Equal(expectedPayload.FullName, payload.Source.FullName);
            Assert.Equal(DeploySuffix, payload.MappingAddedSuffix);
        }

        [Fact]
        public void DeploymentResolver_ClassifiesReferencedAndAdjacentExecutables()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string NestedLauncherPath = $@"nested\{LauncherFileName}";
            const string NestedSetupPath = @"nested\setup.exe";

            FileInfo referencedLauncher = CreateFile(root, $@"{ApplicationDirectory}\{LauncherFileName}");
            FileInfo adjacentLauncher = CreateFile(root, LauncherFileName);
            FileInfo setup = CreateFile(root, SetupFileName);
            CreateFile(root, NestedLauncherPath);
            CreateFile(root, NestedSetupPath);

            ApplicationManifest application = CreateApplicationManifest();
            AddAssemblyReference(application, LauncherFileName, isEntryPoint: true);
            FileInfo applicationManifest = WriteManifest(
                root,
                $@"{ApplicationDirectory}\{ApplicationManifestFileName}",
                application);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                Path.GetRelativePath(root.FullName, applicationManifest.FullName));

            ClickOnceFileGraph graph = _deploymentResolver.Resolve(deploymentManifest);

            ClickOnceFileGraphEntry payload = Assert.Single(graph.Payloads);
            Assert.Equal(referencedLauncher.FullName, payload.Source.FullName);
            Assert.Equal(ClickOnceFileGraphEntryKind.Payload, payload.Kind);

            Assert.Contains(
                graph.AdjacentExecutables,
                entry =>
                    entry.Source.FullName == setup.FullName &&
                    entry.Kind == ClickOnceFileGraphEntryKind.Setup);
            Assert.Contains(
                graph.AdjacentExecutables,
                entry =>
                    entry.Source.FullName == adjacentLauncher.FullName &&
                    entry.Kind == ClickOnceFileGraphEntryKind.Launcher);
            Assert.Equal(expected: 2, actual: graph.AdjacentExecutables.Count);
        }

        [Fact]
        public void DeploymentResolver_WhenRootLauncherIsReferenced_DoesNotClassifyItAsAdjacent()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            FileInfo launcher = CreateFile(root, LauncherFileName);
            ApplicationManifest application = CreateApplicationManifest();
            AddAssemblyReference(application, LauncherFileName, isEntryPoint: true);
            FileInfo applicationManifest = WriteManifest(
                root,
                ApplicationManifestFileName,
                application);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                applicationManifest.Name);

            ClickOnceFileGraph graph = _deploymentResolver.Resolve(deploymentManifest);

            Assert.Equal(launcher.FullName, Assert.Single(graph.Payloads).Source.FullName);
            Assert.Empty(graph.AdjacentExecutables);
        }

        [Theory]
        [InlineData(DeploymentManifestFileName)]
        [InlineData(VstoManifestFileName)]
        public void DeploymentResolver_WhenDeploymentManifestIsWrongType_Throws(string fileName)
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            FileInfo deploymentManifest = WriteManifest(
                root,
                fileName,
                CreateApplicationManifest());

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => _deploymentResolver.Resolve(deploymentManifest));

            Assert.Contains(deploymentManifest.FullName, exception.Message, StringComparison.Ordinal);
            const string ExpectedMessage = "not a ClickOnce deployment manifest";

            Assert.Contains(ExpectedMessage, exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(DeploymentManifestFileName)]
        [InlineData(VstoManifestFileName)]
        public void DeploymentResolver_WhenDeploymentManifestIsMalformed_Throws(string fileName)
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            FileInfo deploymentManifest = CreateFile(root, fileName);

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => _deploymentResolver.Resolve(deploymentManifest));

            Assert.Contains(deploymentManifest.FullName, exception.Message, StringComparison.Ordinal);
            const string ExpectedMessage = "Failed to read ClickOnce deployment manifest";

            Assert.Contains(ExpectedMessage, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void DeploymentResolver_WhenReferencedApplicationManifestIsWrongType_Throws()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;

            FileInfo wrongTypeManifest = CreateFusionManifest(root, ApplicationManifestFileName);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                wrongTypeManifest.Name);

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => _deploymentResolver.Resolve(deploymentManifest));

            Assert.Contains(wrongTypeManifest.FullName, exception.Message, StringComparison.Ordinal);
            const string ExpectedMessage = "not a ClickOnce application manifest";

            Assert.Contains(ExpectedMessage, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void DeploymentResolver_WhenReferencedApplicationManifestIsMalformed_Throws()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;

            FileInfo malformedManifest = CreateFile(root, ApplicationManifestFileName);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                malformedManifest.Name);

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => _deploymentResolver.Resolve(deploymentManifest));

            Assert.Contains(malformedManifest.FullName, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void DeploymentResolver_WhenReferencedApplicationManifestIsMissing_ThrowsWithExpectedPath()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string MissingManifest = ApplicationDirectory + @"\" + ApplicationManifestFileName;

            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                MissingManifest);

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => _deploymentResolver.Resolve(deploymentManifest));

            Assert.Contains(
                Path.Combine(root.FullName, MissingManifest),
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                exception.Diagnostics,
                diagnostic => diagnostic.Type == OutputMessageType.Error);
        }

        [Fact]
        public void DeploymentResolver_WhenEntryPointIsUnresolved_DoesNotUseTargetPathAsFallback()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;

            FileInfo deploymentManifestFile = CreateFile(root, DeploymentManifestFileName);
            FileInfo applicationManifestFile = CreateFile(root, ApplicationManifestFileName);
            AssemblyReference entryPoint = new()
            {
                TargetPath = applicationManifestFile.Name
            };
            DeployManifest diagnosticSource = new();
            Mock<IDeployManifest> deploymentManifest = new();

            deploymentManifest.SetupProperty(manifest => manifest.ReadOnly);
            deploymentManifest.SetupGet(manifest => manifest.EntryPoint).Returns(entryPoint);
            deploymentManifest.SetupGet(manifest => manifest.OutputMessages).Returns(diagnosticSource.OutputMessages);
            deploymentManifest
                .Setup(manifest => manifest.ResolveFiles(
                    It.Is<string[]>(paths =>
                        paths.SequenceEqual(new[] { root.FullName }, StringComparer.OrdinalIgnoreCase))))
                .Verifiable();

            IDeployManifest? resolvedDeploymentManifest = deploymentManifest.Object;
            Mock<IClickOnceManifestReader> manifestReader = new();

            manifestReader
                .Setup(reader => reader.TryReadDeployManifest(
                    stream: It.IsAny<Stream>(),
                    preserveStream: false,
                    manifest: out resolvedDeploymentManifest))
                .Returns(value: true);

            ClickOnceDeployManifestFileGraphResolver resolver = new(
                manifestReader.Object,
                _payloadResolver);

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => resolver.Resolve(deploymentManifestFile));

            Assert.Contains(applicationManifestFile.FullName, exception.Message, StringComparison.Ordinal);
            deploymentManifest.Verify();
        }

        [Fact]
        public void DeploymentResolver_WhenRequiredPayloadIsMissing_ThrowsWithTargetPath()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string PayloadTargetPath = @"missing\payload.dll";

            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadTargetPath);
            FileInfo applicationManifest = WriteManifest(
                root,
                ApplicationManifestFileName,
                application);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                applicationManifest.Name);

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => _deploymentResolver.Resolve(deploymentManifest));

            Assert.Contains(PayloadTargetPath, exception.Message, StringComparison.Ordinal);
            Assert.Contains(
                exception.Diagnostics,
                diagnostic =>
                    diagnostic.Type == OutputMessageType.Error &&
                    diagnostic.Text.Contains(PayloadTargetPath, StringComparison.Ordinal));
        }

        [Fact]
        public void DeploymentResolver_WhenApplicationResolutionProducesWarning_PreservesDiagnosticAndSucceeds()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;

            FileInfo deploymentManifestFile = CreateFile(root, DeploymentManifestFileName);
            FileInfo applicationManifestFile = CreateFile(root, ApplicationManifestFileName);
            FileInfo payload = CreateFile(root, PayloadFileName);

            DeployManifest deploymentDiagnosticSource = new();
            AssemblyReference deploymentEntryPoint = new()
            {
                ResolvedPath = applicationManifestFile.FullName,
                TargetPath = applicationManifestFile.Name
            };
            Mock<IDeployManifest> deploymentManifest = new();

            deploymentManifest.SetupProperty(manifest => manifest.ReadOnly);
            deploymentManifest.SetupGet(manifest => manifest.EntryPoint).Returns(deploymentEntryPoint);
            deploymentManifest.SetupGet(manifest => manifest.MapFileExtensions).Returns(value: false);
            deploymentManifest
                .SetupGet(manifest => manifest.OutputMessages)
                .Returns(deploymentDiagnosticSource.OutputMessages);

            ApplicationManifest applicationModel = CreateApplicationManifest();
            AddFileReference(applicationModel, payload.Name);
            Mock<IApplicationManifest> applicationManifest = new();

            applicationManifest.SetupProperty(manifest => manifest.ReadOnly);
            applicationManifest
                .SetupGet(manifest => manifest.AssemblyReferences)
                .Returns(applicationModel.AssemblyReferences);
            applicationManifest
                .SetupGet(manifest => manifest.EntryPoint)
                .Returns(applicationModel.EntryPoint);
            applicationManifest
                .SetupGet(manifest => manifest.FileReferences)
                .Returns(applicationModel.FileReferences);
            applicationManifest
                .SetupGet(manifest => manifest.OutputMessages)
                .Returns(applicationModel.OutputMessages);
            applicationManifest
                .Setup(manifest => manifest.ResolveFiles(It.IsAny<string[]>()))
                .Callback(() =>
                {
                    AddWarning(applicationModel.OutputMessages, WarningOneTargetPath);
                    AddWarning(applicationModel.OutputMessages, WarningTwoTargetPath);
                });

            IDeployManifest? resolvedDeploymentManifest = deploymentManifest.Object;
            IApplicationManifest? resolvedApplicationManifest = applicationManifest.Object;
            Mock<IClickOnceManifestReader> manifestReader = new();

            manifestReader
                .Setup(reader => reader.TryReadDeployManifest(
                    stream: It.IsAny<Stream>(),
                    preserveStream: false,
                    manifest: out resolvedDeploymentManifest))
                .Returns(value: true);
            manifestReader
                .Setup(reader => reader.TryReadApplicationManifest(
                    stream: It.IsAny<Stream>(),
                    preserveStream: false,
                    manifest: out resolvedApplicationManifest))
                .Returns(value: true);

            ClickOnceDeployManifestFileGraphResolver resolver = new(
                manifestReader.Object,
                _payloadResolver);

            ClickOnceFileGraph graph = resolver.Resolve(deploymentManifestFile);

            Assert.Equal(payload.FullName, Assert.Single(graph.Payloads).Source.FullName);
            Assert.Collection(
                graph.Diagnostics,
                diagnostic =>
                {
                    Assert.Equal(OutputMessageType.Warning, diagnostic.Type);
                    Assert.Contains(WarningOneTargetPath, diagnostic.Text, StringComparison.Ordinal);
                },
                diagnostic =>
                {
                    Assert.Equal(OutputMessageType.Warning, diagnostic.Type);
                    Assert.Contains(WarningTwoTargetPath, diagnostic.Text, StringComparison.Ordinal);
                });
        }

        [Fact]
        public void ApplicationResolver_WhenTargetExists_UsesTargetWithoutMappingSuffix()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string PayloadTargetPath = @"content\payload.dll";

            FileInfo expectedPayload = CreateFile(root, PayloadTargetPath);
            CreateFile(root, $"{PayloadTargetPath}{DeploySuffix}");
            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadTargetPath);
            FileInfo applicationManifest = WriteManifest(
                root,
                ApplicationManifestFileName,
                application);

            Assert.True(_applicationResolver.TryResolve(applicationManifest, out ClickOnceFileGraph? graph));

            Assert.NotNull(graph);
            ClickOnceFileGraphEntry payload = Assert.Single(graph.Payloads);
            Assert.Equal(expectedPayload.FullName, payload.Source.FullName);
            Assert.Null(payload.MappingAddedSuffix);
        }

        [Fact]
        public void ApplicationResolver_WhenOnlyMappedTargetExists_RecordsOneMappingAddedSuffix()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string PayloadTargetPath = "payload.deploy";

            FileInfo expectedPayload = CreateFile(root, $"{PayloadTargetPath}{DeploySuffix}");
            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadTargetPath);
            FileInfo applicationManifest = WriteManifest(
                root,
                ApplicationManifestFileName,
                application);

            Assert.True(_applicationResolver.TryResolve(applicationManifest, out ClickOnceFileGraph? graph));

            Assert.NotNull(graph);
            ClickOnceFileGraphEntry payload = Assert.Single(graph.Payloads);
            Assert.Equal(expectedPayload.FullName, payload.Source.FullName);
            Assert.Equal(PayloadTargetPath, payload.TargetPath);
            Assert.Equal(DeploySuffix, payload.MappingAddedSuffix);
        }

        [Fact]
        public void ApplicationResolver_WhenOnlyDoubleMappedTargetExists_DoesNotInventAdditionalSuffix()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string DoubleMappedPayloadPath = $"{PayloadFileName}{DeploySuffix}{DeploySuffix}";

            CreateFile(root, DoubleMappedPayloadPath);
            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadFileName);
            FileInfo applicationManifest = WriteManifest(
                root,
                ApplicationManifestFileName,
                application);

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => _applicationResolver.TryResolve(applicationManifest, out _));

            Assert.Contains(PayloadFileName, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ApplicationResolver_DoesNotUseDeploymentDirectoryFallback()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string PayloadTargetPath = "shared.dll";

            CreateFile(root, PayloadTargetPath);
            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadTargetPath);
            FileInfo applicationManifest = WriteManifest(
                root,
                $@"{ApplicationDirectory}\{ApplicationManifestFileName}",
                application);

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => _applicationResolver.TryResolve(applicationManifest, out _));

            Assert.Contains(PayloadTargetPath, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ApplicationResolver_WhenManifestIsNotClickOnce_ReturnsFalse()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            FileInfo fusionManifest = CreateFusionManifest(
                temporaryDirectory.Directory,
                DependencyManifestFileName);

            bool result = _applicationResolver.TryResolve(
                fusionManifest,
                out ClickOnceFileGraph? graph);

            Assert.False(result);
            Assert.Null(graph);
        }

        [Fact]
        public void ApplicationResolver_WhenOptionalPayloadIsMissing_CapturesDiagnosticAndContinues()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string PayloadTargetPath = OptionalPayloadFileName;

            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadTargetPath, isOptional: true);
            FileInfo applicationManifest = WriteManifest(
                root,
                ApplicationManifestFileName,
                application);

            Assert.True(_applicationResolver.TryResolve(applicationManifest, out ClickOnceFileGraph? graph));

            Assert.NotNull(graph);
            Assert.Empty(graph.Payloads);
            ClickOnceManifestDiagnostic diagnostic = Assert.Single(graph.Diagnostics);
            Assert.Equal(OutputMessageType.Error, diagnostic.Type);
            Assert.Contains(PayloadTargetPath, diagnostic.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void DeploymentResolver_WhenOptionalPayloadHasNoTargetPath_OmitsIt()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;

            ApplicationManifest application = CreateApplicationManifest();
            application.FileReferences.Add(new FileReference() { IsOptional = true });
            FileInfo applicationManifest = WriteManifest(
                root,
                ApplicationManifestFileName,
                application);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                applicationManifest.Name);

            ClickOnceFileGraph graph = _deploymentResolver.Resolve(deploymentManifest);

            Assert.Empty(graph.Payloads);
        }

        [Fact]
        public void DeploymentResolver_WhenOptionalPayloadIsMissing_CapturesDiagnosticAndContinues()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            const string PayloadTargetPath = OptionalPayloadFileName;

            ApplicationManifest application = CreateApplicationManifest();
            AddFileReference(application, PayloadTargetPath, isOptional: true);
            FileInfo applicationManifest = WriteManifest(
                root,
                ApplicationManifestFileName,
                application);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                applicationManifest.Name);

            ClickOnceFileGraph graph = _deploymentResolver.Resolve(deploymentManifest);

            Assert.Empty(graph.Payloads);
            ClickOnceManifestDiagnostic diagnostic = Assert.Single(graph.Diagnostics);
            Assert.Equal(OutputMessageType.Error, diagnostic.Type);
            Assert.Contains(PayloadTargetPath, diagnostic.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void DeploymentResolver_WhenRequiredPayloadHasNoTargetPath_Throws()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;

            ApplicationManifest application = CreateApplicationManifest();
            application.FileReferences.Add(new FileReference());
            FileInfo applicationManifest = WriteManifest(
                root,
                ApplicationManifestFileName,
                application);
            FileInfo deploymentManifest = WriteDeploymentManifest(
                root,
                DeploymentManifestFileName,
                applicationManifest.Name);

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => _deploymentResolver.Resolve(deploymentManifest));

            Assert.Contains(MissingTargetPathMessage, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void PayloadResolver_WhenOptionalReferenceHasNoTargetPath_OmitsIt()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            FileInfo applicationManifestFile = CreateFile(
                temporaryDirectory.Directory,
                ApplicationManifestFileName);
            ApplicationManifest applicationManifest = CreateApplicationManifest();
            applicationManifest.FileReferences.Add(new FileReference() { IsOptional = true });

            IReadOnlyList<ClickOnceFileGraphEntry> payloads = _payloadResolver.ResolveForExplicitApplication(
                applicationManifestFile,
                new ApplicationManifestAdapter(applicationManifest),
                new List<ClickOnceManifestDiagnostic>());

            Assert.Empty(payloads);
        }

        [Fact]
        public void PayloadResolver_IncludesOnlyPhysicalReferencesAndPreservesReferenceIdentity()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;
            FileInfo applicationManifestFile = CreateFile(root, ApplicationManifestFileName);
            ApplicationManifest applicationManifest = CreateApplicationManifest();

            const string AssemblyTargetPath = "library.dll";
            const string ContentTargetPath = "content.txt";
            const string EntryPointTargetPath = "entry.exe";
            const string PrerequisiteTargetPath = "prerequisite.dll";
            const string VirtualTargetPath = "virtual.dll";

            AssemblyReference assemblyReference = new() { TargetPath = AssemblyTargetPath };
            AssemblyReference prerequisiteReference = new()
            {
                IsPrerequisite = true,
                TargetPath = PrerequisiteTargetPath
            };
            AssemblyReference virtualReference = new()
            {
                AssemblyIdentity = new AssemblyIdentity(ClrPlatformAssemblyName, ManifestVersion),
                TargetPath = VirtualTargetPath
            };
            AssemblyReference entryPoint = new() { TargetPath = EntryPointTargetPath };
            FileReference fileReference = new() { TargetPath = ContentTargetPath };

            applicationManifest.AssemblyReferences.Add(assemblyReference);
            applicationManifest.AssemblyReferences.Add(prerequisiteReference);
            applicationManifest.AssemblyReferences.Add(virtualReference);
            applicationManifest.EntryPoint = entryPoint;
            applicationManifest.FileReferences.Add(fileReference);

            CreateFile(root, assemblyReference.TargetPath);
            CreateFile(root, prerequisiteReference.TargetPath);
            CreateFile(root, virtualReference.TargetPath);
            CreateFile(root, entryPoint.TargetPath);
            CreateFile(root, fileReference.TargetPath);

            IReadOnlyList<ClickOnceFileGraphEntry> payloads = _payloadResolver.ResolveForExplicitApplication(
                applicationManifestFile,
                new ApplicationManifestAdapter(applicationManifest),
                new List<ClickOnceManifestDiagnostic>());

            Assert.Equal(expected: 3, actual: payloads.Count);
            Assert.Contains(payloads, payload => ReferenceEquals(payload.ManifestReference, assemblyReference));
            Assert.Contains(payloads, payload => ReferenceEquals(payload.ManifestReference, entryPoint));
            Assert.Contains(payloads, payload => ReferenceEquals(payload.ManifestReference, fileReference));
            Assert.DoesNotContain(payloads, payload => ReferenceEquals(payload.ManifestReference, prerequisiteReference));
            Assert.DoesNotContain(payloads, payload => ReferenceEquals(payload.ManifestReference, virtualReference));
        }

        [Fact]
        public void PayloadResolver_WhenTargetPathIsInvalid_ThrowsLocalizedResolutionException()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            FileInfo applicationManifestFile = CreateFile(
                temporaryDirectory.Directory,
                ApplicationManifestFileName);
            ApplicationManifest applicationManifest = CreateApplicationManifest();
            const string InvalidTargetPath = "invalid\0path";

            applicationManifest.FileReferences.Add(new FileReference() { TargetPath = InvalidTargetPath });

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => _payloadResolver.ResolveForExplicitApplication(
                    applicationManifestFile,
                    new ApplicationManifestAdapter(applicationManifest),
                    new List<ClickOnceManifestDiagnostic>()));

            const string ExpectedMessage = "invalid target path";

            Assert.Contains(ExpectedMessage, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void PayloadResolver_WhenRequiredReferenceHasNoTargetPath_Throws()
        {
            using TemporaryDirectory temporaryDirectory = new(_directoryService);
            DirectoryInfo root = temporaryDirectory.Directory;

            FileInfo applicationManifestFile = CreateFile(root, ApplicationManifestFileName);
            ApplicationManifest applicationManifest = CreateApplicationManifest();
            applicationManifest.FileReferences.Add(new FileReference());

            ClickOnceFileGraphResolutionException exception = Assert.Throws<ClickOnceFileGraphResolutionException>(
                () => _payloadResolver.ResolveForExplicitApplication(
                    applicationManifestFile,
                    new ApplicationManifestAdapter(applicationManifest),
                    new List<ClickOnceManifestDiagnostic>()));

            Assert.Contains(MissingTargetPathMessage, exception.Message, StringComparison.Ordinal);
        }

        private static ApplicationManifest CreateApplicationManifest()
        {
            ApplicationManifest manifest = new();
            manifest.AssemblyIdentity.Name = "TestApplication";
            manifest.AssemblyIdentity.Version = ManifestVersion;
            manifest.AssemblyIdentity.ProcessorArchitecture = ProcessorArchitecture;

            return manifest;
        }

        private static void AddAssemblyReference(
            ApplicationManifest manifest,
            string targetPath,
            bool isEntryPoint = false,
            bool isOptional = false)
        {
            AssemblyReference reference = new()
            {
                IsOptional = isOptional,
                TargetPath = targetPath
            };

            manifest.AssemblyReferences.Add(reference);

            if (isEntryPoint)
            {
                manifest.EntryPoint = reference;
            }
        }

        private static void AddFileReference(
            ApplicationManifest manifest,
            string targetPath,
            bool isOptional = false)
        {
            manifest.FileReferences.Add(
                new FileReference()
                {
                    IsOptional = isOptional,
                    TargetPath = targetPath
                });
        }

        private static FileInfo WriteDeploymentManifest(
            DirectoryInfo root,
            string relativePath,
            string applicationManifestTargetPath,
            bool mapFileExtensions = false)
        {
            DeployManifest manifest = new()
            {
                MapFileExtensions = mapFileExtensions
            };
            manifest.AssemblyIdentity.Name = "TestDeployment";
            manifest.AssemblyIdentity.Version = ManifestVersion;
            manifest.AssemblyIdentity.ProcessorArchitecture = ProcessorArchitecture;

            AssemblyReference entryPoint = new(applicationManifestTargetPath);
            manifest.AssemblyReferences.Add(entryPoint);
            manifest.EntryPoint = entryPoint;

            return WriteManifest(root, relativePath, manifest);
        }

        private static FileInfo WriteManifest(
            DirectoryInfo root,
            string relativePath,
            Manifest manifest)
        {
            FileInfo file = new(Path.Combine(root.FullName, relativePath));
            file.Directory!.Create();

            using (FileStream stream = file.Create())
            {
                ManifestWriter.WriteManifest(manifest, stream);
            }

            file.Refresh();

            return file;
        }

        private static FileInfo CreateFile(
            DirectoryInfo root,
            string relativePath)
        {
            FileInfo file = new(Path.Combine(root.FullName, relativePath));
            file.Directory!.Create();
            File.WriteAllText(path: file.FullName, contents: FileContents);
            file.Refresh();

            return file;
        }

        private static FileInfo CreateFusionManifest(
            DirectoryInfo root,
            string relativePath)
        {
            const string Xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
                  <assemblyIdentity name="SideBySide" version="1.0.0.0" processorArchitecture="msil" type="win32" />
                </assembly>
                """;
            FileInfo file = new(Path.Combine(root.FullName, relativePath));
            file.Directory!.Create();
            File.WriteAllText(file.FullName, Xml, Encoding.UTF8);
            file.Refresh();

            return file;
        }

        private static void AddWarning(
            OutputMessageCollection outputMessages,
            string targetPath)
        {
            MethodInfo addWarning = typeof(OutputMessageCollection).GetMethod(
                name: "AddWarningMessage",
                bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic)!;

            addWarning.Invoke(
                obj: outputMessages,
                parameters: new object[]
                {
                    WarningMessageName,
                    new[] { targetPath }
                });
        }
    }
}
