// Copyright (C) KitWright. Licensed under MIT.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("KitWright.Editor.Tests")]
[assembly: InternalsVisibleTo("KitWright.Editor.InputSystem")]
[assembly: InternalsVisibleTo("KitWright.Editor.Timeline")]
[assembly: InternalsVisibleTo("KitWright.Editor.Navigation")]
[assembly: InternalsVisibleTo("KitWright.Editor.ProjectAuditor")]
[assembly: InternalsVisibleTo("KitWright.Editor.Pro")]
// The add-on's tests assert what its tools look like through this package's own registry and export
// policy, both internal. Without this they would need a second copy of ToolRegistry.ToSnakeCase and
// would pass while the real tool name differed.
[assembly: InternalsVisibleTo("KitWright.Editor.Pro.Tests")]
