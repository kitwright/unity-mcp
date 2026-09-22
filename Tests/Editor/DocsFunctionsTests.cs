// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class DocsFunctionsTests
    {
        [Test]
        public void DocVersion_StripsPatchAndSuffix()
        {
            Assert.AreEqual("2022.3", DocsFunctions.DocVersion("2022.3.15f1"));
            Assert.AreEqual("6000.0", DocsFunctions.DocVersion("6000.0.23f1"));
        }

        [Test]
        public void DocVersion_HandlesMajorMinorOnly()
        {
            Assert.AreEqual("2021.2", DocsFunctions.DocVersion("2021.2"));
        }

        [Test]
        public void DocVersion_NoDotReturnsAsIs()
        {
            Assert.AreEqual("2022", DocsFunctions.DocVersion("2022"));
        }

        [Test]
        public void HtmlToText_DropsNavBeforeHeadingAndFooterAfter()
        {
            const string html =
                "<html><body><div class=\"sidebar\"><a href=\"x\">Scripting API</a><a href=\"y\">Transform</a></div>" +
                "<h1>Physics.Raycast</h1><p>Casts a ray.</p>" +
                "<div class=\"footer\">Copyright Unity Technologies</div></body></html>";

            var text = DocsFunctions.HtmlToText(html);

            StringAssert.Contains("Physics.Raycast", text);
            StringAssert.Contains("Casts a ray.", text);
            Assert.IsFalse(text.Contains("Scripting API"), "Left nav should be dropped.");
            Assert.IsFalse(text.Contains("Copyright"), "Footer should be dropped.");
        }

        [Test]
        public void HtmlToText_RemovesScriptAndStyleBlocks()
        {
            const string html =
                "<h1>Rigidbody</h1><script>var nav = 1;</script><style>.x{color:red}</style><p>Control via physics.</p>";

            var text = DocsFunctions.HtmlToText(html);

            StringAssert.Contains("Control via physics.", text);
            Assert.IsFalse(text.Contains("var nav"), "Script body should be dropped.");
            Assert.IsFalse(text.Contains("color:red"), "Style body should be dropped.");
        }

        [Test]
        public void HtmlToText_DecodesEntitiesAndCollapsesWhitespace()
        {
            const string html = "<h1>Mathf</h1><p>a &lt; b &amp;&amp; b &gt; c</p>\n\n\n\n<p>done</p>";

            var text = DocsFunctions.HtmlToText(html);

            StringAssert.Contains("a < b && b > c", text);
            Assert.IsFalse(text.Contains("\n\n\n"), "Blank line runs should collapse.");
        }

        [Test]
        public void HtmlToText_DropsScriptReferenceFeedbackForm()
        {
            const string html =
                "<h1>Physics.Raycast</h1>" +
                "<div class=\"scrollToFeedback\"><a>Leave feedback</a></div>" +
                "<div class=\"suggest\"><p>Thank you for helping us improve the quality of Unity Documentation.</p>" +
                "<button>Submit suggestion</button></div>" +
                "<div class=\"subsection\"><p>Casts a ray.</p></div>";

            var text = DocsFunctions.HtmlToText(html);

            StringAssert.Contains("Physics.Raycast", text);
            StringAssert.Contains("Casts a ray.", text);
            Assert.IsFalse(text.Contains("Leave feedback"), "Feedback form should be dropped.");
            Assert.IsFalse(text.Contains("Submit suggestion"), "Suggestion form should be dropped.");
        }

        [Test]
        public void HtmlToText_KeepsFeedbackRegionWhenNoSubsectionFollows()
        {
            const string html =
                "<h1>Some.Page</h1><div class=\"scrollToFeedback\"><a>Leave feedback</a></div><p>Body text.</p>";

            var text = DocsFunctions.HtmlToText(html);

            StringAssert.Contains("Body text.", text);
        }

        // The footer anchor sits inside the tag, so cutting at it used to leave a "<div " fragment
        // the tag stripper could not match, and it surfaced at the end of every page's text.
        [Test]
        public void HtmlToText_CutsAtTheFooterTagNotTheAttributeInsideIt()
        {
            const string html = "<h1>Mathf</h1><p>Body.</p><div class=\"footer\">Copyright</div>";

            var text = DocsFunctions.HtmlToText(html);

            StringAssert.Contains("Body.", text);
            Assert.IsFalse(text.Contains("<div"), "Footer tag fragment leaked into the text: " + text);
        }

        [Test]
        public void ExtractExamples_ReturnsEachCodeBlockDecodedAndTagFree()
        {
            const string html =
                "<h1>Physics.Raycast</h1>" +
                "<pre class=\"codeExampleCS\">if (a &lt; b)\n    <span class=\"kw\">return</span>;</pre>" +
                "<p>prose</p>" +
                "<pre class=\"codeExampleCS\">Debug.Log(&quot;hi&quot;);</pre>";

            var examples = DocsFunctions.ExtractExamples(html);

            Assert.AreEqual(2, examples.Length);
            Assert.AreEqual("if (a < b)\n    return;", examples[0]);
            Assert.AreEqual("Debug.Log(\"hi\");", examples[1]);
        }

        [Test]
        public void ExtractExamples_IgnoresMarkupWithNoCodeBlocks()
        {
            Assert.IsEmpty(DocsFunctions.ExtractExamples("<h1>Mathf</h1><p>No code here.</p>"));
            Assert.IsEmpty(DocsFunctions.ExtractExamples(null));
        }

        // Markup that never reaches the trimming anchors: no heading to cut to, or nothing at all.
        [Test]
        public void HtmlToText_HandlesDegenerateInput()
        {
            StringAssert.Contains("No heading here.", DocsFunctions.HtmlToText("<p>No heading here.</p>"));
            Assert.AreEqual(string.Empty, DocsFunctions.HtmlToText(null));
            Assert.AreEqual(string.Empty, DocsFunctions.HtmlToText(string.Empty));
        }
    }
}
