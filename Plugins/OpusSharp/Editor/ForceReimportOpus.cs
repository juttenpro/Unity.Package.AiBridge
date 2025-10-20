using UnityEngine;
using UnityEditor;

namespace OpusSharp.Editor
{
    /// <summary>
    /// Temporary script to force reimport of Opus native libraries
    /// </summary>
    public static class ForceReimportOpus
    {
        [MenuItem("Tools/OpusSharp/Force Reimport Native DLLs")]
        public static void ForceReimport()
        {
            Debug.Log("[OpusSharp] Force reimporting native DLLs...");
            
            // Windows x64 DLL
            string win64Path = "Packages/com.simulationcrew.aibridge/Plugins/OpusSharp/OpusSharp.Natives/runtimes/win-x64/native/opus.dll";
            
            // Delete the .meta file to force Unity to reimport
            string metaPath = win64Path + ".meta";
            if (System.IO.File.Exists(metaPath))
            {
                System.IO.File.Delete(metaPath);
                Debug.Log($"[OpusSharp] Deleted meta file: {metaPath}");
            }
            
            // Refresh to regenerate meta
            AssetDatabase.Refresh();
            
            // Now configure the importer
            PluginImporter importer = AssetImporter.GetAtPath(win64Path) as PluginImporter;
            if (importer != null)
            {
                Debug.Log($"[OpusSharp] Configuring importer for: {win64Path}");
                
                // Clear all settings
                importer.ClearSettings();
                
                // Set for Windows x64 and Editor
                importer.SetCompatibleWithAnyPlatform(false);
                importer.SetCompatibleWithEditor(true);
                importer.SetEditorData("CPU", "x86_64");
                importer.SetEditorData("OS", "Windows");
                
                importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true);
                importer.SetPlatformData(BuildTarget.StandaloneWindows64, "CPU", "x86_64");
                
                // Apply and reimport
                importer.SaveAndReimport();
                
                Debug.Log("[OpusSharp] DLL import settings applied successfully!");
                Debug.Log("[OpusSharp] Please restart Unity if the DLL still doesn't load.");
            }
            else
            {
                Debug.LogError($"[OpusSharp] Could not get importer for: {win64Path}");
            }
        }
        
        [MenuItem("Tools/OpusSharp/Check DLL Status")]
        public static void CheckDLLStatus()
        {
            string win64Path = "Packages/com.simulationcrew.aibridge/Plugins/OpusSharp/OpusSharp.Natives/runtimes/win-x64/native/opus.dll";
            
            PluginImporter importer = AssetImporter.GetAtPath(win64Path) as PluginImporter;
            if (importer != null)
            {
                Debug.Log($"[OpusSharp] DLL Status for: {win64Path}");
                Debug.Log($"  - Compatible with Editor: {importer.GetCompatibleWithEditor()}");
                Debug.Log($"  - Compatible with Win64: {importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64)}");
                Debug.Log($"  - Compatible with Any Platform: {importer.GetCompatibleWithAnyPlatform()}");
                
                // Check if DLL can be loaded
                try
                {
                    var handle = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<System.Action>(System.IntPtr.Zero);
                    Debug.Log("[OpusSharp] DLL loading test passed");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[OpusSharp] DLL loading test failed: {e.Message}");
                }
            }
            else
            {
                Debug.LogError($"[OpusSharp] Could not find importer for: {win64Path}");
            }
        }
    }
}