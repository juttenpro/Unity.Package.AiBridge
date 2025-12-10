using UnityEngine;
using UnityEditor;
using System.IO;

namespace OpusSharp.Editor
{
    /// <summary>
    /// Automatically installs Opus native libraries to Assets/Plugins/OpusSharp/ folder.
    /// This ensures libraries are in a writable location that works with all package installation methods.
    ///
    /// Why Assets/Plugins instead of package folder:
    /// - Package folder may be immutable (git cache, read-only)
    /// - Assets/Plugins is always writable
    /// - Unity automatically handles platform-specific plugin loading from this location
    /// - macOS users can add their libopus.dylib without modifying the package
    /// </summary>
    [InitializeOnLoad]
    public static class OpusPluginInstaller
    {
        private const string PackageNativesPath = "Packages/com.simulationcrew.aibridge/Plugins/OpusSharp/OpusSharp.Natives/runtimes";
        private const string TargetPluginsPath = "Assets/Plugins/OpusSharp";
        private const string InstalledVersionKey = "OpusSharp.InstalledVersion";
        private const string CurrentVersion = "1.1.15";

        static OpusPluginInstaller()
        {
            EditorApplication.delayCall += CheckAndInstallPlugins;
        }

        [MenuItem("Tools/OpusSharp/Install Native Libraries to Project")]
        public static void InstallPluginsMenu()
        {
            InstallPlugins(forceReinstall: true);
        }

        private static void CheckAndInstallPlugins()
        {
            string installedVersion = EditorPrefs.GetString(InstalledVersionKey, "");

            // Check if we need to install or update
            if (installedVersion != CurrentVersion || !PluginsExist())
            {
                InstallPlugins(forceReinstall: false);
            }
            else
            {
                // Just verify macOS status on Mac
#if UNITY_EDITOR_OSX
                CheckMacOSLibrary();
#endif
            }
        }

        private static bool PluginsExist()
        {
            // Check if at least the Windows x64 DLL exists (included in package)
            return File.Exists(Path.Combine(TargetPluginsPath, "Windows/x86_64/opus.dll"));
        }

        private static void InstallPlugins(bool forceReinstall)
        {
            Debug.Log("[OpusSharp] Checking native library installation...");

            bool anyInstalled = false;

            // Create base directory
            if (!Directory.Exists(TargetPluginsPath))
            {
                Directory.CreateDirectory(TargetPluginsPath);
            }

            // Install Windows x64
            anyInstalled |= InstallLibrary(
                $"{PackageNativesPath}/win-x64/native/opus.dll",
                $"{TargetPluginsPath}/Windows/x86_64/opus.dll",
                "Windows x64"
            );

            // Install Windows x86
            anyInstalled |= InstallLibrary(
                $"{PackageNativesPath}/win-x86/native/opus.dll",
                $"{TargetPluginsPath}/Windows/x86/opus.dll",
                "Windows x86"
            );

            // Install Linux x64
            anyInstalled |= InstallLibrary(
                $"{PackageNativesPath}/linux-x64/native/opus.so",
                $"{TargetPluginsPath}/Linux/x86_64/opus.so",
                "Linux x64"
            );

            // Install Android ARM64
            anyInstalled |= InstallLibrary(
                $"{PackageNativesPath}/android-arm64/native/libopus.so",
                $"{TargetPluginsPath}/Android/ARM64/libopus.so",
                "Android ARM64"
            );

            // macOS - check if user has already added the library
            bool macOSInstalled = InstallMacOSLibraryIfAvailable();
            anyInstalled |= macOSInstalled;

            if (anyInstalled)
            {
                AssetDatabase.Refresh();
                Debug.Log("[OpusSharp] Native libraries installed to Assets/Plugins/OpusSharp/");

                // Configure the installed plugins
                ConfigureInstalledPlugins();
            }

            // Mark as installed
            EditorPrefs.SetString(InstalledVersionKey, CurrentVersion);

#if UNITY_EDITOR_OSX
            if (!macOSInstalled)
            {
                CheckMacOSLibrary();
            }
#endif
        }

        private static bool InstallLibrary(string sourcePath, string targetPath, string platformName)
        {
            // Skip if already exists
            if (File.Exists(targetPath))
            {
                return false;
            }

            // Check if source exists in package
            if (!File.Exists(sourcePath))
            {
                // This is expected for macOS (not included in package)
                return false;
            }

            try
            {
                string targetDir = Path.GetDirectoryName(targetPath);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(sourcePath, targetPath, overwrite: false);
                Debug.Log($"[OpusSharp] Installed {platformName} library: {targetPath}");
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[OpusSharp] Failed to install {platformName} library: {ex.Message}");
                return false;
            }
        }

        private static bool InstallMacOSLibraryIfAvailable()
        {
            // Check both architectures from package (in case they were added)
            bool anyInstalled = false;

            // ARM64 (Apple Silicon)
            string arm64Source = $"{PackageNativesPath}/osx-arm64/native/libopus.dylib";
            string arm64Target = $"{TargetPluginsPath}/macOS/libopus.dylib";

            if (File.Exists(arm64Source) && !File.Exists(arm64Target))
            {
                anyInstalled |= InstallLibrary(arm64Source, arm64Target, "macOS ARM64");
            }

            // x64 (Intel) - same target file, Universal Binary preferred
            string x64Source = $"{PackageNativesPath}/osx-x64/native/libopus.dylib";

            if (!File.Exists(arm64Target) && File.Exists(x64Source))
            {
                anyInstalled |= InstallLibrary(x64Source, arm64Target, "macOS x64");
            }

            return anyInstalled;
        }

#if UNITY_EDITOR_OSX
        private static void CheckMacOSLibrary()
        {
            string macOSLibPath = $"{TargetPluginsPath}/macOS/libopus.dylib";

            if (File.Exists(macOSLibPath))
            {
                return; // Already installed
            }

            // Check if Homebrew opus is available
            bool isAppleSilicon = SystemInfo.processorType.Contains("Apple") ||
                                  System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                                  System.Runtime.InteropServices.Architecture.Arm64;

            string homebrewPath = isAppleSilicon ? "/opt/homebrew" : "/usr/local";
            string homebrewLibPath = $"{homebrewPath}/lib/libopus.dylib";

            if (File.Exists(homebrewLibPath))
            {
                // Homebrew opus found - offer to install
                bool install = EditorUtility.DisplayDialog(
                    "OpusSharp - macOS Library Found",
                    "Opus library found via Homebrew.\n\n" +
                    "Would you like to install it to your Unity project?\n\n" +
                    $"Source: {homebrewLibPath}\n" +
                    $"Target: {macOSLibPath}",
                    "Install",
                    "Later"
                );

                if (install)
                {
                    InstallMacOSFromHomebrew(homebrewLibPath, macOSLibPath);
                }
            }
            else
            {
                // No Homebrew opus - show setup instructions
                Debug.LogWarning(
                    "[OpusSharp] macOS: Opus native library not found.\n" +
                    "Audio encoding/decoding will not work until installed.\n\n" +
                    "To install:\n" +
                    "1. Run: brew install opus\n" +
                    "2. Use menu: Tools > OpusSharp > Setup macOS Library (Homebrew)\n\n" +
                    "Or manually copy libopus.dylib to: Assets/Plugins/OpusSharp/macOS/"
                );
            }
        }

        [MenuItem("Tools/OpusSharp/Setup macOS Library (Homebrew)")]
        public static void SetupMacOSLibraryMenu()
        {
            bool isAppleSilicon = SystemInfo.processorType.Contains("Apple") ||
                                  System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                                  System.Runtime.InteropServices.Architecture.Arm64;

            string homebrewPath = isAppleSilicon ? "/opt/homebrew" : "/usr/local";
            string homebrewLibPath = $"{homebrewPath}/lib/libopus.dylib";
            string targetPath = $"{TargetPluginsPath}/macOS/libopus.dylib";

            if (!File.Exists(homebrewLibPath))
            {
                bool install = EditorUtility.DisplayDialog(
                    "Opus Library Not Found",
                    "The Opus library is not installed via Homebrew.\n\n" +
                    "Would you like to install it now?\n\n" +
                    "This will run: brew install opus",
                    "Install via Homebrew",
                    "Cancel"
                );

                if (install)
                {
                    InstallOpusViaHomebrew();
                }
                return;
            }

            InstallMacOSFromHomebrew(homebrewLibPath, targetPath);
        }

        private static void InstallMacOSFromHomebrew(string sourcePath, string targetPath)
        {
            try
            {
                string targetDir = Path.GetDirectoryName(targetPath);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(sourcePath, targetPath, overwrite: true);
                AssetDatabase.Refresh();

                // Configure the plugin
                ConfigureMacOSPlugin(targetPath);

                Debug.Log($"[OpusSharp] Successfully installed macOS library: {targetPath}");

                EditorUtility.DisplayDialog(
                    "Success",
                    "Opus library installed successfully!\n\n" +
                    $"Location: {targetPath}",
                    "OK"
                );
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[OpusSharp] Failed to install macOS library: {ex.Message}");
                EditorUtility.DisplayDialog(
                    "Error",
                    $"Failed to install library:\n{ex.Message}",
                    "OK"
                );
            }
        }

        private static void InstallOpusViaHomebrew()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/zsh",
                    Arguments = "-c \"brew install opus\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        Debug.Log($"[OpusSharp] Homebrew install complete: {output}");
                        EditorUtility.DisplayDialog(
                            "Homebrew Install Complete",
                            "Opus has been installed via Homebrew.\n\n" +
                            "Click OK, then use:\nTools > OpusSharp > Setup macOS Library (Homebrew)",
                            "OK"
                        );
                    }
                    else
                    {
                        Debug.LogError($"[OpusSharp] Homebrew install failed: {error}");
                        EditorUtility.DisplayDialog(
                            "Installation Failed",
                            $"Failed to install opus via Homebrew.\n\nError: {error}\n\n" +
                            "Please install manually:\nbrew install opus",
                            "OK"
                        );
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[OpusSharp] Failed to run Homebrew: {ex.Message}");
                EditorUtility.DisplayDialog(
                    "Error",
                    "Could not run Homebrew.\n\n" +
                    "Please ensure Homebrew is installed:\nhttps://brew.sh\n\n" +
                    "Then run: brew install opus",
                    "OK"
                );
            }
        }

        private static void ConfigureMacOSPlugin(string pluginPath)
        {
            AssetDatabase.Refresh();

            var importer = AssetImporter.GetAtPath(pluginPath) as PluginImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[OpusSharp] Could not get importer for: {pluginPath}");
                return;
            }

            importer.ClearSettings();
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(true);
            importer.SetEditorData("OS", "OSX");
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true);

            // Disable for all other platforms
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows, false);
            importer.SetCompatibleWithPlatform(BuildTarget.Android, false);
            importer.SetCompatibleWithPlatform(BuildTarget.iOS, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, false);

            importer.SaveAndReimport();
            Debug.Log($"[OpusSharp] Configured macOS plugin: {pluginPath}");
        }
#endif

        private static void ConfigureInstalledPlugins()
        {
            // Windows x64
            ConfigurePlugin(
                $"{TargetPluginsPath}/Windows/x86_64/opus.dll",
                BuildTarget.StandaloneWindows64,
                editorEnabled: true,
                editorOS: "Windows",
                editorCPU: "x86_64"
            );

            // Windows x86
            ConfigurePlugin(
                $"{TargetPluginsPath}/Windows/x86/opus.dll",
                BuildTarget.StandaloneWindows,
                editorEnabled: false,
                editorOS: "Windows",
                editorCPU: "x86"
            );

            // Linux x64
            ConfigurePlugin(
                $"{TargetPluginsPath}/Linux/x86_64/opus.so",
                BuildTarget.StandaloneLinux64,
                editorEnabled: false,
                editorOS: "Linux",
                editorCPU: "x86_64"
            );

            // Android ARM64
            ConfigurePlugin(
                $"{TargetPluginsPath}/Android/ARM64/libopus.so",
                BuildTarget.Android,
                editorEnabled: false,
                platformCPU: "ARM64"
            );

            // macOS (if exists)
            string macOSPath = $"{TargetPluginsPath}/macOS/libopus.dylib";
            if (File.Exists(macOSPath))
            {
                ConfigurePlugin(
                    macOSPath,
                    BuildTarget.StandaloneOSX,
                    editorEnabled: true,
                    editorOS: "OSX"
                );
            }
        }

        private static void ConfigurePlugin(
            string pluginPath,
            BuildTarget buildTarget,
            bool editorEnabled,
            string editorOS = null,
            string editorCPU = null,
            string platformCPU = null)
        {
            if (!File.Exists(pluginPath))
            {
                return;
            }

            var importer = AssetImporter.GetAtPath(pluginPath) as PluginImporter;
            if (importer == null)
            {
                return;
            }

            // Check if already configured
            if (importer.GetCompatibleWithPlatform(buildTarget) == true)
            {
                return;
            }

            importer.ClearSettings();
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(editorEnabled);

            if (editorEnabled && editorOS != null)
            {
                importer.SetEditorData("OS", editorOS);
            }
            if (editorEnabled && editorCPU != null)
            {
                importer.SetEditorData("CPU", editorCPU);
            }

            importer.SetCompatibleWithPlatform(buildTarget, true);
            if (platformCPU != null)
            {
                importer.SetPlatformData(buildTarget, "CPU", platformCPU);
            }

            // Disable for all other platforms explicitly
            var allTargets = new[] {
                BuildTarget.StandaloneWindows64,
                BuildTarget.StandaloneWindows,
                BuildTarget.StandaloneLinux64,
                BuildTarget.StandaloneOSX,
                BuildTarget.Android,
                BuildTarget.iOS,
                BuildTarget.WebGL
            };

            foreach (var target in allTargets)
            {
                if (target != buildTarget)
                {
                    importer.SetCompatibleWithPlatform(target, false);
                }
            }

            importer.SaveAndReimport();
            Debug.Log($"[OpusSharp] Configured plugin: {pluginPath}");
        }
    }
}
