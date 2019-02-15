---
title: "Building Stack Overflow Job Search - Parsing Queries"
date: 2019-02-02T00:00:00Z
tags: [.net, jobs, stack-overflow]
draft: true
---

In [Part 1](2019-02-job-search-intro) we talked about some of the shortcomings of Stack Overflow's job search and how we addressed them. In this episode we'll dive into how our parser is written.

## Some Background

Most people seem to get scared the moment the words lexer or parser get mentioned. I'd highly recommend watching [Rob Pike's talk](https://youtube.com/watch?v=HxaD_trXwRE) on the Go lexer and parser; it clears up a lot of misconceptions and provides a solid basis for writing a hand-rolled parser.

You may be asking why we built a hand-rolled parser instead of something produced with a tool like ANTLR? Well, originally we were producing both C# and Javascript parsers for the backend and frontend of the site. We tried ANTLR and found the output produced for the front-end was absurdly large and not something we could realistically deploy on a production website. Our hand-rolled lexer/parser were substantially smaller and faster. That said, in the end, we decided not to ship a Javascript version of the parser. Longer term we're intending to move to a [parser combinator](https://en.wikipedia.org/wiki/Parser_combinator) library instead of the hand-rolled implementation. 

> NOTE: Although we go into the detail of our hand-rolled parser below, *please* don't do this unless you have a good reason. Use something like [Pidgin](https://github.com/benjamin-hodgson/Pidgin) instead, it eliminates a lot of the mistakes that are common in writing your own parser and performs very well thanks to Stack's very own parser wizard [Benjamin Hodgson](https://benjamin.pizza/). More on that later...

Onwards, let's talk about the steps we took to build our parser!

## Defining the Grammar

We started with an idea of the language we wanted to implement. This is referred to as the grammar. It's common to use [Extended Backus-Naur Form (EBNF)](https://en.m.wikipedia.org/wiki/Extended_Backus–Naur_form)  which is a syntax used to desribe the individual components of the grammar. Here's a snippet of Jobs Query Language (JQL) in EBNF:

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

This supports a wide variety of inputs for search:

- **Simple Text**: `hello world`
- **Quoted Text**: `"hello world"`
- **Tags**: `[asp.net]`
- **Modifiers**: `salary:10000` or `salary:10000USD`
- **Ranges**: `salary:10000..20000` or `salary:..50000` or `salary:10000..`
- **Expressions**: `[asp.net] or "hello world"` or `[c#] and not [java] and salary:10000..20000`
- **Complex Expressions**: `([asp.net] or "hello world") and (([c#] and not [java]) or salary:10000..)`

Of particular importance is the definition of `<expression>`; this is what allows for the nesting of different parts of the grammar via recursion.

## What's Next?

Once we've defined a grammar, our next step is to create a lexer and a parser to handle input based on that grammar. A reminder for those that don't use such things on a regular basis...

- A lexer is responsible for taking an input and *a character at a time* transforming it into a list of tokens (i.e. individual parts of the grammar). This might involve grouping several characters together and usually results in a list of tokens that are represented by an offset and a length into the original input.
- A parser takes the list of tokens produced by a lexer and interprets them as something that matches the grammar defined for the language.

Together a lexer and parser are are responsible for taking an arbitary input and transforming into the grammar we've previously defined. A common way of representing a grammar after lexing & parsing is as an [abstract syntax tree (AST)](https://en.wikipedia.org/wiki/Abstract_syntax_tree).

## Creating the Lexer

Following the implementation that Rob Pike used in Go we built a simple lexer that enumerates an array of characters (i.e. a `string`) and, based upon what token we're expecting next, tests whether the input meets the requirements of the token.

If it does, we produce that token as an output, and if not we throw an error!

The grammar defined above is a little complex to implement in a blog post so we'll start with something a little simpler:

```
<and> ::= 'and' | '&&';
<quote> ::= '"';
<colon> ::= ':';
<string> ::= { <any_character> - (<quote> | <colon>) };
<quoted_string> ::= <quote> { <any_character> - <quote> } <quote>;
<literal> ::= <string> | <quoted_string>;
<term> ::= <modifier>, <colon>, <literal> | <literal>;
<expression> ::= 
    <expression>, <and>, <expression> | 
    <term>;
```

First off we define an enum representing each of the types of token we want to handle:

```c#
public enum TokenType
{
    And,
    Colon,
    Quote,
    String,
    Modifier,
}
```

Then we define a struct representing a token in the input:

```c#
public struct Token
{
    public Token(JqlTokenType type, string value, int index, int length)
    {
        Type = type;
        Value = value;
        Index = index;
        Length = length;
    }

    public JqlTokenType Type { get; }
    public string Value { get; }
    public int Index { get; }
    public int Length { get; }
}
```

To make the lexing process a little less involved we define a class that
can be used to match each token in our grammar against an arbitrary input:

> Note: You'll see here that we're using a regex to determine whether a substring of the input matches a specific token. This sucks, but it works well for our needs. I really can't stress enough that you should use a parser combinator like [Pidgin](https://github.com/benjamin-hodgson/Pidgin) rather than performing the shennanigans below.

```c#
public abstract class TokenMatcher
{
    public static readonly TokenMatcher And = new RegexTokenMatcher(TokenType.And, new Regex(@"^(?:and\s+|&&)", RegexOptions.IgnoreCase | RegexOptions.Compiled));
    public static readonly TokenMatcher And = new RegexTokenMatcher(TokenType.And, new Regex(@"^(?:and\s+|&&)", RegexOptions.IgnoreCase | RegexOptions.Compiled));

    private TokenMatcher(TokenType type)
    {
        Type = type;
    }

    public TokenType Type { get; }

    public abstract Token GetToken(string input, int index);

    // Matches a regex against an input at a given index
    // E.g. 
    //   input="text:hello"
    //   index=0
    //   pattern="^[a-z\-]+\:"
    // Then this will return the following token:
    //   Token(TokenType.Modifier, "text:", index, 5);
    // However, if:
    //   input="text:hello"
    //   index=4
    //   pattern="^[a-z\-]+\:"
    // Then this will return null.
    private class RegexTokenMatcher : TokenMatcher
    {
        private readonly Regex _pattern;

        public RegexTokenMatcher(TokenType type, Regex pattern) : base(type)
        {
            _pattern = pattern;
        }

        public override Token GetToken(string input, int index)
        {
            var match = _pattern.Match(input.Substring(index));
            if (!match.Success)
            {
                return null;
            }

            var value = match.Value.Trim();
            return new Token(Type, value, index, value.Length);
        }
    }

    // Matches a specific string against an input at a given index
    // E.g. 
    //   input="text:hello"
    //   index=4
    //   value=":"
    // Then this will return the following token:
    //   Token(TokenType.Colon, ":", index, 1);
    // However, if:
    //   input="text:hello"
    //   index=1
    //   value=":"
    // Then this will return null.
    private class ValueTokenMatcher : TokenMatcher
    {
        private readonly string _value;

        public ValueTokenMatcher(TokenType type, string value) : base(type)
        {
            _value = value;
        }

        public override Token GetToken(string input, int index)
        {
            if (input.Length - index < _value.Length)
            {
                return null;
            }

            for (var i = 0 ; i < _value.Length; i++)
            {
                if (input[index + 1] != _value[i])
                {
                    return null;
                }
            }

            return new Token(Type, _value, index, _value.Length);
        }
    }
}
```

Now we have a way of matching each token we can finally write the lexer. Its job is to take a `string` and produce a `Token` for each part of the input. Our implementation keeps track of what index (`_index`) we got to in the input (`_input`) and the method used to read the next token at that index (`_lexMethod`). 

```c#

```

```c#
public class Lexer
{
    private LexMethod _lexMethod;
    private string _input;
    private int _index;
    private Queue<Token> _tokens;

    public Lexer(string input)
    {
        _lexMethod = LexExpression;
        _tokens = new Queue<Token>();
        _input = input;
        _index = 0;
    }

    public delegate LexMethod LexMethod();

    public Token Next()
    {
        if (_tokens.Count == 0)
        {
            QueueNext();
        }

        return _tokens.Dequeue();
    }

    private Token ConsumeToken(Token token)
    {
        _index += token.Length;
        _tokens.Enqueue(token);
        return token;
    }

    private void QueueNext()
    {
        while (_tokens.Count == 0)
        {
            _lexMethod = _lexMethod();
        }
    }

    private LexMethod LexExpression()
    {
        var startIndex = _index;
        var currentIndex = _index;
        if (_input[currentIndex] == '"')
        {
            // we've encountered a quote, this is a quoted string
            return LexQuotedString;
        }

        if (_input[]
        while (currentIndex < _input.Length)
        {
            if (_input[currentIndex] != ':' && _input[currentIndex])
        }
    }

}
```