using System;
using System.Linq;

namespace Samples.Lib
{
    public static class JqlExtensions
    {
        public static bool SyntacticallyEquals(this JqlNode left, JqlNode right)
        {
            switch (left)
            {
                case BinaryNode b1:
                    switch (right)
                    {
                        case BinaryNode b2:
                            return b1.Type == b2.Type
                                && SyntacticallyEquals(b1.Left, b2.Left)
                                && SyntacticallyEquals(b2.Right, b2.Right);
                        default:
                            return false;
                    }
                case ModifierNode m1:
                    switch (right)
                    {
                        case ModifierNode m2:
                            return m1.Name == m2.Name
                                && SyntacticallyEquals(m1.Value, m2.Value);
                        default:
                            return false;
                    }
                case QueryNode q1:
                    switch (right)
                    {
                        case QueryNode q2:
                            return q1.Children.Length == q2.Children.Length
                                && q1.Children
                                    .Zip(q2.Children, SyntacticallyEquals)
                                    .All(b => b);
                        default:
                            return false;
                    }
                case GroupNode g1:
                    switch (right)
                    {
                        case GroupNode g2:
                            return g1.Children.Length == g2.Children.Length
                                && g1.Children
                                    .Zip(g2.Children, SyntacticallyEquals)
                                    .All(b => b);
                        default:
                            return false;
                    }
                case TextNode t1:
                    switch (right)
                    {
                        case TextNode t2:
                            return t1.Value == t2.Value;
                        default:
                            return false;
                    }
            }
            throw new ArgumentOutOfRangeException($"Unknown node type: {left.GetType().Name}", nameof(left));
        }

        public static JqlNode CollapseNodes(this JqlNode node)
        {
            switch (node)
            {
                case BinaryNode binaryNode:
                    var newLeft = CollapseNodes(binaryNode.Left);
                    var newRight = CollapseNodes(binaryNode.Right);
                    if (!ReferenceEquals(binaryNode.Left, newLeft) || ReferenceEquals(binaryNode.Right, newRight))
                    {
                        return new BinaryNode(newLeft, newRight, binaryNode.Type);
                    }
                    break;
                case QueryNode queryNode:
                    {
                        var children = queryNode.Children;
                        for (var i = children.Length - 1; i >= 0; i--)
                        {
                            var child = children[i];
                            var newChild = CollapseNodes(child);
                            if (!ReferenceEquals(child, newChild))
                            {
                                children = children.SetItem(i, newChild);
                            }
                        }

                        if (!ReferenceEquals(children, queryNode.Children))
                        {
                            return new QueryNode(children);
                        }
                        break;
                    }
                case GroupNode groupNode:
                    {
                        if (groupNode.Children.Length == 1)
                        {
                            return CollapseNodes(groupNode.Children[0]);
                        }

                        var children = groupNode.Children;
                        for (var i = children.Length - 1; i >= 0; i--)
                        {
                            var child = children[i];
                            var newChild = CollapseNodes(child);
                            if (!ReferenceEquals(child, newChild))
                            {
                                children = children.SetItem(i, newChild);
                            }
                        }

                        if (!ReferenceEquals(children, groupNode.Children))
                        {
                            return new GroupNode(children);
                        }
                        break;
                    }
            }

            return node;
        }
        public static string PrettyPrint(this JqlNode node)
        {
            switch (node)
            {
                case BinaryNode binaryNode:
                    return PrettyPrint(binaryNode.Left) + PrettyPrint(binaryNode.Type) + PrettyPrint(binaryNode.Right);
                case ModifierNode modifierNode:
                    return modifierNode.Name == "tag"
                        ? "[" + PrettyPrint(modifierNode.Value) + "]"
                        : modifierNode.Name + ":" + PrettyPrint(modifierNode.Value);
                case QueryNode queryNode:
                    return string.Join(" ", queryNode.Children.Select(PrettyPrint));
                case GroupNode groupNode:
                    return string.Join(" ", groupNode.Children.Select(PrettyPrint));
                case TextNode textNode:
                    return textNode.Value;
            }

            throw new ArgumentOutOfRangeException($"Unknown node type: {node.GetType().Name}", nameof(node));
        }

        private static string PrettyPrint(JqlNodeType type)
        {
            switch (type)
            {
                case JqlNodeType.And:
                    return " and ";
                case JqlNodeType.Or:
                    return " or ";
                default:
                    throw new Exception("should be unreachable");
            }
        }
    }
}
