using System;
using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;

namespace Samples.Benchmarks
{
    public class DictionaryVsArrayIndexer
    {
        private ImmutableDictionary<int, Job> _jobsById;
        private ImmutableArray<Job> _jobs;

        [GlobalSetup]
        public void Setup()
        {
            var rng = new Random();
            var tags = ImmutableArray.Create(
                "c#", "javascript", "sql-server"
            );
            var jobBuilder = ImmutableArray.CreateBuilder<Job>(1_000_000);
            for (var i = 0 ; i < jobBuilder.Capacity; i++)
            {
                jobBuilder.Add(
                    new Job(i, "Job " + i, (JobSeniority)rng.Next(1, 5), tags, rng.Next(0, 2) == 1)
                );
            }
            _jobs = jobBuilder.MoveToImmutable();
            _jobsById = _jobs.ToImmutableDictionary(x => x.Id);
        }

        [Benchmark(Baseline = true)]
        public void DictionaryLookup()
        {
            var jobs = new Job[25];
            for (var i = 0 ; i < jobs.Length; i++)
            {
                jobs[i] = _jobsById[i * 2000];
            }
        }

        [Benchmark]
        public void ArrayIndexer()
        {
            var jobs = new Job[25];
            for (var i = 0 ; i < jobs.Length; i++)
            {
                jobs[i] = _jobs[i * 2000];
            }
        }
    }
}