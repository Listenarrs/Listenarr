namespace Listenarr.Tests.Common
{
    /// <summary>
    /// Time limits for tests that wait on a signal from concurrent work.
    /// </summary>
    public static class TestTimeouts
    {
        /// <summary>
        /// How long a test waits for a step that must happen. It only bounds a hang: a passing
        /// test never waits this long, so it is generous enough for a loaded CI machine, where
        /// file-system work that takes milliseconds locally can take tens of seconds.
        /// </summary>
        public static readonly TimeSpan Signal = TimeSpan.FromMinutes(2);
    }
}
