using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Samples.Lib
{
    public enum JqlNodeType
    {
        Modifier,
        Text,
        And,
        Or,
        Query,
        Group,
    }

    public abstract class JqlNode
    {
        protected JqlNode(JqlNodeType type)
        {
            Type = type;
        }

        public JqlNodeType Type { get; }
    }

    public class QueryNode : JqlNode
    {
        public QueryNode(JqlNode node) : base(JqlNodeType.Query)
        {
            Children = ImmutableList.Create(node);
        }

        public QueryNode(IEnumerable<JqlNode> nodes) : base(JqlNodeType.Query)
        {
            Children = ImmutableList.CreateRange(nodes);
        }

        public ImmutableList<JqlNode> Children { get; }
    }

    public class TextNode : JqlNode
    {
        public TextNode(string value) : base(JqlNodeType.Text)
        {
            Value = value;
        }

        public string Value { get; }
    }

    public class BinaryNode : JqlNode
    {
        public BinaryNode(JqlNode left, JqlNode right, JqlNodeType type) : base(type)
        {
            Left = left;
            Right = right;
        }

        public JqlNode Left { get; }

        public JqlNode Right{ get; }
    }

    public class GroupNode : JqlNode
    {
        public GroupNode(JqlNode node) : base(JqlNodeType.Group)
        {
            Children = ImmutableList.Create(node);
        }

        public GroupNode(IEnumerable<JqlNode> nodes) : base(JqlNodeType.Group)
        {
            Children = ImmutableList.CreateRange(nodes);
        }

        public ImmutableList<JqlNode> Children { get; }
    }

    public class ModifierNode : JqlNode
    {
        public ModifierNode(string name, JqlNode value) : base(JqlNodeType.Modifier)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }
        public JqlNode Value { get; }
    }

    public static class JqlBuilder
    {
        public static JqlNode And(this JqlNode left, JqlNode right) => new BinaryNode(left, right, JqlNodeType.And);
        public static JqlNode Or(this JqlNode left, JqlNode right) => new BinaryNode(left, right, JqlNodeType.Or);
        public static JqlNode Tag(string tag) => new ModifierNode("tag", new TextNode(tag));
        public static JqlNode Text(string value) => new TextNode(value);
        public static JqlNode Group(IEnumerable<JqlNode> nodes) => new GroupNode(nodes);
        public static JqlNode Group(params JqlNode[] nodes) => new GroupNode(nodes);
        public static JqlNode Query(JqlNode node) => new QueryNode(node);
        public static JqlNode Query(params JqlNode[] nodes) => new QueryNode(nodes);
    }
}
