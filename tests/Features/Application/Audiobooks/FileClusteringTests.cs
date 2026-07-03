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
using Listenarr.Application.Audiobooks;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    /// <summary>
    /// Fixtures are real filename shapes from the live 773-file Heinlein
    /// collection record that motivated the Split workflow.
    /// </summary>
    public class FileClusteringTests
    {
        private const string Base = "/audiobooks/Robert A. Heinlein/Let There Be Light/Peter Coates";

        private static AudiobookFile F(int id, string rel) => new() { Id = id, Path = $"{Base}/{rel}" };

        [Fact]
        public void Cluster_TrackNumberedSiblings_FormOneCluster()
        {
            var clusters = FileClustering.Cluster(new[]
            {
                F(1, "(heinlein_robert)-friday-01_77.mp3"),
                F(2, "(heinlein_robert)-friday-02_77.mp3"),
                F(3, "(heinlein_robert)-friday-44_77.mp3"),
                F(4, "Robert A. Heinlein - Expanded Universe Part 01 of 26.mp3"),
                F(5, "Robert A. Heinlein - Expanded Universe Part 26 of 26.mp3"),
            }, Base);

            Assert.Equal(2, clusters.Count);
            Assert.Equal(3, clusters[0].Files.Count); // friday
            Assert.Equal(2, clusters[1].Files.Count); // expanded universe
        }

        [Fact]
        public void Cluster_SubdirectoryWins_OverFilenames()
        {
            var clusters = FileClustering.Cluster(new[]
            {
                F(1, "Future History/Revolt in 2100/01 Intro.mp3"),
                F(2, "Future History/Revolt in 2100/02 Introduction; The Innocent Eye.mp3"),
                F(3, "All You Zombies/Spider Robinson/All You Zombies.mp3"),
            }, Base);

            Assert.Equal(2, clusters.Count);
            var dirCluster = clusters.First(c => c.DisplayName == "Future History");
            Assert.Equal(2, dirCluster.Files.Count);
        }

        [Fact]
        public void Cluster_SingleFiles_StaySeparate()
        {
            var clusters = FileClustering.Cluster(new[]
            {
                F(1, "Robert A. Heinlein - The Moon Is a Harsh Mistress.mp3"),
                F(2, "Robert A. Heinlein - Stranger in a Strange Land.mp3"),
            }, Base);

            Assert.Equal(2, clusters.Count);
            Assert.All(clusters, c => Assert.Single(c.Files));
        }

        [Fact]
        public void Cluster_TwoCopiesWithDifferentNumberingStyles_FormTwoClusters()
        {
            // Live case: a record holding two complete copies of the same book.
            // The "(N)" copy must not explode into singletons, AND must not
            // merge with the "-NN" copy — the marker style is the identity that
            // lets the user delete one redundant copy.
            var clusters = FileClustering.Cluster(new[]
            {
                F(1, "The Rolling Stones (1).mp3"),
                F(2, "The Rolling Stones (10).mp3"),
                F(3, "The Rolling Stones (11).mp3"),
                F(4, "The Rolling Stones-00.mp3"),
                F(5, "The Rolling Stones-01.mp3"),
            }, Base);

            Assert.Equal(2, clusters.Count);
            Assert.Equal(3, clusters[0].Files.Count); // the "(N)" copy
            Assert.Equal(2, clusters[1].Files.Count); // the "-NN" copy
            Assert.All(clusters, c => Assert.Equal("The Rolling Stones", c.DisplayName));
        }

        [Fact]
        public void Cluster_EmbeddedBookTitles_SplitACollectionRenamedToTheParentName()
        {
            // Live case (John Scalzi "Old Man's War"): a collection's files were all renamed to
            // the parent record's name and sit flat in the record's own folder, so filenames
            // cluster into a single useless group — but each file's embedded tag names the real
            // book. For flat files (no subfolder) the embedded title must win and split them,
            // with track noise stripped.
            const string omwBase = "/audiobooks/John Scalzi/Old Man's War/Old Man's War";
            AudiobookFile E(int id, string rel) => new() { Id = id, Path = $"{omwBase}/{rel}" };

            var files = new[]
            {
                E(1, "Old Man's War-001.mp3"),
                E(2, "Old Man's War-030.mp3"),
                E(3, "Old Man's War-072.mp3"),
                E(4, "Old Man's War-114.mp3"),
            };
            var embedded = new Dictionary<int, string>
            {
                [1] = "Old Man's War 1-77",
                [2] = "The Ghost Brigades 10-41",
                [3] = "The Ghost Brigades 38-41",
                [4] = "1 - The Sagan Diary",
            };

            var clusters = FileClustering.Cluster(files, omwBase, embedded);

            Assert.Equal(3, clusters.Count);
            Assert.Contains(clusters, c => c.DisplayName == "The Ghost Brigades" && c.Files.Count == 2);
            Assert.Contains(clusters, c => c.DisplayName == "The Sagan Diary" && c.Files.Count == 1);
            Assert.Contains(clusters, c => c.DisplayName == "Old Man's War" && c.Files.Count == 1);
        }

        [Fact]
        public void Cluster_SubfolderWins_OverInconsistentEmbeddedTags()
        {
            // Live case (Throne of Glass, 2325): per-book subfolders, but the embedded album tags
            // are series-based ("Throne of Glass bk 2") and even mis-applied — the Dramatized
            // Adaptation file is tagged with the novel's title. The subfolder must win so
            // different books don't merge on a shared/garbage tag, and a book whose files are
            // inconsistently tagged (one file untagged, one tagged differently) still groups as
            // one. Flat files with no subfolder still fall back to the embedded tag.
            const string b = "/audiobooks/Sarah J. Maas/Throne of Glass/Elizabeth Evans";
            AudiobookFile T(int id, string rel) => new() { Id = id, Path = $"{b}/{rel}" };

            var files = new[]
            {
                T(1, "Crown of Midnight-01.mp3"),                              // flat
                T(2, "Crown of Midnight/Crown of Midnight-001.mp3"),          // subfolder
                T(3, "Throne of Glass [Dramatized Adaptation]/dram-001.mp3"), // subfolder, mis-tagged
                T(4, "Queen of Shadows/Queen of Shadows-002.mp3"),            // subfolder, no tag
                T(5, "Queen of Shadows/Queen of Shadows-003.mp3"),            // subfolder, different tag
            };
            var embedded = new Dictionary<int, string>
            {
                [1] = "Throne of Glass bk 2",
                [2] = "Throne of Glass bk 2",
                [3] = "Crown of Midnight",                     // mis-tag on the Dramatized file
                [5] = "Throne of Glass 04 - Queen of Shadows", // file 4 has no tag
            };

            var clusters = FileClustering.Cluster(files, b, embedded);

            Assert.Equal(4, clusters.Count);
            // Subfolder books each cluster by their folder, regardless of the (bad) tags:
            Assert.Contains(clusters, c => c.DisplayName == "Crown of Midnight" && c.Files.Count == 1);
            // NOT merged with Crown of Midnight despite sharing its title tag:
            Assert.Contains(clusters, c => c.DisplayName == "Throne of Glass [Dramatized Adaptation]" && c.Files.Count == 1);
            // Both Queen of Shadows files merge by folder despite inconsistent tags:
            Assert.Contains(clusters, c => c.DisplayName == "Queen of Shadows" && c.Files.Count == 2);
            // The flat file falls back to its embedded tag:
            Assert.Contains(clusters, c => c.DisplayName == "Throne of Glass bk 2" && c.Files.Count == 1);
        }

        [Fact]
        public void Cluster_NoEmbeddedTitle_FallsBackToPathClustering()
        {
            // A file with no usable embedded tag must still cluster by filename/subdir, and a
            // tag that cleans to nothing is treated as "no signal".
            const string b = "/audiobooks/Author/Record/Narrator";
            AudiobookFile F2(int id, string rel) => new() { Id = id, Path = $"{b}/{rel}" };

            var files = new[] { F2(1, "Some Book-01.mp3"), F2(2, "Some Book-02.mp3") };
            var embedded = new Dictionary<int, string> { [1] = "   ", [2] = "07" }; // whitespace + bare digits

            var clusters = FileClustering.Cluster(files, b, embedded);

            Assert.Single(clusters);
            Assert.Equal("Some Book", clusters[0].DisplayName);
        }

        [Fact]
        public void Cluster_DifferentVolumesInOneFolder_FormSeparateClusters()
        {
            // Live case (book 1335): a Vol. 2 set was moved into the Vol. 1
            // record's folder, so both volumes sit side by side distinguished
            // only by "Vol. 1-NNN" vs "Vol. 2-NNN". The track-numbering stripper
            // must not also eat the volume number, or both reduce to
            // "Expanded Universe, Vol." and collapse into a single cluster —
            // making the record un-splittable.
            const string volBase = "/audiobooks/Robert A. Heinlein/Expanded Universe, Vol. 1/Bronson Pinchot";
            AudiobookFile V(int id, string rel) => new() { Id = id, Path = $"{volBase}/{rel}" };

            var clusters = FileClustering.Cluster(new[]
            {
                V(1, "Expanded Universe, Vol. 1-001.mp3"),
                V(2, "Expanded Universe, Vol. 1-002.mp3"),
                V(3, "Expanded Universe, Vol. 1-031.mp3"),
                V(4, "Expanded Universe, Vol. 2-001.mp3"),
                V(5, "Expanded Universe, Vol. 2-002.mp3"),
                V(6, "Expanded Universe, Vol. 2-031.mp3"),
            }, volBase);

            Assert.Equal(2, clusters.Count);
            Assert.All(clusters, c => Assert.Equal(3, c.Files.Count));
            Assert.Contains(clusters, c => c.DisplayName == "Expanded Universe, Vol. 1");
            Assert.Contains(clusters, c => c.DisplayName == "Expanded Universe, Vol. 2");
        }

        [Fact]
        public void Cluster_UnnumberedFirstTrack_JoinsItsNumberedSiblings()
        {
            // Numbering often starts at the unmarked file: "Title.mp3" IS track
            // one of the "Title (N)" set (users renamed it "Title (0).mp3" by
            // hand to fix this). Comparable duration → merge, listed first.
            var clusters = FileClustering.Cluster(new[]
            {
                new AudiobookFile { Id = 1, Path = $"{Base}/Example Track.mp3", DurationSeconds = 600 },
                new AudiobookFile { Id = 2, Path = $"{Base}/Example Track (1).mp3", DurationSeconds = 610 },
                new AudiobookFile { Id = 3, Path = $"{Base}/Example Track (2).mp3", DurationSeconds = 590 },
            }, Base);

            Assert.Single(clusters);
            Assert.Equal(3, clusters[0].Files.Count);
            Assert.Equal(1, clusters[0].Files[0].Id);
        }

        [Fact]
        public void Cluster_SameStemWholeBookFile_StaysItsOwnGroup()
        {
            // A complete single-file copy sharing the stem must NOT be swallowed
            // into the track set — it is ~30× a track's duration.
            var clusters = FileClustering.Cluster(new[]
            {
                new AudiobookFile { Id = 1, Path = $"{Base}/Example Track.mp3", DurationSeconds = 20000 },
                new AudiobookFile { Id = 2, Path = $"{Base}/Example Track (1).mp3", DurationSeconds = 610 },
                new AudiobookFile { Id = 3, Path = $"{Base}/Example Track (2).mp3", DurationSeconds = 590 },
            }, Base);

            Assert.Equal(2, clusters.Count);
        }

        [Theory]
        [InlineData("The Rolling Stones (10)", "The Rolling Stones")]
        [InlineData("Track [07]", "Track")]
        [InlineData("(heinlein_robert)-friday-01_77", "(heinlein_robert)-friday")]
        [InlineData("Robert A. Heinlein - Expanded Universe Part 01 of 26", "Robert A. Heinlein - Expanded Universe")]
        [InlineData("TunnelintheSkyUnabridged_51", "TunnelintheSkyUnabridged")]
        [InlineData("1 The Year of the Jackpot", "The Year of the Jackpot")]
        [InlineData("HEINLEIN - Door 12_41", "HEINLEIN - Door")]
        [InlineData("Robert A. Heinlein - Starship Troopers Part 61 of 61", "Robert A. Heinlein - Starship Troopers")]
        // A volume/book designator keeps its own number; only the track marker goes.
        [InlineData("Expanded Universe, Vol. 1-001", "Expanded Universe, Vol. 1")]
        [InlineData("Expanded Universe, Vol. 2-031", "Expanded Universe, Vol. 2")]
        [InlineData("Wheel of Time Book 3 - 12", "Wheel of Time Book 3")]
        public void CleanStem_StripsNumberingNoise(string input, string expected)
        {
            Assert.Equal(expected, FileClustering.CleanStem(input));
        }

        [Fact]
        public void CleanStem_NeverReturnsEmpty()
        {
            // A name that is nothing but numbering must fall back to itself.
            Assert.Equal("01_77", FileClustering.CleanStem("01_77"));
        }
    }

    public class SplitDestinationSuggesterTests
    {
        private static readonly List<(int, string)> Library = new()
        {
            (398, "Friday"),
            (421, "Tunnel in the Sky"),
            (395, "Expanded Universe, Vol. 1"),
            (1335, "Expanded Universe, Vol. 2"),
            (415, "The Moon Is a Harsh Mistress"),
        };

        [Fact]
        public void Suggest_PrefersLongestTitle()
        {
            // "Vol. 2" must beat the shorter "Expanded Universe, Vol. 1"… both
            // contain "expanded universe", but only Vol. 2's full title is in
            // the cluster name.
            Assert.Equal(1335, SplitDestinationSuggester.Suggest("Expanded Universe, Vol. 2", Library));
        }

        [Fact]
        public void Suggest_SpaceInsensitive()
        {
            Assert.Equal(421, SplitDestinationSuggester.Suggest("TunnelintheSkyUnabridged", Library));
        }

        [Fact]
        public void Suggest_NoContainment_ReturnsNull()
        {
            Assert.Null(SplitDestinationSuggester.Suggest("Robert A. Heinlein - 8 Books", Library));
        }

        [Fact]
        public void Suggest_FullSentenceTitle_Matches()
        {
            Assert.Equal(415, SplitDestinationSuggester.Suggest(
                "Robert A. Heinlein - The Moon Is a Harsh Mistress", Library));
        }
    }
}
