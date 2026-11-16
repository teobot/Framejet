using System;
using System.Collections.Generic;
using Framejet.Core.Audio;
using Xunit;

namespace Framejet.Core.Tests.Audio
{
    public class LowLatencyRegistryTests
    {
        static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

        [Fact]
        public void Tries_unknown_endpoints()
        {
            var r = new LowLatencyRegistry();
            Assert.True(r.ShouldTry("a"));
        }

        [Fact]
        public void Null_or_empty_endpoint_is_not_tried_and_calls_are_ignored()
        {
            var r = new LowLatencyRegistry();
            var fell = 0;
            r.FellBack += _ => fell++;
            Assert.False(r.ShouldTry(null));
            Assert.False(r.ShouldTry(""));
            r.Unsupported(null);
            Assert.False(r.Underrun("", S(0)));
            Assert.False(r.Underrun(null, S(0)));
            Assert.Equal(0, fell);
        }

        [Fact]
        public void Unsupported_endpoint_is_not_tried_again()
        {
            var r = new LowLatencyRegistry();
            r.Unsupported("a");
            Assert.False(r.ShouldTry("a"));
            Assert.True(r.ShouldTry("b"));
        }

        [Fact]
        public void Two_underruns_in_10s_are_allowed()
        {
            var r = new LowLatencyRegistry();
            Assert.False(r.Underrun("a", S(0)));
            Assert.False(r.Underrun("a", S(5)));
            Assert.True(r.ShouldTry("a"));
        }

        [Fact]
        public void Third_underrun_within_10s_falls_back_once()
        {
            var r = new LowLatencyRegistry();
            var fell = new List<string>();
            r.FellBack += fell.Add;
            Assert.False(r.Underrun("a", S(0)));
            Assert.False(r.Underrun("a", S(4)));
            Assert.True(r.Underrun("a", S(10)));   // exactly 10 s after the first counts
            Assert.False(r.ShouldTry("a"));
            Assert.False(r.Underrun("a", S(11)));
            Assert.Equal(new[] { "a" }, fell);
        }

        [Fact]
        public void Underruns_spread_over_time_never_fall_back()
        {
            var r = new LowLatencyRegistry();
            for (var t = 0; t <= 60; t += 6)
                Assert.False(r.Underrun("a", S(t)));
            Assert.True(r.ShouldTry("a"));
        }

        [Fact]
        public void Fallback_is_per_endpoint()
        {
            var r = new LowLatencyRegistry();
            r.Underrun("a", S(0));
            r.Underrun("b", S(1));
            r.Underrun("a", S(2));
            Assert.True(r.Underrun("a", S(3)));
            Assert.False(r.ShouldTry("a"));
            Assert.True(r.ShouldTry("b"));
            Assert.False(r.Underrun("b", S(4)));
        }
    }
}
