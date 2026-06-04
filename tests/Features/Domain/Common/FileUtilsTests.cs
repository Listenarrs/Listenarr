using Listenarr.Domain.Common;
using Xunit;

namespace Listenarr.Tests.Features.Domain.Common
{
    public class FileUtilsTests
    {
        public class IsPathInsideOfTestData : TheoryData<bool, string, string>
        {
            public IsPathInsideOfTestData()
            {
                Add(true, FileUtils.GetAbsolutePath("data", "test"), FileUtils.GetAbsolutePath("data"));

                // A directory is not inside itself
                Add(false, FileUtils.GetAbsolutePath("data", "test"), FileUtils.GetAbsolutePath("data", "test"));

                // Same as above plus we cannot discriminate directory from filenames
                Add(false, FileUtils.GetAbsolutePath("datatest", "audio.mp3"), FileUtils.GetAbsolutePath("datatest", "audio.mp3"));

                // Should succeed for filenames too
                Add(true, FileUtils.GetAbsolutePath("data", "test", "audio.mp3"), FileUtils.GetAbsolutePath("data", "test"));
                Add(true, FileUtils.GetAbsolutePath("data", "test", "audio.mp3"), FileUtils.GetAbsolutePath("data"));

                // Path sharing part of filenames should fail
                Add(false, FileUtils.GetAbsolutePath("datatest", "test", "audio.mp3"), FileUtils.GetAbsolutePath("data"));

            }
        }

        [Theory]
        [ClassData(typeof(IsPathInsideOfTestData))]
        public void IsPathInsideOf(bool expectedResult, string needle, string haystack)
        {
            Assert.Equal(expectedResult, FileUtils.IsPathInsideOf(needle, haystack));
        }

        public class IsSameDirectoryTestData : TheoryData<bool, string, string>
        {
            public IsSameDirectoryTestData()
            {
                Add(false, FileUtils.GetAbsolutePath("data", "test"), FileUtils.GetAbsolutePath("data"));
                Add(true, FileUtils.GetAbsolutePath("data", "test"), FileUtils.GetAbsolutePath("data", "test"));

                // We treat everything as directory
                Add(true, FileUtils.GetAbsolutePath("datatest", "audio.mp3"), FileUtils.GetAbsolutePath("datatest", "audio.mp3"));

                // Path sharing part of filenames should fail
                Add(false, FileUtils.GetAbsolutePath("datatest"), FileUtils.GetAbsolutePath("data"));
            }
        }

        [Theory]
        [ClassData(typeof(IsSameDirectoryTestData))]
        public void IsSameDirectory(bool expectedResult, string a, string b)
        {
            Assert.Equal(expectedResult, FileUtils.IsSameDirectory(a, b));
        }
    }
}
