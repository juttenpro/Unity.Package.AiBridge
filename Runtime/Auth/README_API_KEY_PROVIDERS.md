# API Key Provider System

## Overview

The API key provider system allows flexible and secure retrieval of API keys for authenticating with the backend orchestrator service. The system supports both synchronous and asynchronous (coroutine-based) providers.

## Architecture

### Interfaces

1. **`IApiKeyProvider`** - Synchronous API key retrieval
   - Simple, direct key retrieval
   - Suitable for: Environment variables, hardcoded keys, local config files
   - Example: `EnvironmentApiKeyProvider`

2. **`IAsyncApiKeyProvider`** - Asynchronous API key retrieval via coroutines
   - Coroutine-based with callback pattern
   - Suitable for: Network requests, encrypted storage, remote configuration
   - Example: `OrchestratorApiKeyProvider` (in AIBridge.Extended)

### Components

- **`SimpleApiKeyProvider`** - Basic implementation for hardcoded keys (development only)
- **`EnvironmentApiKeyProvider`** - Retrieves API key from environment variable
- **`AsyncApiKeyProviderAdapter`** - Internal adapter that wraps async providers for sync usage
- **`OrchestratorApiKeyProvider`** - Internal implementation using ApiCallHandler (Extended package only)

## Usage

### For 3rd Party Developers

#### Option 1: Use Environment Variables (Recommended)

1. Add `EnvironmentApiKeyProvider` component to your GameObject:
   ```csharp
   // In Unity Editor:
   // 1. Select your WebSocketClient GameObject
   // 2. Add Component > Tsc.AIBridge.Auth > EnvironmentApiKeyProvider
   // 3. Configure environment variable name (default: ORCHESTRATOR_API_KEY)
   // 4. Assign component to WebSocketClient's apiKeyProviderComponent field
   ```

2. Set environment variable before running:
   ```bash
   # Windows PowerShell
   $env:ORCHESTRATOR_API_KEY = "your_api_key_here"

   # Linux/Mac
   export ORCHESTRATOR_API_KEY="your_api_key_here"

   # Unity Editor (set in Project Settings or startup script)
   ```

#### Option 2: Create Custom Provider

Implement `IApiKeyProvider` for synchronous retrieval:

```csharp
using Tsc.AIBridge.Auth;
using UnityEngine;

public class MyCustomApiKeyProvider : MonoBehaviour, IApiKeyProvider
{
    public string GetOrchestratorApiKey()
    {
        // Your custom logic here
        // Examples:
        // - Read from PlayerPrefs
        // - Read from local file
        // - Read from custom configuration system

        string apiKey = PlayerPrefs.GetString("ApiKey", "");

        if (string.IsNullOrEmpty(apiKey))
        {
            throw new System.InvalidOperationException("API key not configured!");
        }

        return apiKey;
    }
}
```

#### Option 3: Create Async Provider (Advanced)

Implement `IAsyncApiKeyProvider` for network/async operations:

```csharp
using System;
using System.Collections;
using Tsc.AIBridge.Auth;
using UnityEngine;
using UnityEngine.Networking;

public class RemoteApiKeyProvider : MonoBehaviour, IAsyncApiKeyProvider
{
    [SerializeField] private string configServerUrl = "https://config.mycompany.com/api-key";

    public IEnumerator GetOrchestratorApiKeyAsync(Action<bool, string> callback)
    {
        using var request = UnityWebRequest.Get(configServerUrl);
        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string apiKey = request.downloadHandler.text;
            callback(true, apiKey); // Success
        }
        else
        {
            callback(false, request.error); // Failure
        }
    }
}
```

### For SimulationCrew Internal Systems

Use `OrchestratorApiKeyProvider` from the Extended package:

```csharp
// In Unity Editor:
// 1. Select your WebSocketClient GameObject
// 2. Add Component > Tsc.AIBridge.Extended.Auth > OrchestratorApiKeyProvider
// 3. Assign component to WebSocketClient's apiKeyProviderComponent field

// Requires:
// - Training package properly initialized
// - User logged in via TrainingGlobals.Instance.UserSession
// - ApiCallHandler configured with API key access code
```

## WebSocketClient Configuration

### Inspector Setup

**apiKeyProviderComponent** (Required):
   - Assign a MonoBehaviour implementing `IApiKeyProvider` or `IAsyncApiKeyProvider`
   - Provides flexibility and security
   - Can be changed without code modifications
   - **MUST be configured** - no fallback options

### Example Scene Setup

```
Scene Hierarchy:
├── WebSocketClient
│   ├── Component: WebSocketClient
│   │   └── apiKeyProviderComponent: --> EnvironmentApiKeyProvider
│   └── Component: EnvironmentApiKeyProvider
│       └── environmentVariableName: "ORCHESTRATOR_API_KEY"
```

## Security Best Practices

### ❌ DON'T

- Hardcode API keys in source code
- Commit API keys to version control
- Share API keys in screenshots or logs
- Use production keys in development builds

### ✅ DO

- Use environment variables for deployment
- Use different keys for development/staging/production
- Rotate API keys regularly
- Use secure configuration management systems
- Keep API keys in .gitignore'd files or secure vaults

## Migration Guide

### From Custom API Key System

If you were using a custom API key retrieval system:

1. Implement `IApiKeyProvider` or `IAsyncApiKeyProvider` interface in your existing class
2. Add MonoBehaviour if not already present
3. Add component to appropriate GameObject
4. Assign to `apiKeyProviderComponent` field in WebSocketClient Inspector
5. Remove old custom integration code

## Troubleshooting

### "No API key configured" Error

**Cause:** `apiKeyProviderComponent` is not assigned in Inspector.

**Solution:**
1. Create a provider component (e.g., `EnvironmentApiKeyProvider` or `OrchestratorApiKeyProvider`)
2. Add it to your GameObject
3. Assign it to `apiKeyProviderComponent` field in WebSocketClient Inspector

### "Environment variable not set" Error

**Cause:** `EnvironmentApiKeyProvider` can't find the configured environment variable.

**Solution:**
1. Check variable name matches in Inspector
2. Set environment variable before starting Unity
3. Restart Unity Editor after setting variable
4. Use `System.Environment.GetEnvironmentVariable()` to verify

### "TrainingGlobals.Instance is null" Error (Internal Only)

**Cause:** `OrchestratorApiKeyProvider` requires Training package and user login.

**Solution:**
1. Ensure Training package is properly initialized
2. User must be logged in before WebSocket connection
3. Check `TrainingGlobals.Instance.UserSession` is valid

### API Key Fetch Timeout

**Cause:** Async provider took too long (>10 seconds default).

**Solution:**
1. Check network connectivity
2. Verify remote config server is accessible
3. Add retry logic in custom provider
4. Consider caching API key after first retrieval

## Examples

See the following example implementations:

- **SimpleApiKeyProvider.cs** - Basic hardcoded key (development only)
- **EnvironmentApiKeyProvider.cs** - Environment variable retrieval (recommended)
- **OrchestratorApiKeyProvider.cs** (Extended) - ApiCallHandler integration (internal)

## API Reference

### IApiKeyProvider

```csharp
public interface IApiKeyProvider
{
    /// <summary>
    /// Gets the orchestrator API key synchronously.
    /// </summary>
    /// <returns>The API key string</returns>
    /// <exception cref="InvalidOperationException">When API key cannot be retrieved</exception>
    string GetOrchestratorApiKey();
}
```

### IAsyncApiKeyProvider

```csharp
public interface IAsyncApiKeyProvider
{
    /// <summary>
    /// Gets the orchestrator API key asynchronously via coroutine.
    /// </summary>
    /// <param name="callback">
    /// Callback invoked with (success, result) where:
    /// - success=true: result contains API key
    /// - success=false: result contains error message
    /// </param>
    /// <returns>Coroutine for Unity execution</returns>
    IEnumerator GetOrchestratorApiKeyAsync(Action<bool, string> callback);
}
```

## Support

For questions or issues:
- Internal developers: Check Training package documentation
- 3rd party developers: Refer to AIBridge package documentation
- Both: See example implementations in this folder
