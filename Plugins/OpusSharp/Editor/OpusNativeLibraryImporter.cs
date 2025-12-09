using UnityEngine;
using UnityEditor;
using System.IO;
using System.Diagnostics;

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

#if UNITY_EDITOR_OSX
        [MenuItem("Tools/OpusSharp/Setup macOS Libraries (Homebrew)")]
        public static void SetupMacOSLibraries()
        {
            bool isAppleSilicon = SystemInfo.processorType.Contains("Apple") ||
                                  System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64;

            string homebrewPath = isAppleSilicon ? "/opt/homebrew" : "/usr/local";
            string sourcePath = $"{homebrewPath}/lib/libopus.dylib";
            string targetFolder = isAppleSilicon ? "osx-arm64" : "osx-x64";
            string targetPath = $"{OPUS_NATIVES_PATH}/{targetFolder}/native/libopus.dylib";

            // Check if Homebrew opus is installed
            if (!File.Exists(sourcePath))
            {
                bool install = EditorUtility.DisplayDialog(
                    "Opus Library Not Found",
                    "The Opus library is not installed via Homebrew.\n\n" +
                    "Would you like to install it now?\n\n" +
                    "This will run: brew install opus",
                    "Install",
                    "Cancel");

                if (install)
                {
                    InstallOpusViaHomebrew();
                }
                return;
            }

            // Copy the library
            try
            {
                string targetDir = Path.GetDirectoryName(targetPath);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(sourcePath, targetPath, overwrite: true);
                UnityEngine.Debug.Log($"[OpusSharp] Successfully copied libopus.dylib to {targetPath}");

                AssetDatabase.Refresh();
                ConfigureNativeLibraries(true);

                EditorUtility.DisplayDialog(
                    "Success",
                    $"Opus library installed successfully!\n\nArchitecture: {(isAppleSilicon ? "Apple Silicon (ARM64)" : "Intel (x64)")}",
                    "OK");
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[OpusSharp] Failed to copy libopus.dylib: {ex.Message}");
                EditorUtility.DisplayDialog(
                    "Error",
                    $"Failed to copy library: {ex.Message}",
                    "OK");
            }
        }

        private static void InstallOpusViaHomebrew()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "/bin/zsh",
                    Arguments = "-c \"brew install opus\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        UnityEngine.Debug.Log($"[OpusSharp] Homebrew install complete: {output}");
                        EditorUtility.DisplayDialog(
                            "Homebrew Install Complete",
                            "Opus has been installed via Homebrew.\n\n" +
                            "Click 'Setup macOS Libraries' again to copy the library to the project.",
                            "OK");
                    }
                    else
                    {
                        UnityEngine.Debug.LogError($"[OpusSharp] Homebrew install failed: {error}");
                        EditorUtility.DisplayDialog(
                            "Installation Failed",
                            $"Failed to install opus via Homebrew.\n\nError: {error}\n\n" +
                            "Please install manually by running:\nbrew install opus",
                            "OK");
                    }
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[OpusSharp] Failed to run Homebrew: {ex.Message}");
                EditorUtility.DisplayDialog(
                    "Error",
                    "Could not run Homebrew. Please ensure Homebrew is installed.\n\n" +
                    "Install Homebrew from: https://brew.sh\n\n" +
                    "Then run: brew install opus",
                    "OK");
            }
        }
#endif
        
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

                // macOS x64 (Intel)
                anyChanges |= ConfigureMacOSX64();

                // macOS ARM64 (Apple Silicon - M1/M2/M3)
                anyChanges |= ConfigureMacOSARM64();

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

        private static bool ConfigureMacOSX64()
        {
            string dylibPath = $"{OPUS_NATIVES_PATH}/osx-x64/native/libopus.dylib";

            if (!File.Exists(dylibPath))
            {
                // Only show warning on macOS - not relevant for Windows/Linux users
#if UNITY_EDITOR_OSX
                Debug.LogWarning($"[OpusSharp] macOS x64 dylib not found at: {dylibPath}");
                Debug.LogWarning("[OpusSharp] To enable macOS Intel support, run: Tools > OpusSharp > Setup macOS Libraries");
#endif
                return false;
            }

            PluginImporter importer = AssetImporter.GetAtPath(dylibPath) as PluginImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[OpusSharp] Could not get importer for: {dylibPath}");
                return false;
            }

            // Check if already configured correctly - for x64 we enable editor on Intel Macs
            bool isConfiguredForOSX = importer.GetCompatibleWithPlatform(BuildTarget.StandaloneOSX);
            if (isConfiguredForOSX)
            {
                return false; // No changes needed
            }

            // Clear all settings first
            importer.ClearSettings();

            // Configure for macOS x64 (Intel) - also enable for Editor on Intel Macs
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(true);
            importer.SetEditorData("CPU", "x86_64");
            importer.SetEditorData("OS", "OSX");

            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true);
            importer.SetPlatformData(BuildTarget.StandaloneOSX, "CPU", "x86_64");

            // Explicitly disable for other platforms
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows, false);
            importer.SetCompatibleWithPlatform(BuildTarget.Android, false);
            importer.SetCompatibleWithPlatform(BuildTarget.iOS, false);
            importer.SetCompatibleWithPlatform(BuildTarget.WebGL, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, false);

            importer.SaveAndReimport();
            Debug.Log($"[OpusSharp] Configured macOS x64 (Intel) dylib: {dylibPath}");
            return true;
        }

        private static bool ConfigureMacOSARM64()
        {
            string dylibPath = $"{OPUS_NATIVES_PATH}/osx-arm64/native/libopus.dylib";

            if (!File.Exists(dylibPath))
            {
                // Only show warning on macOS - not relevant for Windows/Linux users
#if UNITY_EDITOR_OSX
                Debug.LogWarning($"[OpusSharp] macOS ARM64 dylib not found at: {dylibPath}");
                Debug.LogWarning("[OpusSharp] To enable macOS Apple Silicon support, run: Tools > OpusSharp > Setup macOS Libraries");
#endif
                return false;
            }

            PluginImporter importer = AssetImporter.GetAtPath(dylibPath) as PluginImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[OpusSharp] Could not get importer for: {dylibPath}");
                return false;
            }

            // Check if already configured correctly
            bool isConfiguredForOSX = importer.GetCompatibleWithPlatform(BuildTarget.StandaloneOSX);
            if (isConfiguredForOSX)
            {
                return false; // No changes needed
            }

            // Clear all settings first
            importer.ClearSettings();

            // Configure for macOS ARM64 (Apple Silicon M1/M2/M3)
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(true);
            importer.SetEditorData("CPU", "ARM64");
            importer.SetEditorData("OS", "OSX");

            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true);
            importer.SetPlatformData(BuildTarget.StandaloneOSX, "CPU", "ARM64");

            // Explicitly disable for other platforms
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows, false);
            importer.SetCompatibleWithPlatform(BuildTarget.Android, false);
            importer.SetCompatibleWithPlatform(BuildTarget.iOS, false);
            importer.SetCompatibleWithPlatform(BuildTarget.WebGL, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, false);

            importer.SaveAndReimport();
            Debug.Log($"[OpusSharp] Configured macOS ARM64 (Apple Silicon) dylib: {dylibPath}");
            return true;
        }
    }
}