using MixCut.Services.SceneDetection;
using Xunit;

namespace MixCut.Tests.Services.SceneDetection;

public class SceneDetectionServiceTests
{
    [Fact]
    public void ParseSceneBoundaries_ReadsMetadataSceneScore()
    {
        const string output = """
            [Parsed_metadata_1 @ 000001] frame:0 pts:15360 pts_time:0.5
            [Parsed_metadata_1 @ 000001] lavfi.scene_score=0.421875
            [Parsed_metadata_1 @ 000001] frame:1 pts:92160 pts_time:3
            [Parsed_metadata_1 @ 000001] lavfi.scene_score=0.8125
            """;

        var result = SceneDetectionService.ParseSceneBoundaries(output);

        Assert.Equal(2, result.Count);
        Assert.Equal(0.5, result[0].Time, 6);
        Assert.Equal(0.421875, result[0].Confidence, 6);
        Assert.Equal(3.0, result[1].Time, 6);
        Assert.Equal(0.8125, result[1].Confidence, 6);
    }

    [Fact]
    public void ParseSceneBoundaries_DoesNotInventFallbackScores()
    {
        const string output = """
            [Parsed_metadata_1 @ 000001] frame:0 pts:15360 pts_time:0.5
            """;

        Assert.Empty(SceneDetectionService.ParseSceneBoundaries(output));
    }
}
