using System.Collections.Generic;
using UnityEngine;

namespace VolumetricContrails
{
    // launch smoke as a raymarched volume, tiled 3d textures for the active tail
    public class SmokeVolumeGroup : MonoBehaviour
    {
        private const int MaxTextureLayerPuffs = 256; // must match MAX_PUFFS in SmokeVolumeSplat.compute


        // Bigger chunks with more overlap do more for the seams than resolution alone.
        private const int ActiveTilePuffChunkSize = 20;
        private const int ActiveTileOverlap = 8; // shared puffs on each side, avoids seams between tiles
        private const int MaxActiveTiles = 48;

        // How far density reaches past a puff's own radius. Low enough that puffs read
        // separately instead of merging into one mass.
        private const float BlendRadiusMultiplier = 1.8f;

        private const float ShaderReferenceRadius = 18f; // must match _ReferenceRadius default in the material


        // Render the trail as one analytic capsule chain instead of chunked baked tiles:
        // no bake pass, no tile seams, one draw call. False falls back to the old path.
        public bool usePolylineActiveTrail = true;
        private const int MaxSpinePoints = 200; // must match MAX_SPINE_POINTS in SmokeVolume.shader
        // Ceiling on the ground cloud's share, leaving the rest for the trail.
        private const int MaxGroundSpinePoints = 70;

        private struct Puff
        {
            public Vector3 localPos;
            public Vector3 velocity;
            public float age;
            public float sizeMultiplier;
            // Ground impingement is a ONE-OFF event, not a per-frame force. Otherwise a
            // puff resting on the ground re-triggers the bounce every frame and the kicks
            // accumulate - survivable while the direction was random and they cancelled,
            // but once the outflow had a consistent direction they compounded and the
            // ground smoke shot off the pad in one diagonal streak. Penetration is still
            // resolved every frame; only the velocity kick is gated.
            public bool bounced;
            // Ground-cloud puff, kept out of the trail's chain. The chain is walked in
            // spawn order, and both are emitted at once, so interleaving them makes it
            // jump between the vessel and the pad on alternate links - a thin spike
            // running the length of the flight.
            public bool isGround;
            // Which continuous burst of emission this puff belongs to. Incremented by the
            // controller whenever spawning resumes after a pause, so a chain break is an
            // exact fact rather than something inferred from geometry.
            public int burnId;
            // Monotonic, assigned once. Spine selection uses THIS, not list position:
            // puffs expire off the front, so an index-based rule reshuffles which puffs
            // are picked every frame, which is what made the texture flash.
            public int spawnIndex;
            public bool markedForRemoval;
        }

        private readonly List<Puff> puffs = new List<Puff>();

        private float startSize;
        private float maxSize;
        private float growthSharpness;
        private float growthTime;
        private float lifeTime;
        private int maxPuffs;

        private float buoyancySpeed;
        // Buoyancy is removed rather than tuned down. Puffs of different ages rising at
        // different rates bunch up unevenly along the spine, which shows as radius lobes
        // once interpolated. buoyancySpeed is still passed in but no longer read.
        private Vector3 windDrift;
        private CelestialBody body;
        private float fadeStartAltitude;
        private float fadeEndAltitude;
        // Upward component of a bounce. Raising it fires the ground cloud into the air
        // instead of spreading it, so the energy goes sideways.
        private const float GroundBounceDamping = 0f;

        // Slowly decaying ejection velocity lets puffs drift apart for seconds after
        // spawn - close when laid down, wide gaps once decimation thins the trail.
        private const float VelocityConvergeRate = 0.55f;

        // Ground outflow decelerates far harder than drifting trail smoke: it leaves the
        // deflector near exhaust speed and is braked fast by the air it drags in, so it
        // reads as a violent throw that settles into slow billowing.
        //
        // Reach is speed / rate, so the two must be raised together - a big speed alone
        // sails off the pad, a big rate alone stops it dead.
        private const float GroundVelocityConvergeRate = 1.1f;
        // Residual drift once the throw has decayed - small, but over a long life it is
        // what keeps the cloud expanding rather than sitting still.
        private const float GroundCreepSpeed = 3.5f;
        // metres of radius per second, after growthTime
        private const float ContinuedGrowthRate = 0.35f;
        private const float GroundGrowthBoost = 3f;

        private const float BoxRadiusMarginMultiplier = 2.9f; // must exceed BlendRadiusMultiplier or the box clips density
        // Derived from the values the shader actually displaces by, not a constant. A
        // hand-matched constant is a trap once the warp strength is a live slider: raise
        // it and the AABB stops containing the geometry, so lumps get sliced flat at the
        // box wall.
        private static float BoxWarpMargin
        {
            get
            {
                return (SmokeTuning.SilhouetteWarpStrength + SmokeTuning.VortexStrength) * 1.733f;
            }
        }

        // Depth bias is retired. It existed to shove the volume's box in front of the
        // launchpad so the hardware depth test wouldn't kill the fragment, but a uniform
        // bias cannot distinguish the pad (should not occlude) from terrain (should), so
        // any value that beat the pad also made smoke show through hillsides. The shader
        // now clips the raymarch against _CameraDepthTexture instead, which handles both
        // correctly, so the bias is zero everywhere. Kept as constants rather than ripped
        // out so the shader path stays intact if this ever needs revisiting.
        private const float ActiveDepthBiasDistance = 0f;
        private const float ActiveDepthBiasFraction = 0f;


        private class ActiveTile
        {
            public GameObject obj;
            public MeshRenderer renderer;
            public MaterialPropertyBlock propertyBlock;
            public RenderTexture densityTex;
            public RenderTexture blurredTex;
            public Vector3Int resolution;
        }

        // adaptive tile resolution: pick the smallest tier whose voxel size stays
        // near TargetVoxelSize for this tile's actual box - a tile spanning only
        // young, tightly-spaced puffs gets a cheap small texture, a tile spanning
        // big old puffs (bigger box) gets a bigger one. fixes both problems at once:
        // most tiles (small boxes) get cheaper than the old flat 32^3, and the rare
        // big-box tiles get sharper edges instead of the same fixed resolution
        private static readonly int[] ActiveResolutionTiers = { 16, 24, 32, 40 };
        private const float TargetVoxelSize = 1.25f;

        private static Vector3Int ChooseActiveResolution(Vector3 boxSize)
        {
            float maxDim = Mathf.Max(boxSize.x, Mathf.Max(boxSize.y, boxSize.z));
            int needed = Mathf.CeilToInt(maxDim / TargetVoxelSize);
            int chosen = ActiveResolutionTiers[ActiveResolutionTiers.Length - 1];
            for (int i = 0; i < ActiveResolutionTiers.Length; i++)
            {
                if (needed <= ActiveResolutionTiers[i]) { chosen = ActiveResolutionTiers[i]; break; }
            }
            return new Vector3Int(chosen, chosen, chosen);
        }

        private static void EnsureTileResolution(ActiveTile tile, Vector3Int desired)
        {
            if (tile.resolution == desired) return;
            if (tile.densityTex != null) tile.densityTex.Release();
            if (tile.blurredTex != null) tile.blurredTex.Release();
            tile.densityTex = CreateDensityTexture(desired);
            tile.blurredTex = CreateDensityTexture(desired);
            tile.resolution = desired;
        }

        private readonly List<ActiveTile> activeTiles = new List<ActiveTile>();
        private readonly Vector4[] tileCentersBuffer = new Vector4[MaxTextureLayerPuffs];
        private readonly float[] tileRadiiBuffer = new float[MaxTextureLayerPuffs];

        private MeshRenderer polylineRenderer;
        private MaterialPropertyBlock polylinePropertyBlock;
        private readonly Vector4[] spinePointsBuffer = new Vector4[MaxSpinePoints];
        private readonly float[] spineRadiiBuffer = new float[MaxSpinePoints];
        private readonly List<Vector3> polylineThinnedPos = new List<Vector3>();
        private readonly List<float> polylineThinnedRadius = new List<float>();

        private const int SpineSmoothPasses = 5;
        private readonly Vector3[] smoothedSpine = new Vector3[MaxSpinePoints];
        private readonly Vector3[] smoothScratch = new Vector3[MaxSpinePoints];
        private readonly float[] smoothedRadii = new float[MaxSpinePoints];
        private readonly float[] radiiScratch = new float[MaxSpinePoints];

        // Bounding spheres over runs of SpineGroupSize segments, for the shader's
        // two-level cull - see the note by MAX_SPINE_GROUPS in SmokeVolume.shader.
        // Must match SPINE_GROUP_SIZE / MAX_SPINE_GROUPS there.
        // Padding on top of the vortex displacement itself. The margin has to follow the
        // live strength, or raising the slider shrinks the cull spheres below what the
        // warp needs and chunks of trail vanish depending on view angle.
        private const float VortexWarpMargin = 1.5f;
        private const int SpineGroupSize = 16;
        private const int MaxSpineGroups = (MaxSpinePoints + SpineGroupSize - 1) / SpineGroupSize;
        private readonly Vector4[] spineGroupBounds = new Vector4[MaxSpineGroups];

        // Highest thinning level this trail has ever needed. Only ever rises while the
        // trail is alive, so a spine point that survives one frame keeps surviving.
        private int committedTrailStride = 1;

        // How much the column's width may wander, as a fraction. See ColumnWander.
        private const float ColumnWanderAmount = 0.30f;

        private readonly List<Vector3> activeOrderedPos = new List<Vector3>();
        private readonly List<float> activeOrderedRadius = new List<float>();

        private int splatKernel = -1;
        private int blurKernel = -1;

        public bool HasActivePuffs => puffs.Count > 0;

        // Called while the mod is toggled off. Freezes puffs in place and hides every
        // renderer; cheap to call every frame.
        public void HideAll()
        {
            if (polylineRenderer != null) SmokeRenderRegistry.SetActive(polylineRenderer, false);
            for (int i = 0; i < activeTiles.Count; i++) SmokeRenderRegistry.SetActive(activeTiles[i].renderer, false);
        }

        private static int activeInstanceCount;
        public static bool AnyActive => activeInstanceCount > 0;

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        // Floating-origin compensation for the noise field. Puff positions are stored
        // body-relative so the smoke never drifts, but noise is sampled at world position
        // - and KSP shifts the world origin as the vessel travels, so the noise stays
        // pinned to the world and slides through the smoke.
        //
        // Sending body-relative coordinates would mean ~600km numbers, which eats float
        // precision in the hash. Instead anchor one body-fixed point and publish its
        // displacement; the shader subtracts it, keeping the numbers small.
        private static bool noiseAnchorSet;
        // Valid for as long as we are at the same body. Keying it on instance count was
        // wrong: groups come and go constantly (staging, a relight), so the count dips to
        // zero mid-flight, the anchor is re-derived from a different puff, and the whole
        // noise field shifts at once.
        private static CelestialBody noiseAnchorBody;
        private static Vector3 noiseAnchorLocal;
        private static Vector3 noiseAnchorWorld0;

        private void PublishNoiseOffset()
        {
            if (body == null || body.bodyTransform == null) return;
            // Needs a real smoke position. This object's transform is never positioned -
            // only its renderer children are - so it reads (0,0,0), which is the world
            // origin and exactly the anchor that broke this. Puff positions are real.
            if (noiseAnchorBody != body)
            {
                noiseAnchorSet = false;
                noiseAnchorBody = body;
            }

            if (!noiseAnchorSet && puffs.Count > 0)
            {
                // Anchor NEAR THE SMOKE. Anchoring world origin puts the point ~600km from
                // the body centre, and the body rotates, so it sweeps a huge arc (~17 m/s
                // at Kerbin's radius). The published shift then grows without bound and the
                // noise hash loses all precision - the volume renders as pure stipple.
                noiseAnchorLocal = puffs[0].localPos;
                noiseAnchorWorld0 = LocalToWorld(noiseAnchorLocal);
                noiseAnchorSet = true;
            }
            if (!noiseAnchorSet)
            {
                // No puffs yet, so there is nothing to anchor to. Publish zero rather
                // than leaving the global at whatever a previous scene left behind.
                Shader.SetGlobalVector("_SmokeNoiseOffset", Vector4.zero);
                return;
            }
            Vector3 shift = LocalToWorld(noiseAnchorLocal) - noiseAnchorWorld0;
            Shader.SetGlobalVector("_SmokeNoiseOffset", new Vector4(shift.x, shift.y, shift.z, 0f));
        }

        private Vector3 WorldToLocal(Vector3 worldPos) => body.bodyTransform.InverseTransformPoint(worldPos);
        private Vector3 LocalToWorld(Vector3 localPos) => body.bodyTransform.TransformPoint(localPos);

        // Object.Destroy(collider) is deferred to end of frame, disable it now instead
        private static void RemoveColliderImmediate(GameObject obj)
        {
            Collider collider = obj.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                Object.Destroy(collider);
            }
        }

        public void Initialize(
            float startSize, float maxSize, float growthSharpness, float growthTime, float lifeTime, int maxPuffs,
            float buoyancySpeed, Vector3 windDrift, CelestialBody body,
            float fadeStartAltitude, float fadeEndAltitude)
        {
            this.startSize = startSize;
            this.maxSize = maxSize;
            this.growthSharpness = growthSharpness;
            this.growthTime = growthTime;
            this.lifeTime = lifeTime;
            this.maxPuffs = maxPuffs;
            this.buoyancySpeed = buoyancySpeed;
            this.windDrift = windDrift;
            this.body = body;
            this.fadeStartAltitude = fadeStartAltitude;
            this.fadeEndAltitude = fadeEndAltitude;

            activeInstanceCount++;

            GameObject polylineObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            polylineObj.name = "PolylineActiveTrail";
            polylineObj.transform.SetParent(transform, false);
            RemoveColliderImmediate(polylineObj);
            polylineRenderer = polylineObj.GetComponent<MeshRenderer>();
            polylineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            polylineRenderer.enabled = false; // drawn only via the half-res command buffer
            polylineRenderer.receiveShadows = false;
            SmokeRenderRegistry.SetActive(polylineRenderer, false);
            polylinePropertyBlock = new MaterialPropertyBlock();

            if (ShaderCache.SmokeVolumeShader != null)
            {
                Material polylineMat = new Material(ShaderCache.SmokeVolumeShader);
                polylineMat.EnableKeyword("SMOKE_VOLUME_POLYLINE");
                // Always. The fragment shader clips the march against _CameraDepthTexture,
                // so disabling the hardware test is what lets that clip see the whole ray
                // instead of losing fragments whose box back face was occluded. This
                // correlated with the trail zigzag once, so it is the first suspect if
                // that returns.
                polylineMat.SetFloat("_ZTestMode", 8f); // CompareFunction.Always
                polylineRenderer.material = polylineMat;
            }

            if (ShaderCache.SmokeVolumeSplatCompute != null)
            {
                splatKernel = ShaderCache.SmokeVolumeSplatCompute.FindKernel("Splat");
                blurKernel = ShaderCache.SmokeVolumeSplatCompute.FindKernel("Blur");
            }
            else
            {
                Debug.LogWarning("[HairyBlob] SmokeVolumeSplatCompute is null while creating a SmokeVolumeGroup.");
            }
        }

        private static RenderTexture CreateDensityTexture(Vector3Int resolution)
        {
            RenderTexture tex = new RenderTexture(resolution.x, resolution.y, 0, RenderTextureFormat.RHalf)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth = resolution.z,
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            tex.Create();
            return tex;
        }

        private const float PositionJitterFraction = 0.25f;

        // live tip tracks the engine every physics tick, bridges the gap until the next puff spawns
        private Vector3 liveTipPos;
        private float liveTipRadius;
        private bool hasLiveTip;
        private float lastActiveDebugLogTime = -999f;
        // Off by default, same reasoning as LaunchSmokeController.debugLogging.
        // Per-bounce spam and the pad survey dump. The survey itself still runs either
        // way - it derives the outflow axis - this only gates the logging.
        private const bool debugBounceLogging = true;   // TEMP: on to verify the trench axis. OFF before tagging.
        private float lastBounceLogTime = -999f;

        public void SetLiveTip(Vector3 worldPos, float radius)
        {
            liveTipPos = worldPos;
            liveTipRadius = radius;
            hasLiveTip = true;
        }

        public void ClearLiveTip()
        {
            hasLiveTip = false;
        }

        // Ground cloud, emitted where the exhaust lands rather than produced by puffs
        // falling and bouncing. Bouncing could never sustain it: puffs spawn below the
        // engine, so once the vessel clears ~24m nothing reaches the ground and the cloud
        // stops growing a second after liftoff, while real exhaust keeps striking the
        // deflector for many seconds.
        //
        // Velocity is purely horizontal - the point is that it spreads, not lifts.
        public void AddGroundPuff(Vector3 impactPos, Vector3 up, float outwardSpeed, float sizeScale)
        {
            Vector3 dir;
            if (padAxisValid)
            {
                // exhaust hitting a deflector splits either way from one point
                dir = padOutflowAxis * (Random.value < 0.5f ? -1f : 1f);
                Vector3 jitter = Vector3.ProjectOnPlane(Random.onUnitSphere, up);
                dir = Vector3.Normalize(dir + jitter * GroundOutflowSpread);
            }
            else
            {
                dir = Vector3.ProjectOnPlane(Random.onUnitSphere, up).normalized;
            }

            Puff p = new Puff
            {
                localPos = WorldToLocal(impactPos + up * 1.5f),
                velocity = dir * outwardSpeed,
                age = 0f,
                sizeMultiplier = sizeScale,
                bounced = true,  // already on the ground; no impingement impulse wanted
                isGround = true
            };
            puffs.Add(p);
        }

        // Slow wander in the column's thickness, so the trail has wide and narrow
        // stretches instead of tapering as an even cone. More per-puff randomness cannot
        // do this: that is high frequency, so neighbours disagree and the chain beads -
        // and SmoothRadii, which exists to remove beading, removes the variety with it.
        //
        // Low frequency is what smoothing leaves alone. Two sines at an irrational ratio
        // never repeat, with a period of tens of puffs: a shape, not a wobble.
        private static float ColumnWander(int spawnIndex)
        {
            float t = spawnIndex;
            float w = Mathf.Sin(t * 0.061f) * 0.6f + Mathf.Sin(t * 0.0233f + 1.7f) * 0.4f;
            return 1f + w * ColumnWanderAmount;
        }

        public void AddPuff(Vector3 worldPos, Vector3 initialVelocity, float sizeScale = 1f, int burnId = 0)
        {
            Vector3 jitter = Random.insideUnitSphere * (startSize * PositionJitterFraction);
            puffs.Add(new Puff
            {
                localPos = WorldToLocal(worldPos + jitter),
                velocity = initialVelocity,
                age = 0f,
                sizeMultiplier = Random.Range(0.9f, 1.1f) * ColumnWander(nextSpawnIndex) * sizeScale,
                burnId = burnId,
                spawnIndex = nextSpawnIndex++
            });
        }

        public void Tick(float dt)
        {
            if (dt <= 0f) return;
            if (body == null) return;

            PublishNoiseOffset();

            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                Puff p = puffs[i];
                p.age += dt;

                if (p.age >= lifeTime)
                {
                    p.markedForRemoval = true;
                    puffs[i] = p;
                    continue;
                }

                Vector3 worldPos = LocalToWorld(p.localPos);
                Vector3 up = (worldPos - body.position).normalized;

                // Flattened against local up. windDrift is world-space, and world axes are
                // not horizontal on a globe, so it carries a stray vertical component and
                // the whole trail slowly climbs - 1 m/s of it is 150m over a full life.
                Vector3 target = Vector3.ProjectOnPlane(windDrift, up);
                // Ground puffs never fully park - a slow outward creep keeps the cloud
                // spreading after the initial throw has bled off.
                if (p.isGround)
                {
                    Vector3 outward = Vector3.ProjectOnPlane(worldPos - padCentre, up);
                    if (outward.sqrMagnitude > 1f) target += outward.normalized * GroundCreepSpeed;
                }
                float convergeRate = p.isGround ? GroundVelocityConvergeRate : VelocityConvergeRate;
                p.velocity = Vector3.Lerp(p.velocity, target, dt * convergeRate);
                Vector3 newWorldPos = worldPos + p.velocity * dt;

                TryBounceOffGround(ref newWorldPos, ref p.velocity, up, ref p.bounced, p.age, dt);

                if (!IsFinite(newWorldPos) || !IsFinite(p.velocity))
                {
                    Debug.LogWarning(string.Format(
                        "[HairyBlob] dropping puff with invalid position/velocity (NaN or Inf). pos={0} vel={1} age={2:F1}",
                        newWorldPos, p.velocity, p.age));
                    p.markedForRemoval = true;
                    puffs[i] = p;
                    continue;
                }

                p.localPos = WorldToLocal(newWorldPos);
                puffs[i] = p;
            }

            DecimateActiveTrail();
            puffs.RemoveAll(p => p.markedForRemoval);

            EnforcePuffBudget();


            BuildActiveOrderedList();
            if (usePolylineActiveTrail)
            {
                UpdatePolylineVolume();
                for (int i = 0; i < activeTiles.Count; i++) SmokeRenderRegistry.SetActive(activeTiles[i].renderer, false);
            }
            else
            {
                UpdateActiveTiles();
                SmokeRenderRegistry.SetActive(polylineRenderer, false);
            }
        }

        private void EnforcePuffBudget()
        {
            // One pool - the oldest puffs go first when the budget is exceeded.
            int over = puffs.Count - maxPuffs;
            if (over <= 0) return;
            puffs.RemoveRange(0, Mathf.Min(over, puffs.Count));
        }

        // spacing was tight to keep small puffs continuous - once they grow, that
        // density is redundant, so drop puffs that are closer than a fraction of
        // the current (grown) neighbor radius. keeps the trail full at every stage
        // instead of costing full puff count once everything is big.
        // Lowered because the threshold scales with puff RADIUS, and growthTime kept
        // shrinking (40s -> 12s -> 8s). Each of those made the spine sparser, which
        // shows up twice over: individual capsules read as transverse bands, and
        // residual jitter between distant points bends into a visible loop. Denser
        // spine costs shader time in the O(segments) loop, which half-res now affords.
        private const float DecimateSpacingFraction = 0.20f;
        private const float DecimateMinAge = 1.0f;

        private void DecimateActiveTrail()
        {
            // Thin hard enough that the spine never has to be RESAMPLED.
            //
            // Even-stride resampling recomputed the stride from a list that grows every
            // frame, so the 200 chosen points landed on a different subset each time. The
            // shape stayed put, but every point hopped to a neighbouring puff and took its
            // radius with it - and radius drives noise frequency. That is the texture
            // jumping in place while the shape does not move.
            //
            // Keeping the live list at or below the cap removes the resampling: a puff
            // holds its position for life and is only ever dropped, never re-picked.
            // Counts ground puffs too - they share the spine array, so excluding them
            // lets the combined total climb back over the cap and resampling resumes.
            int liveCount = 0;
            for (int i = 0; i < puffs.Count; i++)
            {
                if (!puffs[i].markedForRemoval) liveCount++;
            }
            float pressure = Mathf.Max(1f, liveCount / (float)MaxSpinePoints);

            Vector3 anchorPos = Vector3.zero;
            float anchorRadius = 0f;
            bool hasAnchor = false;

            for (int i = 0; i < puffs.Count; i++)
            {
                Puff p = puffs[i];
                // Don't reset the anchor here: an already-removed puff must not let the
                // next survivor skip its spacing check, or it escapes decimation for good
                // and freezes a gap that is never re-evaluated as radii grow.
                if (p.markedForRemoval) { continue; }

                Vector3 worldPos = LocalToWorld(p.localPos);

                if (hasAnchor && p.age >= DecimateMinAge)
                {
                    // No pressure multiplier. Scaling by liveCount/MaxSpinePoints made
                    // decimation savage at long lifetimes - the multiplier passed 10, puffs
                    // were culled two radii apart, and the sparse trail read as "spawning
                    // slowly" even though spawning was fine. The cap is handled at upload.
                    float spacingLimit = anchorRadius * DecimateSpacingFraction;
                    if (Vector3.Distance(worldPos, anchorPos) < spacingLimit)
                    {
                        p.markedForRemoval = true;
                        puffs[i] = p;
                        continue;
                    }
                }

                anchorPos = worldPos;
                anchorRadius = SizeForPuff(p);
                hasAnchor = true;
            }
        }

        // Against a ~30 m/s impact this multiplies straight into outward speed, so a
        // value above 1 throws puffs ~100m before convergence reins them in.
        private const float GroundSpreadFactor = 0.8f;
        // how much random spread is mixed into the trench outflow direction; 0 gives two
        // razor-thin streams, 1 washes the axis out back to a splash
        // Wider cone. With only a couple of hundred spine points to spend, a big cloud
        // has to come from spread and size rather than from sheer count.
        private const float GroundOutflowSpread = 0.6f;
        // Fraction of GroundSpreadFactor used once the direction is coherent rather than
        // random. Random kicks partly cancel; aligned ones do not, so they need to be far
        // gentler to travel a similar distance.
        private const float DirectedSpreadScale = 0.25f;

        // Must exceed offsetDistance plus the engine's own height above the pad, or the
        // downward raycast from a puff's spawn position never reaches the ground and the
        // bounce never fires at all - a full test flight logged zero bounces at 6m.
        private const float BuildingRaycastDistance = 60f;
        // How far below a surface a puff may be and still count as resting on it. Caps
        // the upward correction too, so nothing is ever teleported.
        private const float GroundContactTolerance = 2f;
        private static int sceneryCollisionMask = -1;
        private static readonly RaycastHit[] groundRaycastBuffer = new RaycastHit[8];

        // exposed so LaunchSmokeController can trace the exhaust against the same set of
        // surfaces this class already treats as ground
        public static int GetSceneryCollisionMaskPublic() { return GetSceneryCollisionMask(); }

        private static int GetSceneryCollisionMask()
        {
            if (sceneryCollisionMask == -1)
            {
                int mask = 0;
                string[] layerNames = { "Local Scenery", "Default", "TerrainColliders" };
                foreach (string layerName in layerNames)
                {
                    int layer = LayerMask.NameToLayer(layerName);
                    if (layer >= 0) mask |= 1 << layer;
                }
                sceneryCollisionMask = mask;
            }
            return sceneryCollisionMask;
        }

        // ground via PQS terrain height, plus a short raycast against scenery
        // colliders (launchpad, buildings) since those aren't part of the PQS mesh
        private static bool padSurveyDone;

        // Outflow axis derived from the pad's own slanted geometry - see SurveyPad.
        public static bool PadAxisValid { get { return padAxisValid; } }
        public static Vector3 PadOutflowAxis { get { return padOutflowAxis; } }
        // Which of the pad's horizontal axes the trench runs along. Flip if the smoke
        // pours across the trench instead of along it - the log prints both.
        public static bool PadTrenchAlongForward = false;
        private static bool padAxisValid;
        private static Vector3 padOutflowAxis;
        private static Vector3 padCentre;

        // Sweeps a grid of downward rays around the pad and derives one horizontal axis
        // for ground outflow from the slanted surfaces it finds.
        //
        // Reflecting each puff off its own hit normal cannot work: the pad does have
        // slanted colliders, but every measured bounce landed on surfaces under 5 degrees
        // - terrain and the flat deck. The geometry has to be read where it IS, not where
        // the smoke happens to land, which is what this does once at first contact.
        //
        // Opposed downhill directions would cancel if averaged, so they are folded onto a
        // common half-space first. That gives the trench AXIS; which way along it a puff
        // goes is decided per-puff, so the flow splits into two streams.
        private static void SurveyPad(Vector3 centre, Vector3 up)
        {
            Vector3 a = Vector3.Normalize(Vector3.Cross(up, Mathf.Abs(Vector3.Dot(up, Vector3.right)) > 0.9f
                ? Vector3.forward : Vector3.right));
            Vector3 b = Vector3.Cross(up, a);

            var seen = new System.Collections.Generic.Dictionary<string, string>();
            RaycastHit[] buf = new RaycastHit[16];

            Vector3 axisSum = Vector3.zero;
            Vector3 axisRef = Vector3.zero;
            int slantedCount = 0;
            Transform padTransform = null;
            const float SlantedMinDegrees = 15f;

            const int Half = 4;          // 9x9 grid
            const float Spacing = 6f;    // +/- 24m around the puff
            for (int ix = -Half; ix <= Half; ix++)
            {
                for (int iz = -Half; iz <= Half; iz++)
                {
                    Vector3 origin = centre + a * (ix * Spacing) + b * (iz * Spacing) + up * 80f;
                    int n = Physics.RaycastNonAlloc(origin, -up, buf, 200f,
                        GetSceneryCollisionMask(), QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < n; i++)
                    {
                        if (buf[i].collider.GetComponentInParent<Part>() != null) continue;
                        string name = buf[i].collider.gameObject.name;
                        if (seen.ContainsKey(name)) continue;
                        float tilt = Vector3.Angle(buf[i].normal, up);
                        seen[name] = string.Format("layer={0} tilt={1:F1}deg",
                            LayerMask.LayerToName(buf[i].collider.gameObject.layer), tilt);

                        // Remember the pad itself. Normals are a dead end: the trench is a
                        // solid block in physics, so a downward ray lands on the flat lid
                        // and never sees the deflector's face, and the slanted hits that do
                        // come back are outer ramps pointing wherever the model's edges
                        // face. The pad is an oriented object and the trench runs along one
                        // of its own axes, so its transform is the reliable source.
                        if (padTransform == null)
                        {
                            string lower = name.ToLowerInvariant();
                            if (lower.Contains("launch") && lower.Contains("pad"))
                            {
                                padTransform = buf[i].collider.transform;
                            }
                        }

                        if (tilt < SlantedMinDegrees) continue;
                        Vector3 downhill = Vector3.ProjectOnPlane(-buf[i].normal, up);
                        if (downhill.sqrMagnitude < 1e-6f) continue;
                        downhill.Normalize();
                        if (slantedCount == 0) axisRef = downhill;
                        // fold onto one half-space so opposed sides reinforce the axis
                        // instead of cancelling
                        axisSum += downhill * Mathf.Sign(Vector3.Dot(downhill, axisRef));
                        slantedCount++;
                    }
                }
            }

            padCentre = centre;
            padAxisValid = false;

            // Accepting any slanted hit is too loose - one run derived the axis from three
            // samples, one of them a fuel tank wall. Two extra conditions:
            //
            // Enough samples, since a real trench presents a lot of slanted surface to a
            // grid sweep and incidental scenery presents very little. And COHERENCE: a
            // genuine trench gives directions that agree, so the summed vector stays nearly
            // as long as the count, while scattered scenery normals partly cancel. The
            // ratio separates the two without needing to know any object names.
            // Preferred source: the pad's own orientation.
            if (padTransform != null)
            {
                // A trench is longer than it is wide, so measure the model and take the
                // longer side. MeshCollider bounds are LOCAL, which is what we want - the
                // world AABB is axis-aligned and would lose the pad's orientation.
                bool alongForward = PadTrenchAlongForward;
                MeshCollider mc = padTransform.GetComponent<MeshCollider>();
                if (mc != null && mc.sharedMesh != null)
                {
                    Vector3 size = mc.sharedMesh.bounds.size;
                    alongForward = size.z > size.x;
                    if (debugBounceLogging)
                    {
                        Debug.Log(string.Format(
                            "[HairyBlob] PAD SURVEY: pad local size {0} -> trench along {1}",
                            size.ToString("F0"), alongForward ? "FORWARD (z)" : "RIGHT (x)"));
                    }
                }

                Vector3 padAxis = Vector3.ProjectOnPlane(
                    alongForward ? padTransform.forward : padTransform.right, up);
                if (padAxis.sqrMagnitude > 1e-6f)
                {
                    padOutflowAxis = padAxis.normalized;
                    padAxisValid = true;
                    if (debugBounceLogging)
                    {
                        Debug.Log(string.Format(
                            "[HairyBlob] PAD SURVEY: using pad transform '{0}' -> axis {1}  "
                            + "(right={2} forward={3})",
                            padTransform.name, padOutflowAxis.ToString("F2"),
                            Vector3.ProjectOnPlane(padTransform.right, up).normalized.ToString("F2"),
                            Vector3.ProjectOnPlane(padTransform.forward, up).normalized.ToString("F2")));
                    }
                    return;
                }
            }

            // Fallback: derive it from slanted normals. Weak, for the same reason as
            // above, but better than nothing off-KSC or on a modded pad.
            const int MinSlantedSamples = 4;
            const float MinAxisCoherence = 0.6f;
            if (slantedCount >= MinSlantedSamples)
            {
                float coherence = axisSum.magnitude / slantedCount;
                Vector3 horizontal = Vector3.ProjectOnPlane(axisSum, up);
                if (coherence >= MinAxisCoherence && horizontal.sqrMagnitude > 1e-6f)
                {
                    padOutflowAxis = horizontal.normalized;
                    padAxisValid = true;
                }
                if (debugBounceLogging)
                {
                    Debug.Log(string.Format(
                        "[HairyBlob] PAD SURVEY: coherence={0:F2} (need {1:F2}), samples={2} (need {3})",
                        coherence, MinAxisCoherence, slantedCount, MinSlantedSamples));
                }
            }

            if (debugBounceLogging)
            {
                Debug.Log("[HairyBlob] PAD SURVEY: " + seen.Count + " distinct colliders under/around the pad");
                foreach (var kv in seen)
                {
                    Debug.Log("[HairyBlob]   '" + kv.Key + "' " + kv.Value);
                }
                Debug.Log(string.Format("[HairyBlob] PAD SURVEY: {0} slanted samples (>{1:F0}deg), outflow axis {2}",
                    slantedCount, SlantedMinDegrees, padAxisValid ? padOutflowAxis.ToString("F2") : "NONE - falling back to radial"));
            }
        }

        private void TryBounceOffGround(ref Vector3 worldPos, ref Vector3 velocity, Vector3 up, ref bool bounced, float age, float dt)
        {
            double altitude = body.GetAltitude(worldPos);
            if (altitude > 500.0 || altitude < -500.0) return;

            bool hit = false;
            float penetration = 0f;

            if (body.pqsController != null)
            {
                Vector3d radialDir = ((Vector3d)worldPos - body.position).normalized;
                double terrainRadius = body.pqsController.GetSurfaceHeight(radialDir);
                double groundAltitude = terrainRadius - body.Radius;

                const float buffer = 1.0f;
                if (altitude < groundAltitude + buffer)
                {
                    penetration = (float)(groundAltitude + buffer - altitude);
                    hit = true;
                }
            }

            RaycastHit rayHit = default;
            bool rayHitReal = false;
            // Scan every hit, not just the closest. The rocket's own hull and clamps
            // often sit between a puff and the ground, and a single Raycast reports only
            // the nearest - hitting a Part then rejected the whole check.
            int hitCount = Physics.RaycastNonAlloc(worldPos + up * BuildingRaycastDistance, -up,
                groundRaycastBuffer, BuildingRaycastDistance * 2f, GetSceneryCollisionMask(), QueryTriggerInteraction.Ignore);
            // Nearest surface AT OR BELOW the puff, not nearest overall. The ray starts
            // above the puff, so a closer hit is a surface ABOVE it - and puffs spawn
            // under the pad deck, in the trench. Treating that deck as ground the puff had
            // sunk through shoved brand-new puffs 15m straight up. The tolerance still
            // allows genuine shallow penetration.
            float bestHitDist = float.MaxValue;
            int bestHitIndex = -1;
            for (int hi = 0; hi < hitCount; hi++)
            {
                if (groundRaycastBuffer[hi].collider.GetComponentInParent<Part>() != null) continue;
                float d = groundRaycastBuffer[hi].distance;
                if (d < BuildingRaycastDistance - GroundContactTolerance) continue;
                if (d < bestHitDist)
                {
                    bestHitDist = d;
                    bestHitIndex = hi;
                }
            }

            if (bestHitIndex >= 0)
            {
                rayHit = groundRaycastBuffer[bestHitIndex];
                float buildingPenetration = BuildingRaycastDistance - rayHit.distance + 1.0f;
                if (buildingPenetration > penetration)
                {
                    penetration = buildingPenetration;
                    hit = true;
                    rayHitReal = true;
                }
            }

            if (hit)
            {
                // One-shot survey of what is under the pad. The per-bounce log only
                // reports surfaces a puff happened to reach, so it can miss the deflector
                // entirely; this sweeps a grid regardless.
                if (!padSurveyDone)
                {
                    padSurveyDone = true;
                    SurveyPad(worldPos, up);
                }

                worldPos += up * penetration;

                float verticalSpeed = Vector3.Dot(velocity, up);
                if (verticalSpeed < 0f)
                {
                    // CONSTRAINT - every frame. Cancelling motion into the ground only
                    // removes energy, so it is safe to keep applying; gating it would let
                    // a puff push downward forever against the position correction.
                    velocity -= up * verticalSpeed;

                    // IMPULSE - once per puff. See Puff.bounced.
                    if (!bounced)
                    {
                    if (debugBounceLogging && Time.time - lastBounceLogTime > 0.25f)
                    {
                        lastBounceLogTime = Time.time;
                        string what = rayHitReal
                            ? "raycast '" + rayHit.collider.gameObject.name + "' layer=" + LayerMask.LayerToName(rayHit.collider.gameObject.layer)
                            : "PQS terrain";

                        // How far the hit surface tilts from straight up. Zero everywhere
                        // means a flat deck with nothing to reflect off; a consistent tilt
                        // means the deflector is really there in physics.
                        string normalInfo = "n/a";
                        if (rayHitReal)
                        {
                            float tilt = Vector3.Angle(rayHit.normal, up);
                            Vector3 downhill = Vector3.ProjectOnPlane(-rayHit.normal, up);
                            normalInfo = string.Format("tilt={0:F1}deg downhill={1}",
                                tilt, downhill.sqrMagnitude > 1e-6f ? downhill.normalized.ToString("F2") : "flat");
                        }

                        Debug.Log(string.Format(
                            "[HairyBlob] bounce: alt={0:F1} age={1:F2} vSpeed={2:F1} penetration={3:F2} hit={4} {5}",
                            altitude, age, verticalSpeed, penetration, what, normalInfo));
                    }

                    // No upward component - ground smoke should hug the ground and spread
                    // sideways, not lift. Kept at zero rather than deleted so the knob
                    // stays discoverable.
                    velocity += up * (-verticalSpeed * GroundBounceDamping);

                    // Direction of ground outflow. A random horizontal direction per puff
                    // is a circular distribution by construction, so it can only make a
                    // symmetric splash - never the two opposed streams a trench makes.
                    // SurveyPad supplies an axis instead, with a little randomness so the
                    // streams are not knife-edge thin.
                    Vector3 outward;
                    if (padAxisValid)
                    {
                        // Coin flip, not position. Every puff spawns at essentially the
                        // same point under the engine, so a position-based side gave nearly
                        // all of them the same sign and the two streams collapsed into one
                        // jet. Real exhaust does split both ways from one impingement point.
                        float sign = Random.value < 0.5f ? -1f : 1f;
                        Vector3 along = padOutflowAxis * sign;
                        Vector3 jitter = Vector3.ProjectOnPlane(Random.onUnitSphere, up);
                        outward = Vector3.Normalize(along + jitter * GroundOutflowSpread);
                    }
                    else
                    {
                        // no slanted geometry found (not at KSC, or a modded pad) - keep
                        // the old behaviour rather than inventing a direction
                        outward = Vector3.Cross(up, Random.onUnitSphere).normalized;
                    }
                        // A coherent direction needs a much smaller magnitude than a random
                        // one: random kicks partly cancel, aligned ones accumulate and throw
                        // the whole cloud off the pad.
                        float spread = padAxisValid ? GroundSpreadFactor * DirectedSpreadScale
                                                    : GroundSpreadFactor;
                        velocity += outward * (-verticalSpeed * spread);
                        bounced = true;
                    }
                }
            }
        }

        private float SizeForPuff(Puff p)
        {
            // Growth is timed independently of lifeTime. Tying the two together means a
            // puff only reaches full size at the end of its life, by which point the
            // rocket is kilometres away and the visible trail is all young thin puffs.
            float t = Mathf.Clamp01(p.age / growthTime);
            float eased = 1f - Mathf.Pow(1f - t, growthSharpness);
            float grown = Mathf.Lerp(startSize, maxSize, eased);

            // Growth does not stop at growthTime - real smoke keeps expanding as it
            // entrains air, and a fixed size afterwards is what made the trail read as a
            // constant-width tube. Slow and linear, so young smoke still billows fastest.
            if (p.age > growthTime)
            {
                grown += (p.age - growthTime) * ContinuedGrowthRate * (p.isGround ? GroundGrowthBoost : 1f);
            }
            return grown * p.sizeMultiplier;
        }

        // Absolute time, not a fraction of lifeTime - a fraction makes fade-in take
        // seconds, far too slow for something that should feel alive.
        private const float FadeInTime = 0.25f;
        private const float FadeOutStartFraction = 0.75f;

        private float AlphaForAge(float age)
        {
            float effectiveLifeTime = lifeTime;
            float t = Mathf.Clamp01(age / effectiveLifeTime);
            float fadeIn = Mathf.Clamp01(age / FadeInTime);
            float fadeOut = 1f - Mathf.Clamp01((t - FadeOutStartFraction) / (1f - FadeOutStartFraction));
            return fadeIn * fadeOut;
        }

        private float AlphaForAltitude(Vector3 worldPos)
        {
            double altitude = body.GetAltitude(worldPos);
            if (altitude <= fadeStartAltitude) return 1f;
            if (altitude >= fadeEndAltitude) return 0f;
            return 1f - (float)((altitude - fadeStartAltitude) / (fadeEndAltitude - fadeStartAltitude));
        }

        // ---- active tail (tiled) ----

        private int lastEmittedBurnId;
        private int nextSpawnIndex;
        private bool needTrailSeparator;

        private struct GroundSortEntry
        {
            public Vector3 pos;
            public float radius;
            public float key;
        }
        private readonly List<GroundSortEntry> groundScratch = new List<GroundSortEntry>();

        private void BuildActiveOrderedList()
        {
            activeOrderedPos.Clear();
            activeOrderedRadius.Clear();
            lastEmittedBurnId = int.MinValue;
            needTrailSeparator = false;

            // Ground cloud, then a break, then the trail - all in ONE array.
            //
            // The points need not form a connected path to share a volume: the shader
            // unions capsules between consecutive points, so a zero-radius separator gives
            // two independent shapes in one continuous density field. That keeps a single
            // draw and avoids alpha compositing between two volumes, which always shows
            // the boundary where they overlap - the limit that killed the particle path.
            //
            // Sorted SPATIALLY, not by spawn order: both are emitted at the same time, so
            // spawn order interleaves pad-level puffs with ones high above and the chain
            // zigzags between them.
            groundScratch.Clear();
            for (int i = 0; i < puffs.Count; i++)
            {
                if (!puffs[i].isGround) continue;
                Vector3 wp = LocalToWorld(puffs[i].localPos);
                if (AlphaForAge(puffs[i].age) * AlphaForAltitude(wp) <= 0.01f) continue;
                float key = padAxisValid ? Vector3.Dot(wp - padCentre, padOutflowAxis) : wp.x;
                groundScratch.Add(new GroundSortEntry { pos = wp, radius = SizeForPuff(puffs[i]), key = key });
            }

            if (groundScratch.Count > 0)
            {
                groundScratch.Sort((x, y) => x.key.CompareTo(y.key));

                // Hard share of the spine. Both clouds use the same 200-point array, so an
                // unbounded ground cloud starves the trail - its stride jumps and it comes
                // out sparse enough to read as a wisp hanging off the ground cloud. The
                // points are already sorted along the outflow axis, so an even stride
                // samples the spread rather than clipping one end.
                int groundStride = Mathf.Max(1,
                    Mathf.CeilToInt(groundScratch.Count / (float)MaxGroundSpinePoints));
                for (int i = 0; i < groundScratch.Count; i += groundStride)
                {
                    activeOrderedPos.Add(groundScratch[i].pos);
                    activeOrderedRadius.Add(groundScratch[i].radius);
                }
                // Closing zero-radius point, with a MATCHING one at the first trail puff
                // below. One alone is not enough: the capsule from it to the trail still
                // spans the gap with a radius ramping 0 -> r, drawing a thin cone from the
                // pad up the column. Two give a zero-to-zero capsule, which is invisible.
                activeOrderedPos.Add(groundScratch[groundScratch.Count - 1].pos);
                activeOrderedRadius.Add(0f);
                needTrailSeparator = true;
            }

            // Thin the trail with a NESTED rule, so the chosen set only ever shrinks.
            // Stride is a power of two and selection is spawnIndex % stride == 0, so
            // doubling it keeps a strict subset: survivors stay exactly where they were.
            // A fractional stride recomputed from a growing list instead makes every point
            // hop to a neighbouring puff and take its radius with it.
            int liveTrail = 0;
            for (int i = 0; i < puffs.Count; i++)
            {
                if (!puffs[i].isGround && !puffs[i].markedForRemoval) liveTrail++;
            }
            int trailBudget = Mathf.Max(MaxSpinePoints - activeOrderedPos.Count - 4, 16);
            int trailStride = 1;
            while (liveTrail / trailStride > trailBudget) trailStride *= 2;
            // RATCHET. Recomputed from scratch the stride can go back DOWN, and near the
            // threshold it oscillates 2<->4 from frame to frame as puffs come and go. Each
            // flip swaps out half the spine points and the whole density field rebuilds -
            // the trail twitches while the smoke stands still, worst near the pad where the
            // column is densest and therefore sits on the threshold.
            //
            // Only ever rising costs a little detail late in a flight and buys a spine that
            // is stable by construction.
            if (liveTrail == 0) committedTrailStride = 1;
            else if (trailStride > committedTrailStride) committedTrailStride = trailStride;
            trailStride = committedTrailStride;

            for (int i = 0; i < puffs.Count; i++)
            {
                Puff p = puffs[i];
                if (p.isGround) continue;   // see Puff.isGround
                if (trailStride > 1 && (p.spawnIndex % trailStride) != 0) continue;

                Vector3 worldPos = LocalToWorld(p.localPos);
                float alpha = AlphaForAge(p.age) * AlphaForAltitude(worldPos);
                if (alpha <= 0.01f) continue;

                float radius = SizeForPuff(p);

                // Break the chain between separate bursts of emission, or an engine
                // relight gets bridged by a capsule spanning the whole gap.
                //
                // Doing this geometrically, by comparing the gap against the two radii, is
                // wrong: at altitude the radius shrinks while spacing grows, so ordinary
                // spacing crosses the threshold and the trail shatters. A break is a TIME
                // event, so the controller labels it and this only compares labels.
                //
                // Zero-radius points separate without extra draw calls.
                if (activeOrderedPos.Count > 0 && p.burnId != lastEmittedBurnId)
                {
                    Vector3 prevPos = activeOrderedPos[activeOrderedPos.Count - 1];
                    float prevRadius = activeOrderedRadius[activeOrderedRadius.Count - 1];
                    float gap = Vector3.Distance(worldPos, prevPos);
                    if (prevRadius > 0f && gap > 0.01f)
                    {
                        Vector3 dir = (worldPos - prevPos) / Mathf.Max(gap, 0.0001f);
                        activeOrderedPos.Add(prevPos + dir * (prevRadius * 0.5f));
                        activeOrderedRadius.Add(0f);
                        activeOrderedPos.Add(worldPos - dir * (radius * 0.5f));
                        activeOrderedRadius.Add(0f);
                    }
                }

                if (needTrailSeparator)
                {
                    needTrailSeparator = false;
                    activeOrderedPos.Add(worldPos);
                    activeOrderedRadius.Add(0f);
                }

                lastEmittedBurnId = p.burnId;
                activeOrderedPos.Add(worldPos);
                activeOrderedRadius.Add(radius);
            }

            if (hasLiveTip)
            {
                activeOrderedPos.Add(liveTipPos);
                activeOrderedRadius.Add(liveTipRadius);
            }
        }

        private void UpdateActiveTiles()
        {
            int totalPoints = activeOrderedPos.Count;
            int usedTiles = 0;

            bool logTilesThisPass = Time.time - lastActiveDebugLogTime > 1f;

            if (logTilesThisPass)
            {
                LogOverlapGaps(totalPoints);
            }

            if (totalPoints > 0 && splatKernel >= 0)
            {
                int totalChunks = Mathf.CeilToInt((float)totalPoints / ActiveTilePuffChunkSize);
                // trim from the oldest end if over budget, newest end must stay covered
                int startChunk = Mathf.Max(0, totalChunks - MaxActiveTiles);

                for (int chunk = startChunk; chunk < totalChunks; chunk++)
                {
                    int chunkStart = chunk * ActiveTilePuffChunkSize;
                    int rangeStart = Mathf.Max(0, chunkStart - ActiveTileOverlap);
                    int rangeEnd = Mathf.Min(totalPoints, chunkStart + ActiveTilePuffChunkSize + ActiveTileOverlap);

                    BakeActiveTile(usedTiles, rangeStart, rangeEnd, logTilesThisPass);
                    usedTiles++;
                }
            }

            for (int i = usedTiles; i < activeTiles.Count; i++)
            {
                SmokeRenderRegistry.SetActive(activeTiles[i].renderer, false);
            }

            if (Time.time - lastActiveDebugLogTime > 1f)
            {
                lastActiveDebugLogTime = Time.time;
                double tipAltitude = hasLiveTip ? body.GetAltitude(liveTipPos) : 0.0;
                Debug.Log(string.Format(
                    "[HairyBlob] active trail: alt={0:F0} totalPoints={1} tilesUsed={2}/{3}",
                    tipAltitude, totalPoints, usedTiles, activeTiles.Count));
            }
        }

        // ---- active tail (procedural spine, SMOKE_VOLUME_POLYLINE) ----

        // No bake pass and no chunking - one draw call, density computed analytically as
        // a capsule chain. Cannot have tile seams, since there is only one tile.
        // repeated 1-2-1 passes into smoothedSpine, leaving the source list untouched.
        // Endpoints are pinned so the trail still starts exactly at the engine and ends
        // at the live tip.
        private void SmoothSpine(List<Vector3> source, int count, int passes)
        {
            for (int i = 0; i < count; i++) smoothedSpine[i] = source[i];
            if (count < 3) return;

            for (int pass = 0; pass < passes; pass++)
            {
                smoothScratch[0] = smoothedSpine[0];
                smoothScratch[count - 1] = smoothedSpine[count - 1];
                for (int i = 1; i < count - 1; i++)
                {
                    smoothScratch[i] = smoothedSpine[i - 1] * 0.25f
                        + smoothedSpine[i] * 0.5f
                        + smoothedSpine[i + 1] * 0.25f;
                }
                for (int i = 0; i < count; i++) smoothedSpine[i] = smoothScratch[i];
            }
        }

        // Same 1-2-1 treatment for the RADII. Per-puff size variance makes radius jump
        // from one spine point to the next, and the chain lerps between them, so a
        // bigger/smaller/bigger run becomes bulge/waist/bulge - a string of beads down the
        // trail. Smaller puffs overlap less, which makes it more visible, not less.
        private void SmoothRadii(List<float> source, int count, int passes)
        {
            for (int i = 0; i < count; i++) smoothedRadii[i] = source[i];
            if (count < 3) return;

            for (int pass = 0; pass < passes; pass++)
            {
                radiiScratch[0] = smoothedRadii[0];
                radiiScratch[count - 1] = smoothedRadii[count - 1];
                for (int i = 1; i < count - 1; i++)
                {
                    radiiScratch[i] = smoothedRadii[i - 1] * 0.25f
                        + smoothedRadii[i] * 0.5f
                        + smoothedRadii[i + 1] * 0.25f;
                }
                for (int i = 0; i < count; i++) smoothedRadii[i] = radiiScratch[i];
            }
        }

        // Bounding sphere per run of segments, for the shader's two-level cull. Segment i
        // spans points i..i+1, so a group over segments [start, end) touches points
        // [start, end] - missing that extra point lets the shader cull a segment that
        // really does reach the sample.
        //
        // The radius includes the 1.4 blend multiplier, making these strict
        // over-estimates. That is the correctness requirement: over-estimating costs a
        // little speed, under-estimating punches holes in the cloud.
        private int BuildSpineGroupBounds(int totalPoints)
        {
            int lastSegment = totalPoints - 1;
            if (lastSegment <= 0) return 0;

            int groupCount = Mathf.Min(
                (lastSegment + SpineGroupSize - 1) / SpineGroupSize, MaxSpineGroups);

            for (int g = 0; g < groupCount; g++)
            {
                int start = g * SpineGroupSize;
                int end = Mathf.Min(start + SpineGroupSize, lastSegment); // exclusive over segments
                int lastPoint = Mathf.Min(end, totalPoints - 1);          // inclusive over points

                Vector3 min = smoothedSpine[start];
                Vector3 max = min;
                for (int i = start + 1; i <= lastPoint; i++)
                {
                    min = Vector3.Min(min, smoothedSpine[i]);
                    max = Vector3.Max(max, smoothedSpine[i]);
                }

                Vector3 centre = (min + max) * 0.5f;
                float radius = 0f;
                for (int i = start; i <= lastPoint; i++)
                {
                    // The shader culls against pWARPED, but the warp displaces the sample
                    // before coverage is evaluated. Without this margin a warped sample can
                    // fall outside the sphere while the capsule underneath still covers it,
                    // the group is skipped, and a chunk of trail disappears - view
                    // dependent, since which samples land in that shell depends on the ray.
                    // The AABB and these spheres must pad by the SAME amount. sqrt(3)
                    // covers a worst-case per-axis displacement.
                    float warpMargin = (SmokeTuning.SilhouetteWarpStrength + SmokeTuning.VortexStrength) * 1.733f + VortexWarpMargin;
                    float reach = Vector3.Distance(centre, smoothedSpine[i])
                        + smoothedRadii[i] * 1.4f + warpMargin;
                    if (reach > radius) radius = reach;
                }

                spineGroupBounds[g] = new Vector4(centre.x, centre.y, centre.z, radius);
            }

            return groupCount;
        }

        private void UpdatePolylineVolume()
        {
            int totalPoints = activeOrderedPos.Count;

            List<Vector3> pos = activeOrderedPos;
            List<float> radius = activeOrderedRadius;

            if (totalPoints > MaxSpinePoints)
            {
                polylineThinnedPos.Clear();
                polylineThinnedRadius.Clear();
                float stride = (float)totalPoints / MaxSpinePoints;
                for (int i = 0; i < MaxSpinePoints; i++)
                {
                    int srcIndex = Mathf.Min(totalPoints - 1, Mathf.FloorToInt(i * stride));
                    polylineThinnedPos.Add(activeOrderedPos[srcIndex]);
                    polylineThinnedRadius.Add(activeOrderedRadius[srcIndex]);
                }
                pos = polylineThinnedPos;
                radius = polylineThinnedRadius;
                totalPoints = MaxSpinePoints;
            }

            if (totalPoints == 0)
            {
                SmokeRenderRegistry.SetActive(polylineRenderer, false);
                return;
            }

            // The chain joins spine points with exact straight capsules - no blur pass
            // like the old baked system - so spawn jitter and residual gimbal wobble show
            // up directly as a sawtooth.
            //
            // Decimation spacing scales with puff RADIUS, so anything that speeds growth up
            // also makes the spine sparser, and averaging over neighbours that are now
            // metres apart stops hiding the jitter - the sawtooth comes back after a change
            // that never touched positions. Extra passes are free at <=200 points.
            SmoothSpine(pos, totalPoints, SpineSmoothPasses);
            SmoothRadii(radius, totalPoints, SpineSmoothPasses);

            Vector3 boxMin = Vector3.positiveInfinity;
            Vector3 boxMax = Vector3.negativeInfinity;
            float radiusSum = 0f;

            for (int i = 0; i < totalPoints; i++)
            {
                float r = smoothedRadii[i];
                Vector3 p = smoothedSpine[i];
                spinePointsBuffer[i] = new Vector4(p.x, p.y, p.z, 0f);
                spineRadiiBuffer[i] = r;
                radiusSum += r;

                float warpMarginScale = Mathf.Clamp(r / ShaderReferenceRadius, 0.35f, 1f);
                float boundRadius = r * BoxRadiusMarginMultiplier + BoxWarpMargin * warpMarginScale;
                boxMin = Vector3.Min(boxMin, p - Vector3.one * boundRadius);
                boxMax = Vector3.Max(boxMax, p + Vector3.one * boundRadius);
            }

            for (int i = totalPoints; i < MaxSpinePoints; i++)
            {
                spineRadiiBuffer[i] = 0f;
            }

            int spineGroupCount = BuildSpineGroupBounds(totalPoints);

            Vector3 boxCenter = (boxMin + boxMax) * 0.5f;
            Vector3 boxExtents = (boxMax - boxMin) * 0.5f;

            SmokeRenderRegistry.SetActive(polylineRenderer, true);
            polylineRenderer.transform.position = boxCenter;
            polylineRenderer.transform.localScale = boxExtents * 2f;

            float avgRadius = radiusSum / totalPoints;
            float radiusRatio = Mathf.Clamp(avgRadius / ShaderReferenceRadius, 0.05f, 1f);

            polylinePropertyBlock.Clear();
            polylinePropertyBlock.SetInt("_SpineCount", totalPoints);
            polylinePropertyBlock.SetVectorArray("_SpinePoints", spinePointsBuffer);
            polylinePropertyBlock.SetFloatArray("_SpineRadii", spineRadiiBuffer);
            polylinePropertyBlock.SetInt("_SpineGroupCount", spineGroupCount);
            polylinePropertyBlock.SetVectorArray("_SpineGroupBounds", spineGroupBounds);
            polylinePropertyBlock.SetVector("_BoxCenter", new Vector4(boxCenter.x, boxCenter.y, boxCenter.z, 0f));
            polylinePropertyBlock.SetVector("_BoxExtents", new Vector4(boxExtents.x, boxExtents.y, boxExtents.z, 0f));
            polylinePropertyBlock.SetFloat("_TileRadiusRatio", radiusRatio);
            polylinePropertyBlock.SetFloat("_DepthBiasDistance", ActiveDepthBiasDistance);
            polylinePropertyBlock.SetFloat("_DepthBiasFraction", ActiveDepthBiasFraction);
            SmokeTuning.Apply(polylinePropertyBlock);
            ApplyMarchLOD(polylinePropertyBlock, boxCenter, boxExtents, avgRadius);
            polylineRenderer.SetPropertyBlock(polylinePropertyBlock);

            if (Time.time - lastActiveDebugLogTime > 1f)
            {
                lastActiveDebugLogTime = Time.time;
                double tipAltitude = hasLiveTip ? body.GetAltitude(liveTipPos) : 0.0;

                // Box extents and end-to-end spine length, to tell whether a stretched
                // trail is the PUFFS being flung or the volume being DRAWN wrong.
                // spineCount alone cannot separate those two.
                Vector3 spineStart = smoothedSpine[0];
                Vector3 spineEnd = smoothedSpine[Mathf.Max(totalPoints - 1, 0)];
                float endToEnd = Vector3.Distance(spineStart, spineEnd);
                double startAlt = body.GetAltitude(spineStart);
                double endAlt = body.GetAltitude(spineEnd);

                Debug.Log(string.Format(
                    "[HairyBlob] polyline: alt={0:F0} spineCount={1} (rawPoints={2}) "
                    + "endToEnd={3:F0}m startAlt={4:F0} endAlt={5:F0} boxExtents={6} axis={7}",
                    tipAltitude, totalPoints, activeOrderedPos.Count,
                    endToEnd, startAlt, endAlt, boxExtents.ToString("F0"),
                    padAxisValid ? padOutflowAxis.ToString("F2") : "none"));
            }
        }

        // Real spacing between spawn-order neighbours against the sum of their blend
        // radii. If spacing exceeds that sum the coverage has a true gap, whatever the
        // rendering path is doing.
        // same smoothstep falloff as SmokeVolumeSplat.compute's Splat kernel -
        // "spheres overlap" (LogOverlapGaps' old check) isn't the same as "density
        // is actually high": two puffs whose blend spheres barely touch contribute t=0 at
        // the midpoint, so density can read near zero even with no geometric gap.
        private static float SmoothstepContribution(float d, float blendRadius)
        {
            if (d >= blendRadius) return 0f;
            float t = Mathf.Clamp01(1f - d / blendRadius);
            return t * t * (3f - 2f * t);
        }

        private void LogOverlapGaps(int totalPoints)
        {
            int gapCount = 0;
            float worstGap = 0f;
            int worstIndex = -1;

            float worstMidDensity = float.MaxValue;
            int worstDensityIndex = -1;

            for (int i = 0; i < totalPoints - 1; i++)
            {
                Vector3 posA = activeOrderedPos[i];
                Vector3 posB = activeOrderedPos[i + 1];
                float radA = activeOrderedRadius[i];
                float radB = activeOrderedRadius[i + 1];

                float spacing = Vector3.Distance(posA, posB);
                float combinedBlend = (radA + radB) * BlendRadiusMultiplier;
                float gap = spacing - combinedBlend;
                if (gap > 0f)
                {
                    gapCount++;
                    if (gap > worstGap)
                    {
                        worstGap = gap;
                        worstIndex = i;
                    }
                }

                Vector3 mid = (posA + posB) * 0.5f;
                float midDensity = SmoothstepContribution(Vector3.Distance(mid, posA), radA * BlendRadiusMultiplier)
                    + SmoothstepContribution(Vector3.Distance(mid, posB), radB * BlendRadiusMultiplier);
                if (midDensity < worstMidDensity)
                {
                    worstMidDensity = midDensity;
                    worstDensityIndex = i;
                }
            }

            if (gapCount > 0)
            {
                Debug.Log(string.Format(
                    "[HairyBlob] overlap: {0}/{1} neighbor pairs have a REAL gap - worst={2:F1}m at index={3} " +
                    "(posA={4} rA={5:F1} posB={6} rB={7:F1})",
                    gapCount, totalPoints - 1, worstGap, worstIndex,
                    worstIndex >= 0 ? activeOrderedPos[worstIndex] : Vector3.zero,
                    worstIndex >= 0 ? activeOrderedRadius[worstIndex] : 0f,
                    worstIndex >= 0 && worstIndex + 1 < totalPoints ? activeOrderedPos[worstIndex + 1] : Vector3.zero,
                    worstIndex >= 0 && worstIndex + 1 < totalPoints ? activeOrderedRadius[worstIndex + 1] : 0f));
            }
            else if (totalPoints > 1)
            {
                Debug.Log(string.Format(
                    "[HairyBlob] overlap: all {0} neighbor pairs overlap - no real gaps in the puff data itself",
                    totalPoints - 1));
            }

            if (worstDensityIndex >= 0)
            {
                Debug.Log(string.Format(
                    "[HairyBlob] density: worst midpoint density={0:F3} at index={1}/{2} " +
                    "(rA={3:F1} rB={4:F1} spacing={5:F1})",
                    worstMidDensity, worstDensityIndex, totalPoints - 1,
                    activeOrderedRadius[worstDensityIndex], activeOrderedRadius[worstDensityIndex + 1],
                    Vector3.Distance(activeOrderedPos[worstDensityIndex], activeOrderedPos[worstDensityIndex + 1])));
            }
        }

        private void BakeActiveTile(int tileIndex, int rangeStart, int rangeEnd, bool logThisPass = false)
        {
            ActiveTile tile = GetOrCreateActiveTile(tileIndex);

            Vector3 boxMin = Vector3.positiveInfinity;
            Vector3 boxMax = Vector3.negativeInfinity;
            int count = 0;
            float radiusSum = 0f;

            for (int i = rangeStart; i < rangeEnd && count < MaxTextureLayerPuffs; i++)
            {
                float radius = activeOrderedRadius[i];
                if (radius <= 0f) continue;
                Vector3 pos = activeOrderedPos[i];

                tileCentersBuffer[count] = new Vector4(pos.x, pos.y, pos.z, 0f);
                tileRadiiBuffer[count] = radius;
                count++;
                radiusSum += radius;

                float warpMarginScale = Mathf.Clamp(radius / ShaderReferenceRadius, 0.35f, 1f);
                float boundRadius = radius * BoxRadiusMarginMultiplier + BoxWarpMargin * warpMarginScale;
                boxMin = Vector3.Min(boxMin, pos - Vector3.one * boundRadius);
                boxMax = Vector3.Max(boxMax, pos + Vector3.one * boundRadius);
            }

            for (int i = count; i < MaxTextureLayerPuffs; i++)
            {
                tileRadiiBuffer[i] = 0f;
            }

            if (count == 0)
            {
                SmokeRenderRegistry.SetActive(tile.renderer, false);
                return;
            }

            Vector3 boxCenter = (boxMin + boxMax) * 0.5f;
            Vector3 boxExtents = (boxMax - boxMin) * 0.5f;
            Vector3 boxSize = boxExtents * 2f;

            if (logThisPass)
            {
                Debug.Log(string.Format(
                    "[HairyBlob] tile: tile={0} range=[{1},{2}) count={3} boxMin={4} boxMax={5}",
                    tileIndex, rangeStart, rangeEnd, count, boxMin, boxMax));
            }

            Vector3Int resolution = ChooseActiveResolution(boxSize);
            EnsureTileResolution(tile, resolution);

            ComputeShader compute = ShaderCache.SmokeVolumeSplatCompute;
            compute.SetTexture(splatKernel, "_DensityTex", tile.densityTex);
            compute.SetInt("_PuffCount", count);
            compute.SetVectorArray("_PuffCenters", tileCentersBuffer);
            compute.SetFloats("_PuffRadii", tileRadiiBuffer);
            compute.SetFloat("_BlendRadiusMultiplier", BlendRadiusMultiplier);
            compute.SetVector("_BoxMin", boxMin);
            compute.SetVector("_BoxSize", boxSize);
            compute.SetInts("_Resolution", resolution.x, resolution.y, resolution.z);

            int groups = Mathf.CeilToInt(resolution.x / 4f);
            compute.Dispatch(splatKernel, groups, groups, groups);

            if (blurKernel >= 0)
            {
                compute.SetTexture(blurKernel, "_DensityTex", tile.densityTex);
                compute.SetTexture(blurKernel, "_BlurredTex", tile.blurredTex);
                compute.SetInts("_Resolution", resolution.x, resolution.y, resolution.z);
                compute.Dispatch(blurKernel, groups, groups, groups);
            }

            SmokeRenderRegistry.SetActive(tile.renderer, true);
            tile.renderer.transform.position = boxCenter;
            tile.renderer.transform.localScale = boxExtents * 2f;

            float avgRadius = radiusSum / count;
            float tileRadiusRatio = Mathf.Clamp(avgRadius / ShaderReferenceRadius, 0.05f, 1f);

            tile.propertyBlock.Clear();
            tile.propertyBlock.SetTexture("_DensityTex", tile.blurredTex);
            tile.propertyBlock.SetVector("_BoxCenter", new Vector4(boxCenter.x, boxCenter.y, boxCenter.z, 0f));
            tile.propertyBlock.SetVector("_BoxExtents", new Vector4(boxExtents.x, boxExtents.y, boxExtents.z, 0f));
            tile.propertyBlock.SetFloat("_TileRadiusRatio", tileRadiusRatio);
            tile.propertyBlock.SetFloat("_DepthBiasDistance", ActiveDepthBiasDistance);
            tile.propertyBlock.SetFloat("_DepthBiasFraction", ActiveDepthBiasFraction);
            SmokeTuning.Apply(tile.propertyBlock);
            ApplyMarchLOD(tile.propertyBlock, boxCenter, boxExtents, avgRadius);
            tile.renderer.SetPropertyBlock(tile.propertyBlock);
        }

        private ActiveTile GetOrCreateActiveTile(int index)
        {
            if (index < activeTiles.Count) return activeTiles[index];

            GameObject obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obj.name = "ActiveTile_" + index;
            obj.transform.SetParent(transform, false);
            RemoveColliderImmediate(obj);

            MeshRenderer renderer = obj.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = false;

            if (ShaderCache.SmokeVolumeShader != null)
            {
                renderer.material = new Material(ShaderCache.SmokeVolumeShader);
                // ZTest Always reverted here too - see polylineMat for why
            }

            Vector3Int initialResolution = new Vector3Int(ActiveResolutionTiers[0], ActiveResolutionTiers[0], ActiveResolutionTiers[0]);
            ActiveTile tile = new ActiveTile
            {
                obj = obj,
                renderer = renderer,
                propertyBlock = new MaterialPropertyBlock(),
                densityTex = CreateDensityTexture(initialResolution),
                blurredTex = CreateDensityTexture(initialResolution),
                resolution = initialResolution
            };

            activeTiles.Add(tile);
            return tile;
        }

        // Distance LOD: full quality close up, fewer march steps far away. Too few and
        // stepSize exceeds the puffs themselves, so the ray skips thin content between
        // samples and the trail breaks into sparse dots.
        private const float LODFullQualityDistance = 500f;
        private const float LODMinQualityDistance = 8000f;
        // Sample count is the real quality ceiling: the same shader and parameters render
        // as flat plates at 12 steps and as a cloud at 96.
        private const int LODFullMarchSteps = 64;
        private const int LODMinMarchSteps = 32;
        private const int LODFullLightMarchSteps = 4;
        private const int LODMinLightMarchSteps = 2;

        // Just outside a box still covers most of the screen at close range, so the
        // overdraw cost is the same as being inside it.
        private const float NearSurfaceThreshold = 60f;

        // Volume far behind the rocket costs as much to march as the tip, so quality also
        // fades with distance from the tip, independent of the camera.
        private const float TipLODFullDistance = 150f;
        private const float TipLODMinDistance = 900f;

        // The box wraps the WHOLE trail in one draw, and at speed that spans kilometres.
        // The LOD above reacts to camera distance, not box size, so it quietly spreads the
        // same step budget over a much longer traversal and under-samples the trail's own
        // (still thin) radius. Enough steps have to land across that local thickness
        // whatever the box grew to, capped so a long trail cannot blow the frame cost.
        private const float TargetStepsPerAvgRadius = 3f;
        private const int MaxAdaptiveMarchSteps = 96;

        private void ApplyMarchLOD(MaterialPropertyBlock propertyBlock, Vector3 boxCenter, Vector3 boxExtents, float avgRadius)
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 camLocal = cam.transform.position - boxCenter;
            bool cameraInside = Mathf.Abs(camLocal.x) < boxExtents.x
                && Mathf.Abs(camLocal.y) < boxExtents.y
                && Mathf.Abs(camLocal.z) < boxExtents.z;

            float dist = camLocal.magnitude;
            float distToSurface = dist - boxExtents.magnitude;

            int marchSteps;
            int lightMarchSteps;

            bool nearOrInside = cameraInside || distToSurface < NearSurfaceThreshold;

            if (nearOrInside)
            {
                // The closest, most scrutinised view, so it must not sit below the
                // far-distance floor. The shader hides under-sampling by dithering the
                // march offset, and that dither scales with stepSize - too few steps here
                // reads as grain exactly where the volume fills the most screen.
                marchSteps = 56;
                lightMarchSteps = 3;
            }
            else
            {
                float t = Mathf.InverseLerp(LODFullQualityDistance, LODMinQualityDistance, dist);
                marchSteps = Mathf.RoundToInt(Mathf.Lerp(LODFullMarchSteps, LODMinMarchSteps, t));
                lightMarchSteps = Mathf.RoundToInt(Mathf.Lerp(LODFullLightMarchSteps, LODMinLightMarchSteps, t));
            }

            if (hasLiveTip)
            {
                float tipDist = Vector3.Distance(boxCenter, liveTipPos);
                float tt = Mathf.InverseLerp(TipLODFullDistance, TipLODMinDistance, tipDist);
                int tipMarchSteps = Mathf.RoundToInt(Mathf.Lerp(LODFullMarchSteps, LODMinMarchSteps, tt));
                int tipLightMarchSteps = Mathf.RoundToInt(Mathf.Lerp(LODFullLightMarchSteps, LODMinLightMarchSteps, tt));
                marchSteps = Mathf.Min(marchSteps, tipMarchSteps);
                lightMarchSteps = Mathf.Min(lightMarchSteps, tipLightMarchSteps);
            }

            // Only when the camera is NOT close. Up close the box fills the screen, so
            // every extra step is paid by every pixel - and via Mathf.Max this would
            // silently override the near branch above. Empty-space skipping carries
            // close-range quality instead.
            if (!nearOrInside)
            {
                float boxDiagonal = boxExtents.magnitude * 2f;
                float targetStepSize = Mathf.Max(avgRadius / TargetStepsPerAvgRadius, 0.5f);
                int sizeAdaptiveSteps = Mathf.CeilToInt(boxDiagonal / targetStepSize);
                marchSteps = Mathf.Min(Mathf.Max(marchSteps, sizeAdaptiveSteps), MaxAdaptiveMarchSteps);
            }

            // Detail LOD. Noise frequency is fixed in world space, so as the volume
            // shrinks on screen its detail falls below a pixel - and shaping a silhouette
            // at a scale the screen cannot resolve is what makes distant edges shimmer.
            // Fading it costs nothing visible, since it was never resolvable anyway.
            float detailFade = Mathf.Lerp(1f, 0.3f,
                Mathf.InverseLerp(LODFullQualityDistance, LODMinQualityDistance, dist));
            propertyBlock.SetFloat("_DetailStrength", SmokeTuning.DetailStrength * detailFade);
            propertyBlock.SetFloat("_EdgeErosionStrength", SmokeTuning.EdgeErosionStrength * detailFade);

            // QUANTISED. Measured: a one-step change in _MarchSteps visibly changes the
            // image, while a 10% change in _TileRadiusRatio changes nothing. Both counts
            // come from RoundToInt over a continuous camera-distance lerp, so they flip
            // between adjacent integers on the slightest movement, every sample position
            // shifts with them, and that is the flicker. Coarse increments change rarely
            // and visibly once instead of dithering every frame.
            marchSteps = Mathf.Max(8, (marchSteps / 8) * 8);
            lightMarchSteps = Mathf.Max(2, (lightMarchSteps / 2) * 2);
            propertyBlock.SetInt("_MarchSteps", marchSteps);
            propertyBlock.SetInt("_LightMarchSteps", lightMarchSteps);
        }

        private void OnDestroy()
        {
            SmokeRenderRegistry.Remove(polylineRenderer);
            for (int i = 0; i < activeTiles.Count; i++) SmokeRenderRegistry.Remove(activeTiles[i].renderer);

            activeInstanceCount--;


            foreach (ActiveTile tile in activeTiles)
            {
                if (tile.densityTex != null)
                {
                    tile.densityTex.Release();
                    Object.Destroy(tile.densityTex);
                }
                if (tile.blurredTex != null)
                {
                    tile.blurredTex.Release();
                    Object.Destroy(tile.blurredTex);
                }
            }
        }
    }
}
