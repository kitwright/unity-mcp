// Copyright (C) KitWright. Licensed under MIT.

#if KITWRIGHT_PROJECT_AUDITOR
using System.Collections;
using System.IO;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    public sealed class AuditFunctionsTests
    {
        private const string Output = "Temp/KitWrightAuditTests/out.csv";
        private const float ScanTimeoutSeconds = 300f;

        private static JObject Json(object result) => JObject.FromObject(result);

        [Test]
        public void Audit_RefusesAnUnknownCategory_AndANonCsvOutput()
        {
            Assert.AreEqual("UNKNOWN_CATEGORY", Json(AuditFunctions.Audit("NoSuchCategory")).Value<string>("code"));
            Assert.AreEqual("INVALID_PATH", Json(AuditFunctions.Audit("ProjectSetting", "Temp/KitWrightAuditTests/out.txt")).Value<string>("code"));
        }

        [UnityTest]
        public IEnumerator Audit_ScansTheProjectSettingsIntoACsv()
        {
            if (File.Exists(Output))
                File.Delete(Output);

            var started = Json(AuditFunctions.Audit("ProjectSetting", Output));
            Assert.IsTrue(started.Value<bool>("success"), started.ToString());

            var deadline = Time.realtimeSinceStartup + ScanTimeoutSeconds;
            string status;
            do
            {
                yield return null;
                status = (string)Json(AuditFunctions.AuditStatus())["data"]["status"];
            }
            while (status == "scanning" && Time.realtimeSinceStartup < deadline);

            Assert.AreEqual("completed", status, Json(AuditFunctions.AuditStatus()).ToString());
            Assert.IsTrue(File.Exists(Output));
            StringAssert.StartsWith("Category,Severity,Id,Description,Path,Line", File.ReadAllText(Output));
        }
    }
}
#endif
