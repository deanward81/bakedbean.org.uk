using System.Collections.Generic;
using Pidgin;
using Samples.Lib;
using Xunit;

namespace Samples.Tests
{
    public class TagParserTests
    {
        [Theory]
        [MemberData(nameof(GetData), parameters: 3)]

        public void ParserTests(string input, bool shouldFail, ExpectedJql expected)
        {
            if (shouldFail)
            {
                Assert.Throws<ParseException>(() => TagParser.Parse(input));
            }
            else
            {
                var jql = TagParser.Parse(input);
                var actual = new ExpectedJql(jql);
                Assert.True(expected.Equals(actual));
            }
        }

        public static IEnumerable<object[]> GetData(int numTests)
        {
            // good input
            yield return new object[] { "[c#]", false, new ExpectedJql(JqlBuilder.Query(JqlBuilder.Tag("c#"))) };
            yield return new object[] { "[sql-server]", false, new ExpectedJql(JqlBuilder.Query(JqlBuilder.Tag("sql-server"))) };
            yield return new object[] { "[sql-server] or [c#]", false, new ExpectedJql(JqlBuilder.Query(JqlBuilder.Tag("sql-server").Or(JqlBuilder.Tag("c#")))) };
            yield return new object[] { "[sql-server] and [c#]", false, new ExpectedJql(JqlBuilder.Query(JqlBuilder.Tag("sql-server").And(JqlBuilder.Tag("c#")))) };
            yield return new object[] { "[javascript] or [reactjs] and [nodejs]", false, new ExpectedJql(JqlBuilder.Query(JqlBuilder.Tag("javascript").Or(JqlBuilder.Tag("reactjs").And(JqlBuilder.Tag("nodejs"))))) };
            yield return new object[] { "[php] and ([mysql] or [postgres])", false, new ExpectedJql(JqlBuilder.Query(JqlBuilder.Tag("php").And(JqlBuilder.Tag("mysql").Or(JqlBuilder.Tag("postgres"))))) };

            // invalid tags - expect these to throw
            yield return new object[] { "[with space]", true, null };
            yield return new object[] { "[invalid&chars]", true, null };

            // bad input
            yield return new object[] { "[c#] or", false, new ExpectedJql(JqlBuilder.Query(JqlBuilder.Tag("c#"), JqlBuilder.Text("or"))) };
            yield return new object[] { "[c#] and", false, new ExpectedJql(JqlBuilder.Query(JqlBuilder.Tag("c#"), JqlBuilder.Text("and"))) };
            yield return new object[] { "([sql-server] or [php] and)", false, new ExpectedJql(JqlBuilder.Query(JqlBuilder.Tag("sql-server").Or(JqlBuilder.Group(JqlBuilder.Tag("php"), JqlBuilder.Text("and"))))) };
        }
    }
}
