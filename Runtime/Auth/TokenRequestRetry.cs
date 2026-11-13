using System;
using System.Threading.Tasks;

namespace Tsc.AIBridge.Auth
{
    /// <summary>
    /// Sends a token request and repeats it once when it never reached the server (DNS, no route, timeout).
    /// A headset that starts before its network is ready fails the first lookup and succeeds seconds later:
    /// Training Platform report of 2026-11-12, "Cannot resolve destination host", while the same network got a
    /// token 8 s later. An answer from the server (401, any 4xx or 5xx) is never repeated.
    /// </summary>
    internal static class TokenRequestRetry
    {
        /// <summary>Wait before the one repeat.</summary>
        internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

        /// <param name="send">One token request.</param>
        /// <param name="neverReachedServer">True when the attempt got no answer from the server.</param>
        /// <param name="onRetry">Called with the first attempt just before the repeat (for a warning).</param>
        /// <param name="delay">Waits the given time (Task.Delay in production).</param>
        /// <returns>The first attempt, or the repeat when the first never reached the server.</returns>
        internal static async Task<TAttempt> SendAsync<TAttempt>(
            Func<Task<TAttempt>> send,
            Func<TAttempt, bool> neverReachedServer,
            Action<TAttempt> onRetry,
            Func<TimeSpan, Task> delay)
        {
            var first = await send();
            if (!neverReachedServer(first))
                return first;

            onRetry(first);
            await delay(RetryDelay);
            return await send();
        }
    }
}
