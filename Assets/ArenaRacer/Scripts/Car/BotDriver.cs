using System.Collections.Generic;
using UnityEngine;

namespace ArenaRacer
{
    public readonly struct BotProfile
    {
        public readonly string Name;
        public readonly float Pace;      // multiplier on the racing line's speed limits
        public readonly float LaneBias;  // metres; spreads the pack so bots don't drive in single file
        public readonly bool Drifts;     // uses drift + mini-turbo through the slowest corners

        public BotProfile(string name, float pace, float laneBias, bool drifts)
        {
            Name = name;
            Pace = pace;
            LaneBias = laneBias;
            Drifts = drifts;
        }
    }

    /// <summary>
    /// Produces a CarInput for a car nobody is driving: a bot, a paused player on autopilot, or a car doing its
    /// cool-down lap. It runs on whichever client has State Authority over that car, so its state is local and
    /// simply starts fresh if authority moves to another client.
    /// </summary>
    public sealed class BotDriver
    {
        public static readonly BotProfile[] Roster =
        {
            new("Kai", 1.000f, 0.0f, true),
            new("Mira", 0.985f, 0.8f, false),
            new("Jett", 0.970f, -0.8f, true),
            new("Rosa", 0.955f, 1.2f, true),
            new("Bo", 0.940f, -1.2f, false),
            new("Ivy", 0.925f, 0.4f, false),
        };

        const float PassingGap = 3.4f;     // lateral metres to leave when overtaking
        const float LookForCarsAhead = 16f;

        readonly BotProfile _profile;
        float _avoidOffset;
        float _stuckTime;
        float _reverseTime;
        bool _drifting;

        public BotDriver(BotProfile profile) => _profile = profile;

        /// <param name="pace">Speed multiplier for this tick (profile pace plus rubber-banding, or a slow cool-down pace).</param>
        /// <param name="wantsReset">True when the bot has been stuck long enough to ask for a reset onto the track.</param>
        public CarInput Think(in CarState car, int trackIndex, float progress, float pace,
            IReadOnlyList<NetCar> cars, NetCar self, float dt, out bool wantsReset)
        {
            TrackPath track = ArenaCircuit.Path;
            RacingLine line = ArenaCircuit.RacingLine;
            float speed = CarPhysics.ForwardSpeed(car);
            float limit = track.HalfWidth - 1.2f;

            // Stuck against something? Back off with opposite lock, and ask for a reset if that doesn't help.
            _stuckTime = Mathf.Abs(speed) < 1.5f ? _stuckTime + dt : Mathf.Max(0f, _stuckTime - dt * 0.5f);
            wantsReset = _stuckTime > 5f;
            if (_stuckTime > 1.2f && _reverseTime <= 0f) _reverseTime = 1.1f;

            // 1. Aim at a point on the racing line, further ahead the faster we go (pure pursuit).
            float lookAhead = 6f + Mathf.Max(0f, speed) * 0.45f;
            int aim = track.Wrap(trackIndex + Mathf.RoundToInt(lookAhead / track.Spacing));
            float targetSpeed = line.MaxSpeed[track.Wrap(trackIndex + Mathf.RoundToInt((2f + speed * 0.2f) / track.Spacing))] * pace;

            float avoidTarget = 0f;
            float myLateral = Vector2.Dot(car.Position - track.Points[trackIndex], track.Normals[trackIndex]);
            foreach (NetCar other in cars)
            {
                if (other == self) continue;
                float ahead = WrapGap(other.Progress - progress, track.Length);
                if (ahead <= 0f || ahead > LookForCarsAhead) continue;

                int otherIndex = track.IndexAt(ArenaCircuit.ToTrackDistance(other.Progress));
                float otherLateral = Vector2.Dot(other.Position - track.Points[otherIndex], track.Normals[otherIndex]);
                if (Mathf.Abs(otherLateral - myLateral) > PassingGap) continue;

                float otherSpeed = Vector2.Dot(other.Velocity, track.Tangents[otherIndex]);
                if (speed > otherSpeed + 0.5f)
                {
                    // Pass on whichever side of them has more room.
                    float passAt = otherLateral + (otherLateral > 0f ? -PassingGap : PassingGap);
                    avoidTarget = Mathf.Clamp(passAt, -limit, limit) - line.Offset[aim];
                }
                if (ahead < 7f && Mathf.Abs(otherLateral - myLateral) < 2.2f)
                    targetSpeed = Mathf.Min(targetSpeed, otherSpeed);
            }
            _avoidOffset = Mathf.MoveTowards(_avoidOffset, avoidTarget, 5f * dt);

            float offset = Mathf.Clamp(line.Offset[aim] + _profile.LaneBias + _avoidOffset, -limit, limit);
            Vector2 target = track.Points[aim] + track.Normals[aim] * offset;
            float angle = Vector2.SignedAngle(CarPhysics.Forward(car.Heading), target - car.Position);  // + = target is to the left
            float steer = Mathf.Clamp(-angle / 22f, -1f, 1f);

            if (_reverseTime > 0f)
            {
                _reverseTime -= dt;
                return new CarInput { Steer = -steer, Brake = 1f };
            }

            // 2. Pedals: bang-bang around the planned speed is plenty, the car model smooths it.
            var input = new CarInput { Steer = steer };
            if (speed < targetSpeed) input.Throttle = 1f;
            else if (speed > targetSpeed + 1.5f) input.Brake = Mathf.Clamp01((speed - targetSpeed) / 6f);

            // 3. Drift the slowest corners for a mini-turbo on the way out.
            if (_profile.Drifts)
            {
                float tightness = line.Curvature[aim];
                if (!_drifting && tightness > 1f / 20f && speed > 15f && Mathf.Abs(steer) > 0.35f) _drifting = true;
                else if (_drifting && (tightness < 1f / 34f || speed < 9f)) _drifting = false;
                input.Drift = _drifting;
                if (_drifting) input.Throttle = 1f;
            }

            return input;
        }

        /// <summary>Signed distance from b to a along a loop, in (-length/2, length/2].</summary>
        static float WrapGap(float gap, float length)
        {
            gap %= length;
            if (gap > length * 0.5f) gap -= length;
            else if (gap <= -length * 0.5f) gap += length;
            return gap;
        }
    }
}
