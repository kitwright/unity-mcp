// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace KitWright.Editor.MCP.Server
{
    internal sealed class IntegrationsPanel : IMCPWindowPanel
    {
        private struct Integration
        {
            public string Name;
            public string PackageId;
            public string AssemblyPrefix;
            public string Description;
            public string Url;
            public string Icon;
            public Color Accent;
            public string[] Tools;
            public bool EnhancesOnly;
        }

        private static readonly Integration[] Integrations =
        {
            new Integration
            {
                Name = "Hot Reload",
                PackageId = "com.singularity-group.hot-reload",
                AssemblyPrefix = "SingularityGroup.HotReload",
                Description = "Patches script edits into the running editor without domain reloads. MCP detects it and skips fallback recompiles — tool calls stay fast, Play Mode state survives.",
                Url = "https://hotreload.net/",
                Icon = "d_Refresh",
                Accent = new Color(1f, 0.55f, 0.2f),
                Tools = new[] { "request_recompile", "execute_code", "wait_for_compilation" },
                EnhancesOnly = true
            },
            new Integration
            {
                Name = "Memory Profiler",
                PackageId = "com.unity.memoryprofiler",
                Description = "Full .snap workflow: capture real snapshots and query object-level reference chains — 'who keeps this texture alive'.",
                Url = "https://docs.unity3d.com/Packages/com.unity.memoryprofiler@latest",
                Icon = "d_Profiler.Memory",
                Accent = new Color(0.75f, 0.45f, 0.95f),
                Tools = new[] { "memory_take_full_snapshot", "memory_query_top_objects", "memory_query_references" }
            },
            new Integration
            {
                Name = "Addressables",
                PackageId = "com.unity.addressables",
                Description = "Mark assets addressable, manage addresses, labels and groups.",
                Url = "https://docs.unity3d.com/Packages/com.unity.addressables@latest",
                Icon = "d_Package Manager",
                Accent = new Color(0.3f, 0.75f, 0.55f),
                Tools = new[] { "mark_addressable", "set_addressable_address", "set_addressable_label", "list_addressable_groups" }
            },
            new Integration
            {
                Name = "Input System",
                PackageId = "com.unity.inputsystem",
                Description = ".inputactions authoring and Play Mode input simulation.",
                Url = "https://docs.unity3d.com/Packages/com.unity.inputsystem@latest",
                Icon = "d_UnityEditor.GameView",
                Accent = new Color(0.35f, 0.6f, 0.95f),
                Tools = new[] { "create_input_actions", "add_input_binding", "simulate_key_press", "simulate_mouse_drag" }
            },
            new Integration
            {
                Name = "Timeline",
                PackageId = "com.unity.timeline",
                Description = "Scrub a PlayableDirector's timeline to any time in the editor.",
                Url = "https://docs.unity3d.com/Packages/com.unity.timeline@latest",
                Icon = "d_UnityEditor.Timeline.TimelineWindow",
                Accent = new Color(0.9f, 0.75f, 0.3f),
                Tools = new[] { "director_evaluate" }
            },
            new Integration
            {
                Name = "Universal Render Pipeline",
                PackageId = "com.unity.render-pipelines.universal",
                Description = "Post-processing volume authoring: Bloom, Tonemapping, Vignette and more.",
                Url = "https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@latest",
                Icon = "d_Camera Icon",
                Accent = new Color(0.95f, 0.4f, 0.5f),
                Tools = new[] { "create_volume", "add_volume_override", "set_volume_override_property" }
            },
            new Integration
            {
                Name = "Test Framework",
                PackageId = "com.unity.test-framework",
                Description = "Asynchronous EditMode and PlayMode test runs with progress polling.",
                Url = "https://docs.unity3d.com/Packages/com.unity.test-framework@latest",
                Icon = "d_Valid",
                Accent = new Color(0.45f, 0.85f, 0.5f),
                Tools = new[] { "run_tests", "get_test_job", "cancel_test_run" }
            },
        };

        public void Build(VisualElement container)
        {
            var packages = PackageInfo.GetAllRegisteredPackages();
            var installedCount = Integrations.Count(i => Detect(i, packages) != null);

            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;
            headerRow.style.marginBottom = 4;

            var title = MCPSection.PanelTitle("Integrations");
            title.style.marginBottom = 0;
            title.style.flexGrow = 1;
            headerRow.Add(title);

            var counter = new Label(installedCount + " / " + Integrations.Length + " active");
            counter.style.fontSize = 12;
            counter.style.unityFontStyleAndWeight = FontStyle.Bold;
            counter.style.color = new Color(0.45f, 0.85f, 0.5f);
            counter.style.backgroundColor = new Color(0.16f, 0.28f, 0.18f);
            counter.Rounded(10);
            counter.Padding(3, 10, 3, 10);
            headerRow.Add(counter);
            container.Add(headerRow);

            container.Add(MCPSection.PanelHint("Optional packages KitWright MCP detects and integrates with. Installing one unlocks its tools automatically."));

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            container.Add(scroll);

            var unityIntegrations = Integrations.Where(i => i.PackageId.StartsWith("com.unity.", StringComparison.OrdinalIgnoreCase)).ToArray();
            var thirdPartyIntegrations = Integrations.Where(i => !i.PackageId.StartsWith("com.unity.", StringComparison.OrdinalIgnoreCase)).ToArray();

            var unityInstalled = unityIntegrations.Count(i => Detect(i, packages) != null);
            var thirdPartyInstalled = thirdPartyIntegrations.Count(i => Detect(i, packages) != null);

            var (unitySection, unityFoldout) = MCPSection.Create("Unity Official Packages", $"{unityInstalled} / {unityIntegrations.Length} active", "IntegrationsUnity");
            foreach (var integration in unityIntegrations)
                unityFoldout.Add(CreateCard(integration, Detect(integration, packages)));
            scroll.Add(unitySection);

            var (thirdPartySection, thirdPartyFoldout) = MCPSection.Create("Third-Party Packages", $"{thirdPartyInstalled} / {thirdPartyIntegrations.Length} active", "IntegrationsThirdParty");
            foreach (var integration in thirdPartyIntegrations)
                thirdPartyFoldout.Add(CreateCard(integration, Detect(integration, packages)));
            scroll.Add(thirdPartySection);
        }

        private static Button CreateInstallButton(Integration integration, bool isUnity)
        {
            var button = new Button { text = "Install" };
            button.style.fontSize = 11;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.height = 22;
            button.style.paddingLeft = 10;
            button.style.paddingRight = 10;
            button.style.marginTop = 0;
            button.style.marginBottom = 0;
            button.style.marginRight = 0;
            button.Rounded(4);
            button.style.backgroundColor = new Color(0.20f, 0.45f, 0.75f);
            button.style.color = Color.white;

            button.clicked += () =>
            {
                if (!isUnity)
                {
                    EditorUtility.DisplayDialog(
                        "3rd-Party Integration",
                        $"'{integration.Name}' is a third-party package.\n\nPlease visit {integration.Url} to install it.",
                        "OK");
                    return;
                }

                button.text = "Installing...";
                button.SetEnabled(false);
                PackageInstaller.Install(integration.PackageId, (ok, error) =>
                {
                    if (ok)
                    {
                        button.text = "Installed";
                        return;
                    }

                    button.text = "Install";
                    button.SetEnabled(true);
                    EditorUtility.DisplayDialog("Package Install Error", error ?? "Unknown error", "OK");
                });
            };

            return button;
        }

        private static Texture2D LoadIcon(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try { return EditorGUIUtility.IconContent(name)?.image as Texture2D; }
            catch { return null; }
        }

        private static string Detect(Integration integration, PackageInfo[] packages)
        {
            var match = packages.FirstOrDefault(p => string.Equals(p.name, integration.PackageId, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match.version;

            if (!string.IsNullOrEmpty(integration.AssemblyPrefix))
            {
                var asmName = integration.AssemblyPrefix;
                var found = AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => a.GetName().Name.StartsWith(asmName, StringComparison.OrdinalIgnoreCase));
                if (found)
                {
                    return "";
                }
            }

            return null;
        }

        private VisualElement CreateCard(Integration integration, string installedVersion)
        {
            var installed = installedVersion != null;

            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            var normalBg = new Color(0.17f, 0.17f, 0.18f);
            var hoverBg = new Color(0.20f, 0.20f, 0.22f);
            var accent = integration.Accent;

            card.style.backgroundColor = normalBg;
            card.Rounded(6);
            card.Border(1, MCPPalette.BorderDark);
            card.style.marginBottom = 6;
            card.style.overflow = Overflow.Hidden;
            card.style.transitionProperty = new System.Collections.Generic.List<StylePropertyName> { "background-color" };
            card.style.transitionDuration = new System.Collections.Generic.List<TimeValue> { new TimeValue(0.1f, TimeUnit.Second) };
            card.RegisterCallback<MouseEnterEvent>(_ => card.style.backgroundColor = hoverBg);
            card.RegisterCallback<MouseLeaveEvent>(_ => card.style.backgroundColor = normalBg);

            var strip = new VisualElement();
            strip.style.width = 3;
            strip.style.flexShrink = 0;
            strip.style.backgroundColor = accent;
            card.Add(strip);

            var iconBox = new VisualElement();
            iconBox.style.width = 46;
            iconBox.style.flexShrink = 0;
            iconBox.style.justifyContent = Justify.Center;
            iconBox.style.alignItems = Align.Center;

            var iconBadge = new VisualElement();
            iconBadge.style.width = 30;
            iconBadge.style.height = 30;
            iconBadge.Rounded(6);
            iconBadge.style.justifyContent = Justify.Center;
            iconBadge.style.alignItems = Align.Center;
            iconBadge.style.backgroundColor = installed
                ? new Color(accent.r * 0.22f, accent.g * 0.22f, accent.b * 0.22f, 0.8f)
                : new Color(0.18f, 0.18f, 0.20f, 0.6f);
            iconBadge.Border(1, installed
                ? new Color(accent.r * 0.45f, accent.g * 0.45f, accent.b * 0.45f, 0.6f)
                : new Color(0.24f, 0.24f, 0.26f, 0.5f));

            var iconTex = LoadIcon(integration.Icon) ?? LoadIcon("d_Package Manager");
            if (iconTex != null)
            {
                var iconEl = new Image { image = iconTex, scaleMode = ScaleMode.ScaleToFit };
                iconEl.style.width = 18;
                iconEl.style.height = 18;
                if (!installed)
                    iconEl.style.opacity = 0.5f;
                iconBadge.Add(iconEl);
            }
            iconBox.Add(iconBadge);
            card.Add(iconBox);

            var body = new VisualElement();
            body.style.flexGrow = 1;
            body.Padding(8, 10, 8, 2);
            card.Add(body);

            var titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.alignItems = Align.Center;
            titleRow.style.marginBottom = 3;

            var nameLabel = new Label(integration.Name);
            nameLabel.style.fontSize = 13;
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.color = installed ? new Color(0.92f, 0.92f, 0.94f) : MCPPalette.TextDim;
            titleRow.Add(nameLabel);

            var isUnity = integration.PackageId.StartsWith("com.unity.", StringComparison.OrdinalIgnoreCase);
            var originBadge = new Label(isUnity ? "Unity Official" : "3rd Party");
            originBadge.style.fontSize = 9;
            originBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
            originBadge.style.color = isUnity ? new Color(0.65f, 0.85f, 1f) : new Color(1f, 0.80f, 0.40f);
            originBadge.style.backgroundColor = isUnity ? new Color(0.15f, 0.25f, 0.40f) : new Color(0.40f, 0.25f, 0.10f);
            originBadge.Rounded(3);
            originBadge.Padding(1, 5, 1, 5);
            originBadge.style.marginLeft = 6;
            titleRow.Add(originBadge);

            var idLabel = new Label(integration.PackageId);
            idLabel.style.fontSize = 10;
            idLabel.style.color = new Color(0.45f, 0.45f, 0.45f);
            idLabel.style.marginLeft = 6;
            idLabel.style.flexGrow = 1;
            titleRow.Add(idLabel);

            var badge = new Label(installed
                ? (installedVersion == "" ? "Active" : "v" + installedVersion)
                : "Not installed");
            badge.style.fontSize = 11;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.color = installed ? new Color(0.5f, 0.9f, 0.55f) : new Color(0.55f, 0.55f, 0.55f);
            badge.style.marginRight = 5;
            titleRow.Add(badge);

            var dot = new VisualElement();
            dot.style.width = 7;
            dot.style.height = 7;
            dot.Rounded(4);
            dot.style.backgroundColor = installed ? new Color(0.35f, 0.85f, 0.45f) : new Color(0.45f, 0.45f, 0.45f);
            titleRow.Add(dot);

            body.Add(titleRow);

            var desc = new Label(integration.Description);
            desc.style.fontSize = 12;
            desc.style.color = installed ? new Color(0.72f, 0.72f, 0.72f) : new Color(0.55f, 0.55f, 0.55f);
            desc.style.whiteSpace = WhiteSpace.Normal;
            desc.style.marginBottom = 6;
            if (!installed)
                desc.style.marginRight = 72;
            body.Add(desc);

            var chipRow = new VisualElement();
            chipRow.style.flexDirection = FlexDirection.Row;
            chipRow.style.alignItems = Align.Center;
            chipRow.style.justifyContent = Justify.SpaceBetween;

            var leftChips = new VisualElement();
            leftChips.style.flexDirection = FlexDirection.Row;
            leftChips.style.flexWrap = Wrap.Wrap;
            leftChips.style.alignItems = Align.Center;
            leftChips.style.flexGrow = 1;
            leftChips.style.flexShrink = 1;

            var relation = new Label(integration.EnhancesOnly ? "Speeds up:" : "Unlocks:");
            relation.style.fontSize = 10;
            relation.style.unityFontStyleAndWeight = FontStyle.Bold;
            relation.style.color = new Color(0.55f, 0.55f, 0.55f);
            relation.style.marginRight = 5;
            relation.style.marginBottom = 3;
            leftChips.Add(relation);

            foreach (var tool in integration.Tools)
            {
                var chip = new Label(tool);
                chip.style.fontSize = 10;
                chip.style.color = installed
                    ? new Color(accent.r * 0.8f + 0.2f, accent.g * 0.8f + 0.2f, accent.b * 0.8f + 0.2f)
                    : new Color(0.5f, 0.5f, 0.5f);
                chip.style.backgroundColor = installed
                    ? new Color(accent.r * 0.18f, accent.g * 0.18f, accent.b * 0.18f)
                    : new Color(0.19f, 0.19f, 0.19f);
                chip.Rounded(8);
                chip.Border(1, installed
                    ? new Color(accent.r * 0.35f, accent.g * 0.35f, accent.b * 0.35f)
                    : new Color(0.25f, 0.25f, 0.25f));
                chip.Padding(2, 7, 2, 7);
                chip.style.marginRight = 4;
                chip.style.marginBottom = 3;
                leftChips.Add(chip);
            }

            chipRow.Add(leftChips);

            var link = new Label("docs ↗");
            link.style.fontSize = 10;
            link.style.color = new Color(0.45f, 0.70f, 1.0f);
            link.Padding(2, 4, 2, 4);
            link.style.marginBottom = 3;
            link.style.marginLeft = 8;
            link.style.flexShrink = 0;
            link.RegisterCallback<ClickEvent>(evt =>
            {
                Application.OpenURL(integration.Url);
                evt.StopPropagation();
            });
            chipRow.Add(link);

            body.Add(chipRow);

            if (!installed)
            {
                var installBox = new VisualElement();
                installBox.style.position = Position.Absolute;
                installBox.style.right = 10;
                installBox.style.top = 0;
                installBox.style.bottom = 0;
                installBox.style.justifyContent = Justify.Center;
                installBox.Add(CreateInstallButton(integration, isUnity));
                card.Add(installBox);
            }

            return card;
        }

        public void Dispose()
        {
        }
    }
}
