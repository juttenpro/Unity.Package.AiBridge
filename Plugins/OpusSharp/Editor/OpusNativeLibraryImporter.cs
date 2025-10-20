using UnityEngine;
using UnityEditor;
using System.IO;

namespace OpusSharp.Editor
{
    /// <summary>
    /// Configures the native Opus libraries for the correct platforms.
    /// This ensures the DLLs are properly loaded at runtime.
    /// </summary>
    [InitializeOnLoad]
    public class OpusNativeLibraryImporter
    {
        private const string OPUS_NATIVES_PATH = "Packages/com.simulationcrew.aibridge/Plugins/OpusSharp/OpusSharp.Natives/runtimes";
        private const string CONFIGURED_FLAG_KEY = "OpusSharp.NativeLibrariesConfigured";
        private static bool isConfiguring = false;
        
        static OpusNativeLibraryImporter()
        {
            // Only configure once per session unless manually triggered
            if (!SessionState.GetBool(CONFIGURED_FLAG_KEY, false))
            {
                EditorApplication.delayCall += () => ConfigureNativeLibraries(false);
            }
        }
        
        [MenuItem("Tools/OpusSharp/Configure Native Libraries")]
        public static void ConfigureNativeLibrariesMenu()
        {
            ConfigureNativeLibraries(true);
        }
        
        public static void ConfigureNativeLibraries(bool forceReconfigure)
        {
            // Prevent infinite loops
            if (isConfiguring)
            {
                return;
            }
            
            // Check if already configured this session
            if (!forceReconfigure && SessionState.GetBool(CONFIGURED_FLAG_KEY, false))
            {
                return;
            }
            
            isConfiguring = true;
            
            try
            {
                Debug.Log("[OpusSharp] Configuring native library import settings...");
                
                bool anyChanges = false;
                
                // Windows x64 DLL
                anyChanges |= ConfigureWindowsX64();
                
                // Windows x86 DLL
                anyChanges |= ConfigureWindowsX86();
                
                // Android ARM64 SO
                anyChanges |= ConfigureAndroidARM64();
                
                // Linux x64 SO
                anyChanges |= ConfigureLinuxX64();
                
                if (anyChanges)
                {
                    AssetDatabase.Refresh();
                }
                
                Debug.Log("[OpusSharp] Native library configuration complete!");
                
                // Mark as configured for this session
                SessionState.SetBool(CONFIGURED_FLAG_KEY, true);
            }
            finally
            {
                isConfiguring = false;
            }
        }
        
        private static bool ConfigureWindowsX64()
        {
            string dllPath = $"{OPUS_NATIVES_PATH}/win-x64/native/opus.dll";
            
            if (!File.Exists(dllPath))
            {
                Debug.LogWarning($"[OpusSharp] Windows x64 DLL not found at: {dllPath}");
                return false;
            }
            
            PluginImporter importer = AssetImporter.GetAtPath(dllPath) as PluginImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[OpusSharp] Could not get importer for: {dllPath}");
                return false;
            }
            
            // Check if already configured correctly
            if (importer.GetCompatibleWithEditor() && 
                importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64))
            {
                return false; // No changes needed
            }
            
            // Clear all settings first
            importer.ClearSettings();
            
            // Configure for Editor and Standalone Windows x64
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(true);
            importer.SetEditorData("CPU", "x86_64");
            importer.SetEditorData("OS", "Windows");
            
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true);
            importer.SetPlatformData(BuildTarget.StandaloneWindows64, "CPU", "x86_64");
            
            // Explicitly disable for other platforms
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows, false);
            importer.SetCompatibleWithPlatform(BuildTarget.Android, false);
            importer.SetCompatibleWithPlatform(BuildTarget.iOS, false);
            importer.SetCompatibleWithPlatform(BuildTarget.WebGL, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, false);
            
            importer.SaveAndReimport();
            Debug.Log($"[OpusSharp] Configured Windows x64 DLL: {dllPath}");
            return true;
        }
        
        private static bool ConfigureWindowsX86()
        {
            string dllPath = $"{OPUS_NATIVES_PATH}/win-x86/native/opus.dll";
            
            if (!File.Exists(dllPath))
            {
                Debug.LogWarning($"[OpusSharp] Windows x86 DLL not found at: {dllPath}");
                return false;
            }
            
            PluginImporter importer = AssetImporter.GetAtPath(dllPath) as PluginImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[OpusSharp] Could not get importer for: {dllPath}");
                return false;
            }
            
            // Check if already configured correctly
            if (importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows) && 
                !importer.GetCompatibleWithEditor())
            {
                return false; // No changes needed
            }
            
            // Clear all settings first
            importer.ClearSettings();
            
            // Configure for Standalone Windows x86 only
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(false); // x86 not for Editor
            
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows, true);
            importer.SetPlatformData(BuildTarget.StandaloneWindows, "CPU", "x86");
            
            // Explicitly disable for other platforms
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.Android, false);
            importer.SetCompatibleWithPlatform(BuildTarget.iOS, false);
            importer.SetCompatibleWithPlatform(BuildTarget.WebGL, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, false);
            
            importer.SaveAndReimport();
            Debug.Log($"[OpusSharp] Configured Windows x86 DLL: {dllPath}");
            return true;
        }
        
        private static bool ConfigureAndroidARM64()
        {
            string soPath = $"{OPUS_NATIVES_PATH}/android-arm64/native/libopus.so";
            
            if (!File.Exists(soPath))
            {
                Debug.LogWarning($"[OpusSharp] Android ARM64 SO not found at: {soPath}");
                return false;
            }
            
            PluginImporter importer = AssetImporter.GetAtPath(soPath) as PluginImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[OpusSharp] Could not get importer for: {soPath}");
                return false;
            }
            
            // Check if already configured correctly
            if (importer.GetCompatibleWithPlatform(BuildTarget.Android) && 
                !importer.GetCompatibleWithEditor())
            {
                return false; // No changes needed
            }
            
            // Clear all settings first
            importer.ClearSettings();
            
            // Configure for Android ARM64 only
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(false);
            
            importer.SetCompatibleWithPlatform(BuildTarget.Android, true);
            importer.SetPlatformData(BuildTarget.Android, "CPU", "ARM64");
            
            // Explicitly disable for other platforms
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows, false);
            importer.SetCompatibleWithPlatform(BuildTarget.iOS, false);
            importer.SetCompatibleWithPlatform(BuildTarget.WebGL, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, false);
            
            importer.SaveAndReimport();
            Debug.Log($"[OpusSharp] Configured Android ARM64 SO: {soPath}");
            return true;
        }
        
        private static bool ConfigureLinuxX64()
        {
            string soPath = $"{OPUS_NATIVES_PATH}/linux-x64/native/opus.so";
            
            if (!File.Exists(soPath))
            {
                Debug.LogWarning($"[OpusSharp] Linux x64 SO not found at: {soPath}");
                return false;
            }
            
            PluginImporter importer = AssetImporter.GetAtPath(soPath) as PluginImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[OpusSharp] Could not get importer for: {soPath}");
                return false;
            }
            
            // Check if already configured correctly
            if (importer.GetCompatibleWithPlatform(BuildTarget.StandaloneLinux64) && 
                !importer.GetCompatibleWithEditor())
            {
                return false; // No changes needed
            }
            
            // Clear all settings first
            importer.ClearSettings();
            
            // Configure for Linux x64 only
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(false);
            
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, true);
            importer.SetPlatformData(BuildTarget.StandaloneLinux64, "CPU", "x86_64");
            
            // Explicitly disable for other platforms
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows, false);
            importer.SetCompatibleWithPlatform(BuildTarget.Android, false);
            importer.SetCompatibleWithPlatform(BuildTarget.iOS, false);
            importer.SetCompatibleWithPlatform(BuildTarget.WebGL, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, false);
            
            importer.SaveAndReimport();
            Debug.Log($"[OpusSharp] Configured Linux x64 SO: {soPath}");
            return true;
        }
    }
}