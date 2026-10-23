using UnityEngine;

namespace Game.Execution
{
    // Animator-backed driver. States are executor-driven (no auto-transitions):
    // each Enter cross-fades to the state named after the actionId.
    // ROOT MOTION IS ALWAYS OFF: Mixamo clips carry baked root motion that would
    // teleport the fighter; all displacement is programmatic (ApplyLocomotion).
    // The useRootMotion asset flags are deliberately ignored until clips are
    // re-authored without baked motion.
    public sealed class AnimatorDriver : IAnimationDriver
    {
        private readonly Animator animator;
        private readonly float fadeDuration;

        public AnimatorDriver(Animator animator, float fadeDuration = 0.1f)
        {
            this.animator = animator;
            this.fadeDuration = fadeDuration;
            if (this.animator != null) this.animator.applyRootMotion = false;
        }

        public void Play(string stateName, bool useRootMotion)
        {
            if (animator == null || string.IsNullOrEmpty(stateName)) return;
            animator.applyRootMotion = false;
            animator.CrossFadeInFixedTime(stateName, fadeDuration);
        }

        public bool HasState(string stateName)
        {
            if (animator == null || string.IsNullOrEmpty(stateName)) return false;
            return animator.HasState(0, Animator.StringToHash(stateName));
        }
    }
}
