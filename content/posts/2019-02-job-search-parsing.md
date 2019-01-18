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

## Writing the Parser

We started with an idea of the language we wanted to implement. This is referred to as the grammar. It's common to use [Backus-Naur Form (BNF)](https://en.m.wikipedia.org/wiki/Backus–Naur_form) which is a syntax used to desribe the individual components of the grammar. Here's Jobs Query Language (JQL) in BNF:

```
<string> ::= 
<quoted_string> ::= '"' <string> '"'
<value> ::= <number> | <string> | <quoted_string>
```



