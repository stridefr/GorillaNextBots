using System;
using System.Collections.Generic;
using System.IO;
using GorillaLocomotion;
using NextBots.UI;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextBotsRagdoll
{
    /// <summary>
    /// The dust a body throws up where it lands: a ring of it running out along the floor, a layer
    /// left behind, a short column up the middle, grit thrown ahead and bouncing, a crack of fine
    /// dust at the moment of impact and a scuff mark on the floor afterwards. It fires on the same
    /// impacts that make the landing sounds - yours and, through the same network message, other
    /// players' - so everyone sees everyone's.
    ///
    /// <para><b>Sprites, shaded by hand.</b> A mod cannot ship a shader, so the game's own sprite
    /// shader draws every puff. What makes them read as smoke rather than stickers is done on the
    /// CPU instead: each puff's colour comes from where it sits in the cloud - lit on the side
    /// facing the light, dark towards the middle and low down, sky-tinted above and floor-tinted
    /// below - and the puff texture has a lit top and a shaded underside painted into it. Unity
    /// draws the puffs facing whichever camera is looking, so the headset, the ragdoll mod's
    /// monitor camera and the kill cam all see it correctly.</para>
    ///
    /// <para><b>The numbers are the design page's.</b> <c>dust.json</c> holds the look; see
    /// <see cref="DustLook"/>. Everything here is a fixed budget - a hard cap on live puffs and
    /// grit - so a lobby full of tumbling ragdolls cannot run away with the frame rate.</para>
    /// </summary>
    public sealed class Dust : MonoBehaviour
    {
        public static Dust Instance { get; private set; }

        private const int MaxPuffs = 420;
        private const int MaxGrit = 260;
        private const int MaxScuffs = 6;

        /// <summary>Puffs this far from the camera are not worth building.</summary>
        private const float FarAway = 70f;

        private DustLook _look = new DustLook();
        private DateTime _lookStamp;
        private float _nextLookCheck;

        // ------------------------------------------------------------------ the cloud

        private sealed class Mote
        {
            public Vector3 Pos, Vel;
            public float Age, Life, Size, Tint, Seed, Alpha, Wobble;
            public float GroundY;
            public Vector3 Origin;
            public bool Flash;
            public bool Flat;
        }

        private readonly List<Mote> _puffs = new List<Mote>(MaxPuffs);
        private ParticleSystem _smokeSystem;
        private ParticleSystem.Particle[] _smokeBuf = new ParticleSystem.Particle[MaxPuffs];
        private bool _smokeEmpty = true;

        // ------------------------------------------------------------------ the grit

        private struct Grain
        {
            public Vector3 Pos, Vel;
            public float Age, Life, GroundY;
            public float Size, Rot, Spin, Tint;
            public int Shape;
            public float RestFor;
            public bool Rest;
        }

        /// <summary>How many different chunk outlines the atlas holds (a 2 x 2 grid).</summary>
        private const int Shapes = 4;

        private readonly List<Grain> _grit = new List<Grain>(MaxGrit);
        private ParticleSystem _gritSystem;
        private ParticleSystem.Particle[] _gritBuf = new ParticleSystem.Particle[MaxGrit];
        private bool _gritEmpty = true;

        // ------------------------------------------------------------------ the scuffs

        private sealed class Scuff
        {
            public Transform Tf;
            public Material Mat;
            public float Age = -1f;
            public float Strength;
            public float Radius;
        }

        private readonly Scuff[] _scuffs = new Scuff[MaxScuffs];
        private int _nextScuff;

        private Texture2D _puffTex, _chunkTex, _dotTex, _scuffTex;
        private Material _smokeMat, _gritMat;
        private bool _built;
        private float _lastPuff = -99f;

        private void Awake() => Instance = this;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            foreach (var t in new UnityEngine.Object[] { _puffTex, _chunkTex, _dotTex, _scuffTex, _smokeMat, _gritMat })
                if (t != null) Destroy(t);
            foreach (var s in _scuffs)
                if (s != null && s.Mat != null) Destroy(s.Mat);
        }

        // ================================================================== starting one

        /// <summary>
        /// Something landed at <paramref name="point"/> at <paramref name="speed"/> m/s. Harder
        /// hits throw more, further and thicker; a soft one below <c>MinSpeed</c> throws nothing.
        /// Only a floor gets dust: the point has to have ground close beneath it.
        /// </summary>
        public void Puff(Vector3 point, float speed, float reach = 0.9f)
        {
            if (!BridgeConfig.DustEnabled.Value) return;
            if (speed < BridgeConfig.DustMinSpeed.Value) return;
            if (Time.time - _lastPuff < 0.18f) return;
            if (!Finite(point) || !Finite(speed)) return;

            var eye = GTPlayer.Instance != null ? GTPlayer.Instance.mainCamera : null;
            if (eye != null && (eye.transform.position - point).sqrMagnitude > FarAway * FarAway) return;

            RaycastHit hit;
            if (!FindFloor(point, reach, out hit)) return;
            if (!Build()) return;

            _lastPuff = Time.time;
            float top = Mathf.Max(BridgeConfig.DustMinSpeed.Value + 0.1f, BridgeConfig.BreakSpeed.Value);
            float power = 0.3f + 0.7f * Mathf.Clamp01(Mathf.InverseLerp(BridgeConfig.DustMinSpeed.Value, top, speed));

            Emit(hit.point, hit.normal, power);
        }

        private static bool FindFloor(Vector3 point, float reach, out RaycastHit hit)
        {
            int mask;
            try { mask = GTPlayer.LocomotionEnabledLayers; }
            catch { mask = ~0; }

            if (Physics.Raycast(point + Vector3.up * 0.3f, Vector3.down, out hit, 0.3f + reach, mask,
                                QueryTriggerInteraction.Ignore) && hit.normal.y > 0.5f)
                return true;
            hit = default(RaycastHit);
            return false;
        }

        private void Emit(Vector3 ground, Vector3 normal, float power)
        {
            var look = _look;
            float scale = Mathf.Pow(power, 0.7f);
            float opacity = BridgeConfig.DustOpacity.Value;

            // Thicker the more of it there is; a thin cloud is wisps you can see through.
            float alpha0 = Mathf.Clamp(0.7f * Mathf.Pow(look.density, 0.7f) * opacity, 0.04f, 0.95f);

            int count = Mathf.Clamp(Mathf.RoundToInt(BridgeConfig.DustAmount.Value * power), 6, 300);

            // What share is ring, layer left behind, or column.
            float wr = look.ring, wf = 1f, wc = look.column * 1.2f;
            float total = Mathf.Max(0.01f, wr + wf + wc);
            wr /= total; wf /= total;

            // Make room: the oldest go first.
            int overflow = _puffs.Count + count + 1 - MaxPuffs;
            if (overflow > 0) _puffs.RemoveRange(0, Mathf.Min(overflow, _puffs.Count));

            for (int i = 0; i < count; i++)
            {
                float roll = UnityEngine.Random.value;
                float ang = UnityEngine.Random.value * Mathf.PI * 2f;
                var dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                float vary = 0.7f + UnityEngine.Random.value * 0.6f;
                float front = look.speed * look.growth * scale;

                var p = new Mote
                {
                    Origin = ground, GroundY = ground.y,
                    Seed = UnityEngine.Random.value * 10f,
                    Tint = 0.86f + UnityEngine.Random.value * 0.26f,
                    Alpha = alpha0 * (0.8f + UnityEngine.Random.value * 0.4f),
                    Size = look.size * 1.05f * (0.7f + UnityEngine.Random.value * 0.7f) * (0.6f + 0.4f * power),
                    Life = look.life * (0.6f + UnityEngine.Random.value * 0.7f) * (1f - 0.3f * look.erode) * (0.7f + 0.3f * power),
                    Wobble = (UnityEngine.Random.value - 0.5f) * 30f,
                };

                if (roll < wr)
                {
                    // The ring: thrown out along the floor, barely off it. It is the leading edge.
                    p.Pos = ground + dir * 0.18f + Vector3.up * (0.1f + UnityEngine.Random.value * 0.25f);
                    p.Vel = dir * front * vary;
                    p.Vel.y = look.speed * 0.1f * UnityEngine.Random.value;
                    p.Flat = true;
                }
                else if (roll < wr + wf)
                {
                    // The layer it leaves behind: slow, filling in the middle of the cloud.
                    p.Pos = ground + dir * (0.1f + UnityEngine.Random.value * 0.6f) + Vector3.up * (0.1f + UnityEngine.Random.value * 0.4f);
                    p.Vel = dir * front * (0.08f + UnityEngine.Random.value * 0.27f);
                    p.Flat = true;
                }
                else
                {
                    // The column the body pushed straight up.
                    p.Pos = ground + new Vector3((UnityEngine.Random.value - 0.5f) * 0.4f, 0.15f + UnityEngine.Random.value * 0.5f,
                                                 (UnityEngine.Random.value - 0.5f) * 0.4f);
                    p.Vel = dir * look.speed * 0.2f * scale * vary;
                    p.Vel.y = look.speed * 0.45f * scale * (0.5f + UnityEngine.Random.value);
                    p.Size *= 1.25f;
                }
                _puffs.Add(p);
            }

            // The crack of fine dust at the moment of impact: a wide pale disc that is there for a
            // few frames and gone.
            if (look.flash > 0.01f && _puffs.Count < MaxPuffs)
            {
                _puffs.Add(new Mote
                {
                    Origin = ground, GroundY = ground.y, Flash = true, Flat = true,
                    Pos = ground + Vector3.up * 0.12f, Life = 0.16f,
                    Size = (0.8f + Mathf.Min(look.speed * 0.4f, 2.4f)) * scale,
                    Alpha = look.flash * 0.5f, Tint = 1.15f, Seed = 0f,
                });
            }

            // Debris: chips of the floor kicked up by the hit. The cloud says something landed; the
            // debris says it was heavy - if it looks like debris. What reads as debris and what
            // reads as snow is nearly all in four things: chunky angular pieces rather than soft
            // dots, sizes that are mostly small with a few big ones, a tumble, and coming down and
            // settling on the floor instead of floating off.
            int grains = Mathf.RoundToInt(look.grit * power);
            int room = MaxGrit - _grit.Count;
            if (room < grains) _grit.RemoveRange(0, Mathf.Min(_grit.Count, grains - room));
            for (int i = 0; i < grains && _grit.Count < MaxGrit; i++)
            {
                float a = UnityEngine.Random.value * Mathf.PI * 2f;

                // Mostly kicked out a short way, a few a long way: the spread of a real hit, and
                // it keeps the debris inside the dust rather than fired out past it in a ring.
                float far = UnityEngine.Random.value;
                far *= far;
                float horizontal = (1.6f + far * look.speed * 0.6f) * scale;
                float up = (1.8f + UnityEngine.Random.value * (2.2f + look.speed * 0.2f)) * scale;

                // Small pieces far more often than big ones.
                float sz = UnityEngine.Random.value;
                sz = sz * sz * sz;

                _grit.Add(new Grain
                {
                    Pos = ground + new Vector3(Mathf.Cos(a) * 0.25f, 0.06f + UnityEngine.Random.value * 0.12f, Mathf.Sin(a) * 0.25f),
                    Vel = new Vector3(Mathf.Cos(a) * horizontal, up, Mathf.Sin(a) * horizontal),
                    Life = 1.8f + UnityEngine.Random.value * 1.8f,
                    GroundY = ground.y,
                    Size = Mathf.Lerp(0.035f, 0.13f, sz) * look.gritSize,
                    Rot = UnityEngine.Random.value * 360f,
                    Spin = (UnityEngine.Random.value < 0.5f ? -1f : 1f) * (120f + UnityEngine.Random.value * 520f),
                    Tint = 0.7f + UnityEngine.Random.value * 0.55f,
                    Shape = UnityEngine.Random.Range(0, Shapes),
                });
            }

            if (look.scuff > 0.01f) PlaceScuff(ground, normal, power);
        }

        private void PlaceScuff(Vector3 ground, Vector3 normal, float power)
        {
            var s = _scuffs[_nextScuff];
            _nextScuff = (_nextScuff + 1) % MaxScuffs;
            if (s == null || s.Tf == null) return;

            s.Age = 0f;
            s.Strength = _look.scuff * Mathf.Lerp(0.5f, 1f, power);
            s.Radius = Mathf.Lerp(0.5f, 1f, power);
            // Just off the surface, or it fights the floor for the pixel.
            s.Tf.position = ground + normal * 0.02f;
            s.Tf.rotation = Quaternion.FromToRotation(Vector3.back, normal) * Quaternion.Euler(0f, 0f, UnityEngine.Random.value * 360f);
            s.Tf.gameObject.SetActive(true);
        }

        // ================================================================== every frame

        private void LateUpdate()
        {
            LoadLook();
            if (!_built) return;

            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            StepPuffs(dt);
            StepGrit(dt);
            StepScuffs(dt);
            WriteSmoke();
            WriteGrit();
        }

        private void StepPuffs(float dt)
        {
            var look = _look;
            float time = Time.time;
            float d = Mathf.Exp(-look.drag * dt);

            var a = look.lightAngle * Mathf.Deg2Rad;
            var windDir = new Vector3(Mathf.Cos(a + 1.1f), 0f, Mathf.Sin(a + 1.1f));

            for (int i = _puffs.Count - 1; i >= 0; i--)
            {
                var p = _puffs[i];
                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    _puffs[i] = _puffs[_puffs.Count - 1];
                    _puffs.RemoveAt(_puffs.Count - 1);
                    continue;
                }
                if (p.Flash) continue;

                // Curl: a slow swirl that varies with where the puff is, which is what stops a
                // burst looking like something fired out of a nozzle.
                if (look.curl > 0.001f)
                {
                    float c = look.curl * dt;
                    p.Vel.x += Mathf.Sin(p.Pos.z * 0.9f + time * 0.7f + p.Seed) * c;
                    p.Vel.z += Mathf.Cos(p.Pos.x * 0.9f + time * 0.6f + p.Seed) * c;
                    p.Vel.y += Mathf.Sin(p.Pos.x * 0.5f + p.Pos.z * 0.5f + time * 0.4f) * c * 0.35f;
                }

                p.Vel.x *= d; p.Vel.z *= d;
                p.Vel.y = p.Vel.y * d + look.rise * dt;

                // Wind shears the cloud: the higher a puff is, the further it is carried.
                float h = p.Pos.y - p.GroundY;
                float shear = 0.25f + Mathf.SmoothStep(0f, 1f, h / 1.6f);
                p.Pos += (p.Vel + windDir * (look.wind * 0.16f * shear)) * dt;

                float floor = p.GroundY + 0.05f + p.Size * 0.18f;
                if (p.Pos.y < floor)
                {
                    p.Pos.y = floor;
                    p.Vel.y = Mathf.Abs(p.Vel.y) * 0.25f;
                }
            }
        }

        private void StepGrit(float dt)
        {
            float bounce = _look.gritBounce;
            for (int i = _grit.Count - 1; i >= 0; i--)
            {
                var g = _grit[i];
                g.Age += dt;
                if (g.Age >= g.Life)
                {
                    _grit[i] = _grit[_grit.Count - 1];
                    _grit.RemoveAt(_grit.Count - 1);
                    continue;
                }

                if (!g.Rest)
                {
                    g.Vel.y -= 14f * dt;
                    // A little air resistance, so a chip slows and drops rather than sailing.
                    float air = Mathf.Exp(-0.5f * dt);
                    g.Vel.x *= air; g.Vel.z *= air;
                    g.Pos += g.Vel * dt;
                    g.Rot += g.Spin * dt;

                    float rest = g.GroundY + g.Size * 0.5f;
                    if (g.Pos.y <= rest)
                    {
                        g.Pos.y = rest;
                        if (Mathf.Abs(g.Vel.y) < 0.8f)
                        {
                            g.Rest = true;
                            g.Vel = Vector3.zero;
                        }
                        else
                        {
                            // Skips and slides. The tumble slows with every hit, the way a real
                            // chip's does, until it settles.
                            g.Vel.y = -g.Vel.y * bounce;
                            g.Vel.x *= 0.6f; g.Vel.z *= 0.6f;
                            g.Spin *= 0.55f;
                        }
                    }
                }
                else g.RestFor += dt;
                _grit[i] = g;
            }
        }

        private void StepScuffs(float dt)
        {
            float life = _look.scuffLife;
            foreach (var s in _scuffs)
            {
                if (s == null || s.Age < 0f) continue;
                s.Age += dt;
                if (s.Age >= life)
                {
                    s.Age = -1f;
                    s.Tf.gameObject.SetActive(false);
                    continue;
                }

                // Spreads a little as it settles, then just fades.
                float r = s.Radius * (0.7f + Mathf.Min(0.8f, s.Age * 0.8f));
                s.Tf.localScale = new Vector3(r * 2f, r * 2f, 1f);
                float a = s.Strength * 0.5f * Mathf.Pow(1f - s.Age / life, 1.2f) * Mathf.SmoothStep(0f, 1f, s.Age / 0.05f);
                s.Mat.color = new Color(0f, 0f, 0f, a);
            }
        }

        // ================================================================== drawing the cloud

        private static readonly Vector3 UpLight = new Vector3(0.35f, 0.75f, 0.55f);

        private void WriteSmoke()
        {
            int n = _puffs.Count;
            if (n == 0)
            {
                if (!_smokeEmpty) { _smokeSystem.SetParticles(_smokeBuf, 0); _smokeEmpty = true; }
                return;
            }
            _smokeEmpty = false;

            var look = _look;
            Color lit = look.Lit, shade = look.Shadow, sky = look.Sky, bounce = look.Bounce;

            // The scene's own sun if there is one, so the smoke is lit the way everything else is;
            // otherwise the angle from the file.
            Vector3 L;
            var sun = RenderSettings.sun;
            if (sun != null) L = -sun.transform.forward;
            else
            {
                float ang = look.lightAngle * Mathf.Deg2Rad;
                L = new Vector3(Mathf.Cos(ang), 0.72f, Mathf.Sin(ang)).normalized;
            }

            for (int i = 0; i < n; i++)
            {
                var p = _puffs[i];
                float u = p.Age / p.Life;

                // Size: grows as it goes; a puff on the floor is a little squashed, a pancake
                // rather than a ball.
                float grow = 1f + 1.6f * (1f - (1f - u) * (1f - u));
                float w = p.Size * grow;
                float h = w;
                float height = p.Pos.y - p.GroundY;
                if (p.Flat) h *= 1f - 0.5f * look.hug * (1f - Mathf.SmoothStep(0f, 1f, height / 0.9f));

                // Alpha: there at once, gone by the end, and thinner the more it has broken up.
                float alpha = p.Alpha * Mathf.SmoothStep(0f, 1f, p.Age / 0.02f) * Mathf.Pow(1f - u, 1.4f) * (1f - look.erode * 0.5f * u);

                Color col;
                if (p.Flash)
                {
                    float k = p.Age / p.Life;
                    alpha = p.Alpha * (1f - k) * (1f - k);
                    col = lit * p.Tint;
                    w = h = p.Size * (0.5f + k);
                }
                else
                {
                    // Where it sits in the cloud decides how it is lit: the side facing the light is
                    // bright, the far side dark, the middle dark because the light never got there.
                    Vector3 rel = p.Pos - (p.Origin + Vector3.up * 0.5f);
                    Vector3 nrm = (rel + Vector3.up * 0.7f).normalized;
                    float lam = Mathf.Clamp01(Vector3.Dot(nrm, L) * 0.5f + 0.5f);
                    col = Color.Lerp(shade, lit, Mathf.Pow(lam, 1.6f));

                    Color amb = Color.Lerp(bounce, sky, Mathf.Clamp01(height * 0.6f + 0.25f));
                    col = Color.Lerp(col, amb * (0.5f + 0.5f * lam), look.ambient * 0.4f);

                    // The underside sits in its own shadow, close to the floor.
                    col *= Mathf.Lerp(0.68f, 1f, Mathf.SmoothStep(0f, 1f, height / 0.55f));

                    float flat = new Vector2(rel.x, rel.z).magnitude;
                    float buried = (1f - Mathf.SmoothStep(0f, 1f, (flat - 0.2f) / 2f)) * (1f - Mathf.SmoothStep(0f, 1f, (u - 0.1f) / 0.7f));
                    col *= (1f - look.core * 0.35f * buried) * p.Tint;
                }

                col.a = Mathf.Clamp01(alpha);

                var part = new ParticleSystem.Particle();
                part.position = p.Pos;
                part.velocity = Vector3.zero;
                part.startLifetime = 1f;
                part.remainingLifetime = 1f;
                part.startColor = col;
                part.startSize3D = new Vector3(w, h, 1f);
                // The texture is shaded from above, so it must stay the right way up: only a gentle
                // wobble, never a spin.
                part.rotation = p.Wobble * Mathf.Sin(p.Age * 0.6f * look.churn + p.Seed) * 0.5f;
                _smokeBuf[i] = part;
            }
            _smokeSystem.SetParticles(_smokeBuf, n);
        }

        private void WriteGrit()
        {
            int n = _grit.Count;
            if (n == 0)
            {
                if (!_gritEmpty) { _gritSystem.SetParticles(_gritBuf, 0); _gritEmpty = true; }
                return;
            }
            _gritEmpty = false;

            // The floor's own colour, darkened: chips are the ground, not the dust.
            Color baseCol = Color.Lerp(_look.Bounce, _look.Shadow, 0.45f);

            for (int i = 0; i < n; i++)
            {
                var g = _grit[i];

                // Shrinks away at the end instead of fading: a solid thing does not go transparent.
                float left = g.Life - g.Age;
                float shrink = Mathf.SmoothStep(0f, 1f, left / 0.45f);
                float s = g.Size * shrink;

                var col = baseCol * g.Tint;
                col.a = 1f;

                var part = new ParticleSystem.Particle();
                part.position = g.Pos;
                part.velocity = Vector3.zero;
                // The frame of the atlas is picked through the particle's age: the sheet runs once
                // over a particle's life, so putting it a quarter of the way through shows the
                // second outline. The life is long so the system's own ageing cannot move it.
                part.startLifetime = 100f;
                part.remainingLifetime = 100f * (1f - (g.Shape + 0.5f) / Shapes);
                part.startColor = col;
                part.startSize3D = new Vector3(s, s, 1f);
                part.rotation = g.Rot;
                _gritBuf[i] = part;
            }
            _gritSystem.SetParticles(_gritBuf, n);
        }

        // ================================================================== building it

        private bool Build()
        {
            if (_built) return true;

            try
            {
                var shader = FindShader();
                if (shader == null)
                {
                    Plugin.Log.LogWarning("[Dust] no usable shader; dust is off");
                    BridgeConfig.DustEnabled.Value = false;
                    return false;
                }

                _puffTex = BuildPuffTexture(128);
                _chunkTex = BuildChunkTexture(64);
                _dotTex = BuildRadial(32, 0.25f, 0.95f, false);
                _scuffTex = BuildRadial(128, 0.05f, 0.95f, true);

                _smokeMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, mainTexture = _puffTex };
                _gritMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, mainTexture = _chunkTex };

                _smokeSystem = BuildSystem("Dust.Smoke", _smokeMat, MaxPuffs);
                _gritSystem = BuildSystem("Dust.Grit", _gritMat, MaxGrit);

                // The atlas: 2 x 2 chunk outlines, one shown per particle.
                var sheet = _gritSystem.textureSheetAnimation;
                sheet.enabled = true;
                sheet.mode = ParticleSystemAnimationMode.Grid;
                sheet.numTilesX = 2;
                sheet.numTilesY = 2;
                sheet.animation = ParticleSystemAnimationType.WholeSheet;
                sheet.cycleCount = 1;
                sheet.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));

                for (int i = 0; i < MaxScuffs; i++)
                {
                    var go = new GameObject("Dust.Scuff" + i);
                    go.transform.SetParent(transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = UiResources.Quad;
                    var r = go.AddComponent<MeshRenderer>();
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    r.receiveShadows = false;
                    var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, mainTexture = _scuffTex };
                    mat.color = new Color(0f, 0f, 0f, 0f);
                    r.sharedMaterial = mat;
                    go.SetActive(false);
                    _scuffs[i] = new Scuff { Tf = go.transform, Mat = mat };
                }

                _built = true;
                Plugin.Log.LogInfo("[Dust] built | shader " + shader.name);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Dust] could not build: " + ex.Message);
                BridgeConfig.DustEnabled.Value = false;
                return false;
            }
        }

        private static Shader FindShader()
        {
            foreach (var name in new[] { "Sprites/Default", "UI/Default", "Universal Render Pipeline/Particles/Unlit" })
            {
                try
                {
                    var s = Shader.Find(name);
                    if (s != null) return s;
                }
                catch { /* try the next one */ }
            }
            return UiResources.Unlit;
        }

        /// <summary>
        /// A particle system used only as a fast way to draw sprites that face whichever camera is
        /// looking. It never emits: every particle is written by <see cref="WriteSmoke"/> each
        /// frame, so this class owns the physics and the system owns nothing but the drawing.
        /// </summary>
        private ParticleSystem BuildSystem(string name, Material mat, int max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            main.duration = 3600f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = max;
            main.startSize3D = true;
            main.gravityModifier = 0f;

            var emission = ps.emission; emission.enabled = false;
            var shape = ps.shape; shape.enabled = false;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sortMode = ParticleSystemSortMode.Distance;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            // The default clamps a particle to half the screen, which would cut a big puff off
            // when it is close.
            r.maxParticleSize = 4f;
            r.minParticleSize = 0f;

            ps.Play();
            return ps;
        }

        // ================================================================== textures

        /// <summary>
        /// One puff: noise with a torn edge, and a sphere's worth of shading painted in - lit from
        /// above, dark underneath - so a sprite reads as a lump with a top and a bottom. The colour
        /// per puff is multiplied on top of this.
        /// </summary>
        private static Texture2D BuildPuffTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "NextBotsRagdoll.Puff", wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave
            };

            var pix = new Color32[size * size];
            var light = new Vector3(-0.25f, 0.8f, 0.55f).normalized;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);

                    float n = Fbm(dx * 2.2f + 4f, dy * 2.2f + 9f);

                    // A torn outline: the falloff is pushed in and out by the noise, not blurred.
                    float edge = 1f - Mathf.SmoothStep(0.30f, 1.02f, r + (n - 0.5f) * 0.55f);
                    float density = edge * (0.42f + 0.78f * n);
                    float a = Mathf.SmoothStep(0.03f, 0.6f, density);

                    var nrm = new Vector3(dx, dy, Mathf.Sqrt(Mathf.Max(0f, 1f - Mathf.Min(r * r, 1f))) + 0.35f).normalized;
                    float lam = Mathf.Clamp01(Vector3.Dot(nrm, light) * 0.5f + 0.5f);
                    float shade = Mathf.Clamp01(0.5f + 0.5f * Mathf.Pow(lam, 1.4f)) * (0.86f + 0.24f * n);

                    byte g = (byte)Mathf.Clamp(shade * 255f, 0f, 255f);
                    pix[y * size + x] = new Color32(g, g, g, (byte)Mathf.Clamp(a * 255f, 0f, 255f));
                }
            }
            tex.SetPixels32(pix);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>
        /// Four chips of stone in a 2 x 2 atlas. Each is an irregular convex polygon with hard,
        /// slightly anti-aliased edges - hard edges are what say "solid" - split into a lit face
        /// and a shaded face, with a darker rim so it has thickness. Left grey; the floor's colour
        /// is multiplied on top.
        /// </summary>
        private static Texture2D BuildChunkTexture(int cell)
        {
            int size = cell * 2;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "NextBotsRagdoll.Chunks", wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave
            };
            var pix = new Color32[size * size];
            var rng = new System.Random(7);

            for (int shape = 0; shape < Shapes; shape++)
            {
                int ox = (shape % 2) * cell, oy = (shape / 2) * cell;

                // An irregular convex outline: points round a circle at uneven radii.
                int corners = 5 + rng.Next(0, 3);
                var vx = new float[corners];
                var vy = new float[corners];
                float rot0 = (float)rng.NextDouble() * Mathf.PI * 2f;
                for (int k = 0; k < corners; k++)
                {
                    float a = rot0 + (k + (float)(rng.NextDouble() - 0.5) * 0.5f) / corners * Mathf.PI * 2f;
                    float r = cell * (0.28f + (float)rng.NextDouble() * 0.16f);
                    vx[k] = cell * 0.5f + Mathf.Cos(a) * r;
                    vy[k] = cell * 0.5f + Mathf.Sin(a) * r;
                }

                // The line splitting the lit face from the shaded one.
                float sa = (float)rng.NextDouble() * Mathf.PI * 2f;
                Vector2 split = new Vector2(Mathf.Cos(sa), Mathf.Sin(sa));

                for (int y = 0; y < cell; y++)
                {
                    for (int x = 0; x < cell; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;

                        // Signed distance to the edge: positive inside.
                        float d = float.MaxValue;
                        bool inside = true;
                        for (int k = 0; k < corners; k++)
                        {
                            int j = (k + 1) % corners;
                            float ex = vx[j] - vx[k], ey = vy[j] - vy[k];
                            float len2 = Mathf.Max(ex * ex + ey * ey, 1e-6f);

                            // Which side of this edge: all edges agreeing means inside.
                            float cross = (px - vx[k]) * ey - (py - vy[k]) * ex;
                            if (cross > 0f) inside = false;

                            // How far from the edge itself. The segment, not the infinite line it
                            // lies on: the line runs out past the corners and would draw hairlines.
                            float t = Mathf.Clamp01(((px - vx[k]) * ex + (py - vy[k]) * ey) / len2);
                            float cx = vx[k] + ex * t - px, cy = vy[k] + ey * t - py;
                            d = Mathf.Min(d, Mathf.Sqrt(cx * cx + cy * cy));
                        }
                        float signed = inside ? d : -d;
                        float alpha = Mathf.Clamp01(signed + 0.5f);

                        float side = Vector2.Dot(new Vector2(px - cell * 0.5f, py - cell * 0.5f), split);
                        float face = side > 0f ? 1f : 0.62f;
                        float rim = Mathf.Lerp(0.72f, 1f, Mathf.Clamp01(signed / (cell * 0.09f)));
                        float speck = 0.9f + 0.2f * Hash(x + shape * 97, y);

                        byte g = (byte)Mathf.Clamp(255f * face * rim * speck, 0f, 255f);
                        pix[(oy + y) * size + ox + x] = new Color32(g, g, g, (byte)(alpha * 255f));
                    }
                }
            }

            tex.SetPixels32(pix);
            tex.Apply(false, true);
            return tex;
        }

        private static Texture2D BuildRadial(int size, float inner, float outer, bool ragged)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave
            };
            var pix = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float ang = Mathf.Atan2(dy, dx);
                    float wob = ragged ? Mathf.Sin(ang * 7f + r * 9f) * 0.06f + Mathf.Sin(ang * 17f) * 0.03f : 0f;
                    float a = Mathf.Clamp01(1f - Mathf.SmoothStep(inner, outer, r + wob));
                    pix[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(pix);
            tex.Apply(false, true);
            return tex;
        }

        // Value noise, a few octaves, from a hash: no tables, nothing to load.
        private static float Hash(int x, int y)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + 1442695041;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xffff) / 65535f;
            }
        }

        private static float Value(float x, float y)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash(xi, yi), b = Hash(xi + 1, yi), c = Hash(xi, yi + 1), d = Hash(xi + 1, yi + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        private static float Fbm(float x, float y)
        {
            float f = 0f, amp = 0.5f, tot = 0f;
            for (int o = 0; o < 5; o++)
            {
                f += Value(x, y) * amp;
                tot += amp;
                amp *= 0.5f;
                x = x * 2.03f + 5.3f;
                y = y * 2.03f + 1.7f;
            }
            return f / tot;
        }

        // ================================================================== the look file

        private void LoadLook()
        {
            if (Time.unscaledTime < _nextLookCheck) return;
            _nextLookCheck = Time.unscaledTime + 1f;

            string path = DustLook.FilePath;
            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, JsonUtility.ToJson(new DustLook(), true));
                    _lookStamp = File.GetLastWriteTimeUtc(path);
                    return;
                }

                var stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _lookStamp) return;
                _lookStamp = stamp;

                var look = new DustLook();
                JsonUtility.FromJsonOverwrite(File.ReadAllText(path), look);
                look.Sanitise();
                _look = look;
                Plugin.Log.LogInfo("[Dust] look loaded | speed " + look.speed + " drag " + look.drag +
                                   " density " + look.density + " grit " + look.grit);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Dust] could not read dust.json (" + ex.Message + "); keeping the last good one");
            }
        }

        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
