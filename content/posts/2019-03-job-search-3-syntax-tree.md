---
title: "Building Stack Overflow Job Search - Transforming Queries"
date: 2019-03-26T00:00:00Z
tags: [.net, jobs, stack-overflow]
draft: true
---
[Last time](2019-02-job-search-2-parsing) we talked about how we built a parser that can take a string input (written in Jobs Query Language or JQL) and parse it into an abstract syntax tree (AST) representing the query.

This episode explores the reasons why we would do this and what we can do with tree once we have it.