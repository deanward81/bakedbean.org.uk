using System;
using System.Collections.Generic;
using Pidgin;
using Pidgin.Expression;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

namespace Samples.Lib
{
    public class TagParser
    {
        private static Parser<char, T> Token<T>(Parser<char, T> p) => Try(p).Before(SkipWhitespaces);
        private static Parser<char, string> Token(string token) => Token(String(token));
        private static Parser<char, char> Token(char token) => Token(Char(token));

        private static readonly Parser<char, char> _tagChars = LetterOrDigit.Or(OneOf('-', '#', '_', '+', '*', '.'));

        private static readonly Parser<char, JqlNode> _tag =
            Token(
                _tagChars
                    .AtLeastOnceString()
                    .Between(Char('['), Char(']'))
            )
            .Select(t => JqlBuilder.Tag(t))
            .Labelled("tag");

        private static Parser<char, JqlNode> Parenthesised(Parser<char, JqlNode> parser) =>
            parser.Between(Token('('), Token(')')).Select<JqlNode>(n => new GroupNode(n));

        private static readonly Parser<char, Func<JqlNode, JqlNode, JqlNode>> _and =
            Binary(Token("and").ThenReturn(JqlNodeType.And));
        private static readonly Parser<char, Func<JqlNode, JqlNode, JqlNode>> _or =
            Binary(Token("or").ThenReturn(JqlNodeType.Or));

        private static Parser<char, Func<JqlNode, JqlNode, JqlNode>> Binary(Parser<char, JqlNodeType> op) =>
                op.Select<Func<JqlNode, JqlNode, JqlNode>>(type => (l, r) => new BinaryNode(l, r, type));

        private static readonly Parser<char, Unit> _validTerminators = Lookahead(
            Char(')').ThenReturn(Unit.Value)
        ).Or(End);

        private static readonly Parser<char, JqlNode> _trailingAnd = Try(
            Token("and").Select<JqlNode>(JqlBuilder.Text).Before(_validTerminators)
        );

        private static readonly Parser<char, JqlNode> _trailingOr = Try(
            Token("or").Select<JqlNode>(x => JqlBuilder.Text(x)).Before(_validTerminators)
        );

        private static readonly Parser<char, JqlNode> _expressionParser =
             ExpressionParser.Build<char, JqlNode>(
                p => OneOf(
                    _trailingAnd,
                    _trailingOr,
                    _tag,
                    Parenthesised(p)
                ).AtLeastOnce().Select<JqlNode>(JqlBuilder.Group),
                new[]
                {
                    Operator.InfixL(_and),
                    Operator.InfixL(_or)
                }
            ).AtLeastOnce().Select<JqlNode>(x => new QueryNode(x));

        public static QueryNode Parse(string input) => (QueryNode)_expressionParser.ParseOrThrow(input).CollapseNodes();
    }
}
