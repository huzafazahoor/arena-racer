using UnityEngine;

namespace ArenaRacer
{
    /// <summary>Two-dimensional one-shots (UI, countdown, lap chimes) and the global mute toggle.</summary>
    public sealed class Sfx : MonoBehaviour
    {
        static Sfx _instance;
        AudioSource _source;
        AudioSource _pitchedSource;

        public static bool Muted { get; private set; }

        void Awake()
        {
            _instance = this;
            _source = CreateSource();
            _pitchedSource = CreateSource();
            Muted = PlayerPrefs.GetInt("arena-racer.muted", 0) == 1;
            AudioListener.volume = Muted ? 0f : 1f;
        }

        AudioSource CreateSource()
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            return source;
        }

        public static void Play(AudioClip clip, float volume = 1f)
        {
            if (_instance != null && clip != null) _instance._source.PlayOneShot(clip, volume);
        }

        public static void PlayPitched(AudioClip clip, float pitch, float volume = 1f)
        {
            if (_instance == null || clip == null) return;
            _instance._pitchedSource.pitch = pitch;
            _instance._pitchedSource.PlayOneShot(clip, volume);
        }

        public static void ToggleMute()
        {
            Muted = !Muted;
            AudioListener.volume = Muted ? 0f : 1f;
            PlayerPrefs.SetInt("arena-racer.muted", Muted ? 1 : 0);
        }
    }
}
