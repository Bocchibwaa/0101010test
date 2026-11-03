// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Client.Light;
using Robust.Shared.Configuration;

namespace Content.Trauma.Client.DynamicShadows;

public sealed partial class DynamicShadowSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IOverlayManager _overlay = default!;

    private DynamicShadowOverlay _shadowOverlay = default!;
    private SunShadowOverlay? _sunOverlay;

    public override void Initialize()
    {
        base.Initialize();

        _shadowOverlay = new();

        Subs.CVar(_cfg, DynamicShadowCVars.Opacity, v => _shadowOverlay.Opacity = v, true);
        Subs.CVar(_cfg, DynamicShadowCVars.Blur, v => _shadowOverlay.BlurRadius = v, true);
        Subs.CVar(_cfg, DynamicShadowCVars.Occlusion, v => _shadowOverlay.UseOcclusion = v, true);
        Subs.CVar(_cfg, DynamicShadowCVars.Enabled, SetEnabled, true);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        // The sun overlay is added by another system (and can be re-created), so connect lazily
        // and reconnect if the instance ever changes.
        _overlay.TryGetOverlay<SunShadowOverlay>(out var sun);
        if (sun == _sunOverlay)
            return;

        if (_sunOverlay != null)
            _sunOverlay.ExtraShadows -= _shadowOverlay.DrawSunShadows;

        _sunOverlay = sun;

        if (_sunOverlay != null)
            _sunOverlay.ExtraShadows += _shadowOverlay.DrawSunShadows;
    }

    public override void Shutdown()
    {
        base.Shutdown();

        if (_sunOverlay != null)
            _sunOverlay.ExtraShadows -= _shadowOverlay.DrawSunShadows;

        _overlay.RemoveOverlay(_shadowOverlay);
    }

    private void SetEnabled(bool enabled)
    {
        _shadowOverlay.SunEnabled = enabled;

        if (enabled)
            _overlay.AddOverlay(_shadowOverlay);
        else
            _overlay.RemoveOverlay(_shadowOverlay);
    }
}
