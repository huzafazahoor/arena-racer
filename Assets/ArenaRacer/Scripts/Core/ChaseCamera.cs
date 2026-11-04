using UnityEngine;

namespace ArenaRacer
{
    /// <summary>
    /// Follows your car from behind and above, lagging a little in yaw so drifts read as drifts. Before you join
    /// it slowly orbits the arena; with no car of your own it follows the race leader.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class ChaseCamera : MonoBehaviour
    {
        [SerializeField] float distance = 8.5f;
        [SerializeField] float height = 3.4f;
        [SerializeField] float baseFov = 62f;
        [SerializeField] float speedFov = 12f;

        Camera _camera;
        Vector3 _flatForward = Vector3.forward;
        float _shake;
        int _lastBumps = -1;
        NetCar _following;
        float _orbitAngle = 210f;
        float _devBlend;  // 0 = normal chase cam, 1 = higher Dev View cam that shows the ghosts behind you

        void Awake() => _camera = GetComponent<Camera>();

        public void Shake(float amount) => _shake = Mathf.Max(_shake, amount);

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            NetCar target = PickTarget();
            if (target == null)
            {
                Orbit(dt);
                return;
            }

            if (target != _following)
            {
                _following = target;
                _flatForward = target.transform.forward;
                _lastBumps = target.BumpCount;
                SnapBehind(target);
            }
            if (target.BumpCount != _lastBumps)
            {
                _lastBumps = target.BumpCount;
                Shake(0.35f);
            }

            Vector3 carPosition = target.transform.position;
            float speed = target.Velocity.magnitude;
            Vector3 carForward = target.transform.forward;
            _flatForward = Vector3.Slerp(_flatForward, carForward, 1f - Mathf.Exp(-5f * dt)).normalized;

            _devBlend = Mathf.MoveTowards(_devBlend, DevView.Enabled ? 1f : 0f, dt * 1.5f);
            float back = Mathf.Lerp(distance, 15f, _devBlend) + speed * 0.05f;
            float up = Mathf.Lerp(height, 10f, _devBlend) + speed * 0.02f;
            Vector3 desired = carPosition - _flatForward * back + Vector3.up * up;
            transform.position = Vector3.Lerp(transform.position, desired, 1f - Mathf.Exp(-14f * dt));

            Vector3 lookAt = carPosition + _flatForward * 5f + Vector3.up * 0.9f;
            transform.rotation = Quaternion.LookRotation(lookAt - transform.position, Vector3.up);

            float boostKick = target.BoostTime > 0f ? 6f : 0f;
            float fov = baseFov + speedFov * Mathf.Clamp01(speed / CarPhysics.BoostTopSpeed) + boostKick;
            _camera.fieldOfView = Mathf.Lerp(_camera.fieldOfView, fov, 1f - Mathf.Exp(-4f * dt));

            if (_shake > 0f)
            {
                transform.position += Random.insideUnitSphere * (_shake * 0.25f);
                _shake = Mathf.MoveTowards(_shake, 0f, dt * 1.5f);
            }
        }

        NetCar PickTarget()
        {
            GameSession session = GameSession.Instance;
            if (session == null || !session.WantsToRace) return null;
            NetCar mine = session.LocalCar;
            if (mine != null) return mine;
            RaceDirector race = RaceDirector.Instance;
            if (race == null || NetCar.All.Count == 0) return null;
            NetCar leader = null;
            foreach (NetCar car in NetCar.All)
                if (leader == null || car.Progress > leader.Progress) leader = car;
            return leader;
        }

        void SnapBehind(NetCar target)
        {
            Vector3 p = target.transform.position;
            transform.position = p - _flatForward * distance + Vector3.up * height;
            transform.rotation = Quaternion.LookRotation(p + _flatForward * 5f - transform.position, Vector3.up);
        }

        void Orbit(float dt)
        {
            _following = null;
            _orbitAngle += dt * 4f;
            Vector3 centre = new(-15f, 0f, -5f);
            Vector3 offset = Quaternion.Euler(0f, _orbitAngle, 0f) * new Vector3(0f, 0f, -175f);
            transform.position = centre + offset + Vector3.up * 85f;
            transform.rotation = Quaternion.LookRotation(centre - transform.position, Vector3.up);
            _camera.fieldOfView = 50f;
        }
    }
}
