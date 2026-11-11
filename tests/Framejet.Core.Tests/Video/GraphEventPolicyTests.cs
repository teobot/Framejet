using Framejet.Core.Video;
using Xunit;

namespace Framejet.Core.Tests.Video
{
    public class GraphEventPolicyTests
    {
        [Theory]
        [InlineData(GraphEventPolicy.EC_DEVICE_LOST, 0, true)] [InlineData(GraphEventPolicy.EC_DEVICE_LOST, 1, false)]
        [InlineData(GraphEventPolicy.EC_ERRORABORT, 0, true)] [InlineData(GraphEventPolicy.EC_ERRORABORTEX, 0, true)]
        [InlineData(GraphEventPolicy.EC_STREAM_ERROR_STOPPED, 0, true)] [InlineData(GraphEventPolicy.EC_COMPLETE, 0, false)] [InlineData(0x0E, 0, false)]
        public void Graph_errors_count_as_device_lost(int code, long param2, bool lost) => Assert.Equal(lost, GraphEventPolicy.MeansDeviceLost(code, param2));

        [Theory]
        [InlineData(GraphEventPolicy.EC_ERRORABORT, unchecked((int)0xC00D3EA3), 0, GraphEventKind.TakenByAnotherApp)]
        [InlineData(GraphEventPolicy.EC_ERRORABORTEX, unchecked((int)0xC00D3EA3), 0, GraphEventKind.TakenByAnotherApp)]
        [InlineData(GraphEventPolicy.EC_ERRORABORT, unchecked((int)0x80004005), 0, GraphEventKind.DeviceLost)]
        [InlineData(GraphEventPolicy.EC_DEVICE_LOST, 0, 0, GraphEventKind.DeviceLost)]
        [InlineData(GraphEventPolicy.EC_DEVICE_LOST, 0, 1, GraphEventKind.None)]
        [InlineData(GraphEventPolicy.EC_COMPLETE, 0, 0, GraphEventKind.None)]
        public void Classifies_preemption_separately(int code, int param1, long param2, GraphEventKind kind)
            => Assert.Equal(kind, GraphEventPolicy.Classify(code, param1, param2));
    }
}
