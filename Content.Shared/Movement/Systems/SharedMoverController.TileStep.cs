// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Movement.Components;
using Content.Trauma.Common.TileMovement;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Shared.Movement.Systems;

public abstract partial class SharedMoverController
{
    [Dependency] private SharedDoorSystem _door = default!;

    private const float StepArriveEpsilon = 0.01f;

    /// <summary>
    /// Returns true if tile movement handled this tick. False means fall through to stock movement.
    /// </summary>
    private bool HandleTileStep(Entity<InputMoverComponent> ent, TileStepComponent step, float frameTime)
    {
        var (uid, mover) = ent;
        var xform = Transform(uid);

        // Off-grid, weightless or no body: let stock movement take over.
        if (frameTime <= 0f
            || !PhysicsQuery.TryGetComponent(uid, out var body)
            || xform.GridUid is not { } gridUid
            || xform.ParentUid != gridUid
            || !MapGridQuery.TryGetComponent(gridUid, out var grid)
            || _gravity.IsWeightless(uid))
        {
            if (step.Moving)
            {
                step.Moving = false;
                step.Returning = false;
                Dirty(uid, step);
            }
            return false;
        }

        var speed = GetEntityMoveSpeed(uid, mover.Sprinting);
        var stepDist = speed * frameTime;
        var input = GetStepInput(mover);

        // Teleported mid-step (portal, admin move, etc.): the old step no longer applies.
        if (step.Moving && (step.Destination - xform.LocalPosition).Length() > grid.TileSize * 2.5f)
        {
            step.Moving = false;
            step.Returning = false;
            Dirty(uid, step);
        }

        if (step.Moving)
        {
            var dist = (step.Destination - xform.LocalPosition).Length();

            // Still holding the same direction on the final tick: extend by one tile
            // so straight-line speed stays constant.
            if (dist <= stepDist && !step.Returning && input != Vector2i.Zero && input == step.Direction)
            {
                var destTile = _mapSystem.TileIndicesFor(gridUid, grid, new EntityCoordinates(gridUid, step.Destination));
                if (PickStep(gridUid, grid, destTile, input, body) == input)
                {
                    BeginStep(uid, step, gridUid, grid, destTile, input, xform.LocalPosition, speed);
                    dist = (step.Destination - xform.LocalPosition).Length();
                }
            }

            if (dist <= StepArriveEpsilon)
            {
                // Arrived. At most a 1 cm alignment, so it is never visible.
                if (dist > 0f)
                    _transform.SetLocalPosition(uid, step.Destination, xform);

                step.Moving = false;
                step.Returning = false;
                Dirty(uid, step);
            }
            else if (CurrentTime > step.Deadline)
            {
                // Something got in the way mid-slide.
                if (!step.Returning)
                {
                    step.Returning = true;
                    step.Destination = step.Origin;
                    step.Deadline = StepDeadline(xform.LocalPosition, step.Origin, speed);
                }
                else
                {
                    step.Moving = false;
                    step.Returning = false;
                }
                Dirty(uid, step);
            }
        }

        if (!step.Moving)
        {
            if (input == Vector2i.Zero)
            {
                PhysicsSystem.SetLinearVelocity(uid, Vector2.Zero, body: body);
                return true;
            }

            var tile = _mapSystem.TileIndicesFor(gridUid, grid, xform.Coordinates);
            var dir = PickStep(gridUid, grid, tile, input, body);

            if (dir == Vector2i.Zero)
            {
                BumpDoors(uid, gridUid, grid, tile + input);
                PhysicsSystem.SetLinearVelocity(uid, Vector2.Zero, body: body);
                return true;
            }

            BeginStep(uid, step, gridUid, grid, tile, dir, xform.LocalPosition, speed);
        }

        // Drive towards the destination, clamping the last tick so we land exactly.
        var remaining = step.Destination - xform.LocalPosition;
        var remainingDist = remaining.Length();
        if (remainingDist <= 0f)
            return true;

        var localVel = remainingDist <= stepDist
            ? remaining / frameTime
            : remaining / remainingDist * speed;

        var worldVel = _transform.GetWorldRotation(gridUid).RotateVec(localVel);
        PhysicsSystem.SetLinearVelocity(uid, worldVel, body: body);
        PhysicsSystem.WakeBody(uid, body: body);

        if (!NoRotateQuery.HasComponent(uid))
            _transform.SetLocalRotation(uid, ((Vector2) step.Direction).ToWorldAngle(), xform);

        return true;
    }

    /// <summary>Held movement keys as a tile offset in grid space. Zero if nothing is held.</summary>
    private Vector2i GetStepInput(InputMoverComponent mover)
    {
        var (walk, sprint) = GetVelocityInput(mover);
        var held = walk + sprint;
        if (held.LengthSquared() < 0.01f)
            return Vector2i.Zero;

        return mover.RelativeRotation.RotateVec(held).GetDir().ToIntVec();
    }

    /// <summary>
    /// Picks the step to take from a tile. Diagonals need both neighbouring cardinals free
    /// (no corner cutting); otherwise it falls back to whichever cardinal is open.
    /// </summary>
    private Vector2i PickStep(EntityUid gridUid, MapGridComponent grid, Vector2i from, Vector2i wanted, PhysicsComponent body)
    {
        bool Free(Vector2i offset) =>
            !IsStepBlocked(gridUid, grid, from + offset, body.CollisionMask);

        if (wanted.X == 0 || wanted.Y == 0)
            return Free(wanted) ? wanted : Vector2i.Zero;

        var x = new Vector2i(wanted.X, 0);
        var y = new Vector2i(0, wanted.Y);
        var xFree = Free(x);
        var yFree = Free(y);

        if (xFree && yFree && Free(wanted))
            return wanted;
        if (xFree)
            return x;
        return yFree ? y : Vector2i.Zero;
    }

    /// <summary>
    /// True if an anchored object that actually collides with us sits on the tile.
    /// </summary>
    private bool IsStepBlocked(EntityUid gridUid, MapGridComponent grid, Vector2i tile, int mask)
    {
        var anchored = _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        while (anchored.MoveNext(out var other))
        {
            // Collision disabled (wall lights, open airlocks, etc.): not an obstacle.
            if (!PhysicsQuery.TryGetComponent(other.Value, out var otherBody) || !otherBody.CanCollide)
                continue;

            if (!TryComp<FixturesComponent>(other.Value, out var fixtures))
                continue;

            foreach (var fixture in fixtures.Fixtures.Values)
            {
                if (fixture.Hard && (fixture.CollisionLayer & mask) != 0)
                    return true;
            }
        }

        return false;
    }

    private void BeginStep(EntityUid uid, TileStepComponent step, EntityUid gridUid, MapGridComponent grid,
        Vector2i fromTile, Vector2i dir, Vector2 currentPos, float speed)
    {
        step.Origin = _mapSystem.GridTileToLocal(gridUid, grid, fromTile).Position;
        step.Destination = _mapSystem.GridTileToLocal(gridUid, grid, fromTile + dir).Position;
        step.Direction = dir;
        step.Moving = true;
        step.Returning = false;
        step.Deadline = StepDeadline(currentPos, step.Destination, speed);
        Dirty(uid, step);
    }

    private TimeSpan StepDeadline(Vector2 from, Vector2 to, float speed)
    {
        var expected = (to - from).Length() / MathF.Max(speed, 0.1f);
        return CurrentTime + TimeSpan.FromSeconds(expected * 1.5f + 0.25f);
    }

    /// <summary>We never touch a blocked tile, so bump-to-open has to be triggered by hand.</summary>
    private void BumpDoors(EntityUid uid, EntityUid gridUid, MapGridComponent grid, Vector2i tile)
    {
        var anchored = _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        while (anchored.MoveNext(out var other))
        {
            if (TryComp<DoorComponent>(other, out var door) && door.BumpOpen)
                _door.TryOpen(other.Value, door, uid, predicted: true);
        }
    }
}
