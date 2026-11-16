using Xunit;

namespace Framejet.Core.Tests
{
    public class LogScrubTests
    {
        static readonly string[] Profile = { @"C:\Users\sample", @"C:\Users\SAMPLE~1" };

        [Fact] public void Replaces_the_profile_folder()
            => Assert.Equal(@"Settings %USERPROFILE%\AppData\Roaming\Framejet\settings.ini",
                LogScrub.Scrub(@"Settings C:\Users\sample\AppData\Roaming\Framejet\settings.ini", Profile));

        [Fact] public void Ignores_case_and_replaces_every_occurrence()
            => Assert.Equal(@"%USERPROFILE%\a and %USERPROFILE%\b",
                LogScrub.Scrub(@"c:\users\Sample\a and C:\Users\sample\b", Profile));

        [Fact] public void Replaces_the_short_form()
            => Assert.Equal(@"at %USERPROFILE%\AppData\Local\Temp\x.tmp",
                LogScrub.Scrub(@"at C:\Users\SAMPLE~1\AppData\Local\Temp\x.tmp", Profile));

        [Fact] public void Replaces_the_folder_at_the_end_or_before_punctuation()
            => Assert.Equal(@"home is %USERPROFILE%; quoted ""%USERPROFILE%""",
                LogScrub.Scrub(@"home is C:\Users\sample; quoted ""C:\Users\sample""", Profile));

        [Fact] public void Leaves_a_longer_folder_name_alone()
            => Assert.Equal(@"C:\Users\samples\x and C:\Users\sample.old",
                LogScrub.Scrub(@"C:\Users\samples\x and C:\Users\sample.old", Profile));

        [Fact] public void Matches_after_a_near_miss()
            => Assert.Equal(@"C:\Users\samplex %USERPROFILE%\y",
                LogScrub.Scrub(@"C:\Users\samplex C:\Users\sample\y", Profile));

        [Fact] public void Ignores_missing_empty_and_drive_only_paths()
        {
            Assert.Equal(@"C:\x", LogScrub.Scrub(@"C:\x", new[] { null, "", @"C:\" }));
            Assert.Equal("text", LogScrub.Scrub("text", null));
            Assert.Null(LogScrub.Scrub(null, Profile));
        }

        [Fact] public void Trailing_separator_on_the_profile_does_not_matter()
            => Assert.Equal(@"%USERPROFILE%\a", LogScrub.Scrub(@"C:\Users\sample\a", new[] { @"C:\Users\sample\" }));
    }
}
