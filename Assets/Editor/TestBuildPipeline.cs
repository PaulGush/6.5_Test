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
        private const string ConfigPath = "Assets/Editor/TestBuildConfig.asset";

        // Publish settings live in an asset (TestBuildConfig), auto-created here so
        // "fill in the Inspector" is the whole setup story.
        private static TestBuildConfig Config
        {
            get
            {
                var cfg = AssetDatabase.LoadAssetAtPath<TestBuildConfig>(ConfigPath);
                if (cfg == null)
                {
                    cfg = ScriptableObject.CreateInstance<TestBuildConfig>();
                    AssetDatabase.CreateAsset(cfg, ConfigPath);
                    AssetDatabase.SaveAssets();
                }
                return cfg;
            }
        }

        [MenuItem("Tools/Ship/Build Test Builds (Win + Linux)")]
        public static void BuildAllMenu()
        {
            string failure = BuildAll();
            if (failure == null)
                EditorUtility.RevealInFinder(Path.Combine(RepoRoot, "Builds"));
            else
                EditorUtility.DisplayDialog("Test builds", failure, "OK");
        }

        // The config is an asset instance, not the script — this creates it on first
        // use and selects it, so the Itch Target field is right there to fill in.
        [MenuItem("Tools/Ship/itch Publish Settings")]
        public static void SelectConfigMenu()
        {
            TestBuildConfig cfg = Config;
            Selection.activeObject = cfg;
            EditorGUIUtility.PingObject(cfg);
        }

        [MenuItem("Tools/Ship/Build + Push To itch.io")]
        public static void BuildAndPushMenu()
        {
            TestBuildConfig cfg = Config;
            if (string.IsNullOrWhiteSpace(cfg.itchTarget))
            {
                Selection.activeObject = cfg;
                EditorGUIUtility.PingObject(cfg);
                EditorUtility.DisplayDialog("itch.io push",
                    "One-time setup first:\n\n" +
                    "1. Create the project on itch.io (kind: downloadable, visibility: Restricted).\n" +
                    "2. Install butler and run 'butler login' once.\n" +
                    "3. Fill in Itch Target on the TestBuildConfig asset just selected\n" +
                    "    in the Project window (\"username/game-slug\").\n\n" +
                    "Then this menu builds and uploads both platforms in one go.", "OK");
                return;
            }
            string target = NormalizeItchTarget(cfg.itchTarget);
            string failure = BuildAll() ?? PushAll(target);
            if (failure != null)
                EditorUtility.DisplayDialog("itch.io push", failure, "OK");
            else
                EditorUtility.DisplayDialog("itch.io push",
                    $"Pushed {_lastVersion} to {target} (win64 + linux64).", "OK");
        }

        // butler wants "username/game-slug"; people naturally paste the page URL
        // ("https://username.itch.io/game-slug") — accept both.
        private static string NormalizeItchTarget(string raw)
        {
            string t = raw.Trim().TrimEnd('/');
            var m = System.Text.RegularExpressions.Regex.Match(
                t, @"^https?://([^./]+)\.itch\.io/(.+)$");
            return m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : t;
        }

        // Unity's GUI process doesn't reliably inherit the shell's PATH, so prefer the
        // no-sudo install spot (~/.local/bin) when it exists, PATH lookup otherwise.
        private static string ButlerPath()
        {
            string local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "bin", "butler");
            return File.Exists(local) ? local : "butler";
        }

        // butler diffs against the previous push, so testers only download what changed.
        private static string PushAll(string itchTarget)
        {
            foreach (string platform in new[] { "win64", "linux64" })
            {
                string dir = Path.Combine(RepoRoot, "Builds", platform, ProductDirName);
                string args = $"push \"{dir}\" {itchTarget}:{platform} --userversion {_lastVersion}";
                try
                {
                    var psi = new ProcessStartInfo(ButlerPath(), args)
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
                    return "butler not found (checked ~/.local/bin and PATH). Install it " +
                           "from https://itch.io/docs/butler/ then run 'butler login' once.";
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
