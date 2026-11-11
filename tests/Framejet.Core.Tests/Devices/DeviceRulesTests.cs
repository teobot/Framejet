using System;
using Framejet.Core.Devices;
using Xunit;
using static Framejet.Core.Tests.Devices.TestDevices;

namespace Framejet.Core.Tests.Devices
{
    public class DeviceRulesTests
    {
        [Theory]
        [InlineData("Razer Ripsaw X", true)] [InlineData("Elgato Game Capture HD60 S", true)] [InlineData("Cam Link 4K", true)]
        [InlineData("USB Video", true)] [InlineData("AVerMedia Live Gamer Portable 2", true)]
        [InlineData("Logi W100 HD WebCam", false)] [InlineData("Phone (Windows Virtual Camera)", false)] [InlineData("Camera (Virtual Effects)", false)]
        public void Recognises_capture_cards(string name, bool card) => Assert.Equal(card, DeviceRules.LooksLikeCaptureCard(name));

        [Fact] public void Auto_opens_the_card_not_the_webcams()
        { var p = DeviceRules.PickVideo(new[] { Webcam, PhoneCamera, Ripsaw, EffectsCamera }, "", ""); Assert.Same(Ripsaw, p.Device); Assert.Equal(VideoPickReason.LooksLikeCard, p.Reason); }
        [Fact] public void No_card_means_nothing_opens()
            => Assert.Null(DeviceRules.PickVideo(new[] { Webcam, PhoneCamera, EffectsCamera }, "", "").Device);
        [Fact] public void Saved_pick_wins_even_for_a_webcam()
        { var p = DeviceRules.PickVideo(new[] { Webcam, Ripsaw }, Webcam.Path, Webcam.Name); Assert.Same(Webcam, p.Device); Assert.Equal(VideoPickReason.SavedPath, p.Reason); }
        [Fact] public void Saved_card_on_another_port_is_found_by_name()
        { var p = DeviceRules.PickVideo(new[] { Webcam, RipsawOtherPort }, Ripsaw.Path, Ripsaw.Name); Assert.Same(RipsawOtherPort, p.Device); Assert.Equal(VideoPickReason.SavedName, p.Reason); }

        // A first run saves nothing yet (empty path and name): a webcam with an empty name or path must not pass for the saved pick.
        static readonly VideoDeviceInfo NamelessWebcam = new("", @"@device:pnp:\\?\usb#vid_abcd&pid_0002&mi_00#d#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global");
        static readonly VideoDeviceInfo PathlessWebcam = new("Integrated Webcam", "");
        [Fact] public void An_empty_saved_path_matches_no_device_with_an_empty_path()
        { var p = DeviceRules.PickVideo(new[] { PathlessWebcam, Ripsaw }, "", ""); Assert.Same(Ripsaw, p.Device); Assert.Equal(VideoPickReason.LooksLikeCard, p.Reason); }
        [Fact] public void An_empty_saved_name_matches_no_device_with_an_empty_name()
        { var p = DeviceRules.PickVideo(new[] { NamelessWebcam, Ripsaw }, "", ""); Assert.Same(Ripsaw, p.Device); Assert.Equal(VideoPickReason.LooksLikeCard, p.Reason); }
        [Theory] [InlineData(null)] [InlineData("")] [InlineData(" ")]
        public void A_blank_saved_pick_matches_no_device(string blank)
        {
            var blankDevice = new VideoDeviceInfo(blank, blank);
            var p = DeviceRules.PickVideo(new[] { blankDevice, Ripsaw }, blank, blank);
            Assert.Same(Ripsaw, p.Device); Assert.Equal(VideoPickReason.LooksLikeCard, p.Reason);
        }
        [Theory] [InlineData(null)] [InlineData("")] [InlineData(" ")]
        public void A_blank_saved_pick_opens_nothing_without_a_card(string blank)
            => Assert.Null(DeviceRules.PickVideo(new[] { new VideoDeviceInfo(blank, blank), Webcam }, blank, blank).Device);

        [Fact] public void Card_audio_matches_by_windows_device_group()
        { var m = DeviceRules.MatchAudioInput(Ripsaw, RipsawContainer, AllInputs()); Assert.Same(RipsawHdmi, m.Input); Assert.Equal(AudioInputSource.SameDevice, m.Source); }
        [Fact] public void Webcam_without_a_mic_gets_no_audio()
            => Assert.Null(DeviceRules.MatchAudioInput(Webcam, WebcamContainer, AllInputs()).Input);
        [Fact] public void Falls_back_to_the_full_name()
        {
            var hdmi = new AudioInputInfo("x", "HDMI (Razer Ripsaw X)", Guid.Empty);
            var m = DeviceRules.MatchAudioInput(Ripsaw, Guid.Empty, new[] { HeadsetMic, hdmi });
            Assert.Same(hdmi, m.Input); Assert.Equal(AudioInputSource.NameMatch, m.Source);
        }
        [Fact] public void Falls_back_to_a_distinctive_word()
        { var a = new AudioInputInfo("x", "Digital Audio Interface (Ripsaw)", Guid.Empty); Assert.Same(a, DeviceRules.MatchAudioInput(Ripsaw, Guid.Empty, new[] { a }).Input); }
        [Fact] public void Brand_words_do_not_count()
            => Assert.Null(DeviceRules.MatchAudioInput(Ripsaw, Guid.Empty, new[] { new AudioInputInfo("y", "Microphone (Razer Seiren Mini)", Guid.Empty) }).Input);
        [Fact] public void The_no_container_id_never_matches()
            => Assert.Null(DeviceRules.MatchAudioInput(EffectsCamera, DeviceRules.NoContainer, new[] { VirtualMic, StreamingMic }).Input);
        // An empty name is contained in every name: it must never pick a mic (here the headset mic, listed first).
        [Theory] [InlineData(null)] [InlineData("")] [InlineData(" ")]
        public void A_blank_video_name_matches_no_input_by_name(string blank)
            => Assert.Null(DeviceRules.MatchAudioInput(new VideoDeviceInfo(blank, Ripsaw.Path), Guid.Empty, AllInputs()).Input);
        [Fact] public void A_nameless_video_device_still_matches_by_windows_device_group()
        { var m = DeviceRules.MatchAudioInput(new VideoDeviceInfo("", Ripsaw.Path), RipsawContainer, AllInputs()); Assert.Same(RipsawHdmi, m.Input); Assert.Equal(AudioInputSource.SameDevice, m.Source); }
        [Theory]
        [InlineData("Razer Ripsaw X", "ripsaw")] [InlineData("Logi W100 HD WebCam", "w100")] [InlineData("Elgato Game Capture HD60 S", "hd60")]
        public void Distinctive_words_skip_brands_generic_and_short_words(string name, string words)
            => Assert.Equal(words, string.Join(",", DeviceRules.DistinctiveWords(name)));
    }
}
