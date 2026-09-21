using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    // Mirrors sdks/dotnet's SqlStatementTests.
    public class SqlStatementTests
    {
        // Stands in for Npgsql's PostgresException, which exposes the statement as .Statement.
        private sealed class FakePostgresException : Exception
        {
            public FakePostgresException(string message, string statement) : base(message) => Statement = statement;

            public string Statement { get; }
        }

        private static Configuration NewConfiguration(bool objects = true, bool statement = false) => new Configuration
        {
            EnvironmentName = "production",
            CaptureSqlObjects = objects,
            CaptureSqlStatement = statement
        };

        [Test]
        public void FindIn_reads_a_Statement_property_and_the_explicit_Data_key_and_walks_inner_exceptions()
        {
            Assert.AreEqual("SELECT 1", SqlStatement.FindIn(new FakePostgresException("boom", "SELECT 1")));

            var tagged = new InvalidOperationException("boom");
            tagged.Data[SqlStatement.ExplicitDataKey] = "SELECT 2";
            Assert.AreEqual("SELECT 2", SqlStatement.FindIn(tagged));

            var wrapped = new InvalidOperationException("refund failed", new FakePostgresException("db", "CALL refund_order(1)"));
            Assert.AreEqual("CALL refund_order(1)", SqlStatement.FindIn(wrapped));

            Assert.IsNull(SqlStatement.FindIn(new InvalidOperationException("nope")));
            Assert.IsNull(SqlStatement.FindIn(null));
        }

        [Test]
        public void MaskStatement_replaces_strings_and_numbers_but_not_identifiers_or_placeholders()
        {
            Assert.AreEqual(
                "SELECT * FROM orders2 WHERE email = ? AND id = ? AND x = $1",
                SqlStatement.MaskStatement("SELECT * FROM orders2 WHERE email = 'a@b.co' AND id = 42 AND x = $1"));
            Assert.AreEqual("EXEC sp_x @t = ?", SqlStatement.MaskStatement("EXEC sp_x @t = 'it''s'"));
            Assert.AreEqual("SELECT ? WHERE n = ?", SqlStatement.MaskStatement("SELECT 1 WHERE n = 'oops"));
            Assert.AreEqual("DO ?", SqlStatement.MaskStatement("DO $b$ BEGIN PERFORM 1; END $b$"));
            Assert.IsNull(SqlStatement.MaskStatement("  "));
        }

        [Test]
        public void MaskStatement_is_idempotent_and_truncates()
        {
            var once = SqlStatement.MaskStatement("SELECT * FROM t WHERE a = 'x' AND b = 9");
            Assert.AreEqual(once, SqlStatement.MaskStatement(once));
            Assert.AreEqual(SqlStatement.MaxLength + 3, SqlStatement.MaskStatement("SELECT " + new string('a', 9000) + " b").Length);
        }

        [Test]
        public void Objects_finds_procedures_views_tables_and_table_functions()
        {
            var found = SqlStatement.Objects("EXEC dbo.sp_refund_order @id = ?");
            Assert.AreEqual("EXEC", found["operation"]);
            Assert.AreEqual(new List<string> { "dbo.sp_refund_order" }, found["procedures"]);
            Assert.AreEqual(new List<string> { "refund_order" }, SqlStatement.Objects("CALL refund_order(?, ?)")["procedures"]);
            Assert.AreEqual(new List<string> { "refund_order" }, SqlStatement.Objects("SELECT refund_order(?, ?)")["procedures"]);
            Assert.AreEqual(
                new List<string> { "v_totals", "public.customers" },
                SqlStatement.Objects("SELECT * FROM v_totals t JOIN public.customers c ON c.id = t.id")["relations"]);
            Assert.AreEqual(new List<string> { "get_open_orders" }, SqlStatement.Objects("SELECT * FROM get_open_orders(?) o")["procedures"]);
        }

        [Test]
        public void Objects_does_not_misread_column_lists_or_builtins_and_returns_null_for_garbage()
        {
            Assert.AreEqual(new List<string>(), SqlStatement.Objects("INSERT INTO audit_log (a) VALUES (?)")["procedures"]);
            Assert.AreEqual(new List<string>(), SqlStatement.Objects("SELECT count(*) FROM orders")["procedures"]);
            Assert.IsNull(SqlStatement.Objects("garbage"));
        }

        [Test]
        public void EventBuilder_sends_the_procedure_name_by_default_and_the_statement_only_when_opted_in()
        {
            var error = new InvalidOperationException("boom");
            error.Data[SqlStatement.ExplicitDataKey] = "EXEC dbo.sp_refund_order @order_id = 8814, @note = 'a@b.co'";

            var payload = EventBuilder.BuildFromException(NewConfiguration(), error);
            var objects = (Dictionary<string, object>)payload["sql_objects"];
            Assert.AreEqual(new List<string> { "dbo.sp_refund_order" }, objects["procedures"]);
            Assert.IsFalse(payload.ContainsKey("sql_statement"));

            payload = EventBuilder.BuildFromException(NewConfiguration(statement: true), error);
            Assert.AreEqual("EXEC dbo.sp_refund_order @order_id = ?, @note = ?", payload["sql_statement"]);

            payload = EventBuilder.BuildFromException(NewConfiguration(objects: false), error);
            Assert.IsFalse(payload.ContainsKey("sql_objects"));
            Assert.IsFalse(payload.ContainsKey("sql_statement"));
            Assert.IsFalse(EventBuilder.BuildFromException(NewConfiguration(), new InvalidOperationException("nope")).ContainsKey("sql_objects"));
        }
    }
}
