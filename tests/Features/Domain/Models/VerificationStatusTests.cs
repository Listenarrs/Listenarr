namespace Listenarr.Tests.Features.Domain.Models
{
    [Trait("Name", "VerificationStatusTests")]
    [Trait("Category", "Domain")]
    public class VerificationStatusTests
    {
        [Theory]
        [InlineData(VerificationStatus.Unverified)]
        [InlineData(VerificationStatus.AgentVerified)]
        [InlineData(VerificationStatus.AgentFlagged)]
        public void IsAgentWritable_AllowsAgentToReplaceAgentOrUnverifiedStates(VerificationStatus status)
        {
            Assert.True(status.IsAgentWritable());
        }

        [Theory]
        [InlineData(VerificationStatus.ManuallyVerified)]
        [InlineData(VerificationStatus.Rejected)]
        public void IsAgentWritable_ProtectsManualStatesFromAgentOverwrite(VerificationStatus status)
        {
            Assert.False(status.IsAgentWritable());
        }
    }
}
