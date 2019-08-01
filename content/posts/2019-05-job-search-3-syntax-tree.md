---
title: "Building Stack Overflow Job Search - Transforming Queries"
date: 2019-05-13T00:00:00Z
tags: [.net, jobs, stack-overflow]
draft: true
images: [img/job-search-cover.png]
---
[Last time](2019-05-job-search-2-parsing) we talked about how we built a parser that can take a string input (written in Jobs Query Language or JQL) and parse it into an abstract syntax tree (AST) representing the query.

This episode explores the reasons why we would do this and what we can do with tree once we have it.

## Syntax Tree Uses

We could take an input string representing JQL and directly translate into a query against our data store (in our case ElasticSearch) but we choose to represent it as an abstract syntax tree instead.

An intermediate representation between a user's raw string input and a query to a data store affords us a host of benefits. One of the most useful ones is being able to rewrite parts of a query to better suit the underlying data store. For example `favorite:true` is used to search for jobs a user favourited (yeh I know "favorite" - British English vs American English, one of the pitfalls of working for a US-based company :). However we don't store that data directly in Elasticsearch because doing so means we have to update Elastic every time a user changes their favourites. This is expensive and unnecessary - instead we can query SQL for the job identifiers that the user favourited and then rewrite the query to search for those identifiers instead.

Another example is where we perform geo-lookups - a user can type "London, UK" into the location input and we'll rewrite the query to use lat/lon or a bounding box based upon results from our geo-provider (Google right now) for the text.

In addition we can do all kinds of extra fun things like targeting a different data store. In practice this is used to allow us to run certain queries entirely in-memory. I'll talk more on that next time!

# How it works?

In a long gone past we used something a little like the [TODO: link visitor pattern]() and had a relatively large hierarchy of "visitors" to manipulate the query.
