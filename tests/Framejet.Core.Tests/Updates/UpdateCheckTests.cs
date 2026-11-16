using System;
using Framejet.Core.Ui;
using Framejet.Core.Updates;
using Xunit;

namespace Framejet.Core.Tests.Updates
{
    public class UpdateCheckTests
    {
        const string V120 = "{\"tag_name\":\"v1.2.0\",\"html_url\":\"https://github.com/teobot/Framejet/releases/tag/v1.2.0\",\"prerelease\":false," +
            "\"assets\":[{\"name\":\"DirectShowLib-source.zip\"},{\"name\":\"Framejet.exe\"},{\"name\":\"THIRD-PARTY-NOTICES.md\"}],\"author\":{\"login\":\"x\"}}";
        static string NoExe => V120.Replace("Framejet.exe", "Other.exe");

        [Fact] public void Parse_reads_tag_page_and_assets()
        { var r = ReleaseInfo.Parse(V120); Assert.Equal("v1.2.0", r.Tag); Assert.Equal("https://github.com/teobot/Framejet/releases/tag/v1.2.0", r.PageUrl); Assert.Contains("Framejet.exe", r.AssetNames); }
        [Fact] public void Parse_rejects_a_body_without_a_tag()
            => Assert.Throws<FormatException>(() => ReleaseInfo.Parse("{\"message\":\"API rate limit exceeded\",\"documentation_url\":\"https://docs.github.com\"}"));
        [Theory] [InlineData("<html>oops</html>")] [InlineData("")] public void Parse_rejects_non_json(string body) => Assert.Throws<FormatException>(() => ReleaseInfo.Parse(body));

        [Fact] public void Offers_a_newer_release_with_its_exe()
        { var o = UpdateCheck.Evaluate("1.1.0", ReleaseInfo.Parse(V120)); Assert.Equal("1.2.0", o.Version); Assert.Equal("https://github.com/teobot/Framejet/releases/tag/v1.2.0", o.PageUrl); }
        [Fact] public void Offers_the_release_to_its_own_release_candidate() => Assert.NotNull(UpdateCheck.Evaluate("1.2.0-rc.1", ReleaseInfo.Parse(V120)));
        [Theory] [InlineData("1.2.0")] [InlineData("1.2.0+abc")] [InlineData("1.3.0-beta.1")] [InlineData("2.0.0")]
        public void No_offer_when_current_or_ahead(string running) => Assert.Null(UpdateCheck.Evaluate(running, ReleaseInfo.Parse(V120)));
        [Fact] public void No_offer_before_the_exe_is_attached() => Assert.Null(UpdateCheck.Evaluate("1.1.0", ReleaseInfo.Parse(NoExe)));
        [Fact] public void Exe_name_match_ignores_case() => Assert.NotNull(UpdateCheck.Evaluate("1.1.0", ReleaseInfo.Parse(V120.Replace("Framejet.exe", "framejet.EXE"))));
        [Fact] public void No_offer_for_an_unparseable_tag() => Assert.Null(UpdateCheck.Evaluate("1.1.0", ReleaseInfo.Parse(V120.Replace("\"v1.2.0\"", "\"latest\""))));
        [Theory] [InlineData("https://evil.example/teobot/Framejet/releases/tag/v1.2.0")] [InlineData("http://github.com/teobot/Framejet/releases/tag/v1.2.0")] [InlineData("file:///C:/Windows/notepad.exe")]
        public void No_offer_for_a_page_outside_the_releases(string url)
            => Assert.Null(UpdateCheck.Evaluate("1.1.0", ReleaseInfo.Parse(V120.Replace("https://github.com/teobot/Framejet/releases/tag/v1.2.0", url))));
        [Fact] public void No_offer_without_a_release() => Assert.Null(UpdateCheck.Evaluate("1.1.0", null));

        [Theory]
        [InlineData("1.1.0", true, false, true)]
        [InlineData("1.1.0", false, false, false)]
        [InlineData("1.1.0", true, true, false)]
        [InlineData("0.0.0-dev", true, false, false)]
        [InlineData("not a version", true, false, false)]
        [InlineData(null, true, false, false)]
        public void Checks_only_real_versions_when_enabled_outside_the_self_test(string running, bool enabled, bool selfTesting, bool expected)
            => Assert.Equal(expected, UpdateCheck.ShouldCheck(running, enabled, selfTesting));

        [Theory] [InlineData("1.2.0+abc", "Framejet/1.2.0")] [InlineData("junk", "Framejet")]
        public void User_agent_names_only_the_app(string running, string expected) => Assert.Equal(expected, UpdateCheck.UserAgent(running));

        [Fact] public void Update_copy_is_exact()
        { Assert.Equal("Update to 1.2.0", UiText.UpdateTo("1.2.0")); Assert.Equal("Open the Framejet 1.2.0 release page", UiText.UpdateTooltip("1.2.0")); }
    }
}
