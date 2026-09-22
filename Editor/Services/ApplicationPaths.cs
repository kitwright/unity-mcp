// Copyright (C) KitWright. All rights reserved.

using System.IO;
using UnityEngine;

namespace KitWright.Editor.Services
{
    internal static class ApplicationPaths
    {
        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
        public static string AssetsPath => Application.dataPath;
    }
}
