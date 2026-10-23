// hi code browser
using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
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
        private const int MaxSpinePoints = 320; // must match MAX_SPINE_POINTS in SmokeVolume.shader
        // Ceiling on the ground cloud's share, leaving the rest for the trail.
        private const int MaxGroundSpinePoints = 110;

        private struct Puff
        {
            public Vector3 localPos;
            public Vector3 hitLocal;          // where this parcel hit the surface (origin of its wall jet)
            public float hitSpeed, hitR0;     // its sideways speed there, and the length scale of the wall jet (m)
            public Vector3 velocity;
            public float age;
            public float sizeMultiplier;
            // Final expansion factor this puff will reach, applied OVER its growth rather
            // than at birth. The plume leaves the nozzle narrow and opens into a bell over
            // the following seconds; baking the factor into the birth size instead gives a
            // cone that is already at full width where it meets the engine.
            public float expansion;
            // How much exhaust this puff represents, 0..1, fixed at emission: the motor's
            // delivered thrust and the air it was born into. Separate from expansion,
            // which is about SIZE - a puff can be wide and still carry little smoke.
            public float emitDensity;
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
            public float liftFreeUntil;       // until then the climb limit of a wall jet is off (it is running up a wall)
            // Which continuous burst of emission this puff belongs to. Incremented by the
            // controller whenever spawning resumes after a pause, so a chain break is an
            // exact fact rather than something inferred from geometry.
            public int burnId;
            // Monotonic, assigned once. Spine selection uses THIS, not list position:
            // puffs expire off the front, so an index-based rule reshuffles which puffs
            // are picked every frame, which is what made the texture flash.
            public int spawnIndex;
            public bool markedForRemoval;
            // water-vapour share (liquid-engine exhaust) fixed at birth, 0..1
            public float steam;
            // water mass fraction of this puff's exhaust, and the current visibility of its condensate (physical puffs)
            public float fw, pVis;
            // radius multiplier (per-engine / liquid scale) and the dilution out to which the exhaust stays visible
            public float pScale, pVisDil;
            public float pR0;                 // radius when released (for the growth cap)
            // cached terrain height under this puff (m above sea level) and when it was sampled
            public float gAlt, gTime;
            // Which fixed landing-splash ray this ground puff belongs to, or -1 for the
            // old omnidirectional pad-deflector ground cloud. See AddGroundPuff and
            // BuildActiveOrderedList - puffs on the same ray chain together into one
            // continuous streak instead of the whole splash sorting as one axis, which is
            // what a "starburst" of independent random directions needs to read as
            // separate jets rather than a scribble connecting between them.
            public int groundRayId;
            // Emission tick this ray puff belongs to (all rays of one tick share it), so
            // thinning can pick whole ticks with a nested power-of-two rule, stable frame
            // to frame - see BuildActiveOrderedList.
            public int groundTick;
            // Horizontal direction of this ray puff's throw; drives its later creep.
            public Vector3 rayDir;
            // REAL PHYSICS puff state (see PuffPhysics). pM = total mass, pMe = exhaust mass, pT = temperature,
            // pM0 = mass at release, pR = equivalent-sphere radius. velocity above is reused (surface frame).
            public bool physical;
            public float pM, pMe, pT, pM0, pR;
            // What is DRAWN: the radius and the condensate visibility eased toward the physical values every tick. Old parcels are stepped
            // only every 2nd-8th tick, and their size changed in steps (at a different tick for each, so a different slice of the spine
            // jumped every frame: the twitching).
            public float pRs, pVisS;
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

        // The hierarchical warp displaces by up to BillowAmp of the LOCAL radius (three octaves
        // summing to ~1 amplitude, sqrt(3) per-axis worst case), which for a big puff is far more
        // than the fixed margin above - the box then clips the plume flat at its walls. Seen in
        // game as a flat "plate" cut at the base of a fat trail.
        private static float RadiusWarpMargin(float radius)
        {
            if (SmokeTuning.BillowMix <= 0.001f) return 0f;
            return 1.9f * SmokeTuning.BillowAmp * radius
                 * Mathf.Clamp01(SmokeTuning.SilhouetteWarpStrength / 20f);
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
        private readonly List<float> polylineThinnedDensity = new List<float>();

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
        // Identifies this volume in the log. Two engine clusters make two independent
        // volumes drawn side by side, and without an id the per-frame lines are
        // indistinguishable - which is exactly the "two different renderings" question.
        private static int nextInstanceId = 1;
        private readonly int instanceId = nextInstanceId++;
        // Last values this volume actually rendered with, for the same diagnostic.
        private float lastRadiusRatio;
        private int lastMarchSteps;
        private Vector3 lastNoiseShift;

        private int committedTrailStride = 1;
        // Same ratchet, for the SECOND thinning stage - see BuildMergedSpine.
        private int committedSpineStride = 1;

        // Set by LaunchSmokeController each tick: 1 at sea level, falling as the plume
        // blooms, so an expanding plume also gets thinner instead of just fatter.
        public float densityScale = 1f;

        // How much the column's width may wander, as a fraction. See ColumnWander.
        // OFF by default. It was meant to stop the trail tapering as an even cone, but the
        // warp amplitude is capped against the local radius, so wobbling the radius also
        // wobbles how far the silhouette is displaced - and that reads as the whole column
        // meandering from side to side rather than as a change in thickness.

        private readonly List<Vector3> activeOrderedPos = new List<Vector3>();
        private readonly List<float> activeOrderedRadius = new List<float>();
        // Runs alongside activeOrderedRadius. A puff carries its own density because the
        // bloom is a property of the air the puff was born into, not of the vessel now: a
        // trail reaching from the pad to 24km is thick at the bottom and thin at the top,
        // and one scale for the whole volume made the entire column vanish at altitude.
        private readonly List<float> activeOrderedDensity = new List<float>();
        private int clusterChains = 1;
        // This chain's slice of the shared spine buffer, set by BuildMergedSpine (water-filling over the chains'
        // demands), the spine points this chain would keep with no budget pressure, and the continuous band width
        // actually in use (see BuildActiveOrderedList).
        private int shareBudgetPts = 0;
        private int lastFullDemand = 0;
        private float bandEff = 0f;

        private int splatKernel = -1;
        private int blurKernel = -1;

        public bool HasActivePuffs => puffs.Count > 0;

        // Called while the mod is toggled off. Freezes puffs in place and hides every
        // renderer; cheap to call every frame.
        public void HideAll()
        {
            hiddenByToggle = true;
            if (polylineRenderer != null) SmokeRenderRegistry.SetActive(polylineRenderer, false);
            for (int i = 0; i < activeTiles.Count; i++) SmokeRenderRegistry.SetActive(activeTiles[i].renderer, false);
        }

        private static int activeInstanceCount;
        public static bool AnyActive => activeInstanceCount > 0;

        // Every live group, so nearby trails can be drawn as ONE volume. See RenderAll.
        private static readonly List<SmokeVolumeGroup> all = new List<SmokeVolumeGroup>();
        private float lastTickTime;
        private bool hiddenByToggle;
        private int lastLeaderId;
        public int MergedCount { get; private set; }

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
            lastNoiseShift = shift;
            // Still published globally so anything without its own block has a value, but
            // the authoritative copy now goes into each volume's property block - see the
            // note where it is applied.
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
            all.Add(this);
            MergedCount = 1;

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
                Debug.LogWarning("[PlumeTrails] SmokeVolumeSplatCompute is null while creating a SmokeVolumeGroup.");
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
        // static, not const: as a const the compiler proves the guarded blocks dead and
        // warns on every one of them, and the flag can no longer be flipped while running.
        private static bool debugBounceLogging = false;
        private float lastBounceLogTime = -999f;

        // How far the live tip may move in one tick before it is logged as a jump. Real
        // motion is bounded by vessel speed * fixedDeltaTime, which even at orbital speed
        // over a 0.02s physics step is on the order of tens of metres - a few hundred
        // catches a genuine discontinuity without false-positiving on a fast, normal tick.
        private const float TipJumpWarnDistance = 500f;

        public void SetLiveTip(Vector3 worldPos, float radius)
        {
            // Diagnostic for the SetLiveRoot "balloon on a string" bug (28 IX): logs the
            // jump itself, one tick before SetLiveRoot would otherwise have to guess at
            // why nozzle and tip went out of sync. hasLiveTip guards against the very
            // first call, which has nothing to compare against.
            if (hasLiveTip)
            {
                float jump = Vector3.Distance(liveTipPos, worldPos);
                if (jump > TipJumpWarnDistance)
                {
                    Debug.LogWarning(string.Format(
                        "[PlumeTrails] instance {0}: live tip jumped {1:F0}m in one tick "
                        + "({2} -> {3}), {4:F2}s since the last SetLiveTip. Likely cause: the "
                        + "engine-group centroid this tip is spawned from moved (group "
                        + "membership changed, e.g. via a landing-state RecomputeGroups) "
                        + "rather than the vessel actually travelling that far.",
                        instanceId, jump, liveTipPos, worldPos, Time.time - lastLiveTipRefresh));
                }
            }
            liveTipPos = worldPos;
            liveTipRadius = radius;
            hasLiveTip = true;
            lastLiveTipRefresh = Time.time;
        }

        public void ClearLiveTip()
        {
            hasLiveTip = false;
            liveRootCount = 0;
        }

        // The plume's root: a short curve from the nozzle to the live tip, so the smoke
        // starts AT the engine and visibly leaves along the flame before bending away.
        //
        // Without it the chain began a jet-length behind the nozzle, leaving a gap between
        // the engine and the first smoke that the flame had to hide - and any change in
        // where that first point sat, whether from a turn or from speed, read as smoke
        // appearing out of nowhere beside the exhaust rather than being pushed out of it.
        //
        // A quadratic Bezier whose control point lies on the nozzle axis: the curve leaves
        // tangent to the axis and arrives wherever the wind has carried the tip to, which
        // is exactly the shape of a jet turning into a crossflow.
        private const int LiveRootPoints = 4;
        private readonly Vector3[] liveRootPos = new Vector3[LiveRootPoints];
        private readonly float[] liveRootRadius = new float[LiveRootPoints];
        private readonly float[] liveRootDens = new float[LiveRootPoints];
        private int liveRootCount;
        private float liveRootDensity = 1f;

        public bool HasLiveTip => hasLiveTip;
        public Vector3 LiveTipPosition => liveTipPos;
        // KSP moved the origin of the world (the active vessel is brought back to it): the live tip is raw world space, so it moves with it
        public void ShiftLiveTip(Vector3 delta) { liveTipPos += delta; }

        // Turn the live tip and root into ordinary puffs at the moment emission stops.
        //
        // They exist only while an engine feeds them, so cutting the engine deleted the
        // last jet-length of plume plus its root in a single frame. The trail then seemed
        // to jump BACK toward the vessel and ended bluntly on the last full puff - the
        // "pulls back and flattens" when a motor shuts down. Freezing them into puffs
        // keeps exactly what was on screen and lets it age and spread like the rest.
        //
        // Ordered tip first, then toward the nozzle, which is the spine's own order.
        public void CommitLiveTip()
        {
            if (!hasLiveTip)
            {
                liveRootCount = 0;
                return;
            }

            // Same burn as the puffs before it, or the chain would be broken between them.
            // The last puff in the list may be a ground-cloud one (burn 0), so look back.
            int burn = 0;
            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                if (!puffs[i].isGround) { burn = puffs[i].burnId; break; }
            }

            AddFrozenPuff(liveTipPos, liveTipRadius, burn, liveRootDensity);
            for (int i = 0; i < liveRootCount; i++)
                AddFrozenPuff(liveRootPos[i], liveRootRadius[i], burn, liveRootDens[i]);

            hasLiveTip = false;
            liveRootCount = 0;
        }

        private void AddFrozenPuff(Vector3 worldPos, float radius, int burnId, float density)
        {
            puffs.Add(new Puff
            {
                localPos = WorldToLocal(worldPos),
                velocity = Vector3.zero,
                age = 0f,
                // SizeForPuff returns startSize * sizeMultiplier at age 0, so this puts the
                // frozen puff at exactly the radius the live tip had.
                sizeMultiplier = radius / Mathf.Max(startSize, 0.01f),
                expansion = 1f,
                emitDensity = density,
                burnId = burnId,
                spawnIndex = nextSpawnIndex++
            });
        }

        // Nozzle and the live tip should never be far apart - the tip is spawned within
        // SpawnOffset (~15m default) of the nozzle every tick. Bound generously above that
        // so real high-standoff configs (umbrella retro burns, big clusters) are not
        // clipped, but a runaway gap is caught before it draws anything.
        private const float MaxSaneRootLen = 200f;

        public void SetLiveRoot(Vector3 nozzle, Vector3 axis, float nozzleRadius, float density)
        {
            liveRootCount = 0;
            liveRootDensity = density;
            if (!hasLiveTip || nozzleRadius <= 0f) return;
            float len = Vector3.Distance(nozzle, liveTipPos);
            if (len < 1.5f) return;

            // Sanity guard (28 IX fix). Without it, nozzle and tip going out of sync for
            // even one frame - the live tip stale while the nozzle position jumps, e.g.
            // from engine-group membership changing under it - draws a hugely stretched
            // 4-point Bezier: a near-zero-width needle for almost its whole length, capped
            // by the tip's full radius at the very end. Reported 28 IX as a "balloon on a
            // string" / floating sphere disconnected from the trail. Skipping the root for
            // that one bad frame is far less jarring than drawing it.
            if (len > MaxSaneRootLen)
            {
                Debug.LogWarning(string.Format(
                    "[PlumeTrails] instance {0}: SetLiveRoot nozzle-to-tip distance {1:F0}m exceeds "
                    + "the {2:F0}m sane bound - skipping the root this frame. nozzle={3} tip={4} "
                    + "tipRadius={5:F1}. If this repeats, the nozzle and live tip positions are out "
                    + "of sync (stale tip vs a jumped engine-group centroid).",
                    instanceId, len, MaxSaneRootLen, nozzle, liveTipPos, liveTipRadius));
                return;
            }

            Vector3 control = nozzle + axis * (len * 0.55f);
            // tip -> nozzle, matching the spine's oldest-to-newest order
            for (int i = 0; i < LiveRootPoints; i++)
            {
                float t = 1f - (i + 1) / (float)LiveRootPoints;   // 0.75, 0.5, 0.25, 0
                float u = 1f - t;
                liveRootPos[i] = u * u * nozzle + 2f * u * t * control + t * t * liveTipPos;
                float grow = t * t * (3f - 2f * t);               // smoothstep: no needle at the nozzle
                liveRootRadius[i] = Mathf.Lerp(nozzleRadius, liveTipRadius, grow);
                // Smoke does not start AT the nozzle: it fades in along the root, so the visible smoke begins
                // roughly where the flame ends (this is what the Spawn offset slider moves).
                liveRootDens[i] = liveRootDensity * Mathf.Lerp(1f, t * t, Mathf.Clamp01(SmokeTuning.SrbRootFade));
            }
            liveRootCount = LiveRootPoints;
        }

        // Ground cloud, emitted where the exhaust lands rather than produced by puffs
        // falling and bouncing. Bouncing could never sustain it: puffs spawn below the
        // engine, so once the vessel clears ~24m nothing reaches the ground and the cloud
        // stops growing a second after liftoff, while real exhaust keeps striking the
        // deflector for many seconds.
        //
        // Velocity is purely horizontal - the point is that it spreads, not lifts.
        //
        // rayId >= 0 switches from a single random direction per puff to a FIXED direction
        // picked deterministically from rayId/rayCount/up - the same call every tick then
        // keeps extending the SAME line instead of scattering a new random heading each
        // time. That is what makes a "jet stream" look possible at all: a continuous
        // streak is puffs from one ray, spawned close together in time, chained by
        // BuildActiveOrderedList into one capsule strip. Random-per-puff (rayId -1, the
        // old pad-deflector behaviour) can only ever read as a diffuse cloud, because nothing
        // connects any two puffs into the same line for the spine to trace.
        public void AddGroundPuff(Vector3 impactPos, Vector3 up, float outwardSpeed, float sizeScale,
            float emitDensity = 1f, int rayId = -1, int rayCount = 8)
        {
            Vector3 dir;
            if (rayId >= 0 && rayCount > 0)
            {
                float angle = (360f / rayCount) * rayId * Mathf.Deg2Rad;
                // A fixed reference can align with `up` (straight-down engine, near the
                // poles of this construction) and degenerate; picking whichever world axis
                // is LEAST parallel to `up` keeps the basis well-conditioned everywhere.
                Vector3 reference = Mathf.Abs(Vector3.Dot(up, Vector3.forward)) < 0.9f
                    ? Vector3.forward : Vector3.right;
                Vector3 tangent = Vector3.ProjectOnPlane(reference, up).normalized;
                Vector3 bitangent = Vector3.Cross(up, tangent);
                dir = tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle);
            }
            else if (padAxisValid)
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
                expansion = 1f,
                // Parametrised (28 IX): the landing splash wants a thin, fast-moving skirt
                // - real ground-effect gas is nowhere near as optically thick as the launch
                // column, it's just moving fast and low. Defaults to 1 so the original
                // pad-deflector caller (groundCloudEnabled, off by default) is unaffected.
                emitDensity = emitDensity,
                steam = steamFrac,
                bounced = true,  // already on the ground; no impingement impulse wanted
                isGround = true,
                groundRayId = rayId,
                rayDir = dir,
                groundTick = rayId >= 0 && rayCount > 0 ? groundSeqCounter++ / rayCount : 0
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
            return 1f + w * SmokeTuning.ColumnWander;
        }

        // Sizes arrive at Initialize, but they are live sliders now, so they have to be
        // refreshed rather than captured once. Existing puffs pick the change up on their
        // next SizeForPuff, which is what makes the slider readable while flying.
        // Cut loose from the controller: no more puffs will be added, but the existing
        // ones keep ageing, drifting and rendering until the last one expires, and then
        // this object removes itself. Ticked from Update rather than from the vessel,
        // because the vessel is exactly what has gone away.
        private bool orphaned;

        // Watchdog against a stale live tip - see StaleTipWatchdog below.
        private float lastLiveTipRefresh;
        // Generous next to a spawnInterval of ~0.08s: only meant to catch a controller
        // that stopped calling SetLiveTip altogether, not to react to one slow tick.
        private const float LiveTipStaleAfter = 0.5f;

        // the vessel was moved far away at once (a revert, a quickload, a teleport): the old trail belongs to a flight that no longer exists
        public void ClearPuffs()
        {
            puffs.Clear();
            hasLiveTip = false;
            liveRootCount = 0;
        }

        public void Orphan()
        {
            orphaned = true;
            transform.SetParent(null, true);

            // The live tip MUST go with the controller that was feeding it.
            //
            // Every puff is stored in body-local coordinates and converted on read, so the
            // trail survives KSP recentring the world on the active vessel. liveTipPos is
            // the one exception: it is raw world space, which is harmless only because the
            // controller rewrites it every tick. Once orphaned nobody rewrites it, so the
            // scene slides out from under a frozen point and the spine stretches a segment
            // from the abandoned trail towards whatever the origin now follows - which
            // reads in game as a dead rocket's trail attaching itself to the one you fly.
            CommitLiveTip();
        }

        private void Update()
        {
            if (!ModSettings.Enabled) return;
            // Runs for EVERY instance, orphaned or not - a live tip left un-refreshed for
            // half a second gets frozen regardless of why. Orphan() already does this the
            // instant the controller is destroyed, but destruction ordering on a vessel
            // that explodes or crashes is not as reliable as a clean scene unload, and
            // this is what a missed or delayed call looks like: the point sits still while
            // the controller that should be re-aiming it every tick has gone quiet. That
            // is indistinguishable, from here, from "still attached to whatever the world
            // origin now follows" - the exact failure this exists to rule out.
            StaleTipWatchdog();

            if (!orphaned) return;
            if (puffs.Count == 0)
            {
                SmokeRenderRegistry.Remove(polylineRenderer);
                Destroy(gameObject);
                return;
            }
            Tick(Time.deltaTime);
        }

        private void StaleTipWatchdog()
        {
            if (!hasLiveTip) return;
            if (Time.time - lastLiveTipRefresh < LiveTipStaleAfter) return;

            Debug.LogWarning(string.Format(
                "[PlumeTrails] instance {0}: live tip stale for {1:F2}s, freezing it. "
                + "This should only ever be Orphan() reacting a little late - if it fires "
                + "on a vessel that is still flying, something upstream stopped calling "
                + "SetLiveTip without telling this group to stop.",
                instanceId, Time.time - lastLiveTipRefresh));
            CommitLiveTip();
        }

        // Growth timing is a live slider too; existing puffs pick it up on their next SizeForPuff.
        // Engine flame as a light source for the smoke. Strength already carries throttle and nozzle size.
        private Vector3 flamePos;
        private float flameStrength;
        private float flameRange = 25f;
        private Color flameCol = new Color(1f, 0.52f, 0.16f);
        public void SetFlameColor(Color c) { flameCol = c; }
        private Vector3 flameEnd;
        private bool flameSegment;
        public void SetFlameLight(Vector3 worldPos, float strength, float range)
        {
            flamePos = worldPos;
            flameStrength = strength;
            flameRange = range;
            flameSegment = false;
        }
        // the flame as a line of light: from the nozzle (worldPos) to the end of the flame / to where the jet meets the pad
        public void SetFlameLight(Vector3 worldPos, float strength, float range, Vector3 endPos)
        {
            SetFlameLight(worldPos, strength, range);
            flameEnd = endPos; flameSegment = true;
        }

        public void SetGrowth(float newSharpness, float newTime)
        {
            growthSharpness = newSharpness;
            growthTime = newTime;
        }

        // Share of the exhaust that is water vapour (liquid engines) for puffs and the live tip born from now on.
        private float steamFrac;
        private float steamWater = 0.12f;
        private float puffScale = 1f, puffVisDil = 0f;
        public void SetSteam(float s, float waterFrac, float visibleDilution = 0f, float radiusScale = 1f)
        {
            steamFrac = Mathf.Clamp01(s); steamWater = waterFrac;
            puffVisDil = visibleDilution; puffScale = Mathf.Max(radiusScale, 0.05f);
        }

        // density (0..1.4) and steam level packed into the spine w (see SpinePointDensity in the shader)
        private static float EncodeDensity(float d, float steam, float heat = 0f)
        {
            return Mathf.Clamp(d, 0f, 1.4f) + 2f * Mathf.RoundToInt(Mathf.Clamp01(steam) * 7f) + 16f * Mathf.RoundToInt(Mathf.Clamp01(heat) * 7f);
        }

        public void SetSizes(float newStartSize, float newMaxSize)
        {
            startSize = newStartSize;
            maxSize = newMaxSize;
        }

        public void AddPuff(Vector3 worldPos, Vector3 initialVelocity, float sizeScale = 1f, int burnId = 0, float expansion = 1f, float emitDensity = 1f)
        {
            // the legacy trail is hidden (the default since 1.0): it would never be drawn, so it is not simulated either
            if (SmokeTuning.HideOldTrail) return;
            Vector3 jitter = Random.insideUnitSphere * (startSize * PositionJitterFraction);
            puffs.Add(new Puff
            {
                localPos = WorldToLocal(worldPos + jitter),
                velocity = initialVelocity,
                age = 0f,
                sizeMultiplier = Random.Range(0.9f, 1.1f) * ColumnWander(nextSpawnIndex) * sizeScale,
                expansion = expansion,
                emitDensity = Mathf.Clamp01(emitDensity),
                steam = steamFrac,
                burnId = burnId,
                spawnIndex = nextSpawnIndex++
            });
        }

        // ---------------- REAL PHYSICS puffs ----------------
        private int physRayBudget;
        private float physLogTime = -999f;
        public float LastPhysR0;
        public float LastPhysR0Raw;
        public float LastPhysD;

        // Per-tick cost counters, printed by the controller's tick log (and reset there).
        public static int PerfSteps, PerfPqs, PerfRays;
        public static double PerfTickMs;

        // The atmosphere only depends on altitude, and a body's GetPressure / GetTemperature are not free (mods
        // that patch the atmosphere make them dearer still), while hundreds of puffs ask every tick. Cached on a
        // 4 m altitude grid and flushed twice a second.
        private readonly Dictionary<int, Vector3> ambCache = new Dictionary<int, Vector3>();
        private float ambCacheFlush;

        private PuffPhysics.Ambient SampleAmbient(Vector3 worldPos, double alt, Vector3 up, float height)
        {
            PuffPhysics.Ambient a = new PuffPhysics.Ambient();
            if (Time.time - ambCacheFlush > 0.5f) { ambCache.Clear(); ambCacheFlush = Time.time; }
            int key = (int)System.Math.Floor(alt * 0.25);
            Vector3 pt;
            if (!ambCache.TryGetValue(key, out pt))
            {
                double aq = key * 4.0 + 2.0;
                double pKpa = body.atmosphere ? body.GetPressure(aq) : 0.0;
                double tK = body.atmosphere ? body.GetTemperature(aq) : 3.0;
                pt = new Vector3(Mathf.Max((float)(pKpa * 1000.0), 0.5f), Mathf.Max((float)tK, 3f), 0f);
                ambCache[key] = pt;
            }
            a.P = pt.x;
            a.T = pt.y;
            a.rho = Mathf.Max(a.P / (PuffPhysics.RAir * a.T), 1e-6f);
            // surface wind, logarithmic profile, horizontal, along the configured drift direction
            Vector3 wd = Vector3.ProjectOnPlane(windDrift, up);
            if (wd.sqrMagnitude > 1e-6f && SmokeTuning.WindSpeed > 0.001f)
                a.wind = wd.normalized * PuffPhysics.WindAt(SmokeTuning.WindSpeed, height, Mathf.Max(SmokeTuning.PhysWindRoughness, 0.001f));
            return a;
        }

        private static PuffPhysics.Params PhysParams()
        {
            return new PuffPhysics.Params
            {
                alphaJet = SmokeTuning.PhysAlphaJet, alphaThermal = SmokeTuning.PhysAlphaThermal,
                ambientTurb = SmokeTuning.PhysAmbientTurb, addedMass = SmokeTuning.PhysAddedMass,
                groundRestitution = SmokeTuning.PhysRestitution, wallJetFraction = SmokeTuning.PhysWallJet,
                groundFriction = SmokeTuning.PhysGroundFriction
            };
        }

        // Releases a parcel of exhaust at the point where the jet has become smoke. u0 = velocity of the exhaust at
        // the nozzle relative to the AIR (vessel velocity + ve along the exhaust). The jet entrains air on the way
        // to xs (Ricou & Spalding), which dilutes and cools it and, by conservation of momentum, slows it by the
        // same factor.
        public void AddPhysicalJetPuff(Vector3 worldPos, Vector3 u0, float ve, float xs, float mdot0, float sliceDt, int burnId, float emitDensity)
        {
            double alt = body.GetAltitude(worldPos);
            Vector3 up = (worldPos - body.position).normalized;
            PuffPhysics.Ambient a = SampleAmbient(worldPos, alt, up, Mathf.Max((float)alt, 1f));
            float Te = SmokeTuning.PhysExhaustTemp;
            float rhoE = a.P / (PuffPhysics.RExhaust * Te);
            // Equivalent nozzle exit diameter from mass continuity, mdot = rho_e * (pi d^2 / 4) * ve. Derived from the
            // thrust and Isp KSP reports, so it is consistent with the engine whatever size its part model has
            // (the part's visual width is not the nozzle exit).
            float dEff = Mathf.Sqrt(4f * Mathf.Max(mdot0, 1f) / (Mathf.PI * rhoE * Mathf.Max(ve, 100f)));
            LastPhysD = dEff;
            float D0 = PuffPhysics.JetDilution(xs, dEff, a.rho, rhoE, SmokeTuning.PhysMaxDilution);
            float T0 = (Te + (D0 - 1f) * a.T) / D0;
            float M0 = Mathf.Max(D0 * mdot0 * sliceDt, 1f);
            float Me = M0 / D0;
            float rho0 = PuffPhysics.Density(a.P, T0, Me, M0);
            float r0 = PuffPhysics.RadiusFromMass(M0, rho0);
            LastPhysR0 = r0 * SmokeTuning.PhysRadiusScale * puffScale;
            LastPhysR0Raw = r0;

            Vector3 jitter = Random.insideUnitSphere * (r0 * 0.15f);
            puffs.Add(new Puff
            {
                localPos = WorldToLocal(worldPos + jitter),
                velocity = u0 / D0,
                age = 0f,
                sizeMultiplier = 1f,
                expansion = 1f,
                emitDensity = emitDensity,
                steam = steamFrac,
                fw = steamWater,
                pVis = 1f,
                pScale = puffScale,
                pVisDil = puffVisDil,
                burnId = burnId,
                spawnIndex = nextSpawnIndex++,
                physical = true,
                pM = M0, pMe = Me, pT = T0, pM0 = M0, pR = r0, pR0 = r0
            });

            if (Time.time - physLogTime > 2f)
            {
                physLogTime = Time.time;
                Debug.Log(string.Format(
                    "[PlumeTrails] phys release: xs={0:F0}m d={1:F1}m dilution={2:F1} T0={3:F0}K v0={4:F0}m/s M0={5:F0}kg r0={6:F1}m rhoAmb={7:F3} mdot0={8:F0}kg/s",
                    xs, dEff, D0, T0, (u0 / D0).magnitude, M0, r0, a.rho, mdot0));
            }
        }

        // WALL JET BRANCHES. A parcel that has hit the ground, the pad or a trench wall is no longer part of the
        // rising column: it is a ground-hugging flow going OUT in one direction. Left in the single time-ordered
        // chain it made the chain zigzag between the two sides of the pad (the "noodles"). It now moves into the
        // ground-ray machinery instead: one chain per outflow sector (two when a flame trench was surveyed - one
        // each way along it - otherwise WallSectors around the impact), sorted outward by age. Physics keeps
        // running on it (entrainment, buoyancy, friction), so each branch widens and lifts into a side cloud.
        // Glow of a hot ground-hugging parcel (the flame turned by the deflector): by its own temperature, from about
        // 700 K (dull ember) up to 2200 K (white-yellow). Cools as it entrains air, so it fades into the steam.
        // Each billow of a ground-hugging flow has its own size (0.65 .. 1.35 x): equal blobs at equal spacing merged into a smooth
        // tube (a "noodle"); unequal ones read as the lumpy, rolling front a real wall jet has.
        private static float BillowJitter(int index)
        {
            float h = Mathf.Sin(index * 12.9898f) * 43758.5453f;
            return 0.65f + 0.7f * (h - Mathf.Floor(h));
        }

        private static float WallHeat(Puff p)
        {
            if (!p.physical || !p.isGround || SmokeTuning.DeflectorFire <= 0f) return 0f;
            return Mathf.Clamp01((p.pT - 700f) / 1500f) * Mathf.Min(SmokeTuning.DeflectorFire, 1.5f);
        }

        // the real surface under a firing engine, set by the controller (altitude above the datum, position, time)
        private static bool padSurfValid;
        private static Vector3 padSurfPos;
        private static double padSurfAlt;
        private static float padSurfTime;
        public static void NotePadSurface(Vector3 worldPoint)
        {
            CelestialBody b = FlightGlobals.currentMainBody;
            if (b == null) return;
            padSurfPos = worldPoint; padSurfAlt = b.GetAltitude(worldPoint);
            padSurfTime = Time.time; padSurfValid = true;
        }

        // ---- SURFACE STREAMS ----
        private class StreamSource
        {
            public Vector3 local;                 // where the jet lands (group-local, so that it survives origin shifts)
            public float r0 = 6f, v0 = 40f;       // radius of the jet where it lands, speed of the flow along the surface
            public float born, lastSeen, traceT;
            public Vector3 tracedAt;
            public int streams;
            public bool axesOk, hasB; public float azA, azB; public Vector3 axesAt;     // the flow axes found in the geometry (the trench), see ScanAxes
            public Vector3[][] path; public float[][] clear; public int[] count;
            public float[][] widthH, head;       // the room the flow has: the free width at 4 heights above it (per point), the headroom
            public float[][] rad, dn, tArr, tK;  // the stream tube (StreamTube): radius, opacity of the smoke, the time the front gets there, buoyancy g' (m/s2), per point
        }
        private readonly List<StreamSource> streamSources = new List<StreamSource>();
        private static readonly float[] StreamSched = { 0f, 6f, 15f, 28f, 46f, 72f, 108f, 155f, 215f };
        private const float StreamFrontScale = 100f;   // the front slows down: s(t) = L ln(1 + v0 t / L)

        private void NoteStreamImpact(Vector3 hitLocal, float radius, float speed)
        {
            float now = Time.time;
            StreamSource best = null; float bd = 10f * 10f;
            for (int i = 0; i < streamSources.Count; i++)
            {
                float d = (streamSources[i].local - hitLocal).sqrMagnitude;
                if (d < bd) { bd = d; best = streamSources[i]; }
            }
            if (best == null)
            {
                if (streamSources.Count >= 8) return;
                best = new StreamSource { local = hitLocal, born = now, lastSeen = now, r0 = radius, v0 = Mathf.Clamp(speed, 25f, 120f) };
                streamSources.Add(best);
                return;
            }
            if (now - best.lastSeen > 3f) best.born = now;          // a new ignition
            best.local = Vector3.Lerp(best.local, hitLocal, 0.08f);
            best.r0 = Mathf.Lerp(best.r0, radius, 0.1f);
            best.v0 = Mathf.Lerp(best.v0, Mathf.Clamp(speed, 25f, 120f), 0.1f);
            best.lastSeen = now;
        }

        private readonly Vector3[] streamTmpW = new Vector3[16];
        private readonly float[] streamTmpC = new float[16];
        private readonly float[] streamTmpWH = new float[64], streamTmpHd = new float[16];

        // Each source throws a fan of streamlines along the pad surface (PadFluid.TraceStream). A stream is one chain of the spine: radius grows
        // with the distance like a wall jet / steam cloud (and never exceeds what the walls around it allow), the front advances and slows down,
        // and when the jet is gone the tail leaves the source and the whole thing fades.
        // debug: what the last EmitStreams did (why it drew nothing, or how much)
        public static string DbgEmit = "never called"; private static int dbgCalls;
        // THE STREAM TUBE. The flow along one ray is integrated like a real turbulent wall jet, a tube with a cross-section that changes along its way, and with
        // the WATER of the deluge in it (a pad is flooded with it: several times the mass of the exhaust, which is what makes the cloud):
        //   mass       d(m)/ds = rho_a E u P_free          the air it entrains over the part of its surface that is free (not against the floor or a wall)
        //   momentum   d(J)/ds = - Cf rho u^2 / 2 P_wet    the friction of the floor and the walls; the entrained air starts at rest, so J = m u is kept
        //   enthalpy   H = m_dry cp T + m_w cw T + L m_v   the heat of the gas goes into the water: it evaporates (m_v, up to saturation at T) and cools the gas;
        //                                                  T is found from H at every step, and when the mixture cools the vapour condenses and gives the heat back
        //   area       A = Q / u,  Q = (m_dry Rd + m_v Rv) T / p   the volume of the gas (the droplets take no room); the radius is that of a circle of that area,
        //                                                  limited by the room (half of the free width at the height of the stream, 0.8 of the headroom)
        // What is SEEN: the alumina of the exhaust (a share of the flow) and the fine MIST (a small share of the liquid; the rest is spray that rains out) over
        // the volume Q, sigma = kappa_p c_p + kappa_d c_mist, and the picture's opacity is sigma / (sigma + sigma_ref) (it falls gradually as the stream thins out,
        // instead of staying a solid white as long as the extinction is over 1/m). The buoyancy is g' = g (rho_a - rho_eff) / rho_eff with the load of the mist in
        // rho_eff: warm gas and vapour rise, a cold wet stream does not. The front of the cloud travels at 0.7 of the local speed of the flow.
        private const float TubeE = 0.08f, TubeCf = 0.005f, TubeKappaP = 2000f, TubeKappaD = 150f, TubeParticleShare = 0.09f;
        private const float MCp = 1050f, MCw = 4186f, MLv = 2.5e6f, MRd = 287f, MRv = 461.5f, MPa = 101325f, MTa = 288f;

        private static float QSat(float T)
        {
            float es = 611.2f * Mathf.Exp(17.67f * (T - 273.15f) / (T - 29.65f));
            if (es >= 0.95f * MPa) return 1000f;
            return 0.622f * es / (MPa - es);
        }

        // the temperature at which the mixture holds the enthalpy H (the vapour is whatever the water and the saturation allow)
        private static float SolveMoistT(float H, float md, float mw, out float mv)
        {
            float lo = 200f, hi = 1500f;
            for (int it = 0; it < 22; it++)
            {
                float mid = 0.5f * (lo + hi);
                float v = Mathf.Min(mw, md * QSat(mid));
                float h = md * MCp * mid + mw * MCw * mid + MLv * v;
                if (h < H) lo = mid; else hi = mid;
            }
            float T = 0.5f * (lo + hi);
            mv = Mathf.Min(mw, md * QSat(T));
            return T;
        }

        private void StreamTube(StreamSource src, int k, int c, int nRays, Vector3 w)
        {
            float[] rad = new float[c], dn = new float[c], tA = new float[c], gpA = new float[c];
            float rhoA = MPa / (MRd * MTa);
            float r0 = Mathf.Clamp(src.r0, 3f, 12f), u0 = Mathf.Max(src.v0, 10f);
            float T0g = Mathf.Max(SmokeTuning.PadStreamT0, 350f);
            float m0 = MPa / (MRd * T0g) * Mathf.PI * r0 * r0 * u0 / Mathf.Max(nRays, 1);          // this ray's share of the flow where the jet lands
            float md = m0, mw = Mathf.Max(SmokeTuning.PadStreamWater, 0f) * m0;                    // the dry gas and the water, kg/s
            float mp = TubeParticleShare * m0;                                                    // the particles per second (they do not mix away)
            float H = md * MCp * T0g + mw * MCw * 300f;
            float mv; float T = SolveMoistT(H, md, mw, out mv);
            float J = (md + mw) * u0, tt = 0f;
            float rPrev = r0 / Mathf.Sqrt(Mathf.Max(nRays, 1));
            float mist = Mathf.Clamp01(SmokeTuning.PadMistFraction);
            for (int j = 0; j < c; j++)
            {
                if (j > 0)
                {
                    float sA = StreamSched[j - 1], sB = StreamSched[j];
                    int nsub = Mathf.Max(1, Mathf.CeilToInt((sB - sA) / 2f));
                    float dsub = (sB - sA) / nsub;
                    for (int q = 0; q < nsub; q++)
                    {
                        float f = (q + 0.5f) / nsub;
                        float mtot = md + mw;
                        float u = Mathf.Max(J / mtot, 1.5f);
                        float Q = (md * MRd + mv * MRv) * T / MPa;
                        float A = Q / u;
                        float rFree = Mathf.Sqrt(A / Mathf.PI);
                        float hq = Mathf.Clamp(rFree * 1.4f, 1f, 12f);
                        float hf = hq <= 3f ? Mathf.InverseLerp(1f, 3f, hq) : hq <= 6f ? 1f + Mathf.InverseLerp(3f, 6f, hq) : 2f + Mathf.InverseLerp(6f, 12f, hq);
                        int h0 = Mathf.Clamp(Mathf.FloorToInt(hf), 0, 2); float hfr = Mathf.Clamp01(hf - h0);
                        float room = Mathf.Lerp(Mathf.Lerp(src.widthH[k][(j - 1) * 4 + h0], src.widthH[k][j * 4 + h0], f), Mathf.Lerp(src.widthH[k][(j - 1) * 4 + h0 + 1], src.widthH[k][j * 4 + h0 + 1], f), hfr);
                        float roof = Mathf.Lerp(src.head[k][j - 1], src.head[k][j], f);
                        float rCap = Mathf.Max(1.2f, Mathf.Min(0.5f * room, 0.8f * roof));
                        float r = Mathf.Min(rFree, rCap);
                        bool boxed = rFree > rCap * 0.97f;
                        float cf = 0.25f + (boxed ? 0.45f : 0f);                 // the part of the surface that touches the floor and the walls
                        float dm = rhoA * TubeE * u * 2f * Mathf.PI * r * (1f - cf) * dsub;
                        float dJ = -0.5f * TubeCf * (mtot / Mathf.Max(Q, 1e-3f)) * u * u * 2f * Mathf.PI * r * cf * dsub;
                        H += dm * MCp * MTa;
                        md += dm;
                        J = Mathf.Max(J + dJ, 0.02f * (md + mw) * u0);
                        T = SolveMoistT(H, md, mw, out mv);
                        tt += dsub / Mathf.Max(0.7f * u, 1f);
                        rPrev = r;
                    }
                }
                float mt = md + mw;
                float Qn = (md * MRd + mv * MRv) * T / MPa;
                float mMist = mist * Mathf.Max(mw - mv, 0f);
                float rhoEff = (md + mv + mMist) / Mathf.Max(Qn, 1e-3f);            // the gas, the vapour and the load of the mist
                float gp = 9.81f * (rhoA - rhoEff) / Mathf.Max(rhoEff, 0.05f);
                float sigma = (TubeKappaP * mp + TubeKappaD * mMist) / Mathf.Max(Qn, 1e-3f);        // extinction, 1/m
                rad[j] = Mathf.Min(Mathf.Max(rPrev, 0.6f), 14f);
                // no axis in the geometry (open ground): the wall jet spreads the same in all directions, as a DISC, not as twelve separate petals. Each ray
                // then covers the arc between it and its neighbours: the radius at a distance s is at least the half-gap of the fan there.
                if (!src.axesOk) rad[j] = Mathf.Min(Mathf.Max(rad[j], 0.85f * StreamSched[j] * Mathf.PI / Mathf.Max(nRays, 1)), 40f);
                float sref = Mathf.Max(SmokeTuning.PadOpacityRef, 0.5f);
                dn[j] = sigma / (sigma + sref);
                tA[j] = tt; gpA[j] = gp;
            }
            src.rad[k] = rad; src.dn[k] = dn; src.tArr[k] = tA; src.tK[k] = gpA;
        }

        // the arc length the front has reached after `age` seconds (the table of arrival times of the stream tube, StreamSched[j] -> tA[j])
        private static float FrontAt(float[] tA, int c, float age)
        {
            if (tA == null || c < 2) return 0f;
            if (age <= 0f) return 0f;
            for (int j = 1; j < c; j++)
                if (age < tA[j]) return Mathf.Lerp(StreamSched[j - 1], StreamSched[j], Mathf.InverseLerp(tA[j - 1], tA[j], age));
            return StreamSched[c - 1];
        }

        // THE SMOKE OF EVERY NOZZLE. A cluster of engines is one group with one trail, but the jets that make it are separate at first: each has its own radius
        // (that of its nozzle), widens at the rate of a free jet (0.1 m per metre) and meets its neighbours where the radii touch, after
        //     s_m = (d / 2 - r0) / 0.1                     d = the distance to the nearest nozzle, r0 = the radius of the nozzle
        // and from there they are one jet (the mixing: the lateral offset of a thread from the axis goes to zero over about s_m, and its density is taken over by the
        // main trail). The threads are chains of the same volume, laid along the beginning of the main trail (the end of the list, at the nozzles).
        private readonly List<Vector3> nozLat = new List<Vector3>();
        private readonly List<float> nozR = new List<float>();
        private readonly Vector3[] thPos = new Vector3[64]; private readonly float[] thR = new float[64], thD = new float[64], thCum = new float[64];

        public void SetNozzles(List<EngineSample> smp, Vector3 centroid, Vector3 fwd)
        {
            nozLat.Clear(); nozR.Clear();
            if (smp == null || smp.Count < 2) return;
            for (int i = 0; i < smp.Count && i < 40; i++)
            {
                nozLat.Add(Vector3.ProjectOnPlane(smp[i].position - centroid, fwd));
                nozR.Add(Mathf.Max(0.4f, 0.5f * smp[i].nozzleDiameter));
            }
        }

        private static float ScalePackedDensity(float packed, float k)
        {
            float hh = Mathf.Floor(packed / 16f), rem = packed - 16f * hh, ss = Mathf.Floor(rem / 2f), dd = rem - 2f * ss;
            return dd * k + 2f * ss + 16f * hh;
        }

        private static readonly float[] ThreadFrac = { 1.7f, 1.25f, 0.85f, 0.5f, 0.22f, 0f };      // where the threads are laid, in merge lengths from the nozzle end, far to near
        private void EmitNozzleThreads(int shareBudget)
        {
            int n = nozLat.Count;
            if (n < 2 || !SmokeTuning.NozzleThreads) return;
            int cnt = activeOrderedPos.Count;
            if (cnt < 3) return;
            int maxThreads = Mathf.Min(n, Mathf.Max(0, (int)(shareBudget * 0.42f / 8f)));
            if (maxThreads < 2) return;
            // the merge length of every nozzle, from the distance to the nearest neighbour
            float sMaxNeed = 0f;
            float[] sm = new float[n];
            for (int i = 0; i < n; i++)
            {
                float dn = float.MaxValue;
                for (int j = 0; j < n; j++) if (j != i) dn = Mathf.Min(dn, (nozLat[i] - nozLat[j]).magnitude);
                sm[i] = Mathf.Clamp((0.5f * dn - nozR[i]) / 0.1f, 2f, 150f);
                sMaxNeed = Mathf.Max(sMaxNeed, 1.75f * sm[i]);
            }
            // the head of the main trail: from the newest end back, until a break or far enough
            int hc = 0; float cum = 0f;
            for (int i = cnt - 1; i >= 0 && hc < 64; i--)
            {
                if (activeOrderedRadius[i] <= BreakRadius) break;
                thPos[hc] = activeOrderedPos[i]; thR[hc] = activeOrderedRadius[i]; thD[hc] = activeOrderedDensity[i];
                if (hc > 0) cum += (thPos[hc] - thPos[hc - 1]).magnitude;
                thCum[hc] = cum;
                hc++;
                if (cum > sMaxNeed) break;
            }
            if (hc < 2) return;
            // the main trail gives way to the threads near the nozzles: it takes the whole density only where they have mixed
            float smAvg = 0f; for (int i = 0; i < n; i++) smAvg += sm[i]; smAvg /= n;
            for (int q = 0; q < hc; q++)
            {
                float m = Mathf.SmoothStep(0f, 1f, (thCum[q] - 0.4f * smAvg) / (1.2f * smAvg));
                activeOrderedDensity[cnt - 1 - q] = ScalePackedDensity(activeOrderedDensity[cnt - 1 - q], Mathf.Lerp(0.3f, 1f, m));
            }
            int step = Mathf.Max(1, n / maxThreads);
            for (int ti = 0, emitted = 0; ti < n && emitted < maxThreads; ti += step, emitted++)
            {
                float s_m = sm[ti], r0 = nozR[ti];
                Vector3 lastP = Vector3.zero; bool first = true;
                for (int f = 0; f < ThreadFrac.Length; f++)
                {
                    float s = ThreadFrac[f] * s_m;
                    if (s > thCum[hc - 1]) s = thCum[hc - 1];
                    int k = 1; while (k < hc - 1 && thCum[k] < s) k++;
                    float span = Mathf.Max(thCum[k] - thCum[k - 1], 1e-3f);
                    float t = Mathf.Clamp01((s - thCum[k - 1]) / span);
                    Vector3 pos = Vector3.Lerp(thPos[k - 1], thPos[k], t);
                    Vector3 tan = (thPos[k] - thPos[k - 1]); tan = tan.sqrMagnitude > 1e-8f ? tan.normalized : Vector3.up;
                    float rMain = Mathf.Lerp(thR[k - 1], thR[k], t);
                    float dPacked = Mathf.Lerp(thD[k - 1], thD[k], t);
                    float hhh = Mathf.Floor(dPacked / 16f), rem = dPacked - 16f * hhh, sss = Mathf.Floor(rem / 2f), dMain = rem - 2f * sss;
                    float m = Mathf.SmoothStep(0f, 1f, (s - 0.4f * s_m) / (1.2f * s_m));
                    Vector3 lat = nozLat[ti] - tan * Vector3.Dot(nozLat[ti], tan);
                    Vector3 p = pos + lat * (1f - m);
                    float r = Mathf.Min(r0 + 0.1f * s, Mathf.Max(rMain, r0));
                    float dens = Mathf.Min(1.2f, dMain * Mathf.Min(1f, (rMain * rMain) / (n * r * r)) * (1f - m));
                    if (first) { activeOrderedPos.Add(p); activeOrderedRadius.Add(0f); activeOrderedDensity.Add(1f); first = false; }
                    activeOrderedPos.Add(p); activeOrderedRadius.Add(r); activeOrderedDensity.Add(EncodeDensity(Mathf.Max(dens, 0.01f), sss / 7f, hhh / 7f));
                    lastP = p;
                }
                activeOrderedPos.Add(lastP); activeOrderedRadius.Add(0f); activeOrderedDensity.Add(1f);
            }
            needTrailSeparator = true;
        }

        private const int StreamReserve = 96;

        // THE AXES OF THE FLOW. The jet that lands in a flame trench does not spread evenly in all directions: the walls turn it, and what comes out goes along
        // the trench, both ways (a fan of rays at even angles gave three fat lobes 120 degrees apart that had nothing to do with the trench). So the geometry
        // decides: a fan of 16 test rays is traced through the voxels, and the direction in which the flow gets farthest while it is still between walls is the
        // first axis, the farthest one that goes the opposite way the second (the other end of the trench). The streams are then a bundle along them.
        private static readonly Vector3[] scanDisp = new Vector3[16];
        private static readonly float[] scanLen = new float[16];
        private void ScanAxes(StreamSource src, Vector3 w, Vector3 up)
        {
            const int NA = 16;
            src.axesAt = w; src.axesOk = false; src.hasB = false;
            for (int a = 0; a < NA; a++)
            {
                scanLen[a] = 0f;
                float az = a * Mathf.PI * 2f / NA;
                int c = PadFluid.TraceStream(w, az, 1.25f, StreamSched, StreamSched.Length, streamTmpW, streamTmpC);
                if (c < 3) continue;
                scanDisp[a] = Vector3.ProjectOnPlane(streamTmpW[c - 1] - w, up);
                int conf = 0; float rise = 0f;
                for (int j = 0; j < c; j++) { if (streamTmpC[j] < 22f) conf++; rise = Mathf.Max(rise, Vector3.Dot(streamTmpW[j] - w, up)); }
                // a ray that climbed a wall and flew over it is not a flow along the geometry: the farther it climbed, the less it counts
                scanLen[a] = scanDisp[a].magnitude * (0.35f + 0.65f * conf / c) * Mathf.Exp(-Mathf.Max(rise - 2f, 0f) / 4f);
            }
            int ia = -1; float bl = 0f;
            for (int a = 0; a < NA; a++) if (scanLen[a] > bl) { bl = scanLen[a]; ia = a; }
            if (ia < 0 || bl < 8f) return;
            int ib = -1; float bl2 = 0.3f * bl;
            for (int a = 0; a < NA; a++) if (scanLen[a] > bl2 && Vector3.Dot(scanDisp[a], scanDisp[ia]) < 0f) { bl2 = scanLen[a]; ib = a; }
            src.azA = ia * Mathf.PI * 2f / NA; src.axesOk = true;
            if (ib >= 0) { src.azB = ib * Mathf.PI * 2f / NA; src.hasB = true; }
        }
        // The pad flow has the priority for the points of the group only while the rocket is still at the pad: when the live end of the trail is far from the
        // place where the jet lands (hundreds of metres: the rocket has lifted off) the trail needs the points, and what is left of the pad cloud gets a small share.
        // Without this the reserved share (96 points) and three quarters of the rest went to a cloud that the rocket had left, and the trail of the climbing rocket
        // came out as a stub of 20-30 points: it appeared, thinned out, and only a blob at the engine was left.
        private bool PadFlowIsNearTip()
        {
            if (!hasLiveTip || streamSources.Count == 0 || body == null) return true;
            for (int i = 0; i < streamSources.Count; i++)
                if ((LocalToWorld(streamSources[i].local) - liveTipPos).sqrMagnitude < 300f * 300f) return true;
            return false;
        }

        public bool HasLiveStreams(float within)
        {
            float now = Time.time;
            for (int i = 0; i < streamSources.Count; i++) if (now - streamSources[i].lastSeen < within) return PadFlowIsNearTip();
            return false;
        }

        private void EmitStreams(int shareBudget)
        {
            float now = Time.time;
            dbgCalls++;
            int dbgStart = activeOrderedPos.Count;
            for (int i = streamSources.Count - 1; i >= 0; i--)
                if (now - streamSources[i].lastSeen > 30f) streamSources.RemoveAt(i);
            int ns = streamSources.Count;
            if (ns == 0 || !PadFluid.SurfaceReady) { DbgEmit = "calls " + dbgCalls + ": returned, sources " + ns + ", SurfaceReady " + PadFluid.SurfaceReady; return; }
            // The streams share the spine with the trail of the same group, and the trail must not be starved by them (the log: a trail budget
            // of 23 points with 216 of the stream, so the trail was thinned to a few puffs that came and went). A third of this group's share while
            // the jet is still on the pad, an eighth once it is gone (the cloud lingers, but it is a ghost of what it was).
            bool anyLive = false;
            for (int i = 0; i < ns; i++) if (now - streamSources[i].lastSeen < 1f) { anyLive = true; break; }
            if (anyLive && !PadFlowIsNearTip()) anyLive = false;
            // the share of a group that is the source of the flow goes mostly to the flow (its own trail is a few metres long on the pad); the cloud that
            // lingers after the jet is gone keeps a floor so it does not vanish the moment the share shrinks
            int streamBudget = Mathf.RoundToInt(shareBudget * (anyLive ? 0.72f : (HasLiveStreams(12f) ? 0.4f : 0.2f)));
            if (!PadFlowIsNearTip()) streamBudget = Mathf.Min(streamBudget, Mathf.RoundToInt(shareBudget * 0.12f));       // the rocket is gone: the trail comes first
            else if (streamBudget < 28 && shareBudget >= 28) streamBudget = 28;
            if (streamBudget < 20) { DbgEmit = "calls " + dbgCalls + ": stream budget " + streamBudget + " < 20 (share " + shareBudget + ")"; return; }
            int nStreams = Mathf.Clamp(streamBudget / (ns * 10), 2, 12);
            if (nStreams > 2) nStreams &= ~1;                      // even numbers only: the fan is not re-traced for every point of the budget
            int traced = 0;
            float maxS = StreamSched[StreamSched.Length - 1];
            bool any = false;
            for (int si = 0; si < ns; si++)
            {
                StreamSource src = streamSources[si];
                Vector3 w = LocalToWorld(src.local);
                Vector3 up = (w - body.position).normalized;
                if ((src.path == null || src.streams != nStreams || (src.tracedAt - w).sqrMagnitude > 4f || now - src.traceT > 2f) && traced < 1)
                {
                    traced++;
                    src.streams = nStreams; src.tracedAt = w; src.traceT = now;
                    src.path = new Vector3[nStreams][]; src.clear = new float[nStreams][]; src.count = new int[nStreams];
                    src.widthH = new float[nStreams][]; src.head = new float[nStreams][];
                    src.rad = new float[nStreams][]; src.dn = new float[nStreams][]; src.tArr = new float[nStreams][]; src.tK = new float[nStreams][];
                    if (!src.axesOk || (src.axesAt - w).sqrMagnitude > 16f) ScanAxes(src, w, up);
                    for (int k = 0; k < nStreams; k++)
                    {
                        float az, tilt;
                        if (src.axesOk)
                        {
                            // a bundle of near-parallel rays along each axis (a few degrees apart, at different heights above the floor)
                            int axis = src.hasB ? (k & 1) : 0, idx = src.hasB ? (k >> 1) : k, per = src.hasB ? (nStreams + 1) / 2 : nStreams;
                            float off = per > 1 ? idx / (float)(per - 1) - 0.5f : 0f;
                            az = (axis == 0 ? src.azA : src.azB) + off * 0.3f;
                            tilt = Mathf.Lerp(1.0f, 1.45f, (idx * 0.618f) % 1f);
                        }
                        else
                        {
                            az = k * Mathf.PI * 2f / nStreams;
                            tilt = (k & 1) == 0 ? 0.95f : 1.25f;          // no axis found: a fan, 54 or 72 degrees from the jet axis
                        }
                        int c = PadFluid.TraceStream(w, az, tilt, StreamSched, StreamSched.Length, streamTmpW, streamTmpC, streamTmpWH, streamTmpHd);
                        src.count[k] = c;
                        src.path[k] = new Vector3[c]; src.clear[k] = new float[c]; src.widthH[k] = new float[c * 4]; src.head[k] = new float[c];
                        for (int j = 0; j < c; j++)
                        {
                            src.path[k][j] = streamTmpW[j]; src.clear[k][j] = streamTmpC[j]; src.head[k][j] = streamTmpHd[j];
                            for (int hi = 0; hi < 4; hi++) src.widthH[k][j * 4 + hi] = streamTmpWH[j * 4 + hi];
                        }
                        StreamTube(src, k, c, nStreams, w);
                    }
                }
                if (src.path == null) continue;

                float lost = Mathf.Max(0f, now - src.lastSeen - 0.5f);
                // once the jet is gone the cloud is a thermal that goes on mixing with the air: it widens (r ~ t^0.7) and the same smoke fills more room (the density
                // falls with the area), and it thins out with a time of about 45 s
                float life = Mathf.Exp(-lost / 45f);
                if (life <= 0.03f) continue;
                float sf = Mathf.Min(maxS, StreamFrontScale * Mathf.Log(1f + src.v0 * (now - src.born) / StreamFrontScale));
                float st = 0f;                                          // the cloud stays where it is and fades (it does not leave the pad)
                if (sf - st < 1f) continue;
                float r0s = Mathf.Clamp(src.r0, 3f, 12f);
                float grow = Mathf.Min(Mathf.Pow(1f + lost / 25f, 0.6f), 1.9f);
                float rise = 0.05f * lost;

                for (int k = 0; k < src.streams; k++)
                {
                    int c = src.count[k];
                    if (c < 2) continue;
                    float sEnd = Mathf.Min(FrontAt(src.tArr[k], c, now - src.born), StreamSched[c - 1]);
                    if (sEnd - st < 1f) continue;
                    bool first = true; Vector3 lastP = Vector3.zero;
                    // the samples: st, the scheduled distances between, sEnd
                    int jj = 0;
                    while (jj < c && StreamSched[jj] <= st + 0.01f) jj++;
                    // THE RADIUS OF THE STREAM, from physics. A turbulent jet entrains the air around it and widens at a rate of about 0.1 m per metre (the half-angle
                    // of a round jet, 5-6 degrees); it cannot widen into a wall, so where the geometry leaves less room than that the stream FILLS the room (half of the
                    // free width, and of the headroom) and goes on in it, and where the room grows again (it comes out of the trench) it widens again from the size it
                    // has. A wall blocks it only if it reaches as high as the stream is thick: the free width is measured at the stream's own height.
                    float sCur = st;
                    while (true)
                    {
                        // position and clearance at sCur by interpolation along the traced points
                        int a = Mathf.Clamp(jj - 1, 0, c - 2);
                        float t = Mathf.InverseLerp(StreamSched[a], StreamSched[a + 1], sCur);
                        Vector3 pos = Vector3.Lerp(src.path[k][a], src.path[k][a + 1], t);
                        float r = Mathf.Lerp(src.rad[k][a], src.rad[k][a + 1], t);
                        r *= grow;
                        float tip = Mathf.Clamp01((sEnd - sCur) / Mathf.Max(8f, 0.15f * sEnd));
                        float tail = st > 0f ? Mathf.Clamp01((sCur - st) / Mathf.Max(8f, 0.15f * st)) : 1f;
                        r *= Mathf.Lerp(0.25f, 1f, tip) * Mathf.Lerp(0.25f, 1f, tail);
                        // BILLOWS: the eddies of a turbulent stream are of its own size and they grow with the distance, so the radius swells and narrows along it, and the more
                        // the farther it is from the nozzle: a string of round lumps (cumulus) instead of a smooth tube
                        float billowAmp = Mathf.Lerp(0.10f, 0.24f, Mathf.Clamp01(sCur / 120f));
                        r *= 1f + billowAmp * (0.65f * Mathf.Sin(sCur / Mathf.Max(0.9f * r, 2f) + k * 2.1f + si * 1.3f) + 0.35f * Mathf.Sin(sCur / Mathf.Max(0.4f * r, 1.2f) + k * 3.7f + si * 0.6f));
                        float dens = Mathf.Clamp(Mathf.Lerp(src.dn[k][a], src.dn[k][a + 1], t), 0.03f, 1f) * life / (grow * grow);
                        float heat = 0f;
                        // BUOYANCY: the gas is warmer than the air, so lighter, and the stream lifts off the floor: a thermal of radius r rises at about sqrt(g' r),
                        // g' = g (T - Ta) / Ta, and the excess of heat is mixed away with a time of about 25 s (so the rise goes on slowing down). Where the stream
                        // has travelled for longer it has risen higher (and a cloud that lingers after the jet is gone goes on rising): it billows up along both
                        // sides of the trench instead of lying flat. The root, where the gas is young, stays at the floor.
                        float tPt = Mathf.Lerp(src.tArr[k][a], src.tArr[k][a + 1], t);
                        float agePt = tPt + lost;          // the flow is steady while the jet lasts: the gas at a point left the nozzle area tPt ago; when it ends, the time since adds
                        float gP = Mathf.Max(Mathf.Lerp(src.tK[k][a], src.tK[k][a + 1], t), 0f);
                        float w0 = Mathf.Sqrt(gP * Mathf.Max(r, 1f));
                        float lift = Mathf.Min(w0 * 14f * (1f - Mathf.Exp(-agePt / 30f)), 38f);
                        Vector3 centre = pos + up * (0.3f * r + rise + lift);
                        if (first)
                        {
                            activeOrderedPos.Add(centre); activeOrderedRadius.Add(0f); activeOrderedDensity.Add(1f);
                            first = false;
                        }
                        activeOrderedPos.Add(centre); activeOrderedRadius.Add(r); activeOrderedDensity.Add(EncodeDensity(dens, 1f, heat));
                        lastP = centre; any = true;
                        if (sCur >= sEnd - 0.01f) break;
                        float nextS = jj < c ? StreamSched[jj] : sEnd;
                        if (jj < c && StreamSched[jj] <= sCur + 0.01f) { jj++; nextS = jj < c ? StreamSched[jj] : sEnd; }
                        sCur = Mathf.Min(nextS, sEnd);
                    }
                    activeOrderedPos.Add(lastP); activeOrderedRadius.Add(0f); activeOrderedDensity.Add(1f);
                }
            }
            if (any) needTrailSeparator = true;
            DbgEmit = "calls " + dbgCalls + ": budget " + streamBudget + " (share " + shareBudget + "), rays/source " + nStreams + ", spine points added " + (activeOrderedPos.Count - dbgStart) + ", drawn " + any + ", this group " + (activeOrderedPos.Count) + " points, axes " + (streamSources[0].axesOk ? (streamSources[0].azA * Mathf.Rad2Deg).ToString("F0") + (streamSources[0].hasB ? "/" + (streamSources[0].azB * Mathf.Rad2Deg).ToString("F0") : " only") + " deg" : "none (a fan)");
        }

        private const int WallSectors = 8;
        private void MarkWallJet(ref Puff p, Vector3 up, Vector3 outward, bool viaScene = false, bool surface = false)
        {
            if (p.isGround || !SmokeTuning.PhysWallChains) return;
            // SURFACE STREAMS: the flow along the pad is not simulated parcel by parcel (that gave balls, loops and smoke through the walls): the
            // jet that reaches the surface only says WHERE it lands, and the flow is traced along the height map from there (EmitStreams)
            if (surface && SmokeTuning.PadStreams && !PadFluid.Drawing)
            {
                NoteStreamImpact(p.localPos, Mathf.Max(3f, p.pR * SmokeTuning.PhysRadiusScale * p.pScale), Vector3.ProjectOnPlane(p.velocity, up).magnitude);
                p.markedForRemoval = true;
                return;
            }
            // the pad cloud (billows on rails) draws the flow over the pad: the parcel that has reached it is done
            if (PadCloud.Active || PadFluid.Active) { p.markedForRemoval = true; return; }
            int sector;
            if (padAxisValid)
            {
                sector = Vector3.Dot(outward, padOutflowAxis) >= 0f ? 0 : 1;
            }
            else
            {
                Vector3 reference = Mathf.Abs(Vector3.Dot(up, Vector3.forward)) < 0.9f ? Vector3.forward : Vector3.right;
                Vector3 tangent = Vector3.ProjectOnPlane(reference, up).normalized;
                Vector3 bitangent = Vector3.Cross(up, tangent);
                float ang = Mathf.Atan2(Vector3.Dot(outward, bitangent), Vector3.Dot(outward, tangent));
                sector = Mathf.Clamp(Mathf.FloorToInt((ang + Mathf.PI) / (2f * Mathf.PI) * WallSectors), 0, WallSectors - 1);
            }
            // WATER DELUGE. A flame deflector is flooded with water to kill the sound; the flow turned along it is mostly
            // steam. The parcel takes on water here (condensation then decides how much of it is visible as a cloud),
            // and while it is still hot it glows (see WallHeat).
            if (SmokeTuning.PadWater && (padAxisValid || viaScene || SmokeTuning.PadWaterAlways))
            {
                p.steam = 1f;
                p.fw = Mathf.Max(p.fw, SmokeTuning.PadWaterFrac);
            }
            p.hitLocal = p.localPos;
            p.hitSpeed = Mathf.Max(5f, Vector3.ProjectOnPlane(p.velocity, up).magnitude);
            p.hitR0 = Mathf.Max(10f, 3f * p.pR);
            // A deluge-flooded pad turns ANY exhaust (hydrogen included, whose own plume is nearly invisible) into the white
            // steam of a real launch: the cloud is the vapour and condensate of the water, not of the engine's own smoke.
            if (SmokeTuning.PadWater && (padAxisValid || viaScene || SmokeTuning.PadWaterAlways))
                p.emitDensity = Mathf.Max(p.emitDensity, 0.8f);
            p.isGround = true;
            p.bounced = true;
            p.groundRayId = sector;
            p.rayDir = outward;
            p.groundTick = groundSeqCounter++ / WallSectors;
        }

        private static void EaseShown(ref Puff p, float dt)
        {
            float a = 1f - Mathf.Exp(-dt * 5f);
            if (p.pRs <= 0f) { p.pRs = p.pR; p.pVisS = p.pVis; return; }
            p.pRs = Mathf.Lerp(p.pRs, p.pR, a);
            p.pVisS = Mathf.Lerp(p.pVisS, p.pVis, a);
        }

        // One tick of a physical puff. Returns the new world position.
        private Vector3 StepPhysicalPuff(ref Puff p, Vector3 worldPos, Vector3 up, float dt)
        {
            double alt = body.GetAltitude(worldPos);
            float g = (float)(body.gravParameter / ((body.Radius + alt) * (body.Radius + alt)));
            PuffPhysics.State s = new PuffPhysics.State
            {
                vel = p.velocity, M = p.pM, Me = p.pMe, T = p.pT, M0 = p.pM0, radius = p.pR
            };
            PuffPhysics.Params prm = PhysParams();

            // ground height, only worth a terrain query near the surface
            double groundAlt = double.NegativeInfinity;
            if (alt < 600.0 + 3.0 * s.radius && body.pqsController != null)
            {
                // A terrain query is the dearest call in this loop, and the ground under a drifting puff changes
                // slowly: re-sample every ~0.25 s (staggered per puff so they do not all land on one tick).
                if (p.gTime <= 0f || Time.time - p.gTime > 0.25f + (p.spawnIndex % 5) * 0.04f)
                {
                    Vector3d radialDir = ((Vector3d)worldPos - body.position).normalized;
                    double ga = body.pqsController.GetSurfaceHeight(radialDir) - body.Radius;
                    if (body.ocean && ga < 0.0) ga = 0.0;
                    p.gAlt = (float)ga;
                    p.gTime = Time.time;
                    PerfPqs++;
                }
                groundAlt = p.gAlt;
            }
            float height = groundAlt > double.NegativeInfinity ? (float)(alt - groundAlt) : (float)alt;

            // old parcels move slowly: a coarser sub-step is plenty (their dt is also longer, see the stride in Tick)
            int sub = Mathf.Clamp(Mathf.CeilToInt(dt / (0.04f * Mathf.Clamp(p.age / 4f, 1f, 6f))), 1, 8);
            float h = dt / sub;
            Vector3 pos = worldPos;
            PuffPhysics.Ambient amb = default(PuffPhysics.Ambient);
            for (int k = 0; k < sub; k++)
            {
                amb = SampleAmbient(pos, alt, up, Mathf.Max(height, 1f));
                Vector3 velBefore = s.vel;
                PuffPhysics.Step(ref s, h, amb, up, g, prm);
                if (p.isGround && p.hitSpeed > 0f && SmokeTuning.PhysWallMomentum > 0f)
                {
                    // the sideways part of the flow is not slowed by the entrained air as a sphere would be (see PhysWallMomentum)
                    float vbU = Vector3.Dot(velBefore, up), vaU = Vector3.Dot(s.vel, up);
                    Vector3 hb = velBefore - up * vbU, ha = s.vel - up * vaU;
                    s.vel = ha + (hb - ha) * SmokeTuning.PhysWallMomentum + up * vaU;
                }
                if (p.isGround && SmokeTuning.PhysWallLift > 0f && Time.time > p.liftFreeUntil)
                {
                    // A wall jet clings to the surface (Coanda): it can leave it only as fast as it entrains air, so its climb
                    // is limited to a slope of ~PhysWallLift of its own sideways speed (plus a slow buoyant creep). Without it the
                    // hot flow shot up as a vertical wall instead of spreading along the pad and the trench.
                    float vUp = Vector3.Dot(s.vel, up);
                    float hSpd = (s.vel - up * vUp).magnitude;
                    float maxUp = SmokeTuning.PhysWallLift * hSpd + 1.5f;
                    if (vUp > maxUp) s.vel -= up * (vUp - maxUp);
                }
                if (p.isGround && p.hitSpeed > 0f && SmokeTuning.PhysWallDecay > 0f)
                {
                    // Wall jet similarity (Glauert): the sideways speed falls as r^-1.1 with the distance r from the impact
                    // (r0 = its length scale at the impact), so the flow is slow by ~100-150 m and the cloud billows up instead
                    // of running on over the edge of the pad.
                    float rr = Vector3.Distance(p.localPos, p.hitLocal);
                    float cap = p.hitSpeed * Mathf.Pow(Mathf.Min(1f, p.hitR0 / Mathf.Max(rr, p.hitR0)), 1.1f * SmokeTuning.PhysWallDecay);
                    float vU = Vector3.Dot(s.vel, up);
                    Vector3 hv = s.vel - up * vU;
                    float hs = hv.magnitude;
                    if (hs > cap && hs > 0.01f) s.vel = hv * (cap / hs) + up * vU;
                }
                pos += s.vel * h;
            }
            PerfSteps++;

            // Scene geometry first: the pad, a flame trench, buildings. The surface normal decides where the
            // flow goes (a slanted deflector turns it along the slope, walls confine it).
            bool hitScene = false;
            // SURFACE MODE owns the region of the pad grid: the colliders over a trench are a flat lid and the terrain under a mount is above
            // the trench floor, so neither may decide where the flow goes there - the pre-rendered height map does (see below)
            float aboveS = 1e9f;
            bool surfIn = SmokeTuning.PadSurface && PadFluid.SurfaceAbove(pos, out aboveS);
            // Near a pad whose real surface is a scenery mesh (not the analytic terrain): test the scene there too. Without
            // this a puff above a raised pad is "far above the ground" and flew straight through it.
            bool nearPadSurface = false;
            if (padSurfValid && Time.time - padSurfTime < 2f)
            {
                Vector3 dxz = Vector3.ProjectOnPlane(worldPos - padSurfPos, up);
                float dy = (float)(alt - padSurfAlt);
                nearPadSurface = dxz.sqrMagnitude < 400f * 400f && dy < 12f + 1.3f * s.radius && dy > -80f;
                // a flow already lying on the surface is tested every 2nd step only, and only while it still moves: a thousand
                // wall-jet parcels testing the scene every step cost 60-130 ms a tick
                if (nearPadSurface && p.isGround && (((p.spawnIndex + tickCounter) & 1) != 0 || p.age > 25f)) nearPadSurface = false;
            }
            if (!surfIn && SmokeTuning.PhysCollide && (physRayBudget > 0 || p.age < 1.5f) && ((groundAlt > double.NegativeInfinity && height < 12f + 1.3f * s.radius) || nearPadSurface))
            {
                Vector3 move = pos - worldPos;
                float dist = move.magnitude;
                if (dist > 0.05f)
                {
                    if (p.age >= 1.5f) physRayBudget--;   // a fresh parcel (the one meeting the pad now) always gets its test
                    PerfRays++;
                    RaycastHit hit;
                    if (Physics.Raycast(worldPos, move / dist, out hit, dist + 0.5f * s.radius, GetSceneryCollisionMask(), QueryTriggerInteraction.Ignore))
                    {
                        Vector3 n = hit.normal;
                        Vector3 tang = Vector3.ProjectOnPlane(s.vel, n);
                        Vector3 outward = tang.sqrMagnitude > 1f
                            ? tang.normalized
                            : (PadFluid.SurfaceReady ? Vector3.ProjectOnPlane(Random.onUnitSphere, n).normalized : Vector3.ProjectOnPlane(hit.point - padCentre, n).normalized);
                        if (outward.sqrMagnitude < 0.5f) outward = Vector3.ProjectOnPlane(Random.onUnitSphere, n).normalized;
                        PuffPhysics.Collide(ref s, n, outward, dt, prm);
                        pos = hit.point + n * Mathf.Min(0.6f * s.radius, 2f);
                        hitScene = true;
                        // the wall jet starts from the speed AFTER the turn (p.velocity is still the one before it: a vertical jet then looked like 5 m/s and the decay cap held the whole flow at 5 m/s)
                        p.velocity = s.vel;
                        MarkWallJet(ref p, up, outward, true);   // pad / trench / deflector geometry, not bare terrain
                    }
                }
            }

            // SURFACE MODE: the pad as the pre-rendered height map (the trench and the deflector have no colliders): the path of the parcel is
            // walked in 30 cm steps; where it goes into the surface, it is turned along it, so the smoke follows the geometry
            if (!hitScene && surfIn && p.age < 90f)
            {
                float above = aboveS;
                if (above < 14f + 1.3f * s.radius && above > -80f)
                {
                    Vector3 shit, snh;
                    if (PadFluid.SurfaceMarch(worldPos, pos, out shit, out snh))
                    {
                        Vector3 tang = Vector3.ProjectOnPlane(s.vel, snh);
                        // a jet that hits head-on has no sideways direction of its own: it spreads RADIALLY, in every direction (the point
                        // "away from the pad centre" was nearly the same for all the parcels of one jet, so they all went one way)
                        Vector3 outward = tang.sqrMagnitude > 1f ? tang.normalized : Vector3.ProjectOnPlane(Random.onUnitSphere, snh).normalized;
                        if (outward.sqrMagnitude < 0.5f) outward = Vector3.ProjectOnPlane(Random.onUnitSphere, snh).normalized;
                        // A jet that hits a WALL (the side of the trench, the mount) stagnates and splits: part of it runs along the wall sideways,
                        // and the part that the buoyancy of the hot gas lifts runs UP it. Without this a head-on hit only spread along the wall.
                        float steep = 1f - Mathf.Abs(Vector3.Dot(snh, up));
                        if (steep > 0.5f)
                        {
                            Vector3 upAlong = Vector3.ProjectOnPlane(up, snh).normalized;
                            outward = (outward + upAlong * (steep - 0.5f) * 1.6f).normalized;
                        }
                        PuffPhysics.Collide(ref s, snh, outward, dt, prm);
                        // a wall jet CLINGS to the surface: the small rebound of the ball model (restitution) threw the parcels up out of the
                        // trench (upward share 0.15-0.3 in the log); only a sliver of the speed away from the surface is kept
                        float vAway = Vector3.Dot(s.vel, snh);
                        if (vAway > 0f) s.vel -= snh * (vAway * 0.85f);
                        if (steep > 0.5f) p.liftFreeUntil = Time.time + 0.8f;       // running up a wall: not held down by the climb limit
                        pos = shit + snh * Mathf.Min(0.6f * s.radius, 2f);
                        hitScene = true;
                        if (SmokeTuning.PadFluidDebug)
                        {
                            DbgHitCount++; DbgHitSpeedSum += s.vel.magnitude; DbgHitUpSum += Vector3.Dot(s.vel.normalized, up);
                            if ((DbgHitCount & 3) == 0)
                            {
                                int di = DbgHitIdx++ % DbgHitMax;
                                DbgHitPos[di] = shit; DbgHitNormal[di] = snh; DbgHitVel[di] = s.vel; DbgHitTime[di] = Time.time;
                            }
                        }
                        p.velocity = s.vel;
                        MarkWallJet(ref p, up, outward, true, true);
                    }
                }
            }

            // ATTACHMENT (Coanda): a wall-jet parcel lying close to the surface that did not cross it this tick keeps no speed away from it
            if (!hitScene && surfIn && p.isGround && aboveS > 0f && aboveS < 2f * s.radius + 3f)
            {
                float a2; Vector3 n2;
                if (PadFluid.SurfaceQuery(pos, out a2, out n2))
                {
                    float vAway = Vector3.Dot(s.vel, n2);
                    if (vAway > 0f && Time.time > p.liftFreeUntil) s.vel -= n2 * (vAway * 0.6f);
                }
            }

            // terrain / sea level (not inside the pad grid in surface mode: the height map is the floor there, trench included)
            if (!hitScene && !surfIn && groundAlt > double.NegativeInfinity)
            {
                float hNow = (float)(body.GetAltitude(pos) - groundAlt);
                Vector3 outward = Vector3.ProjectOnPlane(pos - padCentre, up);
                if (outward.sqrMagnitude < 1f) outward = Vector3.ProjectOnPlane(s.vel, up);
                if (outward.sqrMagnitude < 1e-4f) outward = Vector3.ProjectOnPlane(Random.onUnitSphere, up);
                outward.Normalize();
                float nh = PuffPhysics.Ground(ref s, hNow, up, outward, dt, prm);
                if (nh > hNow) { pos += up * (nh - hNow); p.velocity = s.vel; MarkWallJet(ref p, up, outward); }
            }

            p.velocity = s.vel;
            p.pM = s.M; p.pT = s.T; p.pR = s.radius;
            if (p.steam > 0.01f)
            {
                // Is the water in this parcel condensed (a visible cloud), or still vapour / evaporated again?
                float cond = PuffPhysics.CondensedFraction(amb, s, p.fw, SmokeTuning.PhysHumidity, (float)alt);
                p.pVis = Mathf.Lerp(SmokeTuning.SteamMinVisibility, 1f, Mathf.Clamp01(cond / Mathf.Max(SmokeTuning.SteamCondRef, 1e-4f)));
            }
            return pos;
        }

        public void Tick(float dt)
        {
            if (dt <= 0f) return;
            if (body == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            tickCounter++;
            TickInner(dt);
            PerfTickMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        // debug: what the ground-hugging (wall jet) parcels look like right now, over all groups
        public static string GroundStats()
        {
            int n = 0, nAll = 0, vis = 0; float r = 0f, rMax = 0f, pv = 0f, t = 0f, sp = 0f, mass = 0f;
            for (int g = 0; g < all.Count; g++)
            {
                SmokeVolumeGroup sg = all[g];
                if (sg == null) continue;
                for (int i = 0; i < sg.puffs.Count; i++)
                {
                    Puff p = sg.puffs[i];
                    if (!p.physical) continue;
                    nAll++;
                    if (!p.isGround) continue;
                    n++;
                    float rr = sg.SizeForPuff(p);
                    r += rr; if (rr > rMax) rMax = rr;
                    float v = p.pVisS > 0f ? p.pVisS : p.pVis; pv += v; if (v > 0.15f) vis++;
                    t += p.pT; sp += p.velocity.magnitude; mass += p.pM;
                }
            }
            string str = StreamStats();
            if (n == 0) return str + "wall-jet parcels: none (of " + nAll + " physical)";
            return str + "wall-jet parcels: " + n + " (of " + nAll + " physical), mean drawn radius " + (r / n).ToString("F1") + " m (max " + rMax.ToString("F1") + "), visible (>0.15) " + vis + ", mean visibility " + (pv / n).ToString("F2") + ", mean T " + (t / n).ToString("F0") + " K, mean speed " + (sp / n).ToString("F0") + " m/s, mean mass " + (mass / n).ToString("F0") + " kg";
        }

        public static readonly Dictionary<int, string> DbgGroupInfo = new Dictionary<int, string>();

        private static string StreamStats()
        {
            string grp = "";
            foreach (var kv in DbgGroupInfo) grp += " {" + kv.Value + "}";
            int src = 0, rays = 0, pts = 0; string each = "";
            for (int g = 0; g < all.Count; g++)
            {
                SmokeVolumeGroup sg = all[g];
                if (sg == null) continue;
                for (int i = 0; i < sg.streamSources.Count; i++)
                {
                    StreamSource q = sg.streamSources[i];
                    src++;
                    int rc = 0, pc = 0;
                    if (q.count != null) for (int k = 0; k < q.count.Length; k++) { if (q.count[k] > 1) rc++; pc += q.count[k]; }
                    rays += rc; pts += pc;
                    if (src <= 4) each += " [r0 " + q.r0.ToString("F1") + " v0 " + q.v0.ToString("F0") + " seen " + (Time.time - q.lastSeen).ToString("F1") + "s ago, rays " + rc + "/" + (q.count != null ? q.count.Length : 0) + "]";
                }
            }
            return "ENGINE GROUPS:" + grp + " | EMIT: " + DbgEmit + " | STREAMS: " + src + " sources," + each + " (rays with a path " + rays + ", points " + pts + ") | ";
        }

        // debug: the last parcels turned by the pad surface (position, surface normal, velocity after the turn) and a count
        public const int DbgHitMax = 16;
        public static readonly Vector3[] DbgHitPos = new Vector3[DbgHitMax], DbgHitNormal = new Vector3[DbgHitMax], DbgHitVel = new Vector3[DbgHitMax];
        public static readonly float[] DbgHitTime = new float[DbgHitMax];
        public static int DbgHitIdx, DbgHitCount;
        public static float DbgHitSpeedSum, DbgHitUpSum;
        private bool shockOn; private Vector3 shockC; private float shockR0, shockR1, shockAmp;

        private void TickInner(float dt)
        {

            PublishNoiseOffset();
            physRayBudget = Mathf.RoundToInt(SmokeTuning.PhysCollideBudget);
            shockOn = PadFluid.ShockFront(out shockC, out shockR0, out shockR1, out shockAmp);

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

                // above the atmosphere (or on an airless body) the smoke is gone
                if (!p.isGround && ((body.atmosphere && body.GetAltitude(worldPos) >= body.atmosphereDepth) || (!body.atmosphere && !SmokeTuning.AirlessBodyPlume)))
                {
                    p.markedForRemoval = true; puffs[i] = p; continue;
                }

                if (p.physical)
                {
                    // the ignition wave of the engines (surface mode): one outward kick when the front passes, shielded by the structure
                    if (shockOn)
                    {
                        Vector3 sd = worldPos - shockC;
                        float sr = sd.magnitude;
                        if (sr > 4f && sr > shockR0 - 3f && sr < shockR1 + 3f)
                        {
                            Vector3 sh, snn;
                            if (!PadFluid.SurfaceMarch(shockC, worldPos, out sh, out snn))
                            {
                                Vector3 sdir = (sd / sr + up * 0.25f).normalized;
                                p.velocity += sdir * (shockAmp * 30f / Mathf.Max(sr, 30f)) / (1f + 0.04f * p.pR);
                            }
                        }
                    }
                    // a wall-jet puff lying on the pad: the fluid cloud draws that flow now, so the old ones are dropped
                    if (p.isGround && PadFluid.Active) { p.markedForRemoval = true; puffs[i] = p; continue; }
                    // Old puffs move slowly and are far from the ground: they are stepped every 2nd / 4th tick with the
                    // matching longer dt (about 1100 puffs were being stepped every tick = 5 ms of every 7 ms tick).
                    int stride = p.age > 40f ? 8 : p.age > 10f ? 4 : p.age > 3f ? 2 : 1;
                    if (stride > 1 && ((p.spawnIndex + tickCounter) % stride) != 0)
                    {
                        // between its physics steps a parcel is NOT frozen (it then jumped, at a different tick for each one): it drifts
                        // with the velocity it has, every tick, so the spine moves smoothly
                        p.localPos = WorldToLocal(worldPos + p.velocity * dt);
                        EaseShown(ref p, dt);
                        puffs[i] = p;
                        continue;
                    }
                    Vector3 newPos = StepPhysicalPuff(ref p, worldPos, up, dt * stride);
                    // the step integrated the whole window; the other ticks of it have already moved the parcel, so only this tick's share is applied
                    if (stride > 1) newPos = worldPos + (newPos - worldPos) / stride;
                    EaseShown(ref p, dt);
                    if (!IsFinite(newPos) || !IsFinite(p.velocity) || float.IsNaN(p.pM) || float.IsNaN(p.pR))
                    {
                        p.markedForRemoval = true;
                        puffs[i] = p;
                        continue;
                    }
                    p.localPos = WorldToLocal(newPos);
                    puffs[i] = p;
                    continue;
                }

                // Flattened against local up. windDrift is world-space, and world axes are
                // not horizontal on a globe, so it carries a stray vertical component and
                // the whole trail slowly climbs - 1 m/s of it is 150m over a full life.
                // Every puff converges to the SAME world-space drift, so the displacement
                // is coherent and grows with age: at 1 m/s over a 150s life the oldest
                // puffs sit 150m downwind of the youngest. That is a shear across the whole
                // column, and since the old puffs are also the largest it reads as the
                // trail being blown over sideways rather than as drift.
                //
                // Kept, because real smoke does drift - but as a live speed with the
                // magnitude taken off the direction, so it can be dialled to zero.
                Vector3 windDir = windDrift.sqrMagnitude > 1e-6f ? windDrift.normalized : Vector3.zero;
                Vector3 target = Vector3.ProjectOnPlane(windDir * SmokeTuning.WindSpeed, up);
                // Ground puffs never fully park - a slow outward creep keeps the cloud
                // spreading after the initial throw has bled off.
                if (p.isGround)
                {
                    if (p.groundRayId >= 0)
                    {
                        // Landing splash: creep along the puff's OWN ray, not away from the
                        // launch pad. Measured from padCentre, every puff of a lander sitting
                        // anywhere else crept the same way and the whole cloud sheared into
                        // parallel streaks flying off to one side (reported). Scaled by this
                        // group's size so a micro engine's splash does not drift like a
                        // booster's.
                        Vector3 rayDirFlat = Vector3.ProjectOnPlane(p.rayDir, up);
                        if (rayDirFlat.sqrMagnitude > 1e-4f)
                        {
                            float creepScale = Mathf.Clamp01(maxSize / Mathf.Max(SmokeTuning.MaxPuffSize, 1f));
                            target += rayDirFlat.normalized * GroundCreepSpeed * creepScale;
                        }
                    }
                    else
                    {
                        Vector3 outward = Vector3.ProjectOnPlane(worldPos - padCentre, up);
                        if (outward.sqrMagnitude > 1f) target += outward.normalized * GroundCreepSpeed;
                    }
                }
                float convergeRate = p.isGround ? GroundVelocityConvergeRate : VelocityConvergeRate;
                p.velocity = Vector3.Lerp(p.velocity, target, dt * convergeRate);
                Vector3 newWorldPos = worldPos + p.velocity * dt;

                // Splash puffs hug the ground. The bounce below only ever pushes a puff UP
                // out of the terrain, so over falling ground a horizontally-thrown puff kept
                // its height and hung in the air ("some of them fly"). Pull it back down to
                // the surface first; the bounce still lifts it over buildings afterwards.
                // Sea level is the floor over water, not the seabed.
                if (p.isGround && p.groundRayId >= 0 && body.pqsController != null)
                {
                    Vector3d radialDir = ((Vector3d)newWorldPos - body.position).normalized;
                    double groundAlt = body.pqsController.GetSurfaceHeight(radialDir) - body.Radius;
                    if (body.ocean && groundAlt < 0.0) groundAlt = 0.0;
                    double altNow = body.GetAltitude(newWorldPos);
                    // Loft, as in KerbalFX: the puff floats up as it billows, roughly half its own
                    // radius above the surface, instead of being pinned flat to the ground.
                    double hugHeight = 1.0 + 0.5 * SizeForPuff(p);
                    if (altNow > groundAlt + hugHeight)
                    {
                        newWorldPos -= up * (float)(altNow - groundAlt - hugHeight);
                    }
                }

                TryBounceOffGround(ref newWorldPos, ref p.velocity, up, ref p.bounced, p.age, dt);

                if (!IsFinite(newWorldPos) || !IsFinite(p.velocity))
                {
                    Debug.LogWarning(string.Format(
                        "[PlumeTrails] dropping puff with invalid position/velocity (NaN or Inf). pos={0} vel={1} age={2:F1}",
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
            hiddenByToggle = false;
            lastTickTime = Time.time;
            ComputeSpineBounds();
            if (usePolylineActiveTrail)
            {
                // The volume is uploaded by RenderAll, once per frame after EVERY group has
                // ticked, because one volume may now carry several groups' trails and it
                // cannot be built until all of their lists are fresh.
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
                            "[PlumeTrails] PAD SURVEY: pad local size {0} -> trench along {1}",
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
                            "[PlumeTrails] PAD SURVEY: using pad transform '{0}' -> axis {1}  "
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
                        "[PlumeTrails] PAD SURVEY: coherence={0:F2} (need {1:F2}), samples={2} (need {3})",
                        coherence, MinAxisCoherence, slantedCount, MinSlantedSamples));
                }
            }

            if (debugBounceLogging)
            {
                Debug.Log("[PlumeTrails] PAD SURVEY: " + seen.Count + " distinct colliders under/around the pad");
                foreach (var kv in seen)
                {
                    Debug.Log("[PlumeTrails]   '" + kv.Key + "' " + kv.Value);
                }
                Debug.Log(string.Format("[PlumeTrails] PAD SURVEY: {0} slanted samples (>{1:F0}deg), outflow axis {2}",
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
                            "[PlumeTrails] bounce: alt={0:F1} age={1:F2} vSpeed={2:F1} penetration={3:F2} hit={4} {5}",
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

        // Density multiplier for a single puff, from the same eased expansion SizeForPuff
        // uses - the two have to move together or a puff would be wide before it was thin.
        //
        // The exponent is physical rather than dialled in: the exhaust mass flow is fixed,
        // so a plume N times wider has N^2 the cross-section and 1/N^2 the density, while
        // a ray crossing it travels N times further. Optical depth therefore falls as 1/N,
        // which is how a real high-altitude plume manages to be enormous and see-through
        // at once. See JellyfishThinningPower.
        private float DensityForPuff(Puff p)
        {
            if (p.physical)
                return p.emitDensity * Mathf.Pow(Mathf.Clamp(p.pM0 / Mathf.Max(DilutedMass(p), 1e-3f), 1e-4f, 1f), SmokeTuning.PhysDensityExp)
                    * (p.steam > 0.01f ? Mathf.Lerp(1f, (p.pRs > 0f ? p.pVisS : p.pVis), p.steam) : 1f);
            if (SmokeTuning.SrbOnly && !p.isGround)
            {
                // Mass conservation along a line. The smoke carried per metre of trail is
                // fixed at emission, so once the column grows past its normal width its
                // density falls as 1/r^2 - and the optical depth through it only as 1/r,
                // since the path through it lengthens. Old, spread-out smoke therefore
                // fades gradually instead of hanging as the same solid rope for minutes.
                //
                // One rule for both kinds of widening: the altitude bloom makes r larger
                // too, so it is thinned by exactly the same law with no separate term.
                float r = SizeForPuff(p);
                float rNominal = maxSize * p.sizeMultiplier;
                float spread = rNominal > 0.01f ? r / rNominal : 1f;
                float thin = spread > 1f ? Mathf.Pow(spread, -SmokeTuning.SrbThinningPower) : 1f;
                return p.emitDensity * thin;
            }

            float emitted = p.emitDensity;
            float expansion = Mathf.Max(p.expansion, 1f);
            if (expansion <= 1.0001f) return emitted;
            float te = Mathf.Clamp01(p.age / Mathf.Max(SmokeTuning.JellyfishExpandTime, 0.05f));
            float easedExpansion = 1f - (1f - te) * (1f - te);
            float current = Mathf.Lerp(1f, expansion, easedExpansion);
            return emitted / Mathf.Pow(current, SmokeTuning.JellyfishThinningPower);
        }

        // VISIBLE part of a physical parcel. A parcel keeps entraining air, but only the core where the exhaust is still
        // concentrated reads as smoke: it holds about (visible dilution x exhaust mass), so past that dilution the visible
        // radius grows no more (it would hold constant mass) and the density stays put, instead of the whole parcel
        // ballooning into a faint fat cloud.
        private static float DilutionLimit(Puff p) { return p.pVisDil * SmokeTuning.PhysVisibleDilution; }

        private static float DilutedMass(Puff p) { return p.pM; }   // density keeps thinning with the full dilution

        private static float VisibleRadiusFactor(Puff p)
        {
            float lim = DilutionLimit(p);
            if (lim <= 1f || p.pMe <= 0f) return 1f;
            float d = p.pM / p.pMe;
            // Past the limit the visible radius almost stops growing (a constant-mass core would stop outright; real
            // plumes keep widening, only slowly) while the density keeps falling with the dilution.
            return d <= lim ? 1f : Mathf.Pow(lim / d, 0.33f);   // ~ entrainment: the radius keeps growing like a cone
        }

        // GROWTH CAP. A parcel's radius follows its entrained mass, so from the nozzle (a few metres) to old smoke (tens of
        // metres) it grows ten-fold, and the whole trail reads as a long needle that is thin at the engine and fat far away -
        // "stretched". A visible plume widens by a few times, not ten. The ratio radius / release radius is limited softly
        // (asymptote = PhysMaxGrowth, the first part of the growth untouched), so the column keeps a similar width along its
        // length. 0 = off.
        // FAR TAPER. With the growth capped, a tall column keeps one width from the engine to the sky - a tube. Smoke
        // that is old (far behind the rocket) is drawn narrower, so the column is thickest by the engine and slims
        // out along its length, as the user asked ("thick at the engine, thin further"). 0 = off.
        private static float FarTaper(Puff p)
        {
            float k = SmokeTuning.PhysFarTaper;
            if (k <= 0.001f) return 1f;
            float x = Mathf.Clamp01(p.age / 45f);
            return 1f - k * x * x * (3f - 2f * x);
        }

        private static float GrowthCapFactor(Puff p)
        {
            float g = SmokeTuning.PhysMaxGrowth;
            if (g <= 1.01f || p.pR0 <= 0f) return 1f;
            float x = (p.pRs > 0f ? p.pRs : p.pR) / p.pR0;
            if (x <= 1f) return 1f;
            float capped = 1f + (g - 1f) * (1f - Mathf.Exp(-(x - 1f) / (g - 1f)));
            return capped / x;
        }

        private float SizeForPuff(Puff p)
        {
            if (p.physical)
            {
                float rr = (p.pRs > 0f ? p.pRs : p.pR) * SmokeTuning.PhysRadiusScale * p.pScale * VisibleRadiusFactor(p) * (p.isGround ? 1f : GrowthCapFactor(p) * FarTaper(p));   // the flow along the pad is NOT capped or tapered like a rising column: it has to grow to the size of a cloud
                // WALL JET (Glauert 1956, radial jet along a wall): the layer thickens linearly with the distance from the
                // impact, half-thickness ~ 0.1 x r. The parcel's own entrainment alone gives a tube of constant width.
                if (p.isGround)
                {
                    float dHit = Vector3.Distance(p.localPos, p.hitLocal);
                    float floorR = SmokeTuning.PhysWallSpread > 0.001f ? 1.3f * SmokeTuning.PhysWallSpread * dHit : 0f;   // steam-like: a little more than the textbook wall jet
                    // The entrained mass of a parcel keeps growing and with it the radius (mean 30 m, max 61 m in the log: spheres wider than the
                    // trench, piling up into clouds over the walls). A layer along the surface is thin: its half-thickness is a few metres plus
                    // a fraction of the distance travelled, and it never exceeds 25 m.
                    float capR = Mathf.Min(25f, 5f + 0.22f * dHit);
                    rr = Mathf.Min(rr, Mathf.Max(capR, floorR));
                    rr = Mathf.Max(rr, Mathf.Min(floorR, 25f));
                }
                return Mathf.Max(rr, 0.05f);
            }
            // Growth is timed independently of lifeTime. Tying the two together means a
            // puff only reaches full size at the end of its life, by which point the
            // rocket is kilometres away and the visible trail is all young thin puffs.
            float grown;
            if (SmokeTuning.SrbOnly && !p.isGround)
            {
                // Turbulent diffusion: the puff's variance grows linearly with time, so
                // r^2 = r0^2 + K*t. Fast at first, slowing, but never stopping - which is
                // how a real cloud of smoke actually spreads, and one curve instead of the
                // eased ramp plus the separate linear tail bolted on after it.
                //
                // K is chosen so the puff still reaches maxSize at growthTime, so the
                // existing size knobs keep meaning what they did.
                float r0 = startSize;
                float T = Mathf.Max(growthTime, 0.1f);
                float t0 = Mathf.Max(SmokeTuning.SrbJetPhaseTime, 0.05f);
                // t^2 / (t + t0) rather than plain t. It behaves as t^2/t0 while the smoke
                // is still inside the jet (a slow, nearly straight start that hides the
                // join with the nozzle) and as t once turbulence has taken over, so the
                // plume neither bells open at the engine nor stays a needle. K still lands
                // the puff on maxSize at growthTime, so the size knobs keep their meaning;
                // SrbSpreadRate scales how fast it gets there.
                float k = Mathf.Max((maxSize * maxSize - r0 * r0) * (T + t0) / (T * T), 0f)
                        * SmokeTuning.SrbSpreadRate;
                if (p.age <= T)
                {
                    grown = Mathf.Sqrt(r0 * r0 + k * p.age * p.age / (p.age + t0));
                }
                else
                {
                    // Past the size limit the puff should ease off and creep, not keep
                    // spreading at the rate that got it there: the unbounded sqrt(t) tail
                    // took a ~34m puff to >100m over its life, and a puff that big lost all
                    // its structure and read as a blob (28 IX report). Same curve up to T,
                    // then only a fraction of the diffusion rate.
                    float rT2 = r0 * r0 + k * T * T / (T + t0);
                    grown = Mathf.Sqrt(rT2 + k * SmokeTuning.SrbPostLimitGrowth * (p.age - T));
                }
                return grown * p.sizeMultiplier * EasedExpansion(p);
            }

            float t = Mathf.Clamp01(p.age / growthTime);
            float eased = 1f - Mathf.Pow(1f - t, growthSharpness);
            grown = Mathf.Lerp(startSize, maxSize, eased);

            // Growth does not stop at growthTime - real smoke keeps expanding as it
            // entrains air, and a fixed size afterwards is what made the trail read as a
            // constant-width tube. Slow and linear, so young smoke still billows fastest.
            if (p.age > growthTime)
            {
                // Scaled by this group's size vs the reference: the rate is in metres per
                // second, so left absolute a tiny lander's splash still ballooned to
                // hundreds of metres (reported: a huge dome from a micro engine).
                float sizeScale = maxSize / Mathf.Max(SmokeTuning.MaxPuffSize, 1f);
                grown += (p.age - growthTime) * ContinuedGrowthRate * (p.isGround ? GroundGrowthBoost : 1f) * sizeScale;
            }
            // Expansion gets its OWN, much shorter time constant than growth. Riding the
            // growth easing put full width tens of kilometres downrange, because at orbital
            // speed growthTime is a long stretch of trail - but an underexpanded nozzle
            // opens almost immediately behind itself. Growth is entrainment over minutes;
            // this is pressure release over a second or two, and they are not the same
            // process.
            return grown * p.sizeMultiplier * EasedExpansion(p);
        }

        private static float EasedExpansion(Puff p)
        {
            float te = Mathf.Clamp01(p.age / Mathf.Max(SmokeTuning.JellyfishExpandTime, 0.05f));
            float easedExpansion = 1f - (1f - te) * (1f - te);
            return Mathf.Lerp(1f, Mathf.Max(p.expansion, 1f), easedExpansion);
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

        // Fades toward a FLOOR, not to zero. Thin air gives a thin plume, but the high
        // altitude bloom is the most striking thing the plume does - fading it out
        // entirely is what used to make the smoke simply stop existing at 15km.
        private float AlphaForAltitude(Vector3 worldPos)
        {
            float floor = SmokeTuning.JellyfishEnabled ? SmokeTuning.JellyfishAlphaFloor : 0f;
            double altitude = body.GetAltitude(worldPos);
            // NO AIR, NO SMOKE: above the atmosphere of the body (or anywhere on a body that has none) there is nothing to hold it. The "thin-air alpha"
            // floor of the jellyfish applied there too, and the trail stayed in space for ever (reported by other players).
            if (!body.atmosphere) return SmokeTuning.AirlessBodyPlume ? floor : 0f;
            double depth = body.atmosphereDepth;
            if (altitude >= depth) return 0f;
            // the fade follows the atmosphere of THIS body: it starts at 0.45 of its depth and ends at its top (the fixed 45 / 90 km were made for one planet)
            float fEnd = (float)System.Math.Min(fadeEndAltitude, depth), fStart = Mathf.Min(fadeStartAltitude, 0.45f * fEnd);
            if (altitude <= fStart) return 1f;
            if (altitude >= fEnd) return floor * Mathf.Clamp01((float)((depth - altitude) / System.Math.Max(depth - fEnd, 1.0)));
            float t = (float)((altitude - fStart) / System.Math.Max(fEnd - fStart, 1.0));
            return Mathf.Lerp(1f, floor, t);
        }

        // ---- active tail (tiled) ----

        private int lastEmittedBurnId;
        private int nextSpawnIndex;
        private bool needTrailSeparator;

        private int groundSeqCounter;
        private int tickCounter;
        private readonly List<GroundSortEntry> wallScratch = new List<GroundSortEntry>();
        private readonly Dictionary<int, Vector3> wallLast = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, float> wallLastR = new Dictionary<int, float>();
        private int committedGroundStride = 1;
        private int committedWallStride = 1;
        private readonly List<GroundSortEntry> wallKept = new List<GroundSortEntry>();
        // static comparers: a lambda passed to Sort is re-created on every call that captures nothing only in newer compilers; this is explicit
        private static readonly System.Comparison<GroundSortEntry> WallOrder = (x, y) => x.rayId != y.rayId ? x.rayId.CompareTo(y.rayId) : x.age.CompareTo(y.age);
        private static readonly System.Comparison<GroundSortEntry> GroundOrder = (x, y) => x.key.CompareTo(y.key);
        private float wallPrevR, wallPrevSpeed, wallPrevAge; private int wallPrevRay;

        private struct GroundSortEntry
        {
            public Vector3 pos;
            public float radius;
            public float density;
            public float key;
            public int rayId; public float age; public float speed;
        }
        private readonly List<GroundSortEntry> groundScratch = new List<GroundSortEntry>();

        private static int AgeBand(float age, float bandT)
        {
            // floor(log2(1 + age / bandT)) without a Log call (this runs per puff, several times per frame)
            float r = 1f + Mathf.Max(age, 0f) / bandT;
            int b = 1;
            while (r >= 2f && b < 4096) { r *= 0.5f; b <<= 1; }
            return b;
        }

        private int BandedCount(int baseStride, float bandT)
        {
            int c = 0;
            for (int i = 0; i < puffs.Count; i++)
            {
                Puff q = puffs[i];
                if (q.isGround || q.markedForRemoval) continue;
                int st = baseStride * AgeBand(q.age, bandT);
                if (st <= 1 || (q.spawnIndex % st) == 0) c++;
            }
            return c;
        }

        private void BuildActiveOrderedList()
        {
            activeOrderedPos.Clear();
            activeOrderedRadius.Clear();
            activeOrderedDensity.Clear();
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
            wallLast.Clear(); wallLastR.Clear();

            // Stable thinning for ray puffs. The even stride used for the legacy ground cloud
            // re-picks EVERY point whenever the stride changes, and with a splash adding
            // dozens of puffs a second it changes constantly - half the chain swapped per
            // frame, which is the flicker and the holes. Whole emission ticks are kept by
            // groundTick % stride == 0 with a power-of-two stride, so the kept set only ever
            // shrinks (a strict subset), and the stride ratchets up, never down.
            int rayPuffCount = 0;
            for (int i = 0; i < puffs.Count; i++)
            {
                if (puffs[i].isGround && puffs[i].groundRayId >= 0) rayPuffCount++;
            }
            int wantedGroundStride = 1;
            while (rayPuffCount / wantedGroundStride > MaxGroundSpinePoints) wantedGroundStride *= 2;
            if (rayPuffCount == 0) committedGroundStride = 1;
            else if (wantedGroundStride > committedGroundStride) committedGroundStride = wantedGroundStride;

            for (int i = 0; i < puffs.Count; i++)
            {
                if (!puffs[i].isGround) continue;
                if (puffs[i].groundRayId >= 0 && committedGroundStride > 1
                    && (puffs[i].groundTick % committedGroundStride) != 0) continue;
                Vector3 wp = LocalToWorld(puffs[i].localPos);
                if (AlphaForAge(puffs[i].age) * AlphaForAltitude(wp) <= 0.01f) continue;
                // side clouds: keep a puff only if it is clear of the last kept one in its branch
                if (puffs[i].physical && puffs[i].groundRayId >= 0 && SmokeTuning.WallSpacing > 0.01f && puffs[i].velocity.sqrMagnitude < 144f)
                {
                    float rr = SizeForPuff(puffs[i]);
                    Vector3 lastPos; float lastR;
                    if (wallLast.TryGetValue(puffs[i].groundRayId, out lastPos) && wallLastR.TryGetValue(puffs[i].groundRayId, out lastR)
                        && Vector3.Distance(wp, lastPos) < SmokeTuning.WallSpacing * (rr + lastR)) continue;
                    wallLast[puffs[i].groundRayId] = wp; wallLastR[puffs[i].groundRayId] = rr;
                }
                // Ray puffs sort by (ray, age) so each ray's own puffs land consecutively
                // and in travel order, oldest (furthest out) last within the group - see
                // AddGroundPuff. Non-ray puffs (rayId -1, the old pad-deflector cloud) keep
                // the original spatial sort, unaffected.
                float key = puffs[i].groundRayId >= 0
                    ? puffs[i].groundRayId * 1000000f + puffs[i].age
                    : (padAxisValid ? Vector3.Dot(wp - padCentre, padOutflowAxis) : wp.x);
                if (puffs[i].physical && puffs[i].groundRayId >= 0)
                {
                    // A ground-hugging flow is a SHEET of billows, not a line: chaining its puffs in time order traced
                    // each parcel's own flight path and drew it as a looping tube (spaghetti). Each is its own blob.
                    wallScratch.Add(new GroundSortEntry {
                        pos = wp, radius = SizeForPuff(puffs[i]), density = EncodeDensity(DensityForPuff(puffs[i]), puffs[i].steam, WallHeat(puffs[i])),
                        key = puffs[i].spawnIndex, rayId = puffs[i].groundRayId, age = puffs[i].age, speed = puffs[i].velocity.magnitude });
                    continue;
                }
                groundScratch.Add(new GroundSortEntry {
                    pos = wp, radius = SizeForPuff(puffs[i]), density = DensityForPuff(puffs[i]),
                    key = key, rayId = puffs[i].groundRayId });
            }

            if (streamSources.Count > 0 && SmokeTuning.PadStreams && !PadFluid.Drawing) EmitStreams(shareBudgetPts > 0 ? shareBudgetPts : MaxSpinePoints);

            // isolated blobs: three spine points each (zero-radius separator, then two coincident points of the blob's
            // radius - a segment with a zero-radius end is skipped by the shader, so nothing connects to anything)
            if (wallScratch.Count > 0)
            {
                int cap = Mathf.Max(8, MaxGroundSpinePoints / 3);
                // The stride only goes UP while the count stays over the cap, and comes down only when the count has fallen well under it (it was
                // recomputed every frame, and a count that hovered at the cap toggled it 1 <-> 2: half of the blobs appeared and vanished)
                int wantedWall = 1;
                while (wallScratch.Count / wantedWall > cap && wantedWall < 64) wantedWall *= 2;
                if (wantedWall > committedWallStride) committedWallStride = wantedWall;
                else if (committedWallStride > 1 && wallScratch.Count / (committedWallStride / 2) <= cap * 0.7f) committedWallStride /= 2;
                int blobStride = committedWallStride;
                // Blobs that lie NEAR each other are chained (nearest neighbour first), so a flow reads as one continuous body of smoke, not as
                // separate balls; where the next blob is farther than the two radii allow, the chain is BROKEN (zero-radius separator), so
                // nothing is ever joined across an empty gap (that was the spaghetti).
                wallKept.Clear();
                for (int b = 0; b < wallScratch.Count; b++)
                    if (!(blobStride > 1 && ((long)wallScratch[b].key % blobStride) != 0)) wallKept.Add(wallScratch[b]);
                int nk = wallKept.Count;
                if (nk > 0)
                {
                    // each ray is a TRAIL: its parcels in order of age, the youngest (at the engine / the impact) first, so the chain runs
                    // from the root outwards like the main trail does
                    wallKept.Sort(WallOrder);
                    int cur = 0;
                    bool first = true;
                    Vector3 lastPos = Vector3.zero;
                    for (int done = 0; done < nk; done++)
                    {
                        GroundSortEntry e = wallKept[cur];
                        float tiny = Mathf.Max(0.03f, e.radius * 0.02f);
                        // link only if the previous blob is within reach: 1.3x the sum of the radii (the capsule between them is then thinner than the blobs)
                        float gap = first ? 1e9f : Vector3.Distance(e.pos, lastPos);
                        // the same ray, next in age: a coherent flow moved the parcel about speed x (age difference) between the two, so a gap of
                        // that size is NOT a break (young fast parcels are far apart yet one stream); different rays link only when really close
                        bool sameRay = !first && e.rayId == wallPrevRay;
                        float reach = sameRay ? 1.6f * Mathf.Max(e.speed, wallPrevSpeed) * Mathf.Abs(e.age - wallPrevAge) + 2.2f * (e.radius + wallPrevR)
                                              : 1.3f * (e.radius + wallPrevR);
                        bool linked = !first && gap < reach;
                        if (!linked) { activeOrderedPos.Add(e.pos); activeOrderedRadius.Add(0f); activeOrderedDensity.Add(1f); }
                        activeOrderedPos.Add(e.pos); activeOrderedRadius.Add(e.radius); activeOrderedDensity.Add(e.density);
                        activeOrderedPos.Add(e.pos + Vector3.up * tiny); activeOrderedRadius.Add(e.radius); activeOrderedDensity.Add(e.density);
                        lastPos = e.pos; wallPrevR = e.radius; wallPrevRay = e.rayId; wallPrevSpeed = e.speed; wallPrevAge = e.age; first = false;
                        if (done + 1 >= nk) break;
                        cur = done + 1;
                    }
                    activeOrderedPos.Add(lastPos); activeOrderedRadius.Add(0f); activeOrderedDensity.Add(1f);
                }
                else { activeOrderedPos.Add(wallScratch[wallScratch.Count - 1].pos); activeOrderedRadius.Add(0f); activeOrderedDensity.Add(1f); }
                needTrailSeparator = true;
                wallScratch.Clear();
            }

            if (groundScratch.Count > 0)
            {
                groundScratch.Sort(GroundOrder);

                // Hard share of the spine. Both clouds use the same 200-point array, so an
                // unbounded ground cloud starves the trail - its stride jumps and it comes
                // out sparse enough to read as a wisp hanging off the ground cloud. The
                // points are already sorted along the outflow axis, so an even stride
                // samples the spread rather than clipping one end.
                int groundStride = Mathf.Max(1,
                    Mathf.CeilToInt(groundScratch.Count / (float)MaxGroundSpinePoints));
                int lastRayId = int.MinValue;
                bool haveLastRay = false;
                for (int i = 0; i < groundScratch.Count; i += groundStride)
                {
                    GroundSortEntry e = groundScratch[i];
                    // Break between rays, same two-zero-point technique the main trail
                    // uses between bursts (see the burnId check further down): a single
                    // ray reads as a "jet stream" only if it never joins the NEXT ray's
                    // puffs with a capsule ramping across open ground between them - one
                    // shared spine, several disconnected streaks radiating out of it.
                    // A ground-hugging flow is drawn as a string of lumps, not one smooth tube: within a branch, puffs
                    // that are further apart than WallLumpGap x the sum of their radii are not joined.
                    bool lumpBreak = false;
                    if (haveLastRay && e.rayId >= 0 && e.rayId == lastRayId && SmokeTuning.WallLumpGap > 0.01f
                        && activeOrderedPos.Count > 0)
                    {
                        float pr = activeOrderedRadius[activeOrderedRadius.Count - 1];
                        lumpBreak = pr > 0f && Vector3.Distance(e.pos, activeOrderedPos[activeOrderedPos.Count - 1])
                            > SmokeTuning.WallLumpGap * (pr + e.radius);
                    }
                    if (haveLastRay && (lumpBreak || (e.rayId != lastRayId && (e.rayId >= 0 || lastRayId >= 0)))
                        && activeOrderedPos.Count > 0)
                    {
                        Vector3 prevPos = activeOrderedPos[activeOrderedPos.Count - 1];
                        float prevRadius = activeOrderedRadius[activeOrderedRadius.Count - 1];
                        float gap = Vector3.Distance(e.pos, prevPos);
                        if (prevRadius > 0f && gap > 0.01f)
                        {
                            Vector3 dir = (e.pos - prevPos) / Mathf.Max(gap, 0.0001f);
                            activeOrderedPos.Add(prevPos + dir * (prevRadius * 0.5f));
                            activeOrderedRadius.Add(0f);
                            activeOrderedDensity.Add(1f);
                            activeOrderedPos.Add(e.pos - dir * (e.radius * 0.5f));
                            activeOrderedRadius.Add(0f);
                            activeOrderedDensity.Add(1f);
                        }
                    }
                    activeOrderedPos.Add(e.pos);
                    activeOrderedRadius.Add(e.radius);
                    activeOrderedDensity.Add(e.density);
                    lastRayId = e.rayId;
                    haveLastRay = true;
                }
                // Closing zero-radius point, with a MATCHING one at the first trail puff
                // below. One alone is not enough: the capsule from it to the trail still
                // spans the gap with a radius ramping 0 -> r, drawing a thin cone from the
                // pad up the column. Two give a zero-to-zero capsule, which is invisible.
                activeOrderedPos.Add(groundScratch[groundScratch.Count - 1].pos);
                activeOrderedRadius.Add(0f);
                activeOrderedDensity.Add(1f);
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
            // Share of the shared spine buffer this chain gets when clusters are merged. Thinning to
            // it HERE, by stable spawnIndex, means the merge stage never has to decimate by list
            // position - that one reselects different points whenever the oldest puff expires and
            // can drop the newest points (tip + root), which twitched the plume next to the nozzle.
            int shareBudget = shareBudgetPts > 0 ? shareBudgetPts : MaxSpinePoints;
            int trailBudget = Mathf.Max(shareBudget - activeOrderedPos.Count - 4, 16);
            int trailStride = 1;
            // AGE-BANDED thinning (replaces one global stride): a puff of age t keeps only every
            // (base * 2^floor(log2(1 + t / bandT)))-th spawn index, so detail is densest at the young end,
            // where the radius changes fast, and sparser further back. A puff changes band at its own age,
            // so points leave the spine ONE AT A TIME. The band width bandT is itself CONTINUOUS: under budget
            // pressure (a second chain starts, e.g. boosters lighting) it is narrowed smoothly, which moves
            // every band boundary a little, instead of a stride doubling that dropped half the points at once.
            // bandT = 0 restores the old uniform stride.
            float bandT = SmokeTuning.SpineBandSeconds;
            bool banded = bandT > 0.05f;
            if (banded)
            {
                float hi = bandT;
                float minBand = Mathf.Min(0.15f, bandT);
                int fullCount = BandedCount(1, hi);
                lastFullDemand = fullCount + activeOrderedPos.Count + 4;
                float target;
                if (fullCount <= trailBudget) target = hi;
                else if (BandedCount(1, minBand) > trailBudget)
                {
                    target = minBand;
                    while (BandedCount(trailStride, minBand) > trailBudget && trailStride < 1024) trailStride *= 2;
                }
                else
                {
                    float lo = minBand, up = hi;
                    for (int it = 0; it < 9; it++)
                    {
                        float mid = 0.5f * (lo + up);
                        if (BandedCount(1, mid) <= trailBudget) lo = mid; else up = mid;
                    }
                    target = lo;
                }
                // narrowing is immediate (the budget must hold), widening back is slow
                bandEff = bandEff <= 0f ? target : Mathf.Min(target, bandEff + hi * Mathf.Min(Time.deltaTime, 0.1f) * 0.5f);
                bandT = bandEff;
            }
            else
            {
                lastFullDemand = liveTrail + activeOrderedPos.Count + 4;
                while (liveTrail / trailStride > trailBudget) trailStride *= 2;
            }
            // RATCHET. Recomputed from scratch the stride can go back DOWN, and near the
            // threshold it oscillates 2<->4 from frame to frame as puffs come and go. Each
            // flip swaps out half the spine points and the whole density field rebuilds -
            // the trail twitches while the smoke stands still, worst near the pad where the
            // column is densest and therefore sits on the threshold.
            //
            // Only ever rising costs a little detail late in a flight and buys a spine that
            // is stable by construction.
            if (liveTrail == 0) committedTrailStride = 1;
            else if (trailStride > committedTrailStride)
            {
                Debug.Log(string.Format("[PlumeTrails] instance {0}: trail stride {1} -> {2} (live puffs={3}, budget={4}, chains={5})",
                    instanceId, committedTrailStride, trailStride, liveTrail, trailBudget, clusterChains));
                committedTrailStride = trailStride;
            }
            trailStride = committedTrailStride;

            for (int i = 0; i < puffs.Count; i++)
            {
                Puff p = puffs[i];
                if (p.isGround) continue;   // see Puff.isGround
                int puffStride = banded ? trailStride * AgeBand(p.age, bandT) : trailStride;
                if (puffStride > 1 && (p.spawnIndex % puffStride) != 0) continue;

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
                        activeOrderedDensity.Add(1f);
                        activeOrderedPos.Add(worldPos - dir * (radius * 0.5f));
                        activeOrderedRadius.Add(0f);
                        activeOrderedDensity.Add(1f);
                    }
                }

                if (needTrailSeparator)
                {
                    needTrailSeparator = false;
                    activeOrderedPos.Add(worldPos);
                    activeOrderedRadius.Add(0f);
                    activeOrderedDensity.Add(1f);
                }

                lastEmittedBurnId = p.burnId;
                activeOrderedPos.Add(worldPos);
                activeOrderedRadius.Add(radius);
                activeOrderedDensity.Add(EncodeDensity(DensityForPuff(p), p.steam));
            }

            if (hasLiveTip)
            {
                activeOrderedPos.Add(liveTipPos);
                activeOrderedRadius.Add(liveTipRadius);
                activeOrderedDensity.Add(EncodeDensity(liveRootDensity, steamFrac));
                for (int i = 0; i < liveRootCount; i++)
                {
                    activeOrderedPos.Add(liveRootPos[i]);
                    activeOrderedRadius.Add(liveRootRadius[i]);
                    activeOrderedDensity.Add(EncodeDensity(liveRootDens[i], steamFrac));
                }
                EmitNozzleThreads(shareBudgetPts > 0 ? shareBudgetPts : MaxSpinePoints);
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
                    "[PlumeTrails] active trail: alt={0:F0} totalPoints={1} tilesUsed={2}/{3}",
                    tipAltitude, totalPoints, usedTiles, activeTiles.Count));
            }
        }

        // ---- active tail (procedural spine, SMOKE_VOLUME_POLYLINE) ----

        // No bake pass and no chunking - one draw call, density computed analytically as
        // a capsule chain. Cannot have tile seams, since there is only one tile.
        // repeated 1-2-1 passes into smoothedSpine, leaving the source list untouched.
        // Endpoints are pinned so the trail still starts exactly at the engine and ends
        // at the live tip.
        // A point of radius 0 is a BREAK of the chain (the separator between two bursts, and the two zero points that isolate every wall-jet
        // blob): it is neither moved nor taken into the average of its neighbours. Averaged, it got a radius from them, the capsules to it
        // became real, and blobs that were meant to stand alone were joined in the order they were born - for slow parcels that sat in a
        // pile, for fast ones that flew apart it drew loops hundreds of metres long (the "spaghetti" over the pad).
        private const float BreakRadius = 1e-4f;
        private void SmoothSpine(List<Vector3> source, List<float> radii, int count, int passes)
        {
            for (int i = 0; i < count; i++) smoothedSpine[i] = source[i];
            if (count < 3) return;

            for (int pass = 0; pass < passes; pass++)
            {
                smoothScratch[0] = smoothedSpine[0];
                smoothScratch[count - 1] = smoothedSpine[count - 1];
                for (int i = 1; i < count - 1; i++)
                {
                    if (radii[i] <= BreakRadius) { smoothScratch[i] = smoothedSpine[i]; continue; }
                    Vector3 l = radii[i - 1] <= BreakRadius ? smoothedSpine[i] : smoothedSpine[i - 1];
                    Vector3 r = radii[i + 1] <= BreakRadius ? smoothedSpine[i] : smoothedSpine[i + 1];
                    smoothScratch[i] = l * 0.25f + smoothedSpine[i] * 0.5f + r * 0.25f;
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
                    if (source[i] <= BreakRadius) { radiiScratch[i] = 0f; continue; }          // a break stays a break (see SmoothSpine)
                    float l = source[i - 1] <= BreakRadius ? smoothedRadii[i] : smoothedRadii[i - 1];
                    float r = source[i + 1] <= BreakRadius ? smoothedRadii[i] : smoothedRadii[i + 1];
                    radiiScratch[i] = l * 0.25f + smoothedRadii[i] * 0.5f + r * 0.25f;
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
                    float warpMargin = (SmokeTuning.SilhouetteWarpStrength + SmokeTuning.VortexStrength) * 1.733f + VortexWarpMargin + RadiusWarpMargin(smoothedRadii[i]);
                    float reach = Vector3.Distance(centre, smoothedSpine[i])
                        + smoothedRadii[i] * 1.4f * (1f + Mathf.Clamp01(SmokeTuning.EdgeProtrusion)) + warpMargin;
                    if (reach > radius) radius = reach;
                }

                spineGroupBounds[g] = new Vector4(centre.x, centre.y, centre.z, radius);
            }

            return groupCount;
        }

        // ------------------------------------------------------------------------------
        // Merging trails that grow into each other.
        //
        // Each engine group simulates its own puffs, but they no longer each get a volume.
        // Groups whose trails have grown until they touch are drawn as ONE chain of
        // capsules in ONE volume, and the shader's smooth union does the rest: where the
        // trails overlap they fuse into a single body, and where they do not they are just
        // two shapes, exactly as before. That is why the merge can happen the moment the
        // bounds touch with no visible change - a union of shapes that do not overlap IS
        // those shapes - and why each trail keeps its own outline until they meet.
        //
        // Separate volumes were composited over one another instead, so two neighbouring
        // plumes showed a seam where they crossed and a hard edge on whichever was behind.
        // ------------------------------------------------------------------------------
        private const int MaxMergedGroups = 6;
        // Once merged, stay merged until the bounds separate by this much more, so a pair
        // hovering at the touching distance does not flip between the two modes every frame.
        private const float MergeHysteresis = 1.3f;
        private const float MergeHoldSeconds = 4f;
        private static float lastMergeLog;
        private float mergeHoldUntil;

        private Vector3 spineMin, spineMax;
        private bool hasSpineBounds;

        private void ComputeSpineBounds()
        {
            hasSpineBounds = false;
            int n = activeOrderedPos.Count;
            if (n < 2) return;

            Vector3 mn = Vector3.positiveInfinity;
            Vector3 mx = Vector3.negativeInfinity;
            for (int i = 0; i < n; i++)
            {
                float r = activeOrderedRadius[i];
                float margin = r * BoxRadiusMarginMultiplier
                             + BoxWarpMargin * Mathf.Clamp(r / ShaderReferenceRadius, 0.35f, 1f)
                             + RadiusWarpMargin(r);
                mn = Vector3.Min(mn, activeOrderedPos[i] - Vector3.one * margin);
                mx = Vector3.Max(mx, activeOrderedPos[i] + Vector3.one * margin);
            }
            spineMin = mn;
            spineMax = mx;
            hasSpineBounds = true;
        }

        private static bool BoundsTouch(SmokeVolumeGroup a, SmokeVolumeGroup b, float grow)
        {
            Vector3 ca = (a.spineMin + a.spineMax) * 0.5f, cb = (b.spineMin + b.spineMax) * 0.5f;
            Vector3 ea = (a.spineMax - a.spineMin) * 0.5f * grow, eb = (b.spineMax - b.spineMin) * 0.5f * grow;
            return Mathf.Abs(ca.x - cb.x) <= ea.x + eb.x
                && Mathf.Abs(ca.y - cb.y) <= ea.y + eb.y
                && Mathf.Abs(ca.z - cb.z) <= ea.z + eb.z;
        }

        private static readonly List<SmokeVolumeGroup> freshGroups = new List<SmokeVolumeGroup>();
        private static readonly List<List<SmokeVolumeGroup>> clusterPool = new List<List<SmokeVolumeGroup>>();
        private static int[] clusterParent = new int[16];
        private static int[] clusterSize = new int[16];

        private static int FindRoot(int i)
        {
            while (clusterParent[i] != i)
            {
                clusterParent[i] = clusterParent[clusterParent[i]];
                i = clusterParent[i];
            }
            return i;
        }

        // Called once a frame, after every group has had its FixedUpdate tick.
        public static void RenderAll()
        {
            if (SmokeTuning.HideOldTrail)
            {
                for (int h = 0; h < all.Count; h++) if (all[h] != null) all[h].HideAll();
                freshGroups.Clear();
                return;
            }
            freshGroups.Clear();
            float now = Time.time;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                if (all[i] == null) all.RemoveAt(i);
            }
            for (int i = 0; i < all.Count; i++)
            {
                SmokeVolumeGroup g = all[i];
                if (!g.usePolylineActiveTrail || g.hiddenByToggle) continue;
                // A vessel that is not being simulated (unloaded, on rails) stops ticking.
                // Leave its volume exactly as it was rather than re-uploading stale lists.
                if (now - g.lastTickTime > 1f) continue;
                if (g.body == null) continue;
                freshGroups.Add(g);
            }

            int n = freshGroups.Count;
            if (n == 0) return;
            if (clusterParent.Length < n)
            {
                clusterParent = new int[n * 2];
                clusterSize = new int[n * 2];
            }
            for (int i = 0; i < n; i++) { clusterParent[i] = i; clusterSize[i] = 1; }

            for (int i = 0; i < n; i++)
            {
                SmokeVolumeGroup a = freshGroups[i];
                for (int j = i + 1; j < n; j++)
                {
                    SmokeVolumeGroup b = freshGroups[j];
                    if (a.body != b.body) continue;

                    int ra = FindRoot(i), rb = FindRoot(j);
                    if (ra == rb) continue;
                    if (clusterSize[ra] + clusterSize[rb] > MaxMergedGroups) continue;

                    // Merged pairs are HELD: once two trails are drawn as one they stay one for a few seconds after their bounds stop
                    // touching, and a frame in which one of them has no bounds (it had no points for an instant) does not split them.
                    // Distance hysteresis alone let a pair that hovers at the touching distance, or a trail that flickered in and out of
                    // existence, switch between one volume and two - and every switch re-builds the spine (the twitch).
                    bool wereMerged = a.lastLeaderId != 0 && a.lastLeaderId == b.lastLeaderId;
                    bool touch = a.hasSpineBounds && b.hasSpineBounds && BoundsTouch(a, b, wereMerged ? MergeHysteresis : 1f);
                    if (touch) { a.mergeHoldUntil = now + MergeHoldSeconds; b.mergeHoldUntil = now + MergeHoldSeconds; }
                    else if (!(wereMerged && now < a.mergeHoldUntil && now < b.mergeHoldUntil)) continue;

                    clusterParent[rb] = ra;
                    clusterSize[ra] += clusterSize[rb];
                }
            }

            // one member list per root, leader (lowest instance id, so the first) first
            int lists = 0;
            for (int i = 0; i < n; i++)
            {
                if (FindRoot(i) != i) continue;
                if (lists >= clusterPool.Count) clusterPool.Add(new List<SmokeVolumeGroup>());
                List<SmokeVolumeGroup> members = clusterPool[lists++];
                members.Clear();
                for (int j = 0; j < n; j++)
                {
                    if (FindRoot(j) == i) members.Add(freshGroups[j]);
                }

                // the leader is the one that led this cluster last frame (if it is still in it): the volume that is drawn - its noise
                // offset, its history for the temporal upscaling - must not change hands just because the list order did
                SmokeVolumeGroup leader = members[0];
                for (int m = 1; m < members.Count; m++)
                    if (members[m].instanceId != 0 && members[m].instanceId == members[m].lastLeaderId) { leader = members[m]; break; }
                if (leader != members[0]) { members.Remove(leader); members.Insert(0, leader); }
                for (int m = 0; m < members.Count; m++)
                {
                    if (members[m].MergedCount != members.Count && Time.time - lastMergeLog > 0.2f)
                    {
                        lastMergeLog = Time.time;
                        Debug.Log("[PlumeTrails] trail merge: group " + members[m].instanceId + " now in a cluster of " + members.Count + " (was " + members[m].MergedCount + "), leader " + leader.instanceId);
                    }
                    members[m].lastLeaderId = leader.instanceId;
                    members[m].MergedCount = members.Count;
                    if (m > 0) SmokeRenderRegistry.SetActive(members[m].polylineRenderer, false);
                }
                leader.UploadCluster(members);
            }
        }

        // The merged chain, each member smoothed on its own and joined by zero-radius
        // separators.
        //
        // Smoothing has to happen per member: the 1-2-1 pass averages each point with its
        // neighbours, and across a chain boundary the neighbour is a point on a DIFFERENT
        // trail that may be hundreds of metres away, which would drag both ends toward
        // each other. The separators are exact copies of the two end points, so the
        // capsule between them has no radius and draws nothing.
        private readonly Vector3[] mergeSpine = new Vector3[MaxSpinePoints];
        private readonly float[] mergeRadii = new float[MaxSpinePoints];
        private readonly float[] mergeDensity = new float[MaxSpinePoints];

        // Split the shared buffer between the chains by WATER-FILLING their demands: a chain that needs less than
        // an equal share (a booster that has just lit, a short trail) gets exactly what it needs and the rest goes
        // to the long ones. An equal split halved the old trail's budget the moment a second chain appeared, which
        // is what made half of it vanish when the SRBs ignited. Continuous in the demands, so no jumps.
        private static readonly int[] wfShare = new int[64];
        private static readonly bool[] wfDone = new bool[64];
        private static void WaterFill(List<SmokeVolumeGroup> members, int budget)
        {
            int n = Mathf.Min(members.Count, 64);
            int open = 0;
            for (int k = 0; k < n; k++)
            {
                wfDone[k] = members[k].activeOrderedPos.Count < 2;
                wfShare[k] = 0;
                if (!wfDone[k]) open++;
            }
            int remaining = budget;
            // The groups that are the SOURCE of the pad flow (a booster over the trench) get a reserved share first: with seven groups of a launch vehicle in
            // one cluster the equal share was 44 points, a third of it (15) went to the streams, under their minimum, and the pad cloud was not drawn at all
            // (the log: "stream budget 15 < 20 (share 44)"). The rest is water-filled among the others.
            for (int k = 0; k < n; k++)
            {
                if (wfDone[k] || !members[k].HasLiveStreams(12f)) continue;
                int res = Mathf.Min(StreamReserve, Mathf.Max(remaining / 2, 0));
                wfShare[k] = res; remaining -= res; wfDone[k] = true; open--;
            }
            bool changed = true;
            while (open > 0 && changed)
            {
                changed = false;
                int level = remaining / open;
                for (int k = 0; k < n; k++)
                {
                    if (wfDone[k]) continue;
                    int d = Mathf.Max(members[k].lastFullDemand, 16);
                    if (d <= level)
                    {
                        wfShare[k] = d; remaining -= d; wfDone[k] = true; open--; changed = true;
                    }
                }
            }
            for (int k = 0; k < n; k++)
            {
                if (members[k].activeOrderedPos.Count < 2) continue;
                members[k].shareBudgetPts = wfDone[k] ? wfShare[k] : Mathf.Max(remaining / Mathf.Max(open, 1), 16);
            }
        }

        private int BuildMergedSpine(List<SmokeVolumeGroup> members)
        {
            int rawTotal = 0, chains = 0;
            for (int k = 0; k < members.Count; k++)
            {
                int n = members[k].activeOrderedPos.Count;
                if (n >= 2) { rawTotal += n; chains++; }
            }
            if (chains == 0) return 0;
            for (int k = 0; k < members.Count; k++) members[k].clusterChains = chains;

            // The buffer is shared, so each chain gets a slice in proportion to its length,
            // after setting aside the two separator points between chains. With one chain
            // this reduces to the old "thin to the buffer size" behaviour.
            int budget = MaxSpinePoints - 2 * (chains - 1);
            int total = 0;
            WaterFill(members, budget);

            for (int k = 0; k < members.Count; k++)
            {
                SmokeVolumeGroup m = members[k];
                int n = m.activeOrderedPos.Count;
                if (n < 2) continue;

                // Equal split by CHAIN COUNT, not by live point ratio.
                //
                // Proportional-to-length sounds better - a longer trail gets more of the
                // budget - but "length" here is activeOrderedPos.Count, which shifts by a
                // handful of points most ticks as puffs age out one end and spawn the
                // other. Recomputing everyone's share from that ratio every frame meant
                // EVERY merged chain's budget wobbled continuously, even when nothing about
                // the smoke itself had changed. chains only changes on an actual merge or
                // split, which is rare, so this is stable by construction instead of by luck.
                int perChainBudget = Mathf.Max(4, m.shareBudgetPts > 0 ? m.shareBudgetPts : budget / chains);
                int room = MaxSpinePoints - total - (total > 0 ? 2 : 0);
                if (room < 4) break;
                perChainBudget = Mathf.Min(perChainBudget, room);

                // RATCHETED integer stride, same idea as committedTrailStride above and for
                // the same reason: a stride recomputed fresh each frame (here as a FLOAT,
                // so it barely has to move at all) reselects a different subset of puffs
                // out of n every time n changes by even one - and since puff size varies
                // randomly +/-10% per puff (see AddPuff) plus ColumnWander, a different
                // subset means a different sequence of thick and thin points, which is
                // what read as "sometimes thin, sometimes thick chunks" with no visible
                // cause. An INTEGER stride that only ever grows keeps picking indices
                // 0, stride, 2*stride, ... - survivors stay exactly where they were, so the
                // apparent width only changes when the smoke's own radius actually does.
                int neededStride = n > 0 ? Mathf.Max(1, Mathf.CeilToInt(n / (float)perChainBudget)) : 1;
                if (neededStride <= 1) m.committedSpineStride = 1;
                else if (neededStride > m.committedSpineStride) m.committedSpineStride = neededStride;
                int intStride = m.committedSpineStride;
                int take = Mathf.Min(n, room, Mathf.CeilToInt(n / (float)intStride));

                List<Vector3> pos = m.activeOrderedPos;
                List<float> rad = m.activeOrderedRadius;
                List<float> den = m.activeOrderedDensity;
                if (intStride > 1 || take < n)
                {
                    m.polylineThinnedPos.Clear();
                    m.polylineThinnedRadius.Clear();
                    m.polylineThinnedDensity.Clear();
                    int prevSrc = -1;
                    for (int i = 0; i < take; i++)
                    {
                        // last point pinned to the newest one (tip/root), never stranded short of it
                        int src = i == take - 1 ? n - 1 : Mathf.Min(n - 1, i * intStride);
                        // A BREAK (a radius-0 point) that falls between two kept points is kept as a break: otherwise the thinning joined the
                        // two blobs / bursts it separated with a capsule across the gap
                        if (prevSrc >= 0 && src > prevSrc + 1 && m.polylineThinnedPos.Count + 3 <= room
                            && m.activeOrderedRadius[prevSrc] > BreakRadius && m.activeOrderedRadius[src] > BreakRadius)
                        {
                            bool brk = false;
                            for (int q = prevSrc + 1; q < src; q++) if (m.activeOrderedRadius[q] <= BreakRadius) { brk = true; break; }
                            if (brk)
                            {
                                m.polylineThinnedPos.Add(m.activeOrderedPos[prevSrc]); m.polylineThinnedRadius.Add(0f); m.polylineThinnedDensity.Add(1f);
                                m.polylineThinnedPos.Add(m.activeOrderedPos[src]); m.polylineThinnedRadius.Add(0f); m.polylineThinnedDensity.Add(1f);
                            }
                        }
                        m.polylineThinnedPos.Add(m.activeOrderedPos[src]);
                        m.polylineThinnedRadius.Add(m.activeOrderedRadius[src]);
                        m.polylineThinnedDensity.Add(m.activeOrderedDensity[src]);
                        prevSrc = src;
                    }
                    pos = m.polylineThinnedPos;
                    rad = m.polylineThinnedRadius;
                    den = m.polylineThinnedDensity;
                    n = take = m.polylineThinnedPos.Count;
                }

                // The chain joins spine points with exact straight capsules - no blur pass
                // like the old baked system - so spawn jitter and residual gimbal wobble
                // show up directly as a sawtooth.
                //
                // Decimation spacing scales with puff RADIUS, so anything that speeds growth
                // up also makes the spine sparser, and averaging over neighbours that are
                // now metres apart stops hiding the jitter - the sawtooth comes back after
                // a change that never touched positions. Extra passes are free at <=200
                // points.
                m.SmoothSpine(pos, rad, n, SpineSmoothPasses);
                m.SmoothRadii(rad, n, SpineSmoothPasses);

                if (total > 0)
                {
                    mergeSpine[total] = mergeSpine[total - 1];
                    mergeRadii[total] = 0f;
                    mergeDensity[total] = 1f;
                    total++;
                    mergeSpine[total] = m.smoothedSpine[0];
                    mergeRadii[total] = 0f;
                    mergeDensity[total] = 1f;
                    total++;
                }
                for (int i = 0; i < take; i++)
                {
                    mergeSpine[total] = m.smoothedSpine[i];
                    mergeRadii[total] = m.smoothedRadii[i];
                    mergeDensity[total] = den[i];
                    total++;
                }
            }

            System.Array.Copy(mergeSpine, smoothedSpine, total);
            System.Array.Copy(mergeRadii, smoothedRadii, total);
            return total;
        }

        private void UploadCluster(List<SmokeVolumeGroup> members)
        {
            int totalPoints = BuildMergedSpine(members);

            if (totalPoints == 0)
            {
                SmokeRenderRegistry.SetActive(polylineRenderer, false);
                return;
            }

            Vector3 boxMin = Vector3.positiveInfinity;
            Vector3 boxMax = Vector3.negativeInfinity;
            float radiusSum = 0f;

            // A thin trail seen from far away: its radius falls under one pixel of the (reduced) render buffer and the temporal filter blends it into the
            // pixels around it, so it fades out. The radius is kept at a minimum on the screen, and the density falls with (r / rMin), so the optical depth through the middle stays the same:
            // it still reads as the same line, not a fainter one that the filter loses (the mass is conserved only to first order: a distant trail is meant to be SEEN).
            float pxRad = 0f;
            Camera fcam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;
            if (fcam != null && SmokeTuning.MinTrailPixels > 0.01f)
                pxRad = SmokeTuning.MinTrailPixels * 2f * Mathf.Tan(0.5f * fcam.fieldOfView * Mathf.Deg2Rad) / Mathf.Max(fcam.pixelHeight * SmokeQuality.Current.renderScale, 100f);
            Vector3 camPos = fcam != null ? fcam.transform.position : Vector3.zero;

            for (int i = 0; i < totalPoints; i++)
            {
                float r = smoothedRadii[i];
                Vector3 p = smoothedSpine[i];
                float wDens = mergeDensity[i];
                if (pxRad > 0f && r > 0.01f)
                {
                    float rMin = pxRad * (p - camPos).magnitude;
                    if (r < rMin)
                    {
                        float hh = Mathf.Floor(wDens / 16f), rem = wDens - 16f * hh, ss = Mathf.Floor(rem / 2f), dd = rem - 2f * ss;
                        float k = r / rMin;
                        wDens = dd * k + 2f * ss + 16f * hh;
                        r = rMin;
                    }
                }
                spinePointsBuffer[i] = new Vector4(p.x, p.y, p.z, wDens);
                spineRadiiBuffer[i] = r;
                radiusSum += r;

                float warpMarginScale = Mathf.Clamp(r / ShaderReferenceRadius, 0.35f, 1f);
                float boundRadius = r * BoxRadiusMarginMultiplier + BoxWarpMargin * warpMarginScale + RadiusWarpMargin(r);
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
            // Clamped to 4, matching the shader's own clamp on the same value - NOT to 1.
            // Pinning it at 1 tells the shader every puff is reference-sized, so a bloomed
            // 500m puff gets noise and warp sized for an 18m one: detail roughly thirty
            // times too fine for the body it sits on, which is the stippled, fuzzy look
            // rather than a smooth bloom. Confirmed in the log, where it read 1.000 flat
            // while the plume was hundreds of metres across.
            // Ceiling matches the shader's own clamp (200). At 4 this pinned for the
            // entire climb - the log read radiusRatio=4.000 while the plume had bloomed
            // past 100x - and every scale the shader derives from it came out wrong.
            float radiusRatio = Mathf.Clamp(avgRadius / ShaderReferenceRadius, 0.05f, 200f);
            lastRadiusRatio = radiusRatio;

            polylinePropertyBlock.Clear();
            polylinePropertyBlock.SetInt("_SpineCount", totalPoints);
            polylinePropertyBlock.SetVectorArray("_SpinePoints", spinePointsBuffer);
            polylinePropertyBlock.SetFloatArray("_SpineRadii", spineRadiiBuffer);
            polylinePropertyBlock.SetInt("_SpineGroupCount", spineGroupCount);
            polylinePropertyBlock.SetVectorArray("_SpineGroupBounds", spineGroupBounds);
            polylinePropertyBlock.SetVector("_BoxCenter", new Vector4(boxCenter.x, boxCenter.y, boxCenter.z, 0f));
            polylinePropertyBlock.SetVector("_BoxExtents", new Vector4(boxExtents.x, boxExtents.y, boxExtents.z, 0f));
            polylinePropertyBlock.SetFloat("_TileRadiusRatio", radiusRatio);
            float maxSpineRadius = 0f;
            for (int mi = 0; mi < totalPoints; mi++)
            {
                if (spineRadiiBuffer[mi] > maxSpineRadius) maxSpineRadius = spineRadiiBuffer[mi];
            }
            polylinePropertyBlock.SetFloat("_MaxSpineRadius", maxSpineRadius);
            // strongest flame among the merged members; a member with no live tip is not burning
            Vector3 fPos = Vector3.zero, fEnd = Vector3.zero; float fStr = 0f, fRange = 25f; bool fSeg = false; Color fCol = flameCol;
            for (int fi = 0; fi < members.Count; fi++)
            {
                SmokeVolumeGroup fm = members[fi];
                if (fm.hasLiveTip && fm.flameStrength > fStr) { fStr = fm.flameStrength; fPos = fm.flamePos; fRange = fm.flameRange; fCol = fm.flameCol; fEnd = fm.flameEnd; fSeg = fm.flameSegment; }
            }
            polylinePropertyBlock.SetVector("_FlameEnd", new Vector4(fEnd.x, fEnd.y, fEnd.z, fSeg ? 1f : 0f));
            polylinePropertyBlock.SetVector("_FlameLight", new Vector4(fPos.x, fPos.y, fPos.z, fStr * SmokeTuning.FlameLightStrength));
            polylinePropertyBlock.SetVector("_FlameColor", new Vector4(fCol.r * SmokeTuning.FlameColorR / 1.0f, fCol.g * SmokeTuning.FlameColorG / 0.52f, fCol.b * SmokeTuning.FlameColorB / 0.16f, fRange));
            polylinePropertyBlock.SetFloat("_DepthBiasDistance", ActiveDepthBiasDistance);
            polylinePropertyBlock.SetFloat("_DepthBiasFraction", ActiveDepthBiasFraction);
            SmokeTuning.Apply(polylinePropertyBlock);
            ScattererSun.ApplyAt(polylinePropertyBlock, boxCenter);
            polylinePropertyBlock.SetFloat("_FireEmission", SmokeTuning.DeflectorFire * 2.4f);
            // After Apply, so it overrides the shared value. Thinning is per-VOLUME (it
            // follows the vessel's ambient pressure), which SmokeTuning cannot express.
            polylinePropertyBlock.SetFloat("_Density", SmokeTuning.Density * densityScale);
            // PER VOLUME, not global. Each volume anchors its noise on its own first puff,
            // and a global is written once per volume per frame - so with two engine
            // clusters the last one to tick wins and the other samples the noise field at
            // someone else's anchor. Two volumes side by side then look like different
            // materials, which is the "two different renderings" report.
            polylinePropertyBlock.SetVector("_SmokeNoiseOffset",
                new Vector4(lastNoiseShift.x, lastNoiseShift.y, lastNoiseShift.z, 0f));
            ApplyMarchLOD(polylinePropertyBlock, boxCenter, boxExtents, avgRadius);
            polylineRenderer.SetPropertyBlock(polylinePropertyBlock);
            SmokeRenderRegistry.Register(polylineRenderer, polylinePropertyBlock);

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
                    "[PlumeTrails] polyline: vol#{0} merged={12} alt={1:F0} spineCount={2} (rawPoints={3}) "
                    + "endToEnd={4:F0}m startAlt={5:F0} endAlt={6:F0} boxExtents={7} axis={8} "
                    + "| radiusRatio={9:F3} marchSteps={10} noiseShift={11}",
                    instanceId, tipAltitude, totalPoints, activeOrderedPos.Count,
                    endToEnd, startAlt, endAlt, boxExtents.ToString("F0"),
                    padAxisValid ? padOutflowAxis.ToString("F2") : "none",
                    lastRadiusRatio, lastMarchSteps, lastNoiseShift.ToString("F1"), members.Count));

                // Radius profile along the spine (oldest -> newest), 12 samples. A sudden thinning that is in
                // THESE numbers comes from the puffs/sizes; one that is not is a rendering matter.
                var prof = new System.Text.StringBuilder();
                int samples = Mathf.Min(12, totalPoints);
                for (int pi = 0; pi < samples; pi++)
                {
                    int idx = samples <= 1 ? 0 : pi * (totalPoints - 1) / (samples - 1);
                    prof.Append(smoothedRadii[idx].ToString("F1")).Append(pi + 1 < samples ? " " : "");
                }
                float rMax = 0f, rMin = float.MaxValue;
                for (int ri = 0; ri < totalPoints; ri++) { float rr = smoothedRadii[ri]; if (rr > rMax) rMax = rr; if (rr > 0.01f && rr < rMin) rMin = rr; }
                Debug.Log(string.Format("[PlumeTrails] radii: vol#{0} max={1:F1} min={2:F1} profile(old->new)=[{3}]",
                    instanceId, rMax, rMin == float.MaxValue ? 0f : rMin, prof));

                // Shading snapshot. "Dym ma zero tekstury" / "nieoswietlone popsute" could
                // be the SHADER doing the wrong thing, or it could be these values reaching
                // it as zero/default because something upstream never got applied (a stale
                // material, a property block that lost its values, etc.) - two very
                // different bugs that look identical on screen. This settles which one by
                // printing what the volume is ACTUALLY being told to render with.
                Color probeSky = RenderSettings.ambientSkyColor;
                Color probeGround = RenderSettings.ambientGroundColor;
                Debug.Log(string.Format(
                    "[PlumeTrails] shading: vol#{0} density={1:F2} absorption={2:F2} detail={3:F2} "
                    + "warp={4:F1} shadowStrength={5:F2} shadowDarkness={6:F2} ambientFloor={7:F2} "
                    + "multiScatter={8:F2} sunlit={9:F2} ambientProbe=(sky {10:F2} gnd {11:F2}) sceneAmbientBlend={12:F2}",
                    instanceId, SmokeTuning.Density, SmokeTuning.Absorption, SmokeTuning.DetailStrength,
                    SmokeTuning.SilhouetteWarpStrength, SmokeTuning.ShadowStrength, SmokeTuning.ShadowDarkness,
                    SmokeTuning.AmbientFloor, SmokeTuning.MultiScatterIntensity, SmokeTuning.SunlitBrightness,
                    probeSky.grayscale, probeGround.grayscale, SmokeTuning.SceneAmbientBlend));

                // Hunt for a "neck" - a point whose radius is a real local minimum, much
                // thinner than both neighbours. A smooth taper never does this; a visible
                // pinch-then-balloon in a screenshot (unlike the frame-to-frame jump the
                // live-tip warning catches) is a DIP BAKED INTO THE SPINE ITSELF, which
                // that warning cannot see because it only watches the newest point. Reused
                // the existing 1s throttle rather than a new one - a real neck sits on the
                // spine for more than one frame, so it will still be caught.
                int worstIdx = -1;
                float worstDip = 0f;
                for (int i = 1; i < totalPoints - 1; i++)
                {
                    float ra = spineRadiiBuffer[i - 1];
                    float rb = spineRadiiBuffer[i];
                    float rc = spineRadiiBuffer[i + 1];
                    if (ra <= 0f || rb <= 0f || rc <= 0f) continue;
                    float expected = (ra + rc) * 0.5f;
                    if (expected < 1f) continue; // ignore noise on near-zero radii
                    float dip = expected - rb;
                    if (dip > worstDip)
                    {
                        worstDip = dip;
                        worstIdx = i;
                    }
                }
                // 40% of the expected radius, so ordinary puff-to-puff size variation
                // (which this system relies on for a natural look) doesn't spam the log -
                // only a dip sharp enough to actually read as a waist in the silhouette.
                if (worstIdx >= 0 && worstDip > 0.4f * ((spineRadiiBuffer[worstIdx - 1] + spineRadiiBuffer[worstIdx + 1]) * 0.5f))
                {
                    Debug.LogWarning(string.Format(
                        "[PlumeTrails] instance {0}: spine neck at point {1}/{2}, radius {3:F1}m vs "
                        + "neighbours {4:F1}m/{5:F1}m ({6:F0}% dip) at world {7}, alt {8:F0}m. If this "
                        + "lines up with a pinch-then-balloon in a screenshot, the radius CURVE itself "
                        + "has a kink here (e.g. jet-phase-to-bloom transition, or a burst boundary), "
                        + "not a rendering/blend artifact.",
                        instanceId, worstIdx, totalPoints,
                        spineRadiiBuffer[worstIdx], spineRadiiBuffer[worstIdx - 1], spineRadiiBuffer[worstIdx + 1],
                        worstDip / ((spineRadiiBuffer[worstIdx - 1] + spineRadiiBuffer[worstIdx + 1]) * 0.5f) * 100f,
                        spinePointsBuffer[worstIdx].ToString("F0"),
                        body.GetAltitude((Vector3)spinePointsBuffer[worstIdx])));
                }
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
                    "[PlumeTrails] overlap: {0}/{1} neighbor pairs have a REAL gap - worst={2:F1}m at index={3} " +
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
                    "[PlumeTrails] overlap: all {0} neighbor pairs overlap - no real gaps in the puff data itself",
                    totalPoints - 1));
            }

            if (worstDensityIndex >= 0)
            {
                Debug.Log(string.Format(
                    "[PlumeTrails] density: worst midpoint density={0:F3} at index={1}/{2} " +
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
                float boundRadius = radius * BoxRadiusMarginMultiplier + BoxWarpMargin * warpMarginScale + RadiusWarpMargin(radius);
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
                    "[PlumeTrails] tile: tile={0} range=[{1},{2}) count={3} boxMin={4} boxMax={5}",
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

        private float smoothedMarchSteps = -1f;

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

            SmokeQuality.Profile q = SmokeQuality.Current;
            int lodFullMarch = Mathf.Max(8, Mathf.RoundToInt(LODFullMarchSteps * q.marchScale));
            int lodMinMarch = Mathf.Max(8, Mathf.RoundToInt(LODMinMarchSteps * q.marchScale));
            int nearMarch = Mathf.Max(8, Mathf.RoundToInt(56 * q.marchScale));
            int maxAdaptive = Mathf.Max(16, Mathf.RoundToInt(MaxAdaptiveMarchSteps * q.marchScale));
            int marchSteps;
            int lightMarchSteps;

            bool nearOrInside = cameraInside || distToSurface < NearSurfaceThreshold;

            if (nearOrInside)
            {
                // The closest, most scrutinised view, so it must not sit below the
                // far-distance floor. The shader hides under-sampling by dithering the
                // march offset, and that dither scales with stepSize - too few steps here
                // reads as grain exactly where the volume fills the most screen.
                marchSteps = nearMarch;
                lightMarchSteps = q.lightNear;
            }
            else
            {
                float t = Mathf.InverseLerp(LODFullQualityDistance, LODMinQualityDistance, dist);
                marchSteps = Mathf.RoundToInt(Mathf.Lerp(lodFullMarch, lodMinMarch, t));
                lightMarchSteps = Mathf.RoundToInt(Mathf.Lerp(q.lightFull, q.lightMin, t));
            }

            if (hasLiveTip)
            {
                float tipDist = Vector3.Distance(boxCenter, liveTipPos);
                float tt = Mathf.InverseLerp(TipLODFullDistance, TipLODMinDistance, tipDist);
                int tipMarchSteps = Mathf.RoundToInt(Mathf.Lerp(lodFullMarch, lodMinMarch, tt));
                int tipLightMarchSteps = Mathf.RoundToInt(Mathf.Lerp(q.lightFull, q.lightMin, tt));
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
                marchSteps = Mathf.Min(Mathf.Max(marchSteps, sizeAdaptiveSteps), maxAdaptive);
            }
            else
            {
                // Close range still needs the adaptive count when the box is large and the
                // puffs in it are small (a splash cloud: ~360 m box, ~2 m puffs) - at the
                // bare near count the ray steps over them and leaves holes. Capped at twice
                // the near count so a close, screen-filling box stays affordable.
                float boxDiagonal = boxExtents.magnitude * 2f;
                float targetStepSize = Mathf.Max(avgRadius / TargetStepsPerAvgRadius, 0.5f);
                int sizeAdaptiveSteps = Mathf.CeilToInt(boxDiagonal / targetStepSize);
                marchSteps = Mathf.Min(Mathf.Max(marchSteps, sizeAdaptiveSteps), nearMarch * 2);
                // CLOSE = EXPENSIVE: with the camera in or at the smoke the volume fills the screen, and the cost is (pixels x steps x light steps).
                // The steps are cut by up to 45% as the camera gets close (full count at 120 m from the surface, 55% inside); the dither of the
                // march offset and the temporal accumulation hide the coarser sampling, which is what made the fps fall to 2 close to the smoke.
                float closeness = cameraInside ? 1f : Mathf.Clamp01(1f - distToSurface / 120f);
                marchSteps = Mathf.Max(16, Mathf.RoundToInt(marchSteps * Mathf.Lerp(1f, 0.55f, closeness)));
            }

            // Detail LOD. Noise frequency is fixed in world space, so as the volume
            // shrinks on screen its detail falls below a pixel - and shaping a silhouette
            // at a scale the screen cannot resolve is what makes distant edges shimmer.
            // Fading it costs nothing visible, since it was never resolvable anyway.
            //
            // Gated by nearOrInside (27 IX fix), same as marchSteps above: dist is to the
            // BOX CENTRE, and a trail's box spans the whole multi-km column, so a camera
            // sitting right at the near surface of a tall trail still measured a huge
            // centre distance and got the far-away 0.3x fade - flattening exactly the
            // close-up shots that should show the most detail. nearOrInside already means
            // "the near surface is close", regardless of where the box centre sits.
            float detailFade = nearOrInside ? 1f : Mathf.Lerp(1f, 0.3f,
                Mathf.InverseLerp(LODFullQualityDistance, LODMinQualityDistance, dist));
            propertyBlock.SetFloat("_DetailStrength", SmokeTuning.DetailStrength * detailFade);
            propertyBlock.SetFloat("_EdgeErosionStrength", SmokeTuning.EdgeErosionStrength * detailFade);

            // QUANTISED. Measured: a one-step change in _MarchSteps visibly changes the
            // image, while a 10% change in _TileRadiusRatio changes nothing. Both counts
            // come from RoundToInt over a continuous camera-distance lerp, so they flip
            // between adjacent integers on the slightest movement, every sample position
            // shifts with them, and that is the flicker. Coarse increments change rarely
            // and visibly once instead of dithering every frame.
            // Fast up, slow down. The near/far branch above flips whenever the camera
            // crosses the near-surface threshold (which itself jitters as the box grows), and
            // the two branches give very different counts - log showed 80 -> 24 -> 48 -> 24
            // within a couple of seconds. Every flip re-seeds all sample positions, and the
            // low side is coarse enough to leave holes in thin smoke: the reported "holes and
            // terrible flicker". Rising at once keeps the good count; decaying at a fixed
            // rate turns a flip into a gentle fade instead of a per-frame toggle.
            if (smoothedMarchSteps < 0f || marchSteps >= smoothedMarchSteps)
                smoothedMarchSteps = marchSteps;
            else
                smoothedMarchSteps = Mathf.MoveTowards(smoothedMarchSteps, marchSteps, 12f * Time.unscaledDeltaTime);
            marchSteps = Mathf.RoundToInt(smoothedMarchSteps);
            marchSteps = Mathf.Max(8, (marchSteps / 8) * 8);
            lastMarchSteps = marchSteps;
            lightMarchSteps = Mathf.Max(2, (lightMarchSteps / 2) * 2);
            propertyBlock.SetInt("_MarchSteps", marchSteps);
            propertyBlock.SetInt("_LightMarchSteps", lightMarchSteps);
        }

        private void OnDestroy()
        {
            SmokeRenderRegistry.Remove(polylineRenderer);
            for (int i = 0; i < activeTiles.Count; i++) SmokeRenderRegistry.Remove(activeTiles[i].renderer);

            activeInstanceCount--;
            all.Remove(this);


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
