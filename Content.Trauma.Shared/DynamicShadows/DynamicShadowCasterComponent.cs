// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Trauma.Shared.DynamicShadows;

[RegisterComponent]
public sealed partial class DynamicShadowCasterComponent : Component
{
    /// <summary>Darkness of this entity's lamp shadows when it's lit by a single light.</summary>
    [DataField]
    public float Opacity = 0.6f;

    /// <summary>How many lamps can cast a shadow from this entity at once (max 3).</summary>
    [DataField]
    public int MaxShadows = 3;

    [DataField]
    public float MinLength = 0.6f;

    [DataField]
    public float MaxLength = 2.5f;

    [DataField]
    public float LengthPerTile = 0.25f;

    [DataField]
    public float Width = 0.9f;

    [DataField]
    public float FootOffset = -0.4f;

    /// <summary>Lamp shadow darkness at the tip relative to the feet (1 = no fade).</summary>
    [DataField]
    public float TipStrength = 0.45f;

    /// <summary>Cast a shadow into the sun mask, matching wall shadows.</summary>
    [DataField]
    public bool UseSun = true;

    /// <summary>
    /// Sun shadow length relative to a 1-tile-tall wall's shadow. 1 = same as walls.
    /// Lower for short things (crates), higher for tall things (trees).
    /// </summary>
    [DataField]
    public float SunHeight = 1f;
}
