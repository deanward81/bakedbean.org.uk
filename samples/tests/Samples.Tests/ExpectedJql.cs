using Samples.Lib;

namespace Samples.Tests
{
    /// <summary>
    /// Helper class for tests that provides equality checks for <see cref="QueryNode"/> instances.
    /// </summary>
    public class ExpectedJql
    {
        private readonly JqlNode _node;

        public ExpectedJql(JqlNode node)
        {
            _node = node;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((ExpectedJql)obj);
        }

        public override int GetHashCode()
        {
            // ReSharper disable once BaseObjectGetHashCodeCallInGetHashCode
            return base.GetHashCode();
        }

        private bool Equals(ExpectedJql other) => Equals(_node, other._node);

        private bool Equals(JqlNode x, JqlNode y) => x.SyntacticallyEquals(y);

        public override string ToString() => _node.PrettyPrint();
    }
}
