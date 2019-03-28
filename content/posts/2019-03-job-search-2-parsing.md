---
title: "Building Stack Overflow Job Search - Parsing Queries"
date: 2019-03-22T00:00:00Z
tags: [.net, jobs, stack-overflow]
draft: true
---

In [Part 1](2019-02-job-search-1-intro) we talked about some of the shortcomings of Stack Overflow's job search and how we addressed them. In this episode we'll dive into how our parser is written.

## Some Background

Most people seem to get scared the moment the words lexer or parser get mentioned. I'd highly recommend watching [Rob Pike's talk](https://youtube.com/watch?v=HxaD_trXwRE) on the Go lexer and parser; it clears up a lot of misconceptions and provides a solid basis for writing a hand-rolled parser.

You may be asking why we built a hand-rolled parser instead of something produced with a tool like ANTLR? Well, originally we were producing both C# and Javascript parsers for the backend and frontend of the site. We tried ANTLR and found the output produced for the front-end was absurdly large and not something we could realistically deploy on a production website. Our hand-rolled lexer & parser were substantially smaller and faster. That said, in the end, we decided not to ship a Javascript version of the parser.

We used our hand-rolled parser until very recently, but as of this week we moved to using a [parser combinator](https://en.wikipedia.org/wiki/Parser_combinator) library called [Pidgin](https://github.com/benjamin-hodgson/Pidgin). This library is maintained by our very own parser wizard [Benjamin Hodgson](https://benjamin.pizza/) and eliminates a lot of the mistakes that are common in writing your own parser. It also performs very well compared to the hand-rolled version while being more maintainable; everybody wins!

Originally I wrote this post about building the hand-rolled parser but trying to explain it concisely wound up being complex and verbose. I think this points to it being hard to reason about and maintain so I set out to write the parser combinator version instead. It turned out well enough (passes all tests and performs better) that I thought I'd write about that instead...

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

## Building the Parser
 
Once we've defined a grammar, our next step is to break down each rule into a set of mini-parsers. In our case we'd like to take an arbitrary string input and return something representing the parsed form of it. A common way of representing the parsed form is as an [abstract syntax tree (AST)](https://en.wikipedia.org/wiki/Abstract_syntax_tree).

JQL is represented using an AST that uses an abstract base class called `JqlNode`. We have implementations that reflect the structure of the grammar. E.g. it consists of a `QueryNode` representing a query which consists of things like `LiteralNode` to represent text/numbers/bools and `ModifierNode` to handle modifiers like `remote:true`.

So, to be effective, each mini-parser must take a `string` (which at its most primitive level is just an array of `char`) and return a `JqlNode` as a result. Pidgin makes this easy, for example here's how we define a tag:

```c#
public class JqlParser
{
    private static Parser<char, T> Token<T>(Parser<char, T> p) => Try(p).Before(SkipWhitespaces);
    
    private static readonly Parser<char, char> _tagChars = LetterOrDigit.Or(OneOf('-', '#', '_', '+', '*', '.'));
        private static readonly Parser<char, JqlNode> _tag =
            Token(
                _tagChars
                    .AtLeastOnceString()
                    .Between(Char('['), Char(']'))
            )
            .Select<JqlNode>(t => JqlBuilder.Mod(JqlModifiers.Job.Tag, JqlBuilder.Text(t)))
            .Labelled("tag");
            
    public JqlNode ParseTag(string input) => _tag.ParseOrThrow(input);
}
```

Here we can see that we've defined the characters that a tag supports; any letter or digit or the `-`, `#`, `+`, `*`, and `.` characters. We've also defined a helper called `Token` that attempts to consume input defined by a parser (using `Try`) skipping whitespace at the start of the input - this means we don't need to worry about handling whitespace in 
We perform this process for each rule in the grammar, re-using combinations of mini-parsers to build more complete parsers. Eventually we get to a point where we have a parser that can handle the entire grammar!

// TODO define a simplified grammar using Pidgin

But that's not the end of the tale, we have a grammar that works well for *expected* input, but production web apps end up encountering all kinds of random junk... How do we make our parser resilient to this kind of input?

## Handling Bad Input

// TODO how to make an elegant parser terrible

## Performance

So, how does our Pidgin-based parser perform compared to the hand-rolled implementation we used previously? // TODO benchmark.net

## Nect Time...

Next time we'll look into how we can use our AST to do useful things like pre-processing queries prior to sending them to Elastic and how we perform the translation into something Elastic understands!



