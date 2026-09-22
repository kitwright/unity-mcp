// Copyright (C) KitWright. All rights reserved.

using System;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEditor;

namespace KitWright.Editor.Tests
{
    public sealed class NoThrottleLeaseTests
    {
        private bool _hadInteractionMode;
        private int _savedInteractionMode;
        private bool _hadIdleTime;
        private int _savedIdleTime;

        [SetUp]
        public void SetUp()
        {
            // The run_tests wrapper holds a live lease while this suite runs; release it before
            // snapshotting, otherwise the snapshot captures the leased values and the teardown
            // pins the user's prefs to No Throttling.
            NoThrottleLease.Release();
            _hadInteractionMode = EditorPrefs.HasKey(NoThrottleLease.InteractionModeKey);
            _savedInteractionMode = EditorPrefs.GetInt(NoThrottleLease.InteractionModeKey, 0);
            _hadIdleTime = EditorPrefs.HasKey(NoThrottleLease.ApplicationIdleTimeKey);
            _savedIdleTime = EditorPrefs.GetInt(NoThrottleLease.ApplicationIdleTimeKey, 4);
            CleanLeaseState();
        }

        [TearDown]
        public void TearDown()
        {
            CleanLeaseState();
            if (_hadInteractionMode)
                EditorPrefs.SetInt(NoThrottleLease.InteractionModeKey, _savedInteractionMode);
            else
                EditorPrefs.DeleteKey(NoThrottleLease.InteractionModeKey);
            if (_hadIdleTime)
                EditorPrefs.SetInt(NoThrottleLease.ApplicationIdleTimeKey, _savedIdleTime);
            else
                EditorPrefs.DeleteKey(NoThrottleLease.ApplicationIdleTimeKey);
        }

        private static void CleanLeaseState()
        {
            EditorPrefs.DeleteKey(NoThrottleLease.ActiveKey);
            EditorPrefs.DeleteKey(NoThrottleLease.PrevInteractionModeKey);
            EditorPrefs.DeleteKey(NoThrottleLease.PrevIdleTimeKey);
            SessionState.EraseString(NoThrottleLease.DeadlineKey);
        }

        [Test]
        public void Acquire_SwitchesToNoThrottling()
        {
            EditorPrefs.SetInt(NoThrottleLease.InteractionModeKey, 0);
            EditorPrefs.SetInt(NoThrottleLease.ApplicationIdleTimeKey, 4);

            NoThrottleLease.Acquire(TimeSpan.FromMinutes(1));

            Assert.IsTrue(NoThrottleLease.IsActive);
            Assert.AreEqual(1, EditorPrefs.GetInt(NoThrottleLease.InteractionModeKey));
            Assert.AreEqual(0, EditorPrefs.GetInt(NoThrottleLease.ApplicationIdleTimeKey));
        }

        [Test]
        public void Acquire_CapturesOriginalSettings()
        {
            EditorPrefs.SetInt(NoThrottleLease.InteractionModeKey, 2);
            EditorPrefs.SetInt(NoThrottleLease.ApplicationIdleTimeKey, 7);

            NoThrottleLease.Acquire(TimeSpan.FromMinutes(1));

            Assert.AreEqual(2, EditorPrefs.GetInt(NoThrottleLease.PrevInteractionModeKey));
            Assert.AreEqual(7, EditorPrefs.GetInt(NoThrottleLease.PrevIdleTimeKey));
        }

        [Test]
        public void SecondAcquire_DoesNotOverwriteCapturedSettings()
        {
            EditorPrefs.SetInt(NoThrottleLease.InteractionModeKey, 2);
            EditorPrefs.SetInt(NoThrottleLease.ApplicationIdleTimeKey, 7);

            NoThrottleLease.Acquire(TimeSpan.Zero);
            NoThrottleLease.Acquire(TimeSpan.Zero);
            NoThrottleLease.TryExpire();

            Assert.IsFalse(NoThrottleLease.IsActive);
            Assert.AreEqual(2, EditorPrefs.GetInt(NoThrottleLease.InteractionModeKey));
            Assert.AreEqual(7, EditorPrefs.GetInt(NoThrottleLease.ApplicationIdleTimeKey));
        }

        [Test]
        public void TryExpire_BeforeDeadline_KeepsLease()
        {
            NoThrottleLease.Acquire(TimeSpan.FromMinutes(10));

            NoThrottleLease.TryExpire();

            Assert.IsTrue(NoThrottleLease.IsActive);
            Assert.AreEqual(1, EditorPrefs.GetInt(NoThrottleLease.InteractionModeKey));
        }

        [Test]
        public void TryExpire_PastDeadline_RestoresAndCleansUp()
        {
            EditorPrefs.SetInt(NoThrottleLease.InteractionModeKey, 0);
            EditorPrefs.SetInt(NoThrottleLease.ApplicationIdleTimeKey, 4);

            NoThrottleLease.Acquire(TimeSpan.Zero);
            NoThrottleLease.TryExpire();

            Assert.IsFalse(NoThrottleLease.IsActive);
            Assert.AreEqual(0, EditorPrefs.GetInt(NoThrottleLease.InteractionModeKey));
            Assert.AreEqual(4, EditorPrefs.GetInt(NoThrottleLease.ApplicationIdleTimeKey));
            Assert.IsFalse(EditorPrefs.HasKey(NoThrottleLease.PrevInteractionModeKey));
            Assert.IsFalse(EditorPrefs.HasKey(NoThrottleLease.PrevIdleTimeKey));
            Assert.AreEqual("", SessionState.GetString(NoThrottleLease.DeadlineKey, ""));
        }

        [Test]
        public void Release_RestoresImmediately()
        {
            EditorPrefs.SetInt(NoThrottleLease.InteractionModeKey, 0);
            EditorPrefs.SetInt(NoThrottleLease.ApplicationIdleTimeKey, 4);

            NoThrottleLease.Acquire(TimeSpan.FromMinutes(10));
            NoThrottleLease.Release();

            Assert.IsFalse(NoThrottleLease.IsActive);
            Assert.AreEqual(0, EditorPrefs.GetInt(NoThrottleLease.InteractionModeKey));
            Assert.AreEqual(4, EditorPrefs.GetInt(NoThrottleLease.ApplicationIdleTimeKey));
        }

        [Test]
        public void Release_WithoutLease_IsNoOp()
        {
            EditorPrefs.SetInt(NoThrottleLease.InteractionModeKey, 2);

            NoThrottleLease.Release();

            Assert.IsFalse(NoThrottleLease.IsActive);
            Assert.AreEqual(2, EditorPrefs.GetInt(NoThrottleLease.InteractionModeKey));
        }

        [Test]
        public void RecoverIfStale_RestoresLeaseFromDeadSession()
        {
            EditorPrefs.SetInt(NoThrottleLease.InteractionModeKey, 0);
            EditorPrefs.SetInt(NoThrottleLease.ApplicationIdleTimeKey, 4);
            NoThrottleLease.Acquire(TimeSpan.FromMinutes(10));

            // A fresh editor session has no SessionState: simulate by erasing the deadline.
            SessionState.EraseString(NoThrottleLease.DeadlineKey);

            Assert.IsTrue(NoThrottleLease.RecoverIfStale());
            Assert.IsFalse(NoThrottleLease.IsActive);
            Assert.AreEqual(0, EditorPrefs.GetInt(NoThrottleLease.InteractionModeKey));
            Assert.AreEqual(4, EditorPrefs.GetInt(NoThrottleLease.ApplicationIdleTimeKey));
        }

        [Test]
        public void RecoverIfStale_LiveLease_DoesNothing()
        {
            NoThrottleLease.Acquire(TimeSpan.FromMinutes(10));

            Assert.IsFalse(NoThrottleLease.RecoverIfStale());
            Assert.IsTrue(NoThrottleLease.IsActive);

            NoThrottleLease.Release();
        }

        [Test]
        public void RecoverIfStale_NoLease_ReturnsFalse()
        {
            Assert.IsFalse(NoThrottleLease.RecoverIfStale());
        }
    }
}
