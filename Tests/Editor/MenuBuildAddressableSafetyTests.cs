// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.IO;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class MenuBuildAddressableSafetyTests
    {
        private static string Root => Path.Combine(Path.GetTempPath(), "KwProjectF1");

        private static string Code(object error) => (string)JObject.FromObject(error)["code"];

        [TestCase("File/Exit")]
        [TestCase("file/exit")]
        [TestCase("File/Quit")]
        [TestCase("Unity/Quit Unity")]
        [TestCase("File/New Project...")]
        [TestCase("File/Open Project")]
        [TestCase("Edit/Clear All PlayerPrefs")]
        [TestCase("/File/Exit")]
        [TestCase("//File/Exit")]
        [TestCase("File//Exit")]
        [TestCase("File/Exit/")]
        [TestCase("File/ Exit")]
        [TestCase("/File/Open Project")]
        [TestCase("File//New Project")]
        [TestCase("Edit//Clear All PlayerPrefs")]
        [TestCase("Unity//Quit Unity")]
        public void QuitProjectSwitchAndPlayerPrefsAreRefusedEvenWithAllowModal(string menuPath)
        {
            foreach (var allowModal in new[] { false, true })
            {
                var refusal = MenuItemFunctions.Refusal(menuPath, allowModal, _ => true);

                Assert.IsNotNull(refusal, $"{menuPath} allowModal={allowModal}");
                Assert.AreEqual("MENU_ITEM_REFUSED", Code(refusal));
                StringAssert.Contains("will not run it", (string)JObject.FromObject(refusal)["hint"]);
            }
        }

        [Test]
        public void ADialogOpenerIsRefusedUnderEverySpellingUnityResolvesToIt()
        {
            foreach (var spelling in new[] { "/File/Save As", "File//Save As...", "//File/Save As/" })
                Assert.AreEqual("MENU_ITEM_OPENS_MODAL", Code(MenuItemFunctions.Refusal(spelling, false, _ => true)), spelling);
        }

        [TestCase("/Edit//Deselect All/", "Edit/Deselect All")]
        [TestCase(" File / Exit ... ", "File/Exit")]
        [TestCase("Edit/Undo", "Edit/Undo")]
        public void MenuPathsAreComparedWithoutEmptySegmentsOrTrailingDots(string spelling, string expected)
        {
            Assert.AreEqual(expected, MenuItemFunctions.NormalizeMenuPath(spelling));
        }

        [Test]
        public void AMenuPathWithATabOrNewlineIsRefusedBeforeItCanBeLearned()
        {
            foreach (var path in new[] { "Edit/Undo\tx", "Edit/Undo\nx" })
                Assert.AreEqual("INVALID_PARAM", Code(MenuItemFunctions.ExecuteMenuItem(path)), path);
        }

        [Test]
        public void AllowModalStillLiftsDialogOpeners()
        {
            Assert.IsNull(MenuItemFunctions.Refusal("File/Save As...", true, _ => true));
            Assert.AreEqual("MENU_ITEM_OPENS_MODAL", Code(MenuItemFunctions.Refusal("File/Save As...", false, _ => true)));
            Assert.IsNull(MenuItemFunctions.Refusal("Edit/Undo", false, _ => true));
        }

        [Test]
        public void ReplacementToolIsNamedOnlyWhenTheProfileExposesIt()
        {
            var hidden = JObject.FromObject(MenuItemFunctions.Refusal("File/Build And Run", false, _ => false));
            Assert.AreEqual(JTokenType.Null, hidden["data"]["tool_instead"].Type);
            StringAssert.DoesNotContain("build_player", (string)hidden["hint"]);

            var exposed = JObject.FromObject(MenuItemFunctions.Refusal("File/Build And Run", false, _ => true));
            Assert.AreEqual("build_player", (string)exposed["data"]["tool_instead"]);
            StringAssert.Contains("build_player", (string)exposed["hint"]);
        }

        [Test]
        public void LearnedModalEntriesExpireAndAnUnreadableStampCountsAsExpired()
        {
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var raw = string.Join("\n",
                "Old/Slow\t" + now.AddHours(-2).Ticks,
                "Fresh/Modal\t" + now.AddMinutes(-5).Ticks,
                "Legacy/Entry",
                "Bad/Stamp\tnot-a-number");

            CollectionAssert.AreEqual(
                new[] { "Fresh/Modal", "Legacy/Entry" },
                MenuItemFunctions.LiveLearnedPaths(raw, now));
            Assert.IsEmpty(MenuItemFunctions.LiveLearnedPaths(string.Empty, now));
        }

        [Test]
        public void BuildOutputInProjectRootItsParentsOrUnityFoldersIsProtected()
        {
            var parent = Path.GetDirectoryName(Root);
            var protectedPaths = new[]
            {
                Root,
                Root + Path.DirectorySeparatorChar,
                parent,
                Path.GetPathRoot(Root),
                Path.Combine(Root, "Assets"),
                Path.Combine(Root, "assets", "Sub", "Game.exe"),
                Path.Combine(Root, "ProjectSettings"),
                Path.Combine(Root, "Library", "Game"),
                Path.Combine(Root, "Packages"),
                Path.Combine(Root, "Game.exe"),
                Path.Combine(parent, "Game.exe"),
            };

            foreach (var path in protectedPaths)
                Assert.IsTrue(BuildFunctions.IsProtectedBuildOutput(path, Root), path);
        }

        [Test]
        public void BuildOutputSpelledWithTrailingDotsOrAnExtendedPrefixIsStillProtected()
        {
            if (Path.DirectorySeparatorChar != '\\')
                Assert.Ignore("Windows path spellings.");

            var protectedPaths = new[]
            {
                Path.Combine(Root, "Assets."),
                Path.Combine(Root, "Assets..."),
                Path.Combine(Root, "Library."),
                Path.Combine(Root, "Packages "),
                Path.Combine(Root, "Assets.", "Sub", "Game.exe"),
                @"\\?\" + Root,
                @"\\?\" + Path.Combine(Root, "Assets"),
                @"\\.\" + Path.Combine(Root, "ProjectSettings"),
            };

            foreach (var path in protectedPaths)
                Assert.IsTrue(BuildFunctions.IsProtectedBuildOutput(path, Root), path);

            var drive = Root.Substring(0, 1);
            var share = @"\\localhost\" + drive + "$" + Root.Substring(2);
            Assert.IsTrue(BuildFunctions.IsProtectedBuildOutput(Path.Combine(share, "Assets"), Root), share);
            Assert.IsFalse(BuildFunctions.IsProtectedBuildOutput(Path.Combine(share, "Builds", "Game.exe"), Root), share);
        }

        [Test]
        public void ABuildOutputGivenAsAnEightDotThreeNameIsExpandedBeforeItIsJudged()
        {
            if (Path.DirectorySeparatorChar != '\\')
                Assert.Ignore("Windows 8.3 names.");

            var folder = Path.Combine(Path.GetTempPath(), "KwLongProjectFolderName" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(folder, "Assets"));
            try
            {
                var shortName = ShortPath(folder);
                if (string.Equals(shortName, folder, StringComparison.OrdinalIgnoreCase))
                    Assert.Ignore("8.3 names are not enabled on this volume.");

                Assert.IsTrue(BuildFunctions.IsProtectedBuildOutput(Path.Combine(shortName, "Assets"), folder), shortName);
                Assert.IsTrue(BuildFunctions.IsProtectedBuildOutput(shortName, folder), shortName);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint GetShortPathNameW(string longPath, System.Text.StringBuilder shortPath, uint length);

        private static string ShortPath(string path)
        {
            var buffer = new System.Text.StringBuilder(512);
            return GetShortPathNameW(path, buffer, (uint)buffer.Capacity) > 0 ? buffer.ToString() : path;
        }

        [Test]
        public void BuildOutputInADedicatedFolderIsAllowed()
        {
            var parent = Path.GetDirectoryName(Root);
            var allowed = new[]
            {
                Path.Combine(Root, "Builds", "Win", "Game.exe"),
                Path.Combine(Root, "Builds", "WebGL"),
                Path.Combine(Root, "Builds", "WebGL", "My.Game"),
                Path.Combine(parent, "KwProjectF1-2", "Builds", "Game.exe"),
                Path.Combine(parent, "KwProjectF1Assets", "Game.exe"),
                Path.Combine(parent, "KwOtherBuilds", "Game.exe"),
            };

            foreach (var path in allowed)
                Assert.IsFalse(BuildFunctions.IsProtectedBuildOutput(path, Root), path);
        }

        [Test]
        public void NotAddressableAnswersAnErrorCodeWithAHint()
        {
            var error = JObject.FromObject(AddressableFunctions.NotAddressable("Assets/Editor/Tool.asset"));

            Assert.AreEqual("ASSET_NOT_ADDRESSABLE", (string)error["code"]);
            Assert.AreEqual("Assets/Editor/Tool.asset", (string)error["data"]["path"]);
            Assert.IsNotEmpty((string)error["hint"]);
        }

        [TestCase("Assets")]
        [TestCase("Assets/")]
        [TestCase("Resources/unity_builtin_extra")]
        [TestCase("Library/unity default resources")]
        [TestCase("Library/unity editor resources")]
        [TestCase("ProjectSettings/ProjectSettings.asset")]
        [TestCase("Packages")]
        [TestCase("Assets/Scripts/Player.cs")]
        [TestCase("Assets/Scripts/Player.CS")]
        [TestCase("Assets/Tools/Editor/Config.asset")]
        [TestCase("Assets\\Tools\\Editor\\Config.asset")]
        [TestCase("Assets/Editor")]
        [TestCase("Packages/com.example.pkg/Editor/Config.asset")]
        public void AnAssetAPlayerCannotServeHasAReasonBeforeAnyGroupIsMade(string path)
        {
            Assert.IsNotEmpty(AddressableFunctions.UnservableReason(path), path);
        }

        [TestCase("Assets/Prefabs/Enemy.prefab")]
        [TestCase("Assets/Textures")]
        [TestCase("Assets/Editorial/Cover.png")]
        [TestCase("Assets/Editor.png")]
        [TestCase("Packages/com.example.pkg/Runtime/Config.asset")]
        public void AnAssetThatShipsInThePlayerHasNoReason(string path)
        {
            Assert.IsNull(AddressableFunctions.UnservableReason(path), path);
        }

        [Test]
        public void TheAddressableRefusalCarriesItsReason()
        {
            var reason = AddressableFunctions.UnservableReason("Assets/Scripts/Player.cs");

            var error = JObject.FromObject(AddressableFunctions.NotAddressable("Assets/Scripts/Player.cs", reason));

            Assert.AreEqual("ASSET_NOT_ADDRESSABLE", (string)error["code"]);
            Assert.AreEqual(reason, (string)error["data"]["reason"]);
        }

        [TestCase("Window/Layouts/Revert Factory Settings")]
        [TestCase("Window/Layouts/Revert Factory Settings...")]
        [TestCase("/Window//Layouts/Revert Factory Settings")]
        public void RevertFactorySettingsIsADialogOpenerThatAllowModalLifts(string menuPath)
        {
            Assert.AreEqual("MENU_ITEM_OPENS_MODAL", Code(MenuItemFunctions.Refusal(menuPath, false, _ => true)), menuPath);
            Assert.IsNull(MenuItemFunctions.Refusal(menuPath, true, _ => true), menuPath);
        }

        [Test]
        public void ADisabledMenuItemIsToldApartFromAMissingOne()
        {
            var leaf = MenuItemFunctions.MenuNodeKind.Leaf;
            var unproven = MenuItemFunctions.MenuNodeKind.Unknown;
            Assert.AreEqual("MENU_ITEM_DISABLED", Code(MenuItemFunctions.NotRunAnswer("Edit/Delete", true, leaf)));

            var missing = JObject.FromObject(MenuItemFunctions.NotRunAnswer("Nothing/Here", false, unproven));
            Assert.AreEqual("MENU_ITEM_NOT_FOUND", (string)missing["code"]);
            StringAssert.DoesNotContain("disabled", (string)missing["data"]["hint"]);

            var unknown = JObject.FromObject(MenuItemFunctions.NotRunAnswer("Nothing/Here", null, unproven));
            Assert.AreEqual("MENU_ITEM_NOT_FOUND", (string)unknown["code"]);
            StringAssert.Contains("disabled", (string)unknown["data"]["hint"]);
            StringAssert.Contains("submenu", (string)unknown["data"]["hint"]);
        }

        [Test]
        public void AnItemThatExistsButIsNotProvenToBeALeafIsNeverCalledDisabled()
        {
            var unproven = JObject.FromObject(MenuItemFunctions.NotRunAnswer("Window/Layouts", true, MenuItemFunctions.MenuNodeKind.Unknown));

            Assert.AreEqual("MENU_ITEM_NOT_FOUND", (string)unproven["code"]);
            StringAssert.Contains("submenu", (string)unproven["data"]["hint"]);
        }

        [Test]
        public void ASubmenuIsAnsweredAsASubmenuNotAsADisabledItem()
        {
            var answer = JObject.FromObject(MenuItemFunctions.NotRunAnswer("GameObject/2D Object", true, MenuItemFunctions.MenuNodeKind.Submenu));

            Assert.AreEqual("MENU_ITEM_IS_SUBMENU", (string)answer["code"]);
            StringAssert.Contains("GameObject/2D Object/<item>", (string)answer["hint"]);
        }

        [Test]
        public void ClassifyMenuNodeProvesALeafOnlyFromAnItemAMethodBacksAndASubmenuFromAChildUnderIt()
        {
            var items = new[]
            {
                "GameObject/2D Object/Sprites/Square",
                "GameObject/Create Empty %#n",
                "Assets/Create/Folder",
                "Tools/Do Thing _g",
                "Tools/Numbered #1/Sub",
            };

            Assert.AreEqual(MenuItemFunctions.MenuNodeKind.Submenu, MenuItemFunctions.ClassifyMenuNode("GameObject/2D Object", items));
            Assert.AreEqual(MenuItemFunctions.MenuNodeKind.Submenu, MenuItemFunctions.ClassifyMenuNode("/GameObject//2D Object/", items));
            Assert.AreEqual(MenuItemFunctions.MenuNodeKind.Submenu, MenuItemFunctions.ClassifyMenuNode("Assets/Create", items));
            Assert.AreEqual(MenuItemFunctions.MenuNodeKind.Submenu, MenuItemFunctions.ClassifyMenuNode("Tools/Numbered #1", items));
            Assert.AreEqual(MenuItemFunctions.MenuNodeKind.Leaf, MenuItemFunctions.ClassifyMenuNode("GameObject/Create Empty", items));
            Assert.AreEqual(MenuItemFunctions.MenuNodeKind.Leaf, MenuItemFunctions.ClassifyMenuNode("Tools/Do Thing", items));
            Assert.AreEqual(MenuItemFunctions.MenuNodeKind.Unknown, MenuItemFunctions.ClassifyMenuNode("Window/Layouts", items));
            Assert.AreEqual(MenuItemFunctions.MenuNodeKind.Unknown, MenuItemFunctions.ClassifyMenuNode("Edit/Undo", items));
            Assert.AreEqual(MenuItemFunctions.MenuNodeKind.Unknown, MenuItemFunctions.ClassifyMenuNode("GameObject/Create", items));
            Assert.AreEqual(MenuItemFunctions.MenuNodeKind.Unknown, MenuItemFunctions.ClassifyMenuNode("GameObject/2D Object", new string[0]));
        }

        [Test]
        public void TheEditorCanSayWhetherAMenuItemExists()
        {
            var edit = MenuItemFunctions.MenuItemExists("Edit/Undo");
            Assume.That(edit.HasValue, "UnityEditor.Menu.MenuItemExists is not reachable in this editor.");

            Assert.IsTrue(edit.Value);
            Assert.AreEqual(false, MenuItemFunctions.MenuItemExists("KwNoSuchMenu/NoSuchItem"));
        }
    }
}
