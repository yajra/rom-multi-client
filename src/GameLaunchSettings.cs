using System;
using System.IO;

namespace ROInstances;

public enum PerformanceLevel { Low, Medium, High }
public enum GameDisplayMode { Windowed540p, Windowed720p, Fullscreen }

public sealed record GameLaunchSettings
{
    public GameDisplayMode Display { get; init; } = GameDisplayMode.Windowed540p;
    public PerformanceLevel Graphics { get; init; } = PerformanceLevel.Low;
    public PerformanceLevel Fps { get; init; } = PerformanceLevel.Low;

    public void Validate()
    {
        if (!Enum.IsDefined(Display) || !Enum.IsDefined(Graphics) || !Enum.IsDefined(Fps))
            throw new InvalidDataException("Choose 540p, 720p, or Fullscreen and Low, Medium, or High graphics and FPS.");
    }
}
