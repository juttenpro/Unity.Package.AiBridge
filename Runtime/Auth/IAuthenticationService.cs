using System.Threading.Tasks;

namespace Tsc.AIBridge.Auth
{
    /// <summary>
    /// Interface for authentication services
    /// </summary>
    public interface IAuthenticationService
    {
        /// <summary>
        /// Gets a JWT authentication token for the specified user
        /// </summary>
        /// <param name="userId">The user identifier</param>
        /// <param name="role">The user role</param>
        /// <param name="apiKey">The API key for authentication</param>
        /// <returns>JWT token or null if authentication fails</returns>
        Task<string> GetAuthTokenAsync(string userId, string role, string apiKey);
    }
}
