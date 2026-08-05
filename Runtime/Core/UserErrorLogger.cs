using UnityEngine;

namespace Tsc.AIBridge
{
    /// <summary>
    /// Helper for logging errors with user-friendly messages using the [UserError:...] tag convention.
    /// ErrorHandler in the Training framework parses this tag and shows the friendly message
    /// in the error popup instead of the generic "Something went wrong" message.
    /// The full technical details are still sent to the server for diagnostics.
    /// </summary>
    public static class UserErrorLogger
    {
        /// <summary>
        /// Logs an error with a user-friendly message tag that ErrorHandler can display.
        /// Format: "[UserError:userMessage] technicalDetails"
        /// </summary>
        /// <param name="userMessage">The message shown to the user in the error popup</param>
        /// <param name="technicalDetails">Technical error details for logging and server reporting</param>
        public static void LogError(string userMessage, string technicalDetails)
        {
            Debug.LogError($"[UserError:{userMessage}] {technicalDetails}");
        }

        /// <summary>
        /// Logs an error the session can recover from on its own, adding the <c>[Recoverable]</c>
        /// marker on top of the normal [UserError:...] tag.
        /// Format: "[UserError:userMessage] [Recoverable] technicalDetails"
        /// </summary>
        /// <remarks>
        /// The host project's ErrorHandler registers "[Recoverable]" as a non-fatal pattern: the
        /// error is still reported to the Oops endpoint for diagnostics, but it does NOT raise the
        /// fatal "the application needs to be restarted" popup.
        ///
        /// Use this for connection failures on a single conversation turn. Those are genuinely
        /// recoverable: every SendXAsync on WebSocketClient calls EnsureConnectionAsync first,
        /// which fetches a FRESH JWT and rebuilds the socket, so the next turn reconnects by
        /// itself. Reported from the field (customer HMC, IVA bedrijfsartsen): one WiFi hiccup on
        /// push-to-talk release ended a live training session with a restart popup.
        ///
        /// Do NOT use this for conditions retrying cannot fix — authentication failure, an
        /// unassigned API-key provider, a missing WebSocket URL. Those keep <see cref="LogError"/>
        /// so they still surface as fatal; continuing silently would leave the user in a lesson
        /// that can never produce an AI response.
        /// </remarks>
        /// <param name="userMessage">The message shown to the user if this error is surfaced</param>
        /// <param name="technicalDetails">Technical error details for logging and server reporting</param>
        public static void LogRecoverableError(string userMessage, string technicalDetails)
        {
            Debug.LogError($"[UserError:{userMessage}] [Recoverable] {technicalDetails}");
        }
    }
}
