---
title: "Building Stack Overflow Job Search - Performance"
date: 2019-12-03T00:00:00Z
tags: [.net, jobs, stack-overflow]
draft: true
images: [img/job-search-cover.png]
---
This is the last episode in our exploration of building Stack Overflow's job search functionality. [In our last installment](2019-12-job-search-3-syntax-tree) we investigated the JQL AST and how we use visitors to transform and manipulate queries written in JQL.

At the end of that post I mentioned that we used the capabilities given to us by an AST and used them to further optimize jobs... Let's dig a little more into that!

## Optimization

At Stack Overflow we love us a bit of performance optimization! Jobs is no different - it has come a long way since the days of hand-written query translations and performance is an important part of our query engine.

When we originally migrated to JQL back in 2017 we decided to write a visitor that could transform a query into its Elastic representation. That happens to work great for full text search - after all that's what Elasticsearch shines at - but we wanted to investigate whether we could provide faster query times for the 40% of queries that aren't using full text search. When I talk about "full text search" here I mean anything that consists of plain text - for example `full stack developer remote:true` would be considered a full text search, whereas `remote:true salary:50000..60000` would be considered a filter-only search.

While full text search tends to have complex indexing needs (e.g. analysis pipelines - things like stemming, synonyms, tokenization), filter-only searches can be handled easily using an inverted index mapping individual facets of the thing being indexed (a job in this case) to its unique identifier.

As an example, consider a set of jobs that are defined as follows:

```json
[
    {
        "id": 1234,
        "tags": ["c#", "sql-server", "typescript"],
        "seniorities": ["junior"],
        "remote": true
    },
    {
        "id": 5678,
        "tags": ["php", "mysql", "javascript"],
        "seniorities": ["junior", "mid-level"],
        "remote": false
    },
    {
        "id": 9012,
        "tags": ["c#", "mongodb", "javascript"],
        "seniorities": ["mid-level", "senior"],
        "remote": false
    }
]
```

If we were to index by the facets of each job, we might end up with something like this:

```json
{
    "tags": {
        "c#": [1234, 9012],
        "sql-server": [1234],
        "typescript": [1234],
        "php": [5678],
        "mysql": [5678],
        "javascript": [5678, 9012],
        "mongodb": [9012]
    },
    "seniorities": {
        "junior": [1234, 5678],
        "mid-level": [5678, 9012],
        "senior": [9012]
    },
    "remote": {
        "true": [1234],
        "false": [5678, 9012]
    }
}
```

If we transform a query such as `tag:c# or seniority:junior` to work against the facets we defined above then we'd do the following:

1. Find all identifiers that match `tag:c#` - that gives us `[1234, 9012]`
1. Find all identifiers that match `seniority:junior` - that gives us `[1234, 5678]`
1. Do a set [union](https://en.wikipedia.org/wiki/Union_(set_theory)) operation against the two sets of identifiers (this is effectively what `or` does when dealing with a set): `[1234, 9012] ∪ [1234, 5678]` which gives us `[1234, 9012, 5678]`
1. Translate the identifiers back into jobs!

Similarly `tag:javascript and seniority:senior` can be performed as follows:

1. Find all identifiers that match `tag:javascript` = `[5678, 9012]`
1. Find all identifiers that match `seniority:senior` = `[9012]`
1. Do a set [intersection](https://en.wikipedia.org/wiki/Intersection_(set_theory)) operation against the two sets of identifiers (this is effectively what `and` does when dealing with a set): `[5678, 9012] ∩ [9012]` = `[9012]`
1. Translate the identifiers back into jobs!

That's great! We can efficiently represent facets of large number of jobs and then perform operations to translate a query into the set of jobs it represents all by manipulating this data structure!

## Translating JQL into set-based operations

Given an AST representing JQL: