using System;
using Xunit;

namespace Framejet.Core.Tests
{
    public class CommandLineTests
    {
        [Fact] public void No_arguments_is_a_normal_launch()
        { var c = CommandLine.Parse(new string[0]); Assert.Null(c.SelfTestReport); Assert.Null(c.SettingsPath); Assert.Equal(4, c.Seconds); }
        [Fact] public void Reads_selftest_seconds_and_settings()
        { var c = CommandLine.Parse(new[] { "--selftest", "out.txt", "--seconds", "10", "--settings", @"C:\t\s.ini" });
          Assert.Equal("out.txt", c.SelfTestReport); Assert.Equal(10, c.Seconds); Assert.Equal(@"C:\t\s.ini", c.SettingsPath); }
        [Fact] public void Selftest_needs_a_path() => Assert.Throws<ArgumentException>(() => CommandLine.Parse(new[] { "--selftest" }));
        [Fact] public void Seconds_must_be_a_positive_number() => Assert.Throws<ArgumentException>(() => CommandLine.Parse(new[] { "--seconds", "x" }));
    }
}
