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
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Api.Utils
{
    public class FinalizePathHelperTests
    {
        [Fact]
        public void BuildMultiFileDestination_WithAuthorInTitle_SplitsAuthorAndTitle()
        {
            var outputPath = FileUtils.GetAbsolutePath("Library");
            var settings = new ApplicationSettings { OutputPath = outputPath };
            var download = new DownloadBuilder().WithTitle("William Faulkner - The Sound and the Fury").Build();

            var dest = FinalizePathHelper.BuildMultiFileDestination(settings, download, "William Faulkner - The Sound and the Fury");

            Assert.Contains("William Faulkner", dest);
            Assert.Contains("The Sound and the Fury", dest);
            Assert.StartsWith(outputPath, dest, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildMultiFileDestination_WithSeries_IncludesSeriesFolder()
        {
            var outputPath = FileUtils.GetAbsolutePath("Library");
            var settings = new ApplicationSettings { OutputPath = outputPath };
            var download = new DownloadBuilder().WithTitle("The Sound and the Fury").WithArtist("William Faulkner").WithSeries("Modern Classics").Build();

            var dest = FinalizePathHelper.BuildMultiFileDestination(settings, download, "The Sound and the Fury");

            Assert.StartsWith(outputPath, dest, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("William Faulkner", dest);
            Assert.Contains("Modern Classics", dest);
            Assert.Contains("The Sound and the Fury", dest);
        }
    }
}

