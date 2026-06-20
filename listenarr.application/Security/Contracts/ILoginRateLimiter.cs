namespace Listenarr.Application.Security.Contracts
{
    public interface ILoginRateLimiter
    {
        bool IsBlocked(string key);
        void RecordFailure(string key);
        void RecordSuccess(string key);
        /// <summary>
        /// If the key is blocked, returns remaining block duration in seconds; otherwise 0.
        /// </summary>
        int GetSecondsUntilUnblock(string key);
    }
}
