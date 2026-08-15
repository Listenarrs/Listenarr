namespace Listenarr.Tests.Builders
{
    public class QualityProfileBuilder
    {
        private QualityProfile _qualityProfile = new();

        public QualityProfileBuilder()
        {
            _qualityProfile.Id = TestEntityIdGenerator.Next();
            _qualityProfile.Qualities = new List<QualityDefinition>();
        }

        public QualityProfileBuilder WithName(string value)
        {
            _qualityProfile.Name = value;
            return this;
        }

        public QualityProfileBuilder WithCutoff(string value)
        {
            _qualityProfile.CutoffQuality = value;
            return this;
        }

        public QualityProfileBuilder WithPreferredFormats(params string[] values)
        {
            _qualityProfile.PreferredFormats = values.ToList();
            return this;
        }

        public QualityProfileBuilder WithQuality(
            string quality,
            int priority,
            string? codec = null,
            int? bitrate = null,
            bool lossless = false,
            bool allowed = true)
        {
            _qualityProfile.Qualities.Add(new QualityDefinition
            {
                Quality = quality,
                Priority = priority,
                Codec = codec,
                Bitrate = bitrate,
                IsLossless = lossless,
                Allowed = allowed
            });
            return this;
        }

        /// <summary>
        /// Adds a structured AAC + MP3 bitrate ladder plus a FLAC lossless rung, mirroring the
        /// frontend's default codec/bitrate ordering (lower priority number = higher quality).
        /// </summary>
        public QualityProfileBuilder WithStructuredDefaults()
        {
            WithQuality("FLAC", 0, codec: "FLAC", lossless: true);
            WithQuality("AAC 320kbps", 1, codec: "AAC", bitrate: 320);
            WithQuality("AAC 256kbps", 2, codec: "AAC", bitrate: 256);
            WithQuality("AAC 192kbps", 3, codec: "AAC", bitrate: 192);
            WithQuality("AAC 128kbps", 4, codec: "AAC", bitrate: 128);
            WithQuality("AAC 64kbps", 5, codec: "AAC", bitrate: 64);
            WithQuality("MP3 320kbps", 6, codec: "MP3", bitrate: 320);
            WithQuality("MP3 256kbps", 7, codec: "MP3", bitrate: 256);
            WithQuality("MP3 VBR", 8, codec: "MP3");
            WithQuality("MP3 192kbps", 9, codec: "MP3", bitrate: 192);
            WithQuality("MP3 128kbps", 10, codec: "MP3", bitrate: 128);
            return this;
        }

        public QualityProfile Build()
        {
            return _qualityProfile;
        }
    }
}
