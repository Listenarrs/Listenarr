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
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using Listenarr.Api.Features.Library;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    /// <summary>
    /// Tests for the organize-library preview and apply endpoints. The
    /// preview buckets every audiobook into already_canonical / will_move /
    /// collision / invalid_target; apply queues a per-book move for each
    /// confirmed id through MoveQueueService.
    /// </summary>
    public class LibraryController_OrganizeTests : BaseTests
    {
        private const string Root = "/audiobooks";
        private readonly Mock<IImageCacheService> imageCacheServiceMock = new Mock<IImageCacheService>();

        public override async Task InitializeAsync()
        {
            _services.AddSingleton(imageCacheServiceMock.Object);
            Init();
            await SeedSettingsAndRootAsync();
        }

        private async Task SeedSettingsAndRootAsync()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithFolderNamingPattern("{Author}/{Title}")
                .WithOutputPath(Root)
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Library")
                .WithPath(Root)
                .WithIsDefault()
                .Build());
        }

        /// <summary>
        /// Attach a single tracked AudiobookFile to <paramref name="audiobook"/> so
        /// it survives <c>GetOrganizePreview</c>'s fileless-row skip filter (rows
        /// with zero tracked files are excluded from the preview entirely because
        /// they have nothing on disk to move). Tests that exercise bucket
        /// classification — already_canonical, will_move, collision,
        /// invalid_target — call this so the row reaches the bucketing logic.
        /// </summary>
        private Task AttachFileAsync(Audiobook audiobook, string? path = null)
        {
            return _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(path ?? $"{audiobook.BasePath ?? Root}/dummy.m4b")
                .Build());
        }

        [Fact]
        public async Task Preview_NoLibrary_ReturnsEmptyBuckets()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.GetOrganizePreview(CancellationToken.None) as OkObjectResult;
            Assert.NotNull(actionResult);
            var preview = Assert.IsType<OrganizeLibraryPreviewDto>(actionResult!.Value);
            Assert.Empty(preview.Rows);
            Assert.Equal(0, preview.WillMoveCount);
            Assert.Equal(0, preview.CollisionCount);
        }

        [Fact]
        public async Task Preview_RowAtCanonicalPath_IsAlreadyCanonical()
        {
            var canonicalPath = $"{Root}/Author A/Book One";
            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book One",
                Authors = new List<string> { "Author A" },
                BasePath = canonicalPath,
            });
            await AttachFileAsync(ab);

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizePreviewStatus.AlreadyCanonical, row.Status);
            Assert.Equal(1, preview.AlreadyCanonicalCount);
            Assert.Equal(0, preview.WillMoveCount);
        }

        [Fact]
        public async Task Preview_RowWithDifferentPath_IsWillMove()
        {
            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book Two",
                Authors = new List<string> { "Author B" },
                BasePath = $"{Root}/Misplaced",
            });
            await AttachFileAsync(ab);

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizePreviewStatus.WillMove, row.Status);
            Assert.Equal($"{Root}/Author B/Book Two", row.TargetPath);
            Assert.Equal(1, preview.WillMoveCount);
        }

        [Fact]
        public async Task Preview_TwoRowsTargetingSameFolder_AreCollision()
        {
            // Same {Author}/{Title} → same target path; different ASINs so the
            // existing dedup tool wouldn't catch them.
            var ab1 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Same Title",
                Authors = new List<string> { "Same Author" },
                Asin = "B00COLL0001",
                BasePath = $"{Root}/Old Place A",
            });
            var ab2 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Same Title",
                Authors = new List<string> { "Same Author" },
                Asin = "B00COLL0002",
                BasePath = $"{Root}/Old Place B",
            });
            await AttachFileAsync(ab1);
            await AttachFileAsync(ab2);

            var preview = await GetPreviewAsync();
            Assert.Equal(2, preview.Rows.Count);
            Assert.All(preview.Rows, r => Assert.Equal(OrganizePreviewStatus.Collision, r.Status));
            var key = preview.Rows[0].CollisionKey;
            Assert.False(string.IsNullOrEmpty(key));
            Assert.All(preview.Rows, r => Assert.Equal(key, r.CollisionKey));
            Assert.Equal(2, preview.CollisionCount);
            Assert.Equal(0, preview.WillMoveCount);
        }

        [Fact]
        public async Task Preview_RowWithNoFilesAndNoBasePath_IsSkipped()
        {
            // Monitored-but-not-downloaded record: tracked in the library,
            // but nothing on disk and no destination set. Including it in
            // will_move would cause an "Source path does not exist" failure
            // when the user applies the default selection.
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Wishlisted Book",
                Authors = new List<string> { "Author M" },
                BasePath = null,
            });

            var preview = await GetPreviewAsync();
            Assert.Empty(preview.Rows);
            Assert.Equal(0, preview.WillMoveCount);
            Assert.Equal(0, preview.AlreadyCanonicalCount);
            Assert.Equal(0, preview.CollisionCount);
            Assert.Equal(0, preview.InvalidTargetCount);
        }

        [Fact]
        public async Task Preview_RowWithNoFilesButBasePathStamped_IsSkipped()
        {
            // Live-data variant: an earlier ingestion path stamped some
            // wishlist records' BasePath at the library root ("/audiobooks")
            // even though no files ever arrived. The narrow filter — empty
            // BasePath AND zero files — let these through; they appeared in
            // will_move, got default-selected, and each failed the move
            // queue's source-path-exists check. The loosened filter drops
            // every fileless row regardless of BasePath value.
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Wishlisted Book",
                Authors = new List<string> { "Author N" },
                BasePath = Root, // i.e. "/audiobooks" — stamped, but no file ever landed
            });

            var preview = await GetPreviewAsync();
            Assert.Empty(preview.Rows);
            Assert.Equal(0, preview.WillMoveCount);
        }

        [Fact]
        public async Task Preview_MissingAuthor_IsInvalidTarget()
        {
            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Orphan Book",
                Authors = new List<string>(),
                BasePath = $"{Root}/Somewhere",
            });
            await AttachFileAsync(ab);

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizePreviewStatus.InvalidTarget, row.Status);
            Assert.Equal("Missing author", row.Reason);
            Assert.Equal(OrganizeInvalidReasonCode.MissingAuthor, row.ReasonCode);
            Assert.Null(row.TargetPath);
            Assert.Equal(1, preview.InvalidTargetCount);
        }

        [Fact]
        public async Task Preview_MissingTitle_IsInvalidTarget()
        {
            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = null,
                Authors = new List<string> { "Author C" },
                BasePath = $"{Root}/Somewhere",
            });
            await AttachFileAsync(ab);

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizePreviewStatus.InvalidTarget, row.Status);
            Assert.Equal("Missing title", row.Reason);
            Assert.Equal(OrganizeInvalidReasonCode.MissingTitle, row.ReasonCode);
        }

        [Fact]
        public async Task Apply_EmptyBody_ReturnsBadRequest()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.ApplyOrganize(new OrganizeLibraryApplyRequest(), CancellationToken.None);
            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task Apply_MissingIds_ReturnsBadRequestWithoutQueuing()
        {
            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Existing Book",
                Authors = new List<string> { "Real Author" },
                BasePath = $"{Root}/Somewhere",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.ApplyOrganize(new OrganizeLibraryApplyRequest
            {
                AudiobookIds = new List<int> { ab.Id, 9999 },
            }, CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
            var jobs = await _moveJobRepository.GetByStatusAsync(new[] { "Queued", "Processing" });
            Assert.Empty(jobs);
        }

        [Fact]
        public async Task Apply_QueuesMovesForWillMoveRows()
        {
            var ab1 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Me",
                Authors = new List<string> { "Author X" },
                BasePath = $"{Root}/Misplaced",
            });
            var ab2 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Me Too",
                Authors = new List<string> { "Author Y" },
                BasePath = $"{Root}/Other",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.ApplyOrganize(new OrganizeLibraryApplyRequest
            {
                AudiobookIds = new List<int> { ab1.Id, ab2.Id },
            }, CancellationToken.None) as OkObjectResult;

            Assert.NotNull(actionResult);
            var result = Assert.IsType<OrganizeLibraryApplyResultDto>(actionResult!.Value);
            Assert.Equal(2, result.Queued);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(0, result.FailedToQueue);
            Assert.Equal(2, result.QueuedJobs.Count);
            Assert.Contains(result.QueuedJobs, j => j.AudiobookId == ab1.Id && j.TargetPath == $"{Root}/Author X/Move Me");
            Assert.Contains(result.QueuedJobs, j => j.AudiobookId == ab2.Id && j.TargetPath == $"{Root}/Author Y/Move Me Too");

            var jobs = await _moveJobRepository.GetByStatusAsync(new[] { "Queued", "Processing" });
            Assert.Equal(2, jobs.Count);
            Assert.Contains(jobs, j => j.AudiobookId == ab1.Id && j.RequestedPath == $"{Root}/Author X/Move Me");
            Assert.Contains(jobs, j => j.AudiobookId == ab2.Id && j.RequestedPath == $"{Root}/Author Y/Move Me Too");
        }

        [Fact]
        public async Task Apply_SkipsRowsAlreadyAtCanonicalPath()
        {
            // User confirmed an id that — between preview and apply —
            // got moved to its target by something else. Apply should
            // skip with a warning, not enqueue a no-op.
            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Already Done",
                Authors = new List<string> { "Author Z" },
                BasePath = $"{Root}/Author Z/Already Done",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.ApplyOrganize(new OrganizeLibraryApplyRequest
            {
                AudiobookIds = new List<int> { ab.Id },
            }, CancellationToken.None) as OkObjectResult;

            Assert.NotNull(actionResult);
            var result = Assert.IsType<OrganizeLibraryApplyResultDto>(actionResult!.Value);
            Assert.Equal(0, result.Queued);
            Assert.Equal(1, result.Skipped);
            Assert.Single(result.SkippedDetails, s => s.AudiobookId == ab.Id);

            var jobs = await _moveJobRepository.GetByStatusAsync(new[] { "Queued", "Processing" });
            Assert.Empty(jobs);
        }

        [Fact]
        public async Task Apply_SkipsCollidingIdsInSameRequest()
        {
            var ab1 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Conflict",
                Authors = new List<string> { "Author Q" },
                BasePath = $"{Root}/Old A",
            });
            var ab2 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Conflict",
                Authors = new List<string> { "Author Q" },
                BasePath = $"{Root}/Old B",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.ApplyOrganize(new OrganizeLibraryApplyRequest
            {
                AudiobookIds = new List<int> { ab1.Id, ab2.Id },
            }, CancellationToken.None) as OkObjectResult;

            Assert.NotNull(actionResult);
            var result = Assert.IsType<OrganizeLibraryApplyResultDto>(actionResult!.Value);
            Assert.Equal(0, result.Queued);
            Assert.Equal(2, result.Skipped);
            Assert.NotEmpty(result.Warnings);

            var jobs = await _moveJobRepository.GetByStatusAsync(new[] { "Queued", "Processing" });
            Assert.Empty(jobs);
        }

        [Fact]
        public async Task Preview_TargetDirectoryOccupied_IsInvalidTarget()
        {
            // Real temp directory tree so the preview can actually stat the target.
            using var tmp = new TempDirectory();
            var root = tmp.Path;
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithFolderNamingPattern("{Author}/{Title}")
                .WithOutputPath(root)
                .Build());
            // Replace the base-seed root (path = "/audiobooks") with one
            // pointing at the temp directory so the preview's filesystem
            // checks actually look at our test fixture.
            foreach (var existing in await _rootFolderRepository.GetAllAsync())
            {
                await _rootFolderRepository.RemoveAsync(existing.Id);
            }
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Library")
                .WithPath(root)
                .WithIsDefault()
                .Build());

            // Audiobook's current path is /<tmp>/Misplaced; target computes to
            // /<tmp>/Author X/Occupied. Pre-populate the target with a foreign
            // AUDIO file so it exists and holds protected content — apply would
            // refuse, so the preview must surface this row as invalid_target
            // rather than will_move. (A non-audio occupant is the replaceable-
            // stub case, covered separately below.)
            var currentPath = Path.Combine(root, "Misplaced");
            Directory.CreateDirectory(currentPath);
            var targetPath = Path.Combine(root, "Author X", "Occupied");
            Directory.CreateDirectory(targetPath);
            await File.WriteAllTextAsync(Path.Combine(targetPath, "foreign.m4b"), "stranger danger");

            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Occupied",
                Authors = new List<string> { "Author X" },
                BasePath = currentPath,
            });
            await AttachFileAsync(ab, $"{currentPath}/dummy.m4b");

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizePreviewStatus.InvalidTarget, row.Status);
            Assert.Contains("already exists", row.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(OrganizeInvalidReasonCode.TargetExists, row.ReasonCode);
            // TargetPath is surfaced on the invalid row so the UI can show the
            // occupied canonical path the operator needs to resolve.
            Assert.Contains("Occupied", row.TargetPath ?? string.Empty);
            Assert.Equal(0, preview.WillMoveCount);
            Assert.Equal(1, preview.InvalidTargetCount);
        }

        [Fact(DisplayName = "Preview: target occupied by metadata-only leftovers nothing references → will_move with replace flag")]
        public async Task Preview_TargetOccupiedByUnreferencedStub_IsWillMoveWithReplaceFlag()
        {
            using var tmp = new TempDirectory();
            var root = tmp.Path;
            await UseTempRootAsync(root);

            var currentPath = Path.Combine(root, "Misplaced");
            Directory.CreateDirectory(currentPath);
            var targetPath = Path.Combine(root, "Author X", "Occupied");
            Directory.CreateDirectory(targetPath);
            await File.WriteAllTextAsync(Path.Combine(targetPath, "Occupied - Author X.opf"), "stale metadata");
            await File.WriteAllTextAsync(Path.Combine(targetPath, "cover.jpg"), "stale cover");

            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Occupied",
                Authors = new List<string> { "Author X" },
                BasePath = currentPath,
            });
            await AttachFileAsync(ab, $"{currentPath}/dummy.m4b");

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizePreviewStatus.WillMove, row.Status);
            Assert.True(row.ReplacesStubTarget);
            Assert.Equal(1, preview.WillMoveCount);
            Assert.Equal(0, preview.InvalidTargetCount);
        }

        [Fact(DisplayName = "Preview: stub target referenced by another record's BasePath → stays invalid_target")]
        public async Task Preview_StubTargetReferencedByOtherBasePath_IsInvalidTarget()
        {
            using var tmp = new TempDirectory();
            var root = tmp.Path;
            await UseTempRootAsync(root);

            var currentPath = Path.Combine(root, "Misplaced");
            Directory.CreateDirectory(currentPath);
            var targetPath = Path.Combine(root, "Author X", "Occupied");
            Directory.CreateDirectory(targetPath);
            await File.WriteAllTextAsync(Path.Combine(targetPath, "cover.jpg"), "stale cover");

            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Occupied",
                Authors = new List<string> { "Author X" },
                BasePath = currentPath,
            });
            await AttachFileAsync(ab, $"{currentPath}/dummy.m4b");

            // Another record claims the target folder as its BasePath — even
            // though the folder holds no audio, replacing it would orphan that
            // record's anchor, so the row must stay invalid.
            var other = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Claimant",
                Authors = new List<string> { "Author Y" },
                BasePath = targetPath,
            });
            await AttachFileAsync(other, $"{targetPath}/claimant.m4b");
            // The claimant's tracked file is DB-only (not on disk) so the
            // target still LOOKS like a stub on disk — the DB check is what
            // must catch it.

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows, r => r.Id == ab.Id);
            Assert.Equal(OrganizePreviewStatus.InvalidTarget, row.Status);
            Assert.Equal(OrganizeInvalidReasonCode.TargetExists, row.ReasonCode);
        }

        [Fact(DisplayName = "Apply: replaceable stub target queues the move (not skipped)")]
        public async Task Apply_StubOccupiedTarget_QueuesMove()
        {
            using var tmp = new TempDirectory();
            var root = tmp.Path;
            await UseTempRootAsync(root);

            var currentPath = Path.Combine(root, "Misplaced");
            Directory.CreateDirectory(currentPath);
            var targetPath = Path.Combine(root, "Author X", "Occupied");
            Directory.CreateDirectory(targetPath);
            await File.WriteAllTextAsync(Path.Combine(targetPath, "leftover.opf"), "stale");

            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Occupied",
                Authors = new List<string> { "Author X" },
                BasePath = currentPath,
            });
            await AttachFileAsync(ab, $"{currentPath}/dummy.m4b");

            var result = await ApplyAsync(new[] { ab.Id });
            Assert.Equal(1, result.Queued);
            Assert.Equal(0, result.Skipped);

            var jobs = await _moveJobRepository.GetByStatusAsync(new[] { "Queued", "Processing" });
            var job = Assert.Single(jobs, j => j.AudiobookId == ab.Id);
            Assert.True(job.ReplaceStubTarget);
        }

        [Fact(DisplayName = "Apply: target occupied by audio is skipped with a reason instead of queuing a doomed job")]
        public async Task Apply_AudioOccupiedTarget_SkipsWithReason()
        {
            using var tmp = new TempDirectory();
            var root = tmp.Path;
            await UseTempRootAsync(root);

            var currentPath = Path.Combine(root, "Misplaced");
            Directory.CreateDirectory(currentPath);
            var targetPath = Path.Combine(root, "Author X", "Occupied");
            Directory.CreateDirectory(targetPath);
            await File.WriteAllTextAsync(Path.Combine(targetPath, "protected.m4b"), "real audio");

            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Occupied",
                Authors = new List<string> { "Author X" },
                BasePath = currentPath,
            });
            await AttachFileAsync(ab, $"{currentPath}/dummy.m4b");

            var result = await ApplyAsync(new[] { ab.Id });
            Assert.Equal(0, result.Queued);
            Assert.Equal(1, result.Skipped);
            Assert.Contains(result.SkippedDetails, d => d.AudiobookId == ab.Id
                && (d.Reason ?? string.Empty).Contains("already exists", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Re-point the seeded root folder + settings at a real temp directory so
        /// the preview's on-disk flatten-feasibility check (added with the
        /// one-click flatten) actually stats the fixture.
        /// </summary>
        private async Task UseTempRootAsync(string root)
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithFolderNamingPattern("{Author}/{Title}")
                .WithOutputPath(root)
                .Build());
            foreach (var existing in await _rootFolderRepository.GetAllAsync())
            {
                await _rootFolderRepository.RemoveAsync(existing.Id);
            }
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Library")
                .WithPath(root)
                .WithIsDefault()
                .Build());
        }

        [Fact]
        public async Task Preview_TargetIsAncestorOfSource_OfferableFlatten()
        {
            // Source /<tmp>/Author Y/Title/Narrator nests one level below its
            // canonical target /<tmp>/Author Y/Title. The target holds nothing
            // but the Narrator subtree, so the flatten is feasible and the
            // preview marks the row CanFlatten.
            using var tmp = new TempDirectory();
            var root = tmp.Path;
            await UseTempRootAsync(root);

            var source = Path.Combine(root, "Author Y", "Title", "Narrator");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "book.m4b"), "audio");

            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Title",
                Authors = new List<string> { "Author Y" },
                BasePath = source,
            });
            await AttachFileAsync(ab, Path.Combine(source, "book.m4b"));

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizePreviewStatus.InvalidTarget, row.Status);
            Assert.Equal(OrganizeInvalidReasonCode.TargetAncestor, row.ReasonCode);
            Assert.Contains("ancestor", row.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(root, "Author Y", "Title"), row.TargetPath);
            Assert.True(row.CanFlatten);
            Assert.Equal(1, preview.InvalidTargetCount);
        }

        [Fact]
        public async Task Preview_TargetAncestor_TargetHasForeignFiles_NotFlattenable()
        {
            // Same nesting, but the canonical target also holds a foreign file
            // directly — flattening would merge into populated content, so the
            // executor would refuse. The preview must not offer the flatten.
            using var tmp = new TempDirectory();
            var root = tmp.Path;
            await UseTempRootAsync(root);

            var target = Path.Combine(root, "Author Y", "Title");
            var source = Path.Combine(target, "Narrator");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "book.m4b"), "audio");
            await File.WriteAllTextAsync(Path.Combine(target, "stray.txt"), "foreign");

            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Title",
                Authors = new List<string> { "Author Y" },
                BasePath = source,
            });
            await AttachFileAsync(ab, Path.Combine(source, "book.m4b"));

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizeInvalidReasonCode.TargetAncestor, row.ReasonCode);
            Assert.False(row.CanFlatten);
            Assert.Equal(1, preview.InvalidTargetCount);
        }

        [Fact]
        public async Task Preview_TargetAncestor_SourceMissingOnDisk_IsSourceMissing()
        {
            // The record points at a nested path that no longer exists on disk
            // (an orphaned record). It must surface as its own source_missing
            // case — not a flatten-able nested row — and never offer the flatten.
            using var tmp = new TempDirectory();
            var root = tmp.Path;
            await UseTempRootAsync(root);

            // Note: the nested source directory is deliberately NOT created.
            var source = Path.Combine(root, "Author Y", "Title", "Narrator");

            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Title",
                Authors = new List<string> { "Author Y" },
                BasePath = source,
            });
            await AttachFileAsync(ab, Path.Combine(source, "book.m4b"));

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizePreviewStatus.InvalidTarget, row.Status);
            Assert.Equal(OrganizeInvalidReasonCode.SourceMissing, row.ReasonCode);
            Assert.False(row.CanFlatten);
            Assert.Equal(1, preview.InvalidTargetCount);
        }

        [Fact]
        public async Task Preview_TargetEqualsCurrentAndExists_IsAlreadyCanonical()
        {
            // Regression guard: the new target-occupied check must NOT fire
            // for the already_canonical case, where the target on disk is
            // populated with the audiobook's OWN files.
            using var tmp = new TempDirectory();
            var root = tmp.Path;
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithFolderNamingPattern("{Author}/{Title}")
                .WithOutputPath(root)
                .Build());
            // Replace the base-seed root (path = "/audiobooks") with one
            // pointing at the temp directory so the preview's filesystem
            // checks actually look at our test fixture.
            foreach (var existing in await _rootFolderRepository.GetAllAsync())
            {
                await _rootFolderRepository.RemoveAsync(existing.Id);
            }
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Library")
                .WithPath(root)
                .WithIsDefault()
                .Build());

            var canonical = Path.Combine(root, "Author Z", "Settled");
            Directory.CreateDirectory(canonical);
            await File.WriteAllTextAsync(Path.Combine(canonical, "book.m4b"), "real audiobook content");

            var ab = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Settled",
                Authors = new List<string> { "Author Z" },
                BasePath = canonical,
            });
            await AttachFileAsync(ab, $"{canonical}/book.m4b");

            var preview = await GetPreviewAsync();
            var row = Assert.Single(preview.Rows);
            Assert.Equal(OrganizePreviewStatus.AlreadyCanonical, row.Status);
            Assert.Equal(1, preview.AlreadyCanonicalCount);
            Assert.Equal(0, preview.InvalidTargetCount);
        }

        private async Task<OrganizeLibraryPreviewDto> GetPreviewAsync()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.GetOrganizePreview(CancellationToken.None) as OkObjectResult;
            Assert.NotNull(actionResult);
            return Assert.IsType<OrganizeLibraryPreviewDto>(actionResult!.Value);
        }

        private async Task<OrganizeLibraryApplyResultDto> ApplyAsync(IEnumerable<int> ids)
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.ApplyOrganize(new OrganizeLibraryApplyRequest
            {
                AudiobookIds = ids.ToList(),
            }, CancellationToken.None) as OkObjectResult;
            Assert.NotNull(actionResult);
            return Assert.IsType<OrganizeLibraryApplyResultDto>(actionResult!.Value);
        }

        private sealed class TempDirectory : IDisposable
        {
            public string Path { get; }
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"listenarr-organize-tests-{Guid.NewGuid():N}");
                Directory.CreateDirectory(Path);
            }
            public void Dispose()
            {
                try { Directory.Delete(Path, recursive: true); }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    /* Best effort — test cleanup. */
                }
            }
        }
    }
}
