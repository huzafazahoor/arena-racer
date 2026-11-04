using UnityEngine;
using UnityEngine.Rendering;

namespace ArenaRacer
{
    /// <summary>
    /// Everything you see and hear of a car, driven purely from NetCar's interpolated render state, so it
    /// looks the same whether this client simulates the car or only receives snapshots of it.
    /// </summary>
    [RequireComponent(typeof(NetCar))]
    public sealed class CarView : MonoBehaviour
    {
        NetCar _car;
        Transform _body;
        Transform[] _wheels;
        Transform[] _frontPivots;
        Renderer _brakeLights;
        Transform[] _flames;
        ParticleSystem _smoke;
        ParticleSystem _driftSparks;
        ParticleSystem _impactSparks;
        AudioSource _engine;
        AudioSource _skid;
        AudioSource _oneShots;
        SkidMarks.Trail _leftTrail, _rightTrail;

        Vector2 _lastPosition;
        Vector2 _velocity;
        float _lastForwardSpeed;
        float _wheelSpin;
        float _roll, _pitch;
        int _lastBumps;
        bool _wasBoosting;

        static readonly Color SmokeColour = new(0.85f, 0.86f, 0.88f, 0.55f);
        static readonly Color SparkSmall = Palette.Hex(0x5CC8FF);
        static readonly Color SparkBig = Palette.Hex(0xFF9A3C);

        public void Bind(NetCar car)
        {
            _car = car;
            GameAssets assets = GameAssets.Instance;

            _body = Part("Body", transform, CarMeshFactory.Body(car.Color), assets.Lit, Vector3.zero).transform;

            _wheels = new Transform[4];
            _frontPivots = new Transform[2];
            for (int i = 0; i < 4; i++)
            {
                var pivot = new GameObject(i < 2 ? "Steer Pivot" : "Axle").transform;
                pivot.SetParent(transform, false);
                pivot.localPosition = CarMeshFactory.WheelPositions[i];
                if (i < 2) _frontPivots[i] = pivot;
                _wheels[i] = Part("Wheel", pivot, CarMeshFactory.Wheel(), assets.Lit, Vector3.zero).transform;
            }

            _brakeLights = Part("Brake Lights", _body, CarMeshFactory.BrakeLights(), assets.Lit, Vector3.zero).GetComponent<Renderer>();
            _brakeLights.enabled = false;

            _flames = new Transform[2];
            for (int i = 0; i < 2; i++)
            {
                var flame = Part("Flame", _body, CarMeshFactory.Flame(), assets.Flame, new Vector3(i == 0 ? -0.4f : 0.4f, 0.36f, -2.1f));
                flame.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
                flame.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
                flame.SetActive(false);
                _flames[i] = flame.transform;
            }

            _smoke = CreateParticles("Tyre Smoke", assets.Smoke, new Vector3(0f, 0.3f, -1.3f), 1.1f, 1.6f, 2.4f);
            _driftSparks = CreateParticles("Drift Sparks", assets.Glow, new Vector3(0f, 0.15f, -1.4f), 0.25f, 0.14f, 6f);
            _impactSparks = CreateParticles("Impact Sparks", assets.Glow, new Vector3(0f, 0.5f, 1.6f), 0.35f, 0.12f, 9f);

            _engine = CreateLoop(assets.Engine, 0.5f);
            _skid = CreateLoop(assets.Skid, 0f);
            _oneShots = CreateLoop(null, 1f);

            _lastPosition = car.RenderPosition;
            _lastBumps = car.BumpCount;
        }

        void LateUpdate()
        {
            if (_car == null || !_car.Object || !_car.Object.IsValid) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // Velocity from what we actually draw, so remote cars animate as smoothly as they move.
            Vector2 position = _car.RenderPosition;
            Vector2 measured = (position - _lastPosition) / dt;
            _lastPosition = position;
            if (measured.sqrMagnitude > 60f * 60f) measured = _car.Velocity;  // teleported
            _velocity = Vector2.Lerp(_velocity, measured, 1f - Mathf.Exp(-14f * dt));

            Vector2 forward = CarPhysics.Forward(_car.RenderHeading), right = CarPhysics.Right(_car.RenderHeading);
            float speed = Vector2.Dot(_velocity, forward);
            float slide = Vector2.Dot(_velocity, right);
            float acceleration = (speed - _lastForwardSpeed) / dt;
            _lastForwardSpeed = speed;

            AnimateWheelsAndBody(speed, slide, acceleration, dt);
            UpdateEffects(speed, slide, acceleration, right);
            UpdateAudio(speed, slide);

            if (_car.BumpCount != _lastBumps)
            {
                _lastBumps = _car.BumpCount;
                _impactSparks.Emit(18);
                if (GameAssets.Instance.Bump) _oneShots.PlayOneShot(GameAssets.Instance.Bump, 0.9f);
            }
        }

        void AnimateWheelsAndBody(float speed, float slide, float acceleration, float dt)
        {
            _wheelSpin = Mathf.Repeat(_wheelSpin + speed * dt / CarMeshFactory.WheelRadius * Mathf.Rad2Deg, 360f);
            foreach (Transform wheel in _wheels) wheel.localRotation = Quaternion.Euler(_wheelSpin, 0f, 0f);
            float steerAngle = (_car.DriftDir != 0 ? -_car.DriftDir * 0.6f : _car.Steer) * 28f;
            foreach (Transform pivot in _frontPivots) pivot.localRotation = Quaternion.Euler(0f, steerAngle, 0f);

            float targetRoll = Mathf.Clamp(slide * 0.9f + _car.Steer * Mathf.Abs(speed) * 0.08f, -6f, 6f);
            float targetPitch = Mathf.Clamp(-acceleration * 0.12f, -3f, 3f);
            _roll = Mathf.Lerp(_roll, targetRoll, 1f - Mathf.Exp(-8f * dt));
            _pitch = Mathf.Lerp(_pitch, targetPitch, 1f - Mathf.Exp(-6f * dt));
            _body.localRotation = Quaternion.Euler(_pitch, 0f, _roll);
        }

        void UpdateEffects(float speed, float slide, float acceleration, Vector2 right)
        {
            _brakeLights.enabled = acceleration < -6f && speed > 2f;

            bool boosting = _car.BoostTime > 0f;
            foreach (Transform flame in _flames)
            {
                flame.gameObject.SetActive(boosting);
                if (boosting) flame.localScale = new Vector3(1f, 0.9f + Random.value * 0.7f, 1f);
            }
            if (boosting && !_wasBoosting && GameAssets.Instance.Boost) _oneShots.PlayOneShot(GameAssets.Instance.Boost, 0.8f);
            _wasBoosting = boosting;

            bool drifting = _car.DriftDir != 0;
            bool sliding = drifting || Mathf.Abs(slide) > 3.5f;
            SetRate(_smoke, sliding && Mathf.Abs(speed) > 5f ? 45f : 0f);

            int level = CarPhysics.DriftLevel(_car.DriftCharge);
            SetRate(_driftSparks, drifting && level > 0 ? 60f : 0f);
            if (level > 0)
            {
                ParticleSystem.MainModule main = _driftSparks.main;
                main.startColor = level == 2 ? SparkBig : SparkSmall;
            }

            Vector3 right3 = new(right.x, 0f, right.y);
            if (sliding && Mathf.Abs(speed) > 4f)
            {
                SkidMarks.Extend(ref _leftTrail, transform.TransformPoint(CarMeshFactory.WheelPositions[2]), right3, 0.32f, 120);
                SkidMarks.Extend(ref _rightTrail, transform.TransformPoint(CarMeshFactory.WheelPositions[3]), right3, 0.32f, 120);
            }
            else
            {
                SkidMarks.Break(ref _leftTrail);
                SkidMarks.Break(ref _rightTrail);
            }
        }

        void UpdateAudio(float speed, float slide)
        {
            bool mine = _car.IsLocalPlayer;
            _engine.spatialBlend = mine ? 0.2f : 1f;
            _skid.spatialBlend = mine ? 0.2f : 1f;

            float rev = Mathf.Abs(speed) / CarPhysics.TopSpeed;
            // A fake gearbox: pitch climbs, drops a little at each "shift", and boost adds a whine.
            float gear = Mathf.Repeat(rev * 3.2f, 1f);
            float pitch = 0.55f + rev * 0.9f + gear * 0.35f + (_car.BoostTime > 0f ? 0.25f : 0f);
            _engine.pitch = Mathf.Lerp(_engine.pitch, pitch, 0.25f);
            _engine.volume = (mine ? 0.45f : 0.6f) * (0.55f + rev * 0.45f);

            float skidTarget = (_car.DriftDir != 0 || Mathf.Abs(slide) > 3.5f) && Mathf.Abs(speed) > 5f ? 0.4f : 0f;
            _skid.volume = Mathf.MoveTowards(_skid.volume, skidTarget, Time.deltaTime * 3f);
        }

        static void SetRate(ParticleSystem system, float rate)
        {
            ParticleSystem.EmissionModule emission = system.emission;
            emission.rateOverTime = rate;
        }

        static GameObject Part(string partName, Transform parent, Mesh mesh, Material material, Vector3 localPosition)
        {
            var go = new GameObject(partName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        ParticleSystem CreateParticles(string systemName, Material material, Vector3 localPosition, float lifetime, float size, float speed)
        {
            var go = new GameObject(systemName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPosition;
            var system = go.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = system.main;
            main.loop = true;
            main.duration = 1f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime * 0.6f, lifetime);
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.6f, size);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.4f, speed);
            main.startColor = SmokeColour;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 200;
            main.gravityModifier = material == GameAssets.Instance.Glow ? 1.2f : -0.05f;

            ParticleSystem.EmissionModule emission = system.emission;
            emission.rateOverTime = 0f;

            ParticleSystem.ShapeModule shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(1.7f, 0.1f, 0.3f);

            ParticleSystem.SizeOverLifetimeModule growth = system.sizeOverLifetime;
            growth.enabled = true;
            growth.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, material == GameAssets.Instance.Glow ? 0.2f : 2.4f));

            ParticleSystem.ColorOverLifetimeModule fade = system.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            fade.color = gradient;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            system.Play();
            return system;
        }

        AudioSource CreateLoop(AudioClip clip, float volume)
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = clip != null;
            source.volume = volume;
            source.spatialBlend = 1f;
            source.minDistance = 6f;
            source.maxDistance = 90f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.dopplerLevel = 0f;
            source.playOnAwake = false;
            if (clip != null)
            {
                source.time = Random.value * clip.length * 0.9f;  // de-sync the six engines
                source.Play();
            }
            return source;
        }
    }
}
