using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Tests
{
    public sealed class GameOverTests
    {
        private static (GameOverManager manager, HealthSystem boss, HealthSystem player) Scene()
        {
            var go = new GameObject("GameOverTest");
            var manager = go.AddComponent<GameOverManager>();
            var boss = new GameObject("Boss").AddComponent<HealthSystem>();
            var player = new GameObject("Player").AddComponent<HealthSystem>();
            manager.Bind(boss, player, null, null);
            return (manager, boss, player);
        }

        [Test]
        public void BossDies_Victory()
        {
            var (manager, boss, player) = Scene();
            GameResult seen = GameResult.None;
            manager.GameOver += r => seen = r;
            boss.ApplyDamage(10);
            Assert.AreEqual(GameResult.Victory, manager.Result);
            Assert.AreEqual(GameResult.Victory, seen);
            Object.DestroyImmediate(manager.gameObject);
            Object.DestroyImmediate(boss.gameObject);
            Object.DestroyImmediate(player.gameObject);
        }

        [Test]
        public void PlayerDies_Defeat()
        {
            var (manager, boss, player) = Scene();
            player.ApplyDamage(10);
            Assert.AreEqual(GameResult.Defeat, manager.Result);
            Object.DestroyImmediate(manager.gameObject);
            Object.DestroyImmediate(boss.gameObject);
            Object.DestroyImmediate(player.gameObject);
        }

        [Test]
        public void FirstDeathWins_NeverBoth()
        {
            var (manager, boss, player) = Scene();
            boss.ApplyDamage(10);
            player.ApplyDamage(10);
            Assert.AreEqual(GameResult.Victory, manager.Result);
            Object.DestroyImmediate(manager.gameObject);
            Object.DestroyImmediate(boss.gameObject);
            Object.DestroyImmediate(player.gameObject);
        }

        [Test]
        public void BossDies_FreezesSpawner_AndEntersPlanning()
        {
            var go = new GameObject("GameOverTest_Full");
            var manager = go.AddComponent<GameOverManager>();
            var boss = new GameObject("Boss").AddComponent<HealthSystem>();
            var player = new GameObject("Player").AddComponent<HealthSystem>();
            var spawner = new GameObject("Spawner").AddComponent<GruntSpawner>();
            var time = new GameObject("Time").AddComponent<TimeController>();

            // Setup GameOverUI
            var overlayGo = new GameObject("Overlay");
            var titleGo = new GameObject("Title");
            var titleText = titleGo.AddComponent<Text>();
            var msgGo = new GameObject("Msg");
            var msgText = msgGo.AddComponent<Text>();
            var hintGo = new GameObject("Hint");
            var hintText = hintGo.AddComponent<Text>();
            var btnGo = new GameObject("Btn");
            var btn = btnGo.AddComponent<Button>();

            var uiGo = new GameObject("GameOverUI");
            var ui = uiGo.AddComponent<GameOverUI>();
            var uiso = new UnityEditor.SerializedObject(ui);
            uiso.FindProperty("overlayPanel").objectReferenceValue = overlayGo;
            uiso.FindProperty("titleText").objectReferenceValue = titleText;
            uiso.FindProperty("messageText").objectReferenceValue = msgText;
            uiso.FindProperty("hintText").objectReferenceValue = hintText;
            uiso.FindProperty("restartButton").objectReferenceValue = btn;
            uiso.FindProperty("manager").objectReferenceValue = manager;
            uiso.ApplyModifiedPropertiesWithoutUndo();

            overlayGo.SetActive(false);

            manager.Bind(boss, player, spawner, null, time, ui);

            Assert.IsTrue(spawner.enabled);
            Assert.AreEqual(CombatPhase.Running, time.Current);
            Assert.IsFalse(ui.IsShowing);

            // Kill boss
            boss.ApplyDamage(10);

            Assert.AreEqual(GameResult.Victory, manager.Result);
            Assert.IsFalse(spawner.enabled);
            Assert.AreEqual(CombatPhase.Planning, time.Current);
            Assert.IsTrue(ui.IsShowing);
            StringAssert.Contains("VICTORIA", ui.Title);
            StringAssert.Contains("MUTANTE", ui.Message);

            Object.DestroyImmediate(go);
            Object.DestroyImmediate(boss.gameObject);
            Object.DestroyImmediate(player.gameObject);
            Object.DestroyImmediate(spawner.gameObject);
            Object.DestroyImmediate(time.gameObject);
            Object.DestroyImmediate(overlayGo);
            Object.DestroyImmediate(titleGo);
            Object.DestroyImmediate(msgGo);
            Object.DestroyImmediate(hintGo);
            Object.DestroyImmediate(btnGo);
            Object.DestroyImmediate(uiGo);
        }

        [Test]
        public void PlayerDies_FreezesSpawner_AndShowsDefeatUI()
        {
            var go = new GameObject("GameOverTest_Defeat");
            var manager = go.AddComponent<GameOverManager>();
            var boss = new GameObject("Boss").AddComponent<HealthSystem>();
            var player = new GameObject("Player").AddComponent<HealthSystem>();
            var spawner = new GameObject("Spawner").AddComponent<GruntSpawner>();
            var time = new GameObject("Time").AddComponent<TimeController>();

            var overlayGo = new GameObject("Overlay");
            var titleGo = new GameObject("Title");
            var titleText = titleGo.AddComponent<Text>();
            var msgGo = new GameObject("Msg");
            var msgText = msgGo.AddComponent<Text>();
            var hintGo = new GameObject("Hint");
            var hintText = hintGo.AddComponent<Text>();

            var uiGo = new GameObject("GameOverUI");
            var ui = uiGo.AddComponent<GameOverUI>();
            var uiso = new UnityEditor.SerializedObject(ui);
            uiso.FindProperty("overlayPanel").objectReferenceValue = overlayGo;
            uiso.FindProperty("titleText").objectReferenceValue = titleText;
            uiso.FindProperty("messageText").objectReferenceValue = msgText;
            uiso.FindProperty("hintText").objectReferenceValue = hintText;
            uiso.FindProperty("manager").objectReferenceValue = manager;
            uiso.ApplyModifiedPropertiesWithoutUndo();

            overlayGo.SetActive(false);

            manager.Bind(boss, player, spawner, null, time, ui);

            // Kill player
            player.ApplyDamage(10);

            Assert.AreEqual(GameResult.Defeat, manager.Result);
            Assert.IsFalse(spawner.enabled);
            Assert.AreEqual(CombatPhase.Planning, time.Current);
            Assert.IsTrue(ui.IsShowing);
            StringAssert.Contains("DERROTA", ui.Title);

            Object.DestroyImmediate(go);
            Object.DestroyImmediate(boss.gameObject);
            Object.DestroyImmediate(player.gameObject);
            Object.DestroyImmediate(spawner.gameObject);
            Object.DestroyImmediate(time.gameObject);
            Object.DestroyImmediate(overlayGo);
            Object.DestroyImmediate(titleGo);
            Object.DestroyImmediate(msgGo);
            Object.DestroyImmediate(hintGo);
            Object.DestroyImmediate(uiGo);
        }
    }
}
