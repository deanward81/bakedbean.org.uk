---
title: "A Deep Dive into Job Search - Parsing Queries"
date: 2019-02-02T00:00:00Z
tags: [.net, jobs, stack-overflow]
draft: true
---

In [Part 1](2019-02-job-search-intro) we talked about some of the shortcomings of Stack Overflow's job search and how we plan to address them. In this episode we'll dive into our parser is written.

## Some Background

Most people seem to get scared the moment the words lexer or parser get mentioned. I'd highly recommend watching [Rob Pike's talk](https://youtube.com/watch?v=HxaD_trXwRE) on the Go lexer and parser; it clears up a lot of misconceptions and provides a solid basis for writing a hand-rolled parser.

You may be asking why we built a hand-rolled parser instead of something produced with a tool like ANTLR? Well, originally we were producing both C# and Javascript parsers for the backend and frontend of the site. We tried ANTLR and found the output produced for the front-end was absurdly large and not something we could realistically deploy on a production website. Our hand-rolled lexer/parser were substantially smaller and faster.

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

This supports a whole variety of inputs for search:

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

- A lexer is responsible for taking an input and *a character at a time* transforming it into a list of tokens. This might involve grouping several tokens together but usually results in a list of tokens that are represented by an offset and a length into the original input.
- A parser takes the set of tokens produced by a lexer and interprets them as something that matches the grammar defined for the language.

Together a lexer and parser are are responsible for taking an arbitary input and transforming into the grammar we've previously defined. A common way of representing a grammar after lexing & parsing is as an [abstract syntax tree (AST)](...).

## Creating the Lexer

