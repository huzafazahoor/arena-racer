using UnityEngine;

namespace ArenaRacer
{
    /// <summary>Everything about a car that changes from tick to tick. Mirrors NetCar's networked properties.</summary>
    public struct CarState
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public float Heading;      // radians, 0 = +Z, clockwise positive (same as Unity's yaw)
        public float Steer;        // smoothed steering, -1 (left) .. 1 (right)
        public int DriftDir;       // 0 = not drifting, -1 / +1 = drifting left / right
        public float DriftCharge;  // seconds of drift banked towards a mini-turbo
        public float BoostTime;    // seconds of boost left

        public bool Drifting => DriftDir != 0;
    }

    public struct CarInput
    {
        public float Steer;     // -1 .. 1
        public float Throttle;  // 0 .. 1
        public float Brake;     // 0 .. 1 (also reverses once stopped)
        public bool Drift;
    }

    /// <summary>
    /// Arcade car model, stepped once per network tick by whoever has State Authority over the car.
    /// The car turns at a speed-dependent yaw rate; lateral grip then drags the velocity round to match the
    /// heading. Holding drift lowers that grip, so the car slides and banks charge for a mini-turbo.
    /// </summary>
    public static class CarPhysics
    {
        public const float TopSpeed = 31f;          // m/s (~112 km/h)
        public const float BoostTopSpeed = 39f;
        const float Acceleration = 16f;
        const float BoostAcceleration = 30f;
        const float BrakeDeceleration = 32f;
        const float ReverseSpeed = 9f;
        const float ReverseAcceleration = 10f;
        const float Coast = 4f;                    // engine braking with no pedal pressed
        const float OverspeedDecay = 5f;           // bleeding off boost speed afterwards
        const float SteerResponse = 5.5f;          // how fast the wheel turns, full lock per second
        const float MaxYawRate = 2.5f;             // rad/s at moderate speed
        const float HighSpeedYawFactor = 0.6f;     // steering gets calmer towards top speed
        const float Grip = 9f;
        const float DriftGrip = 1.7f;
        const float DriftYawBoost = 1.25f;
        const float DriftMinSpeed = 12f;
        const float DriftChargeSmall = 0.8f, DriftChargeBig = 1.8f;
        const float BoostSmall = 0.55f, BoostBig = 1.15f;
        const float WallRestitution = 0.25f;
        const float CarRestitution = 0.4f;

        // Collision shape: two circles, over the front and rear axles.
        public const float CircleRadius = 1.0f;
        public const float CircleOffset = 1.05f;

        public static Vector2 Forward(float heading) => new(Mathf.Sin(heading), Mathf.Cos(heading));
        public static Vector2 Right(float heading) => new(Mathf.Cos(heading), -Mathf.Sin(heading));

        public static float ForwardSpeed(in CarState s) => Vector2.Dot(s.Velocity, Forward(s.Heading));

        public static void Step(ref CarState s, in CarInput input, float dt)
        {
            s.Steer = Mathf.MoveTowards(s.Steer, Mathf.Clamp(input.Steer, -1f, 1f), SteerResponse * dt);
            float speed = ForwardSpeed(s);
            UpdateDrift(ref s, input, speed, dt);

            // 1. Rotate the car.
            float steer = s.Drifting
                ? s.DriftDir * Mathf.Lerp(0.3f, 1f, (s.Steer * s.DriftDir + 1f) * 0.5f)  // counter-steer widens the arc
                : s.Steer;
            float yawRate = steer * MaxYawRate * YawFactor(Mathf.Abs(speed)) * (s.Drifting ? DriftYawBoost : 1f);
            if (speed < 0f) yawRate = -yawRate;  // reversing steers the other way, like a real car
            s.Heading = Mathf.Repeat(s.Heading + yawRate * dt + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;

            // 2. Split the velocity into the car's new frame: whatever points sideways is now slip.
            Vector2 forward = Forward(s.Heading), right = Right(s.Heading);
            float vForward = Vector2.Dot(s.Velocity, forward);
            float vSide = Vector2.Dot(s.Velocity, right);

            // 3. Engine, boost and brakes act along the car.
            bool boosting = s.BoostTime > 0f;
            float top = boosting ? BoostTopSpeed : TopSpeed;
            float throttle = boosting ? 1f : input.Throttle;
            if (throttle > 0f)
            {
                float ratio = Mathf.Clamp01(vForward / top);
                vForward += throttle * (boosting ? BoostAcceleration : Acceleration) * (1f - ratio * ratio) * dt;
            }
            if (input.Brake > 0f && !boosting)
            {
                if (vForward > 0.5f) vForward = Mathf.MoveTowards(vForward, 0f, BrakeDeceleration * input.Brake * dt);
                else vForward = Mathf.MoveTowards(vForward, -ReverseSpeed, ReverseAcceleration * input.Brake * dt);
            }
            if (throttle <= 0f && input.Brake <= 0f) vForward = Mathf.MoveTowards(vForward, 0f, Coast * dt);
            if (vForward > top) vForward = Mathf.MoveTowards(vForward, top, OverspeedDecay * dt);
            if (s.Drifting) vForward -= vForward * 0.06f * dt;
            s.BoostTime = Mathf.Max(0f, s.BoostTime - dt);

            // 4. Tyres kill sideways slip; much less so while drifting.
            vSide *= Mathf.Exp(-(s.Drifting ? DriftGrip : Grip) * dt);

            s.Velocity = forward * vForward + right * vSide;
            s.Position += s.Velocity * dt;
        }

        static void UpdateDrift(ref CarState s, in CarInput input, float speed, float dt)
        {
            if (!s.Drifting)
            {
                if (input.Drift && Mathf.Abs(input.Steer) > 0.3f && speed > DriftMinSpeed)
                {
                    s.DriftDir = input.Steer > 0f ? 1 : -1;
                    s.DriftCharge = 0f;
                }
                return;
            }

            if (speed < DriftMinSpeed * 0.6f)
            {
                s.DriftDir = 0;     // scrubbed off too much speed: drift lost, no reward
                s.DriftCharge = 0f;
                return;
            }

            if (!input.Drift)
            {
                if (s.DriftCharge >= DriftChargeBig) s.BoostTime = BoostBig;
                else if (s.DriftCharge >= DriftChargeSmall) s.BoostTime = BoostSmall;
                s.DriftDir = 0;
                s.DriftCharge = 0f;
                return;
            }

            float intoTheDrift = (s.Steer * s.DriftDir + 1f) * 0.5f;  // 0 = full counter-steer, 1 = full lock into it
            s.DriftCharge += dt * (0.5f + intoTheDrift);
        }

        /// <summary>0 when stopped, 1 around 10 m/s, easing to HighSpeedYawFactor at top speed.</summary>
        public static float YawFactor(float speed)
        {
            float lowSpeed = Mathf.Clamp01(speed / 6f);
            float highSpeed = Mathf.Lerp(1f, HighSpeedYawFactor, Mathf.Clamp01((speed - 10f) / (TopSpeed - 10f)));
            return lowSpeed * highSpeed;
        }

        /// <summary>Smallest radius the car can hold at this speed without drifting (used to plan bot speeds).</summary>
        public static float TightestRadius(float speed) => speed / (MaxYawRate * Mathf.Max(0.05f, YawFactor(speed)));

        public static int DriftLevel(float charge) => charge >= DriftChargeBig ? 2 : charge >= DriftChargeSmall ? 1 : 0;

        /// <summary>Keeps both collision circles inside the barriers. Returns how hard the car hit (m/s into the wall).</summary>
        public static float CollideWithWalls(ref CarState s, TrackPath track, int hint)
        {
            float impact = 0f;
            float limit = ArenaCircuit.WallInner - CircleRadius;
            for (int end = -1; end <= 1; end += 2)
            {
                Vector2 circle = s.Position + Forward(s.Heading) * (CircleOffset * end);
                TrackPath.Location at = track.Locate(circle, hint);
                float excess = Mathf.Abs(at.Lateral) - limit;
                if (excess <= 0f) continue;

                Vector2 outward = at.Normal * Mathf.Sign(at.Lateral);
                s.Position -= outward * excess;

                float into = Vector2.Dot(s.Velocity, outward);
                if (into > 0f)
                {
                    s.Velocity -= outward * (into * (1f + WallRestitution));
                    s.Velocity *= 1f - Mathf.Clamp01(into * 0.025f);  // harder hits scrub more speed
                    impact = Mathf.Max(impact, into);
                    if (into > 8f) s.DriftDir = 0;
                }

                // Swing the car round to slide along the barrier instead of sticking to it nose-first.
                float along = Mathf.Atan2(at.Tangent.x, at.Tangent.y);
                if (Vector2.Dot(Forward(s.Heading), at.Tangent) < 0f) along += Mathf.PI;
                s.Heading = Mathf.LerpAngle(s.Heading * Mathf.Rad2Deg, along * Mathf.Rad2Deg, 0.08f) * Mathf.Deg2Rad;
            }
            return impact;
        }

        /// <summary>
        /// Pushes this car out of another one. Each car only ever moves itself (it may not own the other one),
        /// so it takes half the separation and half the impulse; the other car's owner does the same.
        /// Returns the closing speed, or 0 if they don't touch.
        /// </summary>
        public static float CollideWithCar(ref CarState s, Vector2 otherPosition, float otherHeading, Vector2 otherVelocity)
        {
            if ((otherPosition - s.Position).sqrMagnitude > 36f) return 0f;

            Vector2 mine = Forward(s.Heading) * CircleOffset, theirs = Forward(otherHeading) * CircleOffset;
            float deepest = 0f;
            Vector2 normal = Vector2.zero;
            for (int i = -1; i <= 1; i += 2)
            for (int j = -1; j <= 1; j += 2)
            {
                Vector2 apart = (s.Position + mine * i) - (otherPosition + theirs * j);
                float distance = apart.magnitude;
                float penetration = 2f * CircleRadius - distance;
                if (penetration > deepest)
                {
                    deepest = penetration;
                    normal = distance > 1e-4f ? apart / distance : Right(s.Heading);
                }
            }
            if (deepest <= 0f) return 0f;

            s.Position += normal * (deepest * 0.5f);
            float closing = Vector2.Dot(s.Velocity - otherVelocity, normal);
            if (closing >= 0f) return 0.01f;

            s.Velocity -= normal * (closing * (1f + CarRestitution) * 0.5f);
            return -closing;
        }
    }
}
