using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Centered end-of-game overlay. Hidden by default; shown once by GameOverManager.
    public sealed class GameOverHUD : MonoBehaviour
    {
        [SerializeField] private Text victoryText;
        [SerializeField] private Text defeatText;

        private void Awake()
        {
            Hide();
        }

        public void ShowVictory()
        {
            if (victoryText != null)
            {
                victoryText.text = "¡VICTORIA!\nPulsa R para reiniciar";
                victoryText.color = Color.green;
                victoryText.gameObject.SetActive(true);
            }
            if (defeatText != null) defeatText.gameObject.SetActive(false);
        }

        public void ShowDefeat()
        {
            if (defeatText != null)
            {
                defeatText.text = "HAS CAÍDO\nPulsa R para reiniciar";
                defeatText.color = Color.red;
                defeatText.gameObject.SetActive(true);
            }
            if (victoryText != null) victoryText.gameObject.SetActive(false);
        }

        public void Hide()
        {
            if (victoryText != null) victoryText.gameObject.SetActive(false);
            if (defeatText != null) defeatText.gameObject.SetActive(false);
        }
    }
}
