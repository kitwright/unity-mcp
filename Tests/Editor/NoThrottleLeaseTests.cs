// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEditor;

namespace KitWright.Editor.Tests
{
    public sealed class NoThrottleLeaseTests
    {
        // These are the user's real, user-wide prefs, so they are put back exactly as found.
        private bool _hadInteractionMode;
        private int _savedInteractionMode;
        private bool _hadIdleTime;
        private int _savedIdleTime;

        [SetUp]
        public void SetUp()
        {
            _hadInteractionMode = EditorPrefs.HasKey(NoThrottleLease.InteractionModeKey);
            _savedInteractionMode = EditorPrefs.GetInt(NoThrottleLease.InteractionModeKey, 0);
            _hadIdleTime = EditorPrefs.HasKey(NoThrottleLease.ApplicationIdleTimeKey);
            _savedIdleTime = EditorPrefs.GetInt(NoThrottleLease.ApplicationIdleTimeKey, 4);
        }

        [TearDown]
        public void TearDown()
        {
            EditorPrefs.DeleteKey(NoThrottleLease.ActiveKey);
            EditorPrefs.DeleteKey(NoThrottleLease.PrevInteractionModeKey);
            EditorPrefs.DeleteKey(NoThrottleLease.PrevIdleTimeKey);
            if (_hadInteractionMode)
                EditorPrefs.SetInt(NoThrottleLease.InteractionModeKey, _savedInteractionMode);
            else
                EditorPrefs.DeleteKey(NoThrottleLease.InteractionModeKey);
            if (_hadIdleTime)
                EditorPrefs.SetInt(NoThrottleLease.ApplicationIdleTimeKey, _savedIdleTime);
            else
                EditorPrefs.DeleteKey(NoThrottleLease.ApplicationIdleTimeKey);
        }

        // An older version that crashed mid-lease left No Throttling on for every editor of the user.
        [Test]
        public void ALeaseLeftActiveByAnOlderVersionIsPutBack()
        {
            EditorPrefs.SetInt(NoThrottleLease.PrevInteractionModeKey, 2);
            EditorPrefs.SetInt(NoThrottleLease.PrevIdleTimeKey, 7);
            EditorPrefs.SetInt(NoThrottleLease.InteractionModeKey, 1);
            EditorPrefs.SetInt(NoThrottleLease.ApplicationIdleTimeKey, 0);
            EditorPrefs.SetBool(NoThrottleLease.ActiveKey, true);

            NoThrottleLease.RestoreLeftoverLease();

            Assert.AreEqual(2, EditorPrefs.GetInt(NoThrottleLease.InteractionModeKey));
            Assert.AreEqual(7, EditorPrefs.GetInt(NoThrottleLease.ApplicationIdleTimeKey));
            Assert.IsFalse(EditorPrefs.HasKey(NoThrottleLease.ActiveKey));
            Assert.IsFalse(EditorPrefs.HasKey(NoThrottleLease.PrevInteractionModeKey));
        }

        [Test]
        public void WithNoLeftoverLeaseTheUsersSettingIsLeftAlone()
        {
            EditorPrefs.SetInt(NoThrottleLease.InteractionModeKey, 2);

            NoThrottleLease.RestoreLeftoverLease();

            Assert.AreEqual(2, EditorPrefs.GetInt(NoThrottleLease.InteractionModeKey));
        }
    }
}
