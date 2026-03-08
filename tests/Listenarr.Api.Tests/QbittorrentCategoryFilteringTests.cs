using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Adapters;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    /// <summary>
    /// Tests for qBittorrent category filtering functionality
    /// Verifies that the adapter correctly applies category filters to API calls
    /// </summary>
    public class QbittorrentCategoryFilteringTests
    {
        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_With_Category_Returns_Formatted_Parameter()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "audiobooks" } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Equal("&category=audiobooks", result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_Without_Category_Returns_Empty_String()
        {
            // Arrange
            var settings = new Dictionary<string, object>();

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_With_Null_Settings_Returns_Empty_String()
        {
            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(null, "&");

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_Uses_Correct_Prefix_Question()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "test" } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "?");

            // Assert
            Assert.Equal("?category=test", result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_Uses_Correct_Prefix_Ampersand()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "test" } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Equal("&category=test", result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_With_Special_Characters_AreURLEncoded()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "audio books & stuff" } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            // The spaces and & should be URL encoded
            Assert.Equal("&category=audio%20books%20%26%20stuff", result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_Trims_Configured_Category()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "  audiobooks  " } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Equal("&category=audiobooks", result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_With_Empty_Category_String_Returns_Empty()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "" } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_With_Whitespace_Category_Returns_Empty()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "   " } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_With_Null_Category_Value_Returns_Empty()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", null } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Empty(result);
        }
    }
}

