namespace Game.AI
{
    // Implemented later at scene-integration time (reads live scene state).
    public interface ICombatStateProvider
    {
        CombatStateSnapshot GetSnapshot();
    }
}
