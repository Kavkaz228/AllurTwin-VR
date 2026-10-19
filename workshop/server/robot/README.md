# Server robot engine

This module runs the complete six-axis kinematic simulation on the Node.js server using only built-in modules. The headset sends a target pose; it does not solve inverse kinematics or decide which trajectory is accepted.

`training-arm.json` is a fictional training manipulator. Its dimensions, joint limits and speed limits must not be treated as calibration data for a commercial robot.

```js
import { KinematicRobot, defaultRobotConfig } from './index.js';

const robot = new KinematicRobot(defaultRobotConfig);
const initial = robot.snapshot();
robot.setDeadman(true);
const result = robot.command({
  position: { ...initial.tool.position, y: initial.tool.position.y + 0.02 },
  orientation: initial.tool.orientation
}, 0.5);
if (result.ok) robot.tick(0.02);
console.log(robot.snapshot());
robot.setDeadman(false);
```

Coordinates are right-handed with Y up, positions are metres, joints are radians, and orientations are quaternions `[x,y,z,w]`. Unity needs to reflect Z and transform the quaternion consistently when crossing its left-handed world frame. Each configured joint offset is applied in the preceding joint frame, then its local revolute axis is rotated. The tool offset follows the last joint. `links` contains the base, six joint origins and TCP; the first segment has zero length in the default model.

The inverse-kinematics solver uses a geometric Jacobian, damped least squares, bounded iterations, limited angular steps and a short line search. It solves position and orientation together. Joint bounds are enforced at every iteration. A failed solution is rejected without moving the simulated robot. Numerical IK can reject a reachable target near singularities or when a different configuration branch is required; it never accepts an unconverged solution. Position/orientation tolerances and the iteration budget are configurable.

Every accepted target is checked along a straight path in joint space. All links and the tool are checked against the configured workspace and obstacle boxes. Segment/AABB tests include each link radius. Interpolated samples add a conservative motion margin covering the space between samples; this can reject a path close to an obstacle. Execution advances every joint with the same fraction so it stays on the checked path, while respecting each joint's maximum speed. Each simulation tick repeats the collision check for its actual increment. Gripper motion also has a speed limit.

The deadman starts released. Releasing it cancels the target immediately; pressing it again alone cannot restart motion. `stop(reason)` latches a fault by default, clears the target and releases the deadman. `reset()` requires the deadman to be released and the current pose to pass collision checks. After reset, a new enabled command is required. An invalid or stale timestep, malformed target, unreachable target or collision also latches a fault.

The enclosing authenticated session runtime owns the operator lease, sequence numbers, command timeout and client telemetry acknowledgements. It must call `stop(reason)` when any of these fail. The simulator does not create a substitute for hardware feedback.

These are simulated geometric checks. This module does not model link mesh geometry, self collision, payloads, forces, torques, braking distance or controller dynamics. Physical robot execution needs a separately validated adapter and the robot's own safety controller.

Run the focused verification with `node --test test/robot.test.js` from the project root. It exercises known forward poses, full 6DoF inverse kinematics, unreachable targets, joint bounds and speed limits, whole-link and swept-path collision checks, deadman behavior, fault reset and invalid data/timing.
