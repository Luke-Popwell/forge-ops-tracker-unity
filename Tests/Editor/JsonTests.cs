using System.Collections.Generic;
using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    public class JsonTests
    {
        [Test]
        public void Encodes_primitives()
        {
            Assert.AreEqual("null", Json.Encode(null));
            Assert.AreEqual("true", Json.Encode(true));
            Assert.AreEqual("false", Json.Encode(false));
            Assert.AreEqual("42", Json.Encode(42));
        }

        [Test]
        public void Encodes_and_escapes_strings()
        {
            Assert.AreEqual("\"hello\"", Json.Encode("hello"));
            Assert.AreEqual("\"line1\\nline2\"", Json.Encode("line1\nline2"));
            Assert.AreEqual("\"say \\\"hi\\\"\"", Json.Encode("say \"hi\""));
        }

        [Test]
        public void Encodes_a_dictionary_as_a_json_object()
        {
            var input = new Dictionary<string, object> { ["a"] = 1, ["b"] = "two" };
            Assert.AreEqual("{\"a\":1,\"b\":\"two\"}", Json.Encode(input));
        }

        [Test]
        public void Encodes_a_list_as_a_json_array()
        {
            var input = new List<object> { 1, "two", null };
            Assert.AreEqual("[1,\"two\",null]", Json.Encode(input));
        }

        [Test]
        public void Encodes_a_nested_structure_matching_the_event_payload_shape()
        {
            var payload = new Dictionary<string, object>
            {
                ["exception_class"] = "System.Exception",
                ["backtrace"] = new List<object>
                {
                    new Dictionary<string, object> { ["file"] = null, ["line"] = 42, ["in_app"] = true }
                }
            };

            Assert.AreEqual(
                "{\"exception_class\":\"System.Exception\",\"backtrace\":[{\"file\":null,\"line\":42,\"in_app\":true}]}",
                Json.Encode(payload));
        }
    }
}
