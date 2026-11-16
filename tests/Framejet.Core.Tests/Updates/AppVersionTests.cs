using Framejet.Core.Updates;
using Xunit;

namespace Framejet.Core.Tests.Updates
{
    public class AppVersionTests
    {
        [Theory]
        [InlineData("1.2.0", "1.2.0")]
        [InlineData("v1.2.0", "1.2.0")]
        [InlineData("1.2.0+8c63b00", "1.2.0")]
        [InlineData(" 1.2.0-rc.1 ", "1.2.0-rc.1")]
        [InlineData("1.2", "1.2")]
        [InlineData("1.2.0.4", "1.2.0.4")]
        public void Parses_release_tags_and_product_versions(string text, string shown)
        { Assert.True(AppVersion.TryParse(text, out var v)); Assert.Equal(shown, v.ToString()); }

        [Theory]
        [InlineData(null)] [InlineData("")] [InlineData("latest")] [InlineData("Framejet 1.0.0")] [InlineData("1")] [InlineData("1.2.3.4.5")] [InlineData("1.x.0")]
        public void Rejects_non_versions(string text) => Assert.False(AppVersion.TryParse(text, out _));

        [Theory]
        [InlineData("1.1.0", "1.2.0")]
        [InlineData("1.2.0-rc.1", "1.2.0")]
        [InlineData("1.2.0-rc.1", "1.2.0-rc.2")]
        [InlineData("1.2.0-rc.2", "1.2.0-rc.10")]
        [InlineData("1.2.0-1", "1.2.0-alpha")]
        [InlineData("1.2.0-rc", "1.2.0-rc.1")]
        [InlineData("1.9.0", "1.10.0")]
        [InlineData("1.2", "1.2.1")]
        [InlineData("0.0.0-dev", "1.0.0")]
        public void Orders_lower_before_higher(string lower, string higher)
        {
            AppVersion.TryParse(lower, out var a); AppVersion.TryParse(higher, out var b);
            Assert.True(a.CompareTo(b) < 0); Assert.True(b.CompareTo(a) > 0);
        }

        [Theory]
        [InlineData("1.2", "1.2.0")] [InlineData("v1.2.0", "1.2.0+abc")] [InlineData("1.2.0", "1.2.0.0")]
        public void Equal_versions_compare_equal(string a, string b)
        { AppVersion.TryParse(a, out var x); AppVersion.TryParse(b, out var y); Assert.Equal(0, x.CompareTo(y)); }

        [Theory]
        [InlineData("0.0.0-dev", true)] [InlineData("1.2.0-DEV.3", true)] [InlineData("1.2.0", false)] [InlineData("1.2.0-rc.1", false)] [InlineData("1.2.0-devel", false)]
        public void Knows_dev_builds(string text, bool dev) { AppVersion.TryParse(text, out var v); Assert.Equal(dev, v.IsDev); }
    }
}
