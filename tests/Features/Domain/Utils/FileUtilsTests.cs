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
using Xunit;
using System.Security.AccessControl;
using System.Security.Principal;
using Listenarr.Domain.Common;

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

            var result = FileUtils.GetUniqueDestinationPath(tmp);
            Assert.Equal(tmp, result);
        }

        [Fact]
        public void GetUniqueDestinationPath_AppendsSuffixWhenExists()
        {
            var dir = Path.Join(Path.GetTempPath(), "fu-dir-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            var file = Path.Join(dir, "file.txt");
            File.WriteAllText(file, "x");

            var result = FileUtils.GetUniqueDestinationPath(file);
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

            var result = FileUtils.GetUniqueDestinationPath(path);
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

                var result = FileUtils.GetUniqueDestinationPath(file);
                Assert.NotEqual(file, result);
                Assert.Contains(" (1)", result);
            }
            finally
            {
                try { dirInfo.Attributes = origAttr; } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                try { Directory.Delete(dir, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        [Fact]
        [Trait("Category", "Integration")]
        public void GetUniqueDestinationPath_WriteDeniedByAcl_OnWindows()
        {
            if (!OperatingSystem.IsWindows())
            {
                // Not applicable on non-Windows platforms in this test
                return;
            }

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
                if (currentUser == null)
                {
                    return; // can't determine user, skip
                }

                var rule = new FileSystemAccessRule(currentUser, FileSystemRights.CreateFiles | FileSystemRights.Write, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
                var security = dirInfo.GetAccessControl();
                security.AddAccessRule(rule);
                dirInfo.SetAccessControl(security);

                // Generate unique path
                var result = FileUtils.GetUniqueDestinationPath(desired);

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

        [Fact]
        public void NormalizeStoredPath_ExpandsResolvedShortSegments_WhenResolverProvided()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

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

        [Fact]
        public void NormalizeStoredPath_PreservesUnresolvedTail_WhenResolverProvided()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

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

        [Fact]
        public void NormalizeStoredPath_DoesNotDropPrefix_WhenMalformedDriveSegmentAppears()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var normalized = FileUtils.NormalizeStoredPath(
                @"C:\Books\D:\Files\Track 01.mp3",
                candidatePath => candidatePath);

            Assert.Equal(
                @"C:\Books\Files\Track 01.mp3",
                normalized);
        }
    }
}
