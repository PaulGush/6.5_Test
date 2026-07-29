#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

// Fully qualified: Mirror's PostInstallScript declares a top-level `Editor` NAMESPACE,
// which otherwise shadows the UnityEditor.Editor base type here and breaks compilation.
[CustomEditor(typeof(SyntyPackageHelperConfig))]
public class ExamplePackConfigLoaderEditor : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        SyntyPackageHelperConfig config = (SyntyPackageHelperConfig)target;
        if (GUILayout.Button("Install Packages"))
        {
            SyntyPackageHelper.ProcessConfigs(new SyntyPackageHelperConfig[] { config }, true);
        }
    }
}

[CreateAssetMenu(fileName = "SyntyPackageHelperConfig", menuName = "Scriptable Objects/SyntyPackageHelperConfig")]
public class SyntyPackageHelperConfig : ScriptableObject
{
    public string assetPackDisplayName;
    public string[] packageIds;
    public bool hasPromptedUser;
}
#endif