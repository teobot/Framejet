using System.Drawing;
using Framejet.Core.Settings;
using Xunit;

namespace Framejet.Core.Tests.Settings
{
    public class PlacementRulesTests
    {
        static WindowPlacement Saved(int x, int y, bool fullscreen = false, string screen = "", bool maximized = false)
            => new WindowPlacement { X = x, Y = y, Width = 1280, Height = 720, HasBounds = true, Fullscreen = fullscreen, ScreenName = screen, Maximized = maximized };

        [Fact] public void Restores_bounds_on_a_connected_screen()
            => Assert.Equal(new Rectangle(-1700, 400, 1280, 720), PlacementRules.Restore(Saved(-1700, 400), TestScreens.All).Bounds);
        [Fact] public void Centres_on_the_primary_when_that_screen_is_gone()
            => Assert.Equal(new Rectangle(640, 336, 1280, 720), PlacementRules.Restore(Saved(-1700, 400), new[] { TestScreens.Main, TestScreens.Right }).Bounds);
        [Fact] public void Centres_on_the_primary_without_saved_bounds()
            => Assert.Equal(new Rectangle(640, 336, 1280, 720), PlacementRules.Restore(new WindowPlacement(), TestScreens.All).Bounds);
        [Fact] public void Reopens_fullscreen_on_the_same_screen()
        { var d = PlacementRules.Restore(Saved(2700, 400, true, @"\\.\DISPLAY3"), TestScreens.All); Assert.True(d.Fullscreen); Assert.Same(TestScreens.Right, d.FullscreenScreen); }
        [Fact] public void Drops_fullscreen_when_its_screen_is_gone()
        { var d = PlacementRules.Restore(Saved(2700, 400, true, @"\\.\DISPLAY3"), new[] { TestScreens.Main }); Assert.False(d.Fullscreen); Assert.Equal(new Rectangle(640, 336, 1280, 720), d.Bounds); }
        [Fact] public void Keeps_maximized() => Assert.True(PlacementRules.Restore(Saved(100, 100, maximized: true), TestScreens.All).Maximized);
        [Fact] public void ScreenFor_uses_the_screen_under_the_window_centre()
            => Assert.Same(TestScreens.Right, PlacementRules.ScreenFor(new Rectangle(2600, 300, 1280, 720), TestScreens.All));
        [Fact] public void ScreenFor_window_left_on_a_disconnected_screen_returns_primary()
            => Assert.Same(TestScreens.Main, PlacementRules.ScreenFor(new Rectangle(-1700, 400, 1280, 720), new[] { TestScreens.Main, TestScreens.Right }));

        static WindowPlacement SavedRect(int x, int y, int w, int h, string screen, bool maximized = false)
            => new WindowPlacement { X = x, Y = y, Width = w, Height = h, HasBounds = true, ScreenName = screen, Maximized = maximized };

        [Fact] public void Maximized_window_with_restore_rect_hanging_off_the_top_stays_on_its_screen()
        {
            var d = PlacementRules.Restore(SavedRect(2743, 228, 1280, 720, @"\\.\DISPLAY3", true), TestScreens.MainTaskbarOnly);
            Assert.True(TestScreens.MainTaskbarD3.WorkingArea.Contains(d.Bounds));
            Assert.Same(TestScreens.MainTaskbarD3, PlacementRules.ScreenFor(d.Bounds, TestScreens.MainTaskbarOnly));
            Assert.True(PlacementRules.IsUsable(d.Bounds, TestScreens.MainTaskbarOnly));
            Assert.True(d.Maximized);
        }
        [Fact] public void Non_maximized_bounds_hanging_off_the_top_are_clamped_into_the_saved_screen()
        {
            var d = PlacementRules.Restore(SavedRect(2743, 228, 1280, 720, @"\\.\DISPLAY3"), TestScreens.MainTaskbarOnly);
            Assert.Equal(new Rectangle(2743, 270, 1280, 720), d.Bounds);
            Assert.False(d.Maximized);
        }
        [Fact] public void Bounds_larger_than_the_saved_screen_are_capped_to_its_working_area()
        {
            var d = PlacementRules.Restore(SavedRect(2500, 100, 3000, 2000, @"\\.\DISPLAY3"), TestScreens.MainTaskbarOnly);
            Assert.Equal(TestScreens.MainTaskbarD3.WorkingArea, d.Bounds);
        }
        [Fact] public void Unusable_bounds_with_no_saved_screen_clamp_into_the_screen_they_overlap_most()
        {
            var d = PlacementRules.Restore(SavedRect(2743, 228, 1280, 720, ""), TestScreens.MainTaskbarOnly);
            Assert.Equal(new Rectangle(2743, 270, 1280, 720), d.Bounds);
        }
        [Fact] public void Gone_saved_screen_and_bounds_on_no_screen_centre_on_the_primary()
        {
            var d = PlacementRules.Restore(SavedRect(9000, 9000, 1280, 720, @"\\.\DISPLAY9"), TestScreens.MainTaskbarOnly);
            Assert.Equal(new Rectangle(640, 340, 1280, 720), d.Bounds);
        }
        [Fact] public void Usable_bounds_are_kept_exactly_even_when_the_saved_screen_differs()
        {
            var d = PlacementRules.Restore(SavedRect(-1700, 400, 1280, 720, @"\\.\DISPLAY3"), TestScreens.MainTaskbarOnly);
            Assert.Equal(new Rectangle(-1700, 400, 1280, 720), d.Bounds);
        }
    }
}
