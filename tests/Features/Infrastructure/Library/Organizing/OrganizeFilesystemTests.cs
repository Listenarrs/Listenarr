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
using Listenarr.Application.Audiobooks.Organizing;
using Listenarr.Infrastructure.Library.Organizing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Library.Organizing
{
    [Trait("Name", "OrganizeFilesystemTests")]
    [Trait("Category", "Organize")]
    public class OrganizeFilesystemTests : IDisposable
    {
        private readonly string _root;
        private readonly OrganizeFilesystem _fs = new(NullLogger<OrganizeFilesystem>.Instance);

        public OrganizeFilesystemTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "lna-organize-fs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
        }

        private string MakeDir(params string[] segments)
        {
            var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
            Directory.CreateDirectory(path);
            return path;
        }

        private static string WriteFile(string dir, string name, string content)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        [Fact(DisplayName = "IsMetadataStubDirectory: metadata-only true (even nested), audio false, missing false, empty true")]
        public void IsMetadataStubDirectory_Classification()
        {
            var stub = MakeDir("stub");
            WriteFile(stub, "cover.jpg", "img");
            WriteFile(MakeDir("stub", "nested"), "book.opf", "meta");
            Assert.True(_fs.IsMetadataStubDirectory(stub));

            var withAudio = MakeDir("with-audio");
            WriteFile(withAudio, "cover.jpg", "img");
            WriteFile(MakeDir("with-audio", "nested"), "book.mp3", "audio");
            Assert.False(_fs.IsMetadataStubDirectory(withAudio));

            Assert.False(_fs.IsMetadataStubDirectory(Path.Combine(_root, "does-not-exist")));

            var empty = MakeDir("empty");
            Assert.True(_fs.IsMetadataStubDirectory(empty));
        }

        [Fact(DisplayName = "EvaluateFlatten: nested wrapper with only source's files → Ok with file count")]
        public void EvaluateFlatten_NestedWrapper_Ok()
        {
            // dest/Title holds nothing but the nested source subtree.
            var dest = MakeDir("Author", "Title");
            var source = MakeDir("Author", "Title", "Narrator", "Title");
            WriteFile(source, "book.m4b", "audio");
            WriteFile(source, "cover.jpg", "img");

            var (feasibility, count) = _fs.EvaluateFlatten(source, dest);

            Assert.Equal(FlattenFeasibility.Ok, feasibility);
            Assert.Equal(2, count);
        }

        [Fact(DisplayName = "EvaluateFlatten: dest holds a foreign file → TargetHasForeignFiles")]
        public void EvaluateFlatten_ForeignFile_Refused()
        {
            var dest = MakeDir("Author", "Title");
            WriteFile(dest, "unrelated.mp3", "someone else's audio");
            var source = MakeDir("Author", "Title", "Nested");
            WriteFile(source, "book.m4b", "audio");

            var (feasibility, _) = _fs.EvaluateFlatten(source, dest);

            Assert.Equal(FlattenFeasibility.TargetHasForeignFiles, feasibility);
        }

        [Fact(DisplayName = "EvaluateFlatten: dest not an ancestor → NotAncestor; missing source → SourceMissing")]
        public void EvaluateFlatten_NonFlattenShapes_Refused()
        {
            var a = MakeDir("A");
            var b = MakeDir("B");
            WriteFile(a, "book.m4b", "audio");
            Assert.Equal(FlattenFeasibility.NotAncestor,
                _fs.EvaluateFlatten(a, b).Feasibility);

            Assert.Equal(FlattenFeasibility.SourceMissing,
                _fs.EvaluateFlatten(Path.Combine(_root, "gone"), a).Feasibility);
        }

        [Fact(DisplayName = "ExecuteFlatten: collapses the wrapper; files land at dest, wrapper gone")]
        public void ExecuteFlatten_CollapsesWrapper()
        {
            var dest = MakeDir("Author", "Title");
            var source = MakeDir("Author", "Title", "Narrator", "Title");
            WriteFile(source, "book.m4b", "audio");
            var extras = MakeDir("Author", "Title", "Narrator", "Title", "extras");
            WriteFile(extras, "notes.txt", "notes");

            var outcome = _fs.ExecuteFlatten(source, dest, Guid.NewGuid());

            Assert.True(outcome.Success, outcome.ErrorMessage);
            Assert.Equal(2, outcome.FilesMoved);
            Assert.True(File.Exists(Path.Combine(dest, "book.m4b")));
            Assert.True(File.Exists(Path.Combine(dest, "extras", "notes.txt")));
            Assert.False(Directory.Exists(Path.Combine(dest, "Narrator")));
        }

        [Fact(DisplayName = "ExecuteFlatten: refuses when dest holds foreign files — nothing moved or deleted")]
        public void ExecuteFlatten_ForeignFiles_NothingTouched()
        {
            var dest = MakeDir("Author", "Title");
            var foreign = WriteFile(dest, "keep-me.mp3", "protected");
            var source = MakeDir("Author", "Title", "Nested");
            var nestedFile = WriteFile(source, "book.m4b", "audio");

            var outcome = _fs.ExecuteFlatten(source, dest, Guid.NewGuid());

            Assert.False(outcome.Success);
            Assert.True(File.Exists(foreign));
            Assert.True(File.Exists(nestedFile));
        }
    }
}
