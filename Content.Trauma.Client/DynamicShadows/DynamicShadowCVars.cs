// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Configuration;

namespace Content.Trauma.Client.DynamicShadows;

[CVarDefs]
public sealed class DynamicShadowCVars
{
    public static readonly CVarDef<bool> Enabled =
        CVarDef.Create("trauma.dynamic_shadows", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>Global darkness multiplier applied when compositing.</summary>
    public static readonly CVarDef<float> Opacity =
        CVarDef.Create("trauma.dynamic_shadows_opacity", 1f, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>Blur radius for soft edges. 0 = crisp pixel shadows.</summary>
    public static readonly CVarDef<float> Blur =
        CVarDef.Create("trauma.dynamic_shadows_blur", 1f, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>Raycast lights against walls so lights in other rooms don't cast shadows.</summary>
    public static readonly CVarDef<bool> Occlusion =
        CVarDef.Create("trauma.dynamic_shadows_occlusion", true, CVar.CLIENTONLY | CVar.ARCHIVE);
}
