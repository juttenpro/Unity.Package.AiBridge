using System;
using Newtonsoft.Json;

namespace Tsc.AIBridge.Messages
{
    /// <summary>
    /// Anonymous observability correlation IDs sent along with outbound messages so the
    /// ApiOrchestrator can aggregate cost, usage, and health metrics per lesson, course
    /// and organization — never per individual user.
    ///
    /// Privacy gate: this type intentionally has NO UserId field. UserId is a personal
    /// identifier (GDPR) and must never enter the observability pipeline on either side of
    /// the wire. Callers that populate this context must not copy UserId into any field.
    ///
    /// All fields are nullable because:
    /// - AI Coach standalone sessions have no lessonId (empty string signals "no lesson").
    /// - Anonymous / pre-login sessions have no organizationId.
    /// - Clients may send only a subset when the full context is unavailable.
    /// </summary>
    [Serializable]
    public class ObservabilityContext
    {
        /// <summary>
        /// Unity app session identifier from GameDataLogger. Anonymous; rotates per launch.
        /// Primary correlation key for cross-request aggregation in the observability dashboard.
        /// </summary>
        [JsonProperty("appLogId")]
        public string AppLogId;

        /// <summary>
        /// Lesson identifier within a course. Use empty string (not null) when the client
        /// explicitly signals "no lesson" — typically an AI Coach standalone session.
        /// </summary>
        [JsonProperty("lessonId")]
        public string LessonId;

        /// <summary>
        /// Course identifier. Set to "AICoach" for coach sessions so coach usage can be
        /// aggregated separately from formal course content.
        /// </summary>
        [JsonProperty("courseId")]
        public string CourseId;

        /// <summary>
        /// Organization (school/company) identifier. Matches
        /// <c>Tsc.Training.Network.Domain.V1.OrganisationInfo.OrganisationId</c>.
        /// Null for anonymous / pre-login sessions.
        /// </summary>
        [JsonProperty("organizationId")]
        public int? OrganizationId;

        /// <summary>
        /// Application mode from the Unity build ("Development" or "Production").
        /// Allows the dashboard to filter out dev/test traffic from production reporting.
        /// </summary>
        [JsonProperty("appMode")]
        public string AppMode;
    }
}
