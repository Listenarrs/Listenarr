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
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Utils
{
    public class FileUtilsTests
    {
        [Fact]
        public void GetUniqueDestinationPath_ReturnsSameIfNotExists()
        {
            var tmp = Path.Join(Path.GetTempPath(), $"fu-test-{Guid.NewGuid()}.txt");
            // Ensure it does not exist
            if (File.Exists(tmp)) File.Delete(tmp);

            var result = FileUtils.GetUniqueDestinationPath(tmp, File.Exists);
            Assert.Equal(tmp, result);
        }

        [Fact]
        public void GetUniqueDestinationPath_AppendsSuffixWhenExists()
        {
            var dir = Path.Join(Path.GetTempPath(), "fu-dir-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            var file = Path.Join(dir, "file.txt");
            File.WriteAllText(file, "x");

            var result = FileUtils.GetUniqueDestinationPath(file, File.Exists);
            Assert.NotEqual(file, result);
            Assert.StartsWith(Path.Join(dir, "file ("), result);

            // cleanup
            try { Directory.Delete(dir, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        [Fact]
        public void GetUniqueDestinationPath_RespectsInMemoryUsedSet()
        {
            var dir = Path.Join(Path.GetTempPath(), "fu-dir-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            var desired = Path.Join(dir, "dup.mp3");
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { desired };

            var result = FileUtils.GetUniqueDestinationPath(desired, File.Exists, used);
            Assert.NotEqual(desired, result);
            Assert.Contains("dup (", result);

            try { Directory.Delete(dir, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        [Fact]
        public void GetUniqueDestinationPath_UsesCustomExistsPredicate()
        {
            var tmp = Path.Join(Path.GetTempPath(), "fu-test-" + Guid.NewGuid() + ".bin");
            // pretend only the original path exists by using a predicate that returns true
            // only for the original desired path. This ensures the generator can find a
            // candidate that does not exist according to the predicate.
            bool ExistsPredicate(string p) => string.Equals(p, tmp, StringComparison.OrdinalIgnoreCase);

            var result = FileUtils.GetUniqueDestinationPath(tmp, ExistsPredicate, null);
            Assert.NotEqual(tmp, result);
            Assert.Contains(" (1)", result);
        }

        [Fact]
        public void GetUniqueDestinationPath_LongName_AppendsSuffix()
        {
            var dir = Path.Join(Path.GetTempPath(), "fu-long-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);

            // Create a long filename (but within typical filesystem limits)
            var longName = new string('a', 180) + ".mp3";
            var path = Path.Join(dir, longName);
            File.WriteAllText(path, "x");

            var result = FileUtils.GetUniqueDestinationPath(path, File.Exists);
            Assert.NotEqual(path, result);
            Assert.Contains(" (1)", result);

            try { Directory.Delete(dir, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        [Fact]
        public void GetUniqueDestinationPath_InvalidPredicate_ThrowsHandled_ReturnsOriginal()
        {
            var tmp = Path.Join(Path.GetTempPath(), "fu-test-ex" + Guid.NewGuid() + ".dat");
            bool BadPredicate(string p) => throw new InvalidOperationException("boom");

            var result = FileUtils.GetUniqueDestinationPath(tmp, BadPredicate, null);
            // On predicate exception the helper should fall back to returning the original desired path
            Assert.Equal(tmp, result);
        }

        [Fact]
        public void GetUniqueDestinationPath_ReadOnlyDirectory_AppendsSuffix()
        {
            var dir = Path.Join(Path.GetTempPath(), "fu-ro-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            var file = Path.Join(dir, "exists.mp3");
            File.WriteAllText(file, "x");

            // Make directory read-only to simulate permission edge-case
            var dirInfo = new DirectoryInfo(dir);
            var origAttr = dirInfo.Attributes;
            try
            {
                dirInfo.Attributes |= FileAttributes.ReadOnly;

                var result = FileUtils.GetUniqueDestinationPath(file, File.Exists);
                Assert.NotEqual(file, result);
                Assert.Contains(" (1)", result);
            }
            finally
            {
                try { dirInfo.Attributes = origAttr; } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                try { Directory.Delete(dir, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        [WindowsFact]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        [Trait("Category", "Integration")]
        public void GetUniqueDestinationPath_WriteDeniedByAcl_OnWindows()
        {

            var dir = Path.Join(Path.GetTempPath(), "fu-acl-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            var desired = Path.Join(dir, "blocked.mp3");
            // Create an existing file to force suffixing
            var existing = Path.Join(dir, "blocked.mp3");
            File.WriteAllText(existing, "x");

            var dirInfo = new DirectoryInfo(dir);
            var originalSecurity = dirInfo.GetAccessControl();

            try
            {
                // Deny write permission for the current user
                var currentUser = WindowsIdentity.GetCurrent()?.User;
                Assert.NotNull(currentUser);

                var rule = new FileSystemAccessRule(currentUser, FileSystemRights.CreateFiles | FileSystemRights.Write, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
                var security = dirInfo.GetAccessControl();
                security.AddAccessRule(rule);
                dirInfo.SetAccessControl(security);

                // Generate unique path
                var result = FileUtils.GetUniqueDestinationPath(desired, File.Exists);

                // Attempt to write to the result path - should throw UnauthorizedAccessException when ACL denies write
                bool threw = false;
                try
                {
                    File.WriteAllText(result, "data");
                }
                catch (UnauthorizedAccessException)
                {
                    threw = true;
                }

                Assert.True(threw, "Expected UnauthorizedAccessException when writing to path in ACL-denied directory");
            }
            finally
            {
                try { dirInfo.SetAccessControl(originalSecurity); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (PlatformNotSupportedException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (System.Security.SecurityException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                try { Directory.Delete(dir, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        [Fact]
        public void GetUniqueDestinationPath_BatchImport_HandleMultipleCollisions_WithUsedSet()
        {
            // Simulate batch import scenario: importing multiple files where multiple target the same destination
            var dir = Path.Join(Path.GetTempPath(), "fu-batch-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            var usedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // First file imports to chapter.mp3
            var file1 = Path.Join(dir, "chapter.mp3");
            var result1 = FileUtils.GetUniqueDestinationPath(file1, File.Exists, usedDestinations);
            Assert.Equal(file1, result1); // Does not exist, no used, returns original
            usedDestinations.Add(result1);

            // Second file also wants chapter.mp3 - should get chapter (1).mp3 because first one is in usedDestinations
            var file2 = Path.Join(dir, "chapter.mp3");
            var result2 = FileUtils.GetUniqueDestinationPath(file2, File.Exists, usedDestinations);
            Assert.NotEqual(result1, result2);
            Assert.Contains(" (1)", result2);
            usedDestinations.Add(result2);

            // Third file also wants chapter.mp3 - should get chapter (2).mp3
            var file3 = Path.Join(dir, "chapter.mp3");
            var result3 = FileUtils.GetUniqueDestinationPath(file3, File.Exists, usedDestinations);
            Assert.NotEqual(result1, result3);
            Assert.NotEqual(result2, result3);
            Assert.Contains(" (2)", result3);

            try { Directory.Delete(dir, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        [WindowsFact]
        public void NormalizeStoredPath_ExpandsResolvedShortSegments_WhenResolverProvided()
        {

            string ResolveLongPath(string candidatePath)
            {
                return candidatePath switch
                {
                    @"C:\Books\ALD2A5~9" => @"C:\Books\A Long Directory Name",
                    @"C:\Books\A Long Directory Name\FILES~1" => @"C:\Books\A Long Directory Name\Files",
                    _ => candidatePath
                };
            }

            var normalized = FileUtils.NormalizeStoredPath(
                @"C:\Books\ALD2A5~9\FILES~1\Track 01.mp3",
                ResolveLongPath);

            Assert.Equal(
                @"C:\Books\A Long Directory Name\Files\Track 01.mp3",
                normalized);
        }

        [WindowsFact]
        public void NormalizeStoredPath_PreservesUnresolvedTail_WhenResolverProvided()
        {

            string ResolveLongPath(string candidatePath)
            {
                return candidatePath switch
                {
                    @"C:\Library\AUDIOB~1" => @"C:\Library\Audiobook Imports",
                    _ => candidatePath
                };
            }

            var normalized = FileUtils.NormalizeStoredPath(
                @"C:\Library\AUDIOB~1\New Folder\Disc 1",
                ResolveLongPath);

            Assert.Equal(
                @"C:\Library\Audiobook Imports\New Folder\Disc 1",
                normalized);
        }

        [WindowsFact]
        public void NormalizeStoredPath_DoesNotDropPrefix_WhenMalformedDriveSegmentAppears()
        {

            var normalized = FileUtils.NormalizeStoredPath(
                @"C:\Books\D:\Files\Track 01.mp3",
                candidatePath => candidatePath);

            Assert.Equal(
                @"C:\Books\Files\Track 01.mp3",
                normalized);
        }

        [Theory]
        [InlineData(" ", true)]
        [InlineData("folder ", true)]
        [InlineData("folder.", true)]
        [InlineData("folder name", false)]
        [InlineData("  folder", false)]
        [InlineData(@"C:\Program Files\Listenarr", false)]
        [InlineData(@"C:\media\folder \book.m4b", true)]
        [InlineData(@"C:\Books\NUL", true)]
        [InlineData(@"C:\Books\COM1.txt", true)]
        [InlineData(@"C:\Books\COM¹.txt", true)]
        [InlineData(@"C:\Books\com²", true)]
        [InlineData(@"C:\Books\LPT³.folder", true)]
        [InlineData(@"C:\Books\COM⁴.txt", false)]
        [InlineData(@"C:\Books\Bad|Name", true)]
        [InlineData(@"C:\Books\..\Other", false)]
        [InlineData(@"C:\Books\\Author", false)]
        [InlineData(@"\\server\\share\Books", false)]
        public void IsPathInvalidForOs_UsesSharedWindowsSegmentRules(string path, bool expected)
        {
            Assert.Equal(expected, FileUtils.IsPathInvalidForOs(path, isWindows: true));
        }

        [Fact]
        public void IsPathInvalidForOs_MatchesSharedWindowsReservedDeviceFixture()
        {
            using var fixture = JsonDocument.Parse(
                File.ReadAllText(
                    Path.Join(
                        TestUtils.FindRepositoryRoot(),
                        "test-fixtures",
                        "windows-reserved-device-names.json")));

            foreach (var name in fixture.RootElement
                .GetProperty("reserved")
                .EnumerateArray()
                .Select(value => value.GetString()!))
            {
                Assert.True(
                    FileUtils.IsPathInvalidForOs(
                        $@"C:\Books\{name}.folder",
                        isWindows: true),
                    name);
            }

            foreach (var name in fixture.RootElement
                .GetProperty("nonReserved")
                .EnumerateArray()
                .Select(value => value.GetString()!))
            {
                Assert.False(
                    FileUtils.IsPathInvalidForOs(
                        $@"C:\Books\{name}.folder",
                        isWindows: true),
                    name);
            }
        }

        [Theory]
        [InlineData(" ", false)]
        [InlineData("folder ", false)]
        [InlineData("folder.", false)]
        [InlineData("folder name", false)]
        [InlineData("  folder", false)]
        [InlineData("/media/folder /book.m4b", false)]
        [InlineData("/media/NUL", false)]
        public void IsPathInvalidForOs_AllowsLinuxWhitespacePaths(string path, bool expected)
        {
            Assert.Equal(expected, FileUtils.IsPathInvalidForOs(path, isWindows: false));
        }

        [Theory]
        [InlineData(" ", true)]
        [InlineData("folder ", true)]
        [InlineData("folder name", false)]
        public void IsPathInvalidForCurrentOs_UsesHostOsRules(string path, bool expectedOnWindows)
        {
            Assert.Equal(OperatingSystem.IsWindows() && expectedOnWindows, FileUtils.IsPathInvalidForCurrentOs(path));
        }

        [Fact]
        public void CombineWithOptionalBase_PreservesPathWhitespace()
        {
            var result = FileUtils.CombineWithOptionalBase("root", "  folder  ");

            Assert.Equal(
                string.Join(Path.DirectorySeparatorChar, "root", "  folder  "),
                result);
        }

        [Fact]
        public void CombineWithOptionalBase_PreservesNestedPathSegmentWhitespace()
        {
            var result = FileUtils.CombineWithOptionalBase(
                FileUtils.GetAbsolutePath("downloads"),
                " Book Folder / chapter 01.m4b ");

            Assert.Equal(
                FileUtils.GetAbsolutePath("downloads") + Path.DirectorySeparatorChar + " Book Folder / chapter 01.m4b ",
                result);
        }

        [Fact]
        public void CombineWithOptionalBase_PreservesRootedCandidatePath()
        {
            var candidate = Path.DirectorySeparatorChar + " Book Folder /chapter.m4b";

            var result = FileUtils.CombineWithOptionalBase(
                FileUtils.GetAbsolutePath("downloads"),
                candidate);

            Assert.Equal(candidate, result);
        }

        [Fact]
        public void CombineWithOptionalBase_PreservesCurrentFilesystemRootBase()
        {
            var root = Path.GetPathRoot(Path.GetTempPath());
            Assert.False(string.IsNullOrWhiteSpace(root));
            var candidate = Path.Join(" Author ", " Title .m4b");

            var result = FileUtils.CombineWithOptionalBase(root!, candidate);

            Assert.Equal(Path.Join(root!, candidate), result);
            Assert.Contains(" Author ", result);
            Assert.Contains(" Title .m4b", result);
        }

        [LinuxFact]
        public void CombineWithOptionalBase_PreservesUnixFilesystemRootBase()
        {

            var result = FileUtils.CombineWithOptionalBase("/", "Author/Book.m4b");

            Assert.Equal("/Author/Book.m4b", result);
        }

        [WindowsFact]
        public void CombineWithOptionalBase_PreservesWindowsDriveRootBase()
        {

            var result = FileUtils.CombineWithOptionalBase(@"C:\", @"Books\Title.m4b");

            Assert.Equal(@"C:\Books\Title.m4b", result);
        }

        [WindowsFact]
        public void CombineWithOptionalBase_PreservesUncShareRootBase()
        {

            var result = FileUtils.CombineWithOptionalBase(@"\\server\share", @"Books\Title.m4b");

            Assert.Equal(@"\\server\share\Books\Title.m4b", result);
        }

        [LinuxFact]
        public void NormalizeStoredPath_DoesNotTrimPathWhitespace_OnNonWindows()
        {

            var root = Path.Join(Path.GetTempPath(), "listenarr-path-whitespace-" + Guid.NewGuid().ToString("N"));
            var whitespaceSegment = " Book Folder ";
            var directory = Path.Join(root, whitespaceSegment);
            Directory.CreateDirectory(directory);

            try
            {
                var normalized = FileUtils.NormalizeStoredPath(directory);

                Assert.EndsWith(Path.DirectorySeparatorChar + whitespaceSegment, normalized, StringComparison.Ordinal);
                Assert.True(Directory.Exists(normalized));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        [Fact]
        public void CombineRelativePath_JoinsRelativeSegmentsAndTrimsLeadingSeparators()
        {
            var result = FileUtils.CombineRelativePath(
                "root",
                "/config",
                "\\cache",
                "images");

            Assert.Equal(
                string.Join(Path.DirectorySeparatorChar, "root", "config", "cache", "images"),
                result);
        }

        [Fact]
        public void CombineRelativePath_ThrowsWhenBasePathMissing()
        {
            Assert.Throws<ArgumentException>(() => FileUtils.CombineRelativePath("", "config"));
        }

        [WindowsFact]
        public void CombineRelativePath_RejectsWindowsRootedSegments()
        {

            Assert.Throws<ArgumentException>(() => FileUtils.CombineRelativePath("root", @"C:\escape"));
        }

        [Fact]
        public void TryResolveRelativePathWithinBase_AllowsNestedTempFile()
        {
            var root = Path.Join(Path.GetTempPath(), "fu-contain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                var ok = FileUtils.TryResolveRelativePathWithinBase(
                    root,
                    Path.Join("author", "book.m4b"),
                    out var resolved);

                Assert.True(ok);
                Assert.True(FileUtils.IsPathSameOrInside(resolved, root));
                Assert.Equal(Path.GetFullPath(Path.Join(root, "author", "book.m4b")), resolved);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        [WindowsFact]
        public void TryResolveRelativePathWithinBase_BlocksCaseDistinctSiblingTraversalOnWindows()
        {
            var parent = Path.Join(
                Path.GetTempPath(),
                "fu-case-boundary-" + Guid.NewGuid().ToString("N"));
            var root = Path.Join(parent, "Library");

            var ok = FileUtils.TryResolveRelativePathWithinBase(
                root,
                Path.Join("..", "library", "escape.m4b"),
                out var resolved);

            Assert.False(ok);
            Assert.Equal(string.Empty, resolved);
        }

        [Theory]
        [InlineData("../escape.m4b")]
        [InlineData("author/../../escape.m4b")]
        [InlineData("C:/escape.m4b")]
        [InlineData("C:\\escape.m4b")]
        public void TryResolveRelativePathWithinBase_BlocksTraversalAndRootedSegments(string relativePath)
        {
            var root = Path.Join(Path.GetTempPath(), "fu-contain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                var ok = FileUtils.TryResolveRelativePathWithinBase(root, relativePath, out var resolved);

                Assert.False(ok);
                Assert.Equal(string.Empty, resolved);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        [Fact]
        public void MutationBoundary_WindowsCaseDistinctSibling_IsNotContained()
        {
            Assert.True(FileSystemSafety.IsSameOrInsideMutationBoundary(
                @"c:\Library\Author\Book.m4b",
                @"C:\Library\",
                FileSystemPathSyntax.Windows));
            Assert.False(FileSystemSafety.IsSameOrInsideMutationBoundary(
                @"C:\library\Author\Book.m4b",
                @"C:\Library",
                FileSystemPathSyntax.Windows));
        }

        [WindowsFact]
        public void TryValidateMutationTarget_AllowsCaseAliasOnlyWhenItIsSamePhysicalRoot()
        {
            var parent = Path.Join(
                Path.GetTempPath(),
                "fu-mutation-case-alias-" + Guid.NewGuid().ToString("N"));
            var root = Path.Join(parent, "LibraryRoot");
            Directory.CreateDirectory(root);
            var aliasRoot = Path.Join(parent, "libraryroot");

            try
            {
                Assert.True(
                    Directory.Exists(aliasRoot),
                    "The Windows temp boundary must expose its normal case-insensitive alias for this regression.");

                var target = Path.Join(aliasRoot, "book.m4b");
                Assert.True(new LocalFileSystem().TryValidateMutationTarget(
                    target,
                    [root],
                    out _,
                    out var reason),
                    reason);
            }
            finally
            {
                try { Directory.Delete(parent, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        [Fact]
        public void TryValidateMutationTarget_AllowsOnlyConfiguredRoots()
        {
            var root = Path.Join(Path.GetTempPath(), "fu-mutation-" + Guid.NewGuid().ToString("N"));
            var sibling = root + "-sibling";
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(sibling);

            try
            {
                var allowedTarget = Path.Join(root, "book.m4b");
                var siblingTarget = Path.Join(sibling, "book.m4b");

                Assert.True(new LocalFileSystem().TryValidateMutationTarget(allowedTarget, [root], out var normalized, out var reason));
                Assert.Equal(Path.GetFullPath(allowedTarget), normalized);
                Assert.Equal(string.Empty, reason);

                Assert.False(new LocalFileSystem().TryValidateMutationTarget(siblingTarget, [root], out _, out reason));
                Assert.Contains("outside", reason, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                try { Directory.Delete(sibling, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        [WindowsFact]
        public void TryValidateMutationTarget_ForeignUnixAlias_IsRejectedBeforeWindowsNormalization()
        {
            var root = WindowsPathTestFixture
                .CreateRootRelativeAliasCompatibleDirectory(
                    "fu-mutation-foreign");
            var nativeTarget = Path.Join(root, "book.m4b");
            File.WriteAllText(nativeTarget, "audio");
            var foreignTarget = WindowsPathTestFixture
                .GetRootRelativeForeignAlias(nativeTarget);

            try
            {
                var fileSystem = new LocalFileSystem();
                Assert.False(fileSystem.TryValidateMutationTarget(
                    foreignTarget,
                    [root],
                    out _,
                    out var reason));
                Assert.Contains("Windows", reason, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(nativeTarget));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        [DirectoryLinkFact]
        public void TryValidateMutationTarget_BlocksDirectorySymlinkEscape()
        {
            var root = Path.Join(Path.GetTempPath(), "fu-mutation-" + Guid.NewGuid().ToString("N"));
            var outside = root + "-outside";
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);

            var linkPath = Path.Join(root, "linked-outside");
            try
            {
                try
                {
                    Directory.CreateSymbolicLink(linkPath, outside);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"This native filesystem regression requires symbolic-link support: {exception.Message}");
                }

                Assert.True(
                    Directory.Exists(linkPath),
                    "The symbolic-link directory must be visible before mutation validation runs.");

                var target = Path.Join(linkPath, "escape.mp3");
                var ok = new LocalFileSystem().TryValidateMutationTarget(target, [root], out _, out var reason);

                Assert.False(ok);
                Assert.Contains("resolves outside", reason, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { if (Directory.Exists(linkPath)) Directory.Delete(linkPath); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                try { Directory.Delete(root, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                try { Directory.Delete(outside, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        [Theory]
        [InlineData(@"C:\Books\Author", true)]
        [InlineData(@"C:\Books\.\Author", false)]
        [InlineData(@"C:\", false)]
        [InlineData(@"\Books\Author", false)]
        [InlineData("/Books/Author", false)]
        [InlineData(@"Books\Author", false)]
        [InlineData(@"C:\Books\Author ", false)]
        [InlineData(@"C:\Books\Author.", false)]
        [InlineData(@"C:\Books\NUL", false)]
        [InlineData(@"C:\Books\COM1.txt", false)]
        [InlineData(@"C:\Books\COM¹.txt", false)]
        [InlineData(@"C:\Books\com²", false)]
        [InlineData(@"C:\Books\LPT³.folder", false)]
        [InlineData(@"C:\Books\COM⁴.txt", true)]
        [InlineData(@"C:\Books\Bad|Name", false)]
        public void TryNormalizeUserProvidedDirectoryPathForOs_UsesWindowsRules(string path, bool expected)
        {
            var valid = FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                path,
                isWindows: true,
                out var normalizedPath,
                out var reason);

            Assert.Equal(expected, valid);
            if (expected)
            {
                Assert.False(string.IsNullOrWhiteSpace(normalizedPath));
                Assert.Equal(string.Empty, reason);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(reason));
            }
        }

        [Theory]
        [InlineData(@"\\server\..\Books", "parent")]
        [InlineData(@"\\.\share\Books", "current")]
        [InlineData(@"\\server\NUL\Books", "reserved")]
        [InlineData(@"\\NUL\share\Books", "reserved")]
        [InlineData(@"\\server\COM¹\Books", "reserved")]
        [InlineData(@"\\LPT³\share\Books", "reserved")]
        [InlineData(@"\\server\share.\Books", "space or period")]
        [InlineData(@"\\server.\share\Books", "space or period")]
        [InlineData(@"\\server|name\share\Books", "invalid")]
        [InlineData(@"\\server\\share\Books", "empty")]
        [InlineData(@"\\server\share\\Books", "empty")]
        [InlineData(@"\\\server\share\Books", "empty")]
        [InlineData("//server//share/Books", "empty")]
        public void TryNormalizeUserProvidedDirectoryPathForOs_RejectsInvalidUncAuthorityOrStructure(
            string path,
            string expectedReason)
        {
            var valid = FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                path,
                isWindows: true,
                out var normalizedPath,
                out var reason,
                allowFileSystemRoot: true);

            Assert.False(valid);
            Assert.Empty(normalizedPath);
            Assert.Contains(expectedReason, reason, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(@"\\server\share", @"\\server\share")]
        [InlineData(@"\\server\share\", @"\\server\share")]
        [InlineData("//server/share", @"\\server\share")]
        [InlineData(@"\\server\share\Books", @"\\server\share\Books")]
        [InlineData("//server/share/Books", @"\\server\share\Books")]
        [InlineData(@"\\server/share\Books", @"\\server\share\Books")]
        [InlineData("//server\\share/Books", @"\\server\share\Books")]
        public void TryNormalizeUserProvidedDirectoryPathForOs_CanonicalizesValidUncPaths(
            string path,
            string expected)
        {
            Assert.True(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                path,
                isWindows: true,
                out var normalizedPath,
                out var reason,
                allowFileSystemRoot: true,
                rejectParentTraversal: true));

            Assert.Equal(string.Empty, reason);
            Assert.Equal(expected, normalizedPath);

            Assert.True(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                normalizedPath,
                isWindows: true,
                out var normalizedAgain,
                out var secondReason,
                allowFileSystemRoot: true,
                rejectParentTraversal: true));
            Assert.Equal(string.Empty, secondReason);
            Assert.Equal(normalizedPath, normalizedAgain);
        }

        [Theory]
        [InlineData(@"\\")]
        [InlineData(@"\\server")]
        [InlineData(@"\\server\")]
        [InlineData(@"\\\share")]
        public void TryNormalizeUserProvidedDirectoryPathForOs_RejectsIncompleteUncPaths(string path)
        {
            Assert.False(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                path,
                isWindows: true,
                out var normalizedPath,
                out var reason,
                allowFileSystemRoot: true));

            Assert.Empty(normalizedPath);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void TryNormalizeUserProvidedDirectoryPathForOs_RejectsWindowsDriveRelativePathEvenWhenRootsAreAllowed()
        {
            Assert.False(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                "C:",
                isWindows: true,
                out var normalizedPath,
                out var reason,
                allowFileSystemRoot: true));

            Assert.Empty(normalizedPath);
            Assert.Contains("absolute", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryNormalizeUserProvidedDirectoryPathForOs_CanonicalizesRepeatedDriveRootSeparators()
        {
            Assert.True(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                @"C:\\",
                isWindows: true,
                out var normalizedPath,
                out var reason,
                allowFileSystemRoot: true));

            Assert.Equal(string.Empty, reason);
            Assert.Equal(@"C:\", normalizedPath);
        }

        [Fact]
        public void TryNormalizeUserProvidedDirectoryPathForOs_AllowsWindowsRootWhenExplicitlyRequested()
        {
            var separator = @"\";
            var driveRoot = "C:" + separator;
            var uncRoot = separator + separator + "server" + separator + "share";
            var currentDriveRoot = separator;

            Assert.True(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                driveRoot,
                isWindows: true,
                out var normalizedDriveRoot,
                out var driveRootReason,
                allowFileSystemRoot: true));
            Assert.Equal(string.Empty, driveRootReason);
            Assert.False(string.IsNullOrWhiteSpace(normalizedDriveRoot));

            Assert.True(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                uncRoot,
                isWindows: true,
                out var normalizedUncRoot,
                out var uncRootReason,
                allowFileSystemRoot: true));
            Assert.Equal(string.Empty, uncRootReason);
            Assert.Contains("server", normalizedUncRoot, StringComparison.OrdinalIgnoreCase);

            Assert.True(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                currentDriveRoot,
                isWindows: true,
                out var normalizedCurrentDriveRoot,
                out var currentDriveRootReason,
                allowFileSystemRoot: true));
            Assert.Equal(string.Empty, currentDriveRootReason);
            Assert.False(string.IsNullOrWhiteSpace(normalizedCurrentDriveRoot));

            Assert.True(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                "/",
                isWindows: true,
                out var normalizedForwardSlashRoot,
                out var forwardSlashRootReason,
                allowFileSystemRoot: true));
            Assert.Equal(string.Empty, forwardSlashRootReason);
            Assert.False(string.IsNullOrWhiteSpace(normalizedForwardSlashRoot));
        }

        [Fact]
        public void TryNormalizeUserProvidedDirectoryPathForOs_RejectsWindowsRootByDefault()
        {
            var separator = @"\";

            Assert.False(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                separator,
                isWindows: true,
                out _,
                out var currentDriveRootReason));
            Assert.Contains("root", currentDriveRootReason, StringComparison.OrdinalIgnoreCase);

            Assert.False(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                "/",
                isWindows: true,
                out _,
                out var forwardSlashRootReason));
            Assert.Contains("root", forwardSlashRootReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryNormalizeUserProvidedDirectoryPathForOs_RejectsParentTraversalForDestinations()
        {
            var separator = @"\";
            var windowsTraversal = "C:" + separator + "Books" + separator + ".." + separator + "Other";

            Assert.False(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                windowsTraversal,
                isWindows: true,
                out _,
                out var windowsReason,
                rejectParentTraversal: true));
            Assert.Contains("parent", windowsReason, StringComparison.OrdinalIgnoreCase);

            Assert.False(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                "/media/../other",
                isWindows: false,
                out _,
                out var unixReason,
                rejectParentTraversal: true));
            Assert.Contains("parent", unixReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryNormalizeUserProvidedDirectoryPathForOs_RejectsRootFolderParentTraversal()
        {
            var separator = @"\";
            var parentSegment = new string('.', 2);
            var windowsTraversal = "C:" + separator + "Books" + separator + parentSegment + separator + "Other";

            Assert.False(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                windowsTraversal,
                isWindows: true,
                out _,
                out var windowsReason,
                allowFileSystemRoot: true,
                rejectParentTraversal: true));
            Assert.Contains("parent", windowsReason, StringComparison.OrdinalIgnoreCase);

            var unixTraversal = string.Join('/', string.Empty, "media", parentSegment, "other");
            Assert.False(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                unixTraversal,
                isWindows: false,
                out _,
                out var unixReason,
                allowFileSystemRoot: true,
                rejectParentTraversal: true));
            Assert.Contains("parent", unixReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryNormalizeUserProvidedDirectoryPathForOs_AllowsRootsWhenExplicitlyRequestedAndTraversalRejected()
        {
            var separator = @"\";
            var driveRoot = "C:" + separator;
            var uncRoot = separator + separator + "server" + separator + "share";

            Assert.True(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                driveRoot,
                isWindows: true,
                out var normalizedDriveRoot,
                out var driveRootReason,
                allowFileSystemRoot: true,
                rejectParentTraversal: true));
            Assert.Equal(string.Empty, driveRootReason);
            Assert.False(string.IsNullOrWhiteSpace(normalizedDriveRoot));

            Assert.True(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                separator,
                isWindows: true,
                out var normalizedCurrentDriveRoot,
                out var currentDriveRootReason,
                allowFileSystemRoot: true,
                rejectParentTraversal: true));
            Assert.Equal(string.Empty, currentDriveRootReason);
            Assert.False(string.IsNullOrWhiteSpace(normalizedCurrentDriveRoot));

            Assert.True(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                uncRoot,
                isWindows: true,
                out var normalizedUncRoot,
                out var uncRootReason,
                allowFileSystemRoot: true,
                rejectParentTraversal: true));
            Assert.Equal(string.Empty, uncRootReason);
            Assert.False(string.IsNullOrWhiteSpace(normalizedUncRoot));

            Assert.True(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                "/",
                isWindows: false,
                out var normalizedUnixRoot,
                out var unixRootReason,
                allowFileSystemRoot: true,
                rejectParentTraversal: true));
            Assert.Equal(string.Empty, unixRootReason);
            Assert.Equal("/", normalizedUnixRoot);
        }

        [Theory]
        [InlineData("../escape", true)]
        [InlineData("Book/../../escape", true)]
        [InlineData(".../Book", false)]
        [InlineData("..hidden/Book", false)]
        [InlineData("Book../Title", false)]
        public void ContainsParentDirectorySegment_DetectsOnlyLiteralParentSegments(string path, bool expected)
        {
            Assert.Equal(expected, FileUtils.ContainsParentDirectorySegment(path, '/', '\\'));
        }

        [Fact]
        public void ContainsParentDirectorySegment_WithUnixSeparator_DoesNotTreatBackslashAsSeparator()
        {
            Assert.False(FileUtils.ContainsParentDirectorySegment("Book\\..\\Title", '/'));
        }

        [Theory]
        [InlineData(" /media/Author", true)]
        [InlineData("   /media/Author", true)]
        [InlineData(@" C:\Books\Author", true)]
        [InlineData(@" \\server\share\Books", true)]
        [InlineData(@" \\server", true)]
        [InlineData(@" \\\server\share", true)]
        [InlineData("  Relative Folder", false)]
        [InlineData("/media/Author ", false)]
        [InlineData("/media/ Author", false)]
        public void HasLeadingWhitespaceBeforeRootedPath_DetectsOnlyAmbiguousRootedInputs(string path, bool expected)
        {
            Assert.Equal(expected, FileUtils.HasLeadingWhitespaceBeforeRootedPath(path));
        }

        [Fact]
        public void TryNormalizeUserProvidedDirectoryPathForOs_AllowsUnixRootWhenExplicitlyRequested()
        {
            Assert.True(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                "/",
                isWindows: false,
                out var normalizedRoot,
                out var reason,
                allowFileSystemRoot: true));

            Assert.Equal(string.Empty, reason);
            Assert.Equal("/", normalizedRoot);
        }

        [Theory]
        [InlineData("/media/Author", true)]
        [InlineData("/media/./Author", false)]
        [InlineData("/media/Author ", true)]
        [InlineData("/media/NUL", true)]
        [InlineData("/media/..", false)]
        [InlineData("media/Author", false)]
        [InlineData("/", false)]
        [InlineData("", false)]
        public void TryNormalizeUserProvidedDirectoryPathForOs_UsesUnixRules(string path, bool expected)
        {
            var valid = FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                path,
                isWindows: false,
                out var normalizedPath,
                out var reason);

            Assert.Equal(expected, valid);
            if (expected)
            {
                Assert.False(string.IsNullOrWhiteSpace(normalizedPath));
                Assert.Equal(string.Empty, reason);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(reason));
            }
        }

        [Fact]
        public void TryNormalizeUserProvidedDirectoryPathForOs_RejectsNullCharacter()
        {
            Assert.False(FileUtils.TryNormalizeUserProvidedDirectoryPathForOs(
                "/media/book\0folder",
                isWindows: false,
                out _,
                out var reason));
            Assert.Contains("invalid", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryNormalizeUserProvidedDirectoryPathForCurrentOs_NormalizesCurrentHostPath()
        {
            var path = Path.Join(Path.GetTempPath(), "listenarr-normalize-" + Guid.NewGuid().ToString("N"));

            Assert.True(FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(path, out var normalizedPath, out var reason));
            Assert.Equal(Path.GetFullPath(path), normalizedPath);
            Assert.Equal(string.Empty, reason);
        }

        [WindowsFact]
        public void GetValidMutationRootsForCurrentOs_PreservesCaseDistinctWindowsRoots()
        {
            var roots = FileUtils.GetValidMutationRootsForCurrentOs([
                @"C:\Library",
                @"C:\library"
            ]);

            Assert.Equal(2, roots.Count);
            Assert.Contains(@"C:\Library", roots, StringComparer.Ordinal);
            Assert.Contains(@"C:\library", roots, StringComparer.Ordinal);
        }

        [WindowsFact]
        public void GetValidMutationRootsForCurrentOs_DoesNotLaunderPersistedUnixRoot()
        {
            var nativeRoot = Path.Join(
                Path.GetPathRoot(Environment.CurrentDirectory)!,
                "ListenarrLibrary");

            var roots = FileUtils.GetValidMutationRootsForCurrentOs([
                "/",
                nativeRoot
            ]);

            Assert.Single(roots);
            Assert.Equal(Path.GetFullPath(nativeRoot), roots[0], StringComparer.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("/books/title", "/", false, true)]
        [InlineData("/books/title", "/books", false, true)]
        [InlineData("/books", "/books/", false, true)]
        [InlineData("/bookshelf/title", "/books", false, false)]
        [InlineData("/Books/title", "/books", false, false)]
        [InlineData(@"C:\Books\Title", @"C:\", true, true)]
        [InlineData(@"C:\Books\Title", @"C:\Books", true, true)]
        [InlineData(@"C:\Books", @"C:\Books\", true, true)]
        [InlineData(@"C:\Bookshelf\Title", @"C:\Books", true, false)]
        [InlineData(@"\\server\share\Books\Title", @"\\server\share", true, true)]
        [InlineData(@"\\server\share-other\Books", @"\\server\share", true, false)]
        public void IsPathSameOrInsideForOs_RespectsFilesystemBoundaries(
            string candidatePath,
            string rootPath,
            bool isWindows,
            bool expected)
        {
            Assert.Equal(expected, FileUtils.IsPathSameOrInsideForOs(candidatePath, rootPath, isWindows));
        }

        [Theory]
        [InlineData(@"C:\Books", @"c:\books\", true, true)]
        [InlineData(@"\\server\share\Books", @"\\SERVER\SHARE\books\", true, true)]
        [InlineData(@"\\server\\share\Books", @"\\server\share\Books", true, true)]
        [InlineData("/media/Books", "/media/books", false, false)]
        [InlineData("/media/Books", "/media/Books/", false, true)]
        public void AreFilesystemPathsEquivalentForOs_UsesHostStyleCaseRules(
            string left,
            string right,
            bool isWindows,
            bool expected)
        {
            Assert.Equal(expected, FileUtils.AreFilesystemPathsEquivalentForOs(left, right, isWindows));
        }

        [Fact]
        public void FilesystemPathComparerForCurrentOs_UsesHostCaseRules()
        {
            var root = Path.Join(Path.GetTempPath(), "listenarr-path-case-" + Guid.NewGuid().ToString("N"));
            var paths = new HashSet<string>(FileUtils.FilesystemPathComparerForCurrentOs)
            {
                Path.Join(root, "Track01.m4b"),
                Path.Join(root, "track01.m4b")
            };

            Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, paths.Count);
        }

        [Fact]
        public void CombineWithOptionalBase_PreservesPathSegmentWhitespace()
        {
            var basePath = Path.Join(Path.GetTempPath(), "listenarr-path-space-" + Guid.NewGuid().ToString("N"));
            var candidatePath = Path.Join(" Author With Space ", " Title With Space ", " Chapter 01 .m4b");

            var combined = FileUtils.CombineWithOptionalBase(basePath, candidatePath);

            Assert.Equal(Path.Join(basePath, candidatePath), combined);
        }

        [Fact]
        public void GetCommonPathForDirectories_UsesHostFilesystemCaseRules()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(@"C:\Books", FileUtils.GetCommonPathForDirectories([
                    @"C:\Books\AuthorA",
                    @"c:\Books\AuthorB"
                ]));
            }
            else
            {
                Assert.Equal("/", FileUtils.GetCommonPathForDirectories([
                    "/books/AuthorA",
                    "/Books/AuthorB"
                ]));
            }
        }

        [Fact]
        public void GetCommonPathForDirectories_ExplicitWindowsSemantics_AreHostIndependent()
        {
            var semantics = new FileSystemPathSemantics(
                FileSystemPathSyntax.Windows,
                FileSystemCaseSensitivity.Sensitive);

            Assert.Equal(@"C:\Library", FileUtils.GetCommonPathForDirectories([
                @"c:\Library\Author\BookA",
                @"C:\Library\author\BookB"
            ], semantics));
        }

        [WindowsFact]
        public void GetCommonPathForDirectories_PreservesCaseDistinctWindowsSegments()
        {
            Assert.Equal(@"C:\Library", FileUtils.GetCommonPathForDirectories([
                @"C:\Library\Author\BookA",
                @"C:\Library\author\BookB"
            ]));
        }

        [Fact]
        public void GetCommonPathForDirectories_RespectsPathSegmentBoundaries()
        {
            var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var common = FileUtils.GetCommonPathForDirectories([
                Path.Join(root, "books", "A"),
                Path.Join(root, "bookshelf", "B")
            ]);

            Assert.True(FileUtils.AreFilesystemPathsEquivalentForCurrentOs(root, common ?? string.Empty));
        }

        [Fact]
        public async Task FilesHaveSameContentAsync_UsesSizeAndHash()
        {
            var root = Path.Join(Path.GetTempPath(), "fu-hash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                var first = Path.Join(root, "first.mp3");
                var second = Path.Join(root, "second.mp3");
                var third = Path.Join(root, "third.mp3");
                await File.WriteAllTextAsync(first, "same");
                await File.WriteAllTextAsync(second, "same");
                await File.WriteAllTextAsync(third, "diff");

                Assert.True(await new LocalFileSystem().FilesHaveSameContentAsync(first, second));
                Assert.False(await new LocalFileSystem().FilesHaveSameContentAsync(first, third));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }
    }
}
