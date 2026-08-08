using System.Linq;
using UnityEditor;
using UnityEngine;

// Temporary check for the swim-clip re-export of HumanoidAnims.fbx:
// pinned clip fileIDs must be unchanged, the two swim clips must import, and
// muscle curves must stay in sane humanoid range (the T-pose regression from
// 2026-08-04 showed up here as stretch >1 / thigh twist -1). Delete after use.
// Run from the menu (Tools > Validate Swim Export) or headlessly via
// -batchmode -executeMethod SwimExportValidator.Run.
public static class SwimExportValidator
{
    [MenuItem("Tools/Validate Swim Export")]
    public static void RunFromMenu() => Run();

    private static readonly (string name, long fileId)[] Expected =
    {
        ("Idle_A", 7400000), ("Walk", 7400002), ("Jog", 7400004), ("Sprint", 7400006),
        ("Walk_Backwards", 7400008), ("Walk_Carry", 7400010), ("Crouch_Idle", 7400012),
        ("Crouch_Walk", 7400014), ("Jump_Start", 7400016), ("Jump_Air", 7400018),
        ("Jump_Land", 7400020), ("Climb_Ladder", 7400022), ("Ladder_Idle", 7400024),
        ("Swim_Fwd", 7400026), ("Swim_Idle", 7400028),
    };

    public static void Run()
    {
        const string path = "Assets/Art/Animations/HumanoidAnims.fbx";
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var clips = AssetDatabase.LoadAllAssetRepresentationsAtPath(path)
            .OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview")).ToList();

        var sb = new System.Text.StringBuilder();
        bool ok = true;
        foreach (var (name, wantId) in Expected)
        {
            var clip = clips.FirstOrDefault(c => c.name == name);
            if (clip == null)
            {
                sb.AppendLine($"FAIL missing clip {name}");
                ok = false;
                continue;
            }
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out _, out long fid);
            sb.AppendLine($"{name}: fileID={fid} len={clip.length:F2}s loop={clip.isLooping}");
            if (fid != wantId)
            {
                sb.AppendLine($"FAIL {name} fileID {fid} != pinned {wantId}");
                ok = false;
            }
        }

        foreach (var name in new[] { "Walk", "Swim_Fwd", "Swim_Idle" })
        {
            var clip = clips.FirstOrDefault(c => c.name == name);
            if (clip == null) continue;
            float worst = 0f, worstTwist = 0f;
            string worstProp = "", twistProp = "";
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                if (b.type != typeof(Animator) || b.propertyName.Contains("RootT") || b.propertyName.Contains("RootQ"))
                    continue;
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                if (curve == null) continue;
                foreach (var k in curve.keys)
                {
                    float a = Mathf.Abs(k.value);
                    if (a > worst) { worst = a; worstProp = b.propertyName; }
                    if (b.propertyName.Contains("Twist") && b.propertyName.Contains("Leg") && a > worstTwist)
                    {
                        worstTwist = a; twistProp = b.propertyName;
                    }
                }
            }
            sb.AppendLine($"{name}: max|muscle|={worst:F3} ({worstProp}) maxLegTwist={worstTwist:F3} ({twistProp})");
            if (worst > 1.35f)
            {
                sb.AppendLine($"FAIL {name} muscle range");
                ok = false;
            }
        }

        sb.AppendLine(ok ? "RESULT OK" : "RESULT FAIL");
        Debug.Log("[SwimExportValidator]\n" + sb);
        System.IO.File.WriteAllText("Logs/swim-export-validation.txt", sb.ToString());
        if (Application.isBatchMode)
            EditorApplication.Exit(ok ? 0 : 2);
    }
}
