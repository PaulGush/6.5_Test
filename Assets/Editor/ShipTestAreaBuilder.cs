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
        private const string AnchorPropPath = SyntyRoot + "/Props/SM_Prop_Anchor_01.prefab";

        private const string WaterMatPath = "Assets/Art/Materials/Sea_Water.mat";
        private const string SeaMeshPath = "Assets/Art/Models/SeaGrid.asset";
        private const string SeaMeshName = "SeaDisc4"; // bump when the sea mesh layout changes
        private const string SeaShaderName = "Sea/Waves";
        private const string WoodMatPath = "Assets/Art/Materials/Sea_DockWood.mat";
        private const string RopeMatPath = "Assets/Art/Materials/Sea_Rope.mat";
        private const string SandMatPath = "Assets/Art/Materials/Sea_Sand.mat";
        private const string IslandMeshPath = "Assets/Art/Models/StartIsland.asset";
        private const string IslandMeshName = "StartIsland2"; // bump when the island layout changes

        // Bump when generated-collider logic changes: prefabs carrying an older tag are rebuilt
        // and re-moored by the auto-maintenance pass.
        private const int BuildVersion = 4;

        private const string PlayerLayerName = "PlayerBody";
        private const string ShipHullLayerName = "ShipHull";

        private const float DockTopY = 0.9f;    // walkable dock height; every ship's deck aligns to it
        private const float DockEdgeX = 4f;     // dock is 8 wide, centred on x=0
        private const float DockCenterZ = 0f;   // dock centred on the origin: it is the spawn floor
        private const float RailHeight = 0.55f; // bulwark colliders: keeps cargo in, jumpable by players

        // ------------------------------------------------------------------ ship specs

        private class ShipSpec
        {
            public string prefabName;   // asset name under Assets/Prefabs/
            public string hullPath;     // dressed Synty variant (attachments include wheel/cannons)
            public float mass;
            public float rudderTurnAccel;
            public float maxSailThrust; // acceleration with every mast unfurled; per-count thrust
                                        // is generated linearly from the mast count
            public bool crowsNest;      // probe masts for nest platforms + climbable rigging
            public float draftFraction; // how deep the hull sits: waterline as a fraction of
                                        // hull-bottom → main-deck (lower = rides higher)
        }

        private static ShipSpec MediumSpec => new ShipSpec
        {
            prefabName = "Ship_Medium",
            hullPath = MediumAttachmentsPath,
            mass = 3000f,
            rudderTurnAccel = 14f,
            maxSailThrust = 3.6f,
            crowsNest = false,
            draftFraction = 0.45f,
        };

        private static ShipSpec WarshipSpec => new ShipSpec
        {
            prefabName = "Ship_Warship",
            hullPath = SyntyRoot + "/Vehicles/SM_Veh_Boat_Warship_01_Hull_Attachments.prefab",
            mass = 8000f,
            rudderTurnAccel = 9f,       // heavier ship, wider turns — the chaos scales up
            maxSailThrust = 4.2f,
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
            maxSailThrust = 3.9f,
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
                DockCenterZ + 4.55f - DeckHalfLength);          // hull alongside, stern past the dock
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
                EnsureAnchorStationOnce();
                EnsureShipFloatOnce();
                EnsureFloatDynamicsOnce();
                EnsurePlayerRockOnce();
                EnsureHelmPoseOnce();
                EnsureCameraSwayOnce();
                EnsureWaterSurfaceOnce();
                EnsureSeaWavesOnce();
                EnsureSeaFollowOnce();
                EnsureJettiesOnce();
                EnsureStartIslandOnce();
                EnsureDockShoreStairsOnce();
                EnsureDockPilesOnce();
                EnsureArchipelagoOnce();
                EnsureSharksOnce();
                EnsureShipMoored();
                EnsureDayNightOnce();
                EnsureSkyOnce();
                EnsurePlayerPhysicsIsolation();
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

                // Physics + networking on the root; rotation constraints baked into the asset
                // so they hold from the very first physics step. Height is NOT frozen: the
                // ship's heave rides the wave field (ShipController drives it at runtime).
                var rb = root.AddComponent<Rigidbody>();
                rb.mass = spec.mass;
                rb.useGravity = false;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.constraints = RigidbodyConstraints.FreezeRotationX
                               | RigidbodyConstraints.FreezeRotationZ;

                root.AddComponent<NetworkIdentity>();
                var nt = root.AddComponent<NetworkTransformReliable>();
                nt.target = root.transform;
                nt.syncDirection = SyncDirection.ServerToClient;
                nt.syncInterval = 0.05f;
                nt.coordinateSpace = CoordinateSpace.World;

                var ship = root.AddComponent<ShipController>();
                WireShipTuning(root, ship, spec, hull, masts,
                    pos => rows.OrderBy(r => Mathf.Abs(r.z - pos.z)).First().y);
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

                IsolatePlayerColliders(root);

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

        private static void WireShipTuning(GameObject root, ShipController ship, ShipSpec spec,
            GameObject hull, List<MastColumn> masts, System.Func<Vector3, float> deckYAt)
        {
            var so = new SerializedObject(ship);
            so.FindProperty("rudderTurnAccel").floatValue = spec.rudderTurnAccel;
            so.ApplyModifiedPropertiesWithoutUndo();
            CreateSailStations(root, ship, spec, hull, masts, deckYAt);
        }

        /// <summary>
        /// One ShipSailStation per mast that owns sails. Sail visuals (by naming convention:
        /// "SailUp"/"Sails_Up" = furled, other "Sails" = set) are assigned to their nearest mast
        /// along the ship's length, each mast gets an interaction collider at its base marked
        /// with a ShipSailTarget, and the controller's thrust table is generated linearly from
        /// the mast count (speed = number of unfurled masts). Shared by fresh builds and the
        /// in-place patch of existing prefabs.
        /// </summary>
        private static void CreateSailStations(GameObject root, ShipController ship, ShipSpec spec,
            GameObject hull, List<MastColumn> masts, System.Func<Vector3, float> deckYAt)
        {
            Vector3 rootPos = root.transform.position;

            // Every sail visual, tagged furled/set, keyed by its position along the ship.
            var visuals = new List<(GameObject go, float z, bool furled)>();
            foreach (Transform t in hull.GetComponentsInChildren<Transform>(true))
            {
                var renderer = t.GetComponent<Renderer>();
                if (renderer == null) continue;
                string n = t.name;
                bool isFurled = n.Contains("SailUp") || n.Contains("Sails_Up");
                bool isSet = !isFurled && n.Contains("Sails") && !n.Contains("Rigging");
                if (isFurled || isSet)
                    visuals.Add((t.gameObject, renderer.bounds.center.z - rootPos.z, isFurled));
            }

            var stations = new List<ShipSailStation>();
            foreach (MastColumn m in masts)
            {
                MastColumn mast = m;
                var mine = visuals.Where(v =>
                    masts.OrderBy(o => Mathf.Abs(v.z - o.pos.z)).First().pos == mast.pos).ToList();
                if (mine.Count == 0) continue; // mast with no canvas — not a station

                var station = root.AddComponent<ShipSailStation>();
                var so = new SerializedObject(station);
                SerializedProperty furledProp = so.FindProperty("furledVisuals");
                SerializedProperty setProp = so.FindProperty("setVisuals");
                var furled = mine.Where(v => v.furled).Select(v => v.go).ToList();
                var set = mine.Where(v => !v.furled).Select(v => v.go).ToList();
                furledProp.arraySize = furled.Count;
                for (int i = 0; i < furled.Count; i++)
                    furledProp.GetArrayElementAtIndex(i).objectReferenceValue = furled[i];
                setProp.arraySize = set.Count;
                for (int i = 0; i < set.Count; i++)
                    setProp.GetArrayElementAtIndex(i).objectReferenceValue = set[i];
                so.ApplyModifiedPropertiesWithoutUndo();

                foreach (GameObject go in furled) go.SetActive(true);  // ships start all-furled
                foreach (GameObject go in set) go.SetActive(false);

                // Interaction collider at the mast base (a collar around the pole).
                var marker = new GameObject("SailStationTarget");
                marker.transform.SetParent(root.transform, false);
                marker.transform.localPosition = new Vector3(m.pos.x, deckYAt(m.pos) + 1.1f, m.pos.z);
                var box = marker.AddComponent<BoxCollider>();
                box.size = new Vector3(0.7f, 1.4f, 0.7f);
                marker.AddComponent<ShipSailTarget>().SetStation(station);

                stations.Add(station);
            }

            // Controller wiring: stations + a linear thrust table for 0..N masts unfurled.
            var soShip = new SerializedObject(ship);
            SerializedProperty stationsProp = soShip.FindProperty("sailStations");
            stationsProp.arraySize = stations.Count;
            for (int i = 0; i < stations.Count; i++)
                stationsProp.GetArrayElementAtIndex(i).objectReferenceValue = stations[i];
            var thrust = new float[stations.Count + 1];
            for (int i = 1; i < thrust.Length; i++)
                thrust[i] = spec.maxSailThrust * i / Mathf.Max(1, stations.Count);
            SetFloatArray(soShip.FindProperty("sailThrust"), thrust);
            soShip.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void PatchPrefabAnchorStation(ShipSpec spec)
        {
            string path = PrefabPathFor(spec);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null || asset.GetComponentInChildren<ShipAnchorTarget>(true) != null) return;

            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (!CreateAnchorStation(contents, path)) return;
                PrefabUtility.SaveAsPrefabAsset(contents, path);
                Debug.Log($"[ShipTestAreaBuilder] {path}: anchor station added at the port bow. " +
                          "Hand-adjusted colliders untouched.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // Builds the whole station subtree on loaded prefab contents: interact marker on the
        // bulwark, anchor prop hung OUTSIDE the hull planking (the Hull box's beam, not the
        // narrower walkable deck strip), cathead + rope rigging. False if the hull lacks the
        // pieces the placement is derived from.
        private static bool CreateAnchorStation(GameObject contents, string path)
        {
            var ship = contents.GetComponent<ShipController>();
            BoxCollider fore = ForecastleDeck(contents);
            BoxCollider hullBox = contents.GetComponentsInChildren<BoxCollider>(true)
                .FirstOrDefault(b => b.name == "Hull" && !b.isTrigger);
            if (ship == null || fore == null || hullBox == null)
            {
                Debug.LogWarning($"[ShipTestAreaBuilder] {path}: no ShipController/bow decks/hull box " +
                                 "found; anchor station not added.");
                return false;
            }
            float deckTop = fore.center.y + fore.size.y * 0.5f;
            float halfW = fore.size.x * 0.5f;          // walkable forecastle strip
            float hullHalf = hullBox.size.x * 0.5f;    // the hull's real outer beam

            var station = new GameObject("AnchorStation");
            station.transform.SetParent(contents.transform, false);
            var view = station.AddComponent<ShipAnchorView>();
            view.SetShip(ship);

            // Interact marker on the port bulwark (+z is the bow, port is -x).
            var marker = new GameObject("AnchorStationTarget");
            marker.transform.SetParent(station.transform, false);
            marker.transform.localPosition =
                new Vector3(-(halfW - 0.15f), deckTop + 0.55f, fore.center.z);
            var box = marker.AddComponent<BoxCollider>();
            box.size = new Vector3(0.6f, 1.1f, 1.1f);
            marker.AddComponent<ShipAnchorTarget>().SetShip(ship);
            MakeKinematic(marker); // players lean on it; it must never shove the hull

            // The anchor itself, clear of the hull side below the rail; ShipAnchorView
            // rides it down when dropped. Purely visual, so no prefab link and no colliders.
            var propAsset = AssetDatabase.LoadAssetAtPath<GameObject>(AnchorPropPath);
            if (propAsset != null)
            {
                GameObject prop = Object.Instantiate(propAsset, station.transform);
                prop.name = "AnchorProp";
                prop.transform.localPosition =
                    new Vector3(-(hullHalf + 0.35f), deckTop - 1.3f, fore.center.z + 0.4f);
                prop.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                foreach (Collider c in prop.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(c);
                view.SetAnchor(prop.transform);
                view.SetDropDistance(AnchorDropDistanceFor(prop.transform.localPosition.y));
                AddAnchorRigging(station, view, prop.transform, deckTop, halfW);
            }
            else
            {
                Debug.LogWarning($"[ShipTestAreaBuilder] Anchor prop missing at {AnchorPropPath}; " +
                                 "station added without a visual.");
            }
            return true;
        }

        // The raised forecastle Deck strip the anchor station hangs off: the farthest-forward
        // strip still at (or near) the bow half's highest deck level — skipping the low
        // beakhead platform right at the stem. Null when the hull has no bow decks.
        private static BoxCollider ForecastleDeck(GameObject contents)
        {
            var bowDecks = contents.GetComponentsInChildren<BoxCollider>(true)
                .Where(b => b.name == "Deck" && b.center.z > 0f).ToList();
            if (bowDecks.Count == 0) return null;
            float topY = bowDecks.Max(b => b.center.y);
            return bowDecks.OrderByDescending(b => b.center.z)
                .First(b => b.center.y >= topY - 0.75f);
        }

        // What the anchor actually hangs from: a cathead beam running from inside the bulwark
        // out over the ship's side to directly above the anchor, and a rope (a stretched
        // cylinder) that ShipAnchorView keeps taut from the beam's tip to the anchor's ring
        // as it runs out and is hauled back in. Purely visual.
        private static void AddAnchorRigging(GameObject station, ShipAnchorView view,
            Transform anchor, float deckTop, float halfW)
        {
            Material wood = GetOrCreateMaterial(WoodMatPath, new Color(0.42f, 0.29f, 0.17f), 0.1f);
            float tip = anchor.localPosition.x, z = anchor.localPosition.z;
            float inner = -(halfW - 0.6f); // start well inside the walkable deck

            var cathead = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cathead.name = "Cathead";
            cathead.transform.SetParent(station.transform, false);
            cathead.transform.localPosition =
                new Vector3((tip + inner) * 0.5f, deckTop + 0.95f, z);
            cathead.transform.localScale =
                new Vector3(Mathf.Abs(tip - inner) + 0.3f, 0.18f, 0.18f);
            Object.DestroyImmediate(cathead.GetComponent<Collider>());
            cathead.GetComponent<MeshRenderer>().sharedMaterial = wood;

            var top = new GameObject("AnchorRopeTop");
            top.transform.SetParent(station.transform, false);
            top.transform.localPosition = new Vector3(tip, deckTop + 0.85f, z);

            var rope = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            rope.name = "AnchorRope";
            rope.transform.SetParent(station.transform, false);
            Object.DestroyImmediate(rope.GetComponent<Collider>());
            rope.GetComponent<MeshRenderer>().sharedMaterial =
                GetOrCreateMaterial(RopeMatPath, new Color(0.62f, 0.51f, 0.34f), 0.05f);

            // Stowed pose baked in so the prefab looks right in the editor; the view keeps
            // it taut at runtime with the same math.
            Vector3 ring = anchor.localPosition + Vector3.up * 0.75f;
            Vector3 run = top.transform.localPosition - ring;
            rope.transform.localPosition = ring + run * 0.5f;
            rope.transform.localRotation = Quaternion.FromToRotation(Vector3.up, run.normalized);
            rope.transform.localScale = new Vector3(0.08f, run.magnitude * 0.5f, 0.08f);

            view.SetRope(rope.transform, top.transform);
        }

        // One-shot (EditorPrefs-keyed, so re-enabling by hand sticks): the sailing slice
        // doesn't need the parkour course or its run clock — deactivate the course root and
        // the run HUD canvas. Inactive scene NetworkIdentities simply don't spawn, and the
        // RunManager stays alive (harmless, and NetworkPlayer references it).

        // Maintenance: swap the harbor's flat default plane for a dense grid running the
        // Sea/Waves shader, so the water actually moves. Idempotent: skips once the Water
        // object carries the SeaGrid mesh and the wave material.
        private static void EnsureSeaWavesOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            Transform water = harbor != null ? harbor.transform.Find("Water") : null;
            if (water == null) return;
            Shader shader = Shader.Find(SeaShaderName);
            if (shader == null) return; // shader asset not imported yet; a later pass gets it

            var filter = water.GetComponent<MeshFilter>();
            var renderer = water.GetComponent<MeshRenderer>();
            if (filter == null || renderer == null) return;
            bool meshDone = filter.sharedMesh != null && filter.sharedMesh.name == SeaMeshName;
            bool shaderDone = renderer.sharedMaterial != null
                              && renderer.sharedMaterial.shader == shader;
            if (meshDone && shaderDone) return;

            if (!meshDone)
            {
                Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(SeaMeshPath);
                if (mesh == null || mesh.name != SeaMeshName) mesh = BuildSeaGridMesh();
                filter.sharedMesh = mesh;
                // The grid is authored at world size, unlike the scaled 10x10 default plane.
                water.localScale = Vector3.one;
            }
            if (!shaderDone)
            {
                Material mat = GetOrCreateMaterial(WaterMatPath, new Color(0.09f, 0.32f, 0.42f), 0.85f);
                mat.shader = shader;
                mat.SetColor("_ShallowColor", new Color(0.13f, 0.45f, 0.55f));
                mat.SetColor("_DeepColor", new Color(0.05f, 0.22f, 0.33f));
                mat.SetColor("_CrestColor", new Color(0.72f, 0.88f, 0.90f));
                EditorUtility.SetDirty(mat);
                renderer.sharedMaterial = mat;
            }

            AssetDatabase.SaveAssets();
            Scene scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[ShipTestAreaBuilder] Harbor water upgraded to the Sea/Waves grid.");
        }

        // A radial disc running out to the horizon. The inner region — the whole playable
        // sea plus the shader's detail-fade radius — keeps a uniform fine ring spacing so
        // wave displacement is never undersampled where it is visible (undersampling is
        // what read as smeared/stretched water); past it, rings grow geometrically to a
        // rim 2.5 km out, where the shader has already faded the waves flat.
        private static Mesh BuildSeaGridMesh()
        {
            const int Sectors = 320;
            const float DenseStep = 2.2f; // uniform ring spacing around the viewer
            // The mesh FOLLOWS the camera (SeaFollowView), so the dense region only needs
            // to reach the shader's detail-fade distance — the ocean itself is unbounded.
            const float DenseReach = 180f;
            const float Growth = 1.05f;   // then ring step ~5% of radius: aspect stays ~1
            const float Reach = 2500f;

            var radii = new List<float> { 0f };
            for (float r = DenseStep; r < DenseReach; r += DenseStep) radii.Add(r);
            for (float r = DenseReach; r < Reach; r *= Growth) radii.Add(r);
            radii.Add(Reach);
            int rings = radii.Count - 1; // ring 1..rings each hold Sectors verts; 0 is centre

            var verts = new Vector3[1 + rings * Sectors];
            for (int k = 1; k <= rings; k++)
                for (int s = 0; s < Sectors; s++)
                {
                    float a = s * Mathf.PI * 2f / Sectors;
                    verts[1 + (k - 1) * Sectors + s] =
                        new Vector3(radii[k] * Mathf.Cos(a), 0f, radii[k] * Mathf.Sin(a));
                }

            int Idx(int ring, int s) => 1 + (ring - 1) * Sectors + s % Sectors;
            var tris = new List<int>(rings * Sectors * 6);
            for (int s = 0; s < Sectors; s++) // centre fan
            {
                tris.Add(0); tris.Add(Idx(1, s + 1)); tris.Add(Idx(1, s));
            }
            for (int k = 1; k < rings; k++)
                for (int s = 0; s < Sectors; s++)
                {
                    int a = Idx(k, s), b = Idx(k, s + 1);
                    int c = Idx(k + 1, s), d = Idx(k + 1, s + 1);
                    tris.Add(a); tris.Add(d); tris.Add(c);
                    tris.Add(a); tris.Add(b); tris.Add(d);
                }

            var mesh = new Mesh
            {
                name = SeaMeshName,
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
                vertices = verts,
                triangles = tris.ToArray(),
            };
            mesh.RecalculateNormals();
            // Padded so vertex displacement in the shader can't get the mesh culled.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(Reach * 2f + 8f, 8f, Reach * 2f + 8f));

            Directory.CreateDirectory(Path.GetDirectoryName(SeaMeshPath));
            AssetDatabase.CreateAsset(mesh, SeaMeshPath);
            return mesh;
        }

        // Maintenance: the sea mesh follows the camera once (world-space waves make the
        // slide invisible), so the ocean stays finely tessellated wherever the player is.
        private static void EnsureSeaFollowOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            Transform water = harbor != null ? harbor.transform.Find("Water") : null;
            if (water == null || water.GetComponent<SeaFollowView>() != null) return;
            water.gameObject.AddComponent<SeaFollowView>();
            Scene scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[ShipTestAreaBuilder] Sea mesh now follows the camera (SeaFollowView).");
        }

        // Maintenance: mark the existing scene's water plane with WaterSurface once, so the
        // anchor (and future water-aware visuals) can measure the surface at runtime.
        private static void EnsureWaterSurfaceOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            Transform water = harbor != null ? harbor.transform.Find("Water") : null;
            if (water == null || water.GetComponent<WaterSurface>() != null) return;
            water.gameObject.AddComponent<WaterSurface>();
            Scene scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[ShipTestAreaBuilder] WaterSurface marker added to the harbor water plane.");
        }

        // How far the anchor must run out (m, from its stowed pose at the given ship-local
        // height) to end up well under the harbor's water surface. Reads the live scene —
        // the moored ship instance and the harbor water plane; falls back when absent.
        // Bakes the FALLBACK drop only — at runtime the view measures the live WaterSurface.
        private static float AnchorDropDistanceFor(float stowedLocalY)
        {
            GameObject harbor = GameObject.Find("Harbor");
            var ship = harbor != null ? harbor.GetComponentInChildren<ShipController>(true) : null;
            Transform water = harbor != null ? harbor.transform.Find("Water") : null;
            if (ship == null || water == null) return 6f;
            float stowedWorldY = ship.transform.position.y + stowedLocalY;
            return stowedWorldY - water.position.y + 2f; // 2 m under the surface
        }

        /// <summary>In-place migration: visual buoyancy for an existing ship prefab. Rocks
        /// the hull's visual subtree (and the anchor station) with the sea's wave field;
        /// the physics root, colliders and network sync stay flat and untouched.</summary>
        private static void PatchPrefabShipFloat(ShipSpec spec)
        {
            string path = PrefabPathFor(spec);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null || asset.GetComponent<ShipFloatView>() != null) return;

            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                // The Synty hull visual subtree ("...Hull..." with renderers) — NOT the
                // plain "Hull" world-collision box on the root.
                Transform hullVisual = null;
                foreach (Transform child in contents.transform)
                    if (child.name != "Hull" && child.name.Contains("Hull")
                        && child.GetComponentInChildren<MeshRenderer>(true) != null)
                    {
                        hullVisual = child;
                        break;
                    }
                if (contents.GetComponent<ShipController>() == null || hullVisual == null)
                {
                    Debug.LogWarning($"[ShipTestAreaBuilder] {path}: no ShipController/hull visual " +
                                     "found; float view not added.");
                    return;
                }

                var view = contents.AddComponent<ShipFloatView>();
                var floatTargets = new List<Transform> { hullVisual };
                Transform station = contents.transform.Find("AnchorStation");
                if (station != null) floatTargets.Add(station);
                view.SetTargets(floatTargets.ToArray());

                BoxCollider hullBox = contents.GetComponentsInChildren<BoxCollider>(true)
                    .FirstOrDefault(b => b.name == "Hull" && !b.isTrigger);
                if (hullBox != null)
                    view.SetExtents(hullBox.size.z * 0.4f, Mathf.Max(2f, hullBox.size.x * 0.45f));

                PrefabUtility.SaveAsPrefabAsset(contents, path);
                Debug.Log($"[ShipTestAreaBuilder] {path}: visual buoyancy added " +
                          $"({floatTargets.Count} rocked subtrees). Colliders and physics untouched.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // Maintenance: retune the float view once for the forced-oscillator dynamics —
        // soft limits need headroom above the old hard clamps, and the hull's natural
        // frequency moves near the swell's so waves visibly work the ship. Only touches
        // values still at the old defaults; anything hand-tuned is left alone.
        private static void EnsureFloatDynamicsOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            var current = harbor != null ? harbor.GetComponentInChildren<ShipController>(true) : null;
            if (current == null) return;

            ShipSpec spec = current.name.Contains("Medium") ? MediumSpec
                          : current.name.Contains("Large") ? LargeSpec : WarshipSpec;
            string path = PrefabPathFor(spec);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var assetView = asset != null ? asset.GetComponent<ShipFloatView>() : null;
            if (assetView == null) return;

            var soAsset = new SerializedObject(assetView);
            bool AtOldDefault(string prop, float value) =>
                Mathf.Abs(soAsset.FindProperty(prop).floatValue - value) < 0.001f;
            if (!AtOldDefault("maxPitch", 1.0f) || !AtOldDefault("maxRoll", 1.5f)
                || !AtOldDefault("maxHeave", 0.25f) || !AtOldDefault("stiffness", 1.5f)) return;

            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var view = contents.GetComponent<ShipFloatView>();
                if (view == null) return;
                var so = new SerializedObject(view);
                so.FindProperty("maxPitch").floatValue = 1.75f;
                so.FindProperty("maxRoll").floatValue = 2.5f;
                so.FindProperty("maxHeave").floatValue = 0.35f;
                so.FindProperty("stiffness").floatValue = 2.0f;
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(contents, path);
                Debug.Log($"[ShipTestAreaBuilder] {path}: float view retuned for wave-forced " +
                          "dynamics (soft limits 1.75°/2.5°/0.35 m, hull frequency 2.0 rad/s).");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // Maintenance: the helmsman's visual stance (face the wheel, grip the rim) once.
        private static void EnsureHelmPoseOnce()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (asset == null || asset.GetComponent<PlayerHelmPose>() != null) return;

            GameObject player = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            try
            {
                player.AddComponent<PlayerHelmPose>();
                PrefabUtility.SaveAsPrefabAsset(player, PlayerPrefabPath);
                Debug.Log("[ShipTestAreaBuilder] Player.prefab: helmsman stance added " +
                          "(PlayerHelmPose — face the wheel, hands on the rim).");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(player);
            }
        }

        // Maintenance: the first-person camera gets its subtle deck sway once. The rig is
        // hand-authored in the scene, so this patches the scene object, not a prefab.
        private static void EnsureCameraSwayOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            var rig = Object.FindAnyObjectByType<Game.Player.CameraRig>(FindObjectsInactive.Include);
            if (rig == null) return;

            var so = new SerializedObject(rig);
            var firstPerson = so.FindProperty("firstPerson").objectReferenceValue
                as Unity.Cinemachine.CinemachineCamera;
            if (firstPerson == null
                || firstPerson.GetComponent<Game.Player.CameraDeckSway>() != null) return;

            firstPerson.gameObject.AddComponent<Game.Player.CameraDeckSway>();
            Scene scene = firstPerson.gameObject.scene;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[ShipTestAreaBuilder] First-person camera: subtle deck sway added (CameraDeckSway).");
        }

        // Maintenance: the player's visual model rides the ship's visual rock once.
        private static void EnsurePlayerRockOnce()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (asset == null || asset.GetComponent<PlayerRockView>() != null) return;

            GameObject player = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            try
            {
                player.AddComponent<PlayerRockView>();
                PrefabUtility.SaveAsPrefabAsset(player, PlayerPrefabPath);
                Debug.Log("[ShipTestAreaBuilder] Player.prefab: model now rocks with the ship deck " +
                          "(PlayerRockView added).");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(player);
            }
        }

        // Maintenance: give the moored ship's prefab its visual buoyancy once.
        private static void EnsureShipFloatOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            if (harbor == null) return;
            var current = harbor.GetComponentInChildren<ShipController>(true);
            if (current == null) return;

            ShipSpec spec = current.name.Contains("Medium") ? MediumSpec
                          : current.name.Contains("Large") ? LargeSpec : WarshipSpec;
            PatchPrefabShipFloat(spec);
        }

        // Maintenance: give the moored ship's prefab its bow anchor station once.
        private static void EnsureAnchorStationOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            if (harbor == null) return;
            var current = harbor.GetComponentInChildren<ShipController>(true);
            if (current == null) return;

            ShipSpec spec = current.name.Contains("Medium") ? MediumSpec
                          : current.name.Contains("Large") ? LargeSpec : WarshipSpec;
            PatchPrefabAnchorStation(spec);
        }

        /// <summary>
        /// Makes players physically unable to shove the ship. Every player-facing solid collider
        /// moves onto a kinematic child Rigidbody (kinematic bodies ignore incoming contacts),
        /// leaving only the "Hull" world-collision box on the dynamic root — and that box goes on
        /// the ShipHull layer, which the runtime ignores against PlayerBody. Works on freshly
        /// built roots and as an in-place patch of existing prefabs.
        /// </summary>
        private static int IsolatePlayerColliders(GameObject root)
        {
            // The world-collision hull box must STAY on the dynamic root: pull it out of the
            // Colliders group before that group goes kinematic (a kinematic hull would sail
            // straight through rocks).
            Transform group = FindDeep(root.transform, "Colliders");
            Transform hullBox = group != null ? group.Find("Hull") : null;
            if (hullBox != null) hullBox.SetParent(root.transform, true);

            int hullLayer = LayerMask.NameToLayer(ShipHullLayerName);
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
                if (c.name == "Hull" && !c.isTrigger && hullLayer >= 0)
                    c.gameObject.layer = hullLayer;

            int added = 0;
            // Group nodes cover whole subtrees (generated colliders; the Synty deck props).
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "Colliders" && t.name != "Attachments") continue;
                if (t.GetComponent<Rigidbody>() != null) continue;
                MakeKinematic(t.gameObject);
                added++;
            }
            // Stragglers still attached to the root body (station markers, fallback wheel...).
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
            {
                if (c.isTrigger || !c.enabled || c.gameObject == root || c.name == "Hull") continue;
                if (HasOwnRigidbody(c.transform, root.transform)) continue;
                MakeKinematic(c.gameObject);
                added++;
            }
            return added;
        }

        private static void MakeKinematic(GameObject go)
        {
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
        }

        private static bool HasOwnRigidbody(Transform t, Transform root)
        {
            for (Transform p = t; p != null && p != root; p = p.parent)
                if (p.GetComponent<Rigidbody>() != null) return true;
            return false;
        }

        private static int EnsureLayer(string name)
        {
            int existing = LayerMask.NameToLayer(name);
            if (existing >= 0) return existing;

            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets.Length == 0) return -1;
            var tagManager = new SerializedObject(assets[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");
            for (int i = 8; i < 32; i++)
            {
                SerializedProperty slot = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(slot.stringValue))
                {
                    slot.stringValue = name;
                    tagManager.ApplyModifiedProperties();
                    Debug.Log($"[ShipTestAreaBuilder] Created layer '{name}' (slot {i}).");
                    return i;
                }
            }
            Debug.LogWarning($"[ShipTestAreaBuilder] No free layer slot for '{name}'.");
            return -1;
        }

        private static void EnsurePlayerPhysicsIsolation(bool force = false)
        {
            if (!force && SceneManager.GetActiveScene().path != ScenePath) return;

            int playerLayer = EnsureLayer(PlayerLayerName);
            EnsureLayer(ShipHullLayerName);

            // Player prefab onto its own layer (the runtime ignores PlayerBody vs ShipHull).
            if (playerLayer >= 0)
            {
                var playerAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
                if (playerAsset != null && playerAsset.layer != playerLayer)
                {
                    GameObject player = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
                    try
                    {
                        player.layer = playerLayer;
                        PrefabUtility.SaveAsPrefabAsset(player, PlayerPrefabPath);
                        Debug.Log($"[ShipTestAreaBuilder] Player.prefab moved to layer '{PlayerLayerName}'.");
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(player);
                    }
                }
            }

            // Moored ship's prefab: colliders onto kinematic children (in place, preserving
            // hand-adjusted colliders).
            GameObject harbor = GameObject.Find("Harbor");
            var current = harbor != null ? harbor.GetComponentInChildren<ShipController>(true) : null;
            if (current == null) return;
            ShipSpec spec = current.name.Contains("Medium") ? MediumSpec
                          : current.name.Contains("Large") ? LargeSpec : WarshipSpec;
            string path = PrefabPathFor(spec);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) return;
            Transform assetGroup = FindDeep(asset.transform, "Colliders");
            if (assetGroup != null && assetGroup.GetComponent<Rigidbody>() != null) return; // done

            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                int added = IsolatePlayerColliders(contents);
                PrefabUtility.SaveAsPrefabAsset(contents, path);
                Debug.Log($"[ShipTestAreaBuilder] {path}: player physics isolated " +
                          $"({added} kinematic bodies added; hull box on '{ShipHullLayerName}'). " +
                          "Hand-adjusted colliders untouched.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
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
                if (player.GetComponent<PlayerRockView>() == null)
                {
                    player.AddComponent<PlayerRockView>();
                    changed = true;
                }
                if (player.GetComponent<PlayerHelmPose>() == null)
                {
                    player.AddComponent<PlayerHelmPose>();
                    changed = true;
                }
                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(player, PlayerPrefabPath);
                    Debug.Log("[ShipTestAreaBuilder] Player.prefab: added ShipRider + PlayerHelmUser + PlayerRockView + PlayerHelmPose.");
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

            // Water surface (visual only — no collider, you fall through and swim).
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "Water";
            Object.DestroyImmediate(water.GetComponent<Collider>());
            water.transform.SetParent(harbor.transform, false);
            water.transform.position = new Vector3(0f, waterY, -80f); // z -150 .. -10
            water.transform.localScale = new Vector3(14f, 1f, 14f);   // 140x140 m
            water.AddComponent<WaterSurface>(); // runtime "where is the surface" marker
            water.GetComponent<MeshRenderer>().sharedMaterial = GetOrCreateMaterial(
                WaterMatPath, new Color(0.09f, 0.32f, 0.42f), 0.85f);

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

        /// <summary>Raised dock platform + the dock checkpoint. Centred on the origin —
        /// it is the spawn floor now that the shore course is gone — and wide enough that
        /// every spawn point lands on the planks.</summary>
        private static void BuildDock(GameObject harbor)
        {
            Material wood = GetOrCreateMaterial(WoodMatPath, new Color(0.42f, 0.29f, 0.17f), 0.1f);

            var dock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            dock.name = "Dock";
            dock.transform.SetParent(harbor.transform, false);
            dock.transform.position = new Vector3(0f, DockTopY - 0.25f, DockCenterZ);
            dock.transform.localScale = new Vector3(DockEdgeX * 2f, 0.5f, 16.9f);
            dock.GetComponent<MeshRenderer>().sharedMaterial = wood;

            // Checkpoint on the dock so respawns don't dump you somewhere else.
            var checkpoint = new GameObject("DockCheckpoint");
            checkpoint.transform.SetParent(harbor.transform, false);
            checkpoint.transform.position = new Vector3(0f, DockTopY + 1.1f, DockCenterZ - 3.45f);
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

        /// <summary>Sets the harbor's water to the moored ship's waterline —
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
            Debug.Log($"[ShipTestAreaBuilder] Waterline set to y={waterY:F2} for {spec.prefabName}.");
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

        // ---------------------------------------------------------------- jetties (anchor stations)

        private const string LanternPrefabPath = SyntyRoot + "/Props/SM_Prop_Lantern_01.prefab";
        private const string LanternGlowMatPath = "Assets/Art/Materials/Sea_LanternGlow.mat";

        // First compile after the mooring feature lands: put the jetties in.
        // ---------------------------------------------------------------- start island

        // The home island the dock belongs to. Coastline and height are analytic and
        // deterministic (no RNG), so the mesh, the dressing placements and any future
        // queries all agree about where the sand is.
        private const float IslandCenterZ = 38f;   // island centre, north of the dock
        private const float IslandBaseRadius = 24f;
        private const float IslandDockLobe = 11f;  // extra reach toward the dock, burying its north end
        private const float IslandSkirt = 12f;     // submerged sand ring outside the coastline
        private const float BeachSlope = 0.32f;    // rise per metre inland along the beach (~18°)
        private const float IslandPlateau = 3.2f;  // extra height of the grassy top above the beach crest

        // Coastline radius (m from the island centre) toward angle a (radians, 0 = north/+Z).
        // A tight lobe reaches south toward the dock; elsewhere two sine bands roughen the
        // coast. The noise fades out inside the lobe so the dock approach stays predictable.
        private static float IslandRadius(float a)
        {
            float lobe = Mathf.Pow(Mathf.Max(0f, Mathf.Cos(a - Mathf.PI)), 8f);
            float noise = 2.6f * Mathf.Sin(3f * a + 1.7f) + 1.7f * Mathf.Sin(7f * a + 0.4f);
            return IslandBaseRadius + lobe * IslandDockLobe + (1f - lobe) * noise;
        }

        // Sand height relative to the waterline, s metres inland of the coast (negative =
        // out to sea). A straight beach slope caps into a low crest; the interior rises to
        // a plateau. The same line continues underwater as the seabed skirt.
        private static float IslandHeight(float s)
        {
            float hill = IslandPlateau * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(8f, 22f, s));
            return Mathf.Min(s * BeachSlope, 1f + hill);
        }

        // Sand height (relative to the waterline) at a world-space (x, z) — the single
        // source both the mesh and the dressing sample. A dredged berth keeps the water
        // east of the dock DEEP: the warship draws 1.65 m and heaves nearly a metre more
        // in a swell, and its moored hull starts just 0.85 m off the dock face — so the
        // cut drops like a quay wall from the dock's east edge to 3.4 m below the
        // waterline, covering the whole hull footprint (z < 9). The beach ramp lives at
        // x <= 4 and is untouched.
        private static float IslandHeightWorld(float x, float z)
        {
            float dx = x, dz = z - IslandCenterZ;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            float h = IslandHeight(IslandRadius(Mathf.Atan2(dx, dz)) - dist);
            float channel = Mathf.Clamp01(Mathf.Min((x - 4f) * 1.4f, (9f - z) * 0.8f));
            return Mathf.Lerp(h, Mathf.Min(h, -3.4f), channel);
        }

        // Maintenance: raise the home island behind the dock; rebuilt whenever the layout
        // version (the mesh name) is stale, so berth-dredging fixes reach existing scenes.
        private static void EnsureStartIslandOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            if (harbor == null) return;
            Transform sand = harbor.transform.Find("StartIsland/Sand");
            var mf = sand != null ? sand.GetComponent<MeshFilter>() : null;
            if (mf != null && mf.sharedMesh != null && mf.sharedMesh.name == IslandMeshName) return;
            TearDownStartIsland(harbor);
            BuildStartIsland(harbor);
        }

        // Remove the island and everything derived from it (stairs, piles, old mesh asset).
        private static void TearDownStartIsland(GameObject harbor)
        {
            foreach (string name in new[] { "StartIsland", "DockShoreStairs", "DockPiles" })
            {
                Transform t = harbor.transform.Find(name);
                if (t != null) Object.DestroyImmediate(t.gameObject);
            }
            AssetDatabase.DeleteAsset(IslandMeshPath);
        }

        [MenuItem("Tools/Ship/Rebuild Start Island")]
        public static void RebuildStartIslandMenu()
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
            TearDownStartIsland(harbor);
            BuildStartIsland(harbor);
        }

        private static void BuildStartIsland(GameObject harbor)
        {
            Scene scene = SceneManager.GetActiveScene();
            Transform water = harbor.transform.Find("Water");
            float waterY = water != null ? water.position.y : 0.4f;

            var root = new GameObject("StartIsland");
            root.transform.SetParent(harbor.transform, false);
            // Mesh heights are relative to the waterline, so the island sits AT it.
            root.transform.position = new Vector3(0f, waterY, IslandCenterZ);

            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(IslandMeshPath);
            if (mesh == null || mesh.name != IslandMeshName) mesh = BuildIslandMesh();

            var sand = new GameObject("Sand");
            sand.transform.SetParent(root.transform, false);
            sand.AddComponent<MeshFilter>().sharedMesh = mesh;
            sand.AddComponent<MeshRenderer>().sharedMaterial = GetIslandTerrainMaterial(waterY);
            sand.AddComponent<MeshCollider>().sharedMesh = mesh; // static: non-convex is fine

            PlaceIslandFlora(root);
            if (harbor.transform.Find("DockShoreStairs") == null)
                BuildDockShoreStairs(harbor);
            if (harbor.transform.Find("DockPiles") == null)
                BuildDockPiles(harbor);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[ShipTestAreaBuilder] Start island raised behind the dock: sand mesh + " +
                      "collider, beach ramp over the dock's north end, palms and dressing.");
        }

        // Radial sand mound: rings out to the coastline-plus-skirt, heights from the shared
        // profile. Same disc topology as the sea grid, but the (sin, cos) parametrization
        // mirrors it, so the winding is reversed to keep normals up.
        private static Mesh BuildIslandMesh()
        {
            const int Rings = 40, Sectors = 112;
            var verts = new Vector3[1 + Rings * Sectors];
            verts[0] = new Vector3(0f, IslandHeightWorld(0f, IslandCenterZ), 0f);
            for (int k = 1; k <= Rings; k++)
                for (int s = 0; s < Sectors; s++)
                {
                    float a = s * Mathf.PI * 2f / Sectors;
                    float r = (IslandRadius(a) + IslandSkirt) * k / Rings;
                    float x = Mathf.Sin(a) * r, z = Mathf.Cos(a) * r;
                    verts[1 + (k - 1) * Sectors + s] = new Vector3(
                        x, IslandHeightWorld(x, IslandCenterZ + z), z);
                }
            var uvs = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                uvs[i] = new Vector2(verts[i].x, verts[i].z) * 0.08f;

            int Idx(int ring, int s) => 1 + (ring - 1) * Sectors + s % Sectors;
            var tris = new List<int>(Rings * Sectors * 6);
            for (int s = 0; s < Sectors; s++) // centre fan
            {
                tris.Add(0); tris.Add(Idx(1, s)); tris.Add(Idx(1, s + 1));
            }
            for (int k = 1; k < Rings; k++)
                for (int s = 0; s < Sectors; s++)
                {
                    int a = Idx(k, s), b = Idx(k, s + 1);
                    int c = Idx(k + 1, s), d = Idx(k + 1, s + 1);
                    tris.Add(a); tris.Add(c); tris.Add(d);
                    tris.Add(a); tris.Add(d); tris.Add(b);
                }

            var mesh = new Mesh
            {
                name = IslandMeshName,
                vertices = verts,
                uv = uvs,
                triangles = tris.ToArray(),
            };
            mesh.RecalculateNormals();
            Directory.CreateDirectory(Path.GetDirectoryName(IslandMeshPath));
            AssetDatabase.CreateAsset(mesh, IslandMeshPath);
            return mesh;
        }

        // Maintenance: stairs from the dock down to the island beach once. The dock's deck
        // rides ~4.7 m above the warship's low waterline, so the beach passes well below
        // it — without stairs the island is look-but-don't-touch.
        private static void EnsureDockShoreStairsOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            if (harbor == null || harbor.transform.Find("StartIsland") == null) return;
            if (harbor.transform.Find("DockShoreStairs") != null) return;
            BuildDockShoreStairs(harbor);

            Scene scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        // Maintenance: piles under the home dock once. The dock deck rides ~4.7 m above the
        // water on nothing at all; give it the same wooden posts the jetties stand on, plus
        // supports tucked under the shore stairs.
        private static void EnsureDockPilesOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            if (harbor == null || harbor.transform.Find("Dock") == null) return;
            if (harbor.transform.Find("DockPiles") != null) return;
            BuildDockPiles(harbor);

            Scene scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private static void BuildDockPiles(GameObject harbor)
        {
            Transform water = harbor.transform.Find("Water");
            float waterY = water != null ? water.position.y : 0f;
            Material wood = GetOrCreateMaterial(WoodMatPath, new Color(0.42f, 0.29f, 0.17f), 0.1f);

            var root = new GameObject("DockPiles");
            root.transform.SetParent(harbor.transform, false);

            // Rows of posts along both long edges of the slab, jetty-style: tucked under
            // the deck, running to below the waterline (or into the beach where the island
            // has risen to meet them).
            float postTop = DockTopY - 0.5f;
            float postBottom = waterY - 1.5f;
            foreach (float z in new[] { -7.8f, -3.9f, 0f, 3.9f, 7.8f })
                foreach (float x in new[] { -3.65f, 3.65f })
                    AddDockPost(root, wood, x, DockCenterZ + z, postTop, postBottom);

            // Stair supports: pairs whose tops follow the flight down. Geometry comes from
            // the built stairs so they track however many steps the beach needed.
            Transform stairs = harbor.transform.Find("DockShoreStairs");
            int count = stairs != null ? stairs.childCount : 0;
            const float rise = 0.25f, run = 0.45f;
            float zEdge = DockCenterZ + 8.45f;
            foreach (int i in new[] { count / 3, (2 * count) / 3 })
            {
                if (i < 1 || i > count) continue;
                float top = DockTopY - rise * i - 0.35f; // tucked under the tread
                float z = zEdge + (i - 0.5f) * run;
                AddDockPost(root, wood, -2.2f, z, top, postBottom);
                AddDockPost(root, wood, 2.2f, z, top, postBottom);
            }
            Debug.Log("[ShipTestAreaBuilder] Dock piles added under the slab and the shore stairs.");
        }

        private static void AddDockPost(GameObject root, Material wood,
            float x, float z, float top, float bottom)
        {
            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.name = "Post";
            post.transform.SetParent(root.transform, false);
            post.transform.localPosition = new Vector3(x, (top + bottom) * 0.5f, z);
            post.transform.localScale = new Vector3(0.35f, top - bottom, 0.35f);
            post.GetComponent<MeshRenderer>().sharedMaterial = wood;
        }

        /// <summary>Wooden stair flight from the dock's landward (north) edge down onto the
        /// island beach. Step count and landing derive from the island's analytic sand
        /// height, iterated so the bottom tread always sits within a step of the sand —
        /// whatever the waterline (and so the island) ended up at.</summary>
        private static void BuildDockShoreStairs(GameObject harbor)
        {
            Transform water = harbor.transform.Find("Water");
            float waterY = water != null ? water.position.y : 0f;
            Material wood = GetOrCreateMaterial(WoodMatPath, new Color(0.42f, 0.29f, 0.17f), 0.1f);

            const float rise = 0.25f, run = 0.45f, width = 5f;
            float zEdge = DockCenterZ + 8.45f; // dock's north face

            // The landing's sand height depends on how far the stairs reach; a few
            // fixed-point rounds settle both together.
            int count = 8;
            for (int i = 0; i < 4; i++)
            {
                float sand = waterY + IslandHeightWorld(0f, zEdge + count * run);
                count = Mathf.Max(1, Mathf.CeilToInt((DockTopY - 0.05f - sand) / rise));
            }

            var root = new GameObject("DockShoreStairs");
            root.transform.SetParent(harbor.transform, false);
            for (int i = 1; i <= count; i++)
            {
                var step = GameObject.CreatePrimitive(PrimitiveType.Cube);
                step.name = $"Step{i}";
                step.transform.SetParent(root.transform, false);
                float top = DockTopY - rise * i;
                step.transform.localPosition = new Vector3(
                    0f, top - (rise + 0.05f) * 0.5f, zEdge + (i - 0.5f) * run);
                step.transform.localScale = new Vector3(width, rise + 0.05f, run + 0.06f);
                step.GetComponent<MeshRenderer>().sharedMaterial = wood;
            }
            Debug.Log($"[ShipTestAreaBuilder] Dock shore stairs: {count} steps down to the " +
                      "island beach from the dock's north edge.");
        }

        // Deterministic dressing: (prefab, degrees from north, metres inland of the coast,
        // yaw). Placements stay off the southern dock lobe so the approach and the beach
        // ramp stay clear; heights come from the shared profile, sunk slightly so nothing
        // floats on the slope.
        private static void PlaceIslandFlora(GameObject root)
        {
            (string name, float a, float s, float yaw)[] flora =
            {
                ("SM_Env_PalmTree_01",       30f,  7f,  40f),
                ("SM_Env_PalmTree_03",       75f,  9f, 160f),
                ("SM_Env_PalmTree_Tall_01", 118f,  8f, 300f),
                ("SM_Env_PalmTree_02",      252f,  7f,  10f),
                ("SM_Env_PalmTree_Tall_02", 300f,  9f, 220f),
                ("SM_Env_PalmBush_03",       60f, 11f,   0f),
                ("SM_Env_Bush_01",          272f, 12f,  90f),
                ("SM_Env_GrassPatch_01",     20f, 18f,   0f),
                ("SM_Env_GrassPatch_02",    100f, 20f,  70f),
                ("SM_Env_GrassPatch_03",    205f, 19f, 140f),
                ("SM_Env_GrassPatch_01",    330f, 17f, 200f),
                ("SM_Env_Rocks_01",          92f,  3f,   0f),
                ("SM_Env_Rocks_02",         228f,  2f,  45f),
                ("SM_Env_Beach_Pile_01",    140f,  4f,   0f),
                ("SM_Env_Rock_Skull_01",      0f, 15f, 195f),
            };

            var parent = new GameObject("Flora");
            parent.transform.SetParent(root.transform, false);
            foreach ((string name, float aDeg, float s, float yaw) in flora)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    $"{SyntyRoot}/Environments/{name}.prefab");
                if (prefab == null)
                {
                    Debug.LogWarning($"[ShipTestAreaBuilder] Island dressing missing: {name}");
                    continue;
                }
                float a = aDeg * Mathf.Deg2Rad;
                float r = IslandRadius(a) - s;
                float x = Mathf.Sin(a) * r, z = Mathf.Cos(a) * r;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.transform.SetParent(parent.transform, false);
                go.transform.localPosition = new Vector3(
                    x, IslandHeightWorld(x, IslandCenterZ + z) - 0.12f, z);
                go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            }
        }

        // Maintenance: hang the day/night cycle on the scene's sun once.
        private static void EnsureDayNightOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            Light sun = null;
            foreach (Light l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (l.type == LightType.Directional) { sun = l; break; }
            if (sun == null || sun.GetComponent<DayNightCycle>() != null) return;

            sun.gameObject.AddComponent<DayNightCycle>();
            Scene scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[ShipTestAreaBuilder] Day/night cycle added to the directional light " +
                      "(10-minute days, network-synced clock).");
        }

        private const string SkyMatPath = "Assets/Art/Materials/Sky_Stylized.mat";

        // Maintenance: swap the built-in procedural skybox for the stylized day/night sky
        // once (day gradient + sun by day, starfield + moon by night; DayNightCycle
        // drives the blend at runtime).
        private static void EnsureSkyOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            Shader shader = Shader.Find("Sea/StylizedSky");
            if (shader == null) return; // not imported yet; a later pass gets it

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(SkyMatPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, SkyMatPath);
            }
            else if (mat.shader != shader)
            {
                mat.shader = shader;
                EditorUtility.SetDirty(mat);
            }
            if (RenderSettings.skybox == mat) return;

            RenderSettings.skybox = mat;
            Scene scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[ShipTestAreaBuilder] Skybox swapped to the stylized day/night sky " +
                      "(stars and a moon after dark).");
        }

        // Maintenance: keep the saved scene's ship at its berth. A physics mishap during
        // play (a grounding — like the pre-dredge berth bug) can leave a drifted pose
        // saved into the scene; the berth is the authored start state, so put it back.
        // Derives the mooring from the prefab's own colliders, exactly like a ship swap.
        private static void EnsureShipMoored()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            var ship = harbor != null ? harbor.GetComponentInChildren<ShipController>(true) : null;
            if (ship == null) return;
            GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(ship.gameObject);
            if (source == null) return;

            BoxCollider[] boxes = source.GetComponentsInChildren<BoxCollider>(true);
            var decks = boxes.Where(b => b.name == "Deck" && !b.isTrigger).ToList();
            BoxCollider hull = boxes.FirstOrDefault(b => b.name == "Hull" && !b.isTrigger);
            if (decks.Count == 0 || hull == null) return;

            Vector3 RootLocal(BoxCollider b) =>
                source.transform.InverseTransformPoint(b.transform.TransformPoint(b.center));
            float deckLow = decks.Min(b => RootLocal(b).y + b.size.y * 0.5f);
            float zMin = decks.Min(b => RootLocal(b).z - b.size.z * 0.5f);
            float zMax = decks.Max(b => RootLocal(b).z + b.size.z * 0.5f);
            float beamHalf = hull.size.x * 0.5f / 0.95f;
            var moor = new Vector3(
                DockEdgeX + beamHalf + 0.7f,
                DockTopY - deckLow,
                DockCenterZ + 4.55f - (zMax - zMin) * 0.5f);

            if (Vector3.Distance(ship.transform.position, moor) < 1.5f) return;
            Vector3 was = ship.transform.position;
            ship.transform.SetPositionAndRotation(moor, Quaternion.Euler(0f, 180f, 0f));
            Scene scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[ShipTestAreaBuilder] Ship re-moored: drifted pose {was} put back at {moor}.");
        }

        // ---------------------------------------------------------------- archipelago

        /// <summary>
        /// A destination island: analytic coast + height like the start island, but fully
        /// parameterized — and optionally cut by a ship-navigable river, a quadratic-bezier
        /// channel carved below the waterline from coast to coast. Everything derives from
        /// the spec, so the mesh, the collider, the flora filter and any future gameplay
        /// queries all agree about where land, beach and river are.
        /// </summary>
        private struct RiverSpec
        {
            public float entryDeg, exitDeg; // coast bearings (deg from +Z/north)
            public float halfWidth;         // nominal; the run narrows/widens around this
            public float bend;              // sideways bow of the channel's midpoint (m)

            public RiverSpec(float entry, float exit, float halfWidth, float bend)
            {
                entryDeg = entry; exitDeg = exit; this.halfWidth = halfWidth; this.bend = bend;
            }
        }

        private struct PeakSpec
        {
            public float bearingDeg;  // direction from the island centre
            public float distFrac;    // how far out along the radius (0 = centre)
            public float height;      // summit height above the interior (m)
            public float footRadius;  // gaussian falloff radius (m)

            public PeakSpec(float bearingDeg, float distFrac, float height, float footRadius)
            {
                this.bearingDeg = bearingDeg; this.distFrac = distFrac;
                this.height = height; this.footRadius = footRadius;
            }
        }

        private class IslandSpec
        {
            public string name;
            public Vector2 center;              // world XZ
            public float radius;                // base coast radius (m)
            public float plateau = 4.5f;        // interior rise above the beach crest (m)
            public float hillNoise = 2.5f;      // rolling-terrain noise amplitude inland (m)
            public float phaseA, phaseB;        // noise phases: each island's own character
            public PeakSpec[] peaks = { };      // mountains rising off the interior
            public RiverSpec[] rivers = { };

            // Submerged sand ring outside the coast; grows with the island.
            public float Skirt => Mathf.Max(14f, radius * 0.08f);

            // Coast noise scales with the island: a 260 m island gets ~30 m bays and
            // headlands, so circumnavigating it is a coastline, not a circle.
            public float CoastRadius(float a) =>
                radius
                + radius * 0.10f * Mathf.Sin(3f * a + phaseA)
                + radius * 0.05f * Mathf.Sin(7f * a + phaseB)
                + radius * 0.025f * Mathf.Sin(13f * a + phaseA * 1.7f);
        }

        // The fleet of destinations: three big islands (minutes to sail around) and three
        // small waypoints. Every river entry faces the home harbor's side of the island so
        // a run can line the mouth up from open water; exits punch out the far coast, so a
        // river is a genuine shortcut THROUGH the island, not a dead end — Grande's two
        // rivers cross mid-island in a navigable junction. Positions keep clear of the
        // rock slalom (x -11..13, z -60..-118), JettyEast (48,-85), JettyIsland
        // (-40,-130) and the background-island visual at (-30,-145).
        private static readonly IslandSpec[] Archipelago =
        {
            // Small waypoints on the near sea.
            new IslandSpec { name = "Riverrun", center = new Vector2(150f, -70f), radius = 38f,
                plateau = 4.5f, hillNoise = 1.5f, phaseA = 1.3f, phaseB = 4.0f,
                peaks = new[] { new PeakSpec(150f, 0.3f, 9f, 26f) },
                rivers = new[] { new RiverSpec(-65f, 115f, 9f, 8f) } },
            new IslandSpec { name = "Serpent", center = new Vector2(-160f, -100f), radius = 42f,
                plateau = 5.5f, hillNoise = 1.5f, phaseA = 2.6f, phaseB = 0.9f,
                peaks = new[] { new PeakSpec(-40f, 0.35f, 12f, 30f) },
                rivers = new[] { new RiverSpec(58f, -130f, 9f, 14f) } },
            new IslandSpec { name = "BareKnuckle", center = new Vector2(-90f, -220f), radius = 26f,
                plateau = 3f, hillNoise = 1.2f, phaseA = 5.3f, phaseB = 3.5f,
                peaks = new[] { new PeakSpec(10f, 0.2f, 8f, 18f) } },

            // The big three, out on open water — real relief: ridgelines and summits.
            new IslandSpec { name = "Grande", center = new Vector2(640f, -420f), radius = 260f,
                plateau = 11f, hillNoise = 4f, phaseA = 0.7f, phaseB = 3.1f,
                peaks = new[] { new PeakSpec(95f, 0.32f, 38f, 62f),
                                new PeakSpec(205f, 0.45f, 30f, 72f),
                                new PeakSpec(330f, 0.5f, 22f, 52f) },
                rivers = new[] { new RiverSpec(-60f, 140f, 12f, 35f),
                                 new RiverSpec(30f, -155f, 10f, -30f) } },
            new IslandSpec { name = "Westwatch", center = new Vector2(-600f, -350f), radius = 200f,
                plateau = 9f, hillNoise = 3.5f, phaseA = 4.4f, phaseB = 1.8f,
                peaks = new[] { new PeakSpec(-95f, 0.4f, 27f, 58f),
                                new PeakSpec(120f, 0.42f, 20f, 48f) },
                rivers = new[] { new RiverSpec(40f, -150f, 13f, 55f) } },
            new IslandSpec { name = "Longreach", center = new Vector2(80f, -700f), radius = 160f,
                plateau = 8f, hillNoise = 3f, phaseA = 2.0f, phaseB = 5.1f,
                peaks = new[] { new PeakSpec(95f, 0.35f, 23f, 50f),
                                new PeakSpec(262f, 0.4f, 18f, 44f) },
                rivers = new[] { new RiverSpec(-8f, 175f, 12f, 30f) } },
        };

        // Deterministic value noise (same construction as WaterSurface's) for terrain and
        // scatter — no RNG, so every rebuild and every machine agrees.
        private static float Hash01(float x, float y)
        {
            float h = Mathf.Sin(x * 127.1f + y * 311.7f) * 43758.5453f;
            return h - Mathf.Floor(h);
        }

        private static float VNoise2(float x, float y)
        {
            float ix = Mathf.Floor(x), iy = Mathf.Floor(y);
            float fx = x - ix, fy = y - iy;
            float ux = fx * fx * (3f - 2f * fx), uy = fy * fy * (3f - 2f * fy);
            return Mathf.Lerp(
                Mathf.Lerp(Hash01(ix, iy), Hash01(ix + 1f, iy), ux),
                Mathf.Lerp(Hash01(ix, iy + 1f), Hash01(ix + 1f, iy + 1f), ux), uy);
        }

        private static Vector2 Bearing(float deg) =>
            new Vector2(Mathf.Sin(deg * Mathf.Deg2Rad), Mathf.Cos(deg * Mathf.Deg2Rad));

        // How a river shapes the ground at (x, z): returns the signed distance OUTSIDE the
        // local channel edge (negative = in the water) and the local bed depth. The
        // centreline is a quadratic bezier from coast to coast with a meander wave
        // superimposed (fading at the ends so the mouths stay aimed); the width breathes
        // along the run — narrows in the reaches, flares into estuaries at both mouths —
        // and the bed undulates between pools and shallower bars. All deterministic from
        // the spec's phases; sampled as a polyline, editor-time only.
        private static void RiverCarveAt(IslandSpec spec, in RiverSpec river, float x, float z,
            out float edgeDist, out float bed)
        {
            float reach = spec.radius * 1.2f + spec.Skirt + 4f;
            Vector2 p0 = spec.center + Bearing(river.entryDeg) * reach;
            Vector2 p2 = spec.center + Bearing(river.exitDeg) * reach;
            Vector2 chordMid = (p0 + p2) * 0.5f;
            Vector2 chordDir = (p2 - p0).normalized;
            Vector2 chordPerp = new Vector2(-chordDir.y, chordDir.x);
            Vector2 p1 = Vector2.Lerp(chordMid, spec.center, 0.6f) + chordPerp * river.bend;
            float meanderAmp = Mathf.Clamp(spec.radius * 0.06f, 3f, 16f);

            var p = new Vector2(x, z);
            float best = float.MaxValue, bestT = 0f;
            Vector2 prev = Vector2.zero;
            const int Steps = 48;
            for (int i = 0; i <= Steps; i++)
            {
                float t = i / (float)Steps;
                Vector2 q = (1f - t) * (1f - t) * p0 + 2f * (1f - t) * t * p1 + t * t * p2;
                Vector2 tan = (2f * (1f - t) * (p1 - p0) + 2f * t * (p2 - p1)).normalized;
                // Meander: two sine waves swinging the channel side to side, windowed by
                // sin(pi t) so the mouths themselves never wander off their bearings.
                float wiggle = Mathf.Sin(t * 14.5f + spec.phaseB * 3f)
                             + 0.5f * Mathf.Sin(t * 27f + spec.phaseA * 2f);
                q += new Vector2(-tan.y, tan.x) * (wiggle * meanderAmp * Mathf.Sin(t * Mathf.PI));

                if (i > 0)
                {
                    float d = DistPointSegment(p, prev, q);
                    if (d < best) { best = d; bestT = t - 0.5f / Steps; }
                }
                prev = q;
            }

            // Local width: breathes ±30% along the run, flaring wide into an estuary over
            // the last stretch to each mouth.
            float flare = 1f + 1.4f * (Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.16f, 0f, bestT))
                                     + Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.84f, 1f, bestT)));
            float width = river.halfWidth * flare
                        * (1f + 0.3f * Mathf.Sin(bestT * 11.7f + spec.phaseA * 4f));
            edgeDist = best - width;

            // Bed depth: pools and bars, never shallower than the ship + heave needs.
            bed = -3.4f + 0.5f * Mathf.Sin(bestT * 8.3f + spec.phaseB * 2f);
        }

        private static float DistPointSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            return Vector2.Distance(p, a + ab * t);
        }

        // Sand height relative to the waterline at world (x, z) for a spec island. Beach
        // slope into a radius-scaled interior climb, then real relief on top: rolling
        // value-noise hills and gaussian mountain peaks, both masked to the interior so
        // the coast stays beach. Rivers carve LAST, so they cut through whatever relief
        // is in the way — a river crossing a mountain's foot becomes a gorge, and the
        // terrain shader paints the steep cut walls as rock by itself.
        private static float SpecHeight(IslandSpec spec, float x, float z)
        {
            float dx = x - spec.center.x, dz = z - spec.center.y;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            float s = spec.CoastRadius(Mathf.Atan2(dx, dz)) - dist;
            float hillEnd = Mathf.Max(26f, spec.radius * 0.45f);
            float hill = spec.plateau * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(8f, hillEnd, s));
            float h = Mathf.Min(s * BeachSlope, 1f + hill);

            // Interior mask: relief fades in past the beach so the waterline stays sand.
            float inland = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(6f, 18f, s));
            if (inland > 0f)
            {
                float rolling = VNoise2(x * 0.033f + spec.phaseA, z * 0.033f + spec.phaseB)
                              + 0.5f * VNoise2(x * 0.09f + spec.phaseB, z * 0.09f + spec.phaseA);
                h += inland * spec.hillNoise * (rolling * 1.333f - 1f);

                foreach (PeakSpec peak in spec.peaks)
                {
                    Vector2 summit = spec.center + Bearing(peak.bearingDeg) * (spec.radius * peak.distFrac);
                    float dp = Vector2.Distance(new Vector2(x, z), summit) / peak.footRadius;
                    h += inland * peak.height * Mathf.Exp(-dp * dp);
                }
            }

            // Interior floor: the relief must never dig below the sea's reach — an inland
            // bowl under wave-crest height fills with animated ocean poking up through
            // the ground. The beach ramp itself is spared (the sea DOES lap the shore).
            if (s > 0f) h = Mathf.Max(h, Mathf.Min(s * BeachSlope, 1.25f));

            foreach (RiverSpec river in spec.rivers)
            {
                RiverCarveAt(spec, river, x, z, out float edgeDist, out float bed);
                float carve = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(14f, 0f, edgeDist));
                h = Mathf.Lerp(h, Mathf.Min(h, bed), carve);
            }
            return h;
        }

        // Bump to rebuild every scene's archipelago on the next maintenance pass (layout
        // redesigns, generator changes). Island mesh assets are wiped and regenerated.
        private const int ArchipelagoVersion = 6;

        private const string IslandMatPath = "Assets/Art/Materials/Island_Terrain.mat";

        // The height/slope-painted terrain material (sand -> grass -> rock). Falls back to
        // the plain sand look until the Island/Terrain shader has imported.
        private static Material GetIslandTerrainMaterial(float waterY)
        {
            Material mat = GetOrCreateMaterial(IslandMatPath, new Color(0.87f, 0.78f, 0.55f), 0.05f);
            Shader shader = Shader.Find("Island/Terrain");
            if (shader != null
                && (mat.shader != shader || !Mathf.Approximately(mat.GetFloat("_WaterY"), waterY)))
            {
                mat.shader = shader;
                mat.SetFloat("_WaterY", waterY);
                EditorUtility.SetDirty(mat);
            }
            return mat;
        }

        // A hand-authored story scene: prefab (path under the Synty root), world-axis
        // offsets from the vignette anchor, yaw, sink into the sand, scale, roll.
        // Every item samples the terrain height at its own spot, so scenes drape over
        // the ground instead of floating on a plane.
        private struct DecoItem
        {
            public string path;
            public float dx, dz, yaw, sink, scale, roll;

            public DecoItem(string path, float dx, float dz, float yaw,
                float sink = 0.08f, float scale = 1f, float roll = 0f)
            {
                this.path = path; this.dx = dx; this.dz = dz;
                this.yaw = yaw; this.sink = sink; this.scale = scale; this.roll = roll;
            }
        }

        private class Vignette
        {
            public string island;
            public float bearingDeg, inland; // anchor: coast bearing + metres inland
            public DecoItem[] items;
            // Optional night beacon: a lantern post that auto-lights after dark, so the
            // scene reads as a point of interest from open water.
            public bool lantern;
            public float lanternDx, lanternDz;
        }

        // One story per island, anchored off the analytic coast (verified against the
        // height field: flat enough, clear of the rivers). These are what make each
        // landfall memorable — the scatter is texture, these are destinations.
        private static readonly Vignette[] Vignettes =
        {
            // A trader that didn't make it: bare hull half-sunk on Grande's north shore.
            new Vignette { island = "Grande", bearingDeg = 355f, inland = -1f, items = new[]
            {
                new DecoItem("Vehicles/SM_Veh_Boat_Medium_01_Hull", 0f, 0f, 150f, 1.1f, 1f, 12f),
                new DecoItem("Props/SM_Prop_Debris_01", 6f, 3f, 40f),
                new DecoItem("Props/SM_Prop_Debris_02", -5f, 2f, 210f),
                new DecoItem("Props/SM_Prop_Barrel_02", 4.5f, -2f, 0f, 0.12f),
                new DecoItem("Props/SM_Prop_Crate_02", -4f, -1.5f, 25f, 0.12f),
            } },
            // Smugglers' village on the flat southern shelf: two shanties and a tent
            // around the old camp. Complete preset buildings — no assembly.
            new Vignette { island = "Grande", bearingDeg = 166f, inland = 20f,
                lantern = true, lanternDx = 2.8f, lanternDz = 2.5f, items = new[]
            {
                new DecoItem("Buildings/SM_Bld_Cuba_Hall_Tower_01", -9f, 8f, 236f, 0.35f),
                new DecoItem("Buildings/SM_Bld_Shanty_Preset_02", 6f, 4f, 236f, 0.3f),
                new DecoItem("Buildings/SM_Bld_Shanty_Preset_05", -6f, 5f, 130f, 0.3f),
                new DecoItem("Buildings/SM_Bld_Tent_02", 4f, -5f, 300f, 0.15f),
                new DecoItem("Props/SM_Prop_Campfire_Pot_01", 0f, 0f, 0f),
                new DecoItem("Props/SM_Prop_Bench_01", 0f, -2.2f, 0f),
                new DecoItem("Props/SM_Prop_Barrel_01", 2f, 0.7f, 0f),
                new DecoItem("Props/SM_Prop_Barrel_03", 2.5f, -0.5f, 30f),
                new DecoItem("Props/SM_Prop_Crate_01", -2.1f, 0.9f, 15f),
                new DecoItem("Props/SM_Prop_Crate_04", -2.5f, -0.7f, 70f),
                new DecoItem("Props/SM_Prop_Chest_01", 0.5f, 2.4f, 205f),
                new DecoItem("Props/SM_Prop_BottleTorch_01", 3.2f, -2.2f, 0f),
                new DecoItem("Props/SM_Prop_BottleTorch_01", -3.2f, -2.4f, 0f),
            } },
            // Westwatch earns its name: a gun battery on the summit plateau, aimed to sea.
            new Vignette { island = "Westwatch", bearingDeg = -95f, inland = 115f,
                lantern = true, lanternDx = 2.5f, lanternDz = -2f, items = new[]
            {
                new DecoItem("Buildings/SM_Bld_Fort_Tower_01", -4f, -4f, -95f, 0.25f),
                new DecoItem("Props/SM_Prop_Cannon_03", 0f, 0f, -95f),
                new DecoItem("Props/SM_Prop_CannonBalls_01", 1.6f, -1f, 0f),
                new DecoItem("Props/SM_Prop_Crate_06", -2f, -1.2f, 30f),
                new DecoItem("Props/SM_Prop_Barrel_05", -2.6f, 0.6f, 0f),
                new DecoItem("Props/SM_Prop_Campfire_01", 1.5f, 2.3f, 0f),
                new DecoItem("Props/SM_Prop_BottleTorch_01", 3f, 1f, 0f),
            } },
            // A castaway's beached rowboat on Longreach.
            new Vignette { island = "Longreach", bearingDeg = -60f, inland = 5f, items = new[]
            {
                new DecoItem("Buildings/SM_Bld_Tent_04", 2f, 4f, 200f, 0.15f),
                new DecoItem("Vehicles/SM_Veh_Boat_Rowing_01_Hull_Attachments", 0f, 0f, -80f, 0.45f, 1f, 8f),
                new DecoItem("Props/SM_Prop_Debris_02", 3.4f, 1.2f, 120f),
                new DecoItem("Props/SM_Prop_Barrel_Half_01", 2.6f, -1.6f, 0f),
                new DecoItem("Props/SM_Prop_Crate_03", -2.8f, 1f, 45f),
                new DecoItem("Props/SM_Prop_Campfire_01", -1f, 3f, 0f),
            } },
            // Something unwelcoming flanks Serpent's river mouth.
            new Vignette { island = "Serpent", bearingDeg = 30f, inland = 4f, items = new[]
            {
                new DecoItem("Buildings/SM_Bld_Rickety_Tower_03", -6f, 9f, 150f, 0.3f),
                new DecoItem("Props/SM_Prop_BottleTorch_01", 0f, 0f, 0f),
                new DecoItem("Props/SM_Prop_BottleTorch_01", 2.5f, 1.5f, 0f),
                new DecoItem("Props/SM_Prop_Cage_01", -2f, 1f, 210f, 0.15f),
                new DecoItem("Props/SM_Prop_Grave_03", -3.5f, 2.5f, 160f),
            } },
            // The remains of a trading stop on Riverrun.
            new Vignette { island = "Riverrun", bearingDeg = -130f, inland = 7f,
                lantern = true, lanternDx = -1.5f, lanternDz = 4f, items = new[]
            {
                new DecoItem("Buildings/SM_Bld_Shop_01", -4f, 2f, 117f, 0.25f),
                new DecoItem("Props/SM_Prop_Crane_01", 3f, 5f, -60f, 0.15f),
                new DecoItem("Props/SM_Prop_Cart_01", 0f, 0f, -40f),
                new DecoItem("Props/SM_Prop_Crate_05", 2.2f, 0.8f, 10f),
                new DecoItem("Props/SM_Prop_Crate_02", 2.6f, -0.9f, 55f),
                new DecoItem("Props/SM_Prop_Barrel_02", -2f, 1f, 0f),
                new DecoItem("Props/SM_Prop_Barrel_Half_01", -2.4f, -0.8f, 0f),
                new DecoItem("Props/SM_Prop_Rope_Fence_01", 1f, 3f, 0f),
                new DecoItem("Props/SM_Prop_Rope_Fence_01", 3.5f, 3f, 0f),
            } },
            // The distance layer: silhouettes that read from open water and say
            // "sail here". Verified spots — flat summits and shallows.
            // Skull Rock crowns BareKnuckle's peak: THE pirate landmark.
            new Vignette { island = "BareKnuckle", bearingDeg = 10f, inland = 21f, items = new[]
            {
                new DecoItem("Environments/SM_Env_Rock_Skull_01", 0f, 0f, 205f, 0.45f, 1.6f),
            } },
            // A natural sea arch stands off Grande's north-west shallows.
            new Vignette { island = "Grande", bearingDeg = 320f, inland = -10f, items = new[]
            {
                new DecoItem("Environments/SM_Env_Rock_Arch_01", 0f, 0f, 40f, 1.2f, 1.3f),
            } },
            // A ruined mansion tower on Longreach's eastern summit — old money, old loot.
            new Vignette { island = "Longreach", bearingDeg = 95f, inland = 105f, items = new[]
            {
                new DecoItem("Buildings/SM_Bld_Mansion_Tower_01", 0f, 0f, -95f, 0.35f),
                new DecoItem("Buildings/SM_Bld_Stone_Wall_01", 3f, 1.5f, 40f, 0.3f),
                new DecoItem("Buildings/SM_Bld_Stone_Wall_End_01", -2.8f, 2f, 290f, 0.3f),
                new DecoItem("Props/SM_Prop_TreasurePile_02", 1.2f, -2f, 0f, 0.1f),
            } },

            // BareKnuckle: a hillside graveyard and someone's unburied hoard.
            new Vignette { island = "BareKnuckle", bearingDeg = 100f, inland = 9f, items = new[]
            {
                new DecoItem("Buildings/SM_Bld_Rickety_House_02", 5f, -4f, 280f, 0.3f),
                new DecoItem("Props/SM_Prop_Grave_01", 0f, 0f, 100f),
                new DecoItem("Props/SM_Prop_Grave_02", 1.8f, 0.6f, 80f),
                new DecoItem("Props/SM_Prop_Grave_04", -1.7f, 0.5f, 115f),
                new DecoItem("Props/SM_Prop_Grave_05", 3.4f, 1.4f, 95f),
                new DecoItem("Props/SM_Prop_Grave_03", -3.2f, 1.2f, 120f),
                new DecoItem("Environments/SM_Env_Tree_Dead_01", 0.8f, 3.2f, 0f),
                new DecoItem("Props/SM_Prop_TreasurePile_01", -0.6f, -2.2f, 0f, 0.12f),
            } },
        };

        private static void PlaceVignettes(IslandSpec spec, GameObject island)
        {
            foreach (Vignette v in Vignettes)
            {
                if (v.island != spec.name) continue;
                float a = v.bearingDeg * Mathf.Deg2Rad;
                float r = Mathf.Max(0f, spec.CoastRadius(a) - v.inland);
                float ax = Mathf.Sin(a) * r, az = Mathf.Cos(a) * r;

                var parent = new GameObject($"Vignette_{v.bearingDeg:0}");
                parent.transform.SetParent(island.transform, false);
                foreach (DecoItem item in v.items)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                        $"{SyntyRoot}/{item.path}.prefab");
                    if (prefab == null)
                    {
                        Debug.LogWarning($"[ShipTestAreaBuilder] Vignette prop missing: {item.path}");
                        continue;
                    }
                    float x = ax + item.dx, z = az + item.dz;
                    float h = SpecHeight(spec, spec.center.x + x, spec.center.y + z);
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    go.transform.SetParent(parent.transform, false);
                    go.transform.localPosition = new Vector3(x, h, z);
                    go.transform.localRotation = Quaternion.Euler(0f, item.yaw, item.roll);
                    go.transform.localScale = Vector3.one * item.scale;
                    // sink = how deep the prop's lowest point embeds below the sand.
                    SnapToGround(go, item.sink);
                }
                if (v.lantern)
                    AddVignetteLantern(spec, parent, ax + v.lanternDx, az + v.lanternDz);
            }
        }

        // A ground-standing lantern post whose lamp auto-lights at night (DockLantern in
        // Auto mode) — the after-dark "come look over here" that carries across water.
        private static void AddVignetteLantern(IslandSpec spec, GameObject parent, float x, float z)
        {
            float ground = SpecHeight(spec, spec.center.x + x, spec.center.y + z);
            Material wood = GetOrCreateMaterial(WoodMatPath, new Color(0.42f, 0.29f, 0.17f), 0.1f);

            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.name = "LanternPost";
            post.transform.SetParent(parent.transform, false);
            post.transform.localPosition = new Vector3(x, ground + 0.85f, z);
            post.transform.localScale = new Vector3(0.22f, 1.9f, 0.22f);
            post.GetComponent<MeshRenderer>().sharedMaterial = wood;

            Vector3 lampPos = new Vector3(x, ground + 1.85f, z);
            var lanternPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LanternPrefabPath);
            if (lanternPrefab != null)
            {
                var lantern = (GameObject)PrefabUtility.InstantiatePrefab(lanternPrefab);
                lantern.name = "Lantern";
                lantern.transform.SetParent(parent.transform, false);
                lantern.transform.localPosition = lampPos;
            }

            var lightGo = new GameObject("LanternLight");
            lightGo.transform.SetParent(parent.transform, false);
            lightGo.transform.localPosition = lampPos + new Vector3(0f, 0.25f, 0f);
            var lamp = lightGo.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.color = new Color(1f, 0.72f, 0.42f);
            lamp.intensity = 3f;
            lamp.range = 22f;
            lamp.shadows = LightShadows.None;

            var glow = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            glow.name = "LanternGlow";
            Object.DestroyImmediate(glow.GetComponent<Collider>());
            glow.transform.SetParent(parent.transform, false);
            glow.transform.localPosition = lampPos + new Vector3(0f, 0.22f, 0f);
            glow.transform.localScale = Vector3.one * 0.16f;
            var glowRenderer = glow.GetComponent<MeshRenderer>();
            glowRenderer.sharedMaterial = GetOrCreateEmissiveMaterial(
                LanternGlowMatPath, new Color(1f, 0.72f, 0.42f));

            var control = lightGo.AddComponent<DockLantern>();
            control.SetRefs(lamp, glowRenderer);
        }

        // Maintenance: raise the destination archipelago once per layout version.
        private static void EnsureArchipelagoOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            if (harbor == null) return;
            Transform existing = harbor.transform.Find("Archipelago");
            if (existing != null && existing.Find($"ArchTag_v{ArchipelagoVersion}") != null) return;
            RebuildArchipelago(harbor);
        }

        [MenuItem("Tools/Ship/Rebuild Archipelago")]
        public static void RebuildArchipelagoMenu()
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
            RebuildArchipelago(harbor);
        }

        // Tear down whatever archipelago is in the scene (any version) plus its generated
        // mesh assets, and build the current layout fresh.
        private static void RebuildArchipelago(GameObject harbor)
        {
            Transform old = harbor.transform.Find("Archipelago");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            foreach (string guid in AssetDatabase.FindAssets("t:Mesh", new[] { "Assets/Art/Models" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path).StartsWith("Island_"))
                    AssetDatabase.DeleteAsset(path);
            }
            BuildArchipelago(harbor);
        }

        private const string SharkPrefabPath = SyntyRoot + "/Characters/SM_Shark_01.prefab";
        private const int SharksVersion = 3; // bump when circuits/counts change

        private static void EnsureSharksOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            if (harbor == null) return;
            Transform existing = harbor.transform.Find("Sharks");
            if (existing != null && existing.Find($"SharkTag_v{SharksVersion}") != null) return;
            RebuildSharks(harbor);
        }

        [MenuItem("Tools/Ship/Rebuild Sharks")]
        public static void RebuildSharksMenu()
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
            RebuildSharks(harbor);
        }

        // Ambient sharks on fixed open-water circuits (SharkView drives them off the synced
        // wave clock at runtime, so peers agree without networking). Circuits are chosen so
        // circle + breathing radius stays clear of every island's coast and the dock.
        private static void RebuildSharks(GameObject harbor)
        {
            Transform old = harbor.transform.Find("Sharks");
            if (old != null) Object.DestroyImmediate(old.gameObject);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SharkPrefabPath);
            if (prefab == null)
            {
                Debug.LogWarning($"[ShipTestAreaBuilder] Shark prefab missing at {SharkPrefabPath}; no sharks built.");
                return;
            }

            var root = new GameObject("Sharks");
            root.transform.SetParent(harbor.transform, false);

            Transform water = harbor.transform.Find("Water");
            float waterY = water != null ? water.position.y : 0f;

            (Vector2 c, float r, int n)[] zones =
            {
                (new Vector2(35f, -95f), 18f, 2),    // harbor mouth, past the rock slalom
                (new Vector2(-60f, -140f), 20f, 1),  // Serpent-BareKnuckle gap
                (new Vector2(140f, -160f), 22f, 1),  // south of Riverrun
                (new Vector2(0f, -320f), 30f, 2),    // open sea, mid-map
                (new Vector2(310f, -360f), 22f, 1),  // off Grande's west coast
                (new Vector2(-330f, -300f), 22f, 1), // off Westwatch's east coast
                (new Vector2(40f, -490f), 25f, 1),   // north of Longreach
            };

            int k = 0;
            foreach ((Vector2 c, float r, int n) in zones)
                for (int i = 0; i < n; i++, k++)
                {
                    var shark = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    shark.name = $"Shark_{k:00}";
                    shark.transform.SetParent(root.transform, false);
                    // Parked roughly on its circuit for the scene view; SharkView owns it in play.
                    shark.transform.position = new Vector3(c.x + r, waterY - 0.75f, c.y);

                    // Networked scene object (like the moored ship): the server owns shark
                    // movement so chases are authoritative, and the NetworkTransform
                    // replicates it. Mirror does NOT auto-add the identity — add it first.
                    shark.AddComponent<Mirror.NetworkIdentity>();
                    var view = shark.AddComponent<SharkView>();
                    shark.AddComponent<Mirror.NetworkTransformReliable>();
                    var so = new SerializedObject(view);
                    so.FindProperty("center").vector2Value = c;
                    so.FindProperty("radius").floatValue = r * (1f - 0.18f * i); // ring-in cohabitants
                    so.FindProperty("speed").floatValue = 1.9f + 0.5f * Hash01(k * 3.7f, 1.1f);
                    so.FindProperty("phase").floatValue = k * 2.399f; // golden-angle spread
                    so.FindProperty("seed").floatValue = k;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }

            new GameObject($"SharkTag_v{SharksVersion}").transform.SetParent(root.transform, false);

            Scene scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[ShipTestAreaBuilder] {k} sharks released across {zones.Length} circuits.");
        }

        private static void BuildArchipelago(GameObject harbor)
        {
            Scene scene = SceneManager.GetActiveScene();
            Transform water = harbor.transform.Find("Water");
            float waterY = water != null ? water.position.y : 0f;
            Material terrain = GetIslandTerrainMaterial(waterY);

            var root = new GameObject("Archipelago");
            root.transform.SetParent(harbor.transform, false);

            foreach (IslandSpec spec in Archipelago)
            {
                var island = new GameObject(spec.name);
                island.transform.SetParent(root.transform, false);
                island.transform.position = new Vector3(spec.center.x, waterY, spec.center.y);

                string path = $"Assets/Art/Models/Island_{spec.name}.asset";
                Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null) mesh = BuildIslandSpecMesh(spec, path);

                var sandGo = new GameObject("Sand");
                sandGo.transform.SetParent(island.transform, false);
                sandGo.AddComponent<MeshFilter>().sharedMesh = mesh;
                sandGo.AddComponent<MeshRenderer>().sharedMaterial = terrain;
                sandGo.AddComponent<MeshCollider>().sharedMesh = mesh;

                PlaceSpecFlora(spec, island);
                PlaceVignettes(spec, island);
            }

            // The start island joins the same look: its mound greens over above the beach.
            Transform startSand = harbor.transform.Find("StartIsland/Sand");
            var startRenderer = startSand != null ? startSand.GetComponent<MeshRenderer>() : null;
            if (startRenderer != null) startRenderer.sharedMaterial = terrain;

            new GameObject($"ArchTag_v{ArchipelagoVersion}").transform.SetParent(root.transform, false);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ShipTestAreaBuilder] Archipelago v{ArchipelagoVersion} raised: " +
                      $"{Archipelago.Length} islands, big three minutes-around, " +
                      "5 ship-navigable rivers (Grande's two cross in a junction).");
        }

        // Same radial disc as the start island, centred on the spec, heights from SpecHeight
        // (which is world-space, so the local vertex position adds the island centre back).
        // Resolution scales with the island so rivers stay well-sampled: ~2.6 m rings and
        // ~3.5 m arcs at the coast, whatever the radius.
        private static Mesh BuildIslandSpecMesh(IslandSpec spec, string assetPath)
        {
            int rings = Mathf.Clamp(Mathf.CeilToInt((spec.radius + spec.Skirt) / 2.6f), 40, 130);
            int sectors = Mathf.Clamp(Mathf.CeilToInt(spec.radius * Mathf.PI * 2f / 3.5f), 96, 560);
            var verts = new Vector3[1 + rings * sectors];
            verts[0] = new Vector3(0f, SpecHeight(spec, spec.center.x, spec.center.y), 0f);
            for (int k = 1; k <= rings; k++)
                for (int s = 0; s < sectors; s++)
                {
                    float a = s * Mathf.PI * 2f / sectors;
                    float r = (spec.CoastRadius(a) + spec.Skirt) * k / rings;
                    float x = Mathf.Sin(a) * r, z = Mathf.Cos(a) * r;
                    verts[1 + (k - 1) * sectors + s] = new Vector3(
                        x, SpecHeight(spec, spec.center.x + x, spec.center.y + z), z);
                }
            var uvs = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                uvs[i] = new Vector2(verts[i].x, verts[i].z) * 0.08f;

            int Idx(int ring, int s) => 1 + (ring - 1) * sectors + s % sectors;
            var tris = new List<int>(rings * sectors * 6);
            for (int s = 0; s < sectors; s++) // centre fan
            {
                tris.Add(0); tris.Add(Idx(1, s)); tris.Add(Idx(1, s + 1));
            }
            for (int k = 1; k < rings; k++)
                for (int s = 0; s < sectors; s++)
                {
                    int a = Idx(k, s), b = Idx(k, s + 1);
                    int c = Idx(k + 1, s), d = Idx(k + 1, s + 1);
                    tris.Add(a); tris.Add(c); tris.Add(d);
                    tris.Add(a); tris.Add(d); tris.Add(b);
                }

            var mesh = new Mesh
            {
                name = $"Island_{spec.name}",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
                vertices = verts,
                uv = uvs,
                triangles = tris.ToArray(),
            };
            mesh.RecalculateNormals();
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath));
            AssetDatabase.CreateAsset(mesh, assetPath);
            return mesh;
        }

        // Vegetation and rock scatter across the WHOLE island, not a ring: deterministic
        // hash points, species picked by elevation band and slope. Palms and beach litter
        // near the waterline, real trees/bushes/grass over the green interior, rocks and
        // dead trees on the high or steep ground — so mountains read as crags and river
        // gorges get bare walls, with zero hand placement. Density scales with area.
        private static void PlaceSpecFlora(IslandSpec spec, GameObject island)
        {
            string[] shore = { "SM_Env_PalmTree_01", "SM_Env_PalmTree_02", "SM_Env_PalmTree_03",
                "SM_Env_PalmTree_Tall_01", "SM_Env_PalmTree_Tall_02", "SM_Env_PalmBush_03",
                "SM_Env_Beach_Pile_01", "SM_Env_Beach_Piles_01", "SM_Env_Mangrove_Tree_01",
                "SM_Env_Mangrove_Tree_02" };
            string[] green = { "SM_Env_Tree_Large_01", "SM_Env_Tree_Large_02", "SM_Env_Bush_01",
                "SM_Env_Bush_02", "SM_Env_Fern_01", "SM_Env_GrassPatch_01", "SM_Env_GrassPatch_02",
                "SM_Env_GrassPatch_03", "SM_Env_Plants_01", "SM_Env_Plants_02", "SM_Env_Plants_03",
                "SM_Env_Flowers_01", "SM_Env_Flowers_02", "SM_Env_GroundLeaves_01",
                "SM_Env_SugarCane_01", "SM_Env_Tree_Vines_01", "SM_Env_Sunflower_01" };
            string[] crag = { "SM_Env_Rocks_01", "SM_Env_Rocks_02", "SM_Env_Rocks_03",
                "SM_Env_Rock_01", "SM_Env_Rock_02", "SM_Env_Rock_03", "SM_Env_Tree_Dead_01",
                "SM_Env_GrassPatch_02" };

            var parent = new GameObject("Flora");
            parent.transform.SetParent(island.transform, false);

            int target = Mathf.Clamp(Mathf.RoundToInt(spec.radius * spec.radius / 260f), 50, 340);
            int placed = 0;
            for (int i = 0; i < target * 3 && placed < target; i++)
            {
                // Deterministic ~uniform disc sampling from the hash (sqrt for area).
                float a = Hash01(i * 1.618f, spec.phaseA * 7.13f) * Mathf.PI * 2f;
                float r = Mathf.Sqrt(Hash01(i * 2.398f, spec.phaseB * 5.71f)) * spec.radius * 1.05f;
                float x = Mathf.Sin(a) * r, z = Mathf.Cos(a) * r;
                float wx = spec.center.x + x, wz = spec.center.y + z;
                float h = SpecHeight(spec, wx, wz);
                if (h < 0.12f) continue; // open water or river channel

                // The wet fringe gets seaweed strands instead of land flora.
                if (h < 0.45f)
                {
                    Place(parent, "SM_Env_Seaweed_01", x, h, z,
                        Hash01(i * 5.99f, 1f) * 360f, 0.9f + Hash01(i * 8.31f, 2f) * 0.5f, 0.3f);
                    placed++;
                    continue;
                }

                // Facet slope from finite differences; steep ground rejects trees.
                float g = Mathf.Max(
                    Mathf.Abs(SpecHeight(spec, wx + 2f, wz) - SpecHeight(spec, wx - 2f, wz)),
                    Mathf.Abs(SpecHeight(spec, wx, wz + 2f) - SpecHeight(spec, wx, wz - 2f))) / 4f;
                if (g > 0.75f) continue; // cliff face — nothing sits right there

                string[] band = g > 0.5f || h > 13f ? crag : h < 2.2f ? shore : green;
                string pick = band[(int)(Hash01(i * 3.77f, 0.5f) * band.Length) % band.Length];
                if (Place(parent, pick, x, h, z,
                        Hash01(i * 5.99f, 1f) * 360f, 0.85f + Hash01(i * 8.31f, 2f) * 0.45f) == null)
                    continue;
                placed++;

                // Trees pull companions: clumps read as groves, not a uniform sprinkle.
                if (!pick.Contains("Tree")) continue;
                int clump = (int)(Hash01(i * 9.42f, 3f) * 3f); // 0..2 companions
                for (int c = 0; c < clump; c++)
                {
                    float ca = Hash01(i * 11.3f + c, 4f) * Mathf.PI * 2f;
                    float cr = 2.5f + Hash01(i * 13.7f + c, 5f) * 2.8f;
                    float cx = x + Mathf.Sin(ca) * cr, cz = z + Mathf.Cos(ca) * cr;
                    float ch = SpecHeight(spec, spec.center.x + cx, spec.center.y + cz);
                    if (ch < 0.5f) continue;
                    Place(parent, pick, cx, ch, cz,
                        Hash01(i * 17.9f + c, 6f) * 360f, 0.75f + Hash01(i * 19.3f + c, 7f) * 0.4f);
                    placed++;
                }
            }
            Debug.Log($"[ShipTestAreaBuilder] {spec.name}: {placed} scatter props placed.");
        }

        private static GameObject Place(GameObject parent, string envName,
            float x, float groundY, float z, float yaw, float scale, float embed = 0.12f)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                $"{SyntyRoot}/Environments/{envName}.prefab");
            if (prefab == null) return null;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = new Vector3(x, groundY, z);
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one * scale;
            SnapToGround(go, embed);
            return go;
        }

        // Settle a placed prop so its lowest rendered point sits `embed` below the sampled
        // ground height — immune to each prefab's pivot convention (corner, centre, base),
        // which is what left some props floating and others buried.
        private static void SnapToGround(GameObject go, float embed)
        {
            Renderer[] rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return;
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            go.transform.position += Vector3.up * (go.transform.position.y - embed - b.min.y);
        }

        private static void EnsureJettiesOnce()
        {
            if (SceneManager.GetActiveScene().path != ScenePath) return;
            GameObject harbor = GameObject.Find("Harbor");
            if (harbor == null) return;
            if (harbor.transform.Find("Jetties") != null) return;
            BuildJetties(harbor);
        }

        [MenuItem("Tools/Ship/Rebuild Harbor Jetties")]
        public static void RebuildJettiesMenu()
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
            Transform old = harbor.transform.Find("Jetties");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            BuildJetties(harbor);
        }

        /// <summary>
        /// The anchor stations: free-standing jetties out on the water, each with a mooring
        /// bollard (interact to moor/cast off the ship alongside) and a signal lantern that
        /// lights itself when its spot is in shade. Also adds a bollard + lantern to the home
        /// dock so the harbor itself is a mooring. Ships' decks all ride at DockTopY, so a
        /// moored deck lines up with the jetty planks for boarding.
        /// </summary>
        private static void BuildJetties(GameObject harbor)
        {
            Scene scene = SceneManager.GetActiveScene();
            Transform water = harbor.transform.Find("Water");
            float waterY = water != null ? water.position.y : 0f;
            Material wood = GetOrCreateMaterial(WoodMatPath, new Color(0.42f, 0.29f, 0.17f), 0.1f);

            var jetties = new GameObject("Jetties");
            jetties.transform.SetParent(harbor.transform, false);

            // Two destinations: one out east past the rock slalom, one by the island at the
            // far end (tucked to the island's shadow side, so its lantern self-lights).
            BuildJetty(jetties, wood, waterY, "JettyEast",
                new Vector3(48f, 0f, -85f), yawDeg: 90f);
            BuildJetty(jetties, wood, waterY, "JettyIsland",
                new Vector3(-40f, 0f, -130f), yawDeg: 205f);

            // Home dock mooring: bollard + lantern on the existing pier, station at its edge.
            var home = new GameObject("HomeMooring");
            home.transform.SetParent(jetties.transform, false);
            home.transform.position = new Vector3(DockEdgeX, 0f, DockCenterZ - 5.45f);
            home.AddComponent<NetworkIdentity>();
            var homeMooring = home.AddComponent<DockMooring>();
            AddBerthZone(home, HomeBerthCenter, HomeBerthSize);
            AddBollard(home, wood, homeMooring, new Vector3(-0.4f, DockTopY + 0.3f, 0f));
            AddLantern(home, wood, new Vector3(-3.2f, 0f, -2.5f), waterY, onDeck: true);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[ShipTestAreaBuilder] Jetties placed: JettyEast, JettyIsland, HomeMooring (bollard toggles moor/cast off; lanterns auto-light in shade).");
        }

        // A free-standing wooden jetty: planked deck at DockTopY on posts, a mooring bollard
        // at the seaward end, and a lantern post. Root carries NetworkIdentity + DockMooring
        // + the berth trigger volume that detects ships coming alongside.
        private static void BuildJetty(GameObject parent, Material wood, float waterY,
            string name, Vector3 position, float yawDeg)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent.transform, false);
            root.transform.position = position;
            root.transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);
            root.AddComponent<NetworkIdentity>();
            var mooring = root.AddComponent<DockMooring>();
            AddBerthZone(root, JettyBerthCenter, JettyBerthSize);

            // Deck: 3 m wide, 14 m long, top flush with every ship's deck height.
            var deck = GameObject.CreatePrimitive(PrimitiveType.Cube);
            deck.name = "Deck";
            deck.transform.SetParent(root.transform, false);
            deck.transform.localPosition = new Vector3(0f, DockTopY - 0.25f, 0f);
            deck.transform.localScale = new Vector3(3f, 0.5f, 14f);
            deck.GetComponent<MeshRenderer>().sharedMaterial = wood;

            // Corner posts down into the water.
            float postTop = DockTopY - 0.5f;
            float postBottom = waterY - 1.5f;
            float postH = postTop - postBottom;
            foreach (Vector2 corner in new[]
                     { new Vector2(-1.2f, -6.4f), new Vector2(1.2f, -6.4f),
                       new Vector2(-1.2f, 6.4f), new Vector2(1.2f, 6.4f),
                       new Vector2(-1.2f, 0f), new Vector2(1.2f, 0f) })
            {
                var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                post.name = "Post";
                post.transform.SetParent(root.transform, false);
                post.transform.localPosition = new Vector3(corner.x, postBottom + postH * 0.5f, corner.y);
                post.transform.localScale = new Vector3(0.35f, postH, 0.35f);
                post.GetComponent<MeshRenderer>().sharedMaterial = wood;
            }

            AddBollard(root, wood, mooring, new Vector3(0f, DockTopY + 0.3f, -5.8f));
            AddLantern(root, wood, new Vector3(1.1f, 0f, 6.2f), waterY, onDeck: true);
        }

        // Berth volumes: a trigger box on the mooring root that detects the hull alongside.
        // The jetty berth straddles both sides of the planks; the home berth reaches seaward
        // (+x) from the dock edge, long enough for every hull the harbor can moor.
        private static readonly Vector3 JettyBerthCenter = new Vector3(0f, 1f, 0f);
        private static readonly Vector3 JettyBerthSize = new Vector3(20f, 8f, 18f);
        private static readonly Vector3 HomeBerthCenter = new Vector3(7f, 1f, 0f);
        private static readonly Vector3 HomeBerthSize = new Vector3(14f, 8f, 30f);

        private static void AddBerthZone(GameObject root, Vector3 center, Vector3 size)
        {
            var box = root.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = center;
            box.size = size;
        }

        private static bool HasTriggerCollider(GameObject go)
        {
            foreach (Collider c in go.GetComponents<Collider>())
                if (c.isTrigger) return true;
            return false;
        }

        // The interact point: a squat wooden bollard whose collider carries the mooring target.
        private static void AddBollard(GameObject root, Material wood, DockMooring mooring,
            Vector3 localPos)
        {
            var bollard = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bollard.name = "MooringBollard";
            bollard.transform.SetParent(root.transform, false);
            bollard.transform.localPosition = localPos;
            bollard.transform.localScale = new Vector3(0.45f, 0.6f, 0.45f);
            bollard.GetComponent<MeshRenderer>().sharedMaterial = wood;
            bollard.AddComponent<DockMooringTarget>().SetMooring(mooring);
        }

        // Lantern post + Synty lantern + warm point light + emissive "flame", driven by a
        // DockLantern in Auto mode (lit only where the map is dark).
        private static void AddLantern(GameObject root, Material wood, Vector3 localBase,
            float waterY, bool onDeck)
        {
            float baseY = onDeck ? DockTopY : waterY;
            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.name = "LanternPost";
            post.transform.SetParent(root.transform, false);
            post.transform.localPosition = localBase + new Vector3(0f, baseY + 0.95f, 0f);
            post.transform.localScale = new Vector3(0.22f, 1.9f, 0.22f);
            post.GetComponent<MeshRenderer>().sharedMaterial = wood;

            Vector3 lampPos = localBase + new Vector3(0f, baseY + 1.95f, 0f);
            var lanternPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LanternPrefabPath);
            if (lanternPrefab != null)
            {
                var lantern = (GameObject)PrefabUtility.InstantiatePrefab(lanternPrefab);
                lantern.name = "Lantern";
                lantern.transform.SetParent(root.transform, false);
                lantern.transform.localPosition = lampPos;
            }

            var lightGo = new GameObject("LanternLight");
            lightGo.transform.SetParent(root.transform, false);
            lightGo.transform.localPosition = lampPos + new Vector3(0f, 0.25f, 0f);
            var lamp = lightGo.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.color = new Color(1f, 0.72f, 0.42f);
            lamp.intensity = 3f;
            lamp.range = 22f;
            lamp.shadows = LightShadows.None;

            var glow = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            glow.name = "LanternGlow";
            Object.DestroyImmediate(glow.GetComponent<Collider>());
            glow.transform.SetParent(root.transform, false);
            glow.transform.localPosition = lampPos + new Vector3(0f, 0.22f, 0f);
            glow.transform.localScale = Vector3.one * 0.16f;
            var glowRenderer = glow.GetComponent<MeshRenderer>();
            glowRenderer.sharedMaterial = GetOrCreateEmissiveMaterial(
                LanternGlowMatPath, new Color(1f, 0.72f, 0.42f));

            var control = lightGo.AddComponent<DockLantern>();
            control.SetRefs(lamp, glowRenderer);
        }

        private static Material GetOrCreateEmissiveMaterial(string path, Color color)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            mat = new Material(shader);
            mat.SetColor("_BaseColor", color);
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", color * 4f);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AssetDatabase.CreateAsset(mat, path);
            return mat;
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
