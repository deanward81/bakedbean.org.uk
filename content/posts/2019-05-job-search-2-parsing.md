---
title: "Building Stack Overflow Job Search - Parsing Queries"
date: 2019-05-09T00:00:00Z
tags: [.net, jobs, stack-overflow]
images: [img/job-search-cover.png]
---

In [Part 1](/posts/2019-05-job-search-1-intro) we talked about some of the shortcomings of Stack Overflow's job search and how we planned to address them. In this episode we'll dive into how our parser is written.

## Some Background

Most people seem to get scared the moment the words lexer or parser get mentioned. I'd highly recommend watching [Rob Pike's talk](https://youtube.com/watch?v=HxaD_trXwRE) on the Go lexer and parser; it clears up a lot of misconceptions and provides a solid basis for writing a hand-rolled parser.

Intitially we went with a hand-rolled parser instead of something produced by a parser generator like ANTLR because the output produced by ANTLR was large and unwieldy. We also had to customise it to handle malformed input (more on that below) which made the code less than elegant.

Originally I wrote this post about building that hand-rolled parser but trying to explain it concisely wound up being complex and verbose. I think this points to it being hard to grok and maintain so I set out to write an implementation using a [parser combinator](https://en.wikipedia.org/wiki/Parser_combinator) library instead. It turned out good enough (i.e. it passes all tests and performance is relatively close to the original) that I thought I'd write about that instead... 

Onwards, let's talk about the steps we took to build our parser!

## Defining the Grammar

We started with an idea of the language we wanted to implement. We wanted it to be similar to what was available in [Stack Overflow's search](https://stackoverflow.com/help/searching) and for it to support the facets defined upon a job. So we came up with some example queries:

- **Simple Text**: `hello world`
- **Quoted Text**: `"hello world"`
- **Tags**: `[asp.net]`
- **Modifiers**: `salary:10000` or `salary:10000USD`
- **Ranges**: `salary:10000..20000` or `salary:..50000` or `salary:10000..`
- **Expressions**: `[asp.net] or "hello world"` or `[c#] and not [java] and salary:10000..20000`
- **Complex Expressions**: `([asp.net] or "hello world") and (([c#] and not [java]) or salary:10000..)`

We then defined the language using [Extended Backus-Naur Form (EBNF)](https://en.m.wikipedia.org/wiki/Extended_Backus–Naur_form). This is generally referred to as the grammar of the language and EBNF is a syntax used to desribe the individual components of the grammar. Here's a snippet of Jobs Query Language (JQL) in EBNF:

```
<and> ::= 'and' | '&&';
<or> ::= 'or' | '||';
<not> ::= 'not' | '-';
<quote> ::= '"';
<colon> ::= ':';
<lparen> ::= '(';
<rparen> ::= ')';
<parens> ::= <lparen> | <rparen>;
<lbracket> ::= '[';
<rbracket> ::= ']';
<brackets> ::= <lbracket> | <rbracket>;
<string> ::= { <any_character> - (<brackets> | <parens> | <quote>) };
<quoted_string> ::= <quote> { <any_character> - <quote> } <quote>;
<tag> ::= { <letter> | <number> | '-' | '.' | '#' | '+' | '*' };
<modifier> ::= { <letter> | '-' } <colon>;
<range> ::= '.', '.';
<unit> ::= <letter>, <letter>, <letter> | <letter>, <letter> | <letter>;
<literal> ::= <number> | <string> | <quoted_string>;
<term> ::= 
    <modifier>, <colon>, <literal> |
    <lbracket>, <tag>, <rbracket> | 
    <string> | 
    <quoted_string>;
    
<expression> ::= 
    <lparen>, <expression>, <rparen> | 
    <not>, <expression> |
    <expression>, <and>, <expression> | 
    <expression>, <or>, <expression> |
    <term>;
```

Of particular importance is the definition of `<expression>`; this is what allows for the nesting of different parts of the grammar via recursion.

## Building the Parser
 
Once we've defined a grammar, our next step is to break down each rule into a set of mini-parsers. This is what a parser combinator does best so we decided to use [Pidgin](https://github.com/benjamin-hodgson/Pidgin) - it eliminates a lot of the mistakes that are common in writing your own parser and performs very well thanks to Stack's very own parser wizard [Benjamin Hodgson](https://benjamin.pizza/).

In our case we'd like to take an arbitrary string input and return something representing the parsed form of it. A common way of representing the parsed form is as an [abstract syntax tree (AST)](https://en.wikipedia.org/wiki/Abstract_syntax_tree).

JQL is represented using an AST that uses an abstract base class called `JqlNode`. We have implementations that reflect the structure of the grammar. E.g. it consists of a `QueryNode` representing a query which can contain things like `LiteralNode` to represent text/numbers/bools and `ModifierNode` to handle modifiers like `remote:true`. Here's how that looks:

```c#
abstract class JqlNode {}
class QueryNode : JqlNode
{
    public ImmutableArray<JqlNode> Children { get; }
}

class GroupNode : JqlNode
{
    public ImmutableArray<JqlNode> Children { get; }
}

class ModifierNode : JqlNode
{
    // for the remote:true case this would be equal to "remote"
    public string Value { get; }
    // for the remote:true case this would be a "BooleanNode" with its value set to true
    public JqlNode Operand { get; }
}

class LiteralNode<T> : JqlNode
{
    public T Value { get; }
}

class TextNode : LiteralNode<string> { }
class BooleanNode : LiteralNode<bool> { }
```

We can build a parser that takes a `string` (which at its most primitive level is just an array of `char`) and produces a `JqlNode` tree as a result. This is represented in Pidgin using a `Parser<char, JqlNode>` - here's an example of how we define a tag:

```c#
public class JqlParser
{
    private static Parser<char, T> Token<T>(Parser<char, T> p) => Try(p).Before(SkipWhitespaces);
    private static Parser<char, string> Token(string token) => Token(String(token));
    private static Parser<char, char> Token(char token) => Token(Char(token));
    
    private static readonly Parser<char, char> _tagChars = 
        LetterOrDigit.Or(OneOf('-', '#', '_', '+', '*', '.'));

    private static readonly Parser<char, JqlNode> _tag =
        Token(
            _tagChars
                .AtLeastOnceString()
                .Between(Char('['), Char(']'))
        )
        .Select<JqlNode>(t => JqlBuilder.Tag(t))
        .Labelled("tag");
            
    public static JqlNode Parse(string input) => _tag.ParseOrThrow(input);
}
```

Here we can see that we've defined the characters that a tag supports in the `_tagChars` static member; any letter or digit or the `-`, `#`, `+`, `*`, and `.` characters. We've also defined a helper called `Token` that attempts to consume input defined by a parser (using `Try`) skipping whitespace at the start of the input - this means we don't need to worry about handling whitespace in our individual parsers. If the parser fails then `Try` also provides back-tracking so we can try another parser. This becomes important later because it lets our parser recover when it starts parsing an expression that eventually ends up being something else. For example the expression `[hello&world]` might initially look like a tag to the parser above, however, once it hits the `&` our parser discovers there's no way that the input can be a tag (remember that `&` is not a valid character in a tag). In this case `Try` will rewind the input to the `[` character and interpret the expression in another way - but only if there are rules that allow it to do so. If there aren't then the parser fails and an error is returned instead.

Finally we put these pieces together in the `_tag` static member. This parser says that we're expecting a string of tag characters, surrounded by square brackets (`[`, `]`), optionally preceeded by whitespace and that when we get a string of these characters we should `Select` a JQL `TagNode` with the value within it.

We perform this process for each rule in the grammar, re-using combinations of smaller parsers to build more complete parsers. Eventually we get to a point where we have a parser that can handle the entire grammar!

## Something a bit more complex

The tag example is simple but let's extend it to support `and` and `or` operations to allow us to support inputs like:

 - `[c#] and [sql-server]`
 - `[javascript] or [reactjs] and [nodejs]`
 - `[php] and ([mysql] or [postgres])`

This seems like it would be complicated, but fear not, Pidgin makes this really quite easy... Building upon our example above:

```c#
public class JqlParser
{
    private static Parser<char, JqlNode> Parenthesised(Parser<char, JqlNode> parser) => 
        parser.Between(Token('('), Token(')')).Select<JqlNode>(n => new GroupNode(n));

    private static readonly Parser<char, Func<JqlNode, JqlNode, JqlNode>> _and = 
        Binary(Token("and").ThenReturn(JqlNodeType.And));
    private static readonly Parser<char, Func<JqlNode, JqlNode, JqlNode>> _or = 
        Binary(Token("or").ThenReturn(JqlNodeType.Or));

    private static Parser<char, Func<JqlNode, JqlNode, JqlNode>> Binary(Parser<char, JqlNodeType> op) =>
            op.Select<Func<JqlNode, JqlNode, JqlNode>>(type => (l, r) => new BinaryNode(l, r, type));

    private static readonly Parser<char, JqlNode> _expressionParser = 
         ExpressionParser.Build<char, JqlNode>(
            p => OneOf(
                    _tag,
                    Parenthesised(p)
            ).AtLeastOnce().Select<JqlNode>(JqlBuilder.Group),
            new[]
            {
                Operator.InfixL(_and),
                Operator.InfixL(_or)
            }
        ).AtLeastOnce().Select<JqlNode>(x => new QueryNode(x));

    public static QueryNode Parse(string input) => (QueryNode)_expressionParser.ParseOrThrow(input);
}
```

Here we use a Pidgin helper called `ExpressionParser.Build` that handles a lot of the pain associated with parsing more complex expressions;

 - Its first parameter takes a `Func<Parser, Parser>` that returns a parser that can be used for parsing individual terms in the expression. Here we say that we either accept a tag *or* something that matches any term defined here wrapped in parenthesis. This is important because it allows us to trivially handle recursion in the grammar.
 - Its second parameter takes an array of operators that can be applied to individual terms in the expression. We support `and` and `or` and we apply them using [infix](http://www.cs.man.ac.uk/~pjj/cs212/fix.html) notation from the left-hand side of the expression. That means we that we treat `[a] and [b] or [c]` as `([a] and [b]) or [c]` rather than `[a] and ([b] or [c])`.
 - Our `and` and `or` parsers are defined as functions that take previously parsed `JqlNode` objects and combine them into `BinaryNode` objects with the correct operator.

 That's it - we can throw input at this thing and it'll be parsed to its equivalent AST representation.

But that's not the end of the tale, we have a grammar that works well for *expected* input, but production web apps end up encountering all kinds of random junk... How do we make our parser resilient to this kind of input?

## Handling Bad Input

We've seen all kinds of nonsense make its way into job search. Sometimes it's typos, other times it's malicious input - how should we handle this kind of input? We *could* just return a HTTP `400 Bad Request` with details of where in the input we failed but we can generally pull something useful enough out of the input to run a query against the backend, so why not do so?

Our most common fallback is to treat anything we don't really understand as just a text query. It's a reasonable fallback - we return results and users generally adjust their query, possibly correcting syntactic mistakes, if the results don't seem to be that useful. The way we treat text queries in Elastic means that we'll generally pick up anything useful in the user's input.

However, there are a couple of cases where the parser can become confused. For example `[c#] and ([sql-server] or)` will break the parser - it doesn't know how to handle the trailing `or` because it interprets it as part of a binary expression. This is essentially an implementation detail of Pidgin's `ExpressionParser` that we need to work around - notably that it starts parsing by evaluating the list of terms we provided and when it successfully parses a term it starts to parse the operators. Once it is parsing the operator it does not have the ability to backtrack again so the trailing `or` is *always* treated as an operator. To workaround this we explicitly handle a trailing binary operator followed by a bracket and treat it as a JQL `TextNode`:

```c#
public class JqlParser
{
    private static readonly Parser<char, Pidgin.Unit> _validTerminators = Lookahead(
        _rparen.ThenReturn(Pidgin.Unit.Value)
    ).Or(End);

    private static readonly Parser<char, JqlNode> _trailingAnd = Try(
        _and.Select<JqlNode>(JqlBuilder.Text).Before(_validTerminators)
    );

    private static readonly Parser<char, JqlNode> _trailingOr = Try(
        _or.Select<JqlNode>(JqlBuilder.Text).Before(_validTerminators)
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
        ).Many().Select(x => new QueryNode(x));
}
```

## Performance

So, how does our Pidgin-based parser perform compared to the hand-rolled implementation we used previously? Here's a benchmark for a few representative cases:

 - Complex - `[c#] and ([sql-server] or [oracle]) -[php] remote:true`
 - Empty - an empty search
 - Invalid - an invalid search
 - Text - `full stack developer`
 - Modifiers - `remote:true`

``` ini

BenchmarkDotNet=v0.11.4, OS=Windows 10.0.17763.437 (1809/October2018Update/Redstone5)
Intel Core i9-9900K CPU 3.60GHz, 1 CPU, 16 logical and 8 physical cores
  [Host]     : .NET Framework 4.7.2 (CLR 4.0.30319.42000), 64bit RyuJIT-v4.7.3362.0
  DefaultJob : .NET Framework 4.7.2 (CLR 4.0.30319.42000), 64bit RyuJIT-v4.7.3362.0


```
|               Method |     query |         Mean |      Error |     StdDev | Ratio | RatioSD | Gen 0/1k Op | Gen 1/1k Op | Gen 2/1k Op | Allocated Memory/Op |
|--------------------- |---------- |-------------:|-----------:|-----------:|------:|--------:|------------:|------------:|------------:|--------------------:|
| **JqlParser_HandRolled** |   **Complex** | **12,874.49 ns** | **62.1052 ns** | **55.0546 ns** |  **1.00** |    **0.00** |      **1.9836** |      **0.0153** |           **-** |             **12528 B** |
|     JqlParser_Pidgin |   Complex | 25,961.08 ns | 91.6681 ns | 76.5470 ns |  2.02 |    0.01 |      1.0071 |           - |           - |              6544 B |
|                      |           |              |            |            |       |         |             |             |             |                     |
| **JqlParser_HandRolled** |     **Empty** |    **302.70 ns** |  **1.1613 ns** |  **1.0863 ns** |  **1.00** |    **0.00** |      **0.1025** |           **-** |           **-** |               **648 B** |
|     JqlParser_Pidgin |     Empty |     96.39 ns |  0.7613 ns |  0.7121 ns |  0.32 |    0.00 |      0.0088 |           - |           - |                56 B |
|                      |           |              |            |            |       |         |             |             |             |                     |
| **JqlParser_HandRolled** |   **Invalid** |  **3,881.28 ns** | **22.3465 ns** | **20.9030 ns** |  **1.00** |    **0.00** |      **0.4883** |           **-** |           **-** |              **3096 B** |
|     JqlParser_Pidgin |   Invalid |  9,201.01 ns | 43.5177 ns | 40.7065 ns |  2.37 |    0.02 |      0.3204 |           - |           - |              2136 B |
|                      |           |              |            |            |       |         |             |             |             |                     |
| **JqlParser_HandRolled** | **Modifiers** |  **3,633.93 ns** | **16.8197 ns** | **14.9102 ns** |  **1.00** |    **0.00** |      **0.4692** |           **-** |           **-** |              **2968 B** |
|     JqlParser_Pidgin | Modifiers |  8,010.70 ns | 61.3875 ns | 47.9273 ns |  2.20 |    0.02 |      0.3052 |           - |           - |              1984 B |
|                      |           |              |            |            |       |         |             |             |             |                     |
| **JqlParser_HandRolled** |      **Text** |  **6,395.75 ns** | **24.5107 ns** | **22.9273 ns** |  **1.00** |    **0.00** |      **0.8392** |           **-** |           **-** |              **5328 B** |
|     JqlParser_Pidgin |      Text | 11,221.87 ns | 66.0639 ns | 55.1664 ns |  1.75 |    0.01 |      0.4730 |           - |           - |              3032 B |


There are some interesting results! We can see that Pidgin allocates less but that our implementation is faster. However, we're talking on the order of microseconds here which, in the context of a request that takes ~30ms, is nothing *and* we haven't performed any optimization of the Pidgin implementation just yet. Also, it's worth noting that we don't *parse* that many things - only around 0.5% of our daily traffic needs to be run through a parser in the first place - I'll detail more about the why of that next time.

I think we'll likely put the Pidgin version into production in the coming weeks - it drastically reduces the line count future developers have to understand and maintain without a significant impact on our performance.

## Next Time...

Next time we'll look into how we can use our AST to do useful things like pre-processing queries prior to sending them to Elastic and how we perform the translation into something Elastic understands!



