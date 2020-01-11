---
title: "Building Stack Overflow Job Search - Performance"
date: 2019-12-27T00:00:00Z
tags: [.net, jobs, stack-overflow]
draft: true
images: [img/job-search-cover.png]
---
This is the last episode in our exploration of building Stack Overflow's job search functionality. [In our last installment](2019-12-job-search-3-syntax-tree) we investigated the JQL AST and how we use visitors to transform and manipulate queries written in JQL.

At the end of that post I mentioned that we used the capabilities given to us by an AST and used them to further optimize jobs... Let's dig a little more into that!

## Optimization

At Stack Overflow we love us a bit of performance optimization! Jobs is no different - it has come a long way since the days of hand-written query translations and performance is an important part of our query engine.

When we originally migrated to JQL back in 2017 we decided to write a visitor that could transform a query into its Elastic representation. That happens to work great for full text search - after all that's what Elasticsearch shines at - but we wanted to investigate whether we could provide faster query times for the 40% of queries that aren't using full text search. When I talk about "full text search" here I mean anything that consists of plain text - for example `full stack developer remote:true` would be considered a full text search that filters the results of `"full stack developer"` by the `remote:true` predicate, whereas `remote:true salary:50000..60000` would be considered a filter-only search.

While full text search tends to have complex indexing needs (e.g. analysis pipelines - things like tokenization, stemming, synonyms), filter-only searches can be handled easily using an inverted index mapping individual facets of the thing being indexed (a job in this case) to its unique identifier.

## Building an inverted index

Consider a set of jobs that are defined as follows:

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

## Querying an inverted index

**OR Queries**

If we transform a query such as `tag:c# or seniority:junior` to work against the facets we defined above then we'd do the following:

1. Find all identifiers that match `tag:c#` - that gives us `[1234, 9012]`
1. Find all identifiers that match `seniority:junior` - that gives us `[1234, 5678]`
1. Do a set [union](https://en.wikipedia.org/wiki/Union_(set_theory)) operation against the two sets of identifiers (this is effectively what `or` does when dealing with a set): `[1234, 9012] ∪ [1234, 5678]` which gives us `[1234, 9012, 5678]`
1. Translate the identifiers back into jobs!

**AND Queries**

Similarly `tag:javascript and seniority:senior` can be performed as follows:

1. Find all identifiers that match `tag:javascript` = `[5678, 9012]`
1. Find all identifiers that match `seniority:senior` = `[9012]`
1. Do a set [intersection](https://en.wikipedia.org/wiki/Intersection_(set_theory)) operation against the two sets of identifiers (this is effectively what `and` does when dealing with a set): `[5678, 9012] ∩ [9012]` = `[9012]`
1. Translate the identifiers back into jobs!

**NOT Queries**

These are simple too! We can treat `NOT remote:true` as a set except operation. A primitive implementation of this is as follows:

1. Find all identifiers that match `remote:true` = `[1234]`
1. Take the set of all jobs `[1234, 5678, 9012]`
1. Do a set [except](TODO:wikipedia) operation against the two sets of identifiers: `[1234, 5678, 9012] - [1234]` = `[5678, 9012]`
1. Translate the identifiers back into jobs!

We can optimize further by understanding how the `NOT` operator is used in the query. E.g. `NOT remote:true` could be transformed into `remote:false` and `tag:javascript NOT tag:mongodb` could perform an except operation against the already filtered set of identifiers yielded by `tag:javascript`.

Overall these approaches work pretty well; we can efficiently represent facets of a large number of jobs and then perform operations to translate a query into the set of jobs it represents all by manipulating this data structure!

## Translating JQL into set-based operations

Given an AST representing JQL:

```
                  Query
                    |
                   AND
                  /  \__
Modifier:seniority      \
          |             OR
        junior         /   \___
                 Modifier:tag  \
                      |         Modifier:tag
                javascript         |
                                   c#
```

By traversing the tree we can translate each term in the query above into a set of identifiers that match that term.

That looks like this:

```
                  Query
                    |
                    ∩
                   /  \__
       [1234, 5678]      \
                         ∪
                       /   \___
                 [5678, 9012]  \
                               [1234, 9012]
```

For each binary operator (such as `OR` or `AND`) we perform a set operation on the operands and the result is *another* set of identifiers that match the combined predicate.

By evaluating the `OR` we end up with this:
```
                  Query
                    |
                    ∩
                   /  \____
       [1234, 5678]        \
                   [1234, 5678, 9012]
```

 And finally, evaluating the `AND`:

 ```
                  Query
                    |
               [1234, 5678]
```

## Identifiers to Objects   

Once we have identifiers it's relatively trivial to use an `ImmutableDictionary<int, Job>` to go turn them into `Job` objects:

```c#
private readonly ImmutableDictionary<int, Job> _jobsById;

public ImmutableArray<Job> GetJobs(ImmutableArray<int> jobIds)
{
    var results = ImmutableArray.CreateBuilder<Job>(jobIds.Length);
    for (var i = 0; i < jobIds.Length; i++)
    {
        results.Add(_jobsById[jobIds[i]]);
    }

    return results.MoveToImmutable();
}
```

But that comes at a cost! Doing dictionary lookups requires locating the hash bucket that the hashcode maps to, which is cheap for the occasional lookup, but doing it in a hotpath called millions of times a day adds up.

Instead we create an array that contains all the jobs and we use the index of a job in that array rather than its unique identifier. This allows us to use an array indexer which is *really* fast:

```c#
private readonly ImmutableArray<Job> _jobs;

public ImmutableArray<Job> GetJobs(ImmutableArray<int> jobIndexes)
{
    var results = ImmutableArray.CreateBuilder<Job>(jobIndexes.Length);
    for (var i = 0; i < jobIndexes.Length; i++)
    {
        results.Add(_jobs[jobIndexes[i]]);
    }

    return results.MoveToImmutable();
}
```

Here's the results of a [benchmark](https://github.com/deanward81/bakedbean.org.uk/tree/master/samples/src/Samples.Benchmarks) demonstrating the speed of using an array indexer rather than the dictionary lookup:

``` ini

BenchmarkDotNet=v0.12.0, OS=macOS 10.15.2 (19C57) [Darwin 19.2.0]
Intel Core i7-3740QM CPU 2.70GHz (Ivy Bridge), 1 CPU, 8 logical and 4 physical cores
.NET Core SDK=3.1.100
  [Host]     : .NET Core 3.1.0 (CoreCLR 4.700.19.56402, CoreFX 4.700.19.56404), X64 RyuJIT
  DefaultJob : .NET Core 3.1.0 (CoreCLR 4.700.19.56402, CoreFX 4.700.19.56404), X64 RyuJIT


```

|           Method |       Mean |    Error |   StdDev | Ratio |
|----------------- |-----------:|---------:|---------:|------:|
| DictionaryLookup | 2,189.1 ns | 22.97 ns | 20.36 ns |  1.00 |
|     ArrayIndexer |   121.4 ns |  1.59 ns |  1.49 ns |  0.06 |

That's 94% faster! This can make a massive difference when the code path is hit as often as this one!



## Sorting AAA 


AAA

Jobs currently supports 3 kinds of sort:

 - Newest - sorted in descending order based upon the date a job was posted by an employer
 - Salary - sorted in descending order based upon the upper bound of a job's salary
 - Matches - sorted based upon a weighting algorithm that uses aspects of a user's job preferences

The last sort is unique per user and so we can't cheaply pre-compute sort orders for it, but the first two can be trivially pre-computed; the value used for sorting is only mutated when the job itself is changed. We have to rebuild our inverted index whenever a job changes anyway so it makes sense to have two arrays of indexes - one sorted by date and one sorted by salary. When we get a set of array indexes as the result of a search operation we first de-ref into the sorted array and then de-ref into the original array of jobs:

TODO: sane ASCII art for this
```
    Jobs: [Job 1, Job 2, Job 3, ..., Job N]
    Sorted By Date: [Job 2, Job 1, ..., Job N]
    Sorted By Salary: [Job 3, Job 2, ..., Job N]

    JQL -> 
    JQL -> array of indexes 
```

TODO: Quick select??

## Efficient Set Operations

## TODO: Efficient set operations
  - `ReadOnlySet<int>`: pre-sorted, minimal iteration (sample with benchmarks vs using a `HashSet<int>` and the set operations on it)

## TODO: Geo-searches
 - visitor to resolve textual query into lat/lon
 - kd-tree (Benjamin's ASCII art in comments)
  - give an example tree, show how we traverse the lat/lon and use that combined with the radius / bounding box predicates
 - radius vs. bounding box

## TODO: Range searches
 - binary tree (TODO: check code to see how this worked)

## TODO: Matching algorithm
 - Pointer to Aurélien's post
 - Sorting optimizations
   - Marc's blog on sorting; `Array.Sort` overloads
 - Pre-computed hashcodes for strings - 
- 

## TODO: Allocations
 - ArrayPool<Job>

## Futures??

## Conclusion
 - benchmarks
 - MiniProfiler; Elastic vs. in-memory