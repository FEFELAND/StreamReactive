using System;
using System.Collections.Generic;
using UnityEngine;

namespace StreamReactive;

internal static class ParticleSpawner
{
    private static GameObject? _particlePrefab;
    // Everything this class creates (the prefab and every pooled instance) lives
    // under this DontDestroyOnLoad root so song-scene teardown can never destroy
    // the shared material, the prefab, or the standby pool. Before this, the
    // prefab and all pooled systems were per-scene objects: every song transition
    // wiped the pool, and once the last scene-bound reference to the runtime
    // material was destroyed (plus a Resources.UnloadUnusedAssets pass on the
    // transition), the material itself was freed — leaving bursts, outlines and
    // trails "spawning" with a null material, invisible for the rest of the
    // session until the game was restarted.
    private static Transform? _root;
    private static readonly Queue<ParticleSystem> _pool = new();
    // The pool may contain the same instance twice (e.g. a scene change runs
    // StopAll while a ReturnToPoolAfter coroutine is still pending for that
    // system). A duplicate entry would hand the SAME particle system to two
    // concurrent explosions — the second config overwrites the first and one
    // explosion silently disappears. Guard membership so each system is pooled
    // at most once.
    private static readonly HashSet<ParticleSystem> _pooled = new();

    // Self-healing: the getter rebuilds the shared material if it was ever
    // unloaded/destroyed (fake-null), instead of returning null forever.
    internal static Material? ParticleMaterial
    {
        get
        {
            EnsureMaterial();
            return _particleMaterial;
        }
    }

    private static Material? _particleMaterial;

    internal static void Init()
    {
        var go = new GameObject("StreamReactiveParticleRoot");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _root = go.transform;

        _particleMaterial = CreateDefaultMaterial();
        MarkParticleAssetsPersistent();
        Plugin.Log.Debug("Particle material created.");
    }

    /// <summary>
    /// Excludes the runtime particle material and its texture from
    /// Resources.UnloadUnusedAssets. The persistent prefab/renderer also keeps
    /// them referenced, but flagging them is belt-and-suspenders against the
    /// teardown ordering leaving them momentarily unreferenced.
    /// </summary>
    private static void MarkParticleAssetsPersistent()
    {
        if (_particleMaterial == null)
            return;
        _particleMaterial.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        var texture = _particleMaterial.mainTexture;
        if (texture != null)
            texture.hideFlags |= HideFlags.DontUnloadUnusedAsset;
    }

    /// <summary>
    /// Rebuilds the shared material if it was destroyed outside our control
    /// (e.g. an unload pass in an older build where it was not yet anchored).
    /// Only a destroyed object reaches this path: a live one fails the fake-null
    /// check and is kept as-is, so the renderer keeps pointing at the same
    /// material instance.
    /// </summary>
    private static void EnsureMaterial()
    {
        if (_particleMaterial != null)
            return;
        _particleMaterial = CreateDefaultMaterial();
        MarkParticleAssetsPersistent();
        Plugin.Log.Debug("Particle material rebuilt after being unloaded.");
    }

    /// <summary>
    /// Builds the shared particle-system prefab ahead of time so the first cut
    /// burst (bits/sub/raid/bomb) never instantiates + configures it on a live
    /// gameplay frame. Safe to call during prewarm: creates one inactive object.
    /// </summary>
    internal static void PrewarmPrefab()
    {
        if (_particlePrefab != null)
            return;
        try
        {
            GetOrCreatePrefab();
            Plugin.Log.Debug("Particle prefab prewarmed.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"Particle prefab prewarm failed: {ex.Message}");
        }
    }

    private static Texture2D CreateWhiteCircleTexture(int size = 64)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var center = size / 2f;
        var radius = size / 2f - 1f;

        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center + 0.5f;
                float dy = y - center + 0.5f;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01((radius - dist) / 1.5f);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }

    private static Material CreateDefaultMaterial()
    {
        var shader = Shader.Find("Particles/Standard Unlit");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");
        if (shader == null)
            shader = Shader.Find("Particles/Standard Surface");

        Material mat;
        if (shader != null)
        {
            mat = new Material(shader);
            Plugin.Log.Debug($"Using shader: {shader.name}");
        }
        else
        {
            mat = new Material(Shader.Find("Sprites/Default"));
            Plugin.Log.Warn("No particle shader found, using Sprites/Default fallback.");
        }

        var tex = CreateWhiteCircleTexture();
        mat.mainTexture = tex;
        mat.SetColor("_Color", Color.white);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.renderQueue = 3000;
        return mat;
    }

    private static GameObject GetOrCreatePrefab()
    {
        if (_particlePrefab != null)
            return _particlePrefab;

        var prefab = new GameObject("StreamReactiveParticle");
        prefab.SetActive(false);
        if (_root != null)
            prefab.transform.SetParent(_root, false);

        var ps = prefab.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.startLifetime = 0.4f;
        main.startSpeed = 15f;
        main.startSize = 0.015f;
        main.loop = false;
        main.playOnAwake = false;
        main.maxParticles = 50000;
        main.startRotation3D = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 0f;

        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = 0;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.01f;

        var colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 0.5f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0f, 1f) }
        );
        colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        var sizeCurve = new AnimationCurve(
            new Keyframe(0f, 1f, 0f, -1.5f),
            new Keyframe(0.3f, 0.7f),
            new Keyframe(1f, 0f)
        );
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

        var renderer = prefab.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        EnsureMaterial();
        renderer.material = _particleMaterial;
        renderer.sortingFudge = -5f;

        _particlePrefab = prefab;
        return prefab;
    }

    private static int _spawnGeneration;

    private static Gradient? _whiteGradient;

    private static Gradient GetWhiteGradient()
    {
        if (_whiteGradient != null) return _whiteGradient;

        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 0.5f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0f, 1f) }
        );
        _whiteGradient = gradient;
        return gradient;
    }

    // Fresh gradient per rainbow spawn so the animation never touches a
    // gradient that another (non-rainbow) particle system is using.
    private static Gradient CreateWhiteGradient()
    {
        var gradient = new Gradient();
        gradient.SetKeys(GetWhiteGradient().colorKeys, GetWhiteGradient().alphaKeys);
        return gradient;
    }

    internal static void SpawnParticles(Vector3 position, int count, Color color, float scale, float lifetime, float speed, bool rainbow = false)
    {
        if (count <= 0)
            return;

        ParticleSystem? ps = null;
        try
        {
            var prefab = GetOrCreatePrefab();

            while (_pool.Count > 0)
            {
                var candidate = _pool.Dequeue();
                _pooled.Remove(candidate);
                if (candidate != null)
                {
                    ps = candidate;
                    break;
                }
            }

            if (ps == null)
            {
                var go = _root != null
                    ? UnityEngine.Object.Instantiate(prefab, _root)
                    : UnityEngine.Object.Instantiate(prefab);
                go.name = "StreamReactiveParticleInstance";
                ps = go.GetComponent<ParticleSystem>();
            }

            int generation = _spawnGeneration;
            ps.gameObject.SetActive(true);

            // A recycled system can resume with a stale "dead" playback state and
            // then Play() won't actually emit — the classic pooling flake that made
            // explosions intermittently vanish on cut. Force a full factory reset
            // before re-arming: stop, wipe particles, rewind, re-enable emission.
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Clear();
            ps.time = 0f;
            var emissionModule = ps.emission;
            emissionModule.enabled = true;

            ps.transform.position = position;
            ps.transform.rotation = Quaternion.identity;

            var main = ps.main;
            main.startLifetime = lifetime;
            main.startSpeed = speed;
            main.startSize = scale;
            main.startColor = rainbow ? Color.white : color;

            // Reset color-over-lifetime so a pooled system that previously ran a
            // rainbow animation does not leak its gradient into this spawn.
            var colorModule = ps.colorOverLifetime;
            colorModule.color = new ParticleSystem.MinMaxGradient(rainbow ? CreateWhiteGradient() : GetWhiteGradient());

            var emission = ps.emission;
            if (count > short.MaxValue)
                emission.SetBursts(new[]
                {
                    new ParticleSystem.Burst(0f, short.MaxValue),
                    new ParticleSystem.Burst(0f, (short)(count - short.MaxValue))
                });
            else
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });

            var renderer = ps.GetComponent<ParticleSystemRenderer>();

            ps.Play();
            if (rainbow)
                RuntimeHooks.RunCoroutine(AnimateRainbowParticles(ps, generation, lifetime + 0.5f));
            RuntimeHooks.RunCoroutine(ReturnToPoolAfter(ps, generation, lifetime + 0.5f));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"SpawnParticles failed at {position}: {ex}");
            if (ps != null)
            {
                try
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.gameObject.SetActive(false);
                }
                catch { }
                UnityEngine.Object.Destroy(ps.gameObject);
            }
        }
    }

    private static System.Collections.IEnumerator AnimateRainbowParticles(ParticleSystem ps, int generation, float duration)
    {
        var colorModule = ps.colorOverLifetime;
        var gradient = colorModule.color.gradient;
        if (gradient == null)
        {
            gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) }
            );
        }

        var elapsed = 0f;
        var colorKeys = gradient.colorKeys;
        var alphaKeys = gradient.alphaKeys;
        while (ps != null && generation == _spawnGeneration && elapsed < duration)
        {
            elapsed += Time.deltaTime;
            var c = NoteCosmeticController.RainbowColor(NoteCosmeticController.GetRainbowSpeed());
            for (int i = 0; i < colorKeys.Length; i++)
                colorKeys[i].color = c;
            gradient.SetKeys(colorKeys, alphaKeys);
            colorModule.color = new ParticleSystem.MinMaxGradient(gradient);
            yield return null;
        }
    }

    internal static void StopAll()
    {
        NoteCosmeticController.ClearQueues();

        _spawnGeneration++;
        foreach (var ps in _pool)
        {
            if (ps != null)
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.gameObject.SetActive(false);
            }
        }

        var active = UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>();
        foreach (var r in active)
        {
            if (r != null && r.gameObject.name == "StreamReactiveParticleInstance")
            {
                var ps = r.GetComponent<ParticleSystem>();
                if (ps != null)
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.gameObject.SetActive(false);
                    if (_pooled.Add(ps))
                        _pool.Enqueue(ps);
                }
            }
        }
    }

    private static System.Collections.IEnumerator ReturnToPoolAfter(ParticleSystem ps, int generation, float delay)
    {
        yield return new UnityEngine.WaitForSeconds(delay);

        if (ps != null && generation == _spawnGeneration && _pooled.Add(ps))
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.gameObject.SetActive(false);
            _pool.Enqueue(ps);
        }
    }
}
