// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;
using Robust.Shared.GameStates;

namespace Content.Trauma.Common.TileMovement;

/// <summary>
/// Tile-stepped movement: the mob always travels from one tile centre to another.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class TileStepComponent : Component
{
    [AutoNetworkedField] public bool Moving;

    /// <summary>True if the step was blocked and we are gliding back to Origin.</summary>
    [AutoNetworkedField] public bool Returning;

    /// <summary>Grid-local centre of the tile the step started from.</summary>
    [AutoNetworkedField] public Vector2 Origin;

    /// <summary>Grid-local centre of the tile being moved to.</summary>
    [AutoNetworkedField] public Vector2 Destination;

    /// <summary>Tile offset of the current step, e.g. (1, 0).</summary>
    [AutoNetworkedField] public Vector2i Direction;

    /// <summary>If the step isn't finished by this time, treat it as blocked.</summary>
    [AutoNetworkedField] public TimeSpan Deadline;
}
