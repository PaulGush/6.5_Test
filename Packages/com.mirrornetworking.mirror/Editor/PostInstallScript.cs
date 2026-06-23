using System.IO;
using UnityEditor;

namespace Editor
{
    public static class PostInstallScript
    {
        [InitializeOnLoadMethod]
        public static void Execute()
        {
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(@"Packages/com.mirrornetworking.mirror");
            if (packageInfo != null)
            {
                var packagePath = packageInfo.resolvedPath;
                if (DeleteExcept(Path.Combine(packagePath, "Mirror"), Path.Combine(packagePath, "Mirror", "Assets")))
                {
                    AssetDatabase.Refresh();
                }
            }
        }

        private static bool DeleteExcept(string targetPath, string preservePath)
        {
            var isDelete = false;
            if (!Directory.Exists(targetPath)) return false;
            foreach (var directory in Directory.GetDirectories(targetPath))
            {
                if (Path.GetFileName(directory) != Path.GetFileName(preservePath))
                {
                    isDelete = true;
                    Directory.Delete(directory, true);
                }
            }
            foreach (var file in Directory.GetFiles(targetPath))
            {
                if (Path.GetFileName(file) != Path.GetFileName(preservePath))
                {
                    isDelete = true;
                    File.Delete(file);
                }
            }
            return isDelete;
        }
    }
}