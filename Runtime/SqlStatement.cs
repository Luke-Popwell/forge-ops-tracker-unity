using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// Finds the SQL behind a database error and reduces it to something safe to send: the names of
    /// the stored procedures, tables and views it touched, and (only if
    /// <see cref="Configuration.CaptureSqlStatement"/> is on) the statement itself with every string
    /// and number replaced by "?". Ported from sdks/dotnet's SqlStatement, which is itself ported from
    /// gems/forge_ops_tracker's, which is ported from the server's own SqlStatementMasker/SqlObjectExtractor: same rules everywhere, and
    /// the server applies them again on arrival, so a difference here can only ever mean less is
    /// masked client-side, never that something unmasked gets stored.
    ///
    /// Deliberately a single pass over a few patterns, not a SQL parser.
    /// </summary>
    public static class SqlStatement
    {
        /// <summary>
        /// The <see cref="Exception.Data"/> key under which a caller can attach the failing SQL
        /// statement to any exception: <c>exception.Data[SqlStatement.ExplicitDataKey] = query;</c>.
        /// </summary>
        public const string ExplicitDataKey = "forge_ops_sql";

        public const string Mask = "?";
        public const int MaxLength = 4000;
        private const int MaxNames = 10;
        private const int MaxNameLength = 200;
        private const int MaxCauseDepth = 5;

        private static readonly Regex Literal = new(
            @"'(?:[^']|'')*(?:'|\z)|(?<tag>\$[A-Za-z_]*\$).*?(?:\k<tag>|\z)|(?<![\w$.])\d+(?:\.\d+)?(?!\w)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private const string Part = @"(?:[\w$#@]+|""[^""]+""|\[[^\]]+\]|`[^`]+`)";
        private const string Name = Part + @"(?:\." + Part + ")*";
        private static readonly HashSet<string> Operations = new()
        {
            "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "WITH", "CALL", "EXEC", "EXECUTE", "CREATE", "ALTER", "DROP", "TRUNCATE"
        };
        private static readonly Regex ProcedureCall = new(
            @"\b(?:CALL|EXEC(?:UTE)?|PERFORM)\s+(?!IMMEDIATE\b|FUNCTION\b|PROCEDURE\b)(" + Name + ")",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Relation = new(
            @"\b(FROM|JOIN|INTO|UPDATE|TABLE)\s+(" + Name + @")(\s*\()?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SelectFunction = new(
            @"\A\s*SELECT\s+(" + Name + @")\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Builtins = new()
        {
            "count", "sum", "min", "max", "avg", "now", "coalesce", "nullif", "lower", "upper", "length", "concat",
            "cast", "date_trunc", "current_timestamp", "current_date", "row_number", "rank", "json_build_object",
            "json_agg", "array_agg"
        };
        private static readonly Regex FromInsideFunction = new(
            @"\b(?:EXTRACT|SUBSTRING|TRIM|OVERLAY)\s*\([^()]*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly HashSet<string> KeywordsNotNames = new()
        {
            "select", "set", "values", "where", "lateral", "only", "unnest", "generate_series"
        };
        private static readonly Regex FullName = new(@"\A" + Name + @"\z", RegexOptions.CultureInvariant);
        private static readonly Regex FromWord = new(@"\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex FirstWord = new(@"\A\s*(\w+)", RegexOptions.CultureInvariant);

        // Public properties database libraries put the failing statement on: Npgsql's
        // PostgresException.Statement, and the Sql/CommandText/Query names other providers and wrappers
        // use. Read reflectively so this client never depends on any provider. Only a string counts.
        private static readonly string[] StatementProperties = { "Statement", "Sql", "SQL", "CommandText", "Query" };

        /// <summary>
        /// The raw statement off the exception itself or, for an app that wraps a database error in its
        /// own exception, off whatever it was raised from (InnerException, or every branch of an
        /// AggregateException). Npgsql's NpgsqlException also carries the batch command that failed.
        /// </summary>
        public static string? FindIn(Exception? exception)
        {
            var seen = new HashSet<Exception>();
            var queue = new Queue<Exception?>();
            queue.Enqueue(exception);
            var visited = 0;
            while (queue.Count > 0 && visited < MaxCauseDepth * 3)
            {
                var current = queue.Dequeue();
                visited++;
                if (current is null || !seen.Add(current))
                {
                    continue;
                }

                var statement = StatementOf(current);
                if (statement is not null)
                {
                    return statement;
                }

                queue.Enqueue(current.InnerException);
                if (current is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        queue.Enqueue(inner);
                    }
                }
            }

            return null;
        }

        private static string? StatementOf(Exception exception)
        {
            // The explicit channel for code that catches a database library's exception and rethrows or
            // reports it: exception.Data["forge_ops_sql"] = query. Works for any exception type,
            // including ones from providers that expose nothing themselves.
            if (exception.Data.Contains(ExplicitDataKey) && exception.Data[ExplicitDataKey] is string explicitSql
                && !string.IsNullOrWhiteSpace(explicitSql))
            {
                return explicitSql;
            }

            var direct = StringProperty(exception);
            if (direct is not null)
            {
                return direct;
            }

            var batchCommand = exception.GetType().GetProperty("BatchCommand", BindingFlags.Public | BindingFlags.Instance)?.GetValue(exception);
            return batchCommand is null ? null : StringProperty(batchCommand);
        }

        private static string? StringProperty(object target)
        {
            foreach (var name in StatementProperties)
            {
                try
                {
                    var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (property?.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0
                        && property.GetValue(target) is string value && !string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
                catch (Exception e) when (e is TargetInvocationException or AmbiguousMatchException or MethodAccessException)
                {
                    // Not a readable string property on this type: not a source of SQL, keep looking.
                }
            }

            return null;
        }

        public static string? MaskStatement(string? statement)
        {
            if (string.IsNullOrWhiteSpace(statement))
            {
                return null;
            }

            var masked = Literal.Replace(statement, Mask);
            return masked.Length > MaxLength ? masked.Substring(0, MaxLength) + "..." : masked;
        }

        /// <summary>
        /// Takes an already-masked statement (so a keyword inside a string value can't be mistaken for
        /// SQL). Returns null when nothing recognizable was found.
        /// </summary>
        public static Dictionary<string, object?>? Objects(string? masked)
        {
            if (string.IsNullOrWhiteSpace(masked))
            {
                return null;
            }

            var sql = FromInsideFunction.Replace(masked, " ");
            var procedures = ProcedureCall.Matches(sql).Select(m => m.Groups[1].Value).ToList();
            var relations = new List<string>();

            foreach (Match match in Relation.Matches(sql))
            {
                var name = match.Groups[2].Value;
                if (KeywordsNotNames.Contains(name.ToLowerInvariant()))
                {
                    continue;
                }

                var keyword = match.Groups[1].Value.ToUpperInvariant();
                var functionCall = match.Groups[3].Success && (keyword == "FROM" || keyword == "JOIN");
                (functionCall ? procedures : relations).Add(name);
            }

            var select = SelectFunction.Match(sql);
            if (select.Success && !Builtins.Contains(select.Groups[1].Value.ToLowerInvariant()) && !FromWord.IsMatch(sql))
            {
                procedures.Add(select.Groups[1].Value);
            }

            var first = FirstWord.Match(sql);
            var operation = first.Success ? first.Groups[1].Value.ToUpperInvariant() : "";
            var result = new Dictionary<string, object?>();
            if (Operations.Contains(operation))
            {
                result["operation"] = operation;
            }

            var cleanProcedures = Clean(procedures);
            var cleanRelations = Clean(relations);
            result["procedures"] = cleanProcedures;
            result["relations"] = cleanRelations;
            if (cleanProcedures.Count == 0 && cleanRelations.Count == 0 && !result.ContainsKey("operation"))
            {
                return null;
            }

            return result;
        }

        private static List<string> Clean(IEnumerable<string> names)
        {
            var cleaned = new List<string>();
            foreach (var raw in names)
            {
                var name = raw.Trim();
                if (name.Length > MaxNameLength)
                {
                    name = name.Substring(0, MaxNameLength);
                }

                if (FullName.IsMatch(name) && !cleaned.Contains(name))
                {
                    cleaned.Add(name);
                }
            }

            return cleaned.Take(MaxNames).ToList();
        }
    }
}
