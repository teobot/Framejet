using System.IO;
using System.Net;
using Framejet.Core.Updates;
using Framejet.Platform.Os;
using Xunit;

namespace Framejet.Platform.Tests.Os
{
    public class LatestReleaseTests
    {
        [SkippableFact, Trait("Category", "Network")]
        public void Fetches_the_latest_release()
        {
            string json = null;
            try { json = LatestRelease.Fetch(UpdateCheck.LatestReleaseApi, "Framejet-tests", UpdateCheck.Timeout); }
            catch (WebException ex) { Skip.If(true, "GitHub not reachable: " + ex.Message); }
            catch (IOException ex) { Skip.If(true, "GitHub not reachable: " + ex.Message); }
            var r = ReleaseInfo.Parse(json);
            Assert.StartsWith(UpdateCheck.ReleasePagePrefix, r.PageUrl);
            Assert.True(AppVersion.TryParse(r.Tag, out _));
        }
    }
}
