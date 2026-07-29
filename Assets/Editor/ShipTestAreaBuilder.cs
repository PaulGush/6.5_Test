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
    /// Builder + maintenance for the free-sailing slice.
    ///
    /// Ships are described by a <see cref="ShipSpec"/> (which Synty hull, mass, handling) and built
    /// into per-ship prefabs (Ship_Medium/Ship_Warship/Ship_Large). Everything physical is measured
    /// from the actual meshes: walkable deck colliders are probed row-by-row by raycast, crow's
    /// nest platforms are discovered by probing around the masts, deck props (cannons, crates) get
    /// fitted box colliders. "Tools > Ship > Use ... In Harbor" swaps the moored ship, re-deriving
    /// the mooring pose from the new hull's measurements so the deck always boards flush from the
    /// dock.
    ///
    /// Also does one-time scene/prefab maintenance after each compile (helm prompt HUD rows,
    /// legacy physics patches, restart-button placement) — every step is idempotent.
    /// </summary>
    public static class ShipTestAreaBuilder
    {
        private const string LegacyShipPrefabPath = "Assets/Prefabs/Ship.prefab";
        private const string WarshipPrefabPath = "Assets/Prefabs/Ship_Warship.prefab";
        private const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
        private const string HudPrefabPath = "Assets/Prefabs/GrabPromptHUD.prefab";
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";

        private const string SyntyRoot = "Assets/Synty/PolygonPirates/Prefabs";
        private const string MediumAttachmentsPath = SyntyRoot + "/Vehicles/SM_Veh_Boat_Medium_01_Hull_Attachments.prefab";
        private const string WheelFallbackPath = SyntyRoot + "/Props/SM_Prop_ShipWheel_01.prefab";

        private const string WaterMatPath = "Assets/Art/Materials/Sea_Water.mat";
        private const string WoodMatPath = "Assets/Art/Materials/Sea_DockWood.mat";

        // Bump when generated-collider logic changes: prefabs carrying an older tag are rebuilt
        // and re-moored by the auto-maintenance pass.
        private const int BuildVersion = 4;

        private const float DockTopY = 0.9f;    // walkable dock height; every ship's deck aligns to it
        private const float DockEdgeX = 2f;     // dock is 4 wide, centred on x=0
        private const float WaterEdgeZ = -10f;  // water surface starts here and runs to -150
        private const float RailHeight = 0.55f; // bulwark colliders: keeps cargo in, jumpable by players

        // ------------------------------------------------------------------ ship specs

        private class ShipSpec
        {
            public string prefabName;   // asset name under Assets/Prefabs/
            public string hullPath;     // dressed Synty variant (attachments include wheel/cannons)
            public float mass;
            public float rudderTurnAccel;
            public float[] sailThrust;
            public bool crowsNest;      // probe masts for nest platforms + build jump pads up
            public float draftFraction; // how deep the hull sits: waterline as a fraction of
                                        // hull-bottom → main-deck (lower = rides higher)
        }

        private static ShipSpec MediumSpec => new ShipSpec
        {
            prefabName = "Ship_Medium",
            hullPath = MediumAttachmentsPath,
            mass = 3000f,
            rudderTurnAccel = 14f,
            sailThrust = new[] { 0f, 1.2f, 2.4f, 3.6f },
            crowsNest = false,
            draftFraction = 0.45f,
        };

        private static ShipSpec WarshipSpec => new ShipSpec
        {
            prefabName = "Ship_Warship",
            hullPath = SyntyRoot + "/Vehicles/SM_Veh_Boat_Warship_01_Hull_Attachments.prefab",
            mass = 8000f,
            rudderTurnAccel = 9f,       // heavier ship, wider turns — the chaos scales up
            sailThrust = new[] { 0f, 1.4f, 2.8f, 4.2f },
            crowsNest = true,
            // Ride high: the gun ports sit low on the hull and must stay clear of the water.
            draftFraction = 0.26f,
        };

        private static ShipSpec LargeSpec => new ShipSpec
        {
            prefabName = "Ship_Large",
            hullPath = SyntyRoot + "/Vehicles/SM_Veh_Veh_Boat_Large_01_Hull_Attachments.prefab",
            mass = 5500f,
            rudderTurnAccel = 11f,
            sailThrust = new[] { 0f, 1.3f, 2.6f, 3.9f },
            crowsNest = true,
            draftFraction = 0.32f,
        };

        private static string PrefabPathFor(ShipSpec spec) => $"Assets/Prefabs/{spec.prefabName}.prefab";

        /// <summary>Everything the scene needs to know to moor a freshly built ship.</summary>
        private class BuildResult
        {
            public GameObject prefab;
            public float deckMainY;   // local Y of the main deck at the boarding row
            public float boardingZ;   // local Z of the boarding row (rail gap / gangway)
            public float hullMinY;    // local Y of the hull bottom
            public float beamHalf;    // half-width of the walkable deck (not spars)
            public float deckZMin, deckZMax; // walkable deck extent (not the bowsprit)

            public float DeckHalfLength => (deckZMax - deckZMin) * 0.5f;
            public Vector3 MooringPosition => new Vector3(
                DockEdgeX + beamHalf + 0.7f,                    // hull side ~0.7m off the dock edge
                DockTopY - deckMainY,                           // deck boards flush with the dock
                WaterEdgeZ - DeckHalfLength - 2f);              // whole hull over water, near the dock
        }

        // ------------------------------------------------------------------ entry points

        // Full first-time build, only if no ship prefab exists at all (fresh checkout).
        [InitializeOnLoadMethod]
        private static void AutoBuildOnce()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (File.Exists(LegacyShipPrefabPath) || File.Exists(WarshipPrefabPath)
                    || File.Exists(PrefabPathFor(MediumSpec)) || File.Exists(PrefabPathFor(LargeSpec))) return;
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
                RebuildDockOnce();
                EnsureWarshipOnce();
                EnsureCurrentBuildOnce();
                EnsureRiggingClimbOnce();
                EnsureNoNestJumpPadsOnce();
            };
        }

        [MenuItem("Tools/Ship/Build Ship Test Area")]
        public static void Build()
        {
            BuildResult result = BuildShipPrefab(MediumSpec);
            if (result == null) return;

            UpdatePlayerPrefab();
            BuildHarborInScene(result);

            AssetDatabase.SaveAssets();
            Debug.Log("[ShipTestAreaBuilder] Done. Ship prefab built, Player.prefab updated, Harbor placed in SampleScene.");
        }

        [MenuItem("Tools/Ship/Use Medium Ship In Harbor")]
        public static void UseMediumShip() => SwapHarborShip(MediumSpec);

        [MenuItem("Tools/Ship/Use Warship In Harbor")]
        public static void UseWarship() => SwapHarborShip(WarshipSpec);

        [MenuItem("Tools/Ship/Use Large Ship In Harbor")]
        public static void UseLargeShip() => SwapHarborShip(LargeSpec);

        // First compile after the swap feature lands: put the warship in (the current test focus).
        private static void EnsureWarshipOnce()
        {
            if (File.Exists(WarshipPrefabPath)) return;
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            if (GameObject.Find("Harbor") == null) return;
            SwapHarborShip(WarshipSpec);
        }

        /// <summary>Rebuilds the spec's prefab from the Synty meshes and replaces the ship moored
        /// in the Harbor, re-deriving the mooring pose from the new hull's measurements.</summary>
        private static void SwapHarborShip(ShipSpec spec)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            GameObject harbor = GameObject.Find("Harbor");
            if (harbor == null)
            {
                Debug.LogError("[ShipTestAreaBuilder] No Harbor in the scene — run Tools > Ship > Build Ship Test Area first.");
                return;
            }

            // Destroy the old instance BEFORE rebuilding the asset it may have come from
            // (SaveAsPrefabAsset regenerates fileIDs, which would orphan a live instance).
            var old = harbor.GetComponentInChildren<ShipController>(true);
            if (old != null) Object.DestroyImmediate(old.gameObject);

            BuildResult result = BuildShipPrefab(spec);
            if (result == null) return;

            var ship = (GameObject)PrefabUtility.InstantiatePrefab(result.prefab);
            ship.transform.SetParent(harbor.transform, false);
            ship.transform.position = result.MooringPosition;
            ship.transform.rotation = Quaternion.Euler(0f, 180f, 0f); // bow out to sea (-Z)

            BuildGangway(harbor, result, ship.transform.position);
            UpdateWaterLevel(harbor, result, spec);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ShipTestAreaBuilder] Harbor ship is now {spec.prefabName} " +
                      $"(deck {result.deckMainY:F2} local, beam {result.beamHalf * 2f:F1} m, moored at {result.MooringPosition}).");
        }

        // ---------------------------------------------------------------- ship prefab

        private static BuildResult BuildShipPrefab(ShipSpec spec)
        {
            var hullPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(spec.hullPath);
            if (hullPrefab == null)
            {
                Debug.LogError($"[ShipTestAreaBuilder] Missing Synty prefab: {spec.hullPath}");
                return null;
            }

            var root = new GameObject(spec.prefabName);
            try
            {
                // Assemble far above the scene so probe raycasts only ever hit our own colliders.
                root.transform.position = new Vector3(0f, 500f, 0f);

                var hull = (GameObject)PrefabUtility.InstantiatePrefab(hullPrefab);
                hull.transform.SetParent(root.transform, false);
                hull.transform.localPosition = Vector3.zero;
                hull.transform.localRotation = Quaternion.identity;

                Bounds bounds = RendererBounds(hull);
                float hullMinY = bounds.min.y - root.transform.position.y;
                float maxLocalY = bounds.max.y - root.transform.position.y;

                // One probing session (temp mesh colliders) for deck rows, crow's nests, yards,
                // and the bowsprit. Sails and rigging are hidden during the session so every ray
                // reads wood, not cloth/rope.
                List<DeckRow> rows;
                List<Vector3> nests;
                List<MastColumn> masts;
                List<(Vector3 center, Vector3 size)> yards;
                List<(float z, float y)> bowspritPts;
                var probeHidden = new List<GameObject>();
                foreach (Transform t in hull.GetComponentsInChildren<Transform>(true))
                    if ((t.name.Contains("Sail") || t.name.Contains("Rigging")) && t.gameObject.activeSelf)
                    {
                        t.gameObject.SetActive(false);
                        probeHidden.Add(t.gameObject);
                    }
                var temp = AddTempMeshColliders(hull);
                try
                {
                    rows = ProbeDeck(root, bounds);
                    if (rows.Count == 0)
                    {
                        Debug.LogError($"[ShipTestAreaBuilder] Deck probing found no walkable surface on {spec.prefabName}; aborting.");
                        return null;
                    }
                    float deckMain = Percentile(rows.Select(r => r.y).ToList(), 0.5f);
                    float deckLow = rows.Min(r => r.y);
                    masts = CollectMasts(root, hull);
                    nests = spec.crowsNest ? ProbeCrowsNests(root, bounds, masts, deckMain) : new List<Vector3>();
                    yards = ProbeYards(root, bounds, masts, deckLow);
                    bowspritPts = ProbeBowsprit(root, bounds, rows.Max(r => r.z + r.halfDepth));
                }
                finally
                {
                    foreach (MeshCollider mc in temp) Object.DestroyImmediate(mc);
                    foreach (GameObject go in probeHidden) go.SetActive(true);
                }

                // The Synty hulls carry non-convex MeshColliders (illegal on a dynamic Rigidbody);
                // our generated boxes do the real collision work.
                foreach (MeshCollider mc in hull.GetComponentsInChildren<MeshCollider>(true))
                    mc.enabled = false;

                // Walkable extents from the probed rows, NOT renderer bounds — spars and the
                // bowsprit stick far outside the hull and would inflate every collider.
                float beamHalf = rows.Max(r => Mathf.Max(-r.xMin, r.xMax)) + 0.25f;
                float zMin = rows.Min(r => r.z - r.halfDepth);
                float zMax = rows.Max(r => r.z + r.halfDepth);
                float xCenter = rows.Average(r => (r.xMin + r.xMax) * 0.5f);
                // The lowest deck row caps the solid hull box — capping at an average would bury
                // lower rows and leave players standing above the visible planks.
                float deckLowY = rows.Min(r => r.y);
                // Boarding happens on the MAIN deck (the lowest deck level), at its sternmost
                // row: aligning to a raised quarterdeck instead would shove the whole hull
                // underwater to bring that high deck down to the dock.
                DeckRow boarding = rows.Where(r => r.y - deckLowY < 0.3f).OrderBy(r => r.z).First();

                var colliderGroup = new GameObject("Colliders");
                colliderGroup.transform.SetParent(root.transform, false);
                BuildColliders(colliderGroup, rows, hullMinY, deckLowY, boarding.z, beamHalf, xCenter, zMin, zMax);

                // Masts, yards, and the bowsprit are solid/walkable too.
                foreach (MastColumn m in masts)
                {
                    DeckRow mRow = rows.OrderBy(r => Mathf.Abs(r.z - m.pos.z)).First();
                    AddBox(colliderGroup, "Mast", new Vector3(m.pos.x, (mRow.y + m.topY) * 0.5f, m.pos.z),
                        new Vector3(0.3f, m.topY - mRow.y, 0.3f), false);
                }
                foreach ((Vector3 center, Vector3 size) yard in yards)
                    AddBox(colliderGroup, "Yard", yard.center, yard.size, false);
                BuildBowspritCollider(colliderGroup, bounds.center.x - root.transform.position.x, bowspritPts);

                // Boarding volumes: a hull-tight one (must not reach the dock), plus a wide one
                // high up covering the yard tips so walking a yard never counts as going ashore.
                float yardHalfSpan = yards.Count > 0
                    ? yards.Max(y => Mathf.Abs(y.center.x - xCenter) + y.size.x * 0.5f) : 0f;
                float bowZ = bowspritPts.Count >= 4
                    ? bounds.max.z - root.transform.position.z + 0.3f : zMax + 0.5f;
                BuildDeckVolumes(root, beamHalf, yardHalfSpan, xCenter, zMin, zMax, bowZ, deckLowY, maxLocalY);

                // Physics + networking on the root; planar constraints baked into the asset so
                // they hold from the very first physics step.
                var rb = root.AddComponent<Rigidbody>();
                rb.mass = spec.mass;
                rb.useGravity = false;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.constraints = RigidbodyConstraints.FreezePositionY
                               | RigidbodyConstraints.FreezeRotationX
                               | RigidbodyConstraints.FreezeRotationZ;

                root.AddComponent<NetworkIdentity>();
                var nt = root.AddComponent<NetworkTransformReliable>();
                nt.target = root.transform;
                nt.syncDirection = SyncDirection.ServerToClient;
                nt.syncInterval = 0.05f;
                nt.coordinateSpace = CoordinateSpace.World;

                var ship = root.AddComponent<ShipController>();
                WireShipTuning(ship, spec, hull);
                foreach (ShipDeck deck in root.GetComponentsInChildren<ShipDeck>(true))
                    deck.SetShip(ship);

                // Helm: the dressed Synty variants include a wheel; fall back to placing one.
                var helm = root.AddComponent<ShipHelm>();
                Transform wheel = EnsureWheel(root, hull);
                helm.SetRefs(ship, wheel);
                wheel.gameObject.AddComponent<ShipHelmTarget>().SetHelm(helm);

                AddPropColliders(hull, wheel);

                foreach (Vector3 nest in nests)
                    BuildNestPlatform(colliderGroup, nest);
                // The Synty shrouds double as ladders: climb volumes probed along the actual
                // rigging meshes (restored after the main probe session, which hid them).
                if (nests.Count > 0)
                    BuildRiggingClimbVolumes(root, ProbeRiggingClimbs(root, nests));

                // Inert marker read by the maintenance pass to detect stale prefabs.
                new GameObject($"BuildTag_v{BuildVersion}").transform.SetParent(root.transform, false);

                root.transform.position = Vector3.zero;
                Directory.CreateDirectory("Assets/Prefabs");
                string path = PrefabPathFor(spec);
                GameObject asset = PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[ShipTestAreaBuilder] {spec.prefabName}: deck {zMax - zMin:F1}x{beamHalf * 2f:F1} m, " +
                          $"boarding deck {boarding.y:F2} (local), {rows.Count} deck strips, {nests.Count} crow's nest(s).");

                return new BuildResult
                {
                    prefab = asset,
                    deckMainY = boarding.y,
                    boardingZ = boarding.z,
                    hullMinY = hullMinY,
                    beamHalf = beamHalf,
                    deckZMin = zMin,
                    deckZMax = zMax,
                };
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

        private static List<MeshCollider> AddTempMeshColliders(GameObject hull)
        {
            var temp = new List<MeshCollider>();
            foreach (MeshFilter mf in hull.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                temp.Add(mc);
            }
            Physics.SyncTransforms();
            return temp;
        }

        // Raycast a grid down onto the temp mesh colliders to find the walkable deck surface,
        // row by row along the ship's length (handles raised bow/stern platforms). Rows that hit
        // almost nothing (the bowsprit, thin spars) drop out on the hit-count check, so the
        // resulting rows also define the true walkable extent of the ship.
        private static List<DeckRow> ProbeDeck(GameObject root, Bounds bounds)
        {
            var rows = new List<DeckRow>();
            int rowCount = Mathf.Clamp(Mathf.RoundToInt(bounds.size.z / 1.4f), 8, 24);
            const int xSamples = 11;
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

                // Low percentile rather than median: the deck is the lowest walkable plateau, and
                // cargo stacks / masts / rails all sit above it.
                float y = Percentile(hits.Select(h => h.y).ToList(), 0.3f);
                var deckHits = hits.Where(h => Mathf.Abs(h.y - y) < 0.35f).ToList();
                if (deckHits.Count < 2) continue;

                // Floor at the LOWEST plank in the band, not the percentile — trim/hatch detail
                // above the planks would otherwise float the collider (and the player's feet).
                float floorY = deckHits.Min(h => h.y) + 0.01f;

                rows.Add(new DeckRow
                {
                    z = z - root.transform.position.z,
                    halfDepth = rowDepth * 0.5f,
                    y = floorY - root.transform.position.y,
                    xMin = deckHits.Min(h => h.x) - root.transform.position.x,
                    xMax = deckHits.Max(h => h.x) - root.transform.position.x,
                });
            }
            return rows;
        }

        private struct MastColumn
        {
            public Vector3 pos; // local, at deck level
            public float topY;  // local top of the mast's own renderers
        }

        private static List<MastColumn> CollectMasts(GameObject root, GameObject hull)
        {
            var masts = new List<MastColumn>();
            Vector3 rootPos = root.transform.position;
            foreach (Transform t in hull.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.Contains("Mast") || t.name.Contains("Sail")) continue;
                Renderer[] rs = t.GetComponentsInChildren<Renderer>();
                if (rs.Length == 0) continue;
                Vector3 p = t.position - rootPos;
                if (masts.Any(m => (new Vector2(m.pos.x, m.pos.z) - new Vector2(p.x, p.z)).sqrMagnitude < 0.25f))
                    continue; // same mast, different node
                Bounds b = rs[0].bounds;
                foreach (Renderer r in rs.Skip(1)) b.Encapsulate(r.bounds);
                masts.Add(new MastColumn { pos = p, topY = b.max.y - rootPos.y });
            }
            return masts;
        }

        // The Synty crow's nests are modelled into the mast meshes (no named object), so find
        // them the same way we find the deck: ring-raycast around each mast and look for a small
        // platform-sized cluster of hits well above the main deck.
        private static List<Vector3> ProbeCrowsNests(GameObject root, Bounds bounds,
            List<MastColumn> masts, float deckMainY)
        {
            var nests = new List<Vector3>();
            Vector3 rootPos = root.transform.position;

            foreach (Vector3 mast in masts.Select(m => m.pos + rootPos))
            {
                var ys = new List<float>();
                foreach (float radius in new[] { 0f, 0.5f, 0.85f })
                {
                    int points = radius == 0f ? 1 : 8;
                    for (int i = 0; i < points; i++)
                    {
                        float a = i * Mathf.PI * 2f / points;
                        Vector3 from = new Vector3(mast.x + Mathf.Cos(a) * radius, bounds.max.y + 2f,
                            mast.z + Mathf.Sin(a) * radius);
                        if (Physics.Raycast(from, Vector3.down, out RaycastHit hit,
                                bounds.size.y + 4f, ~0, QueryTriggerInteraction.Ignore))
                        {
                            float yLocal = hit.point.y - rootPos.y;
                            if (yLocal > deckMainY + 4f) ys.Add(yLocal); // ignore deck/cargo levels
                        }
                    }
                }

                // Largest cluster of similar heights = the nest floor (spars only catch 1-2 rays).
                ys.Sort();
                int bestStart = -1, bestCount = 0;
                for (int i = 0; i < ys.Count; i++)
                {
                    int count = ys.Count(v => v >= ys[i] && v <= ys[i] + 0.35f);
                    if (count > bestCount) { bestCount = count; bestStart = i; }
                }
                if (bestCount < 5) continue;

                float nestY = ys.Skip(bestStart).Take(bestCount).Average();
                nests.Add(new Vector3(mast.x - rootPos.x, nestY, mast.z - rootPos.z));
            }
            return nests;
        }

        private static void BuildColliders(GameObject group, List<DeckRow> rows,
            float hullMinY, float deckLowY, float boardingZ, float beamHalf, float xCenter, float zMin, float zMax)
        {
            float zCenter = (zMin + zMax) * 0.5f;
            float length = zMax - zMin;

            // Solid hull up to just below the LOWEST deck row: what rocks and the dock collide
            // with. The deck strips own the walking surface — the hull box must never poke above
            // one of them (that's what floated players off the planks).
            float hullTop = deckLowY - 0.05f;
            AddBox(group, "Hull", new Vector3(xCenter, (hullMinY + hullTop) * 0.5f, zCenter),
                new Vector3(beamHalf * 2f * 0.95f, hullTop - hullMinY, length + 1.6f), false);

            // Walkable deck strips, one per probed row (follows raised stern/bow platforms).
            const float strip = 0.3f;
            foreach (DeckRow r in rows)
                AddBox(group, "Deck", new Vector3((r.xMin + r.xMax) * 0.5f, r.y - strip * 0.5f, r.z),
                    new Vector3(Mathf.Max(0.5f, r.xMax - r.xMin), strip, r.halfDepth * 2f + 0.05f), false);

            // Bulwark rails: low walls FOLLOWING EACH DECK ROW — a single full-length rail topped
            // at the highest deck would tower over the main deck of a ship with a raised
            // quarterdeck (unjumpable invisible wall). Starboard gets a curb-height gangway gap
            // at the stern quarter, lining up with the dock's boarding plank (ships moor
            // starboard-to).
            foreach (DeckRow r in rows)
            {
                // Gap only on main-deck rows around the boarding row — never in a quarterdeck rail.
                bool gangway = Mathf.Abs(r.z - boardingZ) < 1.35f && r.y - deckLowY < 0.3f;
                float stbH = gangway ? 0.15f : RailHeight;
                AddBox(group, "RailPort",
                    new Vector3(xCenter - beamHalf + 0.1f, r.y + RailHeight * 0.5f - 0.05f, r.z),
                    new Vector3(0.25f, RailHeight + 0.1f, r.halfDepth * 2f + 0.05f), false);
                AddBox(group, gangway ? "RailStarboardGangway" : "RailStarboard",
                    new Vector3(xCenter + beamHalf - 0.1f, r.y + stbH * 0.5f - 0.05f, r.z),
                    new Vector3(0.25f, stbH + 0.1f, r.halfDepth * 2f + 0.05f), false);
            }
            AddBox(group, "RailStern", new Vector3(xCenter, rows.First().y + RailHeight * 0.5f, zMin - 0.25f),
                new Vector3(beamHalf * 2f * 0.95f, RailHeight + 0.1f, 0.3f), false);
            AddBox(group, "RailBow", new Vector3(xCenter, rows.Last().y + RailHeight * 0.5f, zMax + 0.25f),
                new Vector3(beamHalf * 2f * 0.95f, RailHeight + 0.1f, 0.3f), false);
        }

        private static void BuildDeckVolumes(GameObject root, float beamHalf, float yardHalfSpan,
            float xCenter, float zMin, float zMax, float bowZ, float deckLowY, float maxLocalY)
        {
            float bottom = deckLowY - 0.2f;
            float top = maxLocalY + 3f;

            // Hull-tight volume: slightly wider than the deck (a boarding jump counts early),
            // tight enough not to catch players on the dock, tall enough to cover jumps and the
            // nests, long enough to cover the bowsprit walk.
            AddDeckVolume(root, "DeckVolume",
                new Vector3(xCenter, (bottom + top) * 0.5f, (zMin - 0.5f + bowZ) * 0.5f),
                new Vector3(beamHalf * 2f + 1f, top - bottom, bowZ - zMin + 0.5f));

            // Aloft volume: wide enough for the yard tips, but starting well above the dock so
            // it can never swallow players standing on it.
            if (yardHalfSpan > beamHalf)
            {
                float aloftBottom = deckLowY + 3.5f;
                AddDeckVolume(root, "DeckVolumeAloft",
                    new Vector3(xCenter, (aloftBottom + top) * 0.5f, (zMin + zMax) * 0.5f),
                    new Vector3(yardHalfSpan * 2f + 1.2f, top - aloftBottom, zMax - zMin + 1f));
            }
        }

        private static void AddDeckVolume(GameObject root, string name, Vector3 center, Vector3 size)
        {
            var volume = new GameObject(name);
            volume.transform.SetParent(root.transform, false);
            var box = volume.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = center;
            box.size = size;
            volume.AddComponent<ShipDeck>();
        }

        // Yards (the horizontal spars) found by scanning a horizontal ray grid through each
        // mast's athwartships plane: long horizontal runs of hits = a yard. Sails/rigging are
        // hidden during the probe session, so only wood registers.
        private static List<(Vector3 center, Vector3 size)> ProbeYards(GameObject root, Bounds bounds,
            List<MastColumn> masts, float deckLowY)
        {
            var result = new List<(Vector3, Vector3)>();
            Vector3 rootPos = root.transform.position;
            const float step = 0.22f;

            foreach (MastColumn m in masts)
            {
                float xHalf = bounds.size.x * 0.5f;
                float yStart = deckLowY + 2.2f, yEnd = m.topY - 0.1f;
                int nx = Mathf.CeilToInt(xHalf * 2f / step);
                int ny = Mathf.CeilToInt((yEnd - yStart) / step);
                if (ny <= 0) continue;

                var candidates = new List<(float y, float x0, float x1, float z)>();
                for (int yi = 0; yi < ny; yi++)
                {
                    float y = yStart + yi * step;
                    var cells = new List<(float x, float z)>();
                    for (int xi = 0; xi <= nx; xi++)
                    {
                        float x = m.pos.x - xHalf + xi * step;
                        Vector3 from = rootPos + new Vector3(x, y, m.pos.z - 2.5f);
                        if (Physics.Raycast(from, Vector3.forward, out RaycastHit hit, 5f, ~0,
                                QueryTriggerInteraction.Ignore))
                            cells.Add((x, hit.point.z - rootPos.z));
                    }

                    // Consecutive-x runs; only spans longer than any nest/pole detail count.
                    int i0 = 0;
                    for (int i = 1; i <= cells.Count; i++)
                    {
                        if (i == cells.Count || cells[i].x - cells[i - 1].x > step * 1.6f)
                        {
                            if (i0 < i && cells[i - 1].x - cells[i0].x >= 2.2f)
                                candidates.Add((y, cells[i0].x, cells[i - 1].x,
                                    cells.Skip(i0).Take(i - i0).Average(c => c.z)));
                            i0 = i;
                        }
                    }
                }

                // Merge vertically adjacent rows into one collider per yard.
                candidates.Sort((a, b) => a.y.CompareTo(b.y));
                int idx = 0;
                while (idx < candidates.Count)
                {
                    float yTop = candidates[idx].y, xMin = candidates[idx].x0, xMax = candidates[idx].x1;
                    float zAcc = candidates[idx].z;
                    int count = 1, j = idx + 1;
                    while (j < candidates.Count && candidates[j].y - yTop <= step * 1.6f)
                    {
                        yTop = candidates[j].y;
                        xMin = Mathf.Min(xMin, candidates[j].x0);
                        xMax = Mathf.Max(xMax, candidates[j].x1);
                        zAcc += candidates[j].z;
                        count++;
                        j++;
                    }
                    idx = j;
                    result.Add((new Vector3((xMin + xMax) * 0.5f, yTop, zAcc / count),
                                new Vector3(xMax - xMin, 0.24f, 0.45f)));
                }
            }
            return result;
        }

        // Sample the bowsprit's top line (centreline, beyond the deck's bow end).
        private static List<(float z, float y)> ProbeBowsprit(GameObject root, Bounds bounds, float deckZMax)
        {
            var pts = new List<(float, float)>();
            Vector3 rootPos = root.transform.position;
            float zEnd = bounds.max.z - rootPos.z;
            for (float z = deckZMax + 0.4f; z < zEnd - 0.05f; z += 0.35f)
            {
                Vector3 from = new Vector3(bounds.center.x, bounds.max.y + 2f, rootPos.z + z);
                if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, bounds.size.y + 4f, ~0,
                        QueryTriggerInteraction.Ignore))
                    pts.Add((z, hit.point.y - rootPos.y));
            }
            return pts;
        }

        // Angled box following the bowsprit's fitted top line, walkable plank-style.
        private static void BuildBowspritCollider(GameObject group, float xCenter, List<(float z, float y)> pts)
        {
            if (pts.Count < 4) return;

            float mz = pts.Average(p => p.z), my = pts.Average(p => p.y);
            float denom = pts.Sum(p => (p.z - mz) * (p.z - mz));
            float slope = denom < 1e-4f ? 0f : pts.Sum(p => (p.z - mz) * (p.y - my)) / denom;

            var a = new Vector3(xCenter, my + slope * (pts.First().z - mz), pts.First().z);
            var b = new Vector3(xCenter, my + slope * (pts.Last().z - mz), pts.Last().z);

            var go = new GameObject("Bowsprit");
            go.transform.SetParent(group.transform, false);
            go.transform.localPosition = (a + b) * 0.5f - Vector3.up * 0.15f; // top surface on the line
            go.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, (b - a).normalized);
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(0.35f, 0.3f, Vector3.Distance(a, b) + 0.3f);
        }

        // Applies the spec's handling numbers and wires the sail visuals (by naming convention:
        // "SailUp"/"Sails_Up" children are the furled set, other "Sails" children are the set set).
        private static void WireShipTuning(ShipController ship, ShipSpec spec, GameObject hull)
        {
            var furled = new List<GameObject>();
            var set = new List<GameObject>();
            foreach (Transform t in hull.GetComponentsInChildren<Transform>(true))
            {
                if (t.GetComponent<Renderer>() == null) continue;
                string n = t.name;
                bool isFurled = n.Contains("SailUp") || n.Contains("Sails_Up");
                bool isSet = !isFurled && n.Contains("Sails") && !n.Contains("Rigging");
                if (isFurled) { furled.Add(t.gameObject); t.gameObject.SetActive(true); }
                else if (isSet) { set.Add(t.gameObject); t.gameObject.SetActive(false); } // start furled
            }

            var so = new SerializedObject(ship);
            so.FindProperty("rudderTurnAccel").floatValue = spec.rudderTurnAccel;
            SetFloatArray(so.FindProperty("sailThrust"), spec.sailThrust);
            SetObjectArray(so.FindProperty("sailsFurledVisuals"), furled);
            SetObjectArray(so.FindProperty("sailsSetVisuals"), set);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // The dressed Synty variants include a ship's wheel child; use it in place. Fallback for
        // bare hulls: instantiate one at the pose the medium attachments variant uses.
        private static Transform EnsureWheel(GameObject root, GameObject hull)
        {
            foreach (Transform t in hull.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("SM_Prop_ShipWheel")) continue;
                FitLocalBox(t.gameObject, 0.1f, 0.3f);
                return t;
            }

            // Fallback: place the standalone wheel prop near the stern (medium-hull pose).
            Vector3 pos = new Vector3(0f, 1.6f, -3.5f);
            Quaternion rot = Quaternion.identity;
            var attachments = AssetDatabase.LoadAssetAtPath<GameObject>(MediumAttachmentsPath);
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

            var wheelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(WheelFallbackPath);
            var wheel = (GameObject)PrefabUtility.InstantiatePrefab(wheelPrefab);
            wheel.name = "HelmWheel";
            wheel.transform.SetParent(root.transform, false);
            wheel.transform.localPosition = pos;
            wheel.transform.localRotation = rot;
            FitLocalBox(wheel, 0.1f, 0.3f);
            return wheel.transform;
        }

        // Fitted box colliders for the deck props (cannons, crates, barrels) so they read as
        // solid. Skips the wheel (it has the interaction collider) and children of already
        // handled props (crate stacks nest props inside props).
        private static void AddPropColliders(GameObject hull, Transform wheel)
        {
            var handled = new List<Transform>();
            IEnumerable<Transform> props = hull.GetComponentsInChildren<Transform>(true)
                .Where(t => t.name.StartsWith("SM_Prop_"))
                .OrderBy(Depth);
            foreach (Transform t in props)
            {
                if (t == wheel || t.IsChildOf(wheel)) continue;
                if (handled.Any(h => t.IsChildOf(h))) continue;
                if (FitLocalBox(t.gameObject, 0.03f, 0.15f) != null) handled.Add(t);
            }
        }

        private static int Depth(Transform t)
        {
            int d = 0;
            for (Transform p = t.parent; p != null; p = p.parent) d++;
            return d;
        }

        private static void BuildNestPlatform(GameObject group, Vector3 nest)
        {
            const float size = 1.9f, rail = 0.7f, t = 0.12f;
            AddBox(group, "CrowsNest", new Vector3(nest.x, nest.y - 0.15f, nest.z),
                new Vector3(size, 0.3f, size), false);
            float railY = nest.y + rail * 0.5f;
            float edge = size * 0.5f - t * 0.5f;
            AddBox(group, "NestRail", new Vector3(nest.x - edge, railY, nest.z), new Vector3(t, rail, size), false);
            AddBox(group, "NestRail", new Vector3(nest.x + edge, railY, nest.z), new Vector3(t, rail, size), false);
            AddBox(group, "NestRail", new Vector3(nest.x, railY, nest.z - edge), new Vector3(size, rail, t), false);
            AddBox(group, "NestRail", new Vector3(nest.x, railY, nest.z + edge), new Vector3(size, rail, t), false);
        }

        private struct ClimbLine
        {
            public Vector3 a, b; // local-space bottom and top of the climbable line
        }

        // Find the Synty shrouds (the triangular rope rigging from the bulwarks up toward each
        // nest) by raycasting inward at descending heights against temp colliders on the rigging
        // meshes only. Returns one climb line per mast side that has rigging.
        private static List<ClimbLine> ProbeRiggingClimbs(GameObject root, List<Vector3> nests)
        {
            var lines = new List<ClimbLine>();
            Vector3 rootPos = root.transform.position;

            var temp = new List<MeshCollider>();
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || !mf.name.Contains("Rigging") || !mf.gameObject.activeInHierarchy)
                    continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                temp.Add(mc);
            }
            if (temp.Count == 0) return lines;
            Physics.SyncTransforms();

            try
            {
                foreach (Vector3 nest in nests)
                {
                    foreach (int side in new[] { -1, 1 })
                    {
                        var pts = new List<Vector2>(); // (x, y) local hits down the shroud
                        for (float dy = 0.7f; dy < 14f; dy += 0.5f)
                        {
                            float y = nest.y - dy;
                            Vector3 from = rootPos + new Vector3(nest.x + side * 8f, y, nest.z);
                            if (Physics.Raycast(from, new Vector3(-side, 0f, 0f), out RaycastHit hit,
                                    16f, ~0, QueryTriggerInteraction.Ignore))
                            {
                                float x = hit.point.x - rootPos.x;
                                if ((x - nest.x) * side > 0.3f) // this side's shroud, not the mast
                                    pts.Add(new Vector2(x, y));
                            }
                        }
                        if (pts.Count < 3) continue;

                        Vector2 bottom = pts.OrderBy(p => p.y).First();
                        // Top overshoots past the nest floor, pulled slightly inboard, so the
                        // climber rises beside the nest and pushes in over its rail.
                        lines.Add(new ClimbLine
                        {
                            a = new Vector3(bottom.x, bottom.y, nest.z),
                            b = new Vector3(Mathf.Lerp(bottom.x, nest.x, 0.8f), nest.y + 1.6f, nest.z),
                        });
                    }
                }
            }
            finally
            {
                foreach (MeshCollider mc in temp) Object.DestroyImmediate(mc);
            }
            return lines;
        }

        // Angled Ladder trigger volumes along the shroud lines. No visuals — the rigging mesh
        // IS the ladder; generous thickness so the vertical climb tracks the sloped ropes.
        private static void BuildRiggingClimbVolumes(GameObject parent, List<ClimbLine> lines)
        {
            int i = 0;
            foreach (ClimbLine line in lines)
            {
                var go = new GameObject($"RiggingClimb{++i}");
                go.transform.SetParent(parent.transform, false);
                Vector3 d = line.b - line.a;
                go.transform.localPosition = (line.a + line.b) * 0.5f;
                go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, d.normalized);
                var box = go.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.size = new Vector3(1.1f, d.magnitude + 0.6f, 1.2f);
                go.AddComponent<Ladder>();
            }
        }

        /// <summary>
        /// In-place migration of an existing ship prefab: removes the built wooden NestLadders
        /// and adds rigging climb volumes instead. Deliberately NOT a rebuild — it preserves any
        /// hand-adjusted colliders in the prefab. Probing runs on a temporary scene instance
        /// (prefab-contents previews have no physics scene to raycast against).
        /// </summary>
        [MenuItem("Tools/Ship/Replace Nest Ladders With Rigging Climb")]
        public static void ReplaceLaddersWithRiggingMenu()
        {
            GameObject harbor = GameObject.Find("Harbor");
            var current = harbor != null ? harbor.GetComponentInChildren<ShipController>(true) : null;
            ShipSpec spec = current != null && current.name.Contains("Medium") ? MediumSpec
                          : current != null && current.name.Contains("Large") ? LargeSpec : WarshipSpec;
            PatchPrefabRiggingClimb(PrefabPathFor(spec));
        }

        private static void PatchPrefabRiggingClimb(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) return;

            List<ClimbLine> lines;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                instance.transform.position = new Vector3(0f, 500f, 0f);
                Physics.SyncTransforms();
                List<Vector3> nests = instance.GetComponentsInChildren<BoxCollider>(true)
                    .Where(b => b.name == "CrowsNest")
                    .Select(b => b.center + new Vector3(0f, 0.15f, 0f)) // box top = nest floor
                    .ToList();
                lines = ProbeRiggingClimbs(instance, nests);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
            if (lines.Count == 0)
            {
                Debug.LogWarning($"[ShipTestAreaBuilder] No rigging lines found on {path}; ladders left in place.");
                return;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                foreach (Transform t in contents.GetComponentsInChildren<Transform>(true)
                             .Where(t => t.name == "NestLadder").ToArray())
                    Object.DestroyImmediate(t.gameObject);
                BuildRiggingClimbVolumes(contents, lines);
                PrefabUtility.SaveAsPrefabAsset(contents, path);
                Debug.Log($"[ShipTestAreaBuilder] {path}: removed wooden ladders, added {lines.Count} rigging climb volume(s). " +
                          "Hand-adjusted colliders untouched.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // Maintenance: run the ladder→rigging migration once on the moored ship's prefab.
        private static void EnsureRiggingClimbOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            if (harbor == null) return;
            var current = harbor.GetComponentInChildren<ShipController>(true);
            if (current == null) return;

            ShipSpec spec = current.name.Contains("Medium") ? MediumSpec
                          : current.name.Contains("Large") ? LargeSpec : WarshipSpec;
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPathFor(spec));
            if (asset == null || FindDeep(asset.transform, "NestLadder") == null) return;
            PatchPrefabRiggingClimb(PrefabPathFor(spec));
        }

        /// <summary>In-place removal of the nest jump pads from a ship prefab (rigging climbs
        /// are the way up now). Not a rebuild — hand-adjusted colliders stay untouched.</summary>
        [MenuItem("Tools/Ship/Remove Nest Jump Pads")]
        public static void RemoveNestJumpPadsMenu()
        {
            GameObject harbor = GameObject.Find("Harbor");
            var current = harbor != null ? harbor.GetComponentInChildren<ShipController>(true) : null;
            ShipSpec spec = current != null && current.name.Contains("Medium") ? MediumSpec
                          : current != null && current.name.Contains("Large") ? LargeSpec : WarshipSpec;
            RemoveNestJumpPads(PrefabPathFor(spec));
        }

        private static void RemoveNestJumpPads(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null || FindDeep(asset.transform, "NestJumpPad") == null) return;

            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                int removed = 0;
                foreach (Transform t in contents.GetComponentsInChildren<Transform>(true)
                             .Where(t => t.name == "NestJumpPad").ToArray())
                {
                    Object.DestroyImmediate(t.gameObject);
                    removed++;
                }
                PrefabUtility.SaveAsPrefabAsset(contents, path);
                Debug.Log($"[ShipTestAreaBuilder] {path}: removed {removed} nest jump pad(s).");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // Maintenance: strip nest jump pads from the moored ship's prefab once.
        private static void EnsureNoNestJumpPadsOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            if (harbor == null) return;
            var current = harbor.GetComponentInChildren<ShipController>(true);
            if (current == null) return;

            ShipSpec spec = current.name.Contains("Medium") ? MediumSpec
                          : current.name.Contains("Large") ? LargeSpec : WarshipSpec;
            RemoveNestJumpPads(PrefabPathFor(spec));
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

        private static void BuildHarborInScene(BuildResult result)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            if (GameObject.Find("Harbor") != null)
            {
                Debug.Log("[ShipTestAreaBuilder] Harbor already exists in the scene; leaving it untouched.");
                return;
            }

            float shipY = DockTopY - result.deckMainY;
            float waterY = Mathf.Min(shipY + Mathf.Lerp(result.hullMinY, result.deckMainY, 0.45f), DockTopY - 0.5f);

            var harbor = new GameObject("Harbor");

            // Water surface (visual only — no collider, you fall through into the hazard).
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "Water";
            Object.DestroyImmediate(water.GetComponent<Collider>());
            water.transform.SetParent(harbor.transform, false);
            water.transform.position = new Vector3(0f, waterY, -80f); // z -150 .. -10
            water.transform.localScale = new Vector3(14f, 1f, 14f);   // 140x140 m
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

            // Raised dock with stairs from the shore, plus checkpoint.
            BuildDock(harbor);

            // The ship, moored alongside the dock, bow pointing out to sea (-Z).
            var ship = (GameObject)PrefabUtility.InstantiatePrefab(result.prefab);
            ship.transform.SetParent(harbor.transform, false);
            ship.transform.position = result.MooringPosition;
            ship.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            BuildGangway(harbor, result, ship.transform.position);

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

        /// <summary>Raised dock platform + shore stairs + the drowning checkpoint. The player
        /// walks up the steps (each within CharacterController step height) onto the pier.</summary>
        private static void BuildDock(GameObject harbor)
        {
            Material wood = GetOrCreateMaterial(WoodMatPath, new Color(0.42f, 0.29f, 0.17f), 0.1f);

            var dock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            dock.name = "Dock";
            dock.transform.SetParent(harbor.transform, false);
            dock.transform.position = new Vector3(0f, DockTopY - 0.25f, -16.55f);
            dock.transform.localScale = new Vector3(4f, 0.5f, 16.9f); // z -8.1 .. -25
            dock.GetComponent<MeshRenderer>().sharedMaterial = wood;

            // Three steps up from the shore (0.225 each — within the CC's step offset), the
            // fourth "step" being the dock itself.
            for (int i = 1; i <= 3; i++)
            {
                var step = GameObject.CreatePrimitive(PrimitiveType.Cube);
                step.name = $"DockStep{i}";
                step.transform.SetParent(harbor.transform, false);
                float top = DockTopY * i / 4f;
                step.transform.position = new Vector3(0f, top - 0.25f, -6f - 0.7f * i + 0.35f);
                step.transform.localScale = new Vector3(4f, 0.5f, 0.7f);
                step.GetComponent<MeshRenderer>().sharedMaterial = wood;
            }

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
        }

        /// <summary>Boarding plank from the dock edge onto the ship's deck, through the
        /// curb-height gangway gap in the starboard rail. Rebuilt on every ship swap since its
        /// far end depends on the moored hull's beam. Stays behind (dock furniture) when the
        /// ship sails; boarding away from the dock is a hop over the 0.55m rail anywhere.</summary>
        private static void BuildGangway(GameObject harbor, BuildResult result, Vector3 moor)
        {
            Transform old = harbor.transform.Find("Gangway");
            if (old != null) Object.DestroyImmediate(old.gameObject);

            float hullSideX = moor.x - result.beamHalf * 0.95f;   // dock-facing hull side
            float a = DockEdgeX - 0.3f, b = hullSideX + 0.45f;    // overlap both ends slightly

            var plank = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plank.name = "Gangway";
            plank.transform.SetParent(harbor.transform, false);
            // Ship faces -Z (yaw 180), so ship-local z maps to world moor.z - localZ; the plank
            // meets the rail gap at the boarding row.
            plank.transform.position = new Vector3((a + b) * 0.5f, DockTopY - 0.04f,
                moor.z - result.boardingZ);
            plank.transform.localScale = new Vector3(b - a, 0.12f, 1.6f);
            plank.GetComponent<MeshRenderer>().sharedMaterial = GetOrCreateMaterial(
                WoodMatPath, new Color(0.42f, 0.29f, 0.17f), 0.1f);
        }

        /// <summary>Sets the harbor's water (and drown hazard) to the moored ship's waterline —
        /// the deck always boards flush with the dock, so it's the water that adapts to each
        /// hull's proportions (e.g. the warship rides high to keep its gun ports dry).</summary>
        private static void UpdateWaterLevel(GameObject harbor, BuildResult result, ShipSpec spec)
        {
            float shipY = DockTopY - result.deckMainY;
            float waterY = Mathf.Min(
                shipY + result.hullMinY + (result.deckMainY - result.hullMinY) * spec.draftFraction,
                DockTopY - 0.5f);

            Transform water = harbor.transform.Find("Water");
            if (water != null)
                water.position = new Vector3(water.position.x, waterY, water.position.z);
            Transform hazard = harbor.transform.Find("WaterHazard");
            if (hazard != null)
                hazard.position = new Vector3(hazard.position.x, waterY - 1.8f, hazard.position.z);
            Debug.Log($"[ShipTestAreaBuilder] Waterline set to y={waterY:F2} for {spec.prefabName}.");
        }

        // Scene migration: harbors built before the raised dock get the stairs version, then the
        // current ship is re-swapped so its mooring height, rails, and gangway match.
        private static void RebuildDockOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            if (harbor == null) return;
            Transform dock = harbor.transform.Find("Dock");
            if (dock == null) return;

            float top = dock.position.y + dock.localScale.y * 0.5f;
            if (Mathf.Abs(top - DockTopY) < 0.05f) return; // already the raised dock

            Object.DestroyImmediate(dock.gameObject);
            foreach (string name in new[] { "DockCheckpoint", "Gangway", "DockStep1", "DockStep2", "DockStep3" })
            {
                Transform t = harbor.transform.Find(name);
                if (t != null) Object.DestroyImmediate(t.gameObject);
            }
            BuildDock(harbor);

            var current = harbor.GetComponentInChildren<ShipController>(true);
            ShipSpec spec = WarshipSpec;
            if (current != null && current.name.Contains("Medium")) spec = MediumSpec;
            else if (current != null && current.name.Contains("Large")) spec = LargeSpec;
            Debug.Log("[ShipTestAreaBuilder] Dock rebuilt with shore stairs; re-mooring the ship to match.");
            SwapHarborShip(spec); // rebuilds rails/gangway and saves the scene
        }

        // Scene migration: if the moored ship's prefab was generated by an older builder
        // version, rebuild it and re-moor so collider/mooring fixes actually reach the scene.
        private static void EnsureCurrentBuildOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            if (harbor == null) return;
            var current = harbor.GetComponentInChildren<ShipController>(true);
            if (current == null) return;

            ShipSpec spec = current.name.Contains("Medium") ? MediumSpec
                          : current.name.Contains("Large") ? LargeSpec : WarshipSpec;
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPathFor(spec));
            if (asset == null || FindDeep(asset.transform, $"BuildTag_v{BuildVersion}") != null) return;

            Debug.Log($"[ShipTestAreaBuilder] {spec.prefabName} predates builder v{BuildVersion}; rebuilding and re-mooring.");
            SwapHarborShip(spec);
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

        // ---------------------------------------------------------------- maintenance

        /// <summary>
        /// Extends GrabPromptHUD.prefab with the helm prompt rows (take the helm / steering
        /// controls / let go), cloning the existing rows so styling and placement stay consistent.
        /// Idempotent: does nothing if a CanSteer row is already configured.
        /// </summary>
        [MenuItem("Tools/Ship/Add Helm Prompt Rows To HUD")]
        public static void AddHelmPromptRowsMenu() => AddHelmPromptRows(logIfPresent: true);

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

        /// <summary>
        /// Fixes two physics hazards in the legacy Ship.prefab in place (preserving fileIDs so a
        /// scene instance keeps its references): disables the Synty hull's non-convex
        /// MeshColliders and bakes the planar constraints + no-gravity into the Rigidbody.
        /// New-style prefabs get both at build time.
        /// </summary>
        [MenuItem("Tools/Ship/Patch Ship Prefab Physics")]
        public static void PatchShipPrefabPhysics()
        {
            if (!File.Exists(LegacyShipPrefabPath)) return;

            // Cheap check against the asset before doing a full contents edit.
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(LegacyShipPrefabPath);
            var rbAsset = asset != null ? asset.GetComponent<Rigidbody>() : null;
            bool needsPatch = rbAsset != null &&
                (rbAsset.constraints == RigidbodyConstraints.None || rbAsset.useGravity ||
                 asset.GetComponentsInChildren<MeshCollider>(true).Any(mc => mc.enabled));
            if (!needsPatch) return;

            GameObject ship = PrefabUtility.LoadPrefabContents(LegacyShipPrefabPath);
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

                PrefabUtility.SaveAsPrefabAsset(ship, LegacyShipPrefabPath);
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

        // ---------------------------------------------------------------- helpers

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

        private static void AddBox(GameObject parent, string name, Vector3 center, Vector3 size, bool trigger)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var box = go.AddComponent<BoxCollider>();
            box.center = center;
            box.size = size;
            box.isTrigger = trigger;
        }

        /// <summary>Fitted BoxCollider around a prop's renderers, expressed in its local frame.
        /// Returns null if the object has no renderers or already has a collider.</summary>
        private static BoxCollider FitLocalBox(GameObject go, float pad, float minSize)
        {
            if (go.GetComponent<Collider>() != null) return null;
            Renderer[] rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return null;

            Bounds b = rs[0].bounds;
            foreach (Renderer r in rs.Skip(1)) b.Encapsulate(r.bounds);

            var col = go.AddComponent<BoxCollider>();
            Matrix4x4 toLocal = go.transform.worldToLocalMatrix;
            Vector3 s = b.size;
            Vector3 localSize = Abs(toLocal.MultiplyVector(new Vector3(s.x, 0, 0)))
                              + Abs(toLocal.MultiplyVector(new Vector3(0, s.y, 0)))
                              + Abs(toLocal.MultiplyVector(new Vector3(0, 0, s.z)));
            col.center = go.transform.InverseTransformPoint(b.center);
            col.size = Vector3.Max(localSize + Vector3.one * pad, Vector3.one * minSize);
            return col;
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

        private static float Percentile(List<float> values, float p)
        {
            values.Sort();
            int i = Mathf.Clamp(Mathf.RoundToInt((values.Count - 1) * p), 0, values.Count - 1);
            return values[i];
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private static void SetObjectArray(SerializedProperty prop, List<GameObject> items)
        {
            prop.arraySize = items.Count;
            for (int i = 0; i < items.Count; i++)
                prop.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
        }

        private static void SetFloatArray(SerializedProperty prop, float[] items)
        {
            prop.arraySize = items.Length;
            for (int i = 0; i < items.Length; i++)
                prop.GetArrayElementAtIndex(i).floatValue = items[i];
        }

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
