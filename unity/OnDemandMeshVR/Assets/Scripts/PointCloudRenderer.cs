using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Draws an object's sparse cloud as small camera-facing points: the placeholder shown while the object has no
/// mesh. It is a ParticleSystem with emission off and motionless particles, one per point: billboards work in VR
/// stereo and the renderer is frustum-culled like any other, so clouds out of view cost nothing to draw.
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class PointCloudRenderer : MonoBehaviour
{
    ParticleSystem _ps;
    ParticleSystemRenderer _renderer;
    ParticleSystem.Particle[] _particles;

    public bool Visible
    {
        get => _renderer != null && _renderer.enabled;
        set
        {
            if (_renderer != null)
                _renderer.enabled = value;
        }
    }

    /// <param name="localPoints">points in the parent object's local frame (Unity axes)</param>
    /// <param name="material">a particle material, e.g. Universal Render Pipeline/Particles/Unlit</param>
    /// <param name="worldPointSize">point diameter in metres, whatever the object's scale</param>
    public void Show(Vector3[] localPoints, Material material, float worldPointSize, Color color)
    {
        _ps = GetComponent<ParticleSystem>();
        var main = _ps.main;
        main.playOnAwake = false;
        main.loop = false;
        main.startSpeed = 0f;
        main.gravityModifier = 0f;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;   // points follow the object
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;      // ...including its scale
        main.maxParticles = localPoints.Length;
        var emission = _ps.emission;
        emission.enabled = false;
        var shape = _ps.shape;
        shape.enabled = false;

        _renderer = GetComponent<ParticleSystemRenderer>();
        _renderer.renderMode = ParticleSystemRenderMode.Billboard;
        _renderer.sharedMaterial = material;
        _renderer.shadowCastingMode = ShadowCastingMode.Off;
        _renderer.receiveShadows = false;
        _renderer.sortMode = ParticleSystemSortMode.None;

        // with Hierarchy scaling the size is multiplied by the object's scale: compensate (scene objects are uniform)
        var scale = transform.lossyScale.x;
        var size = scale > 1e-6f ? worldPointSize / scale : worldPointSize;
        Color32 c = color;
        _particles = new ParticleSystem.Particle[localPoints.Length];
        for (var i = 0; i < localPoints.Length; i++)
        {
            _particles[i].position = localPoints[i];
            _particles[i].velocity = Vector3.zero;
            _particles[i].startSize = size;
            _particles[i].startColor = c;
            _particles[i].startLifetime = 1e8f;  // ~3 years: the points never expire
            _particles[i].remainingLifetime = 1e8f;
        }
        _ps.Play();
        _ps.SetParticles(_particles, _particles.Length);
        Visible = true;
    }

    void LateUpdate()
    {
        // safety net: if anything cleared or stopped the system (editor operations), put the points back
        if (_particles == null || _ps == null || _ps.particleCount == _particles.Length)
            return;
        if (!_ps.isPlaying)
            _ps.Play();
        _ps.SetParticles(_particles, _particles.Length);
    }
}
