/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Diagnostics;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "FileMoverFallbackTests")]
    [Trait("Category", "FileSystem")]
    public sealed class FileMoverFallbackTests : BaseTests, IDisposable
    {
        private readonly string _root;

        public FileMoverFallbackTests()
        {
            _root = Path.Join(Path.GetTempPath(), "listenarr_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        private static async Task RewriteJournalPathsAsAmbiguousWindowsAliasesAsync(
            string journalPath,
            params string[] pathProperties)
        {
            var envelope = System.Text.Json.Nodes.JsonNode.Parse(
                await File.ReadAllTextAsync(journalPath))!;
            var payloadBytes = Convert.FromBase64String(
                envelope["PayloadBase64"]!.GetValue<string>());
            var payload = System.Text.Json.Nodes.JsonNode.Parse(payloadBytes)!;
            foreach (var property in pathProperties)
            {
                var nativePath = payload[property]!.GetValue<string>();
                payload[property] = "//?/" + nativePath.Replace('\\', '/');
            }

            var rewrittenPayload = System.Text.Encoding.UTF8.GetBytes(
                payload.ToJsonString());
            envelope["PayloadBase64"] = Convert.ToBase64String(rewrittenPayload);
            envelope["Sha256"] = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(rewrittenPayload));
            File.SetAttributes(journalPath, FileAttributes.Normal);
            await File.WriteAllTextAsync(journalPath, envelope.ToJsonString());
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"Ignoring cleanup failure for '{_root}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"Ignoring cleanup failure for '{_root}': {ex.Message}");
            }
        }

        [Fact]
        public async Task MoveDirectoryAsync_UnownedEmptyDestination_IsRejectedWithoutMutation()
        {
            var source = Path.Join(_root, "sourceDir");
            var destination = Path.Join(_root, "destDir");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Join(source, "track1.mp3"), "dummy");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.Equal("dummy", await File.ReadAllTextAsync(
                Path.Join(source, "track1.mp3")));
            Assert.True(Directory.Exists(destination));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(_root),
                directory => Path.GetFileName(directory).Contains(
                    ".listenarr-empty-placeholder-",
                    StringComparison.Ordinal));
        }

        [Fact]
        public async Task MoveFileAsync_SamePath_IsNoOpAndPreservesFile()
        {
            var file = Path.Join(_root, "same.mp3");
            await File.WriteAllTextAsync(file, "content");
            var mover = new FileMover(new NullLogger<FileMover>());

            var ok = await mover.MoveFileAsync(file, Path.GetFullPath(file));

            Assert.True(ok);
            Assert.True(File.Exists(file));
            Assert.Equal("content", await File.ReadAllTextAsync(file));
        }

        [Fact]
        public async Task MoveFilePreservingPhysicalIdentity_AbsentDestination_PreservesExactGeneration()
        {
            var source = Path.Join(_root, "preserve-source.mp3");
            var destination = Path.Join(_root, "preserve-destination.mp3");
            await File.WriteAllTextAsync(source, "audio");
            string expectedIdentity;
            using (var sourceLease = PinnedAudiobookFileRegistrationLease.Open(source))
            {
                expectedIdentity = sourceLease.PhysicalObjectIdentity;
            }
            var mover = new FileMover(new NullLogger<FileMover>());

            var moved = await mover.MoveFilePreservingPhysicalIdentityAsync(
                source,
                destination,
                expectedIdentity);

            Assert.True(moved);
            Assert.False(File.Exists(source));
            using var published = PinnedAudiobookFileRegistrationLease.Open(destination);
            Assert.Equal(expectedIdentity, published.PhysicalObjectIdentity);
            Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        }

        [Fact]
        public async Task MoveFilePreservingPhysicalIdentity_SourceGenerationReplaced_FailsClosed()
        {
            var source = Path.Join(_root, "generation-source.mp3");
            var destination = Path.Join(_root, "generation-destination.mp3");
            await File.WriteAllTextAsync(source, "original");
            string expectedIdentity;
            using (var lease = PinnedAudiobookFileRegistrationLease.Open(source))
            {
                expectedIdentity = lease.PhysicalObjectIdentity;
            }

            File.Delete(source);
            await File.WriteAllTextAsync(source, "replacement");
            var mover = new FileMover(new NullLogger<FileMover>());

            var moved = await mover.MoveFilePreservingPhysicalIdentityAsync(
                source,
                destination,
                expectedIdentity);

            Assert.False(moved);
            Assert.Equal("replacement", await File.ReadAllTextAsync(source));
            Assert.False(File.Exists(destination));
        }

        [Fact]
        public async Task MoveFilePreservingPhysicalIdentity_SameContentDifferentDestinationGeneration_FailsWithoutRetiringSource()
        {
            var source = Path.Join(_root, "same-content-source.mp3");
            var destination = Path.Join(_root, "same-content-destination.mp3");
            await File.WriteAllTextAsync(source, "audio");
            await File.WriteAllTextAsync(destination, "audio");
            string expectedIdentity;
            string originalDestinationIdentity;
            using (var sourceLease = PinnedAudiobookFileRegistrationLease.Open(source))
            using (var destinationLease = PinnedAudiobookFileRegistrationLease.Open(destination))
            {
                expectedIdentity = sourceLease.PhysicalObjectIdentity;
                originalDestinationIdentity = destinationLease.PhysicalObjectIdentity;
            }
            Assert.NotEqual(expectedIdentity, originalDestinationIdentity);
            var mover = new FileMover(new NullLogger<FileMover>());

            var moved = await mover.MoveFilePreservingPhysicalIdentityAsync(
                source,
                destination,
                expectedIdentity);

            Assert.False(moved);
            Assert.True(File.Exists(source));
            using var retainedSource = PinnedAudiobookFileRegistrationLease.Open(source);
            Assert.Equal(expectedIdentity, retainedSource.PhysicalObjectIdentity);
            using var retainedDestination = PinnedAudiobookFileRegistrationLease.Open(destination);
            Assert.Equal(originalDestinationIdentity, retainedDestination.PhysicalObjectIdentity);
            Assert.Equal("audio", await File.ReadAllTextAsync(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        }

        [Fact]
        public async Task MoveFilePreservingPhysicalIdentity_NativeRenameUnavailable_DoesNotCopyDelete()
        {
            var source = Path.Join(_root, "native-required-source.mp3");
            var destination = Path.Join(_root, "native-required-destination.mp3");
            await File.WriteAllTextAsync(source, "audio");
            string expectedIdentity;
            using (var lease = PinnedAudiobookFileRegistrationLease.Open(source))
            {
                expectedIdentity = lease.PhysicalObjectIdentity;
            }
            var mover = new FileMover(new NullLogger<FileMover>())
            {
                DisableNativeFileRenameForTest = true
            };

            var moved = await mover.MoveFilePreservingPhysicalIdentityAsync(
                source,
                destination,
                expectedIdentity);

            Assert.False(moved);
            Assert.Equal("audio", await File.ReadAllTextAsync(source));
            Assert.False(File.Exists(destination));
        }

        [Fact]
        public async Task PerformActionOn_MoveToSamePath_IsNoOpAndPreservesFile()
        {
            var file = Path.Join(_root, "perform-same.mp3");
            await File.WriteAllTextAsync(file, "content");
            var mover = new FileMover(new NullLogger<FileMover>());

            var ok = await mover.PerformActionOn(FileAction.Move, file, file);

            Assert.True(ok);
            Assert.True(File.Exists(file));
            Assert.Equal("content", await File.ReadAllTextAsync(file));
        }

        [Fact]
        public async Task PrepareActionForRegistration_Move_PreservesSourceUntilRegisteredCleanup()
        {
            var source = Path.Join(_root, "prepared-move-source.mp3");
            var destination = Path.Join(_root, "prepared-move-destination.mp3");
            await File.WriteAllTextAsync(source, "audio");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            using var lease = await mover.PrepareActionForRegistrationAsync(
                FileAction.Move,
                source,
                destination);

            Assert.NotNull(lease);
            Assert.True(File.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(destination));
            Assert.True(lease.MatchesCurrentPublication());

            var completed = await mover.CompletePreparedMoveAsync(
                source,
                destination,
                lease);

            Assert.True(completed);
            Assert.False(File.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(destination));
            Assert.True(lease.MatchesCurrentPublication());
        }

        [Fact]
        public async Task PrepareActionForRegistration_HardlinkCrashBeforeClaim_RetryCompletesPreparedPublication()
        {
            var source = Path.Join(_root, "hardlink-prepared-source.m4b");
            var destination = Path.Join(_root, "hardlink-prepared-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var operationId = Guid.NewGuid();
            var crashingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterRegistrationPublicationStatePreparedForTestAsync = () =>
                    throw new InvalidOperationException("simulated crash")
            };

            using var interruptedLease =
                await crashingMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);

            Assert.Null(interruptedLease);
            Assert.True(File.Exists(source));
            Assert.False(File.Exists(destination));
            Assert.Single(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));

            var recoveryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var recoveredLease =
                await recoveryMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);

            Assert.NotNull(recoveredLease);
            Assert.True(recoveredLease.MatchesCurrentPublication());
            Assert.Equal("audio", await File.ReadAllTextAsync(destination));
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                CompletePreparedPublication(recoveredLease));
            Assert.Empty(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));
        }

        [Fact]
        public async Task PrepareActionForRegistration_EmptyHardlinkState_DoesNotAdoptReplacementSource()
        {
            var source = Path.Join(_root, "hardlink-replaced-source.m4b");
            var originalGeneration = Path.Join(
                _root,
                "hardlink-replaced-source-original.m4b");
            var destination = Path.Join(
                _root,
                "hardlink-replaced-destination.m4b");
            await File.WriteAllTextAsync(source, "original-audio");
            var operationId = Guid.NewGuid();
            var crashingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterRegistrationPublicationStatePreparedForTestAsync = () =>
                    throw new InvalidOperationException("simulated crash")
            };
            using var interruptedLease =
                await crashingMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);
            Assert.Null(interruptedLease);
            Assert.Single(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));

            File.Move(source, originalGeneration);
            await File.WriteAllTextAsync(source, "replacement-audio");
            var recoveryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var recoveredLease =
                await recoveryMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);

            Assert.Null(recoveredLease);
            Assert.False(File.Exists(destination));
            Assert.Equal(
                "original-audio",
                await File.ReadAllTextAsync(originalGeneration));
            Assert.Equal("replacement-audio", await File.ReadAllTextAsync(source));
            Assert.Single(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));
        }

        [Fact]
        public async Task PrepareActionForRegistration_EmptyHardlinkState_DoesNotAdoptChangedSourceSize()
        {
            var source = Path.Join(_root, "hardlink-resized-source.m4b");
            var destination = Path.Join(_root, "hardlink-resized-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var operationId = Guid.NewGuid();
            var crashingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterRegistrationPublicationStatePreparedForTestAsync = () =>
                    throw new InvalidOperationException("simulated crash")
            };
            using var interruptedLease =
                await crashingMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);
            Assert.Null(interruptedLease);

            await File.WriteAllTextAsync(source, "replacement-audio-is-longer");
            var recoveryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var recoveredLease =
                await recoveryMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);

            Assert.Null(recoveredLease);
            Assert.False(File.Exists(destination));
            Assert.Equal(
                "replacement-audio-is-longer",
                await File.ReadAllTextAsync(source));
            Assert.Single(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PrepareActionForRegistration_SourceSizeChangesDuringPublication_FailsClosed(
            bool changeAfterClaim)
        {
            var source = Path.Join(
                _root,
                $"hardlink-live-resize-source-{changeAfterClaim}.m4b");
            var destination = Path.Join(
                _root,
                $"hardlink-live-resize-destination-{changeAfterClaim}.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var operationId = Guid.NewGuid();
            Task ChangeSourceAsync() =>
                File.WriteAllTextAsync(source, "replacement-audio-is-longer");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterRegistrationPublicationStatePreparedForTestAsync =
                    changeAfterClaim ? null : ChangeSourceAsync,
                AfterRegistrationPublicationClaimPreparedForTestAsync =
                    changeAfterClaim ? ChangeSourceAsync : null
            };

            using var lease = await mover.PrepareActionForRegistrationAsync(
                FileAction.HardlinkCopy,
                source,
                destination,
                operationId);

            Assert.Null(lease);
            Assert.False(File.Exists(destination));
            Assert.Equal(
                "replacement-audio-is-longer",
                await File.ReadAllTextAsync(source));
            Assert.Single(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));
        }

        [Fact]
        public async Task PrepareActionForRegistration_EmptyLegacyHardlinkState_FailsClosed()
        {
            var source = Path.Join(_root, "legacy-empty-source.m4b");
            var destination = Path.Join(_root, "legacy-empty-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var operationId = Guid.NewGuid();
            var crashingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterRegistrationPublicationStatePreparedForTestAsync = () =>
                    throw new InvalidOperationException("simulated crash")
            };
            using var interruptedLease =
                await crashingMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);
            Assert.Null(interruptedLease);
            var statePath = Assert.Single(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));
            var stateStem = Path.GetFileNameWithoutExtension(statePath);
            var legacyStatePath = Path.Join(
                _root,
                stateStem[..stateStem.LastIndexOf('-')] + ".state");
            Directory.Move(statePath, legacyStatePath);

            var recoveryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var recoveredLease =
                await recoveryMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);

            Assert.Null(recoveredLease);
            Assert.False(File.Exists(destination));
            Assert.True(Directory.Exists(legacyStatePath));
        }

        [Fact]
        public async Task PrepareActionForRegistration_ClaimBearingLegacyHardlinkState_Recovers()
        {
            var source = Path.Join(_root, "legacy-claim-source.m4b");
            var destination = Path.Join(_root, "legacy-claim-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var operationId = Guid.NewGuid();
            var crashingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterRegistrationPublicationClaimPreparedForTestAsync = () =>
                    throw new InvalidOperationException("simulated crash")
            };
            using var interruptedLease =
                await crashingMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);
            Assert.Null(interruptedLease);
            var statePath = Assert.Single(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));
            var stateStem = Path.GetFileNameWithoutExtension(statePath);
            var legacyStatePath = Path.Join(
                _root,
                stateStem[..stateStem.LastIndexOf('-')] + ".state");
            Directory.Move(statePath, legacyStatePath);

            var recoveryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var recoveredLease =
                await recoveryMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);

            Assert.NotNull(recoveredLease);
            Assert.True(recoveredLease.MatchesCurrentPublication());
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                CompletePreparedPublication(recoveredLease));
            Assert.False(Directory.Exists(legacyStatePath));
        }

        [Fact]
        public async Task PrepareActionForRegistration_CopyRecovery_RetainsOriginalSourceGenerationEvidence()
        {
            var source = Path.Join(
                _root,
                "copy-registration-recovery-source.m4b");
            var displacedSource = Path.Join(
                _root,
                "copy-registration-recovery-source-original.m4b");
            var destination = Path.Join(
                _root,
                "copy-registration-recovery-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            await File.WriteAllTextAsync(destination, "previous");
            string originalSourceIdentity;
            using (var sourceLease =
                PinnedAudiobookFileRegistrationLease.Open(source))
            {
                originalSourceIdentity = sourceLease.PhysicalObjectIdentity;
            }

            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterPreparedPublicationCommittedForTestAsync = () =>
                    throw new IOException(
                        "simulated crash after prepared copy commit")
            };
            using var interruptedLease =
                await interruptedMover.PrepareActionForRegistrationAsync(
                    FileAction.Copy,
                    source,
                    destination,
                    Guid.NewGuid());

            Assert.Null(interruptedLease);
            Assert.False(File.Exists(destination));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "prepared.claim",
                SearchOption.AllDirectories));
            File.Move(source, displacedSource);
            await File.WriteAllTextAsync(source, "audio");

            var recoveryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var recoveredLease =
                await recoveryMover.PrepareActionForRegistrationAsync(
                    FileAction.Copy,
                    source,
                    destination,
                    Guid.NewGuid());

            Assert.NotNull(recoveredLease);
            Assert.True(recoveredLease.MatchesCurrentPublication());
            Assert.Equal(
                originalSourceIdentity,
                recoveredLease.SourcePhysicalObjectIdentity);
            Assert.Equal("audio", await File.ReadAllTextAsync(destination));
            Assert.Equal("audio", await File.ReadAllTextAsync(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(displacedSource));
            Assert.Empty(Directory.EnumerateDirectories(
                _root,
                ".listenarr-file-publication-*.state",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task PrepareActionForRegistration_StateCreationCollision_DoesNotFallBackToByteCopy()
        {
            var source = Path.Join(_root, "hardlink-state-collision-source.m4b");
            var destination = Path.Join(
                _root,
                "hardlink-state-collision-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var collisionCreated = false;
            using var hook = ExclusiveDirectoryCreator.PushBeforeCreateHook(
                path =>
                {
                    if (collisionCreated
                        || !Path.GetFileName(path).StartsWith(
                            ".listenarr-registration-publication-",
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    Directory.CreateDirectory(path);
                    collisionCreated = true;
                });
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            using var lease = await mover.PrepareActionForRegistrationAsync(
                FileAction.HardlinkCopy,
                source,
                destination,
                Guid.NewGuid());

            Assert.True(collisionCreated);
            Assert.Null(lease);
            Assert.False(File.Exists(destination));
            Assert.Single(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));
        }

        [Fact]
        public async Task PrepareActionForRegistration_HardlinkFailureAfterClaim_DoesNotFallBackToByteCopy()
        {
            var source = Path.Join(_root, "hardlink-claimed-source.m4b");
            var destination = Path.Join(_root, "hardlink-claimed-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var operationId = Guid.NewGuid();
            var failingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterRegistrationPublicationClaimPreparedForTestAsync = () =>
                    throw new IOException("simulated publication failure")
            };

            using var interruptedLease =
                await failingMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);

            Assert.Null(interruptedLease);
            Assert.False(File.Exists(destination));
            Assert.Single(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));

            var recoveryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var recoveredLease =
                await recoveryMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);

            Assert.NotNull(recoveredLease);
            Assert.True(recoveredLease.MatchesCurrentPublication());
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                CompletePreparedPublication(recoveredLease));
        }

        [Fact]
        public async Task PrepareActionForRegistration_RetiredDurableState_ForcesByteCopyFallback()
        {
            var source = Path.Join(_root, "hardlink-retired-state-source.m4b");
            var destination = Path.Join(
                _root,
                "hardlink-retired-state-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var ordinaryHardlinkAttempted = false;
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterRegistrationPublicationStatePreparedForTestAsync = () =>
                    throw new IOException("simulated durable hardlink failure"),
                BeforePinnedHardlinkCreationForTestAsync = () =>
                {
                    ordinaryHardlinkAttempted = true;
                    return Task.CompletedTask;
                }
            };

            using var lease = await mover.PrepareActionForRegistrationAsync(
                FileAction.HardlinkCopy,
                source,
                destination,
                Guid.NewGuid());

            Assert.NotNull(lease);
            Assert.False(ordinaryHardlinkAttempted);
            Assert.Empty(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));
            await File.WriteAllTextAsync(source, "changed");
            Assert.Equal("audio", await File.ReadAllTextAsync(destination));
            Assert.True(lease.MatchesCurrentPublication());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PrepareActionForRegistration_HardlinkCrash_RetryUsesDurableOperationClaim(
            bool crashAfterDestinationPublication)
        {
            var source = Path.Join(_root, $"hardlink-crash-source-{crashAfterDestinationPublication}.m4b");
            var destination = Path.Join(_root, $"hardlink-crash-destination-{crashAfterDestinationPublication}.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var operationId = Guid.NewGuid();
            var crashingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterRegistrationPublicationClaimPreparedForTestAsync =
                    crashAfterDestinationPublication
                        ? null
                        : () => throw new InvalidOperationException("simulated crash"),
                AfterRegistrationDestinationPublishedForTestAsync =
                    crashAfterDestinationPublication
                        ? () => throw new InvalidOperationException("simulated crash")
                        : null
            };

            using var interruptedLease =
                await crashingMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);

            Assert.Null(interruptedLease);
            Assert.True(File.Exists(source));
            Assert.Equal(
                crashAfterDestinationPublication,
                File.Exists(destination));
            Assert.Single(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));

            var recoveryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var recoveredLease =
                await recoveryMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);

            Assert.NotNull(recoveredLease);
            Assert.True(recoveredLease.MatchesCurrentPublication());
            Assert.Equal("audio", await File.ReadAllTextAsync(destination));
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                CompletePreparedPublication(recoveredLease));
            Assert.Empty(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));
        }

        [Fact]
        public async Task PrepareActionForRegistration_ConcurrentSameOperationRetries_ReturnSamePublication()
        {
            var source = Path.Join(_root, "hardlink-concurrent-source.m4b");
            var destination = Path.Join(
                _root,
                "hardlink-concurrent-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var operationId = Guid.NewGuid();
            var crashingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterRegistrationPublicationClaimPreparedForTestAsync = () =>
                    throw new InvalidOperationException("simulated crash")
            };
            using var interruptedLease =
                await crashingMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);
            Assert.Null(interruptedLease);

            var firstMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            var secondMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            var leases = await Task.WhenAll(
                firstMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId),
                secondMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId));
            using var firstLease = leases[0];
            using var secondLease = leases[1];

            Assert.NotNull(firstLease);
            Assert.NotNull(secondLease);
            Assert.Equal(
                firstLease.PhysicalObjectIdentity,
                secondLease.PhysicalObjectIdentity);
            Assert.True(firstLease.MatchesCurrentPublication());
            Assert.True(secondLease.MatchesCurrentPublication());
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                CompletePreparedPublication(firstLease));
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                CompletePreparedPublication(secondLease));
        }

        [LinuxFact]
        public async Task PrepareActionForRegistration_ConcurrentCompletionAfterDestinationReplacement_FailsClosed()
        {

            var source = Path.Join(_root, "completion-replaced-source.m4b");
            var destination = Path.Join(
                _root,
                "completion-replaced-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var operationId = Guid.NewGuid();
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var firstLease =
                await mover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);
            using var secondLease =
                await mover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);
            Assert.NotNull(firstLease);
            Assert.NotNull(secondLease);
            Assert.True(firstLease.PrepareCleanupRecovery(1));
            Assert.True(secondLease.PrepareCleanupRecovery(1));
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                firstLease.CompletePublication());

            File.Delete(destination);
            await File.WriteAllTextAsync(destination, "replacement");

            Assert.Equal(
                RegistrationPublicationCompletion.CommittedCleanupPending,
                secondLease.CompletePublication());
            Assert.Equal("replacement", await File.ReadAllTextAsync(destination));
            Assert.Equal("audio", await File.ReadAllTextAsync(source));
        }

        [Fact]
        public async Task PrepareActionForRegistration_RegisteredHardlinkGeneration_RemainsIdempotentAfterJournalRetirement()
        {
            var source = Path.Join(_root, "registered-hardlink-source.m4b");
            var destination = Path.Join(
                _root,
                "registered-hardlink-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var operationId = Guid.NewGuid();
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var initialLease =
                await mover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);
            Assert.NotNull(initialLease);
            var registeredIdentity = initialLease.PhysicalObjectIdentity;
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                CompletePreparedPublication(initialLease));

            using var recoveredLease =
                await mover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId,
                    registeredIdentity);

            Assert.NotNull(recoveredLease);
            Assert.True(recoveredLease.MatchesCurrentPublication());
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                CompletePreparedPublication(recoveredLease));
        }

        [Fact]
        public async Task PrepareActionForRegistration_RegisteredHardlinkCleanupCrash_UsesPersistedGenerationProof()
        {
            var source = Path.Join(_root, "registered-cleanup-source.m4b");
            var destination = Path.Join(
                _root,
                "registered-cleanup-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var operationId = Guid.NewGuid();
            var crashingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterRegistrationPublicationClaimRetiredForTest = () =>
                    throw new InvalidOperationException("simulated cleanup crash")
            };
            using var initialLease =
                await crashingMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);
            Assert.NotNull(initialLease);
            var registeredIdentity = initialLease.PhysicalObjectIdentity;

            Assert.Equal(
                RegistrationPublicationCompletion.CommittedCleanupPending,
                CompletePreparedPublication(initialLease));
            Assert.Single(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));

            var recoveryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var recoveredLease =
                await recoveryMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId,
                    registeredIdentity);

            Assert.NotNull(recoveredLease);
            Assert.True(recoveredLease.MatchesCurrentPublication());
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                CompletePreparedPublication(recoveredLease));
            Assert.Empty(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));
        }

        [Fact]
        public async Task PrepareActionForRegistration_CleanupIntentReplacedAfterClaimRetirement_PreservesReplacementAndBlocksCleanup()
        {
            var source = Path.Join(_root, "registered-intent-race-source.m4b");
            var destination = Path.Join(_root, "registered-intent-race-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            string? replacementIntent = null;
            string? displacedIntent = null;
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterRegistrationPublicationClaimRetiredForTest = () =>
                {
                    var stateDirectory = Assert.Single(
                        Directory.EnumerateDirectories(
                            _root,
                            ".listenarr-registration-publication-*.state"));
                    replacementIntent = Path.Join(
                        stateDirectory,
                        "registration.cleanup.json");
                    displacedIntent = replacementIntent + ".displaced";
                    File.Move(replacementIntent, displacedIntent);
                    File.WriteAllText(replacementIntent, "{\"replacement\":true}");
                }
            };
            using var lease = await mover.PrepareActionForRegistrationAsync(
                FileAction.HardlinkCopy,
                source,
                destination,
                Guid.NewGuid());
            Assert.NotNull(lease);

            var completion = CompletePreparedPublication(lease);

            Assert.Equal(
                RegistrationPublicationCompletion.CommittedCleanupPending,
                completion);
            Assert.NotNull(replacementIntent);
            Assert.NotNull(displacedIntent);
            Assert.True(File.Exists(replacementIntent));
            Assert.Equal(
                "{\"replacement\":true}",
                await File.ReadAllTextAsync(replacementIntent!));
            Assert.True(File.Exists(displacedIntent));
            Assert.True(File.Exists(destination));
        }

        [Fact]
        public async Task PrepareActionForRegistration_UnregisteredHardlinkAlias_RemainsRejected()
        {
            var source = Path.Join(_root, "unregistered-hardlink-source.m4b");
            var destination = Path.Join(
                _root,
                "unregistered-hardlink-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            Assert.True(await mover.HardlinkFileAsync(source, destination));

            using var lease = await mover.PrepareActionForRegistrationAsync(
                FileAction.HardlinkCopy,
                source,
                destination,
                Guid.NewGuid(),
                "unrelated-physical-identity");

            Assert.Null(lease);
            Assert.True(File.Exists(source));
            Assert.True(File.Exists(destination));
        }

        [Fact]
        public async Task PrepareActionForRegistration_HardlinkCrash_DifferentOperationCannotClaimPublication()
        {
            var source = Path.Join(_root, "hardlink-operation-source.m4b");
            var destination = Path.Join(_root, "hardlink-operation-destination.m4b");
            await File.WriteAllTextAsync(source, "audio");
            var operationId = Guid.NewGuid();
            var crashingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterRegistrationDestinationPublishedForTestAsync =
                    () => throw new InvalidOperationException("simulated crash")
            };
            using var interruptedLease =
                await crashingMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);
            Assert.Null(interruptedLease);

            var recoveryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var foreignLease =
                await recoveryMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    Guid.NewGuid());

            Assert.Null(foreignLease);
            Assert.Single(
                Directory.EnumerateDirectories(
                    _root,
                    ".listenarr-registration-publication-*.state"));

            using var recoveredLease =
                await recoveryMover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    source,
                    destination,
                    operationId);
            Assert.NotNull(recoveredLease);
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                CompletePreparedPublication(recoveredLease));
        }

        [Fact]
        public async Task PrepareActionForRegistration_DifferentOperationCannotReplacePublishedDestination()
        {
            var firstSource = Path.Join(_root, "hardlink-first-source.m4b");
            var secondSource = Path.Join(_root, "hardlink-second-source.m4b");
            var destination = Path.Join(_root, "hardlink-shared-destination.m4b");
            await File.WriteAllTextAsync(firstSource, "first-audio");
            await File.WriteAllTextAsync(secondSource, "second-audio");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var firstLease = await mover.PrepareActionForRegistrationAsync(
                FileAction.HardlinkCopy,
                firstSource,
                destination,
                Guid.NewGuid());
            Assert.NotNull(firstLease);

            using var conflictingLease =
                await mover.PrepareActionForRegistrationAsync(
                    FileAction.HardlinkCopy,
                    secondSource,
                    destination,
                    Guid.NewGuid());

            Assert.Null(conflictingLease);
            Assert.Equal("first-audio", await File.ReadAllTextAsync(destination));
            Assert.Equal("second-audio", await File.ReadAllTextAsync(secondSource));
            Assert.True(firstLease.MatchesCurrentPublication());
            Assert.Equal(
                RegistrationPublicationCompletion.Completed,
                CompletePreparedPublication(firstLease));
        }

        [Fact]
        public async Task CompletePreparedMoveAsync_SourceGenerationChanged_RetainsReplacementSource()
        {
            var source = Path.Join(_root, "prepared-changed-source.mp3");
            var destination = Path.Join(_root, "prepared-changed-destination.mp3");
            await File.WriteAllTextAsync(source, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var lease = await mover.PrepareActionForRegistrationAsync(
                FileAction.Move,
                source,
                destination);
            Assert.NotNull(lease);
            await File.WriteAllTextAsync(source, "replacement");

            var completed = await mover.CompletePreparedMoveAsync(
                source,
                destination,
                lease);

            Assert.False(completed);
            Assert.Equal("replacement", await File.ReadAllTextAsync(source));
            Assert.Equal("original", await File.ReadAllTextAsync(destination));
        }

        [Fact]
        public async Task CompletePreparedMoveAsync_SameContentSourceReplacement_IsNotDeleted()
        {
            var source = Path.Join(_root, "prepared-same-content-source.mp3");
            var originalGeneration = Path.Join(
                _root,
                "prepared-same-content-original.mp3");
            var destination = Path.Join(
                _root,
                "prepared-same-content-destination.mp3");
            await File.WriteAllTextAsync(source, "same-bytes");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());
            using var lease = await mover.PrepareActionForRegistrationAsync(
                FileAction.Move,
                source,
                destination);
            Assert.NotNull(lease);
            File.Move(source, originalGeneration);
            await File.WriteAllTextAsync(source, "same-bytes");

            var completed = await mover.CompletePreparedMoveAsync(
                source,
                destination,
                lease);

            Assert.False(completed);
            Assert.Equal("same-bytes", await File.ReadAllTextAsync(source));
            Assert.Equal(
                "same-bytes",
                await File.ReadAllTextAsync(originalGeneration));
            Assert.Equal(
                "same-bytes",
                await File.ReadAllTextAsync(destination));
        }

        [WindowsFact]
        public async Task CompletePreparedMoveAsync_WindowsFailureAfterSourceRetirement_RestoresSource()
        {
            var source = Path.Join(_root, "prepared-windows-race-source.mp3");
            var destination = Path.Join(_root, "prepared-windows-race-destination.mp3");
            await File.WriteAllTextAsync(source, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterPreparedMoveSourceDeletedForTestAsync = _ =>
                    Task.FromException(
                        new IOException("Injected post-retirement failure."))
            };
            using var lease = await mover.PrepareActionForRegistrationAsync(
                FileAction.Move,
                source,
                destination);
            Assert.NotNull(lease);

            var completed = await mover.CompletePreparedMoveAsync(
                source,
                destination,
                lease);

            Assert.False(completed);
            Assert.Equal("original", await File.ReadAllTextAsync(source));
            Assert.Equal("original", await File.ReadAllTextAsync(destination));
        }

        [Fact]
        public async Task CompletePreparedMoveAsync_RecoveredClaimSameContentReplacement_IsPreserved()
        {
            var source = Path.Join(_root, "prepared-replaced-claim-source.mp3");
            var destination = Path.Join(_root, "prepared-replaced-claim-destination.mp3");
            var displacedClaim = Path.Join(_root, "prepared-replaced-claim-original.mp3");
            await File.WriteAllTextAsync(source, "same-bytes");
            var operationId = Guid.NewGuid();
            var semanticsResolver = new FileSystemSemanticsResolver();
            using var lease = await new FileMover(
                    new NullLogger<FileMover>(),
                    semanticsResolver: semanticsResolver)
                .PrepareActionForRegistrationAsync(
                    FileAction.Move,
                    source,
                    destination,
                    operationId);
            Assert.NotNull(lease);

            var resolution = await semanticsResolver.ResolveAsync(source);
            Assert.Equal(PathIdentityState.Valid, resolution.State);
            var sourceIdentity = Path.GetFullPath(source);
            var destinationIdentity = Path.GetFullPath(destination);
            if (resolution.Semantics.CaseSensitivity
                == FileSystemCaseSensitivity.Insensitive)
            {
                sourceIdentity = sourceIdentity.ToUpperInvariant();
                destinationIdentity = destinationIdentity.ToUpperInvariant();
            }

            var claimIdentity = FormattableString.Invariant(
                $"{operationId:N}\0{sourceIdentity}\0{destinationIdentity}");
            var claimDigest = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(claimIdentity)));
            var claimPath = Path.Join(
                _root,
                $".listenarr-registration-move-{claimDigest[..32]}.claim");
            File.Move(source, displacedClaim);
            await File.WriteAllTextAsync(claimPath, "same-bytes");

            var completed = await new FileMover(
                    new NullLogger<FileMover>(),
                    semanticsResolver: semanticsResolver)
                .CompletePreparedMoveAsync(
                    source,
                    destination,
                    lease,
                    operationId);

            Assert.False(completed);
            Assert.False(File.Exists(claimPath));
            Assert.True(File.Exists(source));
            Assert.Equal("same-bytes", await File.ReadAllTextAsync(source));
            Assert.True(File.Exists(displacedClaim));
            Assert.Equal("same-bytes", await File.ReadAllTextAsync(displacedClaim));
            Assert.Equal("same-bytes", await File.ReadAllTextAsync(destination));
        }

        [Fact]
        public async Task CompletePreparedMoveAsync_RecoveredClaimFailureAfterRetirement_RestoresSource()
        {
            var source = Path.Join(_root, "prepared-recovery-failure-source.mp3");
            var destination = Path.Join(
                _root,
                "prepared-recovery-failure-destination.mp3");
            await File.WriteAllTextAsync(source, "original");
            var operationId = Guid.NewGuid();
            var semanticsResolver = new FileSystemSemanticsResolver();
            using var lease = await new FileMover(
                    new NullLogger<FileMover>(),
                    semanticsResolver: semanticsResolver)
                .PrepareActionForRegistrationAsync(
                    FileAction.Move,
                    source,
                    destination,
                    operationId);
            Assert.NotNull(lease);

            var resolution = await semanticsResolver.ResolveAsync(source);
            Assert.Equal(PathIdentityState.Valid, resolution.State);
            var sourceIdentity = Path.GetFullPath(source);
            var destinationIdentity = Path.GetFullPath(destination);
            if (resolution.Semantics.CaseSensitivity
                == FileSystemCaseSensitivity.Insensitive)
            {
                sourceIdentity = sourceIdentity.ToUpperInvariant();
                destinationIdentity = destinationIdentity.ToUpperInvariant();
            }

            var claimIdentity = FormattableString.Invariant(
                $"{operationId:N}\0{sourceIdentity}\0{destinationIdentity}");
            var claimDigest = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(claimIdentity)));
            var claimPath = Path.Join(
                _root,
                $".listenarr-registration-move-{claimDigest[..32]}.claim");
            File.Move(source, claimPath);

            var recoveryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: semanticsResolver)
            {
                AfterPreparedMoveSourceDeletedForTestAsync = _ =>
                    Task.FromException(
                        new IOException("Injected recovered-claim failure."))
            };

            var completed = await recoveryMover.CompletePreparedMoveAsync(
                source,
                destination,
                lease,
                operationId);

            Assert.False(completed);
            Assert.Equal("original", await File.ReadAllTextAsync(source));
            Assert.Equal("original", await File.ReadAllTextAsync(destination));
            Assert.False(File.Exists(claimPath));
        }

        [LinuxFact]
        public async Task CompletePreparedMoveAsync_UnixDestinationReplacedAfterSourceRetirement_RestoresSource()
        {
            var source = Path.Join(_root, "prepared-race-source.mp3");
            var destination = Path.Join(_root, "prepared-race-destination.mp3");
            var displaced = Path.Join(_root, "prepared-race-displaced.mp3");
            await File.WriteAllTextAsync(source, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterPreparedMoveSourceDeletedForTestAsync = async path =>
                {
                    File.Move(path, displaced);
                    await File.WriteAllTextAsync(path, "replacement");
                }
            };
            using var lease = await mover.PrepareActionForRegistrationAsync(
                FileAction.Move,
                source,
                destination);
            Assert.NotNull(lease);

            var completed = await mover.CompletePreparedMoveAsync(
                source,
                destination,
                lease);

            Assert.False(completed);
            Assert.Equal("original", await File.ReadAllTextAsync(source));
            Assert.Equal("replacement", await File.ReadAllTextAsync(destination));
            Assert.Equal("original", await File.ReadAllTextAsync(displaced));
        }

        [LinuxFact]
        public async Task CompletePreparedMoveAsync_UnixRecoveredClaimDestinationReplacedAfterRetirement_RestoresSource()
        {
            var source = Path.Join(_root, "prepared-recovery-race-source.mp3");
            var destination = Path.Join(
                _root,
                "prepared-recovery-race-destination.mp3");
            var displaced = Path.Join(
                _root,
                "prepared-recovery-race-displaced.mp3");
            await File.WriteAllTextAsync(source, "original");
            var operationId = Guid.NewGuid();
            var semanticsResolver = new FileSystemSemanticsResolver();
            using var lease = await new FileMover(
                    new NullLogger<FileMover>(),
                    semanticsResolver: semanticsResolver)
                .PrepareActionForRegistrationAsync(
                    FileAction.Move,
                    source,
                    destination,
                    operationId);
            Assert.NotNull(lease);

            var resolution = await semanticsResolver.ResolveAsync(source);
            Assert.Equal(PathIdentityState.Valid, resolution.State);
            var sourceIdentity = Path.GetFullPath(source);
            var destinationIdentity = Path.GetFullPath(destination);
            if (resolution.Semantics.CaseSensitivity
                == FileSystemCaseSensitivity.Insensitive)
            {
                sourceIdentity = sourceIdentity.ToUpperInvariant();
                destinationIdentity = destinationIdentity.ToUpperInvariant();
            }

            var claimIdentity = FormattableString.Invariant(
                $"{operationId:N}\0{sourceIdentity}\0{destinationIdentity}");
            var claimDigest = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(claimIdentity)));
            var claimPath = Path.Join(
                _root,
                $".listenarr-registration-move-{claimDigest[..32]}.claim");
            File.Move(source, claimPath);

            var recoveryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: semanticsResolver)
            {
                AfterPreparedMoveSourceDeletedForTestAsync = async path =>
                {
                    File.Move(path, displaced);
                    await File.WriteAllTextAsync(path, "replacement");
                }
            };

            var completed = await recoveryMover.CompletePreparedMoveAsync(
                source,
                destination,
                lease,
                operationId);

            Assert.False(completed);
            Assert.Equal("original", await File.ReadAllTextAsync(source));
            Assert.Equal("replacement", await File.ReadAllTextAsync(destination));
            Assert.Equal("original", await File.ReadAllTextAsync(displaced));
            Assert.False(File.Exists(claimPath));
        }

        [Fact]
        public async Task MoveDirectoryAsync_SamePath_IsNoOpAndPreservesContents()
        {
            var directory = Path.Join(_root, "same-directory");
            Directory.CreateDirectory(directory);
            var file = Path.Join(directory, "track.mp3");
            await File.WriteAllTextAsync(file, "content");
            var mover = new FileMover(new NullLogger<FileMover>());

            var ok = await mover.MoveDirectoryAsync(directory, directory);

            Assert.True(ok);
            Assert.True(File.Exists(file));
        }

        [Theory]
        [InlineData("same-volume directory source retirement")]
        [InlineData("same-volume directory destination publication")]
        public async Task MoveDirectoryAsync_PostRenameBarrierFailure_ReconcilesSuccessfulMove(
            string failingPhase)
        {
            var source = Path.Join(_root, $"barrier-source-{Guid.NewGuid():N}");
            var destination = Path.Join(_root, $"barrier-destination-{Guid.NewGuid():N}");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                BeforeFileMoveDurabilityBarrierForTest = phase =>
                {
                    if (string.Equals(phase, failingPhase, StringComparison.Ordinal))
                    {
                        throw new IOException("simulated post-rename durability failure");
                    }
                }
            };

            var moved = await mover.MoveDirectoryAsync(source, destination);

            Assert.True(moved);
            Assert.False(Directory.Exists(source));
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(destination, "book.m4b")));
        }

        [Fact]
        public void DirectoryRenameJournalStem_WindowsCaseDistinctPaths_DoNotCollide()
        {
            var first = FileMover.GetDirectoryRenameJournalStem(
                @"C:\Library\Book",
                @"C:\Destination\Book",
                FileSystemPathSyntax.Windows);
            var second = FileMover.GetDirectoryRenameJournalStem(
                @"C:\Library\book",
                @"C:\Destination\book",
                FileSystemPathSyntax.Windows);

            Assert.NotEqual(first, second);
        }

        [Fact]
        public async Task MoveDirectoryAsync_CrashAfterRenameJournalPublication_RecoversMovedGeneration()
        {
            var source = Path.Join(_root, $"journal-source-{Guid.NewGuid():N}");
            var destination = Path.Join(
                _root,
                $"journal-destination-{Guid.NewGuid():N}");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var crashingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDirectoryRenameJournalPublishedForTest = _ =>
                {
                    Directory.Move(source, destination);
                    throw new OperationCanceledException(
                        "simulated process termination after rename");
                }
            };

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                crashingMover.MoveDirectoryAsync(source, destination));

            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(destination));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                ".listenarr-directory-rename-*.journal",
                SearchOption.TopDirectoryOnly));

            var recovered = await new FileMover(
                    new NullLogger<FileMover>(),
                    semanticsResolver: new FileSystemSemanticsResolver())
                .MoveDirectoryAsync(source, destination);

            Assert.True(recovered);
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(destination, "book.m4b")));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                ".listenarr-directory-rename-*.journal",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task MoveDirectoryAsync_RenameJournalReplacedBeforeRetirement_PreservesReplacement()
        {
            var source = Path.Join(_root, $"journal-replaced-source-{Guid.NewGuid():N}");
            var destination = Path.Join(
                _root,
                $"journal-replaced-destination-{Guid.NewGuid():N}");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var crashingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDirectoryRenameJournalPublishedForTest = _ =>
                {
                    Directory.Move(source, destination);
                    throw new OperationCanceledException(
                        "simulated process termination after rename");
                }
            };
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                crashingMover.MoveDirectoryAsync(source, destination));
            var journalPath = Assert.Single(Directory.EnumerateFiles(
                _root,
                ".listenarr-directory-rename-*.journal",
                SearchOption.TopDirectoryOnly));
            var originalBytes = await File.ReadAllBytesAsync(journalPath);
            var displacedJournalPath = journalPath + ".displaced";
            var recoveringMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                BeforeDirectoryRenameJournalRetirementForTest = pinnedJournalPath =>
                {
                    Assert.Equal(journalPath, pinnedJournalPath);
                    File.Move(journalPath, displacedJournalPath);
                    File.WriteAllBytes(journalPath, originalBytes);
                }
            };

            await Assert.ThrowsAsync<IOException>(() =>
                recoveringMover.MoveDirectoryAsync(source, destination));

            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(destination));
            Assert.True(File.Exists(journalPath));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(journalPath));
            Assert.True(File.Exists(displacedJournalPath));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(displacedJournalPath));
        }

        [WindowsFact]
        public async Task MoveDirectoryAsync_ForeignPersistedRenameJournalAlias_FailsClosed()
        {
            var source = Path.Join(_root, $"journal-foreign-source-{Guid.NewGuid():N}");
            var destination = Path.Join(
                _root,
                $"journal-foreign-destination-{Guid.NewGuid():N}");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var crashingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDirectoryRenameJournalPublishedForTest = _ =>
                {
                    Directory.Move(source, destination);
                    throw new OperationCanceledException("simulated process termination after rename");
                }
            };
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                crashingMover.MoveDirectoryAsync(source, destination));
            var journalPath = Assert.Single(Directory.EnumerateFiles(
                _root,
                ".listenarr-directory-rename-*.journal",
                SearchOption.TopDirectoryOnly));
            await RewriteJournalPathsAsAmbiguousWindowsAliasesAsync(
                journalPath,
                "SourcePath",
                "DestinationPath");

            await Assert.ThrowsAsync<IOException>(() =>
                new FileMover(
                    new NullLogger<FileMover>(),
                    semanticsResolver: new FileSystemSemanticsResolver())
                    .MoveDirectoryAsync(source, destination));

            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(destination));
            Assert.True(File.Exists(journalPath));
        }

        [Fact]
        public async Task MoveDirectoryAsync_DestinationInsideSource_IsBlockedWithoutMutation()
        {
            var source = Path.Join(_root, "nested-source");
            var destination = Path.Join(source, "nested", "target");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var treePreflightCalls = 0;
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                BeforeDirectoryTreePreflightForTest = () => treePreflightCalls++
            };

            var result = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.Equal(0, treePreflightCalls);
            Assert.True(Directory.Exists(source));
            Assert.False(Directory.Exists(destination));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [DirectoryLinkFact]
        public async Task MoveDirectoryAsync_SymbolicLinkAlias_BlocksCopyDeleteFallback()
        {

            var source = Path.Join(_root, "linked-source");
            var alias = Path.Join(_root, "linked-alias");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            Directory.CreateSymbolicLink(alias, source);
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.MoveDirectoryAsync(source, alias);

            Assert.False(result);
            Assert.True(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [Fact]
        public async Task MoveDirectoryAsync_UnknownOverlap_BlocksCopyDeleteFallback()
        {
            var source = Path.Join(_root, "unknown-overlap-source");
            var destination = Path.Join(_root, "unknown-overlap-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var resolutionCallCount = 0;
            var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
            semanticsResolver.Setup(service => service.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                {
                    var call = Interlocked.Increment(ref resolutionCallCount);
                    return ValueTask.FromResult(call == 1
                        ? new FileSystemSemanticsResolution(
                            FileSystemPathSemantics.CurrentHostDefault,
                            PathIdentityState.Valid,
                            Path.GetDirectoryName(path) ?? path)
                        : new FileSystemSemanticsResolution(
                            FileSystemPathSemantics.CurrentHostDefault,
                            PathIdentityState.Unavailable,
                            Path.GetDirectoryName(path) ?? path,
                            "simulated overlap probe failure"));
                });
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: semanticsResolver.Object);

            var result = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.False(File.Exists(Path.Join(destination, "book.m4b")));
        }

        [Fact]
        public async Task CopyDirectoryAsync_EquivalentPath_IsRejectedWithoutMutation()
        {
            var source = Path.Join(_root, "copy-same-source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.CopyDirectoryAsync(source, Path.Join(source, "."));

            Assert.False(result);
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [Fact]
        public async Task CopyDirectoryAsync_DestinationInsideSource_IsRejectedBeforeCreatingArtifacts()
        {
            var source = Path.Join(_root, "copy-parent-source");
            var destination = Path.Join(source, "nested", "target");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var treePreflightCalls = 0;
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                BeforeDirectoryTreePreflightForTest = () => treePreflightCalls++
            };

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.Equal(0, treePreflightCalls);
            Assert.False(Directory.Exists(destination));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [Fact]
        public async Task CopyDirectoryAsync_SourceInsideDestination_IsRejectedWithoutWritingDestination()
        {
            var destination = Path.Join(_root, "copy-containing-destination");
            var source = Path.Join(destination, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var unrelated = Path.Join(destination, "unrelated.txt");
            await File.WriteAllTextAsync(unrelated, "preserve");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.Equal("preserve", await File.ReadAllTextAsync(unrelated));
            Assert.False(File.Exists(Path.Join(destination, "book.m4b")));
        }

        [Fact]
        public async Task CopyDirectoryAsync_UnavailableFilesystemSemantics_FailsClosedWithoutArtifacts()
        {
            var source = Path.Join(_root, "copy-unknown-source");
            var destination = Path.Join(_root, "copy-unknown-destination");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
            semanticsResolver.Setup(service => service.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        FileSystemPathSemantics.CurrentHostDefault,
                        PathIdentityState.Unavailable,
                        Path.GetDirectoryName(path) ?? path,
                        "simulated unavailable semantics")));
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: semanticsResolver.Object);

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.False(Directory.Exists(destination));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [Fact]
        public async Task CopyDirectoryAsync_CaseAliasUnderInsensitiveSemantics_IsRejected()
        {
            var source = Path.Join(_root, "CopyCaseSource");
            var destination = Path.Join(_root, "copycasesource", "nested");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
            semanticsResolver.Setup(service => service.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(
                            FileSystemPathSemantics.CurrentHostDefault.Syntax,
                            FileSystemCaseSensitivity.Insensitive),
                        PathIdentityState.Valid,
                        Path.GetPathRoot(path) ?? path)));
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: semanticsResolver.Object);

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.False(Directory.Exists(destination));
        }

        [Fact]
        public async Task CopyDirectoryAsync_SourceMutationAfterPreflight_FailsWithoutPublishingSnapshot()
        {
            var source = Path.Join(_root, "copy-snapshot-source");
            var destination = Path.Join(_root, "copy-snapshot-destination");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "original.m4b"), "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDirectoryCopyPreflightForTestAsync = async () =>
                {
                    var lateDirectory = Path.Join(source, "late");
                    Directory.CreateDirectory(lateDirectory);
                    await File.WriteAllTextAsync(Path.Join(lateDirectory, "late.m4b"), "late");
                }
            };

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.False(Directory.Exists(destination));
            Assert.Equal("original", await File.ReadAllTextAsync(Path.Join(source, "original.m4b")));
            Assert.Equal("late", await File.ReadAllTextAsync(Path.Join(source, "late", "late.m4b")));
            var retainedCopyDirectories = Directory.EnumerateDirectories(_root)
                .Where(directory => Path.GetFileName(directory).Contains(
                    ".listenarr-copy-",
                    StringComparison.Ordinal))
                .ToList();
            Assert.True(
                retainedCopyDirectories.Count == 0,
                string.Join(
                    Environment.NewLine,
                    retainedCopyDirectories.Select(directory =>
                        $"{directory}: {string.Join(", ", Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName))}")));
        }

        [Fact]
        public async Task CopyDirectoryAsync_SiblingPrefix_IsAllowed()
        {
            var source = Path.Join(_root, "copy-book");
            var destination = Path.Join(_root, "copy-book-expanded");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.True(result);
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(destination, "book.m4b")));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [DirectoryLinkFact]
        public async Task CopyDirectoryAsync_MissingDestinationBelowSymlinkedParent_IsRejected()
        {

            var source = Path.Join(_root, "copy-linked-parent-source");
            var alias = Path.Join(_root, "copy-linked-parent-alias");
            var destination = Path.Join(alias, "nested", "target");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            Directory.CreateSymbolicLink(alias, source);
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.False(Directory.Exists(Path.Join(source, "nested")));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [DirectoryLinkFact]
        public async Task CopyDirectoryAsync_SymbolicLinkAlias_IsRejectedWhereSupported()
        {

            var source = Path.Join(_root, "copy-linked-source");
            var alias = Path.Join(_root, "copy-linked-alias");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            Directory.CreateSymbolicLink(alias, source);
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.CopyDirectoryAsync(source, alias);

            Assert.False(result);
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [Fact]
        public async Task MoveFileAsync_UnavailableIdentityForSameFileAlias_PreservesSource()
        {
            var sourceFile = Path.Join(_root, "same-file.mp3");
            var aliasedDestination = Path.Join(_root, ".", "same-file.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");
            var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
            semanticsResolver.Setup(service => service.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        FileSystemPathSemantics.CurrentHostDefault,
                        PathIdentityState.Unavailable,
                        Path.GetDirectoryName(path) ?? path,
                        "simulated probe failure")));
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: semanticsResolver.Object);

            var result = await mover.MoveFileAsync(sourceFile, aliasedDestination);

            Assert.True(result);
            Assert.True(File.Exists(sourceFile));
            Assert.Equal("content", await File.ReadAllTextAsync(sourceFile));
        }

        [FileLinkFact]
        public async Task MoveFileAsync_SymbolicLinkDestinationToSource_PreservesFileContent()
        {

            var sourceFile = Path.Join(_root, "linked-file-source.mp3");
            var aliasedDestination = Path.Join(_root, "linked-file-alias.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");
            File.CreateSymbolicLink(aliasedDestination, sourceFile);
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            await mover.MoveFileAsync(sourceFile, aliasedDestination);

            var survivingPath = File.Exists(sourceFile) ? sourceFile : aliasedDestination;
            Assert.True(File.Exists(survivingPath));
            Assert.Equal("content", await File.ReadAllTextAsync(survivingPath));
        }

        [Fact]
        public async Task MoveFileAsync_VerifiedCopyFallback_RemovesSourceBeforeReportingSuccess()
        {
            var sourceFile = Path.Join(_root, "fallback-source.mp3");
            var destinationFile = Path.Join(_root, "fallback-destination.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 0 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(moved);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("content", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "*.partial",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task MoveFileAsync_CopyFallback_SourceRecreatedAfterClaim_IsPreserved()
        {
            var sourceFile = Path.Join(_root, "fallback-swap-source.mp3");
            var destinationFile = Path.Join(_root, "fallback-swap-destination.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 0 }),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = async (claimedSource, quarantinePath) =>
                {
                    Assert.Equal(sourceFile, claimedSource);
                    Assert.True(File.Exists(quarantinePath));
                    await File.WriteAllTextAsync(sourceFile, "replacement");
                }
            };

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.False(moved);
            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(_root),
                path => path.Contains(".listenarr-copy-cleanup-", StringComparison.Ordinal));
        }

        [Fact]
        public async Task MoveFileAsync_IdempotentDestination_SourceRecreatedAfterClaim_IsPreserved()
        {
            var sourceFile = Path.Join(_root, "idempotent-swap-source.mp3");
            var destinationFile = Path.Join(_root, "idempotent-swap-destination.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = async (claimedSource, quarantinePath) =>
                {
                    Assert.Equal(sourceFile, claimedSource);
                    Assert.True(File.Exists(quarantinePath));
                    await File.WriteAllTextAsync(sourceFile, "replacement");
                }
            };

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.False(moved);
            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(_root),
                path => path.Contains(".listenarr-copy-cleanup-", StringComparison.Ordinal));
        }

        [Fact]
        public async Task MoveFileAsync_DestinationRecreatedDuringCommit_IsPreservedAndBlocksPublication()
        {
            var sourceFile = Path.Join(_root, "destination-swap-source.mp3");
            var destinationFile = Path.Join(_root, "destination-swap-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                DisableNativeFileRenameForTest = true,
                AfterDestinationQuarantinedForTestAsync = async (claimedDestination, claimPath) =>
                {
                    Assert.Equal(destinationFile, claimedDestination);
                    Assert.True(File.Exists(claimPath));
                    await File.WriteAllTextAsync(destinationFile, "replacement");
                }
            };

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.False(moved);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("replacement", await File.ReadAllTextAsync(destinationFile));
            var preservedStage = Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.stage",
                SearchOption.AllDirectories));
            Assert.Equal("original", await File.ReadAllTextAsync(preservedStage));
        }

        [Fact]
        public async Task MoveFileAsync_InterruptedAfterSourceClaim_RecoversOnRetry()
        {
            var sourceFile = Path.Join(_root, "interrupted-claim-source.mp3");
            var destinationFile = Path.Join(_root, "interrupted-claim-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            Assert.False(File.Exists(sourceFile));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
            var retryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var retried = await retryMover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "*.claim",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_ReverseDirectionCannotBypassInterruptedState()
        {
            var firstPath = Path.Join(_root, "reverse-state-first.mp3");
            var secondPath = Path.Join(_root, "reverse-state-second.mp3");
            await File.WriteAllTextAsync(firstPath, "original");
            await File.WriteAllTextAsync(secondPath, "original");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated directional interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(firstPath, secondPath));

            var sourceClaim = Assert.Single(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));

            var reversed = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(secondPath, firstPath);

            Assert.False(reversed);
            Assert.False(File.Exists(firstPath));
            Assert.Equal("original", await File.ReadAllTextAsync(secondPath));
            Assert.Equal("original", await File.ReadAllTextAsync(sourceClaim));
        }

        [Fact]
        public async Task MoveFileAsync_UnexpectedRecoveryState_IsPreservedAndBlocked()
        {
            var sourceFile = Path.Join(_root, "ambiguous-state-source.mp3");
            var destinationFile = Path.Join(_root, "ambiguous-state-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated ambiguous-state interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(sourceFile, destinationFile));

            var sourceStateDirectory = Assert.Single(Directory.EnumerateDirectories(
                _root,
                ".listenarr-file-source-*.state",
                SearchOption.TopDirectoryOnly));
            var unexpectedPath = Path.Join(sourceStateDirectory, "unexpected.txt");
            await File.WriteAllTextAsync(unexpectedPath, "preserve");

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Equal("preserve", await File.ReadAllTextAsync(unexpectedPath));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_DestinationRecreatedAfterVerification_IsPreservedAndBlocksPublication()
        {
            var sourceFile = Path.Join(_root, "verified-swap-source.mp3");
            var destinationFile = Path.Join(_root, "verified-swap-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                DisableNativeFileRenameForTest = true,
                AfterSourceClaimDeletedForTestAsync = () =>
                {
                    File.WriteAllText(destinationFile, "replacement");
                    return Task.CompletedTask;
                }
            };

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.False(moved);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("replacement", await File.ReadAllTextAsync(destinationFile));
            var preservedStage = Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.stage",
                SearchOption.AllDirectories));
            Assert.Equal("original", await File.ReadAllTextAsync(preservedStage));
        }

        [Fact]
        public async Task MoveFileAsync_MissingClaimBeforeRetirementFence_DoesNotInferCompletion()
        {
            var sourceFile = Path.Join(_root, "missing-claim-source.mp3");
            var destinationFile = Path.Join(_root, "missing-claim-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDestinationStateCreatedForTestAsync = () =>
                    throw new OperationCanceledException("simulated destination-state interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(sourceFile, destinationFile));

            var sourceClaim = Assert.Single(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
            File.Delete(sourceClaim);

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_MissingSourceClaimWithUnfencedStage_IsPreservedAndBlocked()
        {
            var sourceFile = Path.Join(_root, "missing-staged-claim-source.mp3");
            var destinationFile = Path.Join(_root, "missing-staged-claim-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source generation");
            await File.WriteAllTextAsync(destinationFile, "destination generation");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                DisableNativeFileRenameForTest = true,
                AfterDestinationQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated staged interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(sourceFile, destinationFile));
            var sourceClaim = Assert.Single(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
            File.Delete(sourceClaim);

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.False(File.Exists(destinationFile));
            var stage = Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.stage",
                SearchOption.AllDirectories));
            var previous = Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.previous",
                SearchOption.AllDirectories));
            Assert.Equal("source generation", await File.ReadAllTextAsync(stage));
            Assert.Equal("destination generation", await File.ReadAllTextAsync(previous));
        }

        [Fact]
        public async Task MoveFileAsync_OpenHandleMutationAfterStaging_IsRestoredAndFailsClosed()
        {
            var sourceFile = Path.Join(_root, "open-handle-source.mp3");
            var destinationFile = Path.Join(_root, "open-handle-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "different");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 0 }),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDestinationQuarantinedForTestAsync = async (_, _) =>
                {
                    var claimedSource = Assert.Single(Directory.EnumerateFiles(
                        _root,
                        "source.claim",
                        SearchOption.AllDirectories));
                    await using var claimedHandle = new FileStream(
                        claimedSource,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.ReadWrite | FileShare.Delete);
                    claimedHandle.Position = 0;
                    await claimedHandle.WriteAsync("changed!"u8.ToArray());
                    await claimedHandle.FlushAsync();
                }
            };

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.False(moved);
            Assert.Equal("changed!", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("different", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_UncommittedRecoverySourceClaimReplaced_PreservesReplacementAndBlocks()
        {
            var sourceFile = Path.Join(_root, "recovery-source-claim-source.mp3");
            var destinationFile = Path.Join(_root, "recovery-source-claim-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source-generation");
            await File.WriteAllTextAsync(destinationFile, "destination-generation");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                DisableNativeFileRenameForTest = true,
                AfterDestinationQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated uncommitted interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(sourceFile, destinationFile));
            var claim = Assert.Single(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
            var displaced = Path.Join(_root, "original-source-claim.mp3");
            File.Move(claim, displaced);
            await File.WriteAllTextAsync(claim, "replacement-source-claim");

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.False(File.Exists(destinationFile));
            Assert.Equal("replacement-source-claim", await File.ReadAllTextAsync(claim));
            Assert.Equal("source-generation", await File.ReadAllTextAsync(displaced));
        }

        [Fact]
        public async Task MoveFileAsync_UncommittedRecoveryStageReplaced_PreservesReplacementAndBlocks()
        {
            var sourceFile = Path.Join(_root, "recovery-stage-source.mp3");
            var destinationFile = Path.Join(_root, "recovery-stage-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source-generation");
            await File.WriteAllTextAsync(destinationFile, "destination-generation");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                DisableNativeFileRenameForTest = true,
                AfterDestinationQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated uncommitted interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(sourceFile, destinationFile));
            var stage = Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.stage",
                SearchOption.AllDirectories));
            var displaced = Path.Join(_root, "original-destination-stage.mp3");
            File.Move(stage, displaced);
            await File.WriteAllTextAsync(stage, "replacement-stage");

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.False(File.Exists(destinationFile));
            Assert.Equal("replacement-stage", await File.ReadAllTextAsync(stage));
            Assert.Equal("source-generation", await File.ReadAllTextAsync(displaced));
        }

        [Fact]
        public async Task MoveFileAsync_UncommittedRecoveryPreviousReplaced_PreservesReplacementAndBlocks()
        {
            var sourceFile = Path.Join(_root, "recovery-previous-source.mp3");
            var destinationFile = Path.Join(_root, "recovery-previous-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source-generation");
            await File.WriteAllTextAsync(destinationFile, "destination-generation");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                DisableNativeFileRenameForTest = true,
                AfterDestinationQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated uncommitted interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(sourceFile, destinationFile));
            var previous = Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.previous",
                SearchOption.AllDirectories));
            var displaced = Path.Join(_root, "original-destination-previous.mp3");
            File.Move(previous, displaced);
            await File.WriteAllTextAsync(previous, "replacement-previous");

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.False(File.Exists(destinationFile));
            Assert.Equal("replacement-previous", await File.ReadAllTextAsync(previous));
            Assert.Equal("destination-generation", await File.ReadAllTextAsync(displaced));
        }

        [Fact]
        public async Task MoveFileAsync_InterruptedAfterDestinationStage_RecoversOnRetry()
        {
            var sourceFile = Path.Join(_root, "stage-crash-source.mp3");
            var destinationFile = Path.Join(_root, "stage-crash-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                DisableNativeFileRenameForTest = true,
                AfterDestinationQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated stage interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            Assert.False(File.Exists(sourceFile));
            Assert.False(File.Exists(destinationFile));
            var interruptedStage = Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.stage",
                SearchOption.AllDirectories));
            Assert.Equal("original", await File.ReadAllTextAsync(interruptedStage));
            var retryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var retried = await retryMover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_InterruptedAfterRetirementFence_PreservesReplacementAndCompletesRecovery()
        {
            var sourceFile = Path.Join(_root, "fence-crash-source.mp3");
            var destinationFile = Path.Join(_root, "fence-crash-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceRetirementCommittedForTestAsync = () =>
                {
                    File.WriteAllText(sourceFile, "replacement");
                    throw new OperationCanceledException("simulated retirement-fence interruption");
                }
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "replacement-generation.fence",
                SearchOption.AllDirectories));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_InterruptedAfterSourceRetirement_CompletesRecovery()
        {
            var sourceFile = Path.Join(_root, "retired-crash-source.mp3");
            var destinationFile = Path.Join(_root, "retired-crash-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                DisableNativeFileRenameForTest = true,
                AfterSourceClaimDeletedForTestAsync = () =>
                    throw new OperationCanceledException("simulated retirement interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            Assert.False(File.Exists(sourceFile));
            Assert.False(File.Exists(destinationFile));
            var interruptedStage = Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.stage",
                SearchOption.AllDirectories));
            Assert.Equal("original", await File.ReadAllTextAsync(interruptedStage));
            var interruptedPrevious = Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.previous",
                SearchOption.AllDirectories));
            var operationState = Assert.Single(Directory.EnumerateFiles(
                _root,
                "operation.state",
                SearchOption.AllDirectories));
            var operationPayload = await File.ReadAllTextAsync(operationState);
            using (var stageParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                Path.GetDirectoryName(interruptedStage)!))
            using (var stage = stageParent.OpenExistingFile(
                Path.GetFileName(interruptedStage),
                requireDeleteAccess: false))
            using (var previous = stageParent.OpenExistingFile(
                Path.GetFileName(interruptedPrevious),
                requireDeleteAccess: false))
            {
                Assert.Contains(
                    $"destinationStageObjectIdentity={stage.GetObjectIdentity()}",
                    operationPayload,
                    StringComparison.Ordinal);
                Assert.Contains(
                    $"destinationPreviousObjectIdentity={previous.GetObjectIdentity()}",
                    operationPayload,
                    StringComparison.Ordinal);
            }
            var retryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var retried = await retryMover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "destination.stage",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_SourceCreatedAfterRetirementBeforePublication_IsPreservedOnRetry()
        {
            var sourceFile = Path.Join(_root, "retirement-replacement-source.mp3");
            var destinationFile = Path.Join(_root, "retirement-replacement-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                DisableNativeFileRenameForTest = true,
                AfterSourceClaimDeletedForTestAsync = () =>
                {
                    File.WriteAllText(sourceFile, "replacement");
                    throw new OperationCanceledException("simulated post-retirement interruption");
                }
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.False(File.Exists(destinationFile));
            var interruptedStage = Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.stage",
                SearchOption.AllDirectories));
            Assert.Equal("original", await File.ReadAllTextAsync(interruptedStage));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_InterruptedAfterStateCleanup_RetryFailsWithoutFalseCompletion()
        {
            var sourceFile = Path.Join(_root, "cleaned-crash-source.mp3");
            var destinationFile = Path.Join(_root, "cleaned-crash-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterFileMoveStateCleanedForTestAsync = () =>
                    throw new OperationCanceledException("simulated post-cleanup interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 0 }),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_MissingSourceWithExistingDestination_DoesNotInferCompletion()
        {
            var sourceFile = Path.Join(_root, "never-started-source.mp3");
            var destinationFile = Path.Join(_root, "unrelated-existing-target.mp3");
            await File.WriteAllTextAsync(destinationFile, "unrelated");

            var moved = await new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 0 }),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(moved);
            Assert.Equal("unrelated", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_RecreatedSourceSurvivesPostCleanupCrashAndRetry()
        {
            var sourceFile = Path.Join(_root, "fenced-source.mp3");
            var destinationFile = Path.Join(_root, "fenced-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, _) =>
                {
                    File.WriteAllText(sourceFile, "replacement");
                    return Task.CompletedTask;
                },
                AfterFileMoveStateCleanedForTestAsync = () =>
                    throw new OperationCanceledException("simulated fenced cleanup interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "replacement-generation.fence",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_SourceCreatedAfterPublication_IsFencedBeforeCleanup()
        {
            var sourceFile = Path.Join(_root, "late-fenced-source.mp3");
            var destinationFile = Path.Join(_root, "late-fenced-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDestinationPublishedForTestAsync = path =>
                {
                    Assert.Equal(destinationFile, path);
                    File.WriteAllText(sourceFile, "replacement");
                    return Task.CompletedTask;
                },
                AfterFileMoveStateCleanedForTestAsync = () =>
                    throw new OperationCanceledException("simulated late fenced cleanup interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_QuarantinedPredecessorReplacedBeforeRetirement_PreservesReplacement()
        {
            var sourceFile = Path.Join(_root, "predecessor-race-source.mp3");
            var destinationFile = Path.Join(_root, "predecessor-race-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "new-generation");
            await File.WriteAllTextAsync(destinationFile, "previous-generation");
            string? displacedPredecessor = null;
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDestinationPublishedForTestAsync = _ =>
                {
                    var predecessor = Assert.Single(Directory.EnumerateFiles(
                        _root,
                        "destination.previous",
                        SearchOption.AllDirectories));
                    displacedPredecessor = predecessor + ".displaced";
                    File.Move(predecessor, displacedPredecessor);
                    File.WriteAllText(predecessor, "replacement-predecessor");
                    return Task.CompletedTask;
                }
            };

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.False(moved);
            Assert.Equal("new-generation", await File.ReadAllTextAsync(destinationFile));
            var replacement = Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.previous",
                SearchOption.AllDirectories));
            Assert.Equal(
                "replacement-predecessor",
                await File.ReadAllTextAsync(replacement));
            Assert.NotNull(displacedPredecessor);
            Assert.True(File.Exists(displacedPredecessor));
            Assert.Equal(
                "previous-generation",
                await File.ReadAllTextAsync(displacedPredecessor!));
        }

        [Fact]
        public async Task MoveFileAsync_PublishedDestinationSubstitution_BlocksRecovery()
        {
            var sourceFile = Path.Join(_root, "substituted-source.mp3");
            var destinationFile = Path.Join(_root, "substituted-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "new-generation");
            await File.WriteAllTextAsync(destinationFile, "previous-generation");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDestinationPublishedForTestAsync = _ =>
                    throw new OperationCanceledException(
                        "simulated interruption after publication")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));
            File.Delete(destinationFile);
            await File.WriteAllTextAsync(destinationFile, "attacker-replacement");

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.Equal(
                "attacker-replacement",
                await File.ReadAllTextAsync(destinationFile));
            var previous = Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.previous",
                SearchOption.AllDirectories));
            Assert.Equal(
                "previous-generation",
                await File.ReadAllTextAsync(previous));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "replacement-generation.fence",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_InterruptedBeforeSourceClaim_DoesNotInferCompletion()
        {
            var sourceFile = Path.Join(_root, "preclaim-crash-source.mp3");
            var destinationFile = Path.Join(_root, "preclaim-crash-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceStateCreatedForTestAsync = () =>
                    throw new OperationCanceledException("simulated preclaim interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));
            File.Delete(sourceFile);

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_UnrelatedPaths_DoNotSharePairGate()
        {
            var firstSource = Path.Join(_root, "gate-first-source.mp3");
            var firstDestination = Path.Join(_root, "gate-first-target.mp3");
            var secondSource = Path.Join(_root, "gate-second-source.mp3");
            var secondDestination = Path.Join(_root, "gate-second-target.mp3");
            await File.WriteAllTextAsync(firstSource, "first");
            await File.WriteAllTextAsync(firstDestination, "first");
            await File.WriteAllTextAsync(secondSource, "second");
            await File.WriteAllTextAsync(secondDestination, "second");
            var firstClaimReached = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstClaim = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = async (_, _) =>
                {
                    firstClaimReached.SetResult();
                    await releaseFirstClaim.Task;
                }
            };

            var firstMove = firstMover.MoveFileAsync(firstSource, firstDestination);
            await firstClaimReached.Task;
            var secondMove = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(secondSource, secondDestination);

            Assert.True(await secondMove);
            releaseFirstClaim.SetResult();
            Assert.True(await firstMove);
            Assert.False(File.Exists(firstSource));
            Assert.False(File.Exists(secondSource));
        }

        [Fact]
        public async Task MoveFileAsync_ConcurrentSamePaths_AreSerializedByPathLocks()
        {
            var sourceFile = Path.Join(_root, "concurrent-source.mp3");
            var destinationFile = Path.Join(_root, "concurrent-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var claimReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = async (_, _) =>
                {
                    claimReached.SetResult();
                    await releaseClaim.Task;
                }
            };
            var firstMove = firstMover.MoveFileAsync(sourceFile, destinationFile);
            await claimReached.Task;
            var competingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var competingMove = competingMover.MoveFileAsync(sourceFile, destinationFile);

            Assert.False(competingMove.IsCompleted);
            releaseClaim.SetResult();
            Assert.True(await firstMove);
            Assert.False(await competingMove);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [WindowsFact]
        public async Task MoveFileAsync_CaseAliasesShareResolvedEndpointLocks()
        {

            var sourceFile = Path.Join(_root, "case-lock-source.mp3");
            var destinationFile = Path.Join(_root, "case-lock-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var claimReached = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseClaim = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = async (_, _) =>
                {
                    claimReached.SetResult();
                    await releaseClaim.Task;
                }
            };

            var firstMove = firstMover.MoveFileAsync(sourceFile, destinationFile);
            await claimReached.Task;
            var aliasedMove = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(
                    sourceFile.ToUpperInvariant(),
                    destinationFile.ToUpperInvariant());

            Assert.False(aliasedMove.IsCompleted);
            releaseClaim.SetResult();
            Assert.True(await firstMove);
            Assert.False(await aliasedMove);
        }

        [WindowsFact]
        public async Task MoveFileAsync_CaseAliasRetryRecoversSameCrashState()
        {

            var sourceFile = Path.Join(_root, "case-recovery-source.mp3");
            var destinationFile = Path.Join(_root, "case-recovery-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated alias interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(sourceFile, destinationFile));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(
                    sourceFile.ToUpperInvariant(),
                    destinationFile.ToUpperInvariant());

            Assert.True(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
        }

        [DirectoryLinkFact]
        public async Task MoveFileAsync_LinkedParentAliasRetryRecoversSameCrashState()
        {

            var realDirectory = Path.Join(_root, "real-parent");
            var aliasDirectory = Path.Join(_root, "linked-parent");
            Directory.CreateDirectory(realDirectory);
            Directory.CreateSymbolicLink(aliasDirectory, realDirectory);
            var realSource = Path.Join(realDirectory, "alias-source.mp3");
            var realDestination = Path.Join(realDirectory, "alias-target.mp3");
            await File.WriteAllTextAsync(realSource, "original");
            await File.WriteAllTextAsync(realDestination, "original");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated linked-parent interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(
                    Path.Join(aliasDirectory, "alias-source.mp3"),
                    Path.Join(aliasDirectory, "alias-target.mp3")));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(realSource, realDestination);

            Assert.True(retried);
            Assert.False(File.Exists(realSource));
            Assert.Equal("original", await File.ReadAllTextAsync(realDestination));
            Assert.Empty(Directory.EnumerateFiles(
                realDirectory,
                "source.claim",
                SearchOption.AllDirectories));
        }

        [DirectoryLinkFact]
        public async Task MoveFileAsync_LinkedParentReplacedAfterEndpointResolution_BlocksBothTargets()
        {
            var firstDirectory = Path.Join(_root, "linked-race-first");
            var secondDirectory = Path.Join(_root, "linked-race-second");
            var aliasDirectory = Path.Join(_root, "linked-race-alias");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            Directory.CreateSymbolicLink(aliasDirectory, firstDirectory);
            var firstSource = Path.Join(firstDirectory, "source.mp3");
            var firstDestination = Path.Join(firstDirectory, "destination.mp3");
            var secondSource = Path.Join(secondDirectory, "source.mp3");
            var secondDestination = Path.Join(secondDirectory, "destination.mp3");
            await File.WriteAllTextAsync(firstSource, "first");
            await File.WriteAllTextAsync(secondSource, "second");
            var swapped = false;
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterFileMoveEndpointsResolvedForTestAsync = (_, _) =>
                {
                    if (!swapped)
                    {
                        Directory.Delete(aliasDirectory);
                        Directory.CreateSymbolicLink(
                            aliasDirectory,
                            secondDirectory);
                        swapped = true;
                    }

                    return Task.CompletedTask;
                }
            };

            var moved = await mover.MoveFileAsync(
                Path.Join(aliasDirectory, "source.mp3"),
                Path.Join(aliasDirectory, "destination.mp3"));

            Assert.True(swapped);
            Assert.False(moved);
            Assert.Equal("first", await File.ReadAllTextAsync(firstSource));
            Assert.False(File.Exists(firstDestination));
            Assert.Equal("second", await File.ReadAllTextAsync(secondSource));
            Assert.False(File.Exists(secondDestination));
        }

        [Fact]
        public async Task MoveFileAsync_LongSourceName_UsesCompactClaimNames()
        {
            var longStem = new string('a', 180);
            var sourceFile = Path.Join(_root, $"{longStem}-source.mp3");
            var destinationFile = Path.Join(_root, $"{longStem}-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            string? observedClaimName = null;
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, claimPath) =>
                {
                    observedClaimName = Path.GetFileName(claimPath);
                    return Task.CompletedTask;
                }
            };

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(moved);
            Assert.NotNull(observedClaimName);
            Assert.True(observedClaimName.Length < 80);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [WindowsFact]
        public async Task MoveFileAsync_VerifiedCopyFallback_SourceDeleteFailureReportsFailure()
        {

            var sourceFile = Path.Join(_root, "retained-source.mp3");
            var destinationFile = Path.Join(_root, "published-destination.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 0 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            bool moved;
            using (File.Open(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                moved = await mover.MoveFileAsync(sourceFile, destinationFile);
            }

            Assert.False(moved);
            Assert.Equal("content", await File.ReadAllTextAsync(sourceFile));
            Assert.False(File.Exists(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_MovesFileSuccessfully()
        {
            var sourceFile = Path.Join(_root, "a.mp3");
            var destFile = Path.Join(_root, "b.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");

            var mover = new FileMover(new NullLogger<FileMover>());
            var ok = await mover.MoveFileAsync(sourceFile, destFile);

            Assert.True(ok);
            Assert.False(File.Exists(sourceFile));
            Assert.True(File.Exists(destFile));
        }

        [Fact]
        public async Task MoveFileAsync_DifferentExistingDestination_ReplacesCapturedGeneration()
        {
            var sourceFile = Path.Join(_root, "replace-source.mp3");
            var destinationFile = Path.Join(_root, "replace-destination.mp3");
            await File.WriteAllTextAsync(sourceFile, "source generation");
            await File.WriteAllTextAsync(destinationFile, "destination generation");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(moved);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal(
                "source generation",
                await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "destination.previous",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task HardlinkFileAsync_InterruptedDestinationCapture_RecoversDeterministically()
        {
            var sourceFile = Path.Join(_root, "publication-crash-source.mp3");
            var destinationFile = Path.Join(_root, "publication-crash-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source generation");
            await File.WriteAllTextAsync(destinationFile, "destination generation");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterPreparedDestinationCapturedForTestAsync = () =>
                    throw new OperationCanceledException("simulated publication interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.HardlinkFileAsync(sourceFile, destinationFile));
            Assert.False(File.Exists(destinationFile));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.previous",
                SearchOption.AllDirectories));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .HardlinkFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.Equal("source generation", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "destination.previous",
                SearchOption.AllDirectories));
            var remainingPublicationState = Directory.EnumerateDirectories(
                _root,
                ".listenarr-file-publication-*.state",
                SearchOption.TopDirectoryOnly).ToList();
            Assert.True(
                remainingPublicationState.Count == 0,
                string.Join(
                    Environment.NewLine,
                    remainingPublicationState.Select(directory =>
                        $"{directory}: {string.Join(", ", Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName))}")));
        }

        [Fact]
        public async Task HardlinkFileAsync_CrashAfterPreparedClaimMoveBeforeEvidence_RecoversOnRetry()
        {
            var sourceFile = Path.Join(
                _root,
                "publication-claim-evidence-gap-source.mp3");
            var destinationFile = Path.Join(
                _root,
                "publication-claim-evidence-gap-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source generation");
            await File.WriteAllTextAsync(destinationFile, "destination generation");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterPreparedClaimMovedBeforeEvidenceForTestAsync = () =>
                    throw new OperationCanceledException(
                        "simulated crash after prepared claim move")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.HardlinkFileAsync(
                    sourceFile,
                    destinationFile));
            Assert.False(File.Exists(destinationFile));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "prepared.claim",
                SearchOption.AllDirectories));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .HardlinkFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.Equal(
                "source generation",
                await File.ReadAllTextAsync(destinationFile));
            Assert.Equal(
                "source generation",
                await File.ReadAllTextAsync(sourceFile));
            Assert.Empty(Directory.EnumerateDirectories(
                _root,
                ".listenarr-file-publication-*.state",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task HardlinkFileAsync_CrashAfterCommitBeforePublication_RecoversOnRetry()
        {
            var sourceFile = Path.Join(
                _root,
                "publication-postcommit-source.mp3");
            var destinationFile = Path.Join(
                _root,
                "publication-postcommit-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source generation");
            await File.WriteAllTextAsync(destinationFile, "destination generation");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterPreparedPublicationCommittedForTestAsync = () =>
                    throw new OperationCanceledException(
                        "simulated crash after publication commit")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.HardlinkFileAsync(
                    sourceFile,
                    destinationFile));
            Assert.False(File.Exists(destinationFile));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "prepared.claim",
                SearchOption.AllDirectories));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.published.claim",
                SearchOption.AllDirectories));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .HardlinkFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.Equal(
                "source generation",
                await File.ReadAllTextAsync(destinationFile));
            Assert.Equal(
                "source generation",
                await File.ReadAllTextAsync(sourceFile));
            Assert.Empty(Directory.EnumerateDirectories(
                _root,
                ".listenarr-file-publication-*.state",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task HardlinkFileAsync_CrashAfterPublicationBeforeGenerationEvidence_RecoversOnRetry()
        {
            var sourceFile = Path.Join(
                _root,
                "publication-postpublish-source.mp3");
            var destinationFile = Path.Join(
                _root,
                "publication-postpublish-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source generation");
            await File.WriteAllTextAsync(destinationFile, "destination generation");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterPreparedDestinationPublishedForTestAsync = () =>
                    throw new OperationCanceledException(
                        "simulated crash after prepared publication")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.HardlinkFileAsync(
                    sourceFile,
                    destinationFile));
            Assert.Equal(
                "source generation",
                await File.ReadAllTextAsync(destinationFile));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.published.claim",
                SearchOption.AllDirectories));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .HardlinkFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.Equal(
                "source generation",
                await File.ReadAllTextAsync(destinationFile));
            Assert.Equal(
                "source generation",
                await File.ReadAllTextAsync(sourceFile));
            Assert.Empty(Directory.EnumerateDirectories(
                _root,
                ".listenarr-file-publication-*.state",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task HardlinkFileAsync_PrecommitPreviousReplaced_PreservesReplacementAndBlocks()
        {
            var sourceFile = Path.Join(_root, "publication-precommit-previous-source.mp3");
            var destinationFile = Path.Join(_root, "publication-precommit-previous-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source generation");
            await File.WriteAllTextAsync(destinationFile, "destination generation");
            string? stateDirectory = null;
            string? displacedPrevious = null;
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterPreparedDestinationCapturedForTestAsync = () =>
                {
                    stateDirectory = Assert.Single(Directory.EnumerateDirectories(
                        _root,
                        ".listenarr-file-publication-*.state",
                        SearchOption.TopDirectoryOnly));
                    var previous = Path.Join(stateDirectory, "destination.previous");
                    displacedPrevious = Path.Join(_root, "original-precommit-previous.mp3");
                    File.Move(previous, displacedPrevious);
                    File.WriteAllText(previous, "replacement previous");
                    throw new OperationCanceledException("simulated precommit previous replacement");
                }
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.HardlinkFileAsync(sourceFile, destinationFile));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .HardlinkFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(destinationFile));
            Assert.NotNull(stateDirectory);
            Assert.Equal(
                "replacement previous",
                await File.ReadAllTextAsync(Path.Join(stateDirectory!, "destination.previous")));
            Assert.NotNull(displacedPrevious);
            Assert.Equal("destination generation", await File.ReadAllTextAsync(displacedPrevious!));
            Assert.Equal("source generation", await File.ReadAllTextAsync(sourceFile));
        }

        [Fact]
        public async Task HardlinkFileAsync_PrecommitPreparedClaimReplaced_PreservesReplacementAndBlocks()
        {
            var sourceFile = Path.Join(_root, "publication-precommit-prepared-source.mp3");
            var destinationFile = Path.Join(_root, "publication-precommit-prepared-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source generation");
            await File.WriteAllTextAsync(destinationFile, "destination generation");
            string? stateDirectory = null;
            string? displacedPrepared = null;
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterPreparedClaimPublishedForTestAsync = () =>
                {
                    stateDirectory = Assert.Single(Directory.EnumerateDirectories(
                        _root,
                        ".listenarr-file-publication-*.state",
                        SearchOption.TopDirectoryOnly));
                    var prepared = Path.Join(stateDirectory, "prepared.claim");
                    displacedPrepared = Path.Join(_root, "original-precommit-prepared.mp3");
                    File.Move(prepared, displacedPrepared);
                    File.WriteAllText(prepared, "replacement prepared");
                    throw new OperationCanceledException("simulated precommit prepared replacement");
                }
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.HardlinkFileAsync(sourceFile, destinationFile));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .HardlinkFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(destinationFile));
            Assert.NotNull(stateDirectory);
            Assert.Equal(
                "replacement prepared",
                await File.ReadAllTextAsync(Path.Join(stateDirectory!, "prepared.claim")));
            Assert.NotNull(displacedPrepared);
            Assert.Equal("source generation", await File.ReadAllTextAsync(displacedPrepared!));
            Assert.Equal("source generation", await File.ReadAllTextAsync(sourceFile));
        }

        [Fact]
        public async Task HardlinkFileAsync_CommittedPreparedClaimReplaced_PreservesReplacementAndBlocks()
        {
            var sourceFile = Path.Join(_root, "publication-committed-prepared-source.mp3");
            var destinationFile = Path.Join(_root, "publication-committed-prepared-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source generation");
            await File.WriteAllTextAsync(destinationFile, "destination generation");
            string? stateDirectory = null;
            string? displacedPrepared = null;
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterPreparedPublicationCommittedForTestAsync = () =>
                {
                    stateDirectory = Assert.Single(Directory.EnumerateDirectories(
                        _root,
                        ".listenarr-file-publication-*.state",
                        SearchOption.TopDirectoryOnly));
                    var prepared = Path.Join(stateDirectory, "prepared.claim");
                    displacedPrepared = Path.Join(_root, "original-committed-prepared.mp3");
                    File.Move(prepared, displacedPrepared);
                    File.WriteAllText(prepared, "replacement committed prepared");
                    throw new OperationCanceledException("simulated committed prepared replacement");
                }
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.HardlinkFileAsync(sourceFile, destinationFile));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .HardlinkFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(destinationFile));
            Assert.NotNull(stateDirectory);
            Assert.Equal(
                "replacement committed prepared",
                await File.ReadAllTextAsync(Path.Join(stateDirectory!, "prepared.claim")));
            Assert.NotNull(displacedPrepared);
            Assert.Equal("source generation", await File.ReadAllTextAsync(displacedPrepared!));
            Assert.Equal("source generation", await File.ReadAllTextAsync(sourceFile));
        }

        [Fact]
        public async Task HardlinkFileAsync_CommittedPreviousReplaced_PreservesReplacementAndBlocks()
        {
            var sourceFile = Path.Join(_root, "publication-committed-previous-source.mp3");
            var destinationFile = Path.Join(_root, "publication-committed-previous-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source generation");
            await File.WriteAllTextAsync(destinationFile, "destination generation");
            string? stateDirectory = null;
            string? displacedPrevious = null;
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterPreparedPublicationCommittedForTestAsync = () =>
                {
                    stateDirectory = Assert.Single(Directory.EnumerateDirectories(
                        _root,
                        ".listenarr-file-publication-*.state",
                        SearchOption.TopDirectoryOnly));
                    var previous = Path.Join(stateDirectory, "destination.previous");
                    displacedPrevious = Path.Join(_root, "original-committed-previous.mp3");
                    File.Move(previous, displacedPrevious);
                    File.WriteAllText(previous, "replacement committed previous");
                    throw new OperationCanceledException("simulated committed previous replacement");
                }
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.HardlinkFileAsync(sourceFile, destinationFile));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .HardlinkFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(destinationFile));
            Assert.NotNull(stateDirectory);
            Assert.Equal(
                "replacement committed previous",
                await File.ReadAllTextAsync(Path.Join(stateDirectory!, "destination.previous")));
            Assert.NotNull(displacedPrevious);
            Assert.Equal("destination generation", await File.ReadAllTextAsync(displacedPrevious!));
            Assert.Equal("source generation", await File.ReadAllTextAsync(sourceFile));
        }

        [Fact]
        public async Task HardlinkFileAsync_InterruptedBeforeCommitFence_RollsBackAndRetries()
        {
            var sourceFile = Path.Join(
                _root,
                "publication-precommit-source.mp3");
            var destinationFile = Path.Join(
                _root,
                "publication-precommit-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source generation");
            await File.WriteAllTextAsync(
                destinationFile,
                "destination generation");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterPreparedDestinationCapturedForTestAsync = () =>
                    throw new OperationCanceledException(
                        "simulated publication interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.HardlinkFileAsync(
                    sourceFile,
                    destinationFile));
            Assert.False(File.Exists(destinationFile));
            var stateDirectory = Assert.Single(Directory.EnumerateDirectories(
                _root,
                ".listenarr-file-publication-*.state",
                SearchOption.TopDirectoryOnly));
            Assert.Single(Directory.EnumerateFiles(
                stateDirectory,
                "destination.previous",
                SearchOption.TopDirectoryOnly));
            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .HardlinkFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.Equal(
                "source generation",
                await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateDirectories(
                _root,
                ".listenarr-file-publication-*.state",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task HardlinkFileAsync_ReplacementAfterInterruptedCapture_IsPreservedAndBlocked()
        {
            var sourceFile = Path.Join(_root, "publication-conflict-source.mp3");
            var destinationFile = Path.Join(_root, "publication-conflict-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "source generation");
            await File.WriteAllTextAsync(destinationFile, "destination generation");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterPreparedDestinationCapturedForTestAsync = () =>
                    throw new OperationCanceledException("simulated publication interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.HardlinkFileAsync(sourceFile, destinationFile));
            await File.WriteAllTextAsync(destinationFile, "replacement generation");

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .HardlinkFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.Equal("source generation", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("replacement generation", await File.ReadAllTextAsync(destinationFile));
            var previous = Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.previous",
                SearchOption.AllDirectories));
            Assert.Equal("destination generation", await File.ReadAllTextAsync(previous));
        }

        [Fact]
        public async Task MoveDirectoryAsync_UnattributedCleanupArtifactIsPreservedAndBlocked()
        {
            var source = Path.Join(_root, "interrupted-cleanup-source");
            var destination = Path.Join(_root, "interrupted-cleanup-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var quarantine = Path.Join(
                source,
                $"book.m4b.listenarr-copy-cleanup-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(quarantine, "audio");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            var moved = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(moved);
            Assert.True(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(quarantine));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        }

        [Fact]
        public async Task MoveDirectoryAsync_ConflictingInterruptedCleanupIsPreservedAndBlocked()
        {
            var source = Path.Join(_root, "conflicting-cleanup-source");
            var destination = Path.Join(_root, "conflicting-cleanup-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var original = Path.Join(source, "book.m4b");
            var quarantine = $"{original}.listenarr-copy-cleanup-{Guid.NewGuid():N}";
            await File.WriteAllTextAsync(original, "new content");
            await File.WriteAllTextAsync(quarantine, "original content");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            var moved = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(moved);
            Assert.Equal("new content", await File.ReadAllTextAsync(original));
            Assert.Equal("original content", await File.ReadAllTextAsync(quarantine));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_UnexpectedSourceContentIsPreserved()
        {
            var source = Path.Join(_root, "cleanup-source");
            var destination = Path.Join(_root, "cleanup-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            await File.WriteAllTextAsync(Path.Join(destination, "book.m4b"), "audio");
            await File.WriteAllTextAsync(Path.Join(source, "arrived-late.txt"), "preserve");
            var mover = new FileMover(new NullLogger<FileMover>());

            var cleanup = await mover.CleanupCopiedSourceTreeAsync(
                source,
                destination);

            Assert.False(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(source, "arrived-late.txt")));
            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(destination, "book.m4b")));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_ContentArrivingAfterVerificationIsPreserved()
        {
            var source = Path.Join(_root, "cleanup-late-source");
            var destination = Path.Join(_root, "cleanup-late-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            await File.WriteAllTextAsync(Path.Join(destination, "book.m4b"), "audio");
            var mover = new FileMover(new NullLogger<FileMover>());

            var cleanup = await mover.CleanupCopiedSourceTreeAsync(
                source,
                destination,
                () => File.WriteAllText(
                    Path.Join(source, "arrived-late.txt"),
                    "preserve"));

            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(source, "arrived-late.txt")));
            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(destination, "book.m4b")));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_SourceChangeAfterVerificationIsRestored()
        {
            var source = Path.Join(_root, "cleanup-changed-source");
            var destination = Path.Join(_root, "cleanup-changed-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var sourceFile = Path.Join(source, "book.m4b");
            var destinationFile = Path.Join(destination, "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(new NullLogger<FileMover>());

            var cleanup = await mover.CleanupCopiedSourceTreeAsync(
                source,
                destination,
                () => File.WriteAllText(sourceFile, "changed"));

            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            Assert.Equal("changed", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                source,
                "*.listenarr-copy-cleanup-*",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_DestinationChangeAfterPinning_PreservesSourceBytes()
        {
            var source = Path.Join(_root, "cleanup-destination-race-source");
            var destination = Path.Join(_root, "cleanup-destination-race-target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var sourceFile = Path.Join(source, "book.m4b");
            var destinationFile = Path.Join(destination, "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "original audio");
            await File.WriteAllTextAsync(destinationFile, "original audio");
            var hookCalls = 0;
            var mover = new FileMover(new NullLogger<FileMover>())
            {
                AfterCleanupDestinationPinnedForTestAsync = relativePath =>
                {
                    Assert.Equal("book.m4b", relativePath);
                    Interlocked.Increment(ref hookCalls);
                    File.WriteAllText(destinationFile, "replacement audio");
                    return Task.CompletedTask;
                }
            };

            var cleanup = await mover.CleanupCopiedSourceTreeAsync(
                source,
                destination);

            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            Assert.Equal(1, hookCalls);
            Assert.Equal(
                "replacement audio",
                await File.ReadAllTextAsync(destinationFile));
            var quarantine = Assert.Single(Directory.EnumerateDirectories(
                _root,
                ".listenarr-copy-cleanup-*.state",
                SearchOption.TopDirectoryOnly));
            Assert.Equal(
                "original audio",
                await File.ReadAllTextAsync(Path.Join(quarantine, "book.m4b")));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                ".listenarr-copy-cleanup-*.journal",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_VersionOneJournal_UpgradesAndRecovers()
        {
            var source = Path.Join(_root, "cleanup-v1-source");
            var destination = Path.Join(_root, "cleanup-v1-target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(
                Path.Join(source, "book.m4b"),
                "legacy journal audio");
            await File.WriteAllTextAsync(
                Path.Join(destination, "book.m4b"),
                "legacy journal audio");
            var interrupted = new FileMover(new NullLogger<FileMover>())
            {
                DirectoryCleanupJournalVersionForTest = 1,
                AfterCleanupDestinationPinnedForTestAsync = _ =>
                    throw new IOException(
                        "Simulated interruption after legacy journal publication.")
            };

            var cleanup = await interrupted.CleanupCopiedSourceTreeAsync(
                source,
                destination);

            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            Assert.False(Directory.Exists(source));
            Assert.Single(Directory.EnumerateDirectories(
                _root,
                ".listenarr-copy-cleanup-*.state",
                SearchOption.TopDirectoryOnly));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                ".listenarr-copy-cleanup-*.journal",
                SearchOption.TopDirectoryOnly));

            var recovered = new FileMover(new NullLogger<FileMover>())
                .TryRecoverInterruptedCopiedSourceCleanup(
                    source,
                    out var recoveryReason);

            Assert.True(recovered, recoveryReason);
            Assert.False(Directory.Exists(source));
            Assert.Equal(
                "legacy journal audio",
                await File.ReadAllTextAsync(
                    Path.Join(destination, "book.m4b")));
            Assert.Empty(Directory.EnumerateDirectories(
                _root,
                ".listenarr-copy-cleanup-*.state",
                SearchOption.TopDirectoryOnly));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                ".listenarr-copy-cleanup-*.journal",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_JournalReplacedAfterQuarantineRetirement_PreservesReplacement()
        {
            var source = Path.Join(_root, "cleanup-journal-replacement-source");
            var destination = Path.Join(_root, "cleanup-journal-replacement-target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            await File.WriteAllTextAsync(Path.Join(destination, "book.m4b"), "audio");
            var interrupted = new FileMover(new NullLogger<FileMover>())
            {
                AfterCleanupDestinationPinnedForTestAsync = _ =>
                    throw new IOException("simulated cleanup interruption")
            };

            var cleanup = await interrupted.CleanupCopiedSourceTreeAsync(
                source,
                destination);
            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            var journalPath = Assert.Single(Directory.EnumerateFiles(
                _root,
                ".listenarr-copy-cleanup-*.journal",
                SearchOption.TopDirectoryOnly));
            var displacedJournalPath = journalPath + ".displaced";
            var recovery = new FileMover(new NullLogger<FileMover>())
            {
                AfterCleanupQuarantineRetiredForTest = pinnedJournalPath =>
                {
                    Assert.Equal(journalPath, pinnedJournalPath);
                    File.Move(journalPath, displacedJournalPath);
                    File.WriteAllText(journalPath, "replacement journal");
                }
            };

            var recovered = recovery.TryRecoverInterruptedCopiedSourceCleanup(
                source,
                out var recoveryReason);

            Assert.False(recovered);
            Assert.Contains("recovery failed", recoveryReason, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(journalPath));
            Assert.Equal("replacement journal", await File.ReadAllTextAsync(journalPath));
            Assert.True(File.Exists(displacedJournalPath));
            Assert.False(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(destination, "book.m4b")));
        }

        [WindowsFact]
        public async Task CleanupCopiedSourceTreeAsync_ForeignPersistedJournalAliases_FailClosed()
        {
            var source = Path.Join(_root, "cleanup-foreign-journal-source");
            var destination = Path.Join(_root, "cleanup-foreign-journal-target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            await File.WriteAllTextAsync(Path.Join(destination, "book.m4b"), "audio");
            var interrupted = new FileMover(new NullLogger<FileMover>())
            {
                AfterCleanupDestinationPinnedForTestAsync = _ =>
                    throw new IOException("simulated cleanup interruption")
            };

            var cleanup = await interrupted.CleanupCopiedSourceTreeAsync(
                source,
                destination);
            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            var journalPath = Assert.Single(Directory.EnumerateFiles(
                _root,
                ".listenarr-copy-cleanup-*.journal",
                SearchOption.TopDirectoryOnly));
            var quarantine = Assert.Single(Directory.EnumerateDirectories(
                _root,
                ".listenarr-copy-cleanup-*.state",
                SearchOption.TopDirectoryOnly));
            await RewriteJournalPathsAsAmbiguousWindowsAliasesAsync(
                journalPath,
                "SourceRoot",
                "DestinationRoot");

            var recovered = new FileMover(new NullLogger<FileMover>())
                .TryRecoverInterruptedCopiedSourceCleanup(
                    source,
                    out var recoveryReason);

            Assert.False(recovered);
            Assert.Contains("unavailable", recoveryReason, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(journalPath));
            Assert.True(Directory.Exists(quarantine));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(destination, "book.m4b")));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_DisplacedLegacyJournal_IsRestoredAndRecovered()
        {
            var source = Path.Join(_root, "cleanup-v1-displaced-source");
            var destination = Path.Join(_root, "cleanup-v1-displaced-target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(
                Path.Join(source, "book.m4b"),
                "legacy displaced journal audio");
            await File.WriteAllTextAsync(
                Path.Join(destination, "book.m4b"),
                "legacy displaced journal audio");
            var interrupted = new FileMover(new NullLogger<FileMover>())
            {
                DirectoryCleanupJournalVersionForTest = 1,
                AfterCleanupDestinationPinnedForTestAsync = _ =>
                    throw new IOException(
                        "Simulated interruption after legacy journal publication.")
            };

            var cleanup = await interrupted.CleanupCopiedSourceTreeAsync(
                source,
                destination);

            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            var journal = Assert.Single(Directory.EnumerateFiles(
                _root,
                ".listenarr-copy-cleanup-*.journal",
                SearchOption.TopDirectoryOnly));
            var backup = Path.Join(
                _root,
                PinnedDirectoryCreation.GetConditionalReplacementBackupName(
                    Path.GetFileName(journal)));
            File.Move(journal, backup);
            Assert.False(File.Exists(journal));
            Assert.True(File.Exists(backup));

            var recovered = new FileMover(new NullLogger<FileMover>())
                .TryRecoverInterruptedCopiedSourceCleanup(
                    source,
                    out var recoveryReason);

            Assert.True(recovered, recoveryReason);
            Assert.False(Directory.Exists(source));
            Assert.Equal(
                "legacy displaced journal audio",
                await File.ReadAllTextAsync(
                    Path.Join(destination, "book.m4b")));
            Assert.False(File.Exists(backup));
            Assert.Empty(Directory.EnumerateDirectories(
                _root,
                ".listenarr-copy-cleanup-*.state",
                SearchOption.TopDirectoryOnly));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                ".listenarr-copy-cleanup-*.journal",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_DestinationReplacementAfterSourceRetirement_PreservesSourceBytes()
        {
            var source = Path.Join(_root, "cleanup-post-delete-source");
            var destination = Path.Join(_root, "cleanup-post-delete-target");
            var retiredDestination = Path.Join(_root, "cleanup-post-delete-original.m4b");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var sourceFile = Path.Join(source, "book.m4b");
            var destinationFile = Path.Join(destination, "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "original audio");
            await File.WriteAllTextAsync(destinationFile, "original audio");
            var replacementSucceeded = false;
            var mover = new FileMover(new NullLogger<FileMover>())
            {
                AfterCleanupSourceFileRetiredForTestAsync = relativePath =>
                {
                    Assert.Equal("book.m4b", relativePath);
                    File.Move(destinationFile, retiredDestination);
                    File.WriteAllText(destinationFile, "replacement audio");
                    replacementSucceeded = true;
                    return Task.CompletedTask;
                }
            };

            var cleanup = await mover.CleanupCopiedSourceTreeAsync(
                source,
                destination);

            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            Assert.False(Directory.Exists(source));
            Assert.Equal(
                replacementSucceeded ? "replacement audio" : "original audio",
                await File.ReadAllTextAsync(destinationFile));
            if (replacementSucceeded)
            {
                Assert.Equal(
                    "original audio",
                    await File.ReadAllTextAsync(retiredDestination));
            }
            else
            {
                Assert.False(File.Exists(retiredDestination));
            }
            var quarantine = Assert.Single(Directory.EnumerateDirectories(
                _root,
                ".listenarr-copy-cleanup-*.state",
                SearchOption.TopDirectoryOnly));
            var recovery = Assert.Single(Directory.EnumerateFiles(
                quarantine,
                ".listenarr-source-recovery-*.bin",
                SearchOption.TopDirectoryOnly));
            Assert.Equal("original audio", await File.ReadAllTextAsync(recovery));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                ".listenarr-copy-cleanup-*.journal",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_DestinationReplacedAtSourceDelete_PreservesRecoverableGeneration()
        {
            var source = Path.Join(_root, "cleanup-final-delete-race-source");
            var destination = Path.Join(_root, "cleanup-final-delete-race-target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var sourceFile = Path.Join(source, "book.m4b");
            var destinationFile = Path.Join(destination, "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "original audio");
            await File.WriteAllTextAsync(destinationFile, "original audio");
            var replacementSucceeded = false;
            var mover = new FileMover(new NullLogger<FileMover>())
            {
                BeforeCleanupSourceRecoveryDeleteForTestAsync = relativePath =>
                {
                    Assert.Equal("book.m4b", relativePath);
                    File.Delete(destinationFile);
                    File.WriteAllText(destinationFile, "replacement audio");
                    replacementSucceeded = true;
                    return Task.CompletedTask;
                }
            };

            var cleanup = await mover.CleanupCopiedSourceTreeAsync(
                source,
                destination);

            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            Assert.Equal(
                replacementSucceeded ? "replacement audio" : "original audio",
                await File.ReadAllTextAsync(destinationFile));
            var recoverableOriginals = Directory.EnumerateFiles(
                    _root,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path => !string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(destinationFile),
                    StringComparison.OrdinalIgnoreCase))
                .Where(path => File.ReadAllText(path) == "original audio")
                .ToList();
            Assert.NotEmpty(recoverableOriginals);
            Assert.Single(Directory.EnumerateFiles(
                _root,
                ".listenarr-copy-cleanup-*.journal",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_InterruptedAfterSourceDelete_RecoversFromDestinationRetention()
        {
            var source = Path.Join(_root, "cleanup-retention-restart-source");
            var destination = Path.Join(_root, "cleanup-retention-restart-target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(
                Path.Join(source, "book.m4b"),
                "restart audio");
            await File.WriteAllTextAsync(
                Path.Join(destination, "book.m4b"),
                "restart audio");
            var interrupted = new FileMover(new NullLogger<FileMover>())
            {
                AfterCleanupSourceRecoveryDeleteForTestAsync = _ =>
                    throw new IOException(
                        "Simulated interruption after source generation deletion.")
            };

            var cleanup = await interrupted.CleanupCopiedSourceTreeAsync(
                source,
                destination);

            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            Assert.False(Directory.Exists(source));
            Assert.Single(Directory.EnumerateFiles(
                destination,
                ".listenarr-destination-retention-*.bin",
                SearchOption.AllDirectories));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                ".listenarr-copy-cleanup-*.journal",
                SearchOption.TopDirectoryOnly));

            var recovered = new FileMover(new NullLogger<FileMover>())
                .TryRecoverInterruptedCopiedSourceCleanup(
                    source,
                    out var reason);

            Assert.True(recovered, reason);
            Assert.Equal(
                "restart audio",
                await File.ReadAllTextAsync(Path.Join(destination, "book.m4b")));
            Assert.Empty(Directory.EnumerateFiles(
                destination,
                ".listenarr-destination-retention-*.bin",
                SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                ".listenarr-copy-cleanup-*.journal",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_DestinationReplacedAfterSourceDelete_PreservesRetention()
        {
            var source = Path.Join(_root, "cleanup-post-delete-race-source");
            var destination = Path.Join(_root, "cleanup-post-delete-race-target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var sourceFile = Path.Join(source, "book.m4b");
            var destinationFile = Path.Join(destination, "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "original audio");
            await File.WriteAllTextAsync(destinationFile, "original audio");
            var replacementSucceeded = false;
            var mover = new FileMover(new NullLogger<FileMover>())
            {
                AfterCleanupSourceRecoveryDeleteForTestAsync = _ =>
                {
                    try
                    {
                        File.Delete(destinationFile);
                        File.WriteAllText(destinationFile, "replacement audio");
                        replacementSucceeded = true;
                    }
                    catch (IOException)
                    {
                        // Windows holds a non-delete-sharing handle through completion.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Some Windows filesystems report sharing denial as access denied.
                    }

                    return Task.CompletedTask;
                }
            };

            var cleanup = await mover.CleanupCopiedSourceTreeAsync(
                source,
                destination);

            if (!replacementSucceeded)
            {
                Assert.True(cleanup.SourceRemoved);
                Assert.Equal("original audio", await File.ReadAllTextAsync(destinationFile));
                Assert.Empty(Directory.EnumerateFiles(
                    destination,
                    ".listenarr-destination-retention-*.bin",
                    SearchOption.AllDirectories));
            }
            else
            {
                Assert.False(cleanup.SourceRemoved);
                Assert.Equal("replacement audio", await File.ReadAllTextAsync(destinationFile));
                var retention = Assert.Single(Directory.EnumerateFiles(
                    destination,
                    ".listenarr-destination-retention-*.bin",
                    SearchOption.AllDirectories));
                Assert.Equal("original audio", await File.ReadAllTextAsync(retention));
                Assert.Single(Directory.EnumerateFiles(
                    _root,
                    ".listenarr-copy-cleanup-*.journal",
                    SearchOption.TopDirectoryOnly));
            }
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_EqualContentReplacementAfterVerificationIsPreserved()
        {
            var source = Path.Join(_root, "cleanup-replaced-source");
            var destination = Path.Join(_root, "cleanup-replaced-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var sourceFile = Path.Join(source, "book.m4b");
            var originalGeneration = Path.Join(_root, "original-generation.m4b");
            var destinationFile = Path.Join(destination, "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "same audio");
            await File.WriteAllTextAsync(destinationFile, "same audio");
            var mover = new FileMover(new NullLogger<FileMover>());

            var cleanup = await mover.CleanupCopiedSourceTreeAsync(
                source,
                destination,
                () =>
                {
                    File.Move(sourceFile, originalGeneration);
                    File.WriteAllText(sourceFile, "same audio");
                });

            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            Assert.Equal("same audio", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("same audio", await File.ReadAllTextAsync(originalGeneration));
            Assert.Equal("same audio", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_EmptyDirectoryReplacementIsPreserved()
        {
            var source = Path.Join(_root, "cleanup-empty-replaced-source");
            var destination = Path.Join(_root, "cleanup-empty-replaced-destination");
            var originalGeneration = Path.Join(_root, "cleanup-empty-original-generation");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var replacementFile = Path.Join(source, "operator-file.txt");
            var mover = new FileMover(new NullLogger<FileMover>());

            var cleanup = await mover.CleanupCopiedSourceTreeAsync(
                source,
                destination,
                () =>
                {
                    Directory.Move(source, originalGeneration);
                    Directory.CreateDirectory(source);
                    File.WriteAllText(replacementFile, "preserve");
                });

            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            Assert.True(Directory.Exists(originalGeneration));
            Assert.Equal("preserve", await File.ReadAllTextAsync(replacementFile));
        }

        [WindowsFact]
        public async Task MoveDirectoryAsync_VerifiedFallbackFailure_DoesNotInvokeExternalProcess()
        {
            var source = Path.Join(_root, "robocopy-source");
            var dest = Path.Join(_root, "robocopy-destination");
            Directory.CreateDirectory(Path.Join(source, "nested"));
            await File.WriteAllTextAsync(Path.Join(source, "nested", "book.m4b"), "audio");
            var runner = new RecordingProcessRunner(_ =>
            {
                Directory.CreateDirectory(Path.Join(dest, "nested"));
                File.Copy(
                    Path.Join(source, "nested", "book.m4b"),
                    Path.Join(dest, "nested", "book.m4b"));
            });
            var publicationHookRan = false;
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 1,
                    RobocopyTimeoutMs = 1000,
                }),
                new FileSystemSemanticsResolver())
            {
                BeforeDirectoryMoveAttemptForTest = () =>
                    throw new IOException("Force the verified directory fallback."),
                BeforeDirectoryCopyPublicationForTestAsync = _ =>
                {
                    publicationHookRan = true;
                    throw new IOException("Force the verified robocopy fallback.");
                }
            };

            var ok = await mover.MoveDirectoryAsync(source, dest);

            Assert.False(ok);
            Assert.True(publicationHookRan);
            Assert.Null(runner.LastStartInfo);
            Assert.True(File.Exists(Path.Join(source, "nested", "book.m4b")));
            Assert.False(Directory.Exists(dest));
        }

        [WindowsFact]
        public async Task MoveDirectoryAsync_RobocopyFallback_DestinationParentReplacedAfterSafetyPreflight_DoesNotWriteReplacement()
        {
            var source = Path.Join(_root, "robocopy-race-source");
            var destinationParent = Path.Join(_root, "robocopy-race-parent");
            var displacedParent = destinationParent + ".original";
            var destination = Path.Join(destinationParent, "destination");
            var sourceFile = Path.Join(source, "nested", "book.m4b");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            Directory.CreateDirectory(destinationParent);
            await File.WriteAllTextAsync(sourceFile, "audio");
            var runner = new RecordingProcessRunner(startInfo =>
            {
                Directory.Move(destinationParent, displacedParent);
                Directory.CreateDirectory(destinationParent);
                var processDestination = startInfo.ArgumentList[1];
                Directory.CreateDirectory(Path.Join(processDestination, "nested"));
                File.Copy(
                    sourceFile,
                    Path.Join(processDestination, "nested", "book.m4b"));
            });
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 1,
                    RobocopyTimeoutMs = 1000,
                }),
                new FileSystemSemanticsResolver())
            {
                BeforeDirectoryMoveAttemptForTest = () =>
                    throw new IOException("Force the verified directory fallback."),
                BeforeDirectoryCopyPublicationForTestAsync = _ =>
                    throw new IOException("Force the external fallback boundary.")
            };

            var ok = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(ok);
            Assert.Null(runner.LastStartInfo);
            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(displacedParent));
            Assert.True(Directory.Exists(destinationParent));
            Assert.False(Directory.Exists(destination));
        }

        [WindowsFact]
        public async Task MoveDirectoryAsync_ConflictingDestination_DoesNotInvokeRobocopy()
        {
            var source = Path.Join(_root, "robocopy-conflict-source");
            var destination = Path.Join(_root, "robocopy-conflict-destination");
            Directory.CreateDirectory(Path.Join(source, "nested"));
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Join(source, "nested", "book.m4b"), "audio");
            await File.WriteAllTextAsync(Path.Join(destination, "nested"), "foreign");
            var runner = new RecordingProcessRunner(_ => { });
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 1,
                    RobocopyTimeoutMs = 1000,
                }),
                new FileSystemSemanticsResolver());

            var ok = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(ok);
            Assert.Null(runner.LastStartInfo);
            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(source, "nested", "book.m4b")));
            Assert.Equal("foreign", await File.ReadAllTextAsync(
                Path.Join(destination, "nested")));
        }

        [WindowsFact]
        public async Task MoveFileAsync_RobocopyFallback_IsNotInvokedForLockedSource()
        {
            var sourceFile = Path.Join(_root, "robocopy-replaced-source.mp3");
            var destinationFile = Path.Join(_root, "robocopy-replaced-destination.mp3");
            await File.WriteAllTextAsync(sourceFile, "source");
            FileStream? sourceLock = File.Open(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            var runner = new RecordingProcessRunner(startInfo =>
            {
                sourceLock?.Dispose();
                sourceLock = null;
            });
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 0,
                    RobocopyTimeoutMs = 1000,
                }),
                new FileSystemSemanticsResolver());

            try
            {
                var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

                Assert.False(moved);
                Assert.True(File.Exists(sourceFile));
                Assert.False(File.Exists(destinationFile));
                Assert.Null(runner.LastStartInfo);
            }
            finally
            {
                sourceLock?.Dispose();
            }
        }

        [WindowsFact]
        public async Task MoveFileAsync_RobocopyFallback_IsNotInvokedWhenEnabled()
        {
            var sourceFile = Path.Join(_root, "robocopy-source.mp3");
            var destFile = Path.Join(_root, "dest", "renamed-destination.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            await File.WriteAllTextAsync(sourceFile, "content");
            FileStream? sourceLock = File.Open(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            var runner = new RecordingProcessRunner(startInfo =>
            {
                sourceLock?.Dispose();
                sourceLock = null;
                File.Copy(
                    sourceFile,
                    Path.Join(
                        startInfo.ArgumentList[1],
                        startInfo.ArgumentList[2]));
            });
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 0,
                    RobocopyTimeoutMs = 1000,
                }),
                new FileSystemSemanticsResolver());

            try
            {
                var ok = await mover.MoveFileAsync(sourceFile, destFile);

                Assert.False(ok);
                Assert.True(File.Exists(sourceFile));
                Assert.False(File.Exists(destFile));
                Assert.Null(runner.LastStartInfo);
            }
            finally
            {
                sourceLock?.Dispose();
            }
        }

        [WindowsFact]
        public async Task MoveFileAsync_RobocopyExitCodeWithoutStagedFileReportsFailure()
        {
            var sourceFile = Path.Join(_root, "robocopy-unverified-source.mp3");
            var destinationFile = Path.Join(_root, "robocopy-unverified-destination.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");
            FileStream? sourceLock = File.Open(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            var runner = new RecordingProcessRunner(_ =>
            {
                sourceLock?.Dispose();
                sourceLock = null;
            });
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 0,
                    RobocopyTimeoutMs = 1000,
                }),
                new FileSystemSemanticsResolver());

            try
            {
                var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

                Assert.False(moved);
                Assert.True(File.Exists(sourceFile));
                Assert.False(File.Exists(destinationFile));
                Assert.Null(runner.LastStartInfo);
            }
            finally
            {
                sourceLock?.Dispose();
            }
        }

        private static RegistrationPublicationCompletion
            CompletePreparedPublication(
                IAudiobookFileRegistrationLease lease,
                int audiobookId = 1)
        {
            Assert.True(lease.PrepareCleanupRecovery(audiobookId));
            return lease.CompletePublication();
        }

        private sealed class RecordingProcessRunner(
            Action<ProcessStartInfo>? onRun = null) : IProcessRunner
        {
            public ProcessStartInfo? LastStartInfo { get; private set; }

            public Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, int timeoutMs = 60000, System.Threading.CancellationToken cancellationToken = default)
            {
                LastStartInfo = startInfo;
                onRun?.Invoke(startInfo);
                return Task.FromResult(new ProcessResult(1, string.Empty, string.Empty, false));
            }

            public Process StartProcess(ProcessStartInfo startInfo) => throw new NotSupportedException();

            public IDisposable RegisterTransientSensitive(IEnumerable<string> values) => new NoopDisposable();

            private sealed class NoopDisposable : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }
    }
}
