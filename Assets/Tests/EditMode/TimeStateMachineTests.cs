using System;
using Game.Core;
using NUnit.Framework;

namespace Game.Tests
{
    internal sealed class StubPlanRunner : IPlanRunner
    {
        public bool IsRunning { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public event Action PlanFinished;

        public void StartPlan()
        {
            IsRunning = true;
            StartCount++;
        }

        public void StopPlan()
        {
            IsRunning = false;
            StopCount++;
        }

        public void Finish()
        {
            IsRunning = false;
            PlanFinished?.Invoke();
        }
    }

    public sealed class TimeStateMachineTests
    {
        [Test]
        public void StartsInRunning_WithControllersTicking()
        {
            var m = new TimeStateMachine();
            Assert.AreEqual(CombatPhase.Running, m.Current);
            Assert.IsTrue(m.ControllersTicking);
        }

        [Test]
        public void EnterPlanning_FreezesControllers()
        {
            var m = new TimeStateMachine();
            Assert.IsTrue(m.EnterPlanning());
            Assert.AreEqual(CombatPhase.Planning, m.Current);
            Assert.IsFalse(m.ControllersTicking);
        }

        [Test]
        public void EnterPlanning_IsIdempotent()
        {
            var m = new TimeStateMachine();
            int events = 0;
            m.PhaseChanged += (_, __) => events++;
            Assert.IsTrue(m.EnterPlanning());
            Assert.IsTrue(m.EnterPlanning());
            Assert.AreEqual(1, events);
        }

        [Test]
        public void FullCycle_Running_Planning_Executing_Running()
        {
            var m = new TimeStateMachine();
            var runner = new StubPlanRunner();
            var seen = new System.Collections.Generic.List<string>();
            m.PhaseChanged += (a, b) => seen.Add(a + "->" + b);

            Assert.IsTrue(m.EnterPlanning());
            Assert.IsTrue(m.AcceptPlan(runner));
            Assert.AreEqual(CombatPhase.Executing, m.Current);
            Assert.AreEqual(1, runner.StartCount);
            Assert.IsTrue(m.ControllersTicking);

            runner.Finish();
            Assert.AreEqual(CombatPhase.Running, m.Current);
            Assert.IsTrue(m.ControllersTicking);
            Assert.AreEqual(
                new[] { "Running->Planning", "Planning->Executing", "Executing->Running" },
                seen.ToArray());
        }

        [Test]
        public void InvalidTransitions_AreRejected()
        {
            var m = new TimeStateMachine();
            var runner = new StubPlanRunner();
            // AcceptPlan from Running: rejected, runner never started.
            Assert.IsFalse(m.AcceptPlan(runner));
            Assert.AreEqual(0, runner.StartCount);
            // OnPlanFinished from Planning: rejected.
            Assert.IsTrue(m.EnterPlanning());
            Assert.IsFalse(m.OnPlanFinished());
            Assert.AreEqual(CombatPhase.Planning, m.Current);
            // EnterPlanning from Executing: rejected.
            Assert.IsTrue(m.AcceptPlan(runner));
            Assert.IsFalse(m.EnterPlanning());
            // AcceptPlan with null: rejected.
            Assert.IsFalse(m.AcceptPlan(null));
        }

        [Test]
        public void OnPlanFinished_IsIdempotentInRunning()
        {
            var m = new TimeStateMachine();
            int events = 0;
            m.PhaseChanged += (_, __) => events++;
            Assert.IsTrue(m.OnPlanFinished());
            Assert.AreEqual(0, events);
        }

        [Test]
        public void CancelPlanning_ReturnsToRunning()
        {
            var m = new TimeStateMachine();
            Assert.IsTrue(m.EnterPlanning());
            Assert.IsTrue(m.CancelPlanning());
            Assert.AreEqual(CombatPhase.Running, m.Current);
            Assert.IsTrue(m.ControllersTicking);
        }

        [Test]
        public void InterruptToRunning_StopsRunner()
        {
            var m = new TimeStateMachine();
            var runner = new StubPlanRunner();
            Assert.IsTrue(m.EnterPlanning());
            Assert.IsTrue(m.AcceptPlan(runner));
            Assert.IsTrue(m.InterruptToRunning());
            Assert.AreEqual(CombatPhase.Running, m.Current);
            Assert.AreEqual(1, runner.StopCount);
        }

        [Test]
        public void TimeScale_DefaultsToOne_AndIsIndependentOfPause()
        {
            var m = new TimeStateMachine();
            Assert.AreEqual(1f, m.TimeScale);
            m.TimeScale = 0.5f;
            Assert.IsTrue(m.EnterPlanning());
            Assert.AreEqual(0.5f, m.TimeScale);
            Assert.IsFalse(m.ControllersTicking);
        }
    }
}
