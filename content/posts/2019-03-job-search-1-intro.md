---
title: "Building Stack Overflow Job Search"
date: 2019-03-21T00:00:00Z
tags: [.net, jobs, stack-overflow]
draft: true
---
[Stack Overflow Jobs](https://stackoverflow.com/jobs) has always had the ability to perform searches across jobs on the site and for many years used a simplistic, hand-rolled implementation that served us well for a long time. It did have its quirks, however, and solicited a fair amount of feedback on our meta sites ([Meta.SE](https://meta.stackexchange.com/) and [Meta.SO](https://meta.stackoverflow.com/))
from developers feeling that they were unable to really filter things the way they want to... And that didn’t really sit too well with us! In an effort to better understand the problems, we set out to investigate how we could provide better search capability for jobs. Along the way we made data store, query engine and performance tweaks.

The next few posts are about that journey and the technical details behind our job search implementation.

## A Little History

Historically job search was powered by SQL Server's full text search (FTS) with a thin layer on top to translate certain inputs into `WHERE` clauses on the relevant fields. That thin layer consisted of some string manipulation to pull out boolean operators and mangle the input into a query suitable enough to throw at SQL.

FTS *just about* worked for out needs back then but it lacks most of the indexing and querying capabilities of other full text products such as Solr or ElasticSearch. Fortunately we make heavy use of ElasticSearch to power the Q&A network's search functionality as well as the company and candidate search features of [Stack Overflow Talent](https://talent.stackoverflow.com/).

We took the jump from SQL FTS over to Elastic for job search in 2015, but it was a simple port of the existing indexing and querying mechanisms to the new platform. As a result of this we still had the nasty string manipulation hanging around. Yuck.

We couldn't take full advantage of Elastic's scoring and boosting capabilities when performing full text queries. Instead we had a single big blob of text containing the parts of the job we needed to query. Things like tags or title were repeated to give them additional weight! We had limited capabilities to query things like specific parts of a job and no way of doing negative filters or complex queries.

At this point we'd had quite enough of the stinking pile of tech debt lurking beneath the surface and decided to do something about it.

## Decisions, Decisions

<img src="/img/job-search-1.jpg" width=250 alt="Silly Kitty"><br/>
<sub style="color:lightgray">We tried not to make bad decisions like kitty did...</sub>

Stack Exchange already has a [fairly comprehensive query language](https://stackoverflow.com/help/searching) that can be used to query posts across a particular site on the network. Ideally we wanted something that used similar syntax so that users could take their knowledge from one part of the site and use it in other places.

In addition we wanted a single code base to interpret user queries for both job search and [company search](https://stackoverflow.com/jobs/companies/).

And finally we wanted something that was easily extendable *and* testable (yes, contrary to popular belief we do testing at Stack Overflow 😮).

We had previously found the string parsing code hard to test and maintain, so avoiding anything similar to that seemed like a good plan. Unfortunately the query language used by Stack Exchange was exactly the kind of parsing nightmare that we were trying to avoid in the first place (it's fast but hard to tweak) so we decided it'd be better to start with a clean implementation that we could drop into other places later.

We wanted to have a functionally equivalent language and decided to implement Jobs Query Language (JQL) using a lexer and recursive descent parser. The parser generates an abstract syntax tree (AST) representing the query which we can then visit and generate an Elastic query from. It could theoretically be used to generate a query for any data store. Turns out that ability cane i useful later on! 

In addition, producing a tree lets us do things like replace front-end parts of a query (e.g. like favorite:true) into a query that makes more sense for the data we actually store in Elastic (e.g. a list of a user’s favorite job ids).

Finally, an AST is easy to test - we can take an input string, parse it and then compare the output tree to an expected tree. \o/

## Next Time

Next time we'll talk about writing the parser for job search...