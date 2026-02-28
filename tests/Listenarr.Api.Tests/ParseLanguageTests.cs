using System;
using Xunit;
using Listenarr.Api.Services;

namespace Listenarr.Api.Tests
{
    public class ParseLanguageTests
    {
        [Theory]
        [InlineData("[ENG / M4B] Some Title", "English")]
        [InlineData("Some Title (EN)", "English")]
        [InlineData("Some Title EN", "English")]
        [InlineData("[DUT] Title", "Dutch")]
        [InlineData("Title - NL", "Dutch")]
        [InlineData("Title (DE)", "German")]
        [InlineData("[GER / MP3] Foo", "German")]
        [InlineData("Book Title FR", "French")]
        [InlineData("[FRE] Bar", "French")]
        [InlineData("No language here", null)]
        public void ParseLanguageFromText_RecognizesCodes(string input, string? expected)
        {
            // Create an uninitialized SearchService instance so we don't have to satisfy constructor dependencies
            var svcObj = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(SearchService));

            var method = typeof(SearchService).GetMethod("ParseLanguageFromText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            var result = method.Invoke(svcObj, new object[] { input }) as string;
            Assert.Equal(expected, result);
        }
    }
}
