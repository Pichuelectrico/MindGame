using Game.AI;
using Game.Combat;
using NUnit.Framework;
using UnityEngine;

// F6 T3: the boss gate. While dormant the boss's BotController and CombatDirector
// stay disabled (wired that way by WireGruntSpawner) and GruntSpawner loops the
// pre-combat idle. Kill counting must wake the boss exactly at killsToWakeBoss.
public sealed class GruntGateTests
{
    private static (GruntSpawner spawner, BotController bot, CombatDirector director) Build()
    {
        var host = new GameObject("WaveDirector");
        var boss = new GameObject("Boss_Mutant");
        var spawner = host.AddComponent<GruntSpawner>();
        var bot = boss.AddComponent<BotController>();
        var director = boss.AddComponent<CombatDirector>();
        // Wire the gate refs exactly like WireGruntSpawner does. Without this,
        // WakeBoss's serialized refs are null and its GetComponent fallback
        // looks on the spawner's own GameObject, finding nothing.
        var so = new UnityEditor.SerializedObject(spawner);
        so.FindProperty("boss").objectReferenceValue = boss;
        so.FindProperty("bossController").objectReferenceValue = bot;
        so.FindProperty("bossDirector").objectReferenceValue = director;
        so.ApplyModifiedPropertiesWithoutUndo();
        // Simulate the wired dormant state (boss disabled until the gate opens).
        bot.enabled = false;
        director.enabled = false;
        return (spawner, bot, director);
    }

    [Test]
    public void NineKills_BossStaysDormant()
    {
        var (spawner, bot, director) = Build();
        for (int i = 0; i < spawner.KillsToWakeBoss - 1; i++)
            spawner.NotifyGruntDied(null);

        Assert.AreEqual(spawner.KillsToWakeBoss - 1, spawner.KillCount, "kill count before the gate");
        Assert.IsFalse(spawner.BossAwake, "boss must stay dormant at 9 kills");
        Assert.IsFalse(bot.enabled, "BotController must remain disabled while dormant");
        Assert.IsFalse(director.enabled, "CombatDirector must remain disabled while dormant");
    }

    [Test]
    public void TenKills_WakeBoss()
    {
        var (spawner, bot, director) = Build();
        for (int i = 0; i < spawner.KillsToWakeBoss; i++)
            spawner.NotifyGruntDied(null);

        Assert.AreEqual(spawner.KillsToWakeBoss, spawner.KillCount);
        Assert.IsTrue(spawner.BossAwake, "boss must wake exactly at 10 kills");
        Assert.IsTrue(bot.enabled, "WakeBoss must enable BotController");
        Assert.IsTrue(director.enabled, "WakeBoss must enable CombatDirector");
    }

    [Test]
    public void KillCounter_EventCarriesRunningTotal()
    {
        var (spawner, _, _) = Build();
        int last = -1;
        spawner.OnGruntKilled += total => last = total;

        spawner.NotifyGruntDied(null);
        Assert.AreEqual(1, last, "first kill reports 1");

        spawner.NotifyGruntDied(null);
        Assert.AreEqual(2, last, "second kill reports the running total");
    }

    [Test]
    public void WakeBoss_IsIdempotent()
    {
        var (spawner, _, _) = Build();
        spawner.WakeBoss();
        spawner.WakeBoss();
        Assert.IsTrue(spawner.BossAwake);
    }

    [Test]
    public void Overkill_KeepsCountingWithoutResurrectingGate()
    {
        var (spawner, _, _) = Build();
        for (int i = 0; i < spawner.KillsToWakeBoss + 5; i++)
            spawner.NotifyGruntDied(null);
        Assert.AreEqual(spawner.KillsToWakeBoss + 5, spawner.KillCount);
        Assert.IsTrue(spawner.BossAwake);
    }
}
