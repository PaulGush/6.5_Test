using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mirror;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Game.Gameplay;
using Game.Ship;

namespace Game.EditorTools
{
    /// <summary>
    /// One-shot builder for the free-sailing vertical slice. Creates:
    ///  - Assets/Prefabs/Ship.prefab: Synty medium hull + generated deck/hull colliders (measured
    ///    by raycasting the actual meshes, so multi-level decks are approximated correctly),
    ///    boarding trigger, helm wheel, Rigidbody + Mirror networking + ShipController/ShipHelm.
    ///  - Player.prefab additions: ShipRider + PlayerHelmUser.
    ///  - A "Harbor" area in SampleScene: water plane + drown hazard, a dock with a checkpoint,
    ///    the ship, rock obstacles, and a background island.
    ///
    /// Runs automatically once (when Ship.prefab doesn't exist yet) after scripts compile, and can
    /// be re-run any time from the menu — it rebuilds the prefab and skips scene objects that
    /// already exist.
    /// </summary>
    public static class ShipTestAreaBuilder
    {
        private const string ShipPrefabPath = "Assets/Prefabs/Ship.prefab";
        private const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
        private const string HudPrefabPath = "Assets/Prefabs/GrabPromptHUD.prefab";
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";

        private const string SyntyRoot = "Assets/Synty/PolygonPirates/Prefabs";
        private const string HullPath = SyntyRoot + "/Vehicles/SM_Veh_Boat_Medium_01_Hull.prefab";
        private const string HullAttachmentsPath = SyntyRoot + "/Vehicles/SM_Veh_Boat_Medium_01_Hull_Attachments.prefab";
        private const string WheelPath = SyntyRoot + "/Props/SM_Prop_ShipWheel_01.prefab";

        private const string WaterMatPath = "Assets/Art/Materials/Sea_Water.mat";
        private const string WoodMatPath = "Assets/Art/Materials/Sea_DockWood.mat";

        private const float DockTopY = 0.25f;   // walkable dock height; the deck is aligned to it
        private const float RailHeight = 0.55f; // bulwark colliders: keeps cargo in, jumpable by players

        [InitializeOnLoadMethod]
        private static void AutoBuildOnce()
        {
            if (File.Exists(ShipPrefabPath)) return;
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (File.Exists(ShipPrefabPath)) return;
                Build();
            };
        }

        // One deferred maintenance pass after every compile; each step is idempotent and cheap
        // when its work is already done.
        [InitializeOnLoadMethod]
        private static void AutoMaintain()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                AddHelmPromptRows(logIfPresent: false);
                PatchShipPrefabPhysics();
                MoveRestartButtonToCorner();
            };
        }

        /// <summary>
        /// Fixes two physics hazards in Ship.prefab in place (preserving fileIDs so the scene
        /// instance keeps its references):
        ///  - Disables the Synty hull's non-convex MeshColliders. They are illegal on a dynamic
        ///    Rigidbody (Unity logs errors every frame and their behaviour is undefined — the
        ///    suspected source of the ship pitching during turns). Our generated boxes do the
        ///    actual collision work.
        ///  - Bakes the planar constraints + no-gravity into the serialized Rigidbody, so they
        ///    hold from the very first physics step instead of only after Awake runs.
        /// </summary>
        [MenuItem("Tools/Ship/Patch Ship Prefab Physics")]
        public static void PatchShipPrefabPhysics()
        {
            if (!File.Exists(ShipPrefabPath)) return;

            // Cheap check against the asset before doing a full contents edit.
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(ShipPrefabPath);
            var rbAsset = asset != null ? asset.GetComponent<Rigidbody>() : null;
            bool needsPatch = rbAsset != null &&
                (rbAsset.constraints == RigidbodyConstraints.None || rbAsset.useGravity ||
                 asset.GetComponentsInChildren<MeshCollider>(true).Any(mc => mc.enabled));
            if (!needsPatch) return;

            GameObject ship = PrefabUtility.LoadPrefabContents(ShipPrefabPath);
            try
            {
                var rb = ship.GetComponent<Rigidbody>();
                rb.constraints = RigidbodyConstraints.FreezePositionY
                               | RigidbodyConstraints.FreezeRotationX
                               | RigidbodyConstraints.FreezeRotationZ;
                rb.useGravity = false;
                rb.interpolation = RigidbodyInterpolation.Interpolate;

                int disabled = 0;
                foreach (MeshCollider mc in ship.GetComponentsInChildren<MeshCollider>(true))
                    if (mc.enabled) { mc.enabled = false; disabled++; }

                PrefabUtility.SaveAsPrefabAsset(ship, ShipPrefabPath);
                Debug.Log($"[ShipTestAreaBuilder] Ship.prefab patched: baked planar Rigidbody constraints, " +
                          $"disabled {disabled} non-convex hull MeshCollider(s).");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(ship);
            }
        }

        /// <summary>Moves the RunHud restart button from bottom-centre (where it overlapped the
        /// interaction prompts) to the bottom-right corner. Idempotent via the anchor check.</summary>
        [MenuItem("Tools/Ship/Move Restart Button To Corner")]
        public static void MoveRestartButtonToCorner()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath) return; // only touch the scene we know

            GameObject button = GameObject.Find("RestartButton");
            var rect = button != null ? button.GetComponent<RectTransform>() : null;
            if (rect == null || rect.anchorMin.x > 0.9f) return; // missing or already moved

            rect.anchorMin = rect.anchorMax = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-160f, 56f); // 260-wide button: ~30px corner margin
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[ShipTestAreaBuilder] RestartButton moved to the bottom-right corner.");
        }

        [MenuItem("Tools/Ship/Add Helm Prompt Rows To HUD")]
        public static void AddHelmPromptRowsMenu() => AddHelmPromptRows(logIfPresent: true);

        /// <summary>
        /// Extends GrabPromptHUD.prefab with the helm prompt rows (take the helm / steering
        /// controls / let go), cloning the existing rows so styling and placement stay consistent.
        /// Idempotent: does nothing if a CanSteer row is already configured.
        /// </summary>
        private static void AddHelmPromptRows(bool logIfPresent)
        {
            if (!File.Exists(HudPrefabPath)) return;

            GameObject hud = PrefabUtility.LoadPrefabContents(HudPrefabPath);
            try
            {
                var view = hud.GetComponentInChildren<GrabPromptView>(true);
                if (view == null) return;

                var so = new SerializedObject(view);
                SerializedProperty rows = so.FindProperty("rows");

                GameObject grabTemplate = null, dropTemplate = null, throwTemplate = null;
                for (int i = 0; i < rows.arraySize; i++)
                {
                    SerializedProperty row = rows.GetArrayElementAtIndex(i);
                    int visibleIn = row.FindPropertyRelative("visibleIn").enumValueIndex;
                    int binding = row.FindPropertyRelative("binding").enumValueIndex;
                    var root = row.FindPropertyRelative("root").objectReferenceValue as GameObject;

                    if (visibleIn == (int)GrabPromptChannel.State.CanSteer)
                    {
                        if (logIfPresent) Debug.Log("[ShipTestAreaBuilder] Helm prompt rows already present.");
                        return;
                    }
                    if (visibleIn == (int)GrabPromptChannel.State.CanGrab) grabTemplate = root;
                    if (visibleIn == (int)GrabPromptChannel.State.Holding && binding == 0) dropTemplate = root;
                    if (visibleIn == (int)GrabPromptChannel.State.Holding && binding == 1) throwTemplate = root;
                }
                if (grabTemplate == null || dropTemplate == null || throwTemplate == null)
                {
                    Debug.LogWarning("[ShipTestAreaBuilder] GrabPromptHUD rows not in expected shape; helm prompts not added.");
                    return;
                }

                // Clones inherit the template's anchors, so each new row lands where its
                // never-simultaneously-visible counterpart sits.
                GameObject takeRow = CloneRow(grabTemplate, "TakeHelmRow");
                GameObject steerRow = CloneRow(dropTemplate, "SteerControlsRow");
                GameObject letGoRow = CloneRow(throwTemplate, "LetGoHelmRow");
                // The controls line is longer than the binding rows; never clip it.
                var steerText = steerRow.GetComponent<UnityEngine.UI.Text>();
                if (steerText != null) steerText.horizontalOverflow = HorizontalWrapMode.Overflow;

                AppendRow(rows, GrabPromptChannel.State.CanSteer, 0, "Take the helm", takeRow, false);
                AppendRow(rows, GrabPromptChannel.State.Steering, 0, "", steerRow, true);
                AppendRow(rows, GrabPromptChannel.State.Steering, 0, "Let go", letGoRow, false);
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(hud, HudPrefabPath);
                Debug.Log("[ShipTestAreaBuilder] GrabPromptHUD.prefab: added helm prompt rows (take/steer/let go).");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(hud);
            }
        }

        private static GameObject CloneRow(GameObject template, string name)
        {
            var clone = Object.Instantiate(template, template.transform.parent);
            clone.name = name;
            return clone;
        }

        private static void AppendRow(SerializedProperty rows, GrabPromptChannel.State visibleIn,
            int binding, string label, GameObject root, bool useContextLine)
        {
            rows.arraySize++;
            SerializedProperty row = rows.GetArrayElementAtIndex(rows.arraySize - 1);
            row.FindPropertyRelative("visibleIn").enumValueIndex = (int)visibleIn;
            row.FindPropertyRelative("binding").enumValueIndex = binding;
            row.FindPropertyRelative("label").stringValue = label;
            row.FindPropertyRelative("root").objectReferenceValue = root;
            row.FindPropertyRelative("text").objectReferenceValue = root.GetComponent<UnityEngine.UI.Text>();
            row.FindPropertyRelative("useContextLine").boolValue = useContextLine;
        }

        [MenuItem("Tools/Ship/Build Ship Test Area")]
        public static void Build()
        {
            var deck = BuildShipPrefab(out float deckMainY, out float hullMinY);
            if (deck == null) return;

            UpdatePlayerPrefab();
            BuildHarborInScene(deck, deckMainY, hullMinY);

            AssetDatabase.SaveAssets();
            Debug.Log("[ShipTestAreaBuilder] Done. Ship.prefab built, Player.prefab updated, Harbor placed in SampleScene.");
        }

        // ---------------------------------------------------------------- ship prefab

        private static GameObject BuildShipPrefab(out float deckMainY, out float hullMinY)
        {
            deckMainY = 0f;
            hullMinY = 0f;

            var hullPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HullPath);
            var wheelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(WheelPath);
            if (hullPrefab == null || wheelPrefab == null)
            {
                Debug.LogError($"[ShipTestAreaBuilder] Missing Synty prefabs ({HullPath} / {WheelPath}).");
                return null;
            }

            var root = new GameObject("Ship");
            try
            {
                // Assemble far above the scene so probe raycasts only ever hit our own temp colliders.
                root.transform.position = new Vector3(0f, 500f, 0f);

                var hull = (GameObject)PrefabUtility.InstantiatePrefab(hullPrefab);
                hull.transform.SetParent(root.transform, false);
                hull.transform.localPosition = Vector3.zero;
                hull.transform.localRotation = Quaternion.identity;

                Bounds bounds = RendererBounds(hull);
                hullMinY = bounds.min.y - root.transform.position.y;

                // Probe the real deck surface with raycasts against temporary mesh colliders.
                List<DeckRow> rows = ProbeDeck(root, hull, bounds);
                if (rows.Count == 0)
                {
                    Debug.LogError("[ShipTestAreaBuilder] Deck probing found no walkable surface; aborting.");
                    Object.DestroyImmediate(root);
                    return null;
                }
                deckMainY = Median(rows.Select(r => r.y).ToList());

                BuildColliders(root, bounds, rows, deckMainY, hullMinY);
                GameObject deckVolume = BuildDeckVolume(root, bounds, deckMainY);

                // Physics + networking on the root.
                var rb = root.AddComponent<Rigidbody>();
                rb.mass = 3000f;

                root.AddComponent<NetworkIdentity>();
                var nt = root.AddComponent<NetworkTransformReliable>();
                nt.target = root.transform;
                nt.syncDirection = SyncDirection.ServerToClient;
                nt.syncInterval = 0.05f;
                nt.coordinateSpace = CoordinateSpace.World;

                var ship = root.AddComponent<ShipController>();
                WireSailVisuals(ship, hull);
                deckVolume.GetComponent<ShipDeck>().SetShip(ship);

                // Helm: wheel prop at the exact pose Synty uses in the attachments variant.
                var helm = root.AddComponent<ShipHelm>();
                Transform wheel = PlaceWheel(root, wheelPrefab);
                helm.SetRefs(ship, wheel);
                wheel.gameObject.AddComponent<ShipHelmTarget>().SetHelm(helm);

                root.transform.position = Vector3.zero;
                Directory.CreateDirectory("Assets/Prefabs");
                GameObject asset = PrefabUtility.SaveAsPrefabAsset(root, ShipPrefabPath);
                Debug.Log($"[ShipTestAreaBuilder] Ship.prefab: hull {bounds.size.x:F1}x{bounds.size.z:F1} m, " +
                          $"main deck {deckMainY:F2} (local), {rows.Count} deck strips.");
                return asset;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private struct DeckRow
        {
            public float z, halfDepth, y, xMin, xMax;
        }

        // Raycast a grid down onto temp mesh colliders to find the walkable deck surface,
        // row by row along the ship's length (handles raised bow/stern platforms).
        private static List<DeckRow> ProbeDeck(GameObject root, GameObject hull, Bounds bounds)
        {
            var temp = new List<MeshCollider>();
            foreach (MeshFilter mf in hull.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                temp.Add(mc);
            }
            Physics.SyncTransforms();

            var rows = new List<DeckRow>();
            try
            {
                const int rowCount = 12;
                const int xSamples = 9;
                float castTop = bounds.max.y + 2f;
                float zPad = 0.6f, xPad = 0.35f;
                float rowDepth = (bounds.size.z - 2f * zPad) / rowCount;

                for (int zi = 0; zi < rowCount; zi++)
                {
                    float z = bounds.min.z + zPad + rowDepth * (zi + 0.5f);
                    var hits = new List<(float x, float y)>();
                    for (int xi = 0; xi < xSamples; xi++)
                    {
                        float x = Mathf.Lerp(bounds.min.x + xPad, bounds.max.x - xPad, xi / (float)(xSamples - 1));
                        if (Physics.Raycast(new Vector3(x, castTop, z), Vector3.down, out RaycastHit hit,
                                bounds.size.y + 4f, ~0, QueryTriggerInteraction.Ignore))
                            hits.Add((x, hit.point.y));
                    }
                    if (hits.Count < 3) continue;

                    // Median rejects the mast and rail tops; keep only samples near it as "deck".
                    float y = Median(hits.Select(h => h.y).ToList());
                    var deckHits = hits.Where(h => Mathf.Abs(h.y - y) < 0.35f).ToList();
                    if (deckHits.Count < 2) continue;

                    rows.Add(new DeckRow
                    {
                        z = z - root.transform.position.z,
                        halfDepth = rowDepth * 0.5f,
                        y = y - root.transform.position.y,
                        xMin = deckHits.Min(h => h.x) - root.transform.position.x,
                        xMax = deckHits.Max(h => h.x) - root.transform.position.x,
                    });
                }
            }
            finally
            {
                foreach (MeshCollider mc in temp) Object.DestroyImmediate(mc);
            }
            return rows;
        }

        private static void BuildColliders(GameObject root, Bounds bounds, List<DeckRow> rows,
            float deckMainY, float hullMinY)
        {
            var group = new GameObject("Colliders");
            group.transform.SetParent(root.transform, false);

            Vector3 rootPos = root.transform.position;
            float length = bounds.size.z, beam = bounds.size.x;
            float zCenter = bounds.center.z - rootPos.z;
            float xCenter = bounds.center.x - rootPos.x;

            // Solid hull below the main deck: what rocks and the dock collide with.
            AddBox(group, "Hull", new Vector3(xCenter, (hullMinY + deckMainY) * 0.5f, zCenter),
                new Vector3(beam * 0.9f, deckMainY - hullMinY, length * 0.96f), false);

            // Walkable deck strips, one per probed row (follows raised stern/bow platforms).
            const float strip = 0.3f;
            foreach (DeckRow r in rows)
                AddBox(group, "Deck", new Vector3((r.xMin + r.xMax) * 0.5f, r.y - strip * 0.5f, r.z),
                    new Vector3(Mathf.Max(0.5f, r.xMax - r.xMin), strip, r.halfDepth * 2f + 0.05f), false);

            // Bulwark rails: low walls all around — cargo stays aboard, players can hop over.
            float deckMaxY = rows.Max(r => r.y);
            float railBottom = rows.Min(r => r.y) - 0.1f;
            float railTop = deckMaxY + RailHeight;
            float railYC = (railBottom + railTop) * 0.5f, railH = railTop - railBottom;
            float halfBeam = beam * 0.5f;
            AddBox(group, "RailPort", new Vector3(xCenter - halfBeam + 0.12f, railYC, zCenter),
                new Vector3(0.25f, railH, length * 0.96f), false);
            AddBox(group, "RailStarboard", new Vector3(xCenter + halfBeam - 0.12f, railYC, zCenter),
                new Vector3(0.25f, railH, length * 0.96f), false);
            AddBox(group, "RailBow", new Vector3(xCenter, railYC, zCenter + length * 0.5f - 0.15f),
                new Vector3(beam * 0.9f, railH, 0.3f), false);
            AddBox(group, "RailStern", new Vector3(xCenter, railYC, zCenter - length * 0.5f + 0.15f),
                new Vector3(beam * 0.9f, railH, 0.3f), false);
        }

        private static GameObject BuildDeckVolume(GameObject root, Bounds bounds, float deckMainY)
        {
            Vector3 rootPos = root.transform.position;
            var volume = new GameObject("DeckVolume");
            volume.transform.SetParent(root.transform, false);
            var box = volume.AddComponent<BoxCollider>();
            box.isTrigger = true;
            // Slightly wider than the hull (a boarding jump counts early) and tall enough that no
            // jump on deck ever exits it — but tight enough not to catch players on a nearby dock.
            box.center = new Vector3(bounds.center.x - rootPos.x, deckMainY + 3f, bounds.center.z - rootPos.z);
            box.size = new Vector3(bounds.size.x + 1f, 6f, bounds.size.z + 1f);
            volume.AddComponent<ShipDeck>();
            return volume;
        }

        private static void WireSailVisuals(ShipController ship, GameObject hull)
        {
            Transform sails = FindDeep(hull.transform, "SM_Veh_Boat_Medium_01_Sails");
            Transform furled = FindDeep(hull.transform, "SM_Veh_Boat_Medium_01_Sails_Up");
            if (sails != null) sails.gameObject.SetActive(false); // start furled
            if (furled != null) furled.gameObject.SetActive(true);

            var so = new SerializedObject(ship);
            so.FindProperty("sailsSetVisual").objectReferenceValue = sails != null ? sails.gameObject : null;
            so.FindProperty("sailsFurledVisual").objectReferenceValue = furled != null ? furled.gameObject : null;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Transform PlaceWheel(GameObject root, GameObject wheelPrefab)
        {
            // Synty's attachments variant already places a wheel on this hull — copy its pose.
            Vector3 pos = new Vector3(0f, 1.6f, -3.5f); // fallback: on deck near the stern
            Quaternion rot = Quaternion.identity;
            var attachments = AssetDatabase.LoadAssetAtPath<GameObject>(HullAttachmentsPath);
            if (attachments != null)
            {
                Transform src = FindDeep(attachments.transform, "SM_Prop_ShipWheel_01");
                if (src != null)
                {
                    Matrix4x4 rel = attachments.transform.worldToLocalMatrix * src.localToWorldMatrix;
                    pos = rel.GetColumn(3);
                    rot = rel.rotation;
                }
            }

            var wheel = (GameObject)PrefabUtility.InstantiatePrefab(wheelPrefab);
            wheel.name = "HelmWheel";
            wheel.transform.SetParent(root.transform, false);
            wheel.transform.localPosition = pos;
            wheel.transform.localRotation = rot;

            // Interaction collider fit around the wheel's meshes (in the wheel's local frame).
            Bounds wb = RendererBounds(wheel);
            var col = wheel.AddComponent<BoxCollider>();
            Vector3 localCenter = wheel.transform.InverseTransformPoint(wb.center);
            Matrix4x4 toLocal = wheel.transform.worldToLocalMatrix;
            Vector3 s = wb.size;
            Vector3 localSize = Abs(toLocal.MultiplyVector(new Vector3(s.x, 0, 0)))
                              + Abs(toLocal.MultiplyVector(new Vector3(0, s.y, 0)))
                              + Abs(toLocal.MultiplyVector(new Vector3(0, 0, s.z)));
            col.center = localCenter;
            col.size = Vector3.Max(localSize + Vector3.one * 0.1f, Vector3.one * 0.3f);
            return wheel.transform;
        }

        // ---------------------------------------------------------------- player prefab

        private static void UpdatePlayerPrefab()
        {
            GameObject player = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            try
            {
                bool changed = false;
                if (player.GetComponent<ShipRider>() == null)
                {
                    player.AddComponent<ShipRider>();
                    changed = true;
                }
                if (player.GetComponent<PlayerHelmUser>() == null)
                {
                    var helmUser = player.AddComponent<PlayerHelmUser>();
                    // Same aim source the other look-driven components use: NetworkPlayer's cameraPivot.
                    var np = new SerializedObject(player.GetComponent<Game.Player.NetworkPlayer>());
                    helmUser.SetAimSource(np.FindProperty("cameraPivot").objectReferenceValue as Transform);
                    changed = true;
                }
                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(player, PlayerPrefabPath);
                    Debug.Log("[ShipTestAreaBuilder] Player.prefab: added ShipRider + PlayerHelmUser.");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(player);
            }
        }

        // ---------------------------------------------------------------- scene

        private static void BuildHarborInScene(GameObject shipPrefab, float deckMainY, float hullMinY)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            if (GameObject.Find("Harbor") != null)
            {
                Debug.Log("[ShipTestAreaBuilder] Harbor already exists in the scene; leaving it untouched.");
                return;
            }

            // Deck boards flush with the dock; the waterline sits at a believable draft below deck.
            float shipY = DockTopY - deckMainY;
            float waterY = Mathf.Min(shipY + Mathf.Lerp(hullMinY, deckMainY, 0.45f), DockTopY - 0.5f);

            var harbor = new GameObject("Harbor");

            // Water surface (visual only — no collider, you fall through into the hazard).
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "Water";
            Object.DestroyImmediate(water.GetComponent<Collider>());
            water.transform.SetParent(harbor.transform, false);
            water.transform.position = new Vector3(0f, waterY, -80f); // z -150 .. -10
            water.transform.localScale = new Vector3(14f, 1f, 14f); // 140x140 m
            water.GetComponent<MeshRenderer>().sharedMaterial = GetOrCreateMaterial(
                WaterMatPath, new Color(0.09f, 0.32f, 0.42f), 0.85f);

            // Drowning: touching the water sends you back to your checkpoint.
            var hazard = new GameObject("WaterHazard");
            hazard.transform.SetParent(harbor.transform, false);
            hazard.transform.position = new Vector3(0f, waterY - 1.8f, -80f);
            var hazardBox = hazard.AddComponent<BoxCollider>();
            hazardBox.isTrigger = true;
            hazardBox.size = new Vector3(140f, 3f, 140f);
            hazard.AddComponent<HazardVolume>();

            // Dock: walkway from the start platform's south edge out over the water.
            var dock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            dock.name = "Dock";
            dock.transform.SetParent(harbor.transform, false);
            dock.transform.position = new Vector3(0f, DockTopY - 0.25f, -15.5f);
            dock.transform.localScale = new Vector3(4f, 0.5f, 19f); // z -6 .. -25
            dock.GetComponent<MeshRenderer>().sharedMaterial = GetOrCreateMaterial(
                WoodMatPath, new Color(0.42f, 0.29f, 0.17f), 0.1f);

            // Checkpoint on the dock so drowning doesn't send you back to the course start.
            var checkpoint = new GameObject("DockCheckpoint");
            checkpoint.transform.SetParent(harbor.transform, false);
            checkpoint.transform.position = new Vector3(0f, DockTopY + 1.1f, -20f);
            checkpoint.transform.rotation = Quaternion.Euler(0f, 180f, 0f); // respawn facing the sea
            var cpBox = checkpoint.AddComponent<BoxCollider>();
            cpBox.isTrigger = true;
            cpBox.size = new Vector3(4f, 2.2f, 4f);
            var trigger = checkpoint.AddComponent<RunTrigger>();
            var soTrigger = new SerializedObject(trigger);
            soTrigger.FindProperty("kind").enumValueIndex = (int)RunTrigger.Kind.Checkpoint;
            soTrigger.ApplyModifiedPropertiesWithoutUndo();

            // The ship, moored alongside the dock, bow pointing out to sea (-Z).
            var ship = (GameObject)PrefabUtility.InstantiatePrefab(shipPrefab);
            ship.transform.SetParent(harbor.transform, false);
            ship.transform.position = new Vector3(5.2f, shipY, -20f);
            ship.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            // Rock slalom on the way out of the harbor.
            PlaceRock(harbor, "SM_Env_Rock_Large_01", new Vector3(6f, waterY - 1.2f, -60f), 0f);
            PlaceRock(harbor, "SM_Env_Rock_Large_02", new Vector3(-11f, waterY - 1.2f, -78f), 70f);
            PlaceRock(harbor, "SM_Env_Rock_Large_03", new Vector3(13f, waterY - 1.2f, -98f), 160f);
            PlaceRock(harbor, "SM_Env_Rock_Huge_01", new Vector3(-6f, waterY - 1.6f, -118f), 220f);

            // A distant island to sail toward (visual only for now).
            var islandPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                SyntyRoot + "/Environments/SM_Env_Background_Island_01.prefab");
            if (islandPrefab != null)
            {
                var island = (GameObject)PrefabUtility.InstantiatePrefab(islandPrefab);
                island.transform.SetParent(harbor.transform, false);
                island.transform.position = new Vector3(-30f, waterY - 0.5f, -145f);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[ShipTestAreaBuilder] Harbor placed: water y={waterY:F2}, ship y={shipY:F2}, dock top y={DockTopY:F2}.");
        }

        private static void PlaceRock(GameObject parent, string prefabName, Vector3 pos, float yaw)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{SyntyRoot}/Environments/{prefabName}.prefab");
            if (prefab == null) return;
            var rock = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            rock.transform.SetParent(parent.transform, false);
            rock.transform.position = pos;
            rock.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            // Static concave mesh colliders are fine for the dynamic ship to bounce off.
            foreach (MeshFilter mf in rock.GetComponentsInChildren<MeshFilter>())
                if (mf.GetComponent<MeshCollider>() == null)
                    mf.gameObject.AddComponent<MeshCollider>();
        }

        // ---------------------------------------------------------------- helpers

        private static void AddBox(GameObject parent, string name, Vector3 center, Vector3 size, bool trigger)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var box = go.AddComponent<BoxCollider>();
            box.center = center;
            box.size = size;
            box.isTrigger = trigger;
        }

        private static Bounds RendererBounds(GameObject go)
        {
            Renderer[] rs = go.GetComponentsInChildren<Renderer>();
            Bounds b = rs[0].bounds;
            foreach (Renderer r in rs.Skip(1)) b.Encapsulate(r.bounds);
            return b;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        private static float Median(List<float> values)
        {
            values.Sort();
            int n = values.Count;
            return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) * 0.5f;
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private static Material GetOrCreateMaterial(string path, Color color, float smoothness)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            mat = new Material(shader);
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", smoothness);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
