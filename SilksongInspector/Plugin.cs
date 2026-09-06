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
        "1.6.3"
    )]
    public class Plugin : BaseUnityPlugin
    {
        private const float ScanInterval = 0.25f;
        private const float MaxTrackDistance = 40f;
        private const float SignificantMoveThreshold = 0.15f;
        private const float BoxPadding = 0.75f;
        private const float MaxZDepth = 3.0f;
        private const float LabelYOffset = 1.0f;

        private float nextScanTime = 0f;
        private Transform hornet;
        private Vector3 hornetPos;
        private bool hornetValid = false;

        private readonly Dictionary<int, EnemyState> enemies = new Dictionary<int, EnemyState>();

        private LineRenderer hornetBox;
        private readonly Dictionary<int, LineRenderer> enemyBoxes = new Dictionary<int, LineRenderer>();
        private readonly Dictionary<int, TextMesh> enemyLabels = new Dictionary<int, TextMesh>();

        private void Update()
        {
            if (Time.time >= nextScanTime)
            {
                nextScanTime = Time.time + ScanInterval;
                ScanWorld();
            }

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
                    GameObject = go
                };
                enemies[id] = state;

                Logger.LogInfo(
                    $"ENEMY FOUND | ID:{id} | {go.name} | " +
                    $"POS:({pos.x:F3},{pos.y:F3},{pos.z:F3}) | " +
                    $"DIST:{distance:F2} | " +
                    $"PARENT:{GetParentName(t)} | ROOT:{t.root.name} | " +
                    $"LAYER:{LayerMask.LayerToName(go.layer)}({go.layer}) | " +
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
        }

        // ==================== VISUAL BOXES + LABELS ====================

        private void UpdateVisuals()
        {
            if (hornetValid && hornet != null && hornet.gameObject.activeInHierarchy)
            {
                if (hornetBox == null)
                    hornetBox = CreateBox("HornetBox", Color.green);

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

                // NEW: only draw a box/label if the enemy's renderer is actually
                // inside the camera's view frustum right now. Off-screen enemies
                // (like the pre-spawned Wave enemies sitting up high) stay tracked
                // in the `enemies` dict for logging, but get no visual.
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
                UpdateBoxPositions(lr, e.Transform.position, BoxPadding);

                if (!enemyLabels.TryGetValue(e.Id, out TextMesh label) || label == null)
                {
                    label = CreateLabel($"EnemyLabel_{e.Id}", e.Name, e.Id);
                    enemyLabels[e.Id] = label;
                }
                label.transform.position = e.Transform.position + new Vector3(0, BoxPadding + LabelYOffset, 0);
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
        }

        // NEW helper: checks the enemy's actual renderer visibility, not just its
        // position relative to the camera. isVisible is set by Unity's culling
        // system, so this correctly handles off-screen, occluded, or not-yet-
        // culled-but-outside-frustum objects.
        private static bool IsEnemyVisible(GameObject go)
        {
            if (go == null) return false;

            Renderer r = go.GetComponent<Renderer>();
            if (r == null)
            {
                // Some enemies might carry the renderer on a child (e.g. tk2dSprite
                // setups sometimes split visuals across children). Fall back to a
                // search if the direct component isn't there.
                r = go.GetComponentInChildren<Renderer>();
            }

            return r != null && r.isVisible;
        }

        private LineRenderer CreateBox(string name, Color color)
        {
            GameObject go = new GameObject(name);
            Object.DontDestroyOnLoad(go);

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 5;
            lr.loop = false;
            lr.useWorldSpace = true;
            lr.startWidth = 0.07f;
            lr.endWidth = 0.07f;
            lr.startColor = color;
            lr.endColor = color;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.sortingOrder = 32767;

            return lr;
        }

        private TextMesh CreateLabel(string goName, string enemyName, int id)
        {
            GameObject go = new GameObject(goName);
            Object.DontDestroyOnLoad(go);

            TextMesh tm = go.AddComponent<TextMesh>();
            tm.text = $"{enemyName}\n#{id}";
            tm.characterSize = 0.08f;
            tm.fontSize = 64;
            tm.anchor = TextAnchor.LowerCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = Color.white;

            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
                mr.sortingOrder = 32767;

            return tm;
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
    }
}