using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Narabemi.Settings
{
    /// <summary>
    /// Load and save the AppState file.
    /// </summary>
    public class AppStatesService
    {
        // Internal for unit-testing: lets tests assert that the path is install-relative.
        internal static readonly string FilePath =
            Path.Combine(AppContext.BaseDirectory, "appstates.json");

        private readonly JsonSerializerOptions _opt = new()
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true,
        };

        private readonly ILogger<AppStatesService> _logger;

        public AppStates? Current { get; private set; }

        public AppStatesService(ILogger<AppStatesService> logger)
        {
            _logger = logger;
            _opt.Converters.Add(new ColorRgbaJsonConverter());
        }

        public void LoadFile()
        {
            if (!File.Exists(FilePath))
            {
                _logger.LogWarning("{FilePath} not found; using default {TypeName}.", FilePath, nameof(AppStates));
                Current = new AppStates();
                return;
            }

            try
            {
                var jsonText = File.ReadAllText(FilePath);
                Current = JsonSerializer.Deserialize<AppStates>(jsonText, _opt);

                if (Current is null)
                {
                    _logger.LogWarning("{FilePath} deserialized to null; using default {TypeName}.", FilePath, nameof(AppStates));
                    Current = new AppStates();
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "Failed to load {FilePath}; using default {TypeName}.", FilePath, nameof(AppStates));
                Current = new AppStates();
            }
        }

        public void SaveFile()
        {
            Guard.IsNotNull(Current);

            var jsonText = JsonSerializer.Serialize(Current, _opt);
            try
            {
                File.WriteAllText(FilePath, jsonText);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to save app state to {FilePath}", FilePath);
            }
        }

        public void ApplyTo(IAppStateTarget target)
        {
            Guard.IsNotNull(Current);

            target.Loop = Current.Loop;
            target.AutoSync = Current.AutoSync;
            target.MainPlayerIndex = Current.MainPlayerIndex;
            for (int i = 0; i < Math.Min(target.StatePlayers.Count, Current.VideoPathList.Count); i++)
                target.StatePlayers[i].VideoPath = Current.VideoPathList[i];
            for (int i = 0; i < Math.Min(target.StatePlayers.Count, Current.PlayerSpeedList.Count); i++)
                target.StatePlayers[i].Speed = Current.PlayerSpeedList[i];
            for (int i = 0; i < Math.Min(target.StatePlayers.Count, Current.PlayerTimeOffsetList.Count); i++)
                target.StatePlayers[i].TimeOffset = Current.PlayerTimeOffsetList[i];
            target.BlendRatio = Current.BlendRatio;
            target.BlendMode = Current.BlendMode;
        }

        public void ApplyFrom(IAppStateTarget target)
        {
            Guard.IsNotNull(Current);

            Current.Loop = target.Loop;
            Current.AutoSync = target.AutoSync;
            Current.MainPlayerIndex = target.MainPlayerIndex;
            Current.VideoPathList.Clear();
            Current.VideoPathList.AddRange(target.StatePlayers.Select(p => p.VideoPath));
            Current.PlayerSpeedList.Clear();
            Current.PlayerSpeedList.AddRange(target.StatePlayers.Select(p => p.Speed));
            Current.PlayerTimeOffsetList.Clear();
            Current.PlayerTimeOffsetList.AddRange(target.StatePlayers.Select(p => p.TimeOffset));
            Current.BlendRatio = target.BlendRatio;
            Current.BlendMode = target.BlendMode;
        }
    }
}
