using Listenarr.Domain.Models;

namespace Listenarr.Tests.Builders
{
    public class QualityProfileBuilder
    {
        private static int IdCounter = 0;

        private readonly QualityProfile _qualityProfile = new()
        {
            Id = ++IdCounter,
            Name = "Test Profile",
            Qualities = [],
            PreferredFormats = [],
            PreferredLanguages = [],
            MustContain = [],
            MustNotContain = []
        };

        public QualityProfileBuilder WithId(int value)
        {
            _qualityProfile.Id = value;
            return this;
        }

        public QualityProfileBuilder WithName(string value)
        {
            _qualityProfile.Name = value;
            return this;
        }

        public QualityProfile Build()
        {
            return _qualityProfile;
        }
    }
}
