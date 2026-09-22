// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class MenuItemGuardTests
    {
        [Test]
        public void MatchBlockingMenuPath_CatchesModalOpenersIncludingTheEllipsisForm()
        {
            Assert.AreEqual("File/Save As", MenuItemFunctions.MatchBlockingMenuPath("File/Save As..."));
            Assert.AreEqual("File/Save As", MenuItemFunctions.MatchBlockingMenuPath("File/Save As…"));
            Assert.AreEqual("File/Exit", MenuItemFunctions.MatchBlockingMenuPath("file/exit"));
            Assert.AreEqual("Assets/Import New Asset", MenuItemFunctions.MatchBlockingMenuPath("Assets/Import New Asset..."));
            Assert.AreEqual("File/Build And Run", MenuItemFunctions.MatchBlockingMenuPath("File/Build And Run"));
        }

        [Test]
        public void LearnedModalPaths_AreRefusedAndClearable()
        {
            var before = MenuItemFunctions.LearnedModalPaths();
            try
            {
                Assert.IsNull(MenuItemFunctions.MatchBlockingMenuPath("Window/Layouts/Default"));

                UnityEditor.EditorPrefs.SetString(MenuItemFunctions.LearnedKey, "Window/Layouts/Default");
                Assert.AreEqual("Window/Layouts/Default",
                    MenuItemFunctions.MatchBlockingMenuPath("Window/Layouts/Default..."));

                MenuItemFunctions.ForgetLearnedModalPaths();
                Assert.IsNull(MenuItemFunctions.MatchBlockingMenuPath("Window/Layouts/Default"));
            }
            finally
            {
                if (before.Length > 0)
                    UnityEditor.EditorPrefs.SetString(MenuItemFunctions.LearnedKey, string.Join("\n", before));
            }
        }

        [Test]
        public void LearnedKey_IsScopedToThisProject()
        {
            StringAssert.StartsWith("KitWright.MenuItem.LearnedModal.", MenuItemFunctions.LearnedKey);
            Assert.Greater(MenuItemFunctions.LearnedKey.Length, "KitWright.MenuItem.LearnedModal.".Length,
                "EditorPrefs is shared across projects, so the key must carry the project pin.");
        }

        [Test]
        public void MatchBlockingMenuPath_LeavesOrdinaryItemsAlone()
        {
            Assert.IsNull(MenuItemFunctions.MatchBlockingMenuPath("GameObject/2D Object/Sprites/Square"));
            Assert.IsNull(MenuItemFunctions.MatchBlockingMenuPath("Edit/Undo"));
            Assert.IsNull(MenuItemFunctions.MatchBlockingMenuPath("Window/Layouts/Default"));
            Assert.IsNull(MenuItemFunctions.MatchBlockingMenuPath("Assets/Refresh"));
            Assert.IsNull(MenuItemFunctions.MatchBlockingMenuPath(null));
            Assert.IsNull(MenuItemFunctions.MatchBlockingMenuPath("   "));
        }

        [Test]
        public void ExecuteMenuItem_RefusesAModalOpenerButHonoursTheOptOut()
        {
            var refused = Newtonsoft.Json.JsonConvert.SerializeObject(
                MenuItemFunctions.ExecuteMenuItem("File/Save As..."));

            StringAssert.Contains("MENU_ITEM_OPENS_MODAL", refused);
            StringAssert.Contains("save_scene", refused);

            var ordinary = Newtonsoft.Json.JsonConvert.SerializeObject(
                MenuItemFunctions.ExecuteMenuItem("Edit/Undo"));
            Assert.IsFalse(ordinary.Contains("MENU_ITEM_OPENS_MODAL"));
        }
    }
}
