// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Client.Graphics;
using Content.Client.Light;
using Content.Shared.Light.Components;
using Content.Trauma.Shared.DynamicShadows;
using Robust.Client.ComponentTrees;
using Robust.Shared.Containers;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Timing;
using System.Diagnostics.CodeAnalysis;

namespace Content.Trauma.Client.DynamicShadows;

/// <summary>
/// Lamp shadows: sprite silhouettes projected away from point lights, weighted by each light's
/// share of the light reaching the caster, added together where they overlap.
/// Sun shadows: silhouettes drawn into upstream's sun shadow mask (see <see cref="DrawSunShadows"/>).
/// </summary>
public sealed partial class DynamicShadowOverlay : Overlay
{
    private static readonly ProtoId<ShaderPrototype> CastShaderProto = "DynamicShadowCast";
    private static readonly ProtoId<ShaderPrototype> AccumulateShaderProto = "DynamicShadowAccumulate";
    private static readonly ProtoId<ShaderPrototype> CompositeShaderProto = "DynamicShadowComposite";
    private static readonly ProtoId<ShaderPrototype> SunShaderProto = "DynamicShadowSunSilhouette";

    /// <summary>Max lamp shadows per caster. Each slot is added on top of the others.</summary>
    public const int SlotCount = 3;

    private const float LightSearchRange = 12f;
    private const float CasterSearchRange = 4f;
    private const float MinLightDistance = 0.35f;
    private const int MaxShadowsPerFrame = 512;

    /// <summary>
    /// Final alpha = MaxShadowAlpha * (1 - e^(-sum * Darkness)).
    /// Higher = darker, but overlaps always stay darker than single shadows.
    /// </summary>
    private const float Darkness = 3.35f;

    /// <summary>Must be 1 or less; alpha can't go above 1.</summary>
    private const float MaxShadowAlpha = 0.95f;

    private const float FadeHalfLife = 0.08f;
    private const float RemoveThreshold = 0.003f;
    private const uint ForgetAfterFrames = 3;

    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IEntityManager _ent = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _proto = default!;

    private readonly EntityLookupSystem _lookup;
    private readonly LightTreeSystem _lightTree;
    private readonly OccluderSystem _occluder;
    private readonly SharedContainerSystem _container;
    private readonly SharedTransformSystem _xform;
    private readonly SpriteSystem _sprite;

    private readonly EntityQuery<SpriteComponent> _spriteQuery;
    private readonly EntityQuery<SunShadowComponent> _sunQuery;
    private readonly EntityQuery<TransformComponent> _xformQuery;
    private readonly EntityQuery<DynamicShadowCasterComponent> _casterQuery;

    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowEntities;

    public float Opacity = 1f;
    public float BlurRadius = 1f;
    public bool UseOcclusion = true;
    public bool SunEnabled = true;

    private readonly ShaderInstance _accumulateShader;
    private readonly ShaderInstance _compositeShader;
    private readonly List<ShaderInstance> _castPool = new();
    private readonly List<ShaderInstance> _sunPool = new();

    private readonly HashSet<Entity<DynamicShadowCasterComponent>> _casters = new();
    private readonly HashSet<Entity<DynamicShadowCasterComponent>> _sunCasters = new();
    private readonly List<Entity<SharedPointLightComponent, TransformComponent>> _lightEntities = new();
    private readonly List<LightSample> _lights = new();
    private readonly List<ShadowCandidate> _candidates = new();

    private readonly Dictionary<EntityUid, CasterState> _states = new();
    private readonly List<EntityUid> _deadCasters = new();
    private readonly List<EntityUid> _deadShadows = new();
    private readonly List<ShadowState> _sortBuffer = new();

    /// <summary>Sprite alpha after the viewcone ran, from the last lamp pass. Used by the sun pass.</summary>
    private readonly Dictionary<EntityUid, float> _visibility = new();

    private readonly List<ShadowDraw>[] _slotDraws = new List<ShadowDraw>[SlotCount];

    private uint _lastSmoothedFrame;

    private readonly OverlayResourceCache<CachedResources> _resources = new();

    public DynamicShadowOverlay()
    {
        IoCManager.InjectDependencies(this);

        _lookup = _ent.System<EntityLookupSystem>();
        _lightTree = _ent.System<LightTreeSystem>();
        _occluder = _ent.System<OccluderSystem>();
        _container = _ent.System<SharedContainerSystem>();
        _xform = _ent.System<SharedTransformSystem>();
        _sprite = _ent.System<SpriteSystem>();

        _spriteQuery = _ent.GetEntityQuery<SpriteComponent>();
        _sunQuery = _ent.GetEntityQuery<SunShadowComponent>();
        _xformQuery = _ent.GetEntityQuery<TransformComponent>();
        _casterQuery = _ent.GetEntityQuery<DynamicShadowCasterComponent>();

        _accumulateShader = _proto.Index(AccumulateShaderProto).InstanceUnique();
        _compositeShader = _proto.Index(CompositeShaderProto).InstanceUnique();

        for (var i = 0; i < SlotCount; i++)
            _slotDraws[i] = new List<ShadowDraw>();

        // After ViewconeSetAlphaOverlay (ZIndex 0) so hidden entities cast no shadow.
        ZIndex = 1;
    }

    #region Lamp shadows

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (args.Viewport.Eye is not { } eye)
            return;

        var viewport = args.Viewport;
        var handle = args.WorldHandle;
        var mapId = args.MapId;
        var bounds = args.WorldBounds; // lambdas can't capture 'in' params

        var eyeRot = eye.Rotation;
        var up = (-eyeRot).RotateVec(Vector2.UnitY);
        var right = (-eyeRot).RotateVec(Vector2.UnitX);

        var frame = _timing.CurFrame;
        var dt = 0f;
        if (frame != _lastSmoothedFrame)
        {
            dt = (float) _timing.RealFrameTime.TotalSeconds;
            _lastSmoothedFrame = frame;
        }

        _casters.Clear();
        _lookup.GetEntitiesIntersecting(mapId, args.WorldAABB.Enlarged(CasterSearchRange), _casters);

        CollectLights(mapId, args.WorldAABB.Enlarged(LightSearchRange));
        UpdateTargets(mapId, up, frame);
        Smooth(dt, frame);

        if (!BuildDraws(up, right, frame))
            return;

        var res = _resources.GetForViewport(viewport, static _ => new CachedResources());
        EnsureTargets(res, viewport.Size);
        var scratch = res.Scratch!;
        var accum = res.Accum!;

        var worldToLocal = scratch.GetWorldToLocalMatrix(eye, viewport.RenderScale);
        var height = (float) scratch.Size.Y;
        var shaderIndex = 0;
        var firstSlot = true;

        for (var slot = 0; slot < SlotCount; slot++)
        {
            var draws = _slotDraws[slot];
            if (draws.Count == 0)
                continue;

            // Weakest first: where same-slot shadows overlap, the strongest replaces the rest.
            draws.Sort(static (a, b) => a.Strength.CompareTo(b.Strength));

            // 1) This slot's silhouettes into scratch. Layers replace each other -> no self-stacking.
            handle.RenderInRenderTarget(scratch, () =>
            {
                foreach (var draw in draws)
                {
                    var shader = GetPooled(_castPool, CastShaderProto, shaderIndex++);
                    var originLocal = Vector2.Transform(draw.Anchor, worldToLocal);
                    var axisLocal = Vector2.TransformNormal(draw.Axis, worldToLocal);

                    shader.SetParameter("shadowMatrix", draw.Matrix * worldToLocal);
                    shader.SetParameter("shadowStrength", draw.Strength);
                    shader.SetParameter("tipStrength", draw.TipStrength);
                    shader.SetParameter("shadowOrigin", new Vector2(originLocal.X, height - originLocal.Y));
                    shader.SetParameter("shadowAxis", new Vector2(axisLocal.X, -axisLocal.Y));

                    handle.UseShader(shader);
                    _sprite.RenderSprite((draw.Uid, draw.Sprite), handle, eyeRot, draw.WorldRot, draw.WorldPos);
                }

                handle.UseShader(null);
                handle.SetTransform(Matrix3x2.Identity);
            }, Color.Transparent);

            // 2) Add scratch onto the accumulator. Slots add -> overlapping shadows get darker.
            handle.RenderInRenderTarget(accum, () =>
            {
                handle.SetTransform(worldToLocal);
                handle.UseShader(_accumulateShader);
                handle.DrawTextureRect(scratch.Texture, bounds);
                handle.UseShader(null);
                handle.SetTransform(Matrix3x2.Identity);
            }, firstSlot ? Color.Transparent : null);

            firstSlot = false;
        }

        // 3) Blur + darkness curve + draw on the floor.
        _compositeShader.SetParameter("darkness", Darkness * Opacity);
        _compositeShader.SetParameter("maxAlpha", MaxShadowAlpha);
        _compositeShader.SetParameter("blurRadius", BlurRadius);
        _compositeShader.SetParameter("texelSize", Vector2.One / (Vector2) accum.Size);

        handle.SetTransform(Matrix3x2.Identity);
        handle.UseShader(_compositeShader);
        handle.DrawTextureRect(accum.Texture, bounds);
        handle.UseShader(null);
    }

    private void CollectLights(MapId mapId, Box2 bounds)
    {
        _lights.Clear();
        _lightEntities.Clear();
        _lightTree.QueryAabb(_lightEntities, mapId, bounds);

        foreach (var (uid, light, xform) in _lightEntities)
        {
            if (!light.Enabled || light.Radius <= 0f || light.Energy <= 0f)
                continue;

            var pos = _xform.GetWorldPosition(xform) + light.Offset;
            _lights.Add(new LightSample(uid, pos, light.Radius, Math.Clamp(light.Energy, 0f, 1f)));
        }
    }

    /// <summary>
    /// Target darkness per lamp = its contribution, scaled down when all light reaching the caster
    /// (lamps + sun) adds up to more than 1. The sun counts toward the total but is drawn by the sun pass.
    /// </summary>
    private void UpdateTargets(MapId mapId, Vector2 up, uint frame)
    {
        _visibility.Clear();

        foreach (var (uid, comp) in _casters)
        {
            if (!CanCast(uid, out var sprite))
            {
                _visibility[uid] = 0f;
                continue;
            }

            _visibility[uid] = sprite.Color.A;

            var xform = _xformQuery.GetComponent(uid);
            var anchor = _xform.GetWorldPosition(xform) + up * comp.FootOffset;

            if (!_states.TryGetValue(uid, out var state))
            {
                state = new CasterState();
                _states[uid] = state;
            }

            state.LastSeenFrame = frame;

            foreach (var shadow in state.Shadows.Values)
                shadow.Target = 0f;

            _candidates.Clear();
            var total = 0f;

            foreach (var light in _lights)
            {
                var delta = anchor - light.Position;
                var dist = delta.Length();
                if (dist < MinLightDistance || dist > light.Radius)
                    continue;

                var contribution = light.Energy * (1f - dist / light.Radius);
                if (contribution < 0.02f)
                    continue;

                var dir = delta / dist;
                if (UseOcclusion && IsOccluded(mapId, light, uid, dir, dist))
                    continue;

                var length = Math.Clamp(comp.MinLength + dist * comp.LengthPerTile, comp.MinLength, comp.MaxLength);
                _candidates.Add(new ShadowCandidate(light.Uid, dir, length, contribution));
                total += contribution;
            }

            // Sunlight dilutes lamp shadows outdoors, but its own shadow is drawn in DrawSunShadows.
            if (xform.GridUid is { } grid && _sunQuery.TryComp(grid, out var sun) && sun.Direction != Vector2.Zero)
                total += Math.Clamp(sun.Alpha, 0f, 1f);

            if (_candidates.Count == 0)
                continue;

            var scale = total > 1f ? 1f / total : 1f;

            _candidates.Sort(static (a, b) => b.Contribution.CompareTo(a.Contribution));
            var count = Math.Min(_candidates.Count, Math.Clamp(comp.MaxShadows, 1, SlotCount));

            for (var i = 0; i < count; i++)
            {
                var cand = _candidates[i];
                if (!state.Shadows.TryGetValue(cand.Source, out var shadow))
                {
                    shadow = new ShadowState();
                    state.Shadows[cand.Source] = shadow;
                }

                shadow.Direction = cand.Direction;
                shadow.Length = cand.Length;
                shadow.Target = Math.Clamp(cand.Contribution * scale * comp.Opacity, 0f, 1f);
            }
        }
    }

    private void Smooth(float dt, uint frame)
    {
        if (dt <= 0f)
            return;

        var k = 1f - MathF.Pow(2f, -dt / FadeHalfLife);

        _deadCasters.Clear();
        foreach (var (uid, state) in _states)
        {
            if (frame - state.LastSeenFrame > ForgetAfterFrames || !_ent.EntityExists(uid))
            {
                _deadCasters.Add(uid);
                continue;
            }

            _deadShadows.Clear();
            foreach (var (source, shadow) in state.Shadows)
            {
                shadow.Current += (shadow.Target - shadow.Current) * k;
                if (shadow.Target <= 0f && shadow.Current < RemoveThreshold)
                    _deadShadows.Add(source);
            }

            foreach (var source in _deadShadows)
                state.Shadows.Remove(source);
        }

        foreach (var uid in _deadCasters)
            _states.Remove(uid);
    }

    private bool BuildDraws(Vector2 up, Vector2 right, uint frame)
    {
        foreach (var list in _slotDraws)
            list.Clear();

        var total = 0;

        foreach (var (uid, state) in _states)
        {
            if (state.LastSeenFrame != frame || state.Shadows.Count == 0)
                continue;

            if (!_spriteQuery.TryComp(uid, out var sprite) || !_casterQuery.TryComp(uid, out var comp))
                continue;

            var xform = _xformQuery.GetComponent(uid);
            var (worldPos, worldRot) = _xform.GetWorldPositionRotation(xform);
            var anchor = worldPos + up * comp.FootOffset;

            _sortBuffer.Clear();
            foreach (var shadow in state.Shadows.Values)
            {
                if (shadow.Current > RemoveThreshold)
                    _sortBuffer.Add(shadow);
            }

            _sortBuffer.Sort(static (a, b) => b.Current.CompareTo(a.Current));
            var count = Math.Min(_sortBuffer.Count, SlotCount);

            for (var i = 0; i < count && total < MaxShadowsPerFrame; i++, total++)
            {
                var s = _sortBuffer[i];
                var matrix = BuildShadowMatrix(anchor, up, right, s.Direction, s.Length, comp.Width);
                var axis = s.Direction * s.Length * (0.5f - comp.FootOffset);

                _slotDraws[i].Add(new ShadowDraw(uid, sprite, worldPos, worldRot, matrix, anchor, axis,
                    Math.Clamp(s.Current, 0f, 1f), Math.Clamp(comp.TipStrength, 0f, 1f)));
            }
        }

        return total > 0;
    }

    #endregion

    #region Sun shadows

    /// <summary>
    /// Called from inside <see cref="SunShadowOverlay"/>'s mask pass. Draws solid white silhouettes
    /// stretched along the sun vector, exactly like the wall polygons, so they get the same
    /// blur, darkness and overlap behaviour.
    /// </summary>
    public void DrawSunShadows(SunShadowOverlay.SunShadowExtraArgs args)
    {
        if (!SunEnabled || args.Direction == Vector2.Zero)
            return;

        var handle = args.Handle;
        var eyeRot = args.Eye.Rotation;
        var up = (-eyeRot).RotateVec(Vector2.UnitY);
        var right = (-eyeRot).RotateVec(Vector2.UnitX);

        var sunLength = args.Direction.Length();
        var sunDir = args.Direction / sunLength;

        _sunCasters.Clear();
        _lookup.GetEntitiesIntersecting(args.MapId, args.Bounds, _sunCasters);

        var index = 0;
        foreach (var (uid, comp) in _sunCasters)
        {
            if (!comp.UseSun || index >= MaxShadowsPerFrame || !CanCast(uid, out var sprite))
                continue;

            var xform = _xformQuery.GetComponent(uid);
            if (xform.GridUid != args.Grid)
                continue;

            // Viewcone alpha from last frame (unknown = visible; those are off-screen anyway).
            var alpha = _visibility.GetValueOrDefault(uid, 1f);
            if (alpha < 0.5f)
                continue;

            var (worldPos, worldRot) = _xform.GetWorldPositionRotation(xform);
            var anchor = worldPos + up * comp.FootOffset;

            // Silhouette spans (0.5 - FootOffset) tiles from feet to head; stretch it so
            // the tip lands where a 1-tile wall's shadow would end.
            var spriteHeight = MathF.Max(0.5f - comp.FootOffset, 0.1f);
            var length = sunLength * comp.SunHeight / spriteHeight;

            var matrix = BuildShadowMatrix(anchor, up, right, sunDir, length, comp.Width);

            var shader = GetPooled(_sunPool, SunShaderProto, index++);
            shader.SetParameter("shadowMatrix", matrix * args.WorldToTarget);

            handle.UseShader(shader);
            _sprite.RenderSprite((uid, sprite), handle, eyeRot, worldRot, worldPos);
        }

        handle.UseShader(null);
    }

    #endregion

    #region Helpers

    private bool CanCast(EntityUid uid, [NotNullWhen(true)] out SpriteComponent? sprite)
    {
        if (!_spriteQuery.TryComp(uid, out sprite) || !sprite.Visible || sprite.ContainerOccluded)
            return false;

        return !_container.IsEntityInContainer(uid) && !HasLayerShader(sprite);
    }

    private static Matrix3x2 BuildShadowMatrix(Vector2 anchor, Vector2 up, Vector2 right, Vector2 dir, float length, float width)
    {
        var newUp = dir * length;
        var newRight = new Vector2(dir.Y, -dir.X) * width;

        var toSpriteAxes = new Matrix3x2(right.X, up.X, right.Y, up.Y, 0f, 0f);
        var toShadowAxes = new Matrix3x2(newRight.X, newRight.Y, newUp.X, newUp.Y, 0f, 0f);

        return Matrix3x2.CreateTranslation(-anchor)
               * toSpriteAxes
               * toShadowAxes
               * Matrix3x2.CreateTranslation(anchor);
    }

    private bool IsOccluded(MapId mapId, in LightSample light, EntityUid caster, Vector2 dir, float dist)
    {
        const float startOffset = 0.3f;
        if (dist <= startOffset)
            return false;

        var ray = new Ray(light.Position + dir * startOffset, dir);
        var hit = _occluder.IntersectRay(mapId, ray, dist - startOffset, (light.Uid, caster),
            static (ent, ignore) => ent.Owner == ignore.Item1 || ent.Owner == ignore.Item2);

        return hit != null;
    }

    private static bool HasLayerShader(SpriteComponent sprite)
    {
        foreach (var layer in sprite.AllLayers)
        {
            if (layer is SpriteComponent.Layer { Visible: true, Shader: not null })
                return true;
        }

        return false;
    }

    private ShaderInstance GetPooled(List<ShaderInstance> pool, ProtoId<ShaderPrototype> proto, int index)
    {
        while (pool.Count <= index)
            pool.Add(_proto.Index(proto).InstanceUnique());

        return pool[index];
    }

    private void EnsureTargets(CachedResources res, Vector2i size)
    {
        if (res.Scratch is { } existing && existing.Size == size)
            return;

        res.Dispose();
        var format = new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8Srgb);
        res.Scratch = _clyde.CreateRenderTarget(size, format, name: "dynamic-shadow-scratch");
        res.Accum = _clyde.CreateRenderTarget(size, format, name: "dynamic-shadow-accum");
    }

    protected override void DisposeBehavior()
    {
        _resources.Dispose();

        foreach (var shader in _castPool)
            shader.Dispose();
        foreach (var shader in _sunPool)
            shader.Dispose();

        _castPool.Clear();
        _sunPool.Clear();
        _accumulateShader.Dispose();
        _compositeShader.Dispose();

        base.DisposeBehavior();
    }

    #endregion

    private readonly record struct LightSample(EntityUid Uid, Vector2 Position, float Radius, float Energy);

    private readonly record struct ShadowCandidate(EntityUid Source, Vector2 Direction, float Length, float Contribution);

    private readonly record struct ShadowDraw(
        EntityUid Uid,
        SpriteComponent Sprite,
        Vector2 WorldPos,
        Angle WorldRot,
        Matrix3x2 Matrix,
        Vector2 Anchor,
        Vector2 Axis,
        float Strength,
        float TipStrength);

    private sealed class CasterState
    {
        public uint LastSeenFrame;
        public readonly Dictionary<EntityUid, ShadowState> Shadows = new();
    }

    private sealed class ShadowState
    {
        public Vector2 Direction;
        public float Length;
        public float Current;
        public float Target;
    }

    private sealed class CachedResources : IDisposable
    {
        public IRenderTexture? Scratch;
        public IRenderTexture? Accum;

        public void Dispose()
        {
            Scratch?.Dispose();
            Accum?.Dispose();
            Scratch = null;
            Accum = null;
        }
    }
}
