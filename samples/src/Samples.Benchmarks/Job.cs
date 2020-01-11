using System.Collections.Immutable;

namespace Samples.Benchmarks
{
    public class Job
    {
        public Job(int id, string title, JobSeniority seniority, ImmutableArray<string> tags, bool isRemote)
        {
            Id = id;
            Title = title;
            Seniority = seniority;
            Tags = tags;
            IsRemote = isRemote;
        }

        public int Id { get; }
        public string Title { get; }
        public JobSeniority Seniority { get; }
        public ImmutableArray<string> Tags { get; }
        public bool IsRemote { get; }
    }
}