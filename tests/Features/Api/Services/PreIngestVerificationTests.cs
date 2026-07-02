using Listenarr.Application.Downloads;

namespace Listenarr.Tests.Features.Api.Services
{
    public class PreIngestVerificationTests
    {
        private static List<AudioMetadata> Files(int count, double durationSeconds)
        {
            var list = new List<AudioMetadata>();
            for (var i = 0; i < count; i++)
            {
                list.Add(new AudioMetadata
                {
                    Duration = TimeSpan.FromSeconds(durationSeconds),
                });
            }
            return list;
        }

        [Fact]
        public void Inspect_RejectsManyShortTracks_AsMusicAlbum()
        {
            // 12 files, ~3.3 min each — classic music album shape.
            var result = PreIngestVerification.Inspect(Files(12, 200));
            Assert.True(result.Rejected);
            Assert.Contains("music album", result.Reason);
        }

        [Fact]
        public void Inspect_KeepsManyLongChapters_AsAudiobook()
        {
            // 12 files, 1 hour each — long-form audiobook chapters.
            var result = PreIngestVerification.Inspect(Files(12, 3600));
            Assert.False(result.Rejected);
        }

        [Fact]
        public void Inspect_KeepsShortBatchBelowFileCountThreshold()
        {
            // Only 5 short files — too few to call it a music album.
            var result = PreIngestVerification.Inspect(Files(5, 200));
            Assert.False(result.Rejected);
        }

        [Fact]
        public void Inspect_FailsOpenWhenNoDurationSignal()
        {
            // 12 files but ffprobe gave no usable durations — don't guess.
            var result = PreIngestVerification.Inspect(Files(12, 0));
            Assert.False(result.Rejected);
        }

        [Fact]
        public void Inspect_KeepsManyMediumTracksAtThresholdBoundary()
        {
            // Median exactly at the 300s threshold is not below it — keep.
            var result = PreIngestVerification.Inspect(Files(12, 300));
            Assert.False(result.Rejected);
        }

        [Fact]
        public void Inspect_EmptyBatch_IsAccepted()
        {
            var result = PreIngestVerification.Inspect(new List<AudioMetadata>());
            Assert.False(result.Rejected);
        }
    }
}
