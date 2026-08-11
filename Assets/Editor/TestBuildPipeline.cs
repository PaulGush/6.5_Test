using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Game.EditorTools
{
    /// <summary>
    /// Friend-test build pipeline: one menu click (or -executeMethod for CI later)
    /// produces versioned, zipped player builds ready to hand to testers.
    ///
    ///  - Windows x64 + Linux x64, Mono backend (IL2CPP for Windows can't be built
    ///    from a Linux editor).
    ///  - Version = git short hash + UTC date, stamped into the zip name, the
    ///    bundleVersion, and a version.txt beside the executable, so a tester can
    ///    always tell you exactly what they are running.
    ///  - steam_appid.txt (480/Spacewar) is copied next to each executable — the
    ///    Steam transport and lobby invites need it when launched outside Steam.
    ///
    /// Output: Builds/ShipGame_&lt;platform&gt;_&lt;version&gt;.zip (Builds/ is gitignored).
    /// </summary>
    public static class TestBuildPipeline
    {
        private const string ProductDirName = "PiecesOfFreight";

        // Your itch.io target, "username/game-slug" (create the project page on
        // itch.io first, visibility Restricted). Empty = the push menu explains setup.
        private const string ItchTarget = "";

        [MenuItem("Tools/Ship/Build Test Builds (Win + Linux)")]
        public static void BuildAllMenu()
        {
            string failure = BuildAll();
            if (failure == null)
                EditorUtility.RevealInFinder(Path.Combine(RepoRoot, "Builds"));
            else
                EditorUtility.DisplayDialog("Test builds", failure, "OK");
        }

        [MenuItem("Tools/Ship/Build + Push To itch.io")]
        public static void BuildAndPushMenu()
        {
            if (ItchTarget.Length == 0)
            {
                EditorUtility.DisplayDialog("itch.io push",
                    "One-time setup first:\n\n" +
                    "1. Create the project on itch.io (kind: downloadable, visibility: Restricted).\n" +
                    "2. Install butler and run 'butler login' once.\n" +
                    "3. Set ItchTarget in TestBuildPipeline.cs to \"username/game-slug\".\n\n" +
                    "Then this menu builds and uploads both platforms in one go.", "OK");
                return;
            }
            string failure = BuildAll() ?? PushAll();
            if (failure != null)
                EditorUtility.DisplayDialog("itch.io push", failure, "OK");
            else
                EditorUtility.DisplayDialog("itch.io push",
                    $"Pushed {_lastVersion} to {ItchTarget} (win64 + linux64).", "OK");
        }

        // butler diffs against the previous push, so testers only download what changed.
        private static string PushAll()
        {
            foreach (string platform in new[] { "win64", "linux64" })
            {
                string dir = Path.Combine(RepoRoot, "Builds", platform, ProductDirName);
                string args = $"push \"{dir}\" {ItchTarget}:{platform} --userversion {_lastVersion}";
                try
                {
                    var psi = new ProcessStartInfo("butler", args)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };
                    using Process p = Process.Start(psi);
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                        return $"butler push {platform} failed (exit {p.ExitCode}) — " +
                               "not logged in? Run 'butler login' in a terminal.\n\n" + output;
                    Debug.Log($"[TestBuildPipeline] butler {platform}:\n{output}");
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return "butler not found on PATH. Install it from " +
                           "https://itch.io/docs/butler/ then run 'butler login' once.";
                }
            }
            return null;
        }

        /// <summary>CI entry point: Unity -batchmode -quit -executeMethod
        /// Game.EditorTools.TestBuildPipeline.BuildAllBatch</summary>
        public static void BuildAllBatch()
        {
            string failure = BuildAll();
            if (failure != null)
            {
                Debug.LogError($"[TestBuildPipeline] {failure}");
                EditorApplication.Exit(1);
            }
        }

        private static string RepoRoot => Path.GetDirectoryName(Application.dataPath);
        private static string _lastVersion = "";

        private static string BuildAll()
        {
            string version = Version();
            _lastVersion = version;
            PlayerSettings.bundleVersion = version;
            Debug.Log($"[TestBuildPipeline] Building version {version}");

            string failure = BuildOne(BuildTarget.StandaloneWindows64, "win64",
                ProductDirName + ".exe", version);
            if (failure == null)
                failure = BuildOne(BuildTarget.StandaloneLinux64, "linux64",
                    ProductDirName + ".x86_64", version);
            return failure;
        }

        private static string BuildOne(BuildTarget target, string platform, string exeName,
            string version)
        {
            string dir = Path.Combine(RepoRoot, "Builds", platform, ProductDirName);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            var options = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/SampleScene.unity" },
                locationPathName = Path.Combine(dir, exeName),
                target = target,
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                return $"{platform} build failed: {report.summary.result} " +
                       $"({report.summary.totalErrors} errors) — see the console/editor log. " +
                       "A missing platform module (Windows Mono build support) looks like " +
                       "'target not supported'.";

            // Steam transport needs the appid beside the executable when the game is
            // launched directly rather than through the Steam library.
            File.Copy(Path.Combine(RepoRoot, "steam_appid.txt"),
                Path.Combine(dir, "steam_appid.txt"), true);
            File.WriteAllText(Path.Combine(dir, "version.txt"),
                version + Environment.NewLine);

            string zip = Path.Combine(RepoRoot, "Builds",
                $"{ProductDirName}_{platform}_{version}.zip");
            if (File.Exists(zip)) File.Delete(zip);
            System.IO.Compression.ZipFile.CreateFromDirectory(
                Path.Combine(RepoRoot, "Builds", platform), zip);
            Debug.Log($"[TestBuildPipeline] {platform}: {zip} " +
                      $"({new FileInfo(zip).Length / (1024 * 1024)} MB)");
            return null;
        }

        // "abc1234-20260811" (plus "-dirty" for uncommitted changes) — enough to map any
        // tester report back to an exact commit.
        private static string Version()
        {
            string hash = "nogit";
            try
            {
                var psi = new ProcessStartInfo("git", "describe --always --dirty")
                {
                    WorkingDirectory = RepoRoot,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };
                using Process p = Process.Start(psi);
                hash = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TestBuildPipeline] git version lookup failed: {e.Message}");
            }
            return $"{hash}-{DateTime.UtcNow:yyyyMMdd}";
        }
    }
}
