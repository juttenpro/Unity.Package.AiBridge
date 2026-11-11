using System.IO;
using System.Linq;
using NUnit.Framework;
using OpusSharp.Editor;
using UnityEditor;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// WHY: a 32-bit Android phone (Galaxy A13, SM-A136B: Samsung runs 32-bit Android on it) cannot install an app
    /// without an armeabi-v7a Opus library ("App is niet geschikt voor je telefoon"). The package must ship one, the
    /// installer must copy it for CPU ARMv7, and the package copy must stay out of Android builds (duplicate plugin).
    /// Read-only: nothing is installed or written here; the slot checks the installed result in the project.
    /// Not covered: the APK itself (aapt check after the build) and a run on a 32-bit device.
    /// </summary>
    [TestFixture]
    public class OpusAndroidNativesTests
    {
        private const string Runtimes = "Packages/com.simulationcrew.aibridge/Plugins/OpusSharp/OpusSharp.Natives/runtimes";

        [Test]
        public void Installer_CopiesEveryShippedAbi_ForItsOwnCpu()
        {
            var table = OpusPluginInstaller.AndroidLibraries.ToDictionary(l => l.Runtime, l => l.Cpu);

            Assert.That(table, Does.ContainKey("android-arm"),
                "No ARMv7 Opus library: a 32-bit Android phone cannot install the app.");
            Assert.That(table["android-arm"], Is.EqualTo("ARMv7"));
            Assert.That(table, Does.ContainKey("android-arm64"),
                "The ARM64 Opus library is gone: no 64-bit phone can encode the user's voice.");
            Assert.That(table["android-arm64"], Is.EqualTo("ARM64"));
            Assert.That(OpusPluginInstaller.AndroidLibraries.Select(l => l.TargetPath).Distinct().Count(),
                Is.EqualTo(OpusPluginInstaller.AndroidLibraries.Count),
                "Two ABIs copy to the same file: one overwrites the other in the build.");
        }

        [Test]
        public void EveryAbiInTheTable_IsInThePackage_AndExcludedFromAndroidThere()
        {
            Assert.That(OpusPluginInstaller.AndroidLibraries.Select(l => l.Runtime),
                Is.EquivalentTo(new[] { "android-arm64", "android-arm" }));
            foreach (var library in OpusPluginInstaller.AndroidLibraries)
            {
                Assert.That(File.Exists(library.SourcePath), Is.True,
                    $"The package has no {library.Runtime} Opus library: the installer has nothing to copy.");
                var importer = AssetImporter.GetAtPath(library.SourcePath) as PluginImporter;
                Assert.That(importer, Is.Not.Null, $"{library.SourcePath} has no plugin importer (.meta missing?).");
                Assert.That(importer.GetCompatibleWithPlatform(BuildTarget.Android), Is.False,
                    $"The package copy of {library.Runtime} is enabled for Android: a duplicate plugin next to the installed one.");
            }
        }
    }
}
