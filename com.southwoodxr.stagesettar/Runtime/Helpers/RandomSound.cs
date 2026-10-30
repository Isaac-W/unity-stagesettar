using UnityEngine;

namespace StageSettAR.Helpers
{
    [AddComponentMenu("StageSettAR/Helpers/Random Sound")]
    [RequireComponent(typeof(AudioSource))]
    public class RandomSound : MonoBehaviour
    {
        [Tooltip("PlayRandom() picks one of these each time (e.g., three different footstep sounds).")]
        public AudioClip[] clips;

        [Tooltip("Randomly shift the pitch a little so repeats sound less robotic. 0 = no change.")]
        [Range(0f, 0.5f)]
        public float pitchVariation = 0.1f;

        private AudioSource source;

        private void Awake()
        {
            source = GetComponent<AudioSource>();
        }

        public void PlayRandom()
        {
            if (clips == null || clips.Length == 0)
            {
                return;
            }
            source.pitch = 1f + Random.Range(-pitchVariation, pitchVariation);
            source.PlayOneShot(clips[Random.Range(0, clips.Length)]);
        }
    }
}
