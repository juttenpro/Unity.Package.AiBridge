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
    }
}
