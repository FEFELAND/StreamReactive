using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace StreamReactive;

/// <summary>
/// 3D particle projection effect. Spawns a ParticleSystem with particles
/// arranged in a 3D shape (built-in procedural or loaded from an OBJ file),
/// parks it in front of the player, and optionally animates it over time.
/// Built-in presets: star (static 3D star), nuke (converge → flash → mushroom
/// cloud), pulse (breathing sphere), spiral (4 orbiting emitter streams). Custom shapes via
/// OBJ files in UserData/StreamReactive/Projections/.
/// </summary>
internal sealed class ProjectionController : MonoBehaviour
{
    private const string ObjectName = "StreamReactiveProjection";

    private static ProjectionController? _instance;

    private ParticleSystem? _particles;
    private ParticleSystem.Particle[]? _particleBuffer;
    private Coroutine? _routine;
    private readonly List<ParticleSystem> _spiralEmitters = new();

    private volatile bool _showRequested;
    private volatile bool _stopRequested;
    private string? _reqName;
    private Color? _reqColor;
    private float? _reqDuration;
    private float? _reqSize;
    private float? _reqDistance;
    private float? _reqPositionX;
    private float? _reqPositionY;
    private float? _reqPositionZ;
    private float? _reqRotationX;
    private float? _reqRotationY;
    private float? _reqRotationZ;

    internal static void EnsureCreated()
    {
        if (_instance != null) return;
        var go = new GameObject(ObjectName);
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<ProjectionController>();
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    internal static void Show(
        string name, Color? color, float? duration, float? size, float? distance,
        float? positionX = null, float? positionY = null, float? positionZ = null,
        float? rotationX = null, float? rotationY = null, float? rotationZ = null)
    {
        var self = _instance;
        if (self == null) return;
        self._reqName = name;
        self._reqColor = color;
        self._reqDuration = duration;
        self._reqSize = size;
        self._reqDistance = distance;
        self._reqPositionX = positionX;
        self._reqPositionY = positionY;
        self._reqPositionZ = positionZ;
        self._reqRotationX = rotationX;
        self._reqRotationY = rotationY;
        self._reqRotationZ = rotationZ;
        self._stopRequested = false;
        self._showRequested = true;
    }

    internal static void StopAll()
    {
        var self = _instance;
        if (self == null) return;
        self._showRequested = false;
        self._stopRequested = true;
    }

    private void LateUpdate()
    {
        if (_stopRequested)
        {
            _stopRequested = false;
            EndRoutine();
            DestroyParticles();
        }

        if (_showRequested)
        {
            _showRequested = false;
            Begin();
        }
    }

    private void Begin()
    {
        var cfg = PluginConfig.Instance;
        var name = _reqName ?? "star";
        var color = _reqColor ?? cfg?.ProjectionColor ?? Color.white;
        var duration = Mathf.Clamp(_reqDuration ?? cfg?.ProjectionDuration ?? 8f, 0.5f, 30f);
        var size = Mathf.Clamp(_reqSize ?? cfg?.ProjectionSize ?? 3f, 0.5f, 10f);
        var fadeIn = Mathf.Clamp(cfg?.ProjectionFadeIn ?? 1f, 0f, 5f);
        var fadeOut = Mathf.Clamp(cfg?.ProjectionFadeOut ?? 2f, 0f, 8f);
        var distance = Mathf.Clamp(_reqDistance ?? cfg?.ProjectionDistance ?? 4f, 1f, 15f);
        var particleSize = Mathf.Clamp(cfg?.ProjectionParticleSize ?? 0.03f, 0.005f, 0.1f);
        var maxCount = Mathf.Clamp(cfg?.ProjectionParticleCount ?? 3000, 100, 50000);

        // Position: X always defaults to 0; Y defaults to the eye-level 1.5 (or -1.3
        // lower for nuke), Z is the "distance" in front of the player. Rotation is
        // Euler degrees, defaulting to 0.
        var position = new Vector3(
            _reqPositionX ?? 0f,
            _reqPositionY ?? (string.Equals(name, "nuke", StringComparison.OrdinalIgnoreCase) ? 0.2f : 1.5f),
            _reqPositionZ ?? (_reqDistance ?? distance));
        var rotation = new Vector3(
            _reqRotationX ?? 0f,
            _reqRotationY ?? 0f,
            _reqRotationZ ?? 0f);

        EndRoutine();
        DestroyParticles();

        NoteCosmeticController.VerboseLog($"Projection: showing '{name}' for {duration:F1}s.");
        _routine = StartCoroutine(RunProjection(name, color, duration, fadeIn, fadeOut, size, position, rotation, particleSize, maxCount));
    }

    private IEnumerator RunProjection(
        string name, Color color, float duration, float fadeIn, float fadeOut,
        float size, Vector3 position, Vector3 rotation, float particleSize, int maxCount)
    {
        if (string.Equals(name, "spiral", StringComparison.OrdinalIgnoreCase))
        {
            yield return RunSpiralEmitters(color, duration, fadeIn, fadeOut, position, rotation, particleSize, maxCount);
            _routine = null;
            yield break;
        }

        var basePositions = LoadShape(name, maxCount);
        if (basePositions.Length == 0)
        {
            Plugin.Log.Warn($"Projection: shape '{name}' produced no particles; falling back to star.");
            basePositions = GenerateStar(maxCount);
        }

        for (int i = 0; i < basePositions.Length; i++)
            basePositions[i] *= size;

        var ps = CreateParticleSystem(basePositions.Length, particleSize, color);
        _particles = ps;

        ps.transform.position = position;
        ps.transform.rotation = Quaternion.Euler(rotation);

        var count = basePositions.Length;
        var buffer = new ParticleSystem.Particle[count];
        ps.Emit(count);
        ps.GetParticles(buffer);
        for (int i = 0; i < count; i++)
        {
            buffer[i].position = basePositions[i];
            buffer[i].startColor = color;
            buffer[i].startSize = particleSize;
            buffer[i].startLifetime = 999f;
        }
        ps.SetParticles(buffer, count);
        _particleBuffer = buffer;

        var animated = IsAnimatedPreset(name);
        var elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;

            float alpha;
            if (elapsed < fadeIn)
                alpha = elapsed / fadeIn;
            else if (elapsed > duration - fadeOut)
                alpha = (duration - elapsed) / fadeOut;
            else
                alpha = 1f;
            alpha = Mathf.Clamp01(alpha);

            var t = Mathf.Clamp01(elapsed / duration);
            var positions = animated ? Animate(name, basePositions, t) : basePositions;

            var liveCount = Mathf.Min(positions.Length, _particleBuffer.Length);
            ps.GetParticles(_particleBuffer, liveCount);
            for (int i = 0; i < liveCount; i++)
            {
                _particleBuffer[i].position = positions[i];
                var c = color;
                c.a = alpha;
                _particleBuffer[i].startColor = c;
            }
            ps.SetParticles(_particleBuffer, liveCount);

            yield return null;
        }

        DestroyParticles();
        _routine = null;
    }

    private ParticleSystem CreateParticleSystem(int count, float particleSize, Color color)
    {
        var go = new GameObject("StreamReactiveProjectionPS");
        go.transform.SetParent(transform, false);

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.startLifetime = 999f;
        main.startSpeed = 0f;
        main.startSize = particleSize;
        main.startColor = color;
        main.maxParticles = count;
        main.loop = false;
        main.playOnAwake = false;

        var emission = ps.emission;
        emission.enabled = false;

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        var mat = ParticleSpawner.ParticleMaterial;
        if (mat == null)
        {
            mat = new Material(Shader.Find("Sprites/Default")!);
            var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var center = 32f; var radius = 31f;
            var pixels = new Color[64 * 64];
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    float dx = x - center + 0.5f; float dy = y - center + 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    pixels[y * 64 + x] = new Color(1, 1, 1, Mathf.Clamp01((radius - d) / 1.5f));
                }
            tex.SetPixels(pixels); tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            mat.mainTexture = tex;
        }
        renderer.material = mat;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;

        return ps;
    }

    private void DestroyParticles()
    {
        if (_particles != null)
        {
            Destroy(_particles.gameObject);
            _particles = null;
            _particleBuffer = null;
        }
        DestroySpiralEmitters();
    }

    private void DestroySpiralEmitters()
    {
        foreach (var ps in _spiralEmitters)
            if (ps != null) Destroy(ps.gameObject);
        _spiralEmitters.Clear();
    }

    private IEnumerator RunSpiralEmitters(
        Color color, float duration, float fadeIn, float fadeOut,
        Vector3 position, Vector3 rotation, float particleSize, int maxCount)
    {
        DestroySpiralEmitters();

        const float orbitRadius = 1.5f;
        const float orbitSpeed = 2.5f;
        const float advanceSpeed = 3f;
        const float streamLife = 3f;
        const float velocitySpread = 0.6f;

        var root = new GameObject("StreamReactiveSpiral");
        root.transform.SetParent(transform, false);
        root.transform.position = position;
        root.transform.rotation = Quaternion.Euler(rotation);
        var rootTransform = root.transform;

        var ps = root.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        _spiralEmitters.Add(ps);

        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = streamLife;
        main.startSpeed = 0f;
        main.startSize = particleSize;
        main.startColor = color;
        main.maxParticles = maxCount;
        main.loop = false;
        main.playOnAwake = false;
        main.gravityModifier = 0f;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = orbitRadius;
        shape.radiusThickness = 0f;

        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = maxCount / streamLife;

        var renderer = root.GetComponent<ParticleSystemRenderer>();
        renderer.material = ParticleSpawner.ParticleMaterial ?? CreateDefaultMaterial();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;

        var buffer = new ParticleSystem.Particle[maxCount];
        var rng = new System.Random(42);
        ps.Play();

        int frame = 0;
        var elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;

            float alpha;
            if (elapsed < fadeIn) alpha = elapsed / fadeIn;
            else if (elapsed > duration - fadeOut) alpha = (duration - elapsed) / fadeOut;
            else alpha = 1f;
            alpha = Mathf.Clamp01(alpha);

            // Throttle: update velocities every 2nd frame — halves the
            // SetParticles overhead at high refresh rates with negligible
            // visual impact since velocity changes are small per frame.
            frame++;
            if ((frame & 1) == 0)
            {
                int live = ps.GetParticles(buffer);
                for (int i = 0; i < live; i++)
                {
                    var p = buffer[i];
                    var pos = p.position;

                    // Work in the projection root's local frame so the 4-arm
                    // orbit math follows the root's position and rotation.
                    var local = rootTransform.InverseTransformPoint(pos);
                    float px = local.x;
                    float py = local.y;
                    float angle = Mathf.Atan2(py, px);

                    // Snap to nearest of 4 arms (0, π/2, π, 3π/2)
                    float armAngle = Mathf.Round(angle / (Mathf.PI * 0.5f)) * Mathf.PI * 0.5f;

                    // Tangential direction from the snapped arm angle
                    float tx = -Mathf.Sin(armAngle);
                    float ty = Mathf.Cos(armAngle);

                    // Small random spread for organic feel
                    float spread = ((float)rng.NextDouble() - 0.5f) * velocitySpread;

                    var localVel = new Vector3(
                        tx * (orbitSpeed + spread),
                        ty * (orbitSpeed + spread),
                        -(advanceSpeed + spread * 0.5f));
                    p.velocity = rootTransform.TransformDirection(localVel);

                    var c = color;
                    c.a = alpha;
                    p.startColor = c;
                    buffer[i] = p;
                }
                ps.SetParticles(buffer, live);
            }

            yield return null;
        }

        emission.enabled = false;
        yield return new WaitForSeconds(streamLife);

        DestroySpiralEmitters();
    }

    private static Material CreateDefaultMaterial()
    {
        var mat = new Material(Shader.Find("Sprites/Default")!);
        var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
        var center = 32f; var radius = 31f;
        var pixels = new Color[64 * 64];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                float dx = x - center + 0.5f; float dy = y - center + 0.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                pixels[y * 64 + x] = new Color(1, 1, 1, Mathf.Clamp01((radius - d) / 1.5f));
            }
        tex.SetPixels(pixels); tex.Apply();
        tex.filterMode = FilterMode.Bilinear;
        mat.mainTexture = tex;
        return mat;
    }

    private void EndRoutine()
    {
        if (_routine == null) return;
        StopCoroutine(_routine);
        _routine = null;
    }

    // ── Shape loading ──────────────────────────────────────────────────

    private static Vector3[] LoadShape(string name, int maxCount)
    {
        return name.ToLowerInvariant() switch
        {
            "star" => GenerateStar(maxCount),
            "sphere" => GenerateSphere(maxCount),
            "nuke" or "pulse" => GenerateSphere(maxCount),
            _ => LoadObjOrFallback(name, maxCount)
        };
    }

    private static Vector3[] LoadObjOrFallback(string name, int maxCount)
    {
        var dir = Path.Combine(Environment.CurrentDirectory, "UserData", "StreamReactive", "Projections");
        var path = Path.Combine(dir, name + ".obj");

        if (!File.Exists(path))
        {
            Plugin.Log.Warn($"Projection: no built-in or OBJ file for '{name}'.");
            return Array.Empty<Vector3>();
        }

        try
        {
            var pts = ParseObj(path);
            Plugin.Log.Info($"Projection: loaded {pts.Length} vertices from '{name}'.obj'.");
            return FitCount(pts, maxCount);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warn($"Projection: failed to parse '{name}'.obj': {ex.Message}");
            return Array.Empty<Vector3>();
        }
    }

    private static bool IsAnimatedPreset(string name)
    {
        return string.Equals(name, "nuke", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "pulse", StringComparison.OrdinalIgnoreCase);
    }

    // ── OBJ parser ─────────────────────────────────────────────────────

    private static Vector3[] ParseObj(string path)
    {
        var vertices = new List<Vector3>();
        var faces = new List<int[]>();

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts[0] == "v" && parts.Length >= 4)
            {
                if (float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                    float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    vertices.Add(new Vector3(x, y, z));
            }
            else if (parts[0] == "f" && parts.Length >= 4)
            {
                var idx = new List<int>();
                for (int i = 1; i < parts.Length; i++)
                {
                    var v = parts[i].Split('/')[0];
                    if (int.TryParse(v, out var vi) && vi >= 1 && vi <= vertices.Count)
                        idx.Add(vi - 1);
                }
                if (idx.Count >= 3)
                    faces.Add(idx.ToArray());
            }
        }

        if (vertices.Count == 0) return Array.Empty<Vector3>();

        var sampled = faces.Count > 0
            ? SampleSurface(vertices, faces)
            : vertices.ToArray();

        return CenterAndNormalize(sampled);
    }

    private static Vector3[] SampleSurface(List<Vector3> verts, List<int[]> faces)
    {
        var result = new List<Vector3>(faces.Count * 3);
        foreach (var face in faces)
        {
            for (int i = 1; i < face.Length - 1; i++)
                result.Add(BarycentricSample(verts[face[0]], verts[face[i]], verts[face[i + 1]]));
        }
        return result.ToArray();
    }

    private static Vector3 BarycentricSample(Vector3 a, Vector3 b, Vector3 c)
    {
        var r1 = UnityEngine.Random.value;
        var r2 = UnityEngine.Random.value;
        var sq = Mathf.Sqrt(r1);
        return (1 - sq) * a + (sq * (1 - r2)) * b + (sq * r2) * c;
    }

    private static Vector3[] FitCount(Vector3[] pts, int target)
    {
        if (pts.Length == target) return pts;
        if (pts.Length > target) return Subsample(pts, target);
        return Pad(pts, target);
    }

    private static Vector3[] Subsample(Vector3[] pts, int count)
    {
        var r = new Vector3[count];
        var step = (float)pts.Length / count;
        for (int i = 0; i < count; i++)
            r[i] = pts[Mathf.FloorToInt(i * step)];
        return r;
    }

    private static Vector3[] Pad(Vector3[] pts, int count)
    {
        var r = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            var j = new Vector3(
                (UnityEngine.Random.value - 0.5f) * 0.02f,
                (UnityEngine.Random.value - 0.5f) * 0.02f,
                (UnityEngine.Random.value - 0.5f) * 0.02f);
            r[i] = pts[i % pts.Length] + j;
        }
        return r;
    }

    private static Vector3[] CenterAndNormalize(Vector3[] pts)
    {
        if (pts.Length == 0) return pts;

        var center = Vector3.zero;
        foreach (var p in pts) center += p;
        center /= pts.Length;
        for (int i = 0; i < pts.Length; i++) pts[i] -= center;

        float max = 0f;
        foreach (var p in pts)
        {
            var e = Mathf.Max(Mathf.Abs(p.x), Mathf.Max(Mathf.Abs(p.y), Mathf.Abs(p.z)));
            if (e > max) max = e;
        }
        if (max > 0.001f)
        {
            var s = 1f / max;
            for (int i = 0; i < pts.Length; i++) pts[i] *= s;
        }
        return pts;
    }

    // ── Built-in shapes ────────────────────────────────────────────────

    private static Vector3[] GenerateStar(int count)
    {
        var outer = new Vector3[5];
        var inner = new Vector3[5];
        for (int i = 0; i < 5; i++)
        {
            float aO = (90f + i * 72f) * Mathf.Deg2Rad;
            float aI = (90f + 36f + i * 72f) * Mathf.Deg2Rad;
            outer[i] = new Vector3(Mathf.Cos(aO), Mathf.Sin(aO), 0f) * 0.5f;
            inner[i] = new Vector3(Mathf.Cos(aI), Mathf.Sin(aI), 0f) * 0.2f;
        }

        var verts = new Vector3[10];
        for (int i = 0; i < 5; i++) { verts[i * 2] = outer[i]; verts[i * 2 + 1] = inner[i]; }

        var center = Vector3.zero;
        var pts = new List<Vector3>(count);
        for (int i = 0; i < 10; i++)
        {
            int next = (i + 1) % 10;
            int per = count / 10 + 1;
            for (int j = 0; j < per; j++)
                pts.Add(BarycentricSample(center, verts[i], verts[next]));
        }

        for (int i = 0; i < pts.Count; i++)
        {
            float r = Mathf.Sqrt(pts[i].x * pts[i].x + pts[i].y * pts[i].y);
            pts[i] = new Vector3(pts[i].x, pts[i].y, r * 0.25f * (i % 2 == 0 ? 1f : -1f));
        }

        return FitCount(pts.ToArray(), count);
    }

    private static Vector3[] GenerateSphere(int count)
    {
        var pts = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            float theta = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float phi = Mathf.Acos(UnityEngine.Random.Range(-1f, 1f));
            pts[i] = new Vector3(
                0.5f * Mathf.Sin(phi) * Mathf.Cos(theta),
                0.5f * Mathf.Cos(phi),
                0.5f * Mathf.Sin(phi) * Mathf.Sin(theta));
        }
        return pts;
    }

    // ── Animations ─────────────────────────────────────────────────────

    private static Vector3[] Animate(string preset, Vector3[] basePos, float t)
    {
        return preset.ToLowerInvariant() switch
        {
            "nuke" => AnimateNuke(basePos, t),
            "pulse" => AnimatePulse(basePos, t),
            _ => basePos
        };
    }

    private static Vector3[] AnimateNuke(Vector3[] basePos, float t)
    {
        var r = new Vector3[basePos.Length];
        var rng = new System.Random(42);

        if (t < 0.10f)
        {
            // PHASE 1: Beam coalesces high above — particles form a vertical column
            var ct = t / 0.10f; ct = ct * ct;
            for (int i = 0; i < basePos.Length; i++)
            {
                float ySpread = (float)rng.NextDouble() * 8f + 4f;
                float xzJitter = (float)(rng.NextDouble() * 2 - 1) * 0.08f;
                var highPoint = new Vector3(xzJitter, ySpread, xzJitter);
                r[i] = Vector3.Lerp(new Vector3((float)(rng.NextDouble() * 2 - 1) * 2f,
                    (float)rng.NextDouble() * 10f + 2f,
                    (float)(rng.NextDouble() * 2 - 1) * 2f), highPoint, ct);
            }
        }
        else if (t < 0.30f)
        {
            // PHASE 2: Beam strikes down — column slams to ground
            var ct = (t - 0.10f) / 0.20f; ct = ct * ct;
            for (int i = 0; i < basePos.Length; i++)
            {
                float ySpread = (float)rng.NextDouble() * 8f + 4f;
                float xzJitter = (float)(rng.NextDouble() * 2 - 1) * 0.08f;
                var highPoint = new Vector3(xzJitter, ySpread, xzJitter);
                var groundPoint = new Vector3(xzJitter * 3f, 0.05f, xzJitter * 3f);
                r[i] = Vector3.Lerp(highPoint, groundPoint, ct);
            }
        }
        else
        {
            // PHASE 3: Mushroom cloud explosion from impact
            var ct = Mathf.SmoothStep(0f, 1f, (t - 0.30f) / 0.70f);
            for (int i = 0; i < basePos.Length; i++)
                r[i] = MushroomCloudPos(basePos[i], ct);
        }
        return r;
    }

    private static Vector3 MushroomCloudPos(Vector3 p, float t)
    {
        float hDist = Mathf.Sqrt(p.x * p.x + p.z * p.z);
        const float stem = 0.5f;
        if (hDist < stem)
        {
            float h = (1f - hDist / Mathf.Max(stem, 0.01f)) * 3f + 1f;
            return new Vector3(p.x * 0.4f, h * t, p.z * 0.4f);
        }
        else
        {
            float ct = (hDist - stem) / Mathf.Max(1f - stem, 0.01f);
            float dome = Mathf.Sqrt(Mathf.Max(0f, 1f - ct * ct)) * 1.5f;
            float y = 3.5f * t + dome * t;
            return new Vector3(p.x * (1f + t), y, p.z * (1f + t));
        }
    }

    private static Vector3[] AnimatePulse(Vector3[] basePos, float t)
    {
        var r = new Vector3[basePos.Length];
        float breatheXZ = 1f + 0.3f * Mathf.Sin(t * Mathf.PI * 4f);
        float breatheY = 1f + 0.2f * Mathf.Sin(t * Mathf.PI * 4f + 0.5f);
        float rotY = t * Mathf.PI * 0.5f;
        float cr = Mathf.Cos(rotY); float sr = Mathf.Sin(rotY);
        for (int i = 0; i < basePos.Length; i++)
        {
            var p = basePos[i];
            float rx = p.x * cr - p.z * sr;
            float rz = p.x * sr + p.z * cr;
            r[i] = new Vector3(rx * breatheXZ, p.y * breatheY, rz * breatheXZ);
        }
        return r;
    }
}
