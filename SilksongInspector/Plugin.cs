#nullable disable
using BepInEx;
using UnityEngine;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace SilksongInspector
{
    [BepInPlugin(
        "com.vamsi.silksonginspector",
        "Silksong Inspector",
        "1.8.0"
    )]
    public class Plugin : BaseUnityPlugin
    {
        private const float ScanInterval = 0.25f;
        private const float MaxTrackDistance = 40f;
        private const float SignificantMoveThreshold = 0.15f;
        private const float BoxPadding = 0.75f;
        private const float MaxZDepth = 3.0f;
        private const float LabelYOffset = 0.3f;

        private float nextScanTime = 0f;
        private Transform hornet;
        private Vector3 hornetPos;
        private bool hornetValid = false;

        private readonly Dictionary<int, EnemyState> enemies = new Dictionary<int, EnemyState>();
        private readonly Dictionary<int, HazardState> hazards = new Dictionary<int, HazardState>();

        private LineRenderer hornetBox;
        private readonly Dictionary<int, LineRenderer> enemyBoxes = new Dictionary<int, LineRenderer>();
        private readonly Dictionary<int, TextMesh> enemyLabels = new Dictionary<int, TextMesh>();
        private readonly Dictionary<int, LineRenderer> hazardBoxes = new Dictionary<int, LineRenderer>();
        private readonly Dictionary<int, TextMesh> hazardLabels = new Dictionary<int, TextMesh>();

        private void Update()
        {
            if (Time.time >= nextScanTime)
            {
                nextScanTime = Time.time + ScanInterval;
                ScanWorld();
                ScanStandaloneHazards(); // wider, rarer scan for non-child hazards
            }

            ScanChildHazards(); // cheap, per-frame, scoped to tracked enemies only
            UpdateVisuals();
        }

        private void ScanWorld()
        {
            if (hornet == null || hornet.gameObject == null)
                hornet = FindHornet();

            hornetValid = false;

            if (hornet == null)
            {
                Logger.LogInfo("HORNET | NOT FOUND");
                DestroyBox(ref hornetBox);
                ClearAllEnemies();
                return;
            }

            hornetPos = hornet.position;

            if (hornetPos.y < -1000f)
            {
                Logger.LogInfo("HORNET | OUT OF WORLD (y < -1000)");
                DestroyBox(ref hornetBox);
                ClearAllEnemies();
                return;
            }

            hornetValid = true;

            Logger.LogInfo(
                $"HORNET | {hornet.gameObject.name} | " +
                $"POS: ({hornetPos.x:F3}, {hornetPos.y:F3}, {hornetPos.z:F3})"
            );

            Transform[] all = Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );

            HashSet<int> seenThisScan = new HashSet<int>();

            foreach (Transform t in all)
            {
                if (t == null || t == hornet)
                    continue;

                GameObject go = t.gameObject;
                if (!go.activeInHierarchy)
                    continue;

                Component healthManager = go.GetComponent("HealthManager");
                if (healthManager == null)
                    continue;

                Vector3 pos = t.position;

                if (Mathf.Abs(pos.z) > MaxZDepth)
                    continue;

                string parentName = t.parent != null ? t.parent.name : "NONE";
                string rootName = t.root != null ? t.root.name : "NONE";

                if (parentName.Contains("EmptyObjectFromNull") ||
                    rootName.Contains("EmptyObjectFromNull"))
                    continue;

                bool isAutomatonTiny = go.name.Contains("Automaton Tiny");
                bool isFreeFloating = t.parent == null;

                if (isAutomatonTiny && isFreeFloating)
                    continue;

                bool underWave = parentName.StartsWith("Wave");

                if (isAutomatonTiny && !underWave)
                    continue;

                bool hasDamageHero = go.GetComponent("DamageHero") != null;

                if (!underWave && !hasDamageHero)
                    continue;

                if (IsProbablyCorpseOrEffect(go.name))
                    continue;

                float dist = Vector3.Distance(hornetPos, pos);
                if (dist > MaxTrackDistance)
                    continue;

                int id = go.GetInstanceID();
                seenThisScan.Add(id);

                UpdateEnemy(id, go, t, dist, healthManager);
            }

            RemoveMissingEnemies(seenThisScan);
        }

        private Transform FindHornet()
        {
            Transform[] all = Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );

            foreach (Transform t in all)
            {
                if (t != null && t.gameObject.activeInHierarchy &&
                    t.gameObject.name.Contains("Hero_Hornet"))
                    return t;
            }
            return null;
        }

        private void UpdateEnemy(int id, GameObject go, Transform t, float distance, Component healthManager)
        {
            Vector3 pos = t.position;

            if (!enemies.TryGetValue(id, out EnemyState state))
            {
                state = new EnemyState
                {
                    Id = id,
                    Name = go.name,
                    Position = pos,
                    PreviousPosition = pos,
                    Distance = distance,
                    Transform = t,
                    GameObject = go,
                    Collider = go.GetComponent<Collider2D>()
                };
                enemies[id] = state;

                Logger.LogInfo(
                    $"ENEMY FOUND | ID:{id} | {go.name} | " +
                    $"POS:({pos.x:F3},{pos.y:F3},{pos.z:F3}) | " +
                    $"DIST:{distance:F2} | " +
                    $"PARENT:{GetParentName(t)} | ROOT:{t.root.name} | " +
                    $"LAYER:{LayerMask.LayerToName(go.layer)}({go.layer}) | " +
                    $"HASCOLLIDER:{state.Collider != null} | " +
                    $"PATH:{GetFullPath(t)} | " +
                    $"COMPONENTS:{GetComponentList(go)} | " +
                    $"HEALTHFIELDS:{GetHealthManagerFields(healthManager)}"
                );
                return;
            }

            float moved = Vector3.Distance(state.Position, pos);

            state.PreviousPosition = state.Position;
            state.Position = pos;
            state.Distance = distance;
            state.Transform = t;

            if (state.Collider == null)
                state.Collider = go.GetComponent<Collider2D>();

            if (moved >= SignificantMoveThreshold)
            {
                Vector3 velocity = (pos - state.PreviousPosition) / ScanInterval;

                Logger.LogInfo(
                    $"ENEMY MOVE  | ID:{id} | {state.Name} | " +
                    $"POS:({pos.x:F3},{pos.y:F3},{pos.z:F3}) | " +
                    $"DIST:{distance:F2} | " +
                    $"Δ:{moved:F2} | VEL:({velocity.x:F2},{velocity.y:F2})"
                );
            }
        }

        private void RemoveMissingEnemies(HashSet<int> seenThisScan)
        {
            List<int> toRemove = new List<int>();

            foreach (var pair in enemies)
            {
                bool stillPresent = seenThisScan.Contains(pair.Key);
                bool stillAlive = pair.Value.GameObject != null &&
                                   pair.Value.GameObject.activeInHierarchy;

                if (!stillPresent || !stillAlive)
                    toRemove.Add(pair.Key);
            }

            foreach (int id in toRemove)
                RemoveEnemy(id);
        }

        private void RemoveEnemy(int id)
        {
            if (!enemies.ContainsKey(id))
                return;

            Logger.LogInfo($"ENEMY REMOVED | ID:{id} | {enemies[id].Name}");

            if (enemyBoxes.TryGetValue(id, out LineRenderer lr))
            {
                if (lr != null)
                    Object.Destroy(lr.gameObject);
                enemyBoxes.Remove(id);
            }

            if (enemyLabels.TryGetValue(id, out TextMesh tm))
            {
                if (tm != null)
                    Object.Destroy(tm.gameObject);
                enemyLabels.Remove(id);
            }

            enemies.Remove(id);
        }

        private void ClearAllEnemies()
        {
            if (enemies.Count == 0 && enemyBoxes.Count == 0 && enemyLabels.Count == 0)
                return;

            foreach (var lr in enemyBoxes.Values)
                if (lr != null) Object.Destroy(lr.gameObject);
            enemyBoxes.Clear();

            foreach (var tm in enemyLabels.Values)
                if (tm != null) Object.Destroy(tm.gameObject);
            enemyLabels.Clear();

            enemies.Clear();

            // Enemies gone (scene change / loading) means their hazards are gone too
            ClearAllHazards();
        }

        // ==================== HAZARD (AoE / ATTACK HITBOX) TRACKING ====================

        // Runs every frame. Scoped to children of enemies we already track, so it's cheap
        // even though it runs at full frame rate — this is what catches short-lived
        // attack hitboxes (a few frames of "active" state) that a 0.25s scan would miss.
        private void ScanChildHazards()
        {
            HashSet<int> seenThisFrame = new HashSet<int>();

            foreach (var pair in enemies)
            {
                EnemyState owner = pair.Value;
                if (owner.GameObject == null || !owner.GameObject.activeInHierarchy)
                    continue;

                Collider2D[] childColliders = owner.GameObject.GetComponentsInChildren<Collider2D>(false);

                foreach (Collider2D col in childColliders)
                {
                    if (col == null || !col.enabled)
                        continue;

                    GameObject colGo = col.gameObject;

                    // Skip the enemy's own root collider — that's the body hurtbox,
                    // already drawn as the enemy's main box. We only want *child*
                    // hitboxes here, which represent a distinct attack region.
                    if (colGo == owner.GameObject)
                        continue;

                    if (colGo.GetComponent("DamageHero") == null)
                        continue;

                    if (!colGo.activeInHierarchy)
                        continue;

                    int hazId = colGo.GetInstanceID();
                    seenThisFrame.Add(hazId);

                    UpdateOrAddHazard(hazId, colGo, col, owner.Name, owner.Id);
                }
            }

            // Also keep re-validating already-known standalone hazards (found via the
            // less frequent full-scene scan) so they don't get removed every frame —
            // only actually remove them here if they've genuinely gone inactive/destroyed.
            foreach (var pair in hazards)
            {
                HazardState h = pair.Value;
                if (h.OwnerEnemyId != null)
                    continue; // owned hazards are handled by the loop above

                bool stillActive = h.GameObject != null &&
                                    h.GameObject.activeInHierarchy &&
                                    h.Collider != null &&
                                    h.Collider.enabled;

                if (stillActive)
                    seenThisFrame.Add(h.Id);
            }

            RemoveMissingHazards(seenThisFrame);
        }

        // Runs only every ScanInterval — a full scene scan for attack hitboxes that
        // AREN'T children of a tracked enemy (projectiles, ground-slam effects,
        // summoned hazards). Slower, so it runs less often; child hazards above
        // already cover the common case at full frame rate.
        private void ScanStandaloneHazards()
        {
            Transform[] all = Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );

            foreach (Transform t in all)
            {
                if (t == null)
                    continue;

                GameObject go = t.gameObject;
                if (!go.activeInHierarchy)
                    continue;

                if (go.GetComponent("DamageHero") == null)
                    continue;

                Collider2D col = go.GetComponent<Collider2D>();
                if (col == null || !col.enabled)
                    continue;

                // Already tracked as a child hazard of a known enemy? skip, avoid dupes.
                int id = go.GetInstanceID();
                if (hazards.ContainsKey(id))
                    continue;

                // If this object's own root/self is actually a tracked enemy body
                // (i.e. this IS an enemy's own collider, not a separate hazard), skip —
                // that's already drawn as the enemy box.
                bool isTrackedEnemyBody = false;
                foreach (var pair in enemies)
                {
                    if (pair.Value.GameObject == go)
                    {
                        isTrackedEnemyBody = true;
                        break;
                    }
                }
                if (isTrackedEnemyBody)
                    continue;

                Vector3 pos = t.position;
                if (Mathf.Abs(pos.z) > MaxZDepth)
                    continue;

                float dist = hornetValid ? Vector3.Distance(hornetPos, pos) : 0f;
                if (hornetValid && dist > MaxTrackDistance)
                    continue;

                UpdateOrAddHazard(id, go, col, "Environment", null);
            }
        }

        private void UpdateOrAddHazard(int id, GameObject go, Collider2D col, string ownerName, int? ownerEnemyId)
        {
            if (!hazards.TryGetValue(id, out HazardState h))
            {
                h = new HazardState
                {
                    Id = id,
                    Name = go.name,
                    OwnerName = ownerName,
                    OwnerEnemyId = ownerEnemyId,
                    GameObject = go,
                    Transform = go.transform,
                    Collider = col
                };
                hazards[id] = h;

                Logger.LogInfo(
                    $"HAZARD ACTIVE | ID:{id} | {go.name} | OWNER:{ownerName} | " +
                    $"BOUNDS_CENTER:({col.bounds.center.x:F3},{col.bounds.center.y:F3}) | " +
                    $"BOUNDS_SIZE:({col.bounds.size.x:F3},{col.bounds.size.y:F3})"
                );
            }
            else
            {
                h.Collider = col; // keep reference fresh
            }
        }

        private void RemoveMissingHazards(HashSet<int> seenThisFrame)
        {
            List<int> toRemove = null;

            foreach (var pair in hazards)
            {
                if (!seenThisFrame.Contains(pair.Key))
                    (toRemove ??= new List<int>()).Add(pair.Key);
            }

            if (toRemove == null)
                return;

            foreach (int id in toRemove)
                RemoveHazard(id);
        }

        private void RemoveHazard(int id)
        {
            if (!hazards.TryGetValue(id, out HazardState h))
                return;

            Logger.LogInfo($"HAZARD ENDED | ID:{id} | {h.Name} | OWNER:{h.OwnerName}");

            if (hazardBoxes.TryGetValue(id, out LineRenderer lr))
            {
                if (lr != null) Object.Destroy(lr.gameObject);
                hazardBoxes.Remove(id);
            }
            if (hazardLabels.TryGetValue(id, out TextMesh tm))
            {
                if (tm != null) Object.Destroy(tm.gameObject);
                hazardLabels.Remove(id);
            }

            hazards.Remove(id);
        }

        private void ClearAllHazards()
        {
            foreach (var lr in hazardBoxes.Values)
                if (lr != null) Object.Destroy(lr.gameObject);
            hazardBoxes.Clear();

            foreach (var tm in hazardLabels.Values)
                if (tm != null) Object.Destroy(tm.gameObject);
            hazardLabels.Clear();

            hazards.Clear();
        }

        // ==================== VISUAL BOXES + LABELS ====================

        private void UpdateVisuals()
        {
            if (hornetValid && hornet != null && hornet.gameObject.activeInHierarchy)
            {
                if (hornetBox == null)
                    hornetBox = CreateBox("HornetBox", Color.green);

                Collider2D hornetCol = hornet.GetComponent<Collider2D>();
                if (hornetCol != null)
                    UpdateBoxFromBounds(hornetBox, hornetCol.bounds);
                else
                    UpdateBoxPositions(hornetBox, hornet.position, BoxPadding);
            }
            else
            {
                DestroyBox(ref hornetBox);
            }

            List<int> deadIds = null;

            foreach (var pair in enemies)
            {
                EnemyState e = pair.Value;

                bool gone = e.Transform == null ||
                            e.GameObject == null ||
                            !e.GameObject.activeInHierarchy;

                if (gone)
                {
                    if (enemyBoxes.TryGetValue(e.Id, out LineRenderer deadLr) && deadLr != null)
                    {
                        Object.Destroy(deadLr.gameObject);
                        enemyBoxes.Remove(e.Id);
                    }
                    if (enemyLabels.TryGetValue(e.Id, out TextMesh deadTm) && deadTm != null)
                    {
                        Object.Destroy(deadTm.gameObject);
                        enemyLabels.Remove(e.Id);
                    }

                    (deadIds ??= new List<int>()).Add(e.Id);
                    continue;
                }

                bool isOnScreen = IsEnemyVisible(e.GameObject);

                if (!isOnScreen)
                {
                    if (enemyBoxes.TryGetValue(e.Id, out LineRenderer hideLr) && hideLr != null)
                    {
                        Object.Destroy(hideLr.gameObject);
                        enemyBoxes.Remove(e.Id);
                    }
                    if (enemyLabels.TryGetValue(e.Id, out TextMesh hideTm) && hideTm != null)
                    {
                        Object.Destroy(hideTm.gameObject);
                        enemyLabels.Remove(e.Id);
                    }
                    continue;
                }

                Color targetColor = e.Distance < 8f ? Color.red : Color.yellow;

                if (!enemyBoxes.TryGetValue(e.Id, out LineRenderer lr) || lr == null)
                {
                    lr = CreateBox($"EnemyBox_{e.Id}", targetColor);
                    enemyBoxes[e.Id] = lr;
                }
                lr.startColor = targetColor;
                lr.endColor = targetColor;

                if (e.Collider != null)
                    UpdateBoxFromBounds(lr, e.Collider.bounds);
                else
                    UpdateBoxPositions(lr, e.Transform.position, BoxPadding);

                if (!enemyLabels.TryGetValue(e.Id, out TextMesh label) || label == null)
                {
                    label = CreateLabel($"EnemyLabel_{e.Id}", e.Name, e.Id);
                    enemyLabels[e.Id] = label;
                }

                float topY = e.Collider != null ? e.Collider.bounds.max.y : e.Transform.position.y + BoxPadding;
                label.transform.position = new Vector3(e.Transform.position.x, topY + LabelYOffset, e.Transform.position.z);
            }

            if (deadIds != null)
            {
                foreach (int id in deadIds)
                {
                    if (enemies.TryGetValue(id, out var st))
                        Logger.LogInfo($"ENEMY REMOVED | ID:{id} | {st.Name}");
                    enemies.Remove(id);
                }
            }

            // ---- Hazard (AoE) boxes — distinct color, drawn on top ----
            Color hazardColor = new Color(1f, 0.15f, 1f); // magenta — visually distinct from red/yellow enemy boxes

            foreach (var pair in hazards)
            {
                HazardState h = pair.Value;

                if (h.Collider == null || h.GameObject == null || !h.GameObject.activeInHierarchy)
                    continue; // will be cleaned up by RemoveMissingHazards next frame

                if (!hazardBoxes.TryGetValue(h.Id, out LineRenderer hlr) || hlr == null)
                {
                    hlr = CreateBox($"HazardBox_{h.Id}", hazardColor);
                    hazardBoxes[h.Id] = hlr;
                }
                UpdateBoxFromBounds(hlr, h.Collider.bounds);

                if (!hazardLabels.TryGetValue(h.Id, out TextMesh hlabel) || hlabel == null)
                {
                    hlabel = CreateLabel($"HazardLabel_{h.Id}", $"AoE: {h.OwnerName}", h.Id, hazardColor);
                    hazardLabels[h.Id] = hlabel;
                }
                float hTopY = h.Collider.bounds.max.y;
                hlabel.transform.position = new Vector3(h.Collider.bounds.center.x, hTopY + LabelYOffset, h.Collider.bounds.center.z);
            }
        }

        private LineRenderer CreateBox(string name, Color color)
        {
            GameObject go = new GameObject(name);
            Object.DontDestroyOnLoad(go);

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 5;
            lr.loop = false;
            lr.useWorldSpace = true;
            lr.startWidth = 0.05f;
            lr.endWidth = 0.05f;
            lr.startColor = color;
            lr.endColor = color;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.sortingOrder = 32767;

            return lr;
        }

        private TextMesh CreateLabel(string goName, string text, int id, Color? color = null)
        {
            GameObject go = new GameObject(goName);
            Object.DontDestroyOnLoad(go);

            TextMesh tm = go.AddComponent<TextMesh>();
            tm.text = $"{text}\n#{id}";
            tm.characterSize = 0.08f;
            tm.fontSize = 64;
            tm.anchor = TextAnchor.LowerCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color ?? Color.white;

            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
                mr.sortingOrder = 32767;

            return tm;
        }

        private void UpdateBoxFromBounds(LineRenderer lr, Bounds bounds)
        {
            if (lr == null) return;

            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            float z = bounds.center.z;

            Vector3 bl = new Vector3(min.x, min.y, z);
            Vector3 br = new Vector3(max.x, min.y, z);
            Vector3 tr = new Vector3(max.x, max.y, z);
            Vector3 tl = new Vector3(min.x, max.y, z);

            lr.SetPosition(0, bl);
            lr.SetPosition(1, br);
            lr.SetPosition(2, tr);
            lr.SetPosition(3, tl);
            lr.SetPosition(4, bl);
        }

        private void UpdateBoxPositions(LineRenderer lr, Vector3 center, float pad)
        {
            if (lr == null) return;

            Vector3 bl = center + new Vector3(-pad, -pad, 0);
            Vector3 br = center + new Vector3(pad, -pad, 0);
            Vector3 tr = center + new Vector3(pad, pad, 0);
            Vector3 tl = center + new Vector3(-pad, pad, 0);

            lr.SetPosition(0, bl);
            lr.SetPosition(1, br);
            lr.SetPosition(2, tr);
            lr.SetPosition(3, tl);
            lr.SetPosition(4, bl);
        }

        private void DestroyBox(ref LineRenderer lr)
        {
            if (lr != null)
            {
                Object.Destroy(lr.gameObject);
                lr = null;
            }
        }

        private static bool IsEnemyVisible(GameObject go)
        {
            if (go == null) return false;

            Renderer r = go.GetComponent<Renderer>();
            if (r == null)
                r = go.GetComponentInChildren<Renderer>();

            return r != null && r.isVisible;
        }

        // ==================== HELPERS ====================

        private static bool IsProbablyCorpseOrEffect(string name)
        {
            name = name.ToLowerInvariant();
            return name.Contains("corpse") ||
                   name.Contains("death") ||
                   name.Contains("hit pt") ||
                   name.Contains("explosion") ||
                   name.Contains("steam") ||
                   (name.Contains("effect") && name.Contains("clone"));
        }

        private static string GetParentName(Transform t)
        {
            return t.parent != null ? t.parent.name : "NONE";
        }

        private static string GetFullPath(Transform t)
        {
            var stack = new List<string>();
            Transform cur = t;
            while (cur != null)
            {
                stack.Add(cur.name);
                cur = cur.parent;
            }
            stack.Reverse();
            return string.Join("/", stack);
        }

        private static string GetComponentList(GameObject go)
        {
            var comps = go.GetComponents<Component>();
            var names = new string[comps.Length];
            for (int i = 0; i < comps.Length; i++)
                names[i] = comps[i] != null ? comps[i].GetType().Name : "null";
            return string.Join(",", names);
        }

        private static string GetHealthManagerFields(Component healthManager)
        {
            if (healthManager == null)
                return "none";

            try
            {
                var type = healthManager.GetType();
                var fields = type.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                var sb = new StringBuilder();
                foreach (var f in fields)
                {
                    if (f.FieldType.IsClass && f.FieldType != typeof(string))
                        continue;

                    object val;
                    try { val = f.GetValue(healthManager); }
                    catch { continue; }

                    sb.Append(f.Name).Append('=').Append(val).Append(';');
                }

                return sb.Length > 0 ? sb.ToString() : "no-simple-fields";
            }
            catch (System.Exception ex)
            {
                return $"reflection-error:{ex.Message}";
            }
        }

        private void OnDestroy()
        {
            DestroyBox(ref hornetBox);
            ClearAllEnemies();
        }
    }

    public class EnemyState
    {
        public int Id;
        public string Name;
        public Vector3 Position;
        public Vector3 PreviousPosition;
        public float Distance;
        public Transform Transform;
        public GameObject GameObject;
        public Collider2D Collider;
    }

    public class HazardState
    {
        public int Id;
        public string Name;
        public string OwnerName;
        public int? OwnerEnemyId; // null = standalone/environment hazard (projectile, ground effect, etc.)
        public GameObject GameObject;
        public Transform Transform;
        public Collider2D Collider;
    }
}