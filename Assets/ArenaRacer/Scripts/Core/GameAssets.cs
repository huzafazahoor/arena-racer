using UnityEngine;

namespace ArenaRacer
{
    /// <summary>Scene-wide references to the few materials and sounds the game uses (wired up by ProjectSetup).</summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GameAssets : MonoBehaviour
    {
        public static GameAssets Instance { get; private set; }

        [Header("Materials")]
        public Material Lit;
        public Material Ghost;
        public Material Line;
        public Material Smoke;
        public Material Glow;
        public Material Flame;
        public Material SkidMark;
        public Sprite Rounded;

        [Header("Sounds")]
        public AudioClip Engine;
        public AudioClip Skid;
        public AudioClip Boost;
        public AudioClip Bump;
        public AudioClip Beep;
        public AudioClip Go;
        public AudioClip Lap;
        public AudioClip Finish;
        public AudioClip Click;

        void Awake() => Instance = this;
    }
}
