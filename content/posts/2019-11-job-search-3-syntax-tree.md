---
title: "Building Stack Overflow Job Search - Transforming Queries"
date: 2019-11-26T00:00:00Z
tags: [.net, jobs, stack-overflow]
draft: true
images: [img/job-search-cover.png]
---
[Last time](2019-05-job-search-2-parsing) we talked about how we built a parser that can take a string input (written in Jobs Query Language or JQL) and parse it into an abstract syntax tree (AST) representing the query.

This episode explores the reasons why we would do this and what we can do with tree once we have it.

## Syntax Tree Uses

We could take an input string representing JQL and directly translate into a query against our data store (in our case ElasticSearch) but we choose to represent it as an abstract syntax tree instead.

An intermediate representation between a user's raw string input and a query to a data store affords us a host of benefits. One of the most useful ones is being able to rewrite parts of a query to better suit the underlying data store. For example `favorite:true` is used to search for jobs a user favorited. However we don't store that data directly in Elasticsearch because doing so means we have to update Elastic every time a user changes their favorites. This is expensive and unnecessary - instead we can query SQL for the job identifiers that the user favorited and then rewrite the query to search for those identifiers instead.

Another example is where we perform geo-lookups - a user can type "London, UK" into the location input and we'll rewrite the query to use lat/lon or a bounding box based upon results from our geo-provider (Google right now) for the text.

In addition we can do all kinds of extra fun things like targeting a different data store. In practice this is used to allow us to run certain queries entirely in-memory. I'll talk more on that next time!

# How it works?

In a long gone past we used something a little like the [Visitor Pattern](https://en.wikipedia.org/wiki/Visitor_pattern) and had a relatively large hierarchy of "visitors" to manipulate the query.

Our base class looked like this:

```c#
public abstract class JqlVisitor<T>
{
    public T Visit(JqlNode node)
    {
        switch (node.Type)
        {
            case JqlNodeType.And:
            case JqlNodeType.Or:
            case JqlNodeType.Between:
                return VisitBinary((BinaryNode)node);
            case JqlNodeType.LessThanEqual:
                return VisitUnary((UnaryNode)node);
            case JqlNodeType.GreaterThanEqual:
                return VisitUnary((UnaryNode)node);
            case JqlNodeType.Not:
                return VisitUnary((UnaryNode)node);
            case JqlNodeType.Boolean:
                return VisitBoolean((BooleanNode)node);
            case JqlNodeType.Text:
                return VisitText((TextNode)node);
            case JqlNodeType.Number:
                return VisitNumber((NumberNode)node);
            case JqlNodeType.Date:
                return VisitDate((DateNode)node);
            case JqlNodeType.Modifier:
                return VisitModifier((ModifierNode)node);
            case JqlNodeType.Query:
                return VisitQuery((QueryNode)node);
            case JqlNodeType.Group:
                return VisitGroup((GroupNode)node);
            case JqlNodeType.Location:
                return VisitLocation((LocationNode) node);
            case JqlNodeType.Geo:
                return VisitGeo((GeoNode)node);
            case JqlNodeType.Weight:
                return VisitWeight((WeightedNode)node);
        }

        throw UnsupportedType(node.Type);
    }

    protected abstract T VisitQuery(QueryNode node);

    protected abstract T VisitGroup(GroupNode node);

    protected abstract T VisitUnary(UnaryNode node);

    protected abstract T VisitBinary(BinaryNode node);

    protected abstract T VisitModifier(ModifierNode node);

    protected abstract T VisitBoolean(BooleanNode node);

    protected abstract T VisitText(TextNode node);

    protected abstract T VisitNumber(NumberNode node);

    protected abstract T VisitDate(DateNode node);

    protected abstract T VisitGeo(GeoNode node);

    protected abstract T VisitLocation(LocationNode node);

    protected abstract T VisitWeight(WeightedNode node);

    protected static Exception UnsupportedType(JqlNodeType type)
    {
        throw new NotSupportedException($"Node type {type} is not supported by this visitor.");
    }
}
```

We then had a whole hierarchy dedicated to different kinds of visitors - for example to transform a tree - like the favorites example from before we needed a base class just for transformations:

```c#
public abstract class TransformingJqlVisitor : JqlVisitor<JqlNode>
{
    public QueryNode Transform(QueryNode query)
    {
        return (QueryNode)Visit(query);
    }

    protected override JqlNode VisitBinary(BinaryNode node)
    {
        node.Left = Visit(node.Left);
        node.Right = Visit(node.Right);

        if (node.Left == null && node.Right == null)
        {
            return null;
        }

        if (node.Left == null)
        {
            return node.Right;
        }

        if (node.Right == null)
        {
            return node.Left;
        }

        return node;
    }

    protected override JqlNode VisitUnary(UnaryNode node)
    {
        var transformedNode = Visit(node.Operand);
        if (transformedNode == null)
        {
            return null;
        }

        if (!ReferenceEquals(node.Operand, transformedNode))
        {
            node = new UnaryNode(transformedNode, node.Type);
        }

        return node;
    }

    protected override JqlNode VisitQuery(QueryNode node)
    {
        var children = node.Children;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var transformedChild = Visit(children[i]);
            if (transformedChild == null)
            {
                children = children.RemoveAt(i);
            }
            else if (!ReferenceEquals(children[i], transformedChild))
            {
                children = children.SetItem(i, transformedChild);
            }
        }

        if (!ReferenceEquals(children, node.Children))
        {
            node = new QueryNode(children);
        }

        return node;
    }

    protected override JqlNode VisitGroup(GroupNode node)
    {
        var children = node.Children;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var transformedChild = Visit(children[i]);
            if (transformedChild == null)
            {
                children = children.RemoveAt(i);
            }
            else if (!ReferenceEquals(children[i], transformedChild))
            {
                children = children.SetItem(i, transformedChild);
            }
        }

        if (!ReferenceEquals(children, node.Children))
        {
            node = new GroupNode(children);
        }

        return node;
    }

    protected override JqlNode VisitBoolean(BooleanNode node)
    {
        return node;
    }

    protected override JqlNode VisitDate(DateNode node)
    {
        return node;
    }

    protected override JqlNode VisitGeo(GeoNode node)
    {
        return node;
    }

    protected override JqlNode VisitLocation(LocationNode node)
    {
        return node;
    }

    protected override JqlNode VisitModifier(ModifierNode node)
    {
        return node;
    }

    protected override JqlNode VisitNumber(NumberNode node)
    {
        return node;
    }

    protected override JqlNode VisitText(TextNode node)
    {
        return node;
    }

    protected override JqlNode VisitWeight(WeightedNode node)
    {
        node.Operand = Visit(node.Operand);
        if (node.Operand == null)
        {
            return null;
        }

        return node;
    }
}
```

To perform the favorites transformation from before we need a visitor that can take a user and transform a `favorite:true` query into a list of job identifiers `id:1,2,3,4`. This visitor takes the `User` and a query object and uses it to change modifier nodes that match the criteria
into a list of job identifiers:

```c#
class FavoriteJqlVisitor : TransformingJqlVisitor
{
    private readonly User _user;
    private readonly IFavoriteJobsQuery _query;

    public FavoriteJqlVisitor(User user, IFavoriteJobsQuery query)
    {
        _user = user;
        _query = query;
    }

    protected override JqlNode VisitModifier(ModifierNode node)
    {
        if (node.Value == JqlModifiers.Job.Favorite)
        {
            var operand = Visit(node.Operand) as BooleanNode;
            if (operand != null && operand.Value)
            {
                IList<int> jobIds = Array.Empty<int>();
                if (!_user.IsAnonymous)
                {
                    // grab the user's favorite job ids and add them
                    // as an id modifier with a list of ids.
                    jobIds = _query.GetFavoriteJobs(_user.Id);
                }

                if (jobIds.Count == 0)
                {
                    // HACK this results in a no-op on the Elastic-side
                    // filter for jobs with a negative identifier...
                    jobIds = new[] { -1 };
                }

                return new ModifierNode(JqlModifiers.Job.Id, new GroupNode(jobIds.Select(id => new NumberNode(id))));
            }
        }

        return base.VisitModifier(node);
    }
}
```

But... what if we needed this query to execute asynchronously? Sadly, to make this work with `async`/`await` we had to implement our abstract hierarchy again. This caused a whole bunch of unnecessary maintenance headaches!

A member of the Talent team, [Benjamin Hodgson](https://benjamin.pizza/) identified this pain and addressed it in his [Sawmill](https://github.com/benjamin-hodgson/Sawmill) library. Sawmill handles tree recursion in a far more elegant way, but I won't go into it here - Benjamin has an extensive [blog post](https://www.benjamin.pizza/posts/2017-11-13-recursion-without-recursion.html) detailing how JQL visitors and its AST representation were tweaked to use Sawmill's capabilities.

# Querying Elastic

Once we have an AST representing our query we can perform transformations upon it - perhaps swapping out parts of the tree like we did for favorite jobs, but we can also completely transform to another representation. In our case that means we can take our query language and transform it into a Elasticsearch query or a SQL query.

The code for this is fairly lengthy, so I'll describe how we convert the simple query `"full stack developer" remote:true` into Elastic:

1. Generate an AST from the query. We end up with

```c#
var query = new QueryNode(
        new AndNode(
            new TextNode("full stack developer"),
            new ModifierNode(
                "remote",
                new BoolNode(true)
            )
        )
);
```

1. Pass that AST to our Elastic visitor. Our Elastic visitor maintains a mapping of modifier names and how to translate them into an Elastic query. For example the `remote` modifier accepts a `bool` operand - in the visitor we map this to the `IsRemote` field in Elastic and a `term` query accepting a `bool` value. Bare text values are generally mapped to a `multi_match` query that targets a whole bunch of different fields with appropriate weights - `c#` will weight highly in the tags of a job listing. Here's how it looks in pseudo-code:

```c#
Visit(QueryNode)
{
        foreach (var child in children)
        {
            var andResult = Visit(AndNode)
            {
                var leftResult = Visit(TextNode) => "multi_match": {
                    "type":"phrase",
                    "query":"full stack developer",
                    "fields":["Title^2","Description"]
                };

                var rightResult = Visit(ModifierNode) => "term": {
                    "IsRemote": true
                };
            } => "bool": {
                "must": [
                    leftResult,
                    rightResult
                ]
            };
        }
} => {
    "bool": {
        "must": [
            andResult
        ]
    }
}
```

We then eliminate unnecessary duplication - we don't need the nested `must` queries and we end up with the following JSON:

```json
{
    "bool": {
        "must": [
            "multi_match": {
                "type":"phrase",
                "query":"full stack developer",
                "fields":["Title^2","Description"]
            },
            "term": {
                "IsRemote": true
            }
        ]
    }
}
```

Finally, we use that as the `query` parameter of a query or count operation!

# Next time...

This might all seem a little complex for something that we could've just hard-coded queries for... But this level of flexibility really allows us to do some other neat tricks - not only can we simply and consistently transform our AST into different forms - a necessity if we want to routinely perform optimizations on the queries we're given by the user, but we can also write tests that ensure our translations give us expected results which hardens the query language against all kinds of attacks.

Crucially the AST has been used to allow us to target other data stores to improve our query performance further. In the next installment we'll dig deeper into those optimizations and other tweaks we've made to get job search as fast as we possibly can.