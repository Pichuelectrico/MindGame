namespace Game.Execution
{
    // Minimal animation seam so EditMode tests run without an Animator.
    public interface IAnimationDriver
    {
        void Play(string stateName, bool useRootMotion);
        bool HasState(string stateName);
    }
}
