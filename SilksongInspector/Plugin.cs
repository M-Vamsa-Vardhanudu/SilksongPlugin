#nullable disable
using BepInEx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SilksongInspector
{
    [BepInPlugin(
        "com.vamsi.silksonginspector",
        "Silksong Inspector",
        "2.0.0"
    )]
    public class Plugin : BaseUnityPlugin
    {
        private const float ScanInterval = 0.25f;
        private const float SnapshotInterval = 0.05f;
        private const float MaxTrackDistance = 40f;
        private const float SignificantMoveThreshold = 0.15f;
        private const float BoxPadding = 0.75f;
        private const float MaxZDepth = 3.0f;
        private const float LabelYOffset = 0.3f;
        private const string OutputDirectory = @"C:\Games\Hollow Knight - Silksong\BepInEx\plugins\SilksongInspectorData";

        private float nextScanTime = 0f;
        private float nextSnapshotTime = 0f;
        private float sessionStartTime = 0f;
        private int sessionCounter = 0;
        private int snapshotIndex = 0;
        private bool sessionActive = false;
        private string currentSessionId;
        private string currentSessionFilePath;
        private StreamWriter sessionWriter;
        private Transform hornet;
        private Vector3 hornetPos;
        private Vector3 lastHornetPos;
        private static int lastHornetFacingSign = 1;
        private bool hasLastHornetPos = false;
        private bool hornetValid = false;

        private readonly Dictionary<int, EnemyState> enemies = new Dictionary<int, EnemyState>();
        private readonly Dictionary<int, HazardState> hazards = new Dictionary<int, HazardState>();

        private int? lastPlayerHp;
        private int? lastPlayerMaxHp;
        private string lastPlayerState = "unknown";
        private float lastActionTime = 0f;
        private bool lastJumpPressed;
        private bool lastDashPressed;
        private bool lastAttackPressed;
        private bool lastSkillPressed;
        private bool lastHealPressed;
        private string currentActionPhase = "idle";
        private readonly Dictionary<int, int> lastEnemyHp = new Dictionary<int, int>();
        private readonly List<string> frameEvents = new List<string>();
        private int? lastSnapshotPlayerHp;
        private readonly Dictionary<int, int> lastSnapshotEnemyHp = new Dictionary<int, int>();

        private HeroController heroCtrl;
        private HeroControllerStates cState;
        private PlayerData playerData;
        private bool heroRefsValid;

        private LineRenderer hornetBox;
        private TextMesh hornetLabel;
        private readonly Dictionary<int, LineRenderer> enemyBoxes = new Dictionary<int, LineRenderer>();
        private readonly Dictionary<int, TextMesh> enemyLabels = new Dictionary<int, TextMesh>();
        private readonly Dictionary<int, LineRenderer> hazardBoxes = new Dictionary<int, LineRenderer>();
        private readonly Dictionary<int, TextMesh> hazardLabels = new Dictionary<int, TextMesh>();

        private void Update()
        {
            float now = Time.realtimeSinceStartup;

            if (now >= nextScanTime)
            {
                nextScanTime = now + ScanInterval;
                ScanWorld();
                ScanStandaloneHazards(); // wider, rarer scan for non-child hazards
            }

            ScanChildHazards(); // cheap, per-frame, scoped to tracked enemies only
            RefreshTrackedEnemies();
            TrackHpChanges();
            TrackActionEvents();
            UpdateVisuals();
            CaptureSnapshotIfDue();
        }

        private void ScanWorld()
        {
            if (hornet == null || hornet.gameObject == null)
                hornet = FindHornet();

            hornetValid = false;

            if (hornet == null)
            {
                Logger.LogInfo("HORNET | NOT FOUND");
                EndRecordingSession("Hornet not found");
                DestroyBox(ref hornetBox);
                ClearAllEnemies();
                return;
            }

            lastHornetPos = hornetPos;
            hasLastHornetPos = hornetValid;
            hornetPos = hornet.position;

            if (hornetPos.y < -1000f)
            {
                Logger.LogInfo("HORNET | OUT OF WORLD (y < -1000)");
                EndRecordingSession("Hornet out of world");
                DestroyBox(ref hornetBox);
                ClearAllEnemies();
                return;
            }

            hornetValid = true;
            EnsureRecordingSession();
            RefreshHeroRefs();

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

                HealthManager healthManager = go.GetComponent<HealthManager>();
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

                // Only rendered enemies may enter the dataset. Off-screen
                // (culled) actors are dropped, so passive bystanders like the
                // Maestro never show up unless the game actually renders them.
                if (!IsEnemyVisible(go))
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
            try
            {
                if (GameManager.instance != null && GameManager.instance.hero_ctrl != null)
                {
                    HeroController hc = GameManager.instance.hero_ctrl;
                    if (hc != null && hc.gameObject != null)
                        return hc.transform;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"FindHornet GM {ex.Message}");
            }

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

        private void RefreshHeroRefs()
        {
            heroRefsValid = false;
            heroCtrl = null;
            cState = null;
            playerData = null;

            if (hornet == null || hornet.gameObject == null)
                return;

            heroCtrl = hornet.gameObject.GetComponent<HeroController>();
            if (heroCtrl == null && GameManager.instance != null)
                heroCtrl = GameManager.instance.hero_ctrl;

            if (heroCtrl == null)
                return;

            cState = heroCtrl.cState;

            try
            {
                if (GameManager.instance != null)
                    playerData = GameManager.instance.playerData;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"RefreshHeroRefs PD {ex.Message}");
            }
            if (playerData == null && heroCtrl.playerData != null)
                playerData = heroCtrl.playerData;

            heroRefsValid = true;
        }

        // ==================== TYPED HERO STATE (FROM GAME ASSEMBLY) ====================
        // The game's real player state. heroCtrl.hero_state (ActorStates enum) + cState flags
        // give us EXACT attack/heal/dash/iframes — what reflection could never find reliably.

        private int? TypedPlayerHp()
        {
            return playerData != null ? (int?)playerData.health : null;
        }

        private int? TypedPlayerMaxHp()
        {
            return playerData != null ? (int?)playerData.maxHealth : null;
        }

        private int? TypedPlayerSilk()
        {
            return playerData != null ? (int?)playerData.silk : null;
        }

        private int? TypedPlayerSilkMax()
        {
            return playerData != null ? (int?)playerData.silkMax : null;
        }

        private bool? TypedPlayerGrounded()
        {
            if (cState != null)
                return cState.onGround;
            return heroCtrl != null ? (bool?)(heroCtrl.hero_state == GlobalEnums.ActorStates.grounded) : null;
        }

        private string TypedPlayerState()
        {
            if (cState == null)
                return heroCtrl != null ? heroCtrl.hero_state.ToString() : "unknown";

            if (cState.dead) return "dead";
            if (cState.focusing) return "focusing";
            if (cState.isBinding) return "binding";
            if (cState.casting) return "casting";
            if (cState.attacking) return cState.upAttacking ? "attacking_up" : cState.downAttacking ? "attacking_down" : "attacking";
            if (cState.downSpikeAntic) return "windup_downspike";
            if (cState.downSpiking) return "downspiking";
            if (cState.downSpikeRecovery) return "downspike_recovery";
            if (cState.nailCharging) return "nail_charging";
            if (cState.whipLashing) return "whip_lashing";
            if (cState.shadowDashing) return "shadow_dashing";
            if (cState.airDashing) return "air_dashing";
            if (cState.dashing) return "dashing";
            if (cState.backDashing) return "back_dashing";
            if (cState.superDashing) return "super_dashing";
            if (cState.recoiling || cState.recoilFrozen) return "hitstun";
            if (cState.jumping) return "jumping";
            if (cState.doubleJumping) return "double_jumping";
            if (cState.wallSliding) return "wall_sliding";
            if (cState.wallClinging) return "wall_clinging";
            if (cState.wallJumping) return "wall_jumping";
            if (cState.falling) return "falling";
            if (cState.onGround) return "grounded";
            return "airborne";
        }

        private string TypedPlayerPhase()
        {
            string s = TypedPlayerState();
            return InferActionPhase(s);
        }

        private bool TypedPlayerAttacking()
        {
            if (cState == null) return false;
            return cState.attacking || cState.upAttacking || cState.downAttacking ||
                   cState.downSpiking || cState.downSpikeAntic || cState.whipLashing;
        }

        private bool TypedPlayerDashing()
        {
            if (cState == null) return false;
            return cState.dashing || cState.airDashing || cState.backDashing ||
                   cState.superDashing || cState.shadowDashing;
        }

        private bool TypedPlayerInvulnerable()
        {
            if (cState == null) return false;
            return cState.invulnerable || cState.Invulnerable;
        }

        private string TypedPlayerFacing()
        {
            if (cState != null)
                return cState.facingRight ? "right" : "left";
            try
            {
                int? s = TryGetHornetFacingSign(hornet != null ? hornet.gameObject : null);
                return s.HasValue ? (s.Value > 0 ? "right" : "left") : null;
            }
            catch { return null; }
        }

        private Vector3 TypedPlayerVelocity()
        {
            if (heroCtrl != null)
            {
                try { return heroCtrl.current_velocity; }
                catch { }
            }
            return GetHornetVelocity();
        }

        private string TypedPlayerStateDetailed()
        {
            if (cState == null)
                return heroCtrl != null ? "hero_state=" + heroCtrl.hero_state : "none";

            var flags = new StringBuilder(256);
            if (cState.attacking) flags.Append("attacking|");
            if (cState.attackCount > 0) flags.Append("attackCount=").Append(cState.attackCount).Append('|');
            if (cState.altAttack) flags.Append("altAttack|");
            if (cState.upAttacking) flags.Append("upAttack|");
            if (cState.downAttacking) flags.Append("downAttack|");
            if (cState.downSpikeAntic) flags.Append("downSpikeAntic|");
            if (cState.downSpiking) flags.Append("downSpiking|");
            if (cState.downSpikeBouncing) flags.Append("downSpikeBounce|");
            if (cState.downSpikeRecovery) flags.Append("downSpikeRecovery|");
            if (cState.focusing) flags.Append("focusing|");
            if (cState.isBinding) flags.Append("binding|");
            if (cState.casting) flags.Append("casting|");
            if (cState.castRecoiling) flags.Append("castRecoil|");
            if (cState.invulnerable) flags.Append("invuln|");
            if (cState.Invulnerable) flags.Append("iframes|");
            if (cState.dashing) flags.Append("dash|");
            if (cState.airDashing) flags.Append("airDash|");
            if (cState.backDashing) flags.Append("backDash|");
            if (cState.superDashing) flags.Append("superDash|");
            if (cState.shadowDashing) flags.Append("shadowDash|");
            if (cState.recoiling) flags.Append("recoil|");
            if (cState.recoilFrozen) flags.Append("recoilFrozen|");
            if (cState.recoilingRight) flags.Append("recoilRight|");
            if (cState.recoilingLeft) flags.Append("recoilLeft|");
            if (cState.onGround) flags.Append("ground|");
            if (cState.jumping) flags.Append("jump|");
            if (cState.doubleJumping) flags.Append("doubleJump|");
            if (cState.falling) flags.Append("fall|");
            if (cState.dead) flags.Append("dead|");
            if (cState.parrying) flags.Append("parry|");

            if (flags.Length > 0)
            {
                flags.Length--;
                return "hero_state=" + heroCtrl.hero_state + "|" + flags;
            }

            return "hero_state=" + heroCtrl.hero_state + "|no_active_flags";
        }

        private void UpdateEnemy(int id, GameObject go, Transform t, float distance, HealthManager healthManager)
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

                EnsureEnemyComponents(state);

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

            EnsureEnemyComponents(state);

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

        private void EnsureEnemyComponents(EnemyState e)
        {
            if (e == null || e.GameObject == null)
                return;

            if (e.Animator == null)
                e.Animator = e.GameObject.GetComponentInChildren<Animator>(true);

            int nowFrame = Time.frameCount;
            if (e.Colliders.Count == 0 || nowFrame - e.ColliderScanFrame >= 15)
            {
                e.ColliderScanFrame = nowFrame;
                foreach (Collider2D c in e.GameObject.GetComponentsInChildren<Collider2D>(true))
                {
                    if (!e.Colliders.Contains(c))
                    {
                        e.Colliders.Add(c);

                        // DIAGNOSTIC: dump every DamageHero-bearing child once so the
                        // real hitbox names can be used to rebuild the whitelist.
                        if (c.gameObject.GetComponent("DamageHero") != null)
                        {
                            Logger.LogInfo(
                                $"DAMAGEHERO CHILD | owner:{e.Name} | goName:{c.gameObject.name} | " +
                                $"trigger:{c.isTrigger} | enabled:{c.enabled} | " +
                                $"path:{GetFullPath(c.transform)}"
                            );
                        }
                    }

                    if (!e.ColliderMonos.ContainsKey(c))
                        e.ColliderMonos[c] = c.GetComponents<MonoBehaviour>();
                }
            }
        }

        private void RefreshTrackedEnemies()
        {
            if (hornetValid && hornet != null && hornet.gameObject != null)
                hornetPos = hornet.position;

            float now = Time.realtimeSinceStartup;
            foreach (var pair in enemies)
            {
                EnemyState e = pair.Value;
                if (e == null || e.Transform == null || e.GameObject == null)
                    continue;

                Vector3 p = e.Transform.position;
                e.PreviousPosition = e.Position;
                e.Position = p;
                e.LastRefreshDelta = Math.Max(now - e.LastRefreshTime > 0f ? now - e.LastRefreshTime : SnapshotInterval, 0.0001f);
                e.LastRefreshTime = now;
                e.Distance = hornetValid ? Vector3.Distance(p, hornetPos) : e.Distance;
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
            if (!enemies.TryGetValue(id, out EnemyState state))
                return;

            // Determine the cause BEFORE removing tracking data.
            // If the last observed HP was <= 0, treat this as a death; otherwise
            // the object was removed/despawned without us observing HP reach zero.
            int? lastHp = lastEnemyHp.TryGetValue(id, out int hp) ? hp : (int?)null;
            bool diedFromDamage = lastHp.HasValue && lastHp.Value <= 0;

            string kind = diedFromDamage ? "enemy_died" : "enemy_despawned";
            frameEvents.Add(BuildEventJson(kind,
                $"{{\"enemy\":\"{EscapeJson(state.Name)}\",\"enemy_id\":{id}," +
                $"\"last_hp\":{(lastHp.HasValue ? lastHp.Value.ToString(CultureInfo.InvariantCulture) : "null")}}}"));

            Logger.LogInfo(
                $"{(diedFromDamage ? "ENEMY DIED" : "ENEMY DESPAWNED")} | " +
                $"ID:{id} | {state.Name} | last_hp={(lastHp.HasValue ? lastHp.Value.ToString(CultureInfo.InvariantCulture) : "null")}"
            );

            if (enemyBoxes.TryGetValue(id, out LineRenderer lr))
            {
                if (lr != null) Object.Destroy(lr.gameObject);
                enemyBoxes.Remove(id);
            }

            if (enemyLabels.TryGetValue(id, out TextMesh tm))
            {
                if (tm != null) Object.Destroy(tm.gameObject);
                enemyLabels.Remove(id);
            }

            enemies.Remove(id);
            lastEnemyHp.Remove(id);
            lastSnapshotEnemyHp.Remove(id);
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

                // Anything already tracked as a child hazard of an enemy is not a
                // standalone projectile. This check must happen before assigning the
                // fallback "Environment" owner.
                int id = go.GetInstanceID();
                if (hazards.ContainsKey(id))
                    continue;

                // IMPORTANT: the standalone scan walks every Transform in the scene.
                // A DamageHero collider belonging to an enemy child can therefore be
                // encountered here before/after ScanChildHazards(). Previously only the
                // enemy ROOT was excluded, so child attack hitboxes could be incorrectly
                // registered as OWNER:Environment and then serialized as projectiles.
                bool belongsToTrackedEnemy = false;
                foreach (var pair in enemies)
                {
                    EnemyState enemy = pair.Value;
                    if (enemy == null || enemy.GameObject == null)
                        continue;

                    Transform enemyRoot = enemy.GameObject.transform;
                    if (t == enemyRoot || t.IsChildOf(enemyRoot))
                    {
                        belongsToTrackedEnemy = true;
                        break;
                    }
                }

                if (belongsToTrackedEnemy)
                    continue;

                // Do not classify arbitrary scene DamageHero objects as projectiles.
                // A Rigidbody2D by itself is NOT enough: background/environment objects
                // can have physics components while remaining completely stationary.
                // Require actual movement, or a very strong projectile name.
                Rigidbody2D rb = go.GetComponent<Rigidbody2D>();
                string lowerName = go.name.ToLowerInvariant();
                bool projectileNamed =
                    lowerName.Contains("projectile") ||
                    lowerName.Contains("projectile(clone)") ||
                    lowerName.Contains("shot(clone)") ||
                    lowerName.Contains("bolt(clone)") ||
                    lowerName.Contains("bullet(clone)") ||
                    lowerName.Contains("missile(clone)");

                bool moving = rb != null && rb.velocity.sqrMagnitude > 0.0025f;
                if (!moving && !projectileNamed)
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
                Bounds hornetBounds = hornetCol != null
                    ? hornetCol.bounds
                    : new Bounds(hornet.position, new Vector3(BoxPadding * 2f, BoxPadding * 2f, 0f));

                if (hornetCol != null)
                    UpdateBoxFromBounds(hornetBox, hornetCol.bounds);
                else
                    UpdateBoxPositions(hornetBox, hornet.position, BoxPadding);

                if (hornetLabel == null)
                    hornetLabel = CreateLabel("HornetLabel", "Hornet", -1, Color.green);

                UpdateLabel(hornetLabel, BuildHornetOverlayText());
                hornetLabel.color = Color.green;
                hornetLabel.transform.position = new Vector3(hornetBounds.center.x, hornetBounds.max.y + LabelYOffset, hornetBounds.center.z);
            }
            else
            {
                DestroyBox(ref hornetBox);
                DestroyLabel(ref hornetLabel);
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

                UpdateLabel(label, BuildEnemyOverlayText(e));
                label.color = HasActiveAttackCollider(e, out _) ? Color.red : targetColor;

                float topY = e.Collider != null ? e.Collider.bounds.max.y : e.Transform.position.y + BoxPadding;
                label.transform.position = new Vector3(e.Transform.position.x, topY + LabelYOffset, e.Transform.position.z);
            }

            if (deadIds != null)
            {
                foreach (int id in deadIds)
                    RemoveEnemy(id);
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
                UpdateLabel(hlabel, BuildHazardOverlayText(h));
                hlabel.color = GetHazardOverlayColor(h);
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
            tm.text = id >= 0 ? $"{text}\n#{id}" : text;
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

        private static void UpdateLabel(TextMesh label, string text)
        {
            if (label != null)
                label.text = text;
        }

        private static void DestroyLabel(ref TextMesh label)
        {
            if (label != null)
            {
                Object.Destroy(label.gameObject);
                label = null;
            }
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

        private string BuildHornetOverlayText()
        {
            if (hornet == null || hornet.gameObject == null)
                return "Hornet\nstate: unknown";

            GameObject go = hornet.gameObject;
            Vector3 velocity = GetHornetVelocity();
            int? hp = TypedPlayerHp() ?? TryGetPlayerHealthValue("hp", "health", "currentHP", "currentHealth", "hitPoints", "life");
            int? maxHp = TypedPlayerMaxHp() ?? TryGetPlayerHealthValue("max_hp", "maxHP", "maxHealth", "maxHitPoints", "maximumHealth");
            bool? grounded = TypedPlayerGrounded() ?? TryGetPlayerGroundedValue();
            string controls = GetHornetControlSummary();
            string animator = GetHornetAnimatorSummary(go);
            string runtime = BuildHornetRuntimeSummary(go);
            int? facing = TypedPlayerFacing() == "right" ? 1 : TypedPlayerFacing() == "left" ? -1 : (int?)null;
            string state = TypedPlayerState();
            string phase = InferActionPhase(state);

            StringBuilder sb = new StringBuilder(256);
            sb.Append("Hornet");
            sb.Append("\nstate: ").Append(state).Append(" [").Append(phase).Append(']');
            sb.Append("\ncontrols: ").Append(controls);
            sb.Append("\nanimator: ").Append(animator);
            sb.Append("\nruntime: ").Append(runtime);
            sb.Append("\nhp: ").Append(hp.HasValue ? hp.Value.ToString(CultureInfo.InvariantCulture) : "unknown");
            sb.Append("/").Append(maxHp.HasValue ? maxHp.Value.ToString(CultureInfo.InvariantCulture) : "unknown");
            sb.Append("\ngrounded: ").Append(grounded.HasValue ? (grounded.Value ? "true" : "false") : "unknown");
            sb.Append("\nfacing: ").Append(facing.HasValue ? (facing.Value > 0 ? "right" : "left") : "unknown");
            sb.Append("\nvelocity: (").Append(velocity.x.ToString("0.###", CultureInfo.InvariantCulture)).Append(", ").Append(velocity.y.ToString("0.###", CultureInfo.InvariantCulture)).Append(")");

            return sb.ToString();
        }

        private string BuildEnemyOverlayText(EnemyState enemy)
        {
            if (enemy == null || enemy.GameObject == null)
                return "enemy: unknown";

            Vector3 velocity = GetEnemyVelocity(enemy);
            Vector3 relative = enemy.Position - hornetPos;
            string state = GetVerifiedStateLabel(enemy.GameObject);
            bool attacking = HasActiveAttackCollider(enemy, out _);
            int? hp = TryGetHealthValue(enemy.GameObject, "hp", "health", "currentHP", "currentHealth", "hitPoints", "life");
            int? maxHp = TryGetHealthValue(enemy.GameObject, "max_hp", "maxHP", "maxHealth", "maxHitPoints", "maximumHealth");

            StringBuilder sb = new StringBuilder(256);
            sb.Append(enemy.Name);
            sb.Append("\nstate: ").Append(string.IsNullOrEmpty(state) ? "unknown" : state);
            sb.Append("\nattacking: ").Append(attacking ? "true" : "false");
            sb.Append("\nhp: ").Append(hp.HasValue ? hp.Value.ToString(CultureInfo.InvariantCulture) : "unknown");
            sb.Append("/").Append(maxHp.HasValue ? maxHp.Value.ToString(CultureInfo.InvariantCulture) : "unknown");
            sb.Append("\nrel: (").Append(relative.x.ToString("0.###", CultureInfo.InvariantCulture)).Append(", ").Append(relative.y.ToString("0.###", CultureInfo.InvariantCulture)).Append(")");
            sb.Append("\nvel: (").Append(velocity.x.ToString("0.###", CultureInfo.InvariantCulture)).Append(", ").Append(velocity.y.ToString("0.###", CultureInfo.InvariantCulture)).Append(")");

            return sb.ToString();
        }

        private string BuildHazardOverlayText(HazardState hazard)
        {
            if (hazard == null || hazard.GameObject == null)
                return "AoE: unknown";

            string inferredType = GetHazardOverlayType(hazard);
            Vector3 velocity = GetProjectileVelocity(hazard);
            Vector3 relative = hazard.Transform.position - hornetPos;

            StringBuilder sb = new StringBuilder(256);
            sb.Append("AoE: ").Append(inferredType);
            sb.Append("\nowner: ").Append(string.IsNullOrEmpty(hazard.OwnerName) ? "unknown" : hazard.OwnerName);
            sb.Append("\nrel: (").Append(relative.x.ToString("0.###", CultureInfo.InvariantCulture)).Append(", ").Append(relative.y.ToString("0.###", CultureInfo.InvariantCulture)).Append(")");
            sb.Append("\nvel: (").Append(velocity.x.ToString("0.###", CultureInfo.InvariantCulture)).Append(", ").Append(velocity.y.ToString("0.###", CultureInfo.InvariantCulture)).Append(")");

            return sb.ToString();
        }

        private string BuildHornetRuntimeSummary(GameObject go)
        {
            if (go == null)
                return "unknown";

            Animator animator = go.GetComponent<Animator>();
            int componentCount = go.GetComponents<Component>().Length;
            return $"components={componentCount}|animator={GetHornetAnimatorSummary(go)}|observations={CountDirectRuntimeObservations(go)}";
        }

        private string GetHornetAnimatorSummary(GameObject go)
        {
            Animator animator = go != null ? go.GetComponent<Animator>() : null;
            if (animator == null)
                return "none";

            try
            {
                StringBuilder sb = new StringBuilder(256);
                sb.Append("enabled=").Append(animator.enabled);
                sb.Append("|active=").Append(animator.isActiveAndEnabled);
                sb.Append("|layers=").Append(animator.layerCount);
                sb.Append("|params=").Append(animator.parameters != null ? animator.parameters.Length : 0);
                sb.Append("|layer0=").Append(GetAnimatorLayerSummary(animator, 0));
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"error:{ex.Message}";
            }
        }

        private string BuildHornetRuntimeJson(GameObject go)
        {
            if (go == null)
                return "null";

            StringBuilder sb = new StringBuilder(8192);
            sb.Append('{');

            bool first = true;
            Component[] components = go.GetComponentsInChildren<Component>(true);
            AppendIntField(sb, ref first, "component_count", components != null ? components.Length : 0);
            AppendArrayField(sb, ref first, "component_types", BuildComponentTypeArrayJson(components));
            AppendObjectField(sb, ref first, "animator", BuildHornetAnimatorRuntimeJson(go.GetComponent<Animator>()));
            AppendArrayField(sb, ref first, "observations", BuildDirectRuntimeObservationsJson(go));
            AppendIntField(sb, ref first, "observation_count", CountDirectRuntimeObservations(go));
            AppendStringField(sb, ref first, "controls", GetHornetControlSummary());

            sb.Append('}');
            return sb.ToString();
        }

        private string BuildHornetAnimatorRuntimeJson(Animator animator)
        {
            if (animator == null)
                return "null";

            StringBuilder sb = new StringBuilder(2048);
            sb.Append('{');

            bool first = true;
            AppendBoolField(sb, ref first, "enabled", animator.enabled);
            AppendBoolField(sb, ref first, "active", animator.isActiveAndEnabled);
            AppendIntField(sb, ref first, "layer_count", animator.layerCount);
            AppendIntField(sb, ref first, "parameter_count", animator.parameters != null ? animator.parameters.Length : 0);
            AppendArrayField(sb, ref first, "layers", BuildAnimatorLayersJson(animator));

            sb.Append('}');
            return sb.ToString();
        }

        private string BuildAnimatorLayersJson(Animator animator)
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append('[');

            bool first = true;
            int layerCount = animator != null ? animator.layerCount : 0;
            for (int i = 0; i < layerCount; i++)
            {
                if (!first)
                    sb.Append(',');
                first = false;
                sb.Append(BuildAnimatorLayerJson(animator, i));
            }

            sb.Append(']');
            return sb.ToString();
        }

        private string BuildAnimatorLayerJson(Animator animator, int layerIndex)
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append('{');

            bool first = true;
            AppendIntField(sb, ref first, "index", layerIndex);
            AppendBoolField(sb, ref first, "in_transition", animator.IsInTransition(layerIndex));
            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layerIndex);
            AppendIntField(sb, ref first, "current_state_hash", state.fullPathHash);
            AppendIntField(sb, ref first, "short_name_hash", state.shortNameHash);
            AppendFloatField(sb, ref first, "normalized_time", state.normalizedTime);
            AppendStringField(sb, ref first, "current_clip", GetAnimatorClipNames(animator.GetCurrentAnimatorClipInfo(layerIndex)));
            AppendStringField(sb, ref first, "next_clip", GetAnimatorClipNames(animator.GetNextAnimatorClipInfo(layerIndex)));

            sb.Append('}');
            return sb.ToString();
        }

        private static string GetAnimatorClipNames(AnimatorClipInfo[] clips)
        {
            if (clips == null || clips.Length == 0)
                return "[]";

            StringBuilder sb = new StringBuilder(256);
            sb.Append('[');
            for (int i = 0; i < clips.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                AppendJsonString(sb, clips[i].clip != null ? clips[i].clip.name : null);
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string GetAnimatorLayerSummary(Animator animator, int layerIndex)
        {
            if (animator == null)
                return "none";

            try
            {
                AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layerIndex);
                return $"transition={animator.IsInTransition(layerIndex)}|state={state.fullPathHash}|norm={state.normalizedTime:0.###}|clip={GetAnimatorClipNames(animator.GetCurrentAnimatorClipInfo(layerIndex))}|next={GetAnimatorClipNames(animator.GetNextAnimatorClipInfo(layerIndex))}";
            }
            catch (Exception ex)
            {
                return $"error:{ex.Message}";
            }
        }

        private string BuildComponentTypeArrayJson(Component[] components)
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append('[');

            bool first = true;
            if (components != null)
            {
                foreach (Component component in components)
                {
                    if (component == null)
                        continue;

                    if (!first)
                        sb.Append(',');
                    first = false;
                    AppendJsonString(sb, component.GetType().FullName ?? component.GetType().Name);
                }
            }

            sb.Append(']');
            return sb.ToString();
        }

        private string BuildDirectRuntimeObservationsJson(GameObject go)
        {
            StringBuilder sb = new StringBuilder(8192);
            sb.Append('[');

            bool first = true;
            HashSet<int> seen = new HashSet<int>();
            Component[] components = go.GetComponentsInChildren<Component>(true);
            foreach (Component component in components)
            {
                if (component == null)
                    continue;

                int id = component.GetInstanceID();
                if (!seen.Add(id))
                    continue;

                string path = component.transform != null ? GetFullPath(component.transform) : go.name;
                Type type = component.GetType();
                FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (FieldInfo field in fields)
                {
                    if (!IsRuntimeObservationType(field.FieldType))
                        continue;

                    object value;
                    try { value = field.GetValue(component); }
                    catch { continue; }

                    if (!first)
                        sb.Append(',');
                    first = false;
                    sb.Append('{');
                    bool innerFirst = true;
                    AppendStringField(sb, ref innerFirst, "component", type.FullName ?? type.Name);
                    AppendStringField(sb, ref innerFirst, "path", path);
                    AppendStringField(sb, ref innerFirst, "member", field.Name);
                    AppendStringField(sb, ref innerFirst, "kind", "field");
                    AppendStringField(sb, ref innerFirst, "value_type", field.FieldType.FullName ?? field.FieldType.Name);
                    AppendStringField(sb, ref innerFirst, "value", value != null ? value.ToString() : null);
                    sb.Append('}');
                }

                PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (PropertyInfo property in properties)
                {
                    if (!property.CanRead || property.GetIndexParameters().Length != 0 || !IsRuntimeObservationType(property.PropertyType))
                        continue;

                    object value;
                    try { value = property.GetValue(component, null); }
                    catch { continue; }

                    if (!first)
                        sb.Append(',');
                    first = false;
                    sb.Append('{');
                    bool innerFirst = true;
                    AppendStringField(sb, ref innerFirst, "component", type.FullName ?? type.Name);
                    AppendStringField(sb, ref innerFirst, "path", path);
                    AppendStringField(sb, ref innerFirst, "member", property.Name);
                    AppendStringField(sb, ref innerFirst, "kind", "property");
                    AppendStringField(sb, ref innerFirst, "value_type", property.PropertyType.FullName ?? property.PropertyType.Name);
                    AppendStringField(sb, ref innerFirst, "value", value != null ? value.ToString() : null);
                    sb.Append('}');
                }
            }

            sb.Append(']');
            return sb.ToString();
        }

        private int CountDirectRuntimeObservations(GameObject go)
        {
            int count = 0;
            HashSet<int> seen = new HashSet<int>();
            Component[] components = go.GetComponentsInChildren<Component>(true);
            foreach (Component component in components)
            {
                if (component == null)
                    continue;

                int id = component.GetInstanceID();
                if (!seen.Add(id))
                    continue;

                Type type = component.GetType();
                FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (FieldInfo field in fields)
                    if (IsRuntimeObservationType(field.FieldType)) count++;

                PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (PropertyInfo property in properties)
                    if (property.CanRead && property.GetIndexParameters().Length == 0 && IsRuntimeObservationType(property.PropertyType)) count++;
            }
            return count;
        }

        private static bool IsRuntimeObservationType(Type type)
        {
            return IsSimpleStateType(type) ||
                   type == typeof(Vector2) ||
                   type == typeof(Vector3) ||
                   type == typeof(Vector4) ||
                   type == typeof(Quaternion) ||
                   type == typeof(Color) ||
                   type == typeof(Color32) ||
                   type == typeof(Bounds) ||
                   type == typeof(Rect) ||
                   type == typeof(Vector2Int) ||
                   type == typeof(Vector3Int);
        }

        private static Color GetHazardOverlayColor(HazardState hazard)
        {
            string type = GetHazardOverlayType(hazard).ToLowerInvariant();
            if (type.Contains("skill"))
                return Color.cyan;
            if (type.Contains("attack"))
                return new Color(1f, 0.25f, 0.25f);
            return new Color(1f, 0.15f, 1f);
        }

        private static Color GetHornetOverlayColor(string action)
        {
            return Color.green;
        }

        private string GetHornetActionLabel(GameObject go, string state, Vector3 velocity)
        {
            return GetHornetAnimatorSummary(go);
        }

        private string GetHornetMovementLabel(GameObject go, string state, Vector3 velocity)
        {
            return GetHornetControlSummary();
        }

        private static string GetHornetActionFromReflection(GameObject go)
        {
            return TryGetReflectedState(go);
        }

        private static bool IsHornetActionFlagTrue(GameObject go, params string[] candidateNames)
        {
            bool? value = TryGetReflectedBoolValue(go, candidateNames);
            return value.HasValue && value.Value;
        }

        private static string GetHazardOverlayType(HazardState hazard)
        {
            if (hazard == null)
                return "unknown";

            string name = (hazard.Name ?? string.Empty).ToLowerInvariant();
            string owner = (hazard.OwnerName ?? string.Empty).ToLowerInvariant();

            if (owner.Contains("hornet") || owner.Contains("hero"))
            {
                if (name.Contains("skill") || name.Contains("spell") || name.Contains("silk"))
                    return "skill aoe";
                if (name.Contains("attack") || name.Contains("slash") || name.Contains("strike") || name.Contains("nail") || name.Contains("pin"))
                    return "attack aoe";
                return "hornet aoe";
            }

            if (name.Contains("skill") || name.Contains("spell") || name.Contains("silk") || owner.Contains("skill"))
                return "skill aoe";
            if (name.Contains("attack") || name.Contains("slash") || name.Contains("strike") || name.Contains("nail") || name.Contains("pin") || owner.Contains("attack"))
                return "attack aoe";
            if (!string.IsNullOrEmpty(hazard.OwnerName) && hazard.OwnerName != "Environment")
                return $"{hazard.OwnerName} aoe";

            return "projectile";
        }

        // ==================== HP / DAMAGE / ACTION TRACKING ====================

        private void TrackHpChanges()
        {
            if (!hornetValid || hornet == null || hornet.gameObject == null)
                return;

            int? currentHp = TypedPlayerHp() ?? TryGetPlayerHealthValue("hp", "health", "currentHP", "currentHealth", "hitPoints", "life");
            int? currentMaxHp = TypedPlayerMaxHp() ?? TryGetPlayerHealthValue("max_hp", "maxHP", "maxHealth", "maxHitPoints", "maximumHealth");

            if (currentHp.HasValue && lastPlayerHp.HasValue)
            {
                int delta = currentHp.Value - lastPlayerHp.Value;
                if (delta != 0)
                {
                    string kind = delta < 0 ? "player_damaged" : "player_healed";
                    frameEvents.Add(BuildEventJson(kind, $"{{\"hp_before\":{lastPlayerHp.Value},\"hp_after\":{currentHp.Value},\"delta\":{delta}}}"));
                    Logger.LogInfo($"EVENT | {kind} | {lastPlayerHp.Value} -> {currentHp.Value} (delta={delta})");
                }
            }

            lastPlayerHp = currentHp;
            lastPlayerMaxHp = currentMaxHp;

            foreach (var pair in enemies)
            {
                EnemyState e = pair.Value;
                if (e.GameObject == null || !e.GameObject.activeInHierarchy)
                    continue;

                int? eHp = TryGetHealthValue(e.GameObject, "hp", "health", "currentHP", "currentHealth", "hitPoints", "life");
                if (!eHp.HasValue)
                    continue;

                if (lastEnemyHp.TryGetValue(e.Id, out int prevHp))
                {
                    int delta = eHp.Value - prevHp;
                    if (delta != 0)
                    {
                        string kind = delta < 0 ? "enemy_damaged" : "enemy_healed";
                        frameEvents.Add(BuildEventJson(kind, $"{{\"enemy\":\"{e.Name}\",\"enemy_id\":{e.Id},\"hp_before\":{prevHp},\"hp_after\":{eHp.Value},\"delta\":{delta}}}"));
                        Logger.LogInfo($"EVENT | {kind} | {e.Name} | {prevHp} -> {eHp.Value} (delta={delta})");
                    }
                }

                lastEnemyHp[e.Id] = eHp.Value;
            }
        }

        private void TrackActionEvents()
        {
            bool jumpNow = IsJumpPressed();
            bool dashNow = IsDashPressed();
            bool attackNow = IsAttackPressed();
            bool skillNow = IsSkillPressed();
            bool healNow = IsHealPressed();

            if (jumpNow && !lastJumpPressed)
                frameEvents.Add(BuildEventJson("button_down", "{\"button\":\"jump\"}"));
            if (dashNow && !lastDashPressed)
                frameEvents.Add(BuildEventJson("button_down", "{\"button\":\"dash\"}"));
            if (attackNow && !lastAttackPressed)
                frameEvents.Add(BuildEventJson("button_down", "{\"button\":\"attack\"}"));
            if (skillNow && !lastSkillPressed)
                frameEvents.Add(BuildEventJson("button_down", "{\"button\":\"skill\"}"));
            if (healNow && !lastHealPressed)
                frameEvents.Add(BuildEventJson("button_down", "{\"button\":\"heal\"}"));

            if (!jumpNow && lastJumpPressed)
                frameEvents.Add(BuildEventJson("button_up", "{\"button\":\"jump\"}"));
            if (!dashNow && lastDashPressed)
                frameEvents.Add(BuildEventJson("button_up", "{\"button\":\"dash\"}"));
            if (!attackNow && lastAttackPressed)
                frameEvents.Add(BuildEventJson("button_up", "{\"button\":\"attack\"}"));
            if (!skillNow && lastSkillPressed)
                frameEvents.Add(BuildEventJson("button_up", "{\"button\":\"skill\"}"));
            if (!healNow && lastHealPressed)
                frameEvents.Add(BuildEventJson("button_up", "{\"button\":\"heal\"}"));

            lastJumpPressed = jumpNow;
            lastDashPressed = dashNow;
            lastAttackPressed = attackNow;
            lastSkillPressed = skillNow;
            lastHealPressed = healNow;

            if (hornet != null && hornet.gameObject != null)
            {
                string state = TypedPlayerState();
                if (state != lastPlayerState)
                {
                    string phase = InferActionPhase(state);
                    frameEvents.Add(BuildEventJson("state_change", $"{{\"from\":\"{EscapeJson(lastPlayerState)}\",\"to\":\"{EscapeJson(state)}\",\"phase\":\"{phase}\"}}"));
                    Logger.LogInfo($"EVENT | state_change | {lastPlayerState} -> {state} (phase={phase})");
                    lastPlayerState = state;
                    currentActionPhase = phase;
                    lastActionTime = Time.realtimeSinceStartup;
                }
            }
        }

        private static string InferActionPhase(string state)
        {
            if (string.IsNullOrEmpty(state))
                return "idle";

            string lower = state.ToLowerInvariant();

            if (lower.Contains("dead"))
                return "death";
            if (lower.Contains("hitstun") || lower.Contains("recoil"))
                return "hitstun";
            if (lower.Contains("focus") || lower.Contains("bind") || lower.Contains("heal"))
                return "healing";
            if (lower.Contains("windup") || lower.Contains("antic") || lower.Contains("charge"))
                return "windup";
            if (lower.Contains("downspike_recovery") || lower.Contains("recovery"))
                return "recovery";
            if (lower.Contains("attacking") || lower.Contains("downspiking") || lower.Contains("slash") || lower.Contains("whip"))
                return "attacking";
            if (lower.Contains("cast") || lower.Contains("skill") || lower.Contains("spell"))
                return "skill";
            if (lower.Contains("dash") || lower.Contains("sprint"))
                return "dash";
            if (lower.Contains("jump") || lower.Contains("air") || lower.Contains("fall"))
                return "airborne";
            if (lower.Contains("wall"))
                return "wall";
            if (lower.Contains("idle") || lower.Contains("land") || lower.Contains("ground"))
                return "idle";
            if (lower.Contains("walk") || lower.Contains("run") || lower.Contains("move"))
                return "movement";

            return "other";
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string BuildEventJson(string kind, string data)
        {
            return $"{{\"kind\":\"{kind}\",\"data\":{data}}}";
        }

        private void EnsureRecordingSession()
        {
            if (sessionActive)
                return;

            Directory.CreateDirectory(OutputDirectory);

            sessionCounter++;
            currentSessionId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{sessionCounter:000}";
            currentSessionFilePath = Path.Combine(OutputDirectory, $"session_{currentSessionId}.jsonl");

            // Keep one buffered writer open for the entire session instead of
            // opening and closing the JSONL file for every snapshot.
            sessionWriter = new StreamWriter(
                currentSessionFilePath,
                false,
                new UTF8Encoding(false),
                65536
            );
            sessionWriter.AutoFlush = false;

            sessionStartTime = Time.realtimeSinceStartup;
            snapshotIndex = 0;
            nextSnapshotTime = sessionStartTime + SnapshotInterval;
            sessionActive = true;

            Logger.LogInfo($"SESSION STARTED | {currentSessionFilePath}");
        }

        private void EndRecordingSession(string reason)
        {
            if (!sessionActive)
                return;

            Logger.LogInfo($"SESSION ENDED | {reason} | {currentSessionFilePath}");

            try
            {
                if (sessionWriter != null)
                {
                    sessionWriter.Flush();
                    sessionWriter.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"SESSION WRITER CLOSE FAILED | {ex.Message}");
            }
            finally
            {
                sessionWriter = null;
            }

            sessionActive = false;
            currentSessionId = null;
            currentSessionFilePath = null;
            snapshotIndex = 0;
            sessionStartTime = 0f;
            hasLastHornetPos = false;
            lastPlayerHp = null;
            lastPlayerMaxHp = null;
            lastSnapshotPlayerHp = null;
            lastPlayerState = "unknown";
            lastEnemyHp.Clear();
            lastSnapshotEnemyHp.Clear();
            frameEvents.Clear();
        }

        private void CaptureSnapshotIfDue()
        {
            if (!sessionActive || string.IsNullOrEmpty(currentSessionFilePath))
                return;

            float now = Time.realtimeSinceStartup;
            if (now < nextSnapshotTime)
                return;

            while (now >= nextSnapshotTime)
            {
                WriteSnapshot(now);
                nextSnapshotTime += SnapshotInterval;
            }
        }

        private void WriteSnapshot(float now)
        {
            if (string.IsNullOrEmpty(currentSessionFilePath))
                return;

            try
            {
                string json = BuildSnapshotJson(now);

                if (sessionWriter == null)
                {
                    Logger.LogWarning("SNAPSHOT WRITE SKIPPED | session writer is null");
                    return;
                }

                sessionWriter.Write(json);
                sessionWriter.Write(Environment.NewLine);
                snapshotIndex++;
                frameEvents.Clear();
                UpdateSnapshotBaselines();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"SNAPSHOT WRITE FAILED | {ex.Message}");
            }
        }

        private void UpdateSnapshotBaselines()
        {
            lastSnapshotPlayerHp = TypedPlayerHp() ?? TryGetPlayerHealthValue("hp", "health", "currentHP", "currentHealth", "hitPoints", "life");
            lastSnapshotEnemyHp.Clear();
            foreach (var pair in enemies)
            {
                int? eHp = TryGetHealthValue(pair.Value.GameObject, "hp", "health", "currentHP", "currentHealth", "hitPoints", "life");
                if (eHp.HasValue)
                    lastSnapshotEnemyHp[pair.Key] = eHp.Value;
            }
        }

        private string BuildSnapshotJson(float now)
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append('{');

            bool first = true;
            AppendFloatField(sb, ref first, "timestamp", now - sessionStartTime);
            AppendIntField(sb, ref first, "frame", Time.frameCount);
            AppendObjectField(sb, ref first, "player", BuildPlayerRecordJson());
            AppendArrayField(sb, ref first, "enemies", BuildEnemyRecordsJson());
            AppendArrayField(sb, ref first, "projectiles", BuildProjectileRecordsJson());
            AppendObjectField(sb, ref first, "action", BuildActionRecordJson());
            AppendObjectField(sb, ref first, "result", BuildResultRecordJson());
            AppendArrayField(sb, ref first, "events", BuildFrameEventsJson());

            sb.Append('}');
            return sb.ToString();
        }

        private string BuildSceneJson()
        {
            Scene scene = SceneManager.GetActiveScene();
            StringBuilder sb = new StringBuilder(128);
            sb.Append('{');

            bool first = true;
            AppendStringField(sb, ref first, "name", scene.name);
            AppendIntField(sb, ref first, "build_index", scene.buildIndex);
            AppendBoolField(sb, ref first, "is_loaded", scene.isLoaded);

            sb.Append('}');
            return sb.ToString();
        }

        private string BuildSummaryJson()
        {
            StringBuilder sb = new StringBuilder(128);
            sb.Append('{');

            bool first = true;
            AppendIntField(sb, ref first, "tracked_enemies", enemies.Count);
            AppendIntField(sb, ref first, "tracked_hazards", hazards.Count);
            AppendBoolField(sb, ref first, "hornet_present", hornetValid && hornet != null && hornet.gameObject != null);

            sb.Append('}');
            return sb.ToString();
        }

        private string BuildHornetJson()
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.Append('{');

            bool first = true;
            AppendBoolField(sb, ref first, "present", hornetValid && hornet != null && hornet.gameObject != null);

            if (hornetValid && hornet != null && hornet.gameObject != null)
            {
                GameObject go = hornet.gameObject;
                Collider2D collider = go.GetComponent<Collider2D>();
                Rigidbody2D rb = go.GetComponent<Rigidbody2D>();
                Vector3 velocity = TypedPlayerVelocity();
                string animatorState = TypedPlayerState();
                string action = GetHornetActionLabel(go, animatorState, velocity);
                string movementDirection = GetHornetMovementLabel(go, animatorState, velocity);
                string facing = GetFacingDirection(go, hornet, velocity);
                Component healthManager = go.GetComponent("HealthManager");

                AppendStringField(sb, ref first, "name", go.name);
                AppendVector3Field(sb, ref first, "position", hornetPos);
                AppendVector3Field(sb, ref first, "velocity", velocity);
                AppendStringField(sb, ref first, "movement_direction", movementDirection);
                AppendStringField(sb, ref first, "action", action);
                AppendStringField(sb, ref first, "animator_state", animatorState);
                AppendStringField(sb, ref first, "state_flags", TypedPlayerStateDetailed());
                AppendBoolField(sb, ref first, "has_collider", collider != null);
                AppendStringField(sb, ref first, "parent", hornet.parent != null ? hornet.parent.name : null);
                AppendStringField(sb, ref first, "root", hornet.root != null ? hornet.root.name : null);
                AppendStringField(sb, ref first, "layer", LayerMask.LayerToName(go.layer));
                AppendStringField(sb, ref first, "components", GetComponentList(go));
                AppendStringField(sb, ref first, "health_fields", GetHealthManagerFields(healthManager));
                AppendStringField(sb, ref first, "rigidbody", GetRigidbodySummary(rb));
                AppendStringField(sb, ref first, "facing", facing);
                AppendIntNullableField(sb, ref first, "hp", TypedPlayerHp());
                AppendIntNullableField(sb, ref first, "max_hp", TypedPlayerMaxHp());
            }

            sb.Append('}');
            return sb.ToString();
        }

        private string BuildEnemiesArrayJson()
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append('[');

            bool first = true;
            foreach (var pair in enemies)
            {
                EnemyState enemy = pair.Value;
                if (enemy == null || enemy.GameObject == null || enemy.Transform == null)
                    continue;

                if (!first)
                    sb.Append(',');
                first = false;

                sb.Append(BuildEnemyJson(enemy));
            }

            sb.Append(']');
            return sb.ToString();
        }

        private string BuildEnemyJson(EnemyState enemy)
        {
            GameObject go = enemy.GameObject;
            Transform t = enemy.Transform;
            Collider2D collider = enemy.Collider != null ? enemy.Collider : go.GetComponent<Collider2D>();
            Rigidbody2D rb = go.GetComponent<Rigidbody2D>();
            Vector3 velocity = rb != null ? (Vector3)rb.velocity : (enemy.Position - enemy.PreviousPosition) / ScanInterval;
            string action = GetActorAction(go);
            string animatorState = GetAnimatorStateLabel(go);
            string movementDirection = GetMovementDirection(velocity, t);
            Component healthManager = go.GetComponent("HealthManager");

            StringBuilder sb = new StringBuilder(1024);
            sb.Append('{');

            bool first = true;
            AppendIntField(sb, ref first, "id", enemy.Id);
            AppendStringField(sb, ref first, "name", enemy.Name);
            AppendBoolField(sb, ref first, "active", go.activeInHierarchy);
            AppendVector3Field(sb, ref first, "position", enemy.Position);
            AppendVector3Field(sb, ref first, "previous_position", enemy.PreviousPosition);
            AppendVector3Field(sb, ref first, "velocity", velocity);
            AppendStringField(sb, ref first, "movement_direction", movementDirection);
            AppendStringField(sb, ref first, "action", action);
            AppendStringField(sb, ref first, "animator_state", animatorState);
            AppendFloatField(sb, ref first, "distance_to_hornet", enemy.Distance);
            AppendBoolField(sb, ref first, "has_collider", collider != null);
            AppendStringField(sb, ref first, "parent", t.parent != null ? t.parent.name : null);
            AppendStringField(sb, ref first, "root", t.root != null ? t.root.name : null);
            AppendStringField(sb, ref first, "layer", LayerMask.LayerToName(go.layer));
            AppendStringField(sb, ref first, "components", GetComponentList(go));
            AppendStringField(sb, ref first, "health_fields", GetHealthManagerFields(healthManager));
            AppendStringField(sb, ref first, "rigidbody", GetRigidbodySummary(rb));
            AppendStringField(sb, ref first, "facing", GetFacingDirection(t, velocity));

            sb.Append('}');
            return sb.ToString();
        }

        private string BuildHazardsArrayJson()
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.Append('[');

            bool first = true;
            foreach (var pair in hazards)
            {
                HazardState hazard = pair.Value;
                if (hazard == null || hazard.GameObject == null || hazard.Transform == null)
                    continue;

                if (!first)
                    sb.Append(',');
                first = false;

                sb.Append(BuildHazardJson(hazard));
            }

            sb.Append(']');
            return sb.ToString();
        }

        private string BuildHazardJson(HazardState hazard)
        {
            GameObject go = hazard.GameObject;
            Transform t = hazard.Transform;
            Collider2D collider = hazard.Collider != null ? hazard.Collider : go.GetComponent<Collider2D>();
            Rigidbody2D rb = go.GetComponent<Rigidbody2D>();
            Vector3 velocity = rb != null ? (Vector3)rb.velocity : Vector3.zero;

            StringBuilder sb = new StringBuilder(1024);
            sb.Append('{');

            bool first = true;
            AppendIntField(sb, ref first, "id", hazard.Id);
            AppendStringField(sb, ref first, "name", hazard.Name);
            AppendStringField(sb, ref first, "owner_name", hazard.OwnerName);
            AppendIntNullableField(sb, ref first, "owner_enemy_id", hazard.OwnerEnemyId);
            AppendBoolField(sb, ref first, "active", go.activeInHierarchy);
            AppendVector3Field(sb, ref first, "position", t.position);
            AppendVector3Field(sb, ref first, "velocity", velocity);
            AppendBoolField(sb, ref first, "has_collider", collider != null);
            AppendStringField(sb, ref first, "parent", t.parent != null ? t.parent.name : null);
            AppendStringField(sb, ref first, "root", t.root != null ? t.root.name : null);
            AppendStringField(sb, ref first, "components", GetComponentList(go));

            sb.Append('}');
            return sb.ToString();
        }

        private string BuildPlayerRecordJson()
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append('{');

            bool first = true;
            GameObject go = hornet != null ? hornet.gameObject : null;
            Vector3 velocity = TypedPlayerVelocity();
            string controls = GetHornetControlSummary();
            string animator = GetHornetAnimatorSummary(go);
            int? facing = TypedPlayerFacing() == "right" ? 1 : TypedPlayerFacing() == "left" ? -1 : (int?)null;
            int? hp = TypedPlayerHp() ?? TryGetPlayerHealthValue("hp", "health", "currentHP", "currentHealth", "hitPoints", "life");
            int? maxHp = TypedPlayerMaxHp() ?? TryGetPlayerHealthValue("max_hp", "maxHP", "maxHealth", "maxHitPoints", "maximumHealth");
            int? silk = TypedPlayerSilk();
            int? silkMax = TypedPlayerSilkMax();
            bool? grounded = TypedPlayerGrounded() ?? TryGetPlayerGroundedValue();
            string state = TypedPlayerState();
            string phase = InferActionPhase(state);
            bool invulnerable = TypedPlayerInvulnerable();
            bool attacking = TypedPlayerAttacking();
            bool dashing = TypedPlayerDashing();
            float timeSinceAction = lastActionTime > 0f ? Time.realtimeSinceStartup - lastActionTime : -1f;

            AppendVector2Field(sb, ref first, "position", hornetPos);
            AppendVector2Field(sb, ref first, "velocity", velocity);
            AppendFloatField(sb, ref first, "speed", velocity.magnitude);
            AppendIntNullableField(sb, ref first, "hp", hp);
            AppendIntNullableField(sb, ref first, "max_hp", maxHp);
            AppendIntNullableField(sb, ref first, "silk", silk);
            AppendIntNullableField(sb, ref first, "silk_max", silkMax);
            AppendNullableBoolField(sb, ref first, "grounded", grounded);
            AppendIntNullableField(sb, ref first, "facing", facing);
            AppendStringField(sb, ref first, "controls", controls);
            AppendStringField(sb, ref first, "state", state);
            AppendStringField(sb, ref first, "action_phase", phase);
            AppendStringField(sb, ref first, "animator", animator);
            AppendBoolField(sb, ref first, "invulnerable", invulnerable);
            AppendBoolField(sb, ref first, "attacking", attacking);
            AppendBoolField(sb, ref first, "dashing", dashing);
            AppendFloatField(sb, ref first, "time_since_last_action", timeSinceAction);
            AppendStringField(sb, ref first, "state_flags", TypedPlayerStateDetailed());

            sb.Append('}');
            return sb.ToString();
        }

        private string BuildEnemyRecordsJson()
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append('[');

            bool first = true;
            foreach (var pair in enemies)
            {
                EnemyState enemy = pair.Value;
                if (enemy == null || enemy.GameObject == null || enemy.Transform == null)
                    continue;

                if (!first)
                    sb.Append(',');
                first = false;

                sb.Append(BuildEnemyRecordJson(enemy));
            }

            sb.Append(']');
            return sb.ToString();
        }

        private static string InferEnemyPhase(string animClip, bool attacking)
        {
            if (attacking)
                return "attacking";

            if (string.IsNullOrEmpty(animClip))
                return "unknown";

            string lower = animClip.ToLowerInvariant();

            // These are intentionally broad temporary heuristics. Once real Silksong
            // clip names are collected from DAMAGEHERO/anim_state logs, replace or
            // extend them with the game's actual names.
            if (lower.Contains("antic") || lower.Contains("windup") || lower.Contains("charge") ||
                lower.Contains("telegraph") || lower.Contains("prep"))
                return "windup";
            if (lower.Contains("recover") || lower.Contains("cooldown"))
                return "recovery";
            if (lower.Contains("hurt") || lower.Contains("stagger") || lower.Contains("stun"))
                return "staggered";
            if (lower.Contains("death") || lower.Contains("die"))
                return "dying";
            if (lower.Contains("walk") || lower.Contains("run") || lower.Contains("move") || lower.Contains("chase"))
                return "moving";
            if (lower.Contains("idle"))
                return "idle";

            return "other";
        }

        private static bool? TryGetEnemyStaggered(GameObject go)
        {
            // Replace/extend these names after checking HEALTHFIELDS logs for the
            // actual stagger/vulnerability field used by each enemy type.
            return TryGetReflectedBoolValue(go,
                "IsStaggered", "isStaggered", "staggered", "isVulnerable");
        }

        private string BuildEnemyRecordJson(EnemyState enemy)
        {
            GameObject go = enemy.GameObject;
            Transform t = enemy.Transform;
            Vector3 velocity = GetEnemyVelocity(enemy);
            Vector3 relative = enemy.Position - hornetPos;
            EnsureEnemyComponents(enemy);
            string state = GetVerifiedStateLabel(go);
            string animState = GetAnimatorClipLabel(enemy.Animator);

            StringBuilder sb = new StringBuilder(1024);
            sb.Append('{');

            bool first = true;
            AppendIntField(sb, ref first, "id", enemy.Id);
            AppendStringField(sb, ref first, "type", enemy.Name);
            AppendVector2Field(sb, ref first, "position", enemy.Position);
            AppendVector2Field(sb, ref first, "velocity", velocity);
            AppendVector2Field(sb, ref first, "relative_position", relative);
            AppendFloatField(sb, ref first, "distance", enemy.Distance);
            int? curHp = TryGetHealthValue(go, "hp", "health", "currentHP", "currentHealth", "hitPoints", "life");
            int? maxHp = TryGetHealthValue(go, "max_hp", "maxHP", "maxHealth", "maxHitPoints", "maximumHealth");
            bool? staggered = TryGetEnemyStaggered(go);

            AppendIntNullableField(sb, ref first, "hp", curHp);
            AppendIntNullableField(sb, ref first, "max_hp", maxHp);
            AppendBoolField(sb, ref first, "downed", curHp.HasValue && curHp.Value <= 0);
            AppendNullableBoolField(sb, ref first, "staggered", staggered);
            AppendStringField(sb, ref first, "state", state);
            AppendStringField(sb, ref first, "anim_state", animState);
            // Attack is true only when an actual DamageHero hitbox is currently
            // enabled and active. Do not infer it from generic enemy state/action
            // names because some enemies keep attack-related AI states active.
            bool attacking = HasActiveAttackCollider(enemy, out string attackSource);
            string phase = InferEnemyPhase(animState, attacking);
            AppendBoolField(sb, ref first, "attacking", attacking);
            AppendStringField(sb, ref first, "phase", phase);
            if (attacking)
                AppendStringField(sb, ref first, "att_src", attackSource);
            AppendArrayField(sb, ref first, "colliders", BuildEnemyCollidersJson(enemy));

            sb.Append('}');
            return sb.ToString();
        }

        private static string GetAnimatorClipLabel(Animator animator)
        {
            if (animator == null)
                return "none";

            try
            {
                if (animator.IsInTransition(0))
                {
                    AnimatorClipInfo[] next = animator.GetNextAnimatorClipInfo(0);
                    if (next != null && next.Length > 0 && next[0].clip != null)
                        return next[0].clip.name;
                }

                AnimatorClipInfo[] cur = animator.GetCurrentAnimatorClipInfo(0);
                if (cur != null && cur.Length > 0 && cur[0].clip != null)
                    return cur[0].clip.name;
            }
            catch
            {
            }

            return "none";
        }

        private static bool HasActiveAttackCollider(EnemyState e, out string source)
        {
            source = null;
            if (e == null)
                return false;

            foreach (Collider2D c in e.Colliders)
            {
                if (c == null || c.gameObject == null)
                    continue;

                GameObject go = c.gameObject;
                string nameLower = go.name.ToLowerInvariant();

                // An attack must be a collider whose NAME matches an actual attack
                // hitbox. Some enemies keep a permanently-active DamageHero collider
                // under a non-attack name (body/sensor/aura); those are not attacks.
                if (!IsRelevantEnemyCollider(c))
                    continue;

                // AI awareness/utility triggers are not attacks.
                if (nameLower.Contains("range") ||
                    nameLower.Contains("alert") ||
                    nameLower.Contains("evade") ||
                    nameLower.Contains("wake") ||
                    nameLower.Contains("unalert") ||
                    nameLower.Contains("patrol") ||
                    // Persistent body-contact hurtbox (touch damage), not a swing.
                    nameLower.Contains("body"))
                    continue;

                // A DamageHero component can exist on a disabled hitbox waiting for
                // the attack animation/state to activate it. Both checks are required.
                if (!c.enabled || !go.activeInHierarchy)
                    continue;

                // The DamageHero component itself must be enabled, otherwise the
                // hitbox is inert even if the collider/GameObject are active.
                Behaviour damageHero = go.GetComponent("DamageHero") as Behaviour;
                if (damageHero != null && damageHero.enabled)
                {
                    source = go.name;
                    return true;
                }
            }

            return false;
        }

        private static bool IsRelevantEnemyCollider(Collider2D c)
        {
            if (c == null || c.gameObject == null)
                return false;

            string n = c.gameObject.name.ToLowerInvariant();
            return n.Contains("hit") || n.Contains("damager") || n.Contains("slash") || n.Contains("whip") ||
                   n.Contains("stab") || n.Contains("punch") || n.Contains("kick") || n.Contains("jump") ||
                   n.Contains("range") || n.Contains("alert") || n.Contains("wake") || n.Contains("evade") ||
                   n.Contains("throw") || n.Contains("patrol") || n.Contains("unalert");
        }

        private string BuildEnemyCollidersJson(EnemyState e)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append('[');

            bool first = true;
            int logged = 0;
            foreach (Collider2D c in e.Colliders)
            {
                if (logged >= 20)
                    break;

                if (!first)
                    sb.Append(',');
                first = false;
                logged++;

                Bounds b = c.bounds;
                sb.Append("{")
                  .Append("\"n\":\"").Append(EscapeJson(c.gameObject.name))
                  .Append("\",\"tag\":\"").Append(EscapeJson(c.gameObject.tag))
                  .Append("\",\"tr\":").Append(c.isTrigger ? "true" : "false")
                  .Append(",\"en\":").Append(c.enabled ? "true" : "false")
                  .Append(",\"act\":").Append(c.gameObject.activeInHierarchy ? "true" : "false")
                  .Append(",\"comp\":").Append(BuildColliderMonosJson(c, e))
                  .Append(",\"x\":").Append(b.center.x.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(",\"y\":").Append(b.center.y.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(",\"w\":").Append(b.size.x.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(",\"h\":").Append(b.size.y.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append("}");
            }

            sb.Append(']');
            return sb.ToString();
        }

        private static string BuildColliderMonosJson(Collider2D c, EnemyState e)
        {
            StringBuilder sb = new StringBuilder(64);
            sb.Append('[');

            if (e.ColliderMonos.TryGetValue(c, out MonoBehaviour[] monos))
            {
                bool f = true;
                int n = 0;
                foreach (MonoBehaviour m in monos)
                {
                    if (m == null || n >= 4)
                        continue;

                    if (!f)
                        sb.Append(',');
                    f = false;
                    n++;

                    sb.Append("{\"t\":\"").Append(EscapeJson(m.GetType().Name))
                      .Append("\",\"e\":").Append(m.enabled ? "true" : "false").Append("}");
                }
            }

            sb.Append(']');
            return sb.ToString();
        }

        private string BuildProjectileRecordsJson()
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.Append('[');

            bool first = true;
            foreach (var pair in hazards)
            {
                HazardState hazard = pair.Value;
                if (hazard == null || hazard.GameObject == null || hazard.Transform == null)
                    continue;

                // The hazards dictionary contains BOTH enemy attack hitboxes and
                // standalone projectiles. Only standalone hazards belong in the
                // JSON "projectiles" array; enemy-owned attack hitboxes are already
                // represented under their enemy record (attacking/att_src/colliders).
                if (hazard.OwnerEnemyId != null)
                    continue;

                if (!first)
                    sb.Append(',');
                first = false;

                sb.Append(BuildProjectileRecordJson(hazard));
            }

            sb.Append(']');
            return sb.ToString();
        }

        private string BuildProjectileRecordJson(HazardState hazard)
        {
            GameObject go = hazard.GameObject;
            Transform t = hazard.Transform;
            Vector3 position = t.position;
            Vector3 velocity = GetProjectileVelocity(hazard);
            Vector3 relative = position - hornetPos;

            StringBuilder sb = new StringBuilder(512);
            sb.Append('{');

            bool first = true;
            AppendIntField(sb, ref first, "id", hazard.Id);
            AppendVector2Field(sb, ref first, "position", position);
            AppendVector2Field(sb, ref first, "velocity", velocity);
            AppendVector2Field(sb, ref first, "relative_position", relative);
            AppendStringField(sb, ref first, "type", GetProjectileType(hazard));
            Collider2D projCol = go != null ? go.GetComponentInChildren<Collider2D>() : null;
            AppendFloatField(sb, ref first, "w", projCol != null ? projCol.bounds.size.x : 0f);
            AppendFloatField(sb, ref first, "h", projCol != null ? projCol.bounds.size.y : 0f);

            sb.Append('}');
            return sb.ToString();
        }

        private string BuildActionRecordJson()
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append('{');

            bool left = IsMoveLeftPressed();
            bool right = IsMoveRightPressed();
            bool up = IsMoveUpPressed();
            bool down = IsMoveDownPressed();
            bool jump = IsJumpPressed();
            bool dash = IsDashPressed();
            bool attack = IsAttackPressed();
            bool skill = IsSkillPressed();
            bool heal = IsHealPressed();
            string controls = GetHornetControlSummary();
            string state = TypedPlayerState();
            string phase = InferActionPhase(state);
            float heldDuration = lastActionTime > 0f ? Time.realtimeSinceStartup - lastActionTime : 0f;

            bool first = true;
            AppendBoolField(sb, ref first, "left", left);
            AppendBoolField(sb, ref first, "right", right);
            AppendBoolField(sb, ref first, "up", up);
            AppendBoolField(sb, ref first, "down", down);
            AppendBoolField(sb, ref first, "jump", jump);
            AppendBoolField(sb, ref first, "dash", dash);
            AppendBoolField(sb, ref first, "attack", attack);
            AppendBoolField(sb, ref first, "skill", skill);
            AppendBoolField(sb, ref first, "heal", heal);
            AppendBoolField(sb, ref first, "any_input", Input.anyKey);
            AppendStringField(sb, ref first, "controls", controls);
            AppendStringField(sb, ref first, "intent", controls);
            AppendStringField(sb, ref first, "state", state);
            AppendStringField(sb, ref first, "action_phase", phase);
            AppendFloatField(sb, ref first, "held_duration", heldDuration);
            AppendArrayField(sb, ref first, "buttons_down_this_frame", BuildButtonsDownJson());

            sb.Append('}');
            return sb.ToString();
        }

        private string BuildResultRecordJson()
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append('{');

            bool first = true;
            int? playerHp = TypedPlayerHp() ?? TryGetPlayerHealthValue("hp", "health", "currentHP", "currentHealth", "hitPoints", "life");
            int? playerDelta = null;
            if (playerHp.HasValue && lastSnapshotPlayerHp.HasValue)
                playerDelta = playerHp.Value - lastSnapshotPlayerHp.Value;

            bool playerHit = playerDelta.HasValue && playerDelta.Value < 0;
            bool playerHealed = playerDelta.HasValue && playerDelta.Value > 0;

            int totalEnemyDelta = 0;
            bool anyEnemyHit = false;
            foreach (var pair in enemies)
            {
                int? eHp = TryGetHealthValue(pair.Value.GameObject, "hp", "health", "currentHP", "currentHealth", "hitPoints", "life");
                if (eHp.HasValue && lastSnapshotEnemyHp.TryGetValue(pair.Key, out int prev))
                {
                    int d = eHp.Value - prev;
                    if (d < 0) anyEnemyHit = true;
                    totalEnemyDelta += d;
                }
            }

            AppendIntNullableField(sb, ref first, "player_hp_delta", playerDelta);
            AppendIntField(sb, ref first, "enemy_hp_delta", totalEnemyDelta);
            AppendBoolField(sb, ref first, "player_hit", playerHit);
            AppendBoolField(sb, ref first, "player_healed", playerHealed);
            AppendBoolField(sb, ref first, "enemy_hit", anyEnemyHit);
            AppendIntNullableField(sb, ref first, "player_hp", playerHp);
            AppendArrayField(sb, ref first, "events", BuildFrameEventsJson());

            sb.Append('}');
            return sb.ToString();
        }

        private Vector3 GetHornetVelocity()
        {
            if (heroRefsValid && heroCtrl != null)
            {
                try { return heroCtrl.current_velocity; } catch { }
            }

            if (hornet == null)
                return Vector3.zero;

            Rigidbody2D rb = hornet.GetComponent<Rigidbody2D>();
            if (rb != null)
                return rb.velocity;

            if (hasLastHornetPos)
                return (hornetPos - lastHornetPos) / ScanInterval;

            return Vector3.zero;
        }

        private Vector3 GetEnemyVelocity(EnemyState enemy)
        {
            if (enemy == null || enemy.GameObject == null)
                return Vector3.zero;

            Rigidbody2D rb = enemy.GameObject.GetComponent<Rigidbody2D>();
            if (rb != null)
                return rb.velocity;

            return (enemy.Position - enemy.PreviousPosition) / enemy.LastRefreshDelta;
        }

        private Vector3 GetProjectileVelocity(HazardState hazard)
        {
            if (hazard == null || hazard.GameObject == null)
                return Vector3.zero;

            Rigidbody2D rb = hazard.GameObject.GetComponent<Rigidbody2D>();
            if (rb != null)
                return rb.velocity;

            return Vector3.zero;
        }

        private string GetProjectileType(HazardState hazard)
        {
            if (hazard == null)
                return "unknown";

            if (!string.IsNullOrEmpty(hazard.OwnerName) && hazard.OwnerName != "Environment")
                return hazard.OwnerName;

            return string.IsNullOrEmpty(hazard.Name) ? "unknown" : hazard.Name;
        }

        private int GetFacingSign(Transform target, Vector3 velocity)
        {
            if (velocity.x > 0.01f)
                return 1;
            if (velocity.x < -0.01f)
                return -1;

            if (target != null)
                return target.localScale.x >= 0f ? 1 : -1;

            return 1;
        }

        private int? TryGetPlayerHealthValue(params string[] candidateNames)
        {
            int? typed = TypedPlayerHp();
            if (typed.HasValue)
                return typed;
            return TryGetHealthValue(hornet != null ? hornet.gameObject : null, candidateNames);
        }

        private bool? TryGetPlayerGroundedValue()
        {
            bool? typed = TypedPlayerGrounded();
            if (typed.HasValue)
                return typed;

            if (hornet == null)
                return null;

            bool? grounded = TryGetReflectedBoolValue(hornet.gameObject, "grounded", "isGrounded", "onGround", "groundState", "grounding");
            if (grounded.HasValue)
                return grounded;

            Rigidbody2D rb = hornet.GetComponent<Rigidbody2D>();
            if (rb == null)
                return null;

            return Mathf.Abs(rb.velocity.y) < 0.02f && hornet.position.y <= hornetPos.y + 0.01f;
        }

        private static int? TryGetHealthValue(GameObject go, params string[] candidateNames)
        {
            if (go != null)
            {
                try
                {
                    HealthManager hm = go.GetComponent<HealthManager>();
                    if (hm != null)
                        return hm.hp;
                }
                catch
                {
                }
            }
            return TryGetReflectedIntValue(go, candidateNames);
        }

        private static int? TryGetReflectedIntValue(GameObject go, params string[] candidateNames)
        {
            if (go == null)
                return null;

            Component[] components = go.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component == null)
                    continue;

                Type type = component.GetType();
                foreach (string candidateName in candidateNames)
                {
                    FieldInfo field = type.GetField(candidateName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null && IsNumericType(field.FieldType))
                    {
                        try
                        {
                            object value = field.GetValue(component);
                            if (value != null)
                                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                        }
                        catch
                        {
                        }
                    }

                    PropertyInfo property = type.GetProperty(candidateName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (property != null && property.CanRead && IsNumericType(property.PropertyType))
                    {
                        try
                        {
                            object value = property.GetValue(component, null);
                            if (value != null)
                                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return null;
        }

        private static bool? TryGetReflectedBoolValue(GameObject go, params string[] candidateNames)
        {
            if (go == null)
                return null;

            Component[] components = go.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component == null)
                    continue;

                Type type = component.GetType();
                foreach (string candidateName in candidateNames)
                {
                    FieldInfo field = type.GetField(candidateName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null && field.FieldType == typeof(bool))
                    {
                        try
                        {
                            return (bool)field.GetValue(component);
                        }
                        catch
                        {
                        }
                    }

                    PropertyInfo property = type.GetProperty(candidateName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (property != null && property.CanRead && property.PropertyType == typeof(bool))
                    {
                        try
                        {
                            return (bool)property.GetValue(component, null);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return null;
        }

        private static string GetVerifiedStateLabel(GameObject go)
        {
            if (go == null)
                return "unknown";

            string reflected = TryGetReflectedState(go);
            if (!string.IsNullOrEmpty(reflected))
                return reflected;

            Animator animator = go.GetComponent<Animator>();
            if (animator == null)
                return "unknown";

            try
            {
                if (animator.IsInTransition(0))
                {
                    AnimatorClipInfo[] next = animator.GetNextAnimatorClipInfo(0);
                    if (next != null && next.Length > 0 && next[0].clip != null)
                        return next[0].clip.name;
                }

                AnimatorClipInfo[] current = animator.GetCurrentAnimatorClipInfo(0);
                if (current != null && current.Length > 0 && current[0].clip != null)
                    return current[0].clip.name;
            }
            catch
            {
            }

            return "unknown";
        }

        private static bool IsNumericType(Type type)
        {
            return type == typeof(int) ||
                   type == typeof(float) ||
                   type == typeof(double) ||
                   type == typeof(long) ||
                   type == typeof(short) ||
                   type == typeof(byte) ||
                   type == typeof(uint) ||
                   type == typeof(ulong) ||
                   type == typeof(ushort) ||
                   type == typeof(sbyte);
        }

        private static string GetFacingDirection(GameObject go, Transform transform, Vector3 velocity)
        {
            return GetFacingSign(go, transform, velocity) > 0 ? "right" : "left";
        }

        private static int GetFacingSign(GameObject go, Transform target, Vector3 velocity)
        {
            if (IsMoveRightPressed())
                return lastHornetFacingSign = 1;
            if (IsMoveLeftPressed())
                return lastHornetFacingSign = -1;

            float axis = Input.GetAxisRaw("Horizontal");
            if (axis > 0.1f)
                return lastHornetFacingSign = 1;
            if (axis < -0.1f)
                return lastHornetFacingSign = -1;

            if (velocity.x > 0.01f)
                return lastHornetFacingSign = 1;
            if (velocity.x < -0.01f)
                return lastHornetFacingSign = -1;

            int? reflected = TryGetHornetFacingSign(go);
            if (reflected.HasValue)
                return lastHornetFacingSign = reflected.Value;

            if (target != null)
                return lastHornetFacingSign;

            return lastHornetFacingSign;
        }

        private static int? TryGetHornetFacingSign(GameObject go)
        {
            if (go == null)
                return null;

            if (TryGetReflectedBoolValue(go, "isFacingRight", "facingRight", "lookingRight", "isLookingRight").HasValue)
            {
                bool right = TryGetReflectedBoolValue(go, "isFacingRight", "facingRight", "lookingRight", "isLookingRight").Value;
                return right ? 1 : -1;
            }

            if (TryGetReflectedBoolValue(go, "isFacingLeft", "facingLeft", "lookingLeft", "isLookingLeft").HasValue)
            {
                bool left = TryGetReflectedBoolValue(go, "isFacingLeft", "facingLeft", "lookingLeft", "isLookingLeft").Value;
                return left ? -1 : 1;
            }

            int? numeric = TryGetReflectedIntValue(go, "facingDirection", "faceDirection", "lookDirection", "direction", "facing", "orientation", "moveDirection");
            if (numeric.HasValue)
            {
                if (numeric.Value > 0)
                    return 1;
                if (numeric.Value < 0)
                    return -1;
            }

            string reflected = TryGetReflectedStringValue(go, "facingDirection", "faceDirection", "lookDirection", "direction", "facing", "orientation", "moveDirection");
            if (!string.IsNullOrEmpty(reflected))
            {
                string lower = reflected.ToLowerInvariant();
                if (lower.Contains("right")) return 1;
                if (lower.Contains("left")) return -1;
            }

            return null;
        }

        private static bool IsMoveLeftPressed()
        {
            return Input.GetAxisRaw("Horizontal") < -0.1f ||
                   Input.GetKey(KeyCode.A) ||
                   Input.GetKey(KeyCode.LeftArrow);
        }

        private static bool IsMoveRightPressed()
        {
            return Input.GetAxisRaw("Horizontal") > 0.1f ||
                   Input.GetKey(KeyCode.D) ||
                   Input.GetKey(KeyCode.RightArrow);
        }

        private static bool IsMoveUpPressed()
        {
            return Input.GetAxisRaw("Vertical") > 0.1f ||
                   Input.GetKey(KeyCode.W) ||
                   Input.GetKey(KeyCode.UpArrow);
        }

        private static bool IsMoveDownPressed()
        {
            return Input.GetAxisRaw("Vertical") < -0.1f ||
                   Input.GetKey(KeyCode.S) ||
                   Input.GetKey(KeyCode.DownArrow);
        }

        private bool IsJumpPressed()
        {
            return cState != null && (cState.jumping || cState.doubleJumping || cState.wallJumping);
        }

        private bool IsDashPressed()
        {
            return TypedPlayerDashing(); // already exists in this file, just call it
        }

        private bool IsAttackPressed()
        {
            return TypedPlayerAttacking(); // already exists in this file, just call it
        }

        private bool IsHealPressed()
        {
            return cState != null && (cState.focusing || cState.isBinding);
        }

        private bool IsSkillPressed()
        {
            return cState != null && cState.casting;
        }

        private string GetHornetControlSummary()
        {
            List<string> controls = new List<string>();

            if (IsMoveLeftPressed()) controls.Add("left");
            if (IsMoveRightPressed()) controls.Add("right");
            if (IsMoveUpPressed()) controls.Add("up");
            if (IsMoveDownPressed()) controls.Add("down");
            if (IsJumpPressed()) controls.Add("jump");
            if (IsDashPressed()) controls.Add("dash");
            if (IsAttackPressed()) controls.Add("attack");
            if (IsSkillPressed()) controls.Add("skill");
            if (IsHealPressed()) controls.Add("heal");
            if (Input.anyKey) controls.Add("anykey");

            return controls.Count > 0 ? string.Join("+", controls.ToArray()) : "none";
        }

        private static string TryGetReflectedStringValue(GameObject go, params string[] candidateNames)
        {
            if (go == null)
                return null;

            Component[] components = go.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component == null)
                    continue;

                Type type = component.GetType();
                foreach (string candidateName in candidateNames)
                {
                    FieldInfo field = type.GetField(candidateName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null)
                    {
                        try
                        {
                            object value = field.GetValue(component);
                            if (value != null)
                                return value.ToString();
                        }
                        catch
                        {
                        }
                    }

                    PropertyInfo property = type.GetProperty(candidateName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (property != null && property.CanRead)
                    {
                        try
                        {
                            object value = property.GetValue(component, null);
                            if (value != null)
                                return value.ToString();
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return null;
        }

        private static void AppendStringField(StringBuilder sb, ref bool first, string name, string value)
        {
            AppendFieldPrefix(sb, ref first, name);
            AppendJsonString(sb, value);
        }

        private static void AppendIntField(StringBuilder sb, ref bool first, string name, int value)
        {
            AppendFieldPrefix(sb, ref first, name);
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendIntNullableField(StringBuilder sb, ref bool first, string name, int? value)
        {
            AppendFieldPrefix(sb, ref first, name);
            if (value.HasValue)
                sb.Append(value.Value.ToString(CultureInfo.InvariantCulture));
            else
                sb.Append("null");
        }

        private static void AppendNullableIntField(StringBuilder sb, ref bool first, string name, int? value)
        {
            AppendIntNullableField(sb, ref first, name, value);
        }

        private static void AppendNullableBoolField(StringBuilder sb, ref bool first, string name, bool? value)
        {
            AppendFieldPrefix(sb, ref first, name);
            if (value.HasValue)
                sb.Append(value.Value ? "true" : "false");
            else
                sb.Append("null");
        }

        private static void AppendFloatField(StringBuilder sb, ref bool first, string name, float value)
        {
            AppendFieldPrefix(sb, ref first, name);
            sb.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private static void AppendBoolField(StringBuilder sb, ref bool first, string name, bool value)
        {
            AppendFieldPrefix(sb, ref first, name);
            sb.Append(value ? "true" : "false");
        }

        private static void AppendVector2Field(StringBuilder sb, ref bool first, string name, Vector3 value)
        {
            AppendFieldPrefix(sb, ref first, name);
            sb.Append('{');
            sb.Append("\"x\":").Append(value.x.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(",\"y\":").Append(value.y.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append('}');
        }

        private static void AppendObjectField(StringBuilder sb, ref bool first, string name, string json)
        {
            AppendFieldPrefix(sb, ref first, name);
            sb.Append(json);
        }

        private static void AppendArrayField(StringBuilder sb, ref bool first, string name, string json)
        {
            AppendFieldPrefix(sb, ref first, name);
            sb.Append(json);
        }

        private static void AppendVector3Field(StringBuilder sb, ref bool first, string name, Vector3 value)
        {
            AppendFieldPrefix(sb, ref first, name);
            sb.Append('{');
            sb.Append("\"x\":").Append(value.x.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(",\"y\":").Append(value.y.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(",\"z\":").Append(value.z.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append('}');
        }

        private static void AppendFieldPrefix(StringBuilder sb, ref bool first, string name)
        {
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append('"').Append(name).Append("\":");
        }

        private static void AppendJsonString(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (char.IsControl(c))
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static string GetActorAction(GameObject go)
        {
            return GetVerifiedStateLabel(go);
        }

        private static string GetAnimatorStateLabel(GameObject go)
        {
            return GetVerifiedStateLabel(go);
        }

        private static string TryGetReflectedState(GameObject go)
        {
            return TryGetReflectedState(go, new[]
            {
                "currentState", "state", "action", "currentAction", "movementState", "animationState", "animState", "attackState", "sprintState", "dashState", "skillState", "healState", "fsmState", "status", "facingDirection", "faceDirection", "lookDirection", "orientation"
            });
        }

        private static string TryGetReflectedState(GameObject go, string[] candidateNames)
        {
            if (go == null)
                return null;

            Component[] components = go.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component == null)
                    continue;

                Type type = component.GetType();
                foreach (string candidateName in candidateNames)
                {
                    FieldInfo field = type.GetField(candidateName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null && IsSimpleStateType(field.FieldType))
                    {
                        try
                        {
                            object value = field.GetValue(component);
                            if (value != null)
                                return value.ToString();
                        }
                        catch
                        {
                        }
                    }

                    PropertyInfo property = type.GetProperty(candidateName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (property != null && property.CanRead && IsSimpleStateType(property.PropertyType))
                    {
                        try
                        {
                            object value = property.GetValue(component, null);
                            if (value != null)
                                return value.ToString();
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return null;
        }

        private static bool IsSimpleStateType(Type type)
        {
            return type == typeof(string) ||
                   type.IsEnum ||
                   type == typeof(bool) ||
                   type == typeof(int) ||
                   type == typeof(float) ||
                   type == typeof(double) ||
                   type == typeof(long) ||
                   type == typeof(short) ||
                   type == typeof(byte);
        }

        private static string GetMovementDirection(Vector3 velocity, Transform transform)
        {
            float x = velocity.x;
            float y = velocity.y;

            if (Mathf.Abs(x) >= Mathf.Abs(y) && Mathf.Abs(x) > 0.01f)
                return x > 0f ? "right" : "left";

            if (Mathf.Abs(y) > 0.01f)
                return y > 0f ? "up" : "down";

            if (transform != null)
                return transform.localScale.x >= 0f ? "stationary_right" : "stationary_left";

            return "stationary";
        }

        private static string GetFacingDirection(Transform transform, Vector3 velocity)
        {
            if (transform == null)
                return "unknown";

            if (Mathf.Abs(velocity.x) > 0.01f)
                return velocity.x >= 0f ? "right" : "left";

            return transform.localScale.x >= 0f ? "right" : "left";
        }

        private static string GetRigidbodySummary(Rigidbody2D rb)
        {
            if (rb == null)
                return "none";

            Vector2 velocity = rb.velocity;
            return $"vel=({velocity.x.ToString("0.###", CultureInfo.InvariantCulture)},{velocity.y.ToString("0.###", CultureInfo.InvariantCulture)})|drag={rb.drag.ToString("0.###", CultureInfo.InvariantCulture)}|gravity={rb.gravityScale.ToString("0.###", CultureInfo.InvariantCulture)}|simulated={rb.simulated}";
        }

        private string BuildFrameEventsJson()
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append('[');

            bool first = true;
            foreach (string evt in frameEvents)
            {
                if (!first)
                    sb.Append(',');
                first = false;
                sb.Append(evt);
            }

            sb.Append(']');
            return sb.ToString();
        }

        private string BuildButtonsDownJson()
        {
            StringBuilder sb = new StringBuilder(128);
            sb.Append('[');

            bool first = true;
            if (IsJumpPressed()) { if (!first) sb.Append(','); first = false; sb.Append("\"jump\""); }
            if (IsDashPressed()) { if (!first) sb.Append(','); first = false; sb.Append("\"dash\""); }
            if (IsAttackPressed()) { if (!first) sb.Append(','); first = false; sb.Append("\"attack\""); }
            if (IsSkillPressed()) { if (!first) sb.Append(','); first = false; sb.Append("\"skill\""); }
            if (IsHealPressed()) { if (!first) sb.Append(','); first = false; sb.Append("\"heal\""); }
            if (IsMoveLeftPressed()) { if (!first) sb.Append(','); first = false; sb.Append("\"left\""); }
            if (IsMoveRightPressed()) { if (!first) sb.Append(','); first = false; sb.Append("\"right\""); }
            if (IsMoveUpPressed()) { if (!first) sb.Append(','); first = false; sb.Append("\"up\""); }
            if (IsMoveDownPressed()) { if (!first) sb.Append(','); first = false; sb.Append("\"down\""); }

            sb.Append(']');
            return sb.ToString();
        }

        private void OnDestroy()
        {
            EndRecordingSession("plugin destroyed");
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
        public Animator Animator;
        public readonly List<Collider2D> Colliders = new List<Collider2D>();
        public readonly Dictionary<Collider2D, MonoBehaviour[]> ColliderMonos = new Dictionary<Collider2D, MonoBehaviour[]>();
        public int ColliderScanFrame;
        public float LastRefreshDelta = 0.05f;
        public float LastRefreshTime;
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