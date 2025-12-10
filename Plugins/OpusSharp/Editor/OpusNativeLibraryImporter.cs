using UnityEngine;
using UnityEditor;
using System.IO;

namespace OpusSharp.Editor
{
    /// <summary>
    /// DEPRECATED: This class is kept for backward compatibility.
    /// Native library management has moved to OpusPluginInstaller which installs
    /// libraries to Assets/Plugins/OpusSharp/ instead of the package folder.
    ///
    /// The new approach:
    /// - Libraries are copied to Assets/Plugins/OpusSharp/ (always writable)
    /// - Works with all package installation methods (git, embedded, local)
    /// - macOS users can add their libopus.dylib without package modifications
    /// </summary>
    [InitializeOnLoad]
    public class OpusNativeLibraryImporter
    {
        private const string CONFIGURED_FLAG_KEY = "OpusSharp.LegacyImporterRan";

        static OpusNativeLibraryImporter()
        {
            // Only run once to show migration message if needed
            if (!SessionState.GetBool(CONFIGURED_FLAG_KEY, false))
            {
                EditorApplication.delayCall += CheckMigrationStatus;
                SessionState.SetBool(CONFIGURED_FLAG_KEY, true);
            }
        }

        private static void CheckMigrationStatus()
        {
            // Check if old package-based libraries exist but new ones don't
            string oldPath = "Packages/com.simulationcrew.aibridge/Plugins/OpusSharp/OpusSharp.Natives/runtimes/win-x64/native/opus.dll";
            string newPath = "Assets/Plugins/OpusSharp/Windows/x86_64/opus.dll";

            if (File.Exists(oldPath) && !File.Exists(newPath))
            {
                Debug.Log("[OpusSharp] Migrating native libraries to Assets/Plugins/OpusSharp/...");
                // OpusPluginInstaller will handle this automatically via its InitializeOnLoad
            }
        }

        // Keep menu item for users who might look for it, redirect to new installer
        [MenuItem("Tools/OpusSharp/Configure Native Libraries")]
        public static void ConfigureNativeLibrariesMenu()
        {
            OpusPluginInstaller.InstallPluginsMenu();
        }
    }
}
